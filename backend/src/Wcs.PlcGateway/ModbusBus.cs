using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Wcs.PlcGateway;

// ════════════════════════════════════════════════════════════════════════════
// ModbusBus — 한 물리 버스(공유 마스터·락·폴 사이클·쓰기 큐) 조정자 (S-MULTISORTER-SHARED-BUS 모듈 B)
//
//   한 물리 버스(같은 host:port / 같은 PortName)에 여러 3D 소터(슬레이브)를 unitId로 구분해 공유
//   운영한다. 하드닝된 PlcPollingService를 in-place로 재작성하지 않고, 그 per-slave 상태·핸드셰이크 표면을
//   그대로 재사용하되(멤버 슬레이브) 아래 버스 스코프 메커니즘만 신설한다(계약 "버스 스코프 조정자"):
//     · B1 공유 마스터 1개(ISharedModbusConnection) — 버스당 클라이언트+포트 1 Open.
//     · B3 버스 락 1개(_busLock) — 폴 read·write·D4 RMW·재연결이 버스 단위 단일 임계구역(프레임 무교차).
//     · B4 버스 폴 사이클 — 주기당 1회 대기 후 멤버 슬레이브를 1회씩 순회(N×트랜잭션+대기1회).
//     · B6 버스 단위 공유 쓰기 큐 1개 + 단일 컨슈머 — 각 쓰기가 대상 unitId를 실어 라우팅.
//   B5 슬레이브별 독립 상태(Latest·Online·연속실패·OFFLINE 전이·arming·C_Seq)는 각 멤버
//   PlcPollingService/HandshakeOrchestrator가 그대로 보유한다.
//
//   단일 슬레이브 버스(멤버 1개) == 현행 단독 동작과 동치(회귀 0) — 서로 다른 버스(엔드포인트/포트)는
//   각자 독립 ModbusBus로 병렬(멀티 포트 보존).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>버스 공유 쓰기 큐 항목 — 대상 슬레이브(unitId) + 쓰기 작업(B6).</summary>
internal readonly record struct BusWrite(byte UnitId, PlcWrite Write);

/// <summary>
/// 공유 버스 조정자. <see cref="AddSlave(byte, ILogger{PlcPollingService})"/>로 멤버 슬레이브를 등록하고
/// <see cref="StartAsync"/>로 단일 폴 루프 + 단일 쓰기 컨슈머를 구동한다.
/// </summary>
public sealed class ModbusBus : IAsyncDisposable
{
    private readonly ISharedModbusConnection _conn;
    private readonly PlcGatewayOptions       _opt;
    private readonly ILogger                 _log;

    // B3 — 버스 단위 공유 락(멤버 PlcPollingService가 ConfigureForBus로 이 락을 공유).
    private readonly SemaphoreSlim _busLock = new(1, 1);

    private readonly List<PlcPollingService>        _members = new();
    private readonly Dictionary<byte, PlcPollingService> _byUnit = new();

    // B6 — 버스 단위 단일 쓰기 큐(단일 컨슈머). 여러 슬레이브/핸드셰이크가 동시 enqueue(SingleWriter=false).
    private readonly Channel<BusWrite> _writeCh = Channel.CreateUnbounded<BusWrite>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private Task? _writeTask;
    private int   _stopped;
    private bool  _started;

