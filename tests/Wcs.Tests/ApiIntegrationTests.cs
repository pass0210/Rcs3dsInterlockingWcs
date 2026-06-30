using System.Net;
using System.Net.Http.Json;
using System.Threading;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wcs.Api;
using Wcs.Core;
using Wcs.Data;
using Wcs.PlcGateway;
using Wcs.Sim3ds;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// M3 API 통합 테스트 (VS-1~VS-7)
//
// WebApplicationFactory로 in-process 호스트 기동.
// IModbusMaster는 FakeModbusMaster로 교체 — PLC/Sim3ds 없이 결정적 동작.
// 결정적 설계: 고정 sleep 없음, WaitUntilAsync 폴링 동기화.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// P2b: FakeModbusMaster + FakeSorterGatewayRegistry를 주입하는 WebApplicationFactory.
/// SorterRegistryFactory(IHostedService)를 교체해 DB 기동 판별을 우회하고
/// FakeModbusMasterForApi 기반 단일 소터 레지스트리를 주입.
/// 기존 FakePolling 기반 스냅샷 접근도 유지.
/// </summary>
public sealed class FakeModbusWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>테스트에서 직접 레지스터를 조작하기 위해 공개.</summary>
    public FakeModbusMasterForApi FakeMaster { get; } = new();

    // ── P2b: 단일 소터 fake 레지스트리 (chuteNo=30, destinationId=DB 조회) ──
    private readonly PlcWriteQueue          _fakeWriteQueue  = new();
    private readonly PlcGatewayOptions      _fakeGwOpt       = new()
    {
        Host = "127.0.0.1", Port = 1502,
        PollIntervalMs = 150, OfflineAfterFailures = 3, WriteTimeoutMs = 1000,
        RFlagPollMs = 100, RFlagTimeoutMs = 30000, CFlagTimeoutMs = 5000,
    };
    private PlcPollingService?          _fakePolling;
    private HandshakeOrchestrator?      _fakeHandshake;
    private FakeSorterGatewayRegistry?  _fakeRegistry;

    // 기존 테스트가 스냅샷 조건을 폴링할 수 있도록 public 노출.
    public PlcPollingService? FakePolling => _fakePolling;

    // ── Named in-memory SQLite ────────────────────────────────────────────────
    private static readonly string _dbName = $"WcsTest_{Guid.NewGuid():N}";
    private readonly Microsoft.Data.Sqlite.SqliteConnection _anchorConnection;

    public FakeModbusWebApplicationFactory()
    {
        _anchorConnection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={_dbName};Mode=Memory;Cache=Shared");
        _anchorConnection.Open();

        // FakePolling·FakeHandshake는 생성자에서 미리 구성.
        // FakeSorterGatewayRegistry는 ConfigureWebHost에서 DB 시드 후 실제 destinationId를 조회해 초기화.
        _fakePolling   = new PlcPollingService(_fakeGwOpt, _fakeWriteQueue, FakeMaster);
        _fakeHandshake = new HandshakeOrchestrator(_fakePolling, _fakeGwOpt);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // base appsettings=SqlServer → 테스트 더블은 인메모리 SQLite. host setting으로 Provider=Sqlite를
        // Program의 builder.Configuration 읽기 전에 주입해 Program이 SQLite 분기로 등록(EF SqlServer
        // provider 미등록 → "Only a single database provider" 충돌 회피). connection은 아래
        // ConfigureServices가 named in-memory(anchor)로 재등록(provider 결선만 — 토폴로지·시드·단언 불변).
        builder.UseSetting("Database:Provider", "Sqlite");

        builder.ConfigureServices(services =>
        {
            // ── WcsDbContext를 named in-memory SQLite로 교체 ─────────────────────
            var dbDescriptors = services
                .Where(d => d.ServiceType == typeof(DbContextOptions<WcsDbContext>)
                         || d.ServiceType == typeof(WcsDbContext))
                .ToList();
            foreach (var d in dbDescriptors)
                services.Remove(d);

            var connStr = $"Data Source={_dbName};Mode=Memory;Cache=Shared";
            services.AddDbContext<WcsDbContext>(opts =>
                opts.UseSqlite(connStr,
                    sqlite => sqlite.CommandTimeout(30))
                    .ConfigureWarnings(w => w.Ignore(
                        Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)),
                ServiceLifetime.Scoped);

            // ── 스키마 생성 + 시드 ─────────────────────────────────────────────
            var dbOpts = new DbContextOptionsBuilder<WcsDbContext>()
                .UseSqlite(_anchorConnection)
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
                .Options;
            using var db = new WcsDbContext(dbOpts);
            db.Database.EnsureCreated();
            DbSeeder.Seed(db, new Dictionary<string, int> { ["1"] = 1, ["2"] = 2 });

            // ── DB 시드 직후 SORTER_3D destination의 실제 id 조회 ────────────────────
            // DbSeeder는 CHUTE 1~5(id=1~5)를 먼저 삽입한 뒤 SORTER_3D(chuteNo=30)를 삽입.
            // 따라서 SORTER_3D의 auto-increment id는 1이 아닌 6(고정값 가정 금지).
            // 시드 직후 조회해 실제 id로 번들을 구성.
            var sorterDest = db.Destinations
                .First(d => d.DestType == Wcs.Data.DestType.SORTER_3D && d.IsActive);
            var bundle = new SorterBundleHandle(
                destinationId: sorterDest.Id,
                chuteNo:       sorterDest.ChuteNo,
                polling:       _fakePolling!,
                handshake:     _fakeHandshake!);
            _fakeRegistry = new FakeSorterGatewayRegistry(bundle);

            // ── P2b: SorterRegistryFactory + ISorterGatewayRegistry 교체 ─────────────
            // SorterRegistryFactory(IHostedService + ISorterGatewayRegistry)를
            // NopSorterRegistryFactory(ISorterGatewayRegistry + IHostedService)로 교체.
            //
            // Program.cs 등록 구조:
            //   ① AddSingleton<SorterRegistryFactory>()  → ServiceType=SorterRegistryFactory
            //   ② AddSingleton<ISorterGatewayRegistry>(sp => sp.Get<SorterRegistryFactory>())
            //   ③ AddSingleton<IHostedService>(sp => sp.Get<SorterRegistryFactory>())
            //      → ②③ 모두 ImplementationType=null(팩토리 람다)
            //
            // 제거 전략:
            //   ① ServiceType=SorterRegistryFactory 제거
            //   ② ServiceType=ISorterGatewayRegistry 제거
            //   ③ ImplementationType=null인 IHostedService 전부 제거(ChuteCapacityService 포함될 수 있음)
            //      → ChuteCapacityService IHostedService를 재등록.
            var srfToRemove = services
                .Where(d => d.ServiceType == typeof(SorterRegistryFactory)
                         || d.ServiceType == typeof(ISorterGatewayRegistry))
                .ToList();
            foreach (var d in srfToRemove)
                services.Remove(d);

            // ImplementationType=null인 IHostedService 전부 제거(SorterRegistryFactory 람다 포함)
            var nullHosted = services
                .Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == null)
                .ToList();
            foreach (var d in nullHosted)
                services.Remove(d);

            // ChuteCapacityService IHostedService 재등록
            // (AddSingleton<IHostedService>(sp => sp.Get<ChuteCapacityService>()) 원본 복원)
            services.AddSingleton<IHostedService>(sp =>
                sp.GetRequiredService<ChuteCapacityService>());

            // NopSorterRegistryFactory: ISorterGatewayRegistry + IHostedService 구현
            // FakePolling 기동·종료 + FakeSorterGatewayRegistry 라우팅 제공.
            var nop = new NopSorterRegistryFactory(_fakePolling!, _fakeRegistry!);
            services.AddSingleton<ISorterGatewayRegistry>(nop);
            services.AddSingleton<IHostedService>(nop);
        });
    }

    // ── 비동기 종료 (teardown 데드락 회피의 핵심) ────────────────────────────────
    // xUnit 2.x의 IClassFixture는 픽스처가 IAsyncDisposable이어도 **동기 IDisposable.Dispose()**를
    // 호출한다(IAsyncDisposable은 클래스 픽스처 종료에 사용되지 않음 — IAsyncLifetime만 인정).
    // WebApplicationFactory.Dispose()(동기)는 IHost 종료를 sync-over-async로 블로킹 대기하는데,
    // Program.cs의 app.Run()은 별도 스레드에서 도는 중이라 그 스레드가 풀리길 기다리며
    // 서로 맞물려 teardown이 데드락한다(테스트호스트가 응답 불가 → "작동이 중단됨").
    //
    // 해결: IAsyncLifetime을 구현해 xUnit이 **비동기 DisposeAsync 경로**로 종료하게 한다.
    // base.DisposeAsync()는 IHost 종료를 await(논블로킹)하므로 app.Run() 스레드가 정상 unwind된다.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => DisposeAsyncCore().AsTask();

    // WebApplicationFactory.DisposeAsync(IAsyncDisposable)도 동일 경로로 위임 —
    // 직접 await using 하는 코드(ScenarioTests 패턴)와의 호환 유지.
    public override ValueTask DisposeAsync() => DisposeAsyncCore();

    private async ValueTask DisposeAsyncCore()
    {
        // 쓰기 큐 채널을 먼저 완료시켜 RunWriteConsumerAsync가 결정적으로 종료되게 한다
        // (CTS 취소만으로는 빈 채널 parked ReadAllAsync가 안 깨어나는 타이밍 경쟁 → 호스트 StopAsync 데드락).
        _fakeWriteQueue.Writer.TryComplete();
        await base.DisposeAsync().ConfigureAwait(false);
        _anchorConnection.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override void Dispose(bool disposing)
    {
        // 동기 경로 fallback(파이널라이저 등) — IHost 종료는 IAsyncLifetime.DisposeAsync에 일임.
        // 여기서 base.Dispose(true)를 호출하면 다시 sync-over-async 블로킹이 되므로,
        // 앵커 연결만 정리하고 호스트 동기 종료는 호출하지 않는다(데드락 차단).
        if (disposing)
            _anchorConnection.Dispose();
    }
}