    public ModbusBus(ISharedModbusConnection conn, PlcGatewayOptions opt, ILogger? log = null)
    {
        _conn = conn;
        _opt  = opt;
        _log  = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>버스 식별(로그·진단).</summary>
    public string BusKey => _conn.BusKey;

    /// <summary>등록된 멤버 슬레이브(등록 순서).</summary>
    public IReadOnlyList<PlcPollingService> Slaves => _members;

    /// <summary>지정 unitId의 멤버 슬레이브 게이트웨이.</summary>
    public PlcPollingService Slave(byte unitId) => _byUnit[unitId];

    // ── 멤버 등록 (StartAsync 이전) ──────────────────────────────────────────

    /// <summary>
    /// 이 버스에 unitId 슬레이브를 추가한다(버스 공통 <see cref="PlcGatewayOptions"/> 사용 — 기존 시그니처).
    /// per-slave 어댑터(BusSlaveMaster)로 공유 연결에 결선한 PlcPollingService를 생성해 반환한다.
    /// </summary>
    public PlcPollingService AddSlave(byte unitId, ILogger<PlcPollingService>? log = null)
        => AddSlave(unitId, _opt, log);

    /// <summary>
    /// per-slave <see cref="PlcGatewayOptions"/>를 받는 오버로드
    /// (S-MULTISORTER-SHARED-BUS Phase 2 — OQ9(ii) 멤버별 핸드셰이크 Timing 오버라이드 보존).
    ///
    /// 폴 cadence(<see cref="RunBusPollLoopAsync"/>의 <c>_opt.PollIntervalMs</c>)와 공유 클라이언트의 연결
    /// 타임아웃(Read/WriteTimeoutMs)은 본질적으로 버스 단위 1개다 — 여전히 버스 공통 <c>_opt</c>/공유 연결이
    /// 실효이고, 같은 버스 멤버의 PollIntervalMs·Read/WriteTimeoutMs는 레지스트리가 일치 강제(fail-loud).
    /// <b>진짜 per-member로 실효하는 값</b>(멤버 PlcPollingService/HandshakeOrchestrator 인스턴스별) —
    /// 핸드셰이크 Timing(RFlagTimeoutMs/CFlagTimeoutMs/RFlagClearConfirmTimeoutMs)·OfflineAfterFailures —
    /// 만 이 <paramref name="memberOpt"/>로 per-slave 오버라이드를 보존한다.
    /// (memberOpt.WriteTimeoutMs는 버스 멤버에선 불활성: 편의 ModbusTcpMaster ctor 경로를 우회하고
    ///  실제 write 타임아웃은 공유 연결이 소유한다. 그래서 버스 단위 일치 검사 대상이다.)
    ///
    /// Phase 1 하위호환: 기존 <see cref="AddSlave(byte, ILogger{PlcPollingService})"/>는 그대로 동작
    /// (버스 공통 <c>_opt</c>로 위임).
    /// </summary>
    public PlcPollingService AddSlave(byte unitId, PlcGatewayOptions memberOpt, ILogger<PlcPollingService>? log = null)
    {
        if (_started)
            throw new InvalidOperationException("ModbusBus: StartAsync 이후에는 슬레이브를 추가할 수 없습니다.");
        if (_byUnit.ContainsKey(unitId))
            throw new InvalidOperationException($"ModbusBus: unitId={unitId} 슬레이브가 이미 등록됨.");

        var master = new BusSlaveMaster(_conn, unitId);
        // 멤버의 writeQueue는 버스 모드에서 미사용(EnqueueAsync가 버스 큐로 라우팅) — 무해한 자리표시자.
        var svc = new PlcPollingService(memberOpt, new PlcWriteQueue(), master, log);
        svc.ConfigureForBus(this, unitId, _busLock);

        _members.Add(svc);
        _byUnit[unitId] = svc;
        return svc;
    }

    // ── 버스 공유 큐 enqueue (멤버 PlcPollingService.EnqueueAsync가 라우팅) ────

    internal ValueTask EnqueueAsync(byte unitId, PlcWrite write, CancellationToken ct) =>
        _writeCh.Writer.WriteAsync(new BusWrite(unitId, write), ct);

    // ── 시작·종료 ────────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken outerCt = default)
    {
        if (_members.Count == 0)
            throw new InvalidOperationException("ModbusBus: 멤버 슬레이브가 없습니다(AddSlave 먼저 호출).");

        // C4 fix — 단일 멤버 버스(solo)면 멤버에 solo 재연결 의미를 부여(read 타임아웃 HARD·재연결 reopen,
        // 현행 단독 동작 동치). 다중 멤버 버스(≥2)는 false 유지(Phase 1 I1 soft·형제 보호). 멤버 수는 여기서
        // 확정(AddSlave 완료 후·폴 루프 기동 직전). 이후 슬레이브 추가는 _started 가드로 금지되므로 불변.
        bool solo = _members.Count == 1;
        foreach (var m in _members) m.SetSoloBusReconnect(solo);

        _started   = true;
        _cts       = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        _pollTask  = Task.Run(() => RunBusPollLoopAsync(_cts.Token));
        _writeTask = Task.Run(() => RunBusWriteConsumerAsync(_cts.Token));
        return Task.CompletedTask;
    }

    // B4 — 주기당 1회 대기 후 멤버 슬레이브를 1회씩 순회 트랜잭션(슬레이브별 sleep 없음).
    private async Task RunBusPollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_opt.PollIntervalMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            foreach (var member in _members)
            {
                if (ct.IsCancellationRequested) break;
                try { await member.PollCycleAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    // B6 — 단일 컨슈머가 버스 큐에서 꺼내 대상 슬레이브로 라우팅(버스 락 안에서 트랜잭션).
    private async Task RunBusWriteConsumerAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var bw in _writeCh.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (!_byUnit.TryGetValue(bw.UnitId, out var member))
                {
                    _log.LogWarning("[버스 쓰기] 알 수 없는 unitId={Unit} — 드롭", bw.UnitId);
                    continue;
                }
                try { await member.HandleWriteAsync(bw.Write, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { /* teardown */ }
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;

        // 쓰기 큐 채널을 먼저 완료 — parked ReadAllAsync가 결정적으로 깨어나 종료(교훈: testhost-teardown-channel-race).
        _writeCh.Writer.TryComplete();

        if (_cts is not null)
            await _cts.CancelAsync().ConfigureAwait(false);

        foreach (var t in new[] { _pollTask, _writeTask }.Where(t => t is not null))
        {
            try { await t!.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception) { /* teardown 경쟁 예외 흡수 */ }
        }

        // 멤버 정지(루프 없음 — _stopped 플립만). 그 다음에 공유 연결을 끊는다(진행 중 트랜잭션 종료 후).
        foreach (var m in _members)
        {
            try { await m.StopAsync().ConfigureAwait(false); } catch { }
        }
        try { _conn.Disconnect(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        foreach (var m in _members)
        {
            try { await m.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        _conn.Dispose();
        _cts?.Dispose();
        _busLock.Dispose();
    }
}