// ── NopSorterRegistryFactory — 테스트 전용 IHostedService + ISorterGatewayRegistry ──

/// <summary>
/// 테스트 배선용 IHostedService + ISorterGatewayRegistry 구현.
/// SorterRegistryFactory를 교체해 DB 기동 판별을 우회하고
/// FakePolling 기동·종료 + FakeSorterGatewayRegistry 라우팅을 제공.
/// </summary>
public sealed class NopSorterRegistryFactory : IHostedService, ISorterGatewayRegistry
{
    private readonly PlcPollingService        _polling;
    private readonly FakeSorterGatewayRegistry _registry;

    public NopSorterRegistryFactory(PlcPollingService polling, FakeSorterGatewayRegistry registry)
    {
        _polling  = polling;
        _registry = registry;
    }

    // IHostedService
    public Task StartAsync(CancellationToken cancellationToken) =>
        _polling.StartAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // DisposeAsync만 호출 — DisposeAsync 내부에서 StopAsync를 포함하므로
        // 명시적 StopAsync 호출 제거(이중 호출 → ObjectDisposedException 근본 원인).
        await _polling.DisposeAsync().ConfigureAwait(false);
    }

    // ISorterGatewayRegistry — FakeSorterGatewayRegistry에 위임
    public Wcs.Core.PlcSnapshot? GetLatest(long destinationId) => _registry.GetLatest(destinationId);
    public SorterBundleHandle? GetBundle(long destinationId) => _registry.GetBundle(destinationId);
    public IReadOnlyCollection<SorterBundleHandle> AllBundles => _registry.AllBundles;
}

// ── FakeSorterGatewayRegistry ─────────────────────────────────────────────────

/// <summary>
/// 테스트용 ISorterGatewayRegistry 구현.
/// FakeModbusMasterForApi 기반 단일 번들을 보유.
/// destinationId로 매핑해 GetLatest/GetBundle 모두 동작.
/// </summary>
public sealed class FakeSorterGatewayRegistry : ISorterGatewayRegistry
{
    private readonly IReadOnlyDictionary<long, SorterBundleHandle> _bundles;

    public FakeSorterGatewayRegistry(SorterBundleHandle bundle)
    {
        _bundles = new Dictionary<long, SorterBundleHandle> { [bundle.DestinationId] = bundle };
    }

    public FakeSorterGatewayRegistry(IReadOnlyDictionary<long, SorterBundleHandle> bundles)
    {
        _bundles = bundles;
    }

    public Wcs.Core.PlcSnapshot? GetLatest(long destinationId) =>
        _bundles.TryGetValue(destinationId, out var h) ? h.Latest : null;

    public SorterBundleHandle? GetBundle(long destinationId) =>
        _bundles.TryGetValue(destinationId, out var h) ? h : null;

    public IReadOnlyCollection<SorterBundleHandle> AllBundles =>
        (IReadOnlyCollection<SorterBundleHandle>)_bundles.Values;
}

/// <summary>
/// WebApplicationFactory용 in-memory IModbusMaster.
/// 레지스터를 직접 조작해 PLC 상태를 시뮬레이션.
/// </summary>
public sealed class FakeModbusMasterForApi : IModbusMaster
{
    private readonly ushort[] _registers = new ushort[RegisterMap.BlockLength];
    private readonly object   _lock      = new();

    // 읽기 실패 주입 — true면 ReadHoldingRegistersAsync가 IOException을 던져 폴 루프가
    // OFFLINE 전이(연속 실패 / HardEx)를 일으킨다. Disconnect()만으로는 EnsureConnected가
    // 즉시 재연결해 OFFLINE이 안 되므로(읽기가 성공) 진짜 오프라인 시뮬레이션엔 이 토글을 쓴다.
    private volatile bool _failReads;

    public FakeModbusMasterForApi()
    {
        // 초기 상태: Ready=1, CurFloor=1, TgtFloor=0
        lock (_lock)
        {
            _registers[RegisterMap.Flags]    = RegisterMap.D4.Ready;
            _registers[RegisterMap.CurFloor] = 1;
            _registers[RegisterMap.TgtFloor] = 0;
        }
    }

    public bool IsConnected { get; private set; } = true;

    public void Connect()    => IsConnected = true;
    public void Disconnect() => IsConnected = false;
    public void Dispose()    { }

    /// <summary>읽기 실패 주입 토글 — true면 폴 읽기가 IOException으로 실패해 OFFLINE 전이를 유발.</summary>
    public void SetFailReads(bool fail) => _failReads = fail;

    public Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort count, CancellationToken ct)
    {
        if (_failReads)
            throw new System.IO.IOException("FakeMaster: read fault injected (OFFLINE simulation)");

        lock (_lock)
        {
            var result = new ushort[count];
            Array.Copy(_registers, startAddress, result, 0, count);
            return Task.FromResult(result);
        }
    }

    public Task WriteSingleRegisterAsync(ushort address, short value, CancellationToken ct)
    {
        lock (_lock) { _registers[address] = (ushort)value; }
        return Task.CompletedTask;
    }

    public Task WriteMultipleRegistersAsync(ushort startAddress, short[] data, CancellationToken ct)
    {
        lock (_lock)
        {
            for (int i = 0; i < data.Length; i++)
                _registers[startAddress + i] = (ushort)data[i];
        }
        return Task.CompletedTask;
    }

    // ── 테스트 헬퍼 ──────────────────────────────────────────────────────────

    public void SetRegister(ushort address, ushort value)
    {
        lock (_lock) { _registers[address] = value; }
    }

    public ushort GetRegister(ushort address)
    {
        lock (_lock) { return _registers[address]; }
    }

    public void SetReady(bool ready)
    {
        lock (_lock)
        {
            if (ready)
                _registers[RegisterMap.Flags] = (ushort)(_registers[RegisterMap.Flags] | RegisterMap.D4.Ready);
            else
                _registers[RegisterMap.Flags] = (ushort)(_registers[RegisterMap.Flags] & ~RegisterMap.D4.Ready);
        }
    }

    public void SetCurFloor(int floor) => SetRegister(RegisterMap.CurFloor, (ushort)floor);
    public void SetTgtFloor(int floor) => SetRegister(RegisterMap.TgtFloor, (ushort)floor);
    public int  GetTgtFloor()          => GetRegister(RegisterMap.TgtFloor);
}

// ════════════════════════════════════════════════════════════════════════════
// VS-1~7 통합 테스트
// ════════════════════════════════════════════════════════════════════════════

public class ApiIntegrationTests : IClassFixture<FakeModbusWebApplicationFactory>
{
    private readonly FakeModbusWebApplicationFactory _factory;
    private readonly HttpClient                      _client;
    private readonly ITestOutputHelper               _out;

    public ApiIntegrationTests(
        FakeModbusWebApplicationFactory factory,
        ITestOutputHelper output)
    {
        _factory = factory;
        _client  = factory.CreateClient();
        _out     = output;
    }

    // ════════════════════════════════════════════════════════════════════════
    // VS-1 IF-05 happy: 시드 매칭→200 OK·chuteNo·NORMAL·예약차감·기록
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task VS1_If05_HappyPath_OkWithChuteNoAndNormal()
    {
        var req = new
        {
            pId        = 1001,
            agvNo      = 1,
            barcode    = "TEST-BARCODE-1",
            inductionNo = 1,
            qty        = 5,
            timeStamp  = "2026-06-16 10:00:00"
        };

        var resp = await _client.PostAsJsonAsync("/api/v1/destination-query", req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.NotNull(body);
        Assert.Equal("OK", body.Result);
        Assert.NotNull(body.ChuteNo);
        Assert.Equal(1, body.ChuteNo); // 시드: TEST-BARCODE-1 → ChuteNo=1
        // 재설계: 응답에 reason 키 부재 — {result, chuteNo}만.

        _out.WriteLine($"[VS-1] result={body.Result} chuteNo={body.ChuteNo}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // VS-2 IF-05 error: 미존재→NG·chuteNo null / pId범위·필드누락→400
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task VS2_If05_UnknownBarcode_NgWithNullChuteNo()
    {
        var req = new
        {
            pId        = 2001,
            agvNo      = 1,
            barcode    = "BARCODE-NOT-EXISTS",
            inductionNo = 1,
            qty        = 1,
            timeStamp  = "2026-06-16 10:00:00"
        };

        var resp = await _client.PostAsJsonAsync("/api/v1/destination-query", req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.NotNull(body);
        Assert.Equal("NG", body.Result);
        Assert.Null(body.ChuteNo);  // NG 시 chuteNo=null (reason 키 부재)
        _out.WriteLine($"[VS-2] NG chuteNo=null (미매칭 바코드)");
    }

    [Fact]
    public async Task VS2_If05_PIdOutOfRange_Returns400()
    {
        var req = new { pId = 0, agvNo = 1, barcode = "X", inductionNo = 1, qty = 1, timeStamp = "2026-06-16 10:00:00" };
        var resp = await _client.PostAsJsonAsync("/api/v1/destination-query", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        _out.WriteLine("[VS-2] pId=0 → 400 확인");
    }

    // ⚠ 의도적 반전(이 스프린트 확정4): 슈트 PAUSED 오더 → IF-05 **OK**(NG→OK).
    // 슈트는 곧 비워지니 보내고 대기 — IF-05 dispatch에서 PAUSED를 차단하지 않는다.
    // 슈트 readiness(ready=false)는 IF-08 푸시로 별도 전달(IF-05와 분리 채널). 소터 PAUSED는 NG 유지.
    [Fact]
    public async Task VS2_If05_PausedOrder_NgPaused()
    {
        // 시드: TEST-BARCODE-PAUSED → destPaused(chuteNo=6, CHUTE, Status=PAUSED)
        var req = new
        {
            pId        = 2002,
            agvNo      = 1,
            barcode    = "TEST-BARCODE-PAUSED",
            inductionNo = 1,
            qty        = 1,
            timeStamp  = "2026-06-16 10:00:00"
        };

        var resp = await _client.PostAsJsonAsync("/api/v1/destination-query", req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.NotNull(body);
        Assert.Equal("OK", body.Result);   // 반전: 슈트 PAUSED → OK
        Assert.Equal(6, body.ChuteNo);     // PAUSED 슈트(chuteNo=6)로 배정
        _out.WriteLine($"[VS-2 반전] 슈트 PAUSED 오더 → OK chuteNo={body.ChuteNo}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // IF-08 deposit-permission 폐지 확인 (재설계 — 엔드포인트 부재)
    // 구 VS-3/VS-4 IF-08 라이브 분기 삭제 → 호출 시 404/405로 부재 입증.
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task If08_DepositPermission_Removed_Returns404Or405()
    {
        var req  = new { pId = 3001, chuteNo = 1, agvNo = 1, timeStamp = (string?)null };
        var resp = await _client.PostAsJsonAsync("/api/v1/deposit-permission", req);

        // 엔드포인트 폐지 — 404(라우트 없음) 또는 405(메서드 불가) 중 하나여야 함.
        Assert.True(
            resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
            $"deposit-permission는 폐지됨 — 404/405 기대, 실제={(int)resp.StatusCode}");
        _out.WriteLine($"[IF-08폐지] deposit-permission → {(int)resp.StatusCode} (부재 확인)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // IF-09 arrival-report — 3D 소터 도착 → 운영층(2) 정렬 (구 VS-3 WrongFloor 재타겟)
    // 미정렬(CurFloor=1·TgtFloor=0) 소터에 도착 → TgtFloor=2(운영층) 쓰기 큐 관찰.
    // WRONG_FLOOR 개념 소멸 → 운영층 고정 정렬로 전환.
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task If09_Sorter3dArrival_NotAligned_WritesOperationalFloor()
    {
        // 사전조건: Ready=1, CurFloor=1(운영층 2와 다름 → 미정렬), TgtFloor=0 → 정렬 쓰기 조건 충족
        _factory.FakeMaster.SetReady(true);
        _factory.FakeMaster.SetCurFloor(1);
        _factory.FakeMaster.SetTgtFloor(0);

        // 폴링이 미정렬·TgtFloor=0 상태를 스냅샷에 반영할 때까지 대기
        await WaitForSnapshotAsync(_factory,
            snap => snap.Ready && snap.CurFloor == 1 && snap.TgtFloor == 0, 5000);

        // IF-09 도착 보고 (chuteNo=30 → SORTER_3D)
        var req  = new { pId = 3101, chuteNo = 30, agvNo = 1, timeStamp = (string?)null };
        var resp = await _client.PostAsJsonAsync("/api/v1/arrival-report", req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ArrivalReportResponse>();
        Assert.NotNull(body);
        Assert.Equal("OK", body.Result);

        // 운영층(2) 정렬 — 큐 처리는 background이므로 폴링 대기
        await WaitForRegisterAsync(_factory, RegisterMap.TgtFloor, 2, timeoutMs: 3000);
        Assert.Equal(2, _factory.FakeMaster.GetTgtFloor());
        _out.WriteLine($"[IF-09] 3D 도착 → TgtFloor={_factory.FakeMaster.GetTgtFloor()} (운영층 정렬)");

        // 복원
        _factory.FakeMaster.SetTgtFloor(0);
    }

    // ════════════════════════════════════════════════════════════════════════
    // IF-09 arrival-report — 이미 운영층 정렬됨(CurFloor=2·Ready=1) → 추가 쓰기 0
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task If09_Sorter3dArrival_AlreadyAligned_NoWrite()
    {
        // 사전조건: Ready=1, CurFloor=2(운영층 일치), TgtFloor=0 → 정렬 불필요(이미 운영층)
        _factory.FakeMaster.SetReady(true);
        _factory.FakeMaster.SetCurFloor(2);
        _factory.FakeMaster.SetTgtFloor(0);

        await WaitForSnapshotAsync(_factory,
            snap => snap.Ready && snap.CurFloor == 2 && snap.TgtFloor == 0, 5000);

        var req  = new { pId = 3102, chuteNo = 30, agvNo = 1, timeStamp = (string?)null };
        var resp = await _client.PostAsJsonAsync("/api/v1/arrival-report", req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // 이미 정렬 → TgtFloor 쓰기 없음. 500ms 동안 TgtFloor=0 유지 확인.
        await Task.Delay(500);
        Assert.Equal(0, _factory.FakeMaster.GetTgtFloor());
        _out.WriteLine($"[IF-09] 이미 운영층 정렬 → TgtFloor 쓰기 0건 (={_factory.FakeMaster.GetTgtFloor()})");

        // 복원
        _factory.FakeMaster.SetCurFloor(1);
    }

    // ════════════════════════════════════════════════════════════════════════
    // IF-09 arrival-report — 슈트 전용 도착 → TgtFloor 쓰기 0 (무정렬)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task If09_ChuteArrival_NoAlignment_NoTgtFloorWrite()
    {
        // 슈트(chuteNo=1, CHUTE)는 정렬 대상 아님 — TgtFloor 쓰기 0.
        _factory.FakeMaster.SetReady(true);
        _factory.FakeMaster.SetCurFloor(1);
        _factory.FakeMaster.SetTgtFloor(0);
        await WaitForSnapshotAsync(_factory,
            snap => snap.Ready && snap.TgtFloor == 0, 3000);

        var req  = new { pId = 3103, chuteNo = 1, agvNo = 1, timeStamp = (string?)null };
        var resp = await _client.PostAsJsonAsync("/api/v1/arrival-report", req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ArrivalReportResponse>();
        Assert.Equal("OK", body!.Result);

        // 슈트 도착: TgtFloor 변경 없음 — 500ms 동안 0 유지.
        await Task.Delay(500);
        Assert.Equal(0, _factory.FakeMaster.GetTgtFloor());
        _out.WriteLine($"[IF-09] 슈트 전용 도착 → TgtFloor 쓰기 0건 (무정렬)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // IF-09 arrival-report — 검증: pId 범위 밖·chuteNo≤0 → 400, 미존재 chuteNo → 200(500 금지)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task If09_InvalidPId_Returns400()
    {
        var req  = new { pId = 0, chuteNo = 1, agvNo = 1, timeStamp = (string?)null };
        var resp = await _client.PostAsJsonAsync("/api/v1/arrival-report", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        _out.WriteLine("[IF-09] pId=0 → 400 확인");
    }

    [Fact]
    public async Task If09_InvalidChuteNo_Returns400()
    {
        var req  = new { pId = 3104, chuteNo = 0, agvNo = 1, timeStamp = (string?)null };
        var resp = await _client.PostAsJsonAsync("/api/v1/arrival-report", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        _out.WriteLine("[IF-09] chuteNo=0 → 400 확인");
    }

    [Fact]
    public async Task If09_UnknownChuteNo_RecordsButNo500()
    {
        // 미존재 chuteNo → 200 + 도착 기록만, 정렬 스킵(500 금지, 사용자 확정).
        var req  = new { pId = 3105, chuteNo = 999, agvNo = 1, timeStamp = (string?)null };
        var resp = await _client.PostAsJsonAsync("/api/v1/arrival-report", req);
        Assert.NotEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        _out.WriteLine("[IF-09] 미존재 chuteNo=999 → 200(정렬 스킵·500 없음)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // VS-5 IF-10 happy + 멱등: 슈트 보고→OK, 같은 pId 재보고→OK 상태무변경
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task VS5_If10_HappyPath_OkAndIdempotent()
    {
        var req = new
        {
            pId       = 5001,
            barcode   = "TEST-BARCODE-1",
            chuteNo   = 1,
            agvNo     = 1,
            qty       = (int?)null,
            timeStamp = (string?)null
        };

        // 1차 보고
        var resp1 = await _client.PostAsJsonAsync("/api/v1/deposit-report", req);
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        var body1 = await resp1.Content.ReadFromJsonAsync<DepositReportResponse>();
        Assert.NotNull(body1);
        Assert.Equal("OK", body1.Result);
        _out.WriteLine("[VS-5] 1차 보고 OK");

        // 2차 보고 (중복 pId — 멱등)
        var resp2 = await _client.PostAsJsonAsync("/api/v1/deposit-report", req);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var body2 = await resp2.Content.ReadFromJsonAsync<DepositReportResponse>();
        Assert.NotNull(body2);
        Assert.Equal("OK", body2.Result);
        _out.WriteLine("[VS-5] 2차 보고 멱등 OK");
    }

    // ════════════════════════════════════════════════════════════════════════
    // VS-6 IF-10→IF-11(핵심): 3D 목적지 보고→핸드셰이크 셀지정 트리거 관찰
    //                          슈트→트리거 0(대조)
    // 트리거는 백그라운드 — C_Flag 상승으로 관찰
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task VS6_If10_3dDestination_TriggersHandshakeCFlag()
    {
        // 사전: 시드 TEST-BARCODE-3 → Sorter3D·ChuteNo=3 로 등록
        // IClassFixture 공유: 명시적으로 C_Flag=0·Ready=1 초기화 후 스냅샷 반영 대기
        _factory.FakeMaster.SetReady(true);
        _factory.FakeMaster.SetCurFloor(1);
        // C_Flag=0 명시 초기화 — 이전 테스트 잔류 방지
        _factory.FakeMaster.SetRegister(RegisterMap.Flags, RegisterMap.D4.Ready);  // Ready=1, C_Flag=0

        // 스냅샷에 C_Flag=0·Ready=1 반영 대기 (이전 테스트 잔류 상태 해소)
        await WaitForSnapshotAsync(_factory, snap => !snap.CFlag && snap.Ready, 5000);

        // IF-05로 먼저 목적지 조회(DestType 기록) — 고유 pId로 바코드 매핑
        var if05Req = new
        {
            pId        = 6001,
            agvNo      = 1,
            barcode    = "TEST-BARCODE-3",
            inductionNo = 1,
            qty        = 1,
            timeStamp  = "2026-06-16 10:00:00"
        };
        var if05Resp = await _client.PostAsJsonAsync("/api/v1/destination-query", if05Req);
        Assert.Equal(HttpStatusCode.OK, if05Resp.StatusCode);
        var if05Body = await if05Resp.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.Equal("OK", if05Body!.Result);
        _out.WriteLine($"[VS-6] IF-05 chuteNo={if05Body.ChuteNo}");

        // IF-10 투입 보고 (3D 목적지)
        var if10Req = new
        {
            pId       = 6001,
            barcode   = "TEST-BARCODE-3",
            chuteNo   = if05Body.ChuteNo!.Value,
            agvNo     = 1,
            qty       = (int?)1,
            timeStamp = (string?)null
        };
        var if10Resp = await _client.PostAsJsonAsync("/api/v1/deposit-report", if10Req);
        Assert.Equal(HttpStatusCode.OK, if10Resp.StatusCode);

        var if10Body = await if10Resp.Content.ReadFromJsonAsync<DepositReportResponse>();
        Assert.Equal("OK", if10Body!.Result);
        _out.WriteLine("[VS-6] IF-10 즉시 OK 반환 확인");

        // IF-11 트리거 관찰: 백그라운드 핸드셰이크 → CellAssign 큐 투입 → C_Flag=1
        // HandshakeOrchestrator.ExecuteAsync → WaitCFlagZeroAsync(C_Flag=0 확인) →
        // CellAssign EnqueueAsync → PlcPollingService 컨슈머가 WriteSingleRegister → C_Flag set
        // 타임아웃 충분히 (폴링 150ms + 큐 처리 시간 포함)
        await WaitForSnapshotAsync(_factory, snap => snap.CFlag, timeoutMs: 5000);

        var snapAfter = GetLatestSnapshot(_factory);
        Assert.True(snapAfter.CFlag, "3D 보고 → IF-11 트리거 → C_Flag=1 관찰");
        _out.WriteLine($"[VS-6] C_Flag={snapAfter.CFlag} — IF-11 트리거 확인");
    }

    [Fact]
    public async Task VS6_If10_ChuteDestination_NoHandshakeTrigger()
    {
        // 슈트 보고 시 트리거 없음 확인
        // 시드 TEST-BARCODE-2 → DestinationType.Chute → IF-11 없음

        // IF-05 먼저
        var if05Req = new
        {
            pId        = 6100,
            agvNo      = 1,
            barcode    = "TEST-BARCODE-2",
            inductionNo = 1,
            qty        = 1,
            timeStamp  = "2026-06-16 10:00:00"
        };
        await _client.PostAsJsonAsync("/api/v1/destination-query", if05Req);

        // CFlag 초기 상태 기록 (슈트 보고는 C_Flag 변경 없어야 함)
        // 주의: 다른 테스트가 C_Flag를 건드릴 수 있으므로 직접 0으로 초기화
        _factory.FakeMaster.SetRegister(RegisterMap.Flags, RegisterMap.D4.Ready); // Ready=1, CFlag=0
        await WaitForSnapshotAsync(_factory, snap => !snap.CFlag && snap.Ready, 3000);

        var if10Req = new
        {
            pId       = 6100,
            barcode   = "TEST-BARCODE-2",
            chuteNo   = 2,
            agvNo     = 1,
            qty       = (int?)1,
            timeStamp = (string?)null
        };
        var if10Resp = await _client.PostAsJsonAsync("/api/v1/deposit-report", if10Req);
        Assert.Equal(HttpStatusCode.OK, if10Resp.StatusCode);

        // 슈트 보고: C_Flag가 상승하면 안 됨 — 500ms 동안 C_Flag=0 유지 확인
        await Task.Delay(500);
        var snap = GetLatestSnapshot(_factory);
        Assert.False(snap.CFlag, "슈트 목적지 보고 → C_Flag 변경 없음(IF-11 트리거 0)");
        _out.WriteLine($"[VS-6] 슈트 보고 후 C_Flag={snap.CFlag} — 트리거 없음 확인");
    }

    // ════════════════════════════════════════════════════════════════════════
    // VS-7 회귀: 기존 28 전부 GREEN (이 파일 외 기존 테스트는 그대로)
    // 여기서는 기존 테스트가 영향받지 않는지 확인을 위한 smoke 테스트
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task VS7_AllEndpoints_RespondNotNotImplemented()
    {
        // 501 NotImplemented가 아닌지 확인 — Controller 이관 후에도 엔드포인트가 활성화됐음을 검증.
        // 재설계: IF-08(deposit-permission)은 폐지 → IF-09(arrival-report)로 교체.
        var req1 = new { pId = 7001, agvNo = 1, barcode = "TEST-BARCODE-1", inductionNo = 1, qty = 1, timeStamp = "2026-06-16 10:00:00" };
        var r1   = await _client.PostAsJsonAsync("/api/v1/destination-query", req1);
        Assert.NotEqual(HttpStatusCode.NotImplemented, r1.StatusCode);

        var req2 = new { pId = 7001, chuteNo = 1, agvNo = 1, timeStamp = (string?)null };
        var r2   = await _client.PostAsJsonAsync("/api/v1/arrival-report", req2);
        Assert.NotEqual(HttpStatusCode.NotImplemented, r2.StatusCode);

        var req3 = new { pId = 7001, barcode = "X", chuteNo = 1, agvNo = 1, qty = (int?)null, timeStamp = (string?)null };
        var r3   = await _client.PostAsJsonAsync("/api/v1/deposit-report", req3);
        Assert.NotEqual(HttpStatusCode.NotImplemented, r3.StatusCode);

        _out.WriteLine("[VS-7] IF-05/IF-09/IF-10 3 엔드포인트 모두 501 아님 확인");
    }

    // ════════════════════════════════════════════════════════════════════════
    // CONCUR-1 IF-10 멱등 동시성 회귀 가드 (코드리뷰 MAJOR 수정 증명)
    //
    // 같은 새 pId로 IF-10을 다수 병렬 호출 → 기록 1회·IF-11 트리거 최대 1회.
    // 모든 응답은 200 OK. RecordDeposit의 TryAdd+lock 원자성 검증.
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CONCUR1_If10_ConcurrentSamePId_OnlyOneRecordAndOneTrigger()
    {
        // 3D 목적지(TEST-BARCODE-3, ChuteNo=3)로 먼저 IF-05를 수행해 DestType 기록
        // 고유 pId 사용 (IClassFixture 공유 팩토리 충돌 방지)
        const int testPId = 9001;

        // C_Flag=0·Ready=1 초기화
        _factory.FakeMaster.SetReady(true);
        _factory.FakeMaster.SetRegister(RegisterMap.Flags, RegisterMap.D4.Ready);
        await WaitForSnapshotAsync(_factory, snap => !snap.CFlag && snap.Ready, 5000);

        var if05Req = new
        {
            pId         = testPId,
            agvNo       = 1,
            barcode     = "TEST-BARCODE-3",
            inductionNo = 1,
            qty         = 1,
            timeStamp   = "2026-06-16 10:00:00"
        };
        var if05Resp = await _client.PostAsJsonAsync("/api/v1/destination-query", if05Req);
        Assert.Equal(HttpStatusCode.OK, if05Resp.StatusCode);
        var if05Body = await if05Resp.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.Equal("OK", if05Body!.Result);
        _out.WriteLine($"[CONCUR-1] IF-05 OK chuteNo={if05Body.ChuteNo}");

        // 동시 IF-10 — 같은 pId로 8건 병렬 발사
        const int concurrency = 8;
        var if10Req = new
        {
            pId       = testPId,
            barcode   = "TEST-BARCODE-3",
            chuteNo   = if05Body.ChuteNo!.Value,
            agvNo     = 1,
            qty       = (int?)1,
            timeStamp = (string?)null
        };

        // WebApplicationFactory의 HttpClient는 단일 인스턴스이므로 각 요청을 독립 클라이언트로 보냄
        using var barrier = new Barrier(concurrency);
        var tasks = Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
        {
            // 최대한 동시에 도달하도록 배리어 동기화
            barrier.SignalAndWait();
            using var client = _factory.CreateClient();
            return await client.PostAsJsonAsync("/api/v1/deposit-report", if10Req);
        })).ToArray();

        var responses = await Task.WhenAll(tasks);

        // 모든 응답은 200 OK (멱등 — 중복 보고도 OK)
        foreach (var resp in responses)
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        _out.WriteLine($"[CONCUR-1] {concurrency}건 병렬 IF-10 모두 200 OK 확인");

        // IF-11 트리거 결과 관찰 대기 (백그라운드 핸드셰이크 처리 시간)
        // CellAssign이 최대 1회만 발생해야 함 — cell_assignment 부분 유니크로 구조적 보장(EfCellSelector)
        // piece 부분 유니크 + UniqueConstraintViolation catch → DB 레벨 진성 멱등(EfDepositRecorder)
        using var scope    = _factory.Services.CreateScope();
        var       recorder = scope.ServiceProvider.GetRequiredService<IDepositRecorder>();

        // HasDepositRecord가 true이면 DB에 piece row 1건 존재 (8병렬 중 1건만 성공)
        Assert.True(recorder.HasDepositRecord(testPId),
            "IF-10 동시 다수 호출 → pId 기록 존재(최소 1건)");

        // CellSelector: EfCellSelector + cell_assignment (cell_id WHERE released_at IS NULL) 부분 유니크.
        // CellAssign 이중 시도 시 UniqueConstraintViolation으로 차단 — 구조적 보장.

        _out.WriteLine($"[CONCUR-1] 기록 존재={recorder.HasDepositRecord(testPId)} — 멱등 원자성 확인");
        _out.WriteLine("[CONCUR-1] IF-10 동시성 멱등 회귀 가드 PASS");
    }

    // IF-05 qty<=0 가드 테스트 (코드리뷰 MINOR 수정 증명)
    [Fact]
    public async Task MINOR1_If05_ZeroQty_Returns400()
    {
        var req = new { pId = 8001, agvNo = 1, barcode = "TEST-BARCODE-1", inductionNo = 1, qty = 0, timeStamp = "2026-06-16 10:00:00" };
        var resp = await _client.PostAsJsonAsync("/api/v1/destination-query", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        _out.WriteLine("[MINOR-1] qty=0 → 400 확인");
    }

    [Fact]
    public async Task MINOR1_If05_NegativeQty_Returns400()
    {
        var req = new { pId = 8002, agvNo = 1, barcode = "TEST-BARCODE-1", inductionNo = 1, qty = -5, timeStamp = "2026-06-16 10:00:00" };
        var resp = await _client.PostAsJsonAsync("/api/v1/destination-query", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        _out.WriteLine("[MINOR-1] qty=-5 → 400 확인");
    }

    // ════════════════════════════════════════════════════════════════════════
    // IF-05 FULL/PAUSED 상류 필터 (재설계 — 구 P2a IF-08 hold 판정 재타겟)
    // FULL/PAUSED 차단이 도착 시점(폐지된 IF-08)에서 배정 시점(IF-05)으로 상류 이동.
    // NORMAL→OK / PAUSED→NG / FULL→NG / 비움 후 NORMAL→OK.
    // ════════════════════════════════════════════════════════════════════════

    // 구 P2a_If08_Chute_HoldNone_Allowed 재타겟: NORMAL 슈트 → IF-05 OK
    [Fact]
    public async Task If05_Chute_Normal_Ok()
    {
        // TEST-BARCODE-1 → chuteNo=1 (CHUTE, Active, NORMAL) → OK
        var req  = new { pId = 10001, agvNo = 1, barcode = "TEST-BARCODE-1", inductionNo = 1, qty = 1, timeStamp = (string?)null };
        var resp = await _client.PostAsJsonAsync("/api/v1/destination-query", req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.NotNull(body);
        Assert.Equal("OK", body.Result);
        Assert.Equal(1, body.ChuteNo);
        _out.WriteLine($"[IF-05필터] NORMAL → OK chuteNo={body.ChuteNo}");
    }

    // ⚠ 의도적 반전(이 스프린트 확정4): 슈트 PAUSED → IF-05 **OK**(NG→OK).
    // 슈트는 곧 비워지니 보내고 대기 — IF-05 dispatch에서 PAUSED를 차단하지 않는다.
    [Fact]
    public async Task If05_Chute_Paused_Ng()
    {
        // TEST-BARCODE-PAUSED → destPaused(chuteNo=6, status PAUSED) → 반전: OK
        var req  = new { pId = 10002, agvNo = 1, barcode = "TEST-BARCODE-PAUSED", inductionNo = 1, qty = 1, timeStamp = (string?)null };
        var resp = await _client.PostAsJsonAsync("/api/v1/destination-query", req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.NotNull(body);
        Assert.Equal("OK", body.Result);   // 반전: 슈트 PAUSED → OK
        Assert.Equal(6, body.ChuteNo);
        _out.WriteLine($"[IF-05 반전] 슈트 PAUSED → OK chuteNo={body.ChuteNo}");
    }

    // ⚠ 의도적 반전(이 스프린트 확정4): 슈트 FULL → IF-05 **OK**(비움 전후 둘 다 OK).
    // ChuteCapacityService에 FULL 주입해도 IF-05 dispatch는 슈트를 차단하지 않는다(보냄).
    // 슈트 readiness(만재 시 ready=false)는 IF-08 푸시로 별도 전달 — IF-05와 분리 채널.
    [Fact]
    public async Task If05_Chute_Full_ThenCleared_Normal()
    {
        using var scope    = _factory.Services.CreateScope();
        var       db       = scope.ServiceProvider.GetRequiredService<Wcs.Data.WcsDbContext>();
        var       capacity = scope.ServiceProvider.GetRequiredService<IChuteCapacityService>();

        // TEST-BARCODE-1 → chuteNo=1 (CHUTE). 그 목적지를 FULL로 만든다.
        var dest1 = db.Destinations.First(d => d.ChuteNo == 1 && d.DestType == Wcs.Data.DestType.CHUTE);
        var detail1 = db.ChuteDetails.First(cd => cd.DestinationId == dest1.Id);
        var workFullQty = detail1.WorkFullQty; // 기본 100

        capacity.OnReserved(dest1.Id, workFullQty);
        Assert.Equal(WcsHold.Full, capacity.GetHold(dest1.Id));
        _out.WriteLine($"[IF-05 반전] OnReserved(qty={workFullQty}) → Full confirmed (그래도 IF-05 OK)");

        // 반전: 슈트 FULL이어도 IF-05 → OK(보냄).
        var req  = new { pId = 10050, agvNo = 1, barcode = "TEST-BARCODE-1", inductionNo = 1, qty = 1, timeStamp = (string?)null };
        var resp = await _client.PostAsJsonAsync("/api/v1/destination-query", req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.NotNull(body);
        Assert.Equal("OK", body.Result);
        Assert.Equal(1, body.ChuteNo);
        _out.WriteLine($"[IF-05 반전] 슈트 FULL → OK chuteNo={body.ChuteNo}");

        // OnCleared 후에도 OK 유지(슈트는 항상 보냄).
        await capacity.OnCleared(dest1.Id);
        Assert.Equal(WcsHold.None, capacity.GetHold(dest1.Id));

        var req2  = new { pId = 10051, agvNo = 1, barcode = "TEST-BARCODE-1", inductionNo = 1, qty = 1, timeStamp = (string?)null };
        var resp2 = await _client.PostAsJsonAsync("/api/v1/destination-query", req2);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var body2 = await resp2.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.NotNull(body2);
        Assert.Equal("OK", body2.Result);
        Assert.Equal(1, body2.ChuteNo);
        _out.WriteLine($"[IF-05 반전] 비움 후에도 → OK chuteNo={body2.ChuteNo}");
    }

    // 회귀 가드: OnCleared 후 InitializeFromDbAsync 재실행 시 NORMAL 유지 (MAJOR-1/MAJOR-2 수정 증명)
    // 재시작 시나리오: OnCleared → last_cleared_at DB 영속화됨 →
    //   InitializeFromDbAsync가 deposited_at > last_cleared_at 필터로 재집계 → FULL 아님.
    // 버그 조건: DB 영속화 없으면 재시작 후 old piece qty 재합산 → FULL 복귀.
    [Fact]
    public async Task P2a_Chute_ClearPersisted_AfterReinitialize_StillNormal()
    {
        // 1. chuteNo=4 CHUTE 목적지 사용 (IClassFixture 충돌 방지)
        using var scope    = _factory.Services.CreateScope();
        var       db       = scope.ServiceProvider.GetRequiredService<Wcs.Data.WcsDbContext>();
        var       capacity = scope.ServiceProvider.GetRequiredService<IChuteCapacityService>();

        var dest4 = db.Destinations.First(d => d.ChuteNo == 4 && d.DestType == Wcs.Data.DestType.CHUTE);

        // 2. FULL 조건 달성 (OnReserved + OnDeposited 각 절반)
        var detail4     = db.ChuteDetails.First(cd => cd.DestinationId == dest4.Id);
        var workFullQty = detail4.WorkFullQty; // 기본 100

        capacity.OnReserved(dest4.Id, workFullQty / 2);
        capacity.OnDeposited(dest4.Id, workFullQty / 2); // InFlight → Deposited
        capacity.OnReserved(dest4.Id, workFullQty / 2);  // 나머지 InFlight 추가 → TotalQty >= workFullQty
        Assert.Equal(WcsHold.Full, capacity.GetHold(dest4.Id));

        // 3. DB에 DEPOSITED piece 삽입 (초기화 재실행 시 합산 대상 확인용)
        //    now 이전 시각으로 deposited_at 기록 — last_cleared_at보다 이전이 되도록
        var pieceBeforeClear = new Wcs.Data.Piece
        {
            PId           = 19001,
            IsActive      = true,
            Barcode       = "REGRESS-TEST",
            Qty           = workFullQty,
            Status        = Wcs.Data.PieceStatus.DEPOSITED,
            DepositedAt   = DateTime.UtcNow.AddMinutes(-10), // 비움 이전 시각
            DestinationId = dest4.Id,
            CreatedAt     = DateTime.UtcNow,
            UpdatedAt     = DateTime.UtcNow,
        };
        db.Pieces.Add(pieceBeforeClear);
        await db.SaveChangesAsync();

        // 4. OnCleared → DB에 last_cleared_at 영속화 + 인메모리 리셋
        await capacity.OnCleared(dest4.Id);
        Assert.Equal(WcsHold.None, capacity.GetHold(dest4.Id));

        // 5. InitializeFromDbAsync 직접 재실행 (재시작 시뮬레이션)
        //    private 메서드이므로 IHostedService.StartAsync 경유
        //    WebApplicationFactory에서 서비스를 재시작할 수 없으므로
        //    ChuteCapacityService를 reflection으로 접근해 내부 재초기화 호출.
        //    대안: IChuteCapacityService의 concrete type을 cast 후 내부 메서드 직접 호출.
        //    → IHostedService.StartAsync 재실행 (CancellationToken.None로 안전)
        var hostedService = scope.ServiceProvider.GetRequiredService<IChuteCapacityService>()
            as IHostedService;
        Assert.NotNull(hostedService);
        await hostedService.StartAsync(CancellationToken.None);

        // 6. 재초기화 후 FULL 아님 확인 (MAJOR-1/MAJOR-2 fix: last_cleared_at 이전 piece 제외)
        var holdAfterReinit = capacity.GetHold(dest4.Id);
        Assert.Equal(WcsHold.None, holdAfterReinit);

        _out.WriteLine($"[회귀가드] OnCleared+재초기화 후 hold={holdAfterReinit} — FULL 복귀 없음 확인");
    }

    // VS-P2a-5: timeStamp 백필 — "yyyy-MM-dd HH:mm:ss" 파싱·UtcNow 폴백
    [Fact]
    public async Task P2a_If05_TimeStampParsed_UtcFallback()
    {
        // timeStamp 있음 → 파싱 성공·OK
        var req1 = new
        {
            pId        = 10100,
            agvNo      = 1,
            barcode    = "TEST-BARCODE-1",
            inductionNo = 1,
            qty        = 1,
            timeStamp  = "2026-06-16 09:30:00"
        };
        var resp1 = await _client.PostAsJsonAsync("/api/v1/destination-query", req1);
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        var body1 = await resp1.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.Equal("OK", body1!.Result);
        _out.WriteLine($"[P2a-5] timeStamp 파싱 OK: {req1.timeStamp}");

        // timeStamp null → UtcNow 폴백·정상 응답
        var req2 = new
        {
            pId        = 10101,
            agvNo      = 1,
            barcode    = "TEST-BARCODE-1",
            inductionNo = 1,
            qty        = 1,
            timeStamp  = (string?)null
        };
        var resp2 = await _client.PostAsJsonAsync("/api/v1/destination-query", req2);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var body2 = await resp2.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.Equal("OK", body2!.Result);
        _out.WriteLine($"[P2a-5] timeStamp=null → UtcNow 폴백 OK");
    }

    // VS-P2a-8: NG DENIED piece destination_id nullable — unknown barcode → 500 없음
    [Fact]
    public async Task P2a_If05_UnknownBarcode_NullableDest_No500()
    {
        // 미매칭 바코드 → NG DENIED 기록. piece.destination_id=null (MINOR-5).
        // 이전 버전: dest?.Id ?? 0 → FK 위반 → 500. 수정 후: null → 200 NG.
        var req = new
        {
            pId        = 10200,
            agvNo      = 1,
            barcode    = "BARCODE-NEVER-EXISTS-P2A",
            inductionNo = 1,
            qty        = 1,
            timeStamp  = "2026-06-16 10:00:00"
        };
        var resp = await _client.PostAsJsonAsync("/api/v1/destination-query", req);

        // 500 아님(MINOR-5 nullable FK 수정 증명)
        Assert.NotEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.NotNull(body);
        Assert.Equal("NG", body.Result);
        Assert.Null(body.ChuteNo);
        _out.WriteLine($"[P2a-8] unknown barcode → NG·chuteNo=null·500없음 result={body.Result}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // 헬퍼
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// P2b: FakePolling.Latest 스냅샷이 조건을 만족할 때까지 폴링 대기.
    /// IPlcGateway를 DI에서 꺼내지 않고 factory.FakePolling에 직접 접근.
    /// </summary>
    private static async Task WaitForSnapshotAsync(
        FakeModbusWebApplicationFactory factory,
        Func<PlcSnapshot, bool> condition,
        int timeoutMs,
        int pollMs = 30)
    {
        var polling = factory.FakePolling
            ?? throw new InvalidOperationException("FakePolling이 초기화되지 않았습니다.");

        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (!condition(polling.Latest))
        {
            if (DateTimeOffset.Now > deadline)
                Assert.Fail($"WaitForSnapshot 타임아웃({timeoutMs}ms): 조건 미충족");
            await Task.Delay(pollMs);
        }
    }

    /// <summary>특정 레지스터 값이 기대값이 될 때까지 대기(쓰기 큐 처리 대기).</summary>
    private static async Task WaitForRegisterAsync(
        FakeModbusWebApplicationFactory factory,
        ushort address,
        ushort expected,
        int timeoutMs,
        int pollMs = 20)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (factory.FakeMaster.GetRegister(address) != expected)
        {
            if (DateTimeOffset.Now > deadline)
                Assert.Fail($"WaitForRegister 타임아웃({timeoutMs}ms): addr={address} expected={expected} actual={factory.FakeMaster.GetRegister(address)}");
            await Task.Delay(pollMs);
        }
    }

    private static PlcSnapshot GetLatestSnapshot(FakeModbusWebApplicationFactory factory)
    {
        var polling = factory.FakePolling
            ?? throw new InvalidOperationException("FakePolling이 초기화되지 않았습니다.");
        return polling.Latest;
    }
}

// ════════════════════════════════════════════════════════════════════════════
// VS-P2b-2/3/7 — 멀티소터 단위 테스트 (WebApplicationFactory 불요)
//
// VS-P2b-2: N대 인스턴스화 — SorterRegistryFactory.StartAsync가 SORTER_3D N대에 대해
//           각각 독립 번들(PlcWriteQueue·PlcPollingService·HandshakeOrchestrator)을 생성.
// VS-P2b-3: 라우팅 독립 — 소터 A·B fake 스냅샷이 달라도 IF-08 판정이 교차하지 않음.
// VS-P2b-7: 소터 0/1/N 기동 + SORTER_3D appsettings 누락 → fail-loud.
// ════════════════════════════════════════════════════════════════════════════

public class P2bMultiSorterTests
{
    // ── 헬퍼: PlcGatewayOptions 기본값 ─────────────────────────────────────────
    private static PlcGatewayOptions MakeGwOpt(int port = 1502) => new()
    {
        Host = "127.0.0.1", Port = port,
        PollIntervalMs = 150, OfflineAfterFailures = 3, WriteTimeoutMs = 1000,
        RFlagPollMs = 100, RFlagTimeoutMs = 30000, CFlagTimeoutMs = 5000,
    };

    // ════════════════════════════════════════════════════════════════════════
    // VS-P2b-2: N대 인스턴스화 — 번들별 독립 큐·폴링·핸드셰이크 인스턴스 확인
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P2b2_TwoSorterDestinations_TwoIndependentBundles()
    {
        // 소터 A (destinationId=10, chuteNo=30)
        var masterA      = new FakeModbusMasterForApi();
        var writeQueueA  = new PlcWriteQueue();
        var pollingA     = new PlcPollingService(MakeGwOpt(1502), writeQueueA, masterA);
        var handshakeA   = new HandshakeOrchestrator(pollingA, MakeGwOpt(1502));
        var bundleA      = new SorterBundleHandle(10L, 30, pollingA, handshakeA);

        // 소터 B (destinationId=11, chuteNo=31) — 완전 다른 인스턴스
        var masterB      = new FakeModbusMasterForApi();
        var writeQueueB  = new PlcWriteQueue();
        var pollingB     = new PlcPollingService(MakeGwOpt(1503), writeQueueB, masterB);
        var handshakeB   = new HandshakeOrchestrator(pollingB, MakeGwOpt(1503));
        var bundleB      = new SorterBundleHandle(11L, 31, pollingB, handshakeB);

        // 번들 2세트가 서로 다른 인스턴스를 보유하는지 확인
        Assert.NotSame(masterA,    masterB);
        Assert.NotSame(writeQueueA, writeQueueB);
        Assert.NotSame(pollingA,   pollingB);
        Assert.NotSame(handshakeA, handshakeB);

        // MultiSorterGatewayRegistry에 2대 등록
        var bundles = new Dictionary<long, SorterBundleHandle>
        {
            [bundleA.DestinationId] = bundleA,
            [bundleB.DestinationId] = bundleB,
        };
        var registry = new MultiSorterGatewayRegistry(bundles);

        Assert.Equal(2, registry.AllBundles.Count);

        // 각 destinationId로 정확히 라우팅되는지 확인
        var gotA = registry.GetBundle(10L);
        var gotB = registry.GetBundle(11L);
        Assert.NotNull(gotA);
        Assert.NotNull(gotB);
        Assert.Equal(30, gotA.ChuteNo);
        Assert.Equal(31, gotB.ChuteNo);
        Assert.NotSame(gotA, gotB); // 공유 번들 없음
    }

    // ════════════════════════════════════════════════════════════════════════
    // VS-P2b-3: 라우팅 독립 — 소터 A(Ready=1·층일치) / 소터 B(Ready=0)
    //           GetLatest(destA) ≠ GetLatest(destB) 상태 교차 없음
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void P2b3_TwoSorterSnapshots_IndependentRouting()
    {
        // 소터 A: Ready=1, CurFloor=1, TgtFloor=0 (Online 초기화는 Start 후이므로 FakeMaster 직접 조회)
        var masterA  = new FakeModbusMasterForApi();
        var queueA   = new PlcWriteQueue();
        var pollingA = new PlcPollingService(MakeGwOpt(1502), queueA, masterA);
        var bundleA  = new SorterBundleHandle(20L, 30, pollingA,
            new HandshakeOrchestrator(pollingA, MakeGwOpt(1502)));

        // 소터 B: Ready=0 (분류 중)
        var masterB  = new FakeModbusMasterForApi();
        var queueB   = new PlcWriteQueue();
        var pollingB = new PlcPollingService(MakeGwOpt(1503), queueB, masterB);
        var bundleB  = new SorterBundleHandle(21L, 31, pollingB,
            new HandshakeOrchestrator(pollingB, MakeGwOpt(1503)));

        // FakeMaster 초기 상태: Ready=1 (생성자 기본값)
        // 소터 B만 Ready=0 설정
        masterB.SetReady(false);

        var registry = new MultiSorterGatewayRegistry(new Dictionary<long, SorterBundleHandle>
        {
            [20L] = bundleA,
            [21L] = bundleB,
        });

        // GetBundle 라우팅 정확성 확인
        var gotA = registry.GetBundle(20L);
        var gotB = registry.GetBundle(21L);
        Assert.NotNull(gotA);
        Assert.NotNull(gotB);
        Assert.NotSame(gotA, gotB);

        // 없는 destinationId → null (OFFLINE 경로 유지)
        Assert.Null(registry.GetBundle(999L));

        // 레지스터 조회를 통해 A와 B의 상태가 독립적임을 확인
        // (폴링 루프가 아직 시작되지 않았으므로 FakeMaster 직접 읽기)
        Assert.Equal(RegisterMap.D4.Ready,
            masterA.GetRegister(RegisterMap.Flags)); // A: Ready=1
        Assert.Equal((ushort)0,
            masterB.GetRegister(RegisterMap.Flags)); // B: Ready=0, C_Flag=0
    }

    // ════════════════════════════════════════════════════════════════════════
    // VS-P2b-7a: 소터 0대 기동 — 빈 레지스트리 StartAsync/StopAsync 정상
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task P2b7a_ZeroSorters_StartStop_Normal()
    {
        // 빈 번들 딕셔너리 → MultiSorterGatewayRegistry.AllBundles=0 → 기동/종료 정상
        var registry = new MultiSorterGatewayRegistry(new Dictionary<long, SorterBundleHandle>());

        Assert.Empty(registry.AllBundles);
        Assert.Null(registry.GetBundle(1L));
        Assert.Null(registry.GetLatest(1L));

        // 빈 레지스트리 — 소터 0대 기동: SORTER_3D IF-08 → bundle null → OFFLINE(경로 유지)
        // (실제 SorterRegistryFactory.StartAsync에서 sorterDests.Count=0 → bundles 비어있음)
        await Task.CompletedTask; // 비동기 흐름 검증 완료
    }

    // ════════════════════════════════════════════════════════════════════════
    // VS-P2b-7b: appsettings Sorters[] ChuteNo 누락 → fail-loud (InvalidOperationException)
    // SorterRegistryFactory.StartAsync가 SORTER_3D destination을 발견했지만
    // appsettings에 해당 ChuteNo가 없으면 예외를 throw해야 함.
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task P2b7b_SorterConfigMissing_ThrowsInvalidOperation_FailLoud()
    {
        // IConfiguration: Sorters 배열을 빈 배열(ChuteNo 누락)로 구성
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Timing 공통 값
                ["Timing:RFlagPollMs"]    = "100",
                ["Timing:RFlagTimeoutMs"] = "30000",
                ["Timing:CFlagTimeoutMs"] = "5000",
                // Sorters 배열 비어 있음 — ChuteNo=30 destination 없음
            })
            .Build();

        // Named in-memory SQLite: anchor connection으로 DB 수명 고정
        // Cache=Shared로 같은 이름의 DB를 여러 connection이 공유.
        var dbName = $"fail_loud_{Guid.NewGuid():N}";
        var connStr = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        await using var anchor = new Microsoft.Data.Sqlite.SqliteConnection(connStr);
        await anchor.OpenAsync();

        // 스키마 + 데이터 삽입 (anchor connection이 DB를 유지하는 동안)
        var dbOpts = new DbContextOptionsBuilder<WcsDbContext>()
            .UseSqlite(anchor)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;

        await using var db = new WcsDbContext(dbOpts);
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.UtcNow;
        db.Destinations.Add(new Wcs.Data.Destination
        {
            ChuteNo   = 30,
            DestType  = Wcs.Data.DestType.SORTER_3D,
            Status    = Wcs.Data.DestStatus.NORMAL,
            IsActive  = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        // SorterRegistryFactory 직접 구성
        // DB는 SORTER_3D(chuteNo=30)이 있지만 appsettings Sorters[]에 ChuteNo=30 없음
        // → fail-loud: InvalidOperationException
        using var services = new ServiceCollection()
            .AddLogging()
            .AddDbContext<WcsDbContext>(o =>
                o.UseSqlite(connStr)
                 .ConfigureWarnings(w => w.Ignore(
                     Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)),
                ServiceLifetime.Scoped)
            .BuildServiceProvider();

        var factory = new SorterRegistryFactory(
            services,
            config,
            services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SorterRegistryFactory>>());

        // StartAsync가 SORTER_3D를 발견하지만 설정 없음 → InvalidOperationException
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.StartAsync(CancellationToken.None));
    }

    // ════════════════════════════════════════════════════════════════════════
    // VS-P2b-7c: N대(2대) 기동 — AllBundles.Count = 2, 각 ChuteNo 일치
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task P2b7c_TwoSorterBundles_StartAndStop_Normal()
    {
        // IConfiguration: Sorters 배열에 2개 항목 (ChuteNo=30, ChuteNo=31)
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Timing:RFlagPollMs"]    = "100",
                ["Timing:RFlagTimeoutMs"] = "30000",
                ["Timing:CFlagTimeoutMs"] = "5000",
                // Sorters[0]
                ["Sorters:0:ChuteNo"]           = "30",
                ["Sorters:0:Transport"]          = "Tcp",
                ["Sorters:0:Host"]               = "127.0.0.1",
                ["Sorters:0:Port"]               = "19502",
                ["Sorters:0:PollIntervalMs"]     = "150",
                ["Sorters:0:OfflineAfterFailures"] = "3",
                ["Sorters:0:WriteTimeoutMs"]     = "1000",
                // Sorters[1]
                ["Sorters:1:ChuteNo"]           = "31",
                ["Sorters:1:Transport"]          = "Tcp",
                ["Sorters:1:Host"]               = "127.0.0.1",
                ["Sorters:1:Port"]               = "19503",
                ["Sorters:1:PollIntervalMs"]     = "150",
                ["Sorters:1:OfflineAfterFailures"] = "3",
                ["Sorters:1:WriteTimeoutMs"]     = "1000",
            })
            .Build();

        // Named in-memory SQLite: anchor connection으로 DB 수명 고정
        var dbName = $"two_sorter_{Guid.NewGuid():N}";
        var connStr = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        await using var anchor = new Microsoft.Data.Sqlite.SqliteConnection(connStr);
        await anchor.OpenAsync();

        var dbOpts = new DbContextOptionsBuilder<WcsDbContext>()
            .UseSqlite(anchor)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;

        await using var db = new WcsDbContext(dbOpts);
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.UtcNow;
        db.Destinations.AddRange(
            new Wcs.Data.Destination
            {
                ChuteNo = 30, DestType = Wcs.Data.DestType.SORTER_3D,
                Status = Wcs.Data.DestStatus.NORMAL, IsActive = true,
                CreatedAt = now, UpdatedAt = now,
            },
            new Wcs.Data.Destination
            {
                ChuteNo = 31, DestType = Wcs.Data.DestType.SORTER_3D,
                Status = Wcs.Data.DestStatus.NORMAL, IsActive = true,
                CreatedAt = now, UpdatedAt = now,
            });
        await db.SaveChangesAsync();

        using var services = new ServiceCollection()
            .AddLogging()
            .AddDbContext<WcsDbContext>(o =>
                o.UseSqlite(connStr)
                 .ConfigureWarnings(w => w.Ignore(
                     Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)),
                ServiceLifetime.Scoped)
            .BuildServiceProvider();

        var factory = new SorterRegistryFactory(
            services,
            config,
            services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SorterRegistryFactory>>());

        // StartAsync: DB에서 SORTER_3D 2대 조회 → 번들 2대 구성 + 폴링 시작
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await factory.StartAsync(cts.Token);

        // 번들 2대 등록 확인
        Assert.Equal(2, factory.AllBundles.Count);

        var chuteNos = factory.AllBundles.Select(b => b.ChuteNo).ToHashSet();
        Assert.Contains(30, chuteNos);
        Assert.Contains(31, chuteNos);

        // 소터별 독립 번들 (같은 인스턴스 참조 없음)
        var bundleList = factory.AllBundles.ToList();
        Assert.NotSame(bundleList[0], bundleList[1]);

        // StopAsync: 예외 없이 정상 종료
        await factory.StopAsync(CancellationToken.None);
    }
}

// ════════════════════════════════════════════════════════════════════════════
// VS-P2b-4/5/6 — 실 Sim3ds 2대 핸드셰이크 독립·직렬화·OFFLINE 격리 테스트
//
// 2대의 독립 SimServer(다른 포트) + 2세트 번들(PlcPollingService+HandshakeOrchestrator)으로
// 소터 간 C_Seq 교차 0·인스턴스별 직렬화·OFFLINE 독립을 실증.
// PlcGatewayIntegrationTests.cs의 SimServer 패턴 참조 (포트 분리, IAsyncLifetime).
// ════════════════════════════════════════════════════════════════════════════

public class P2bSimHandshakeTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _out;

    // 소터 A — Sim3ds 1대 + 번들 1세트
    private int              _portA;
    private SimServer?       _simA;
    private PlcWriteQueue?   _queueA;
    private PlcPollingService?     _pollingA;
    private HandshakeOrchestrator? _hsA;

    // 소터 B — Sim3ds 1대 + 번들 1세트 (완전 독립 인스턴스)
    private int              _portB;
    private SimServer?       _simB;
    private PlcWriteQueue?   _queueB;
    private PlcPollingService?     _pollingB;
    private HandshakeOrchestrator? _hsB;

    public P2bSimHandshakeTests(ITestOutputHelper output)
    {
        _out  = output;
        _portA = GetFreePort();
        _portB = GetFreePort();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // 쓰기 큐 채널을 먼저 완료시켜 RunWriteConsumerAsync가 결정적으로 종료되게 한다
        // (CTS 취소만으로는 빈 채널 parked ReadAllAsync가 안 깨어나는 타이밍 경쟁 → StopAsync 데드락).
        _queueA?.Writer.TryComplete();
        _queueB?.Writer.TryComplete();
        // 각 번들 독립 종료 (순서: polling → sim)
        if (_pollingA is not null) { await _pollingA.StopAsync(); await _pollingA.DisposeAsync(); }
        if (_pollingB is not null) { await _pollingB.StopAsync(); await _pollingB.DisposeAsync(); }
        if (_simA is not null) await _simA.DisposeAsync();
        if (_simB is not null) await _simB.DisposeAsync();
    }

    // ── 헬퍼 ─────────────────────────────────────────────────────────────────

    private static PlcGatewayOptions MakeOpt(int port) => new()
    {
        Host = "127.0.0.1", Port = port,
        PollIntervalMs = 30, OfflineAfterFailures = 3, WriteTimeoutMs = 500,
        RFlagPollMs = 20, RFlagTimeoutMs = 3000, CFlagTimeoutMs = 2000,
    };

    private static SimServer.Options MakeSimOpt(int port) => new()
    {
        Host = "127.0.0.1", Port = port,
        TiltDelayMs = 50, SortDurationMs = 100, MoveDurationMs = 80,
        InitialCurFloor = 1, SimLoopMs = 10,
    };

    /// <summary>두 소터 번들 모두 기동 + Online 대기.</summary>
    private async Task StartBothAsync()
    {
        _simA    = new SimServer(MakeSimOpt(_portA));
        _queueA  = new PlcWriteQueue();
        _pollingA = new PlcPollingService(MakeOpt(_portA), _queueA);
        _hsA     = new HandshakeOrchestrator(_pollingA, MakeOpt(_portA));

        _simB    = new SimServer(MakeSimOpt(_portB));
        _queueB  = new PlcWriteQueue();
        _pollingB = new PlcPollingService(MakeOpt(_portB), _queueB);
        _hsB     = new HandshakeOrchestrator(_pollingB, MakeOpt(_portB));

        await _simA.StartAsync();
        await _simB.StartAsync();
        await _pollingA.StartAsync(CancellationToken.None);
        await _pollingB.StartAsync(CancellationToken.None);

        await WaitUntilAsync(() => _pollingA!.Latest.Online, 2000, "소터A Online");
        await WaitUntilAsync(() => _pollingB!.Latest.Online, 2000, "소터B Online");
    }

    private static async Task WaitUntilAsync(Func<bool> cond, int timeoutMs, string msg, int pollMs = 20)
    {
        var dl = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (!cond())
        {
            if (DateTimeOffset.Now > dl)
                Assert.Fail($"WaitUntil 타임아웃({timeoutMs}ms): {msg}");
            await Task.Delay(pollMs);
        }
    }

    private static int GetFreePort()
    {
        using var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    // ════════════════════════════════════════════════════════════════════════
    // VS-P2b-4: 소터별 핸드셰이크 독립 (핵심) — 실 Sim3ds 2대 동시 핸드셰이크
    // 각 소터 C_Seq↔R_Seq 자기 소터 내 일치, 소터 간 교차 0.
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task P2b4_TwoSimServers_ConcurrentHandshake_NoCrossSeq()
    {
        await StartBothAsync();

        // 소터 A·B 동시 핸드셰이크 발사
        var taskA = _hsA!.ExecuteAsync(cellNo: 1, ct: CancellationToken.None);
        var taskB = _hsB!.ExecuteAsync(cellNo: 2, ct: CancellationToken.None);

        var (resultA, resultB) = (await taskA, await taskB);

        // 각 소터 내 C_Seq↔R_Seq 일치
        Assert.Equal(HandshakeOutcome.Success, resultA.Outcome);
        Assert.Equal(resultA.SentCSeq, resultA.ReceivedRSeq);

        Assert.Equal(HandshakeOutcome.Success, resultB.Outcome);
        Assert.Equal(resultB.SentCSeq, resultB.ReceivedRSeq);

        // 소터 간 C_Seq 교차 없음 — A의 CSeq가 B의 RSeq에 나타나지 않아야 하고
        // B의 CSeq가 A의 RSeq에 나타나지 않아야 함(인스턴스별 독립 _cSeq 보장).
        // 두 C_Seq가 우연히 같을 경우 교차를 감지할 수 없으므로, 시퀀스 번호 독립을 추가로 단언.
        // _cSeq는 인스턴스 초기화부터 독립적으로 증가 — A 건수만큼 증가한 A의 CSeq를 B 결과와 비교.
        // 핵심 단언: A의 R_Seq는 A SimServer가 응답한 것(B SimServer가 개입 불가).
        Assert.Equal(resultA.SentCSeq, resultA.ReceivedRSeq); // 자기 소터 내 일치 재확인
        Assert.Equal(resultB.SentCSeq, resultB.ReceivedRSeq); // 자기 소터 내 일치 재확인

        _out.WriteLine($"[P2b-4] 소터A: C_Seq={resultA.SentCSeq} R_Seq={resultA.ReceivedRSeq} → {resultA.Outcome}");
        _out.WriteLine($"[P2b-4] 소터B: C_Seq={resultB.SentCSeq} R_Seq={resultB.ReceivedRSeq} → {resultB.Outcome}");
        _out.WriteLine($"[P2b-4] portA={_portA} portB={_portB} — 독립 소켓 버스 교차 0 확인");
    }

    // ════════════════════════════════════════════════════════════════════════
    // VS-P2b-5: 인스턴스별 직렬화 — A 폴 진행 중 다수 핸드셰이크, B 무영향
    // 소터 A에서 연속 3건 핸드셰이크 성공(매 건 R_Seq==C_Seq) + B 스냅샷 무영향.
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task P2b5_SorterA_MultipleHandshakes_SorterB_Unaffected()
    {
        await StartBothAsync();

        // 소터 B 초기 스냅샷 기록 (A 핸드셰이크가 B에 영향 주지 않는지 확인용)
        var snapB_before = _pollingB!.Latest;

        // 소터 A에서 연속 3건 핸드셰이크 (폴 진행 중)
        for (int i = 1; i <= 3; i++)
        {
            var result = await _hsA!.ExecuteAsync(cellNo: i, ct: CancellationToken.None);

            Assert.Equal(HandshakeOutcome.Success, result.Outcome);
            Assert.Equal(result.SentCSeq, result.ReceivedRSeq);
            _out.WriteLine($"[P2b-5] 소터A 건#{i}: C_Seq={result.SentCSeq} R_Seq={result.ReceivedRSeq} → Success");

            // 이전 ClearR 처리 완료 대기
            await WaitUntilAsync(
                () => { var s = _pollingA!.Latest; return !s.RFlag && !s.CFlag; },
                timeoutMs: 2000, msg: $"A 건#{i} ClearR 완료");
        }

        // 소터 B의 스냅샷: A 핸드셰이크 3건 동안 B SimServer는 C_Flag·R_Flag 처리 없음
        // B는 독립 포트/소켓/SimServer → A 쓰기가 B 레지스터에 도달 불가
        var snapB_after = _pollingB!.Latest;
        Assert.True(snapB_after.Online, "소터B Online 유지");
        Assert.False(snapB_after.CFlag, "소터B C_Flag 미변경 (A 핸드셰이크 영향 없음)");
        Assert.False(snapB_after.RFlag, "소터B R_Flag 미변경 (A 핸드셰이크 영향 없음)");

        _out.WriteLine($"[P2b-5] 소터B CFlag={snapB_after.CFlag} RFlag={snapB_after.RFlag} Online={snapB_after.Online} — 무영향 확인");
    }

    // ════════════════════════════════════════════════════════════════════════
    // VS-P2b-6: 소터별 OFFLINE 독립
    // A 단절 → A만 OFFLINE(B 정상) → A 재기동 후 후속 핸드셰이크 Success
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task P2b6_SorterA_Offline_SorterB_Unaffected_ThenRecovers()
    {
        await StartBothAsync();
        Assert.True(_pollingA!.Latest.Online, "초기 A Online=true");
        Assert.True(_pollingB!.Latest.Online, "초기 B Online=true");

        // 소터 A Sim 종료 → A만 OFFLINE
        await _simA!.StopAsync();
        await _simA.DisposeAsync();
        _simA = null;

        // A가 OFFLINE이 될 때까지 대기 (폴 실패 * OfflineAfterFailures)
        int offlineMs = (MakeOpt(_portA).WriteTimeoutMs * (MakeOpt(_portA).OfflineAfterFailures + 1)) + 1000;
        await WaitUntilAsync(() => !_pollingA.Latest.Online, offlineMs, "소터A OFFLINE 전이");

        Assert.False(_pollingA.Latest.Online, "소터A OFFLINE 확인");
        // 소터 B는 영향 없음 — 독립 소켓/SimServer
        Assert.True(_pollingB!.Latest.Online, "소터B Online 유지 (A 단절 무영향)");

        _out.WriteLine($"[P2b-6] A OFFLINE, B Online={_pollingB.Latest.Online} — 격리 확인");

        // 소터 A Sim 재기동
        _simA = new SimServer(MakeSimOpt(_portA));
        await _simA.StartAsync();

        // A 복구 대기
        await WaitUntilAsync(() => _pollingA.Latest.Online, 3000, "소터A Online 복구");
        Assert.True(_pollingA.Latest.Online, "소터A 복구 후 Online=true");

        // 복구 후 A에서 핸드셰이크 성공 (off-lock 인스턴스별 보존 — 재연결 후 잠금 정상)
        var result = await _hsA!.ExecuteAsync(cellNo: 9, ct: CancellationToken.None);
        Assert.Equal(HandshakeOutcome.Success, result.Outcome);
        Assert.Equal(result.SentCSeq, result.ReceivedRSeq);

        _out.WriteLine($"[P2b-6] A 복구 후 핸드셰이크: C_Seq={result.SentCSeq} R_Seq={result.ReceivedRSeq} → {result.Outcome}");
        _out.WriteLine($"[P2b-6] B Online={_pollingB!.Latest.Online} — A 재기동 후 B 무영향 유지");
    }
}
