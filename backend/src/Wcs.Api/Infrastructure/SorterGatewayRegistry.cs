using Wcs.PlcGateway;

namespace Wcs.Api;

// ════════════════════════════════════════════════════════════════════════════
// ISorterGatewayRegistry — destination.id 키로 소터 번들 핸들 조회 진입점.
//
// P2b: 멀티소터 — destination.id → 소터별 번들(스냅샷·큐·핸드셰이크).
// ════════════════════════════════════════════════════════════════════════════

// ── 소터 번들 핸들 ──────────────────────────────────────────────────────────

/// <summary>
/// 소터 1대에 대응하는 번들 핸들.
/// 스냅샷 조회·TgtFloor 큐 투입·핸드셰이크 트리거가 전부 이 핸들을 경유.
/// 인스턴스별 독립 IModbusMaster·PlcWriteQueue·HandshakeOrchestrator를 보유.
/// </summary>
public sealed class SorterBundleHandle
{
    // ── 구성 ─────────────────────────────────────────────────────────────────
    /// <summary>destination.id (DB 키). 불변.</summary>
    public long DestinationId { get; init; }

    /// <summary>destination.chute_no (소터 슬롯 번호). 불변.</summary>
    public int ChuteNo { get; init; }

    // ── 번들 컴포넌트 (각 소터별 독립 인스턴스) ──────────────────────────────
    private readonly PlcPollingService   _polling;
    private readonly HandshakeOrchestrator _handshake;

    // 이 소터 전용 쓰기 큐(절대규칙 #1 소터별 단일 큐). 종료 시 채널을 완료시켜
    // PlcPollingService의 쓰기 컨슈머 루프(RunWriteConsumerAsync)가 결정적으로 끝나게 한다
    // (취소 토큰만으로는 빈 채널에 parked된 ReadAllAsync가 깨어나지 않는 타이밍 경쟁이 있어
    //  StopAsync가 쓰기 태스크를 영원히 await → 호스트 종료 데드락).
    private readonly PlcWriteQueue?      _writeQueue;

    public SorterBundleHandle(
        long                   destinationId,
        int                    chuteNo,
        PlcPollingService      polling,
        HandshakeOrchestrator  handshake,
        PlcWriteQueue?         writeQueue = null)
    {
        DestinationId = destinationId;
        ChuteNo       = chuteNo;
        _polling      = polling;
        _handshake    = handshake;
        _writeQueue   = writeQueue;
    }

    // ── API 계층이 사용하는 세 가지 조작 ─────────────────────────────────────

    /// <summary>이 소터의 최신 PLC 스냅샷(논블로킹).</summary>
    public Wcs.Core.PlcSnapshot Latest => _polling.Latest;

    /// <summary>
    /// 콜드스타트 레지스터 클리어(StartupClear)가 이 소터 쓰기 큐 컨슈머에서 처리 완료되면 완료되는 Task(C2 S3).
    /// IF-08 부트스트랩 push(DestinationStatusPusher)가 이 Task를 대기해 "클리어 before push" 순서를 보장한다.
    /// </summary>
    public Task StartupClearCompleted => _polling.StartupClearCompleted;

    /// <summary>TgtFloor 설정을 소터별 쓰기 큐에 투입(번들 전용 큐 — 절대규칙 #1 소터별 보존).</summary>
    public ValueTask EnqueueSetTgtFloorAsync(int floor, CancellationToken ct = default) =>
        _polling.EnqueueAsync(new PlcWrite.SetTgtFloor(floor), ct);

    // ── S-F3a: Ops 진단용 워드 쓰기 enqueue 래퍼 2종(EnqueueSetTgtFloorAsync와 동형) ─────
    // 절대규칙 #1: 컨트롤러/서비스가 Modbus를 직접 호출하지 않고, 이 번들 전용 단일 큐로만 enqueue한다.
    // PlcWrite.ClearR·CellAssign 레코드와 컨슈머(ProcessWriteAsync) case는 Wcs.PlcGateway에 이미 존재 —
    // 여기(Wcs.Api)는 그 기존 큐 경로를 노출하는 박막 래퍼일 뿐이다(PlcGateway 코어 무변경).

    /// <summary>R 영역 강제 클리어(진단)를 소터별 쓰기 큐에 투입 → 컨슈머가 D2·D3=0 + R_Flag clear(RMW).</summary>
    public ValueTask EnqueueClearRAsync(CancellationToken ct = default) =>
        _polling.EnqueueAsync(new PlcWrite.ClearR(), ct);

    /// <summary>셀 지정(진단·고위험)을 소터별 쓰기 큐에 투입 → 컨슈머가 C_Flag==0 확인 후 D0·D1 + C_Flag set(RMW).</summary>
    public ValueTask EnqueueCellAssignAsync(int cellNo, int seq, CancellationToken ct = default) =>
        _polling.EnqueueAsync(new PlcWrite.CellAssign(cellNo, seq), ct);

    /// <summary>
    /// 셀 지정 핸드셰이크 1건 수행(백그라운드 태스크로 호출).
    /// S-IF10-CWRITE-SETTLE-DELAY — <paramref name="depositedAtUtc"/>(IF-10 수신 시각·anchor)를 선택적으로
    /// 받아 오케스트레이터에 위임한다(역호환 — 기존 호출부는 지정 안 함 → null). 번들 코어 로직 무변경(박막 위임).
    /// </summary>
    public Task<HandshakeResult> ExecuteHandshakeAsync(
        int cellNo, CancellationToken ct = default, DateTime? depositedAtUtc = null) =>
        _handshake.ExecuteAsync(cellNo, ct, depositedAtUtc);

    // ── IHostedService 위임 ──────────────────────────────────────────────────

    /// <summary>소터 폴링 서비스 시작 (PlcPollingHostedAdapter에서 호출).</summary>
    public Task StartPollingAsync(CancellationToken ct) => _polling.StartAsync(ct);

    /// <summary>
    /// 소터 폴링 서비스 종료 (독립 소터/PlcPollingHostedAdapter에서 호출).
    /// ⚠ 공유 버스(ModbusBus) 멤버 번들에는 이것을 호출하면 안 된다: writeQueue=null이라 아래 TryComplete는
    ///   무동작이지만, 이어지는 _polling.StopAsync() → _master.Disconnect()(BusSlaveMaster → 공유 연결 Disconnect)가
    ///   형제 슬레이브의 연결까지 끊는다. 공유 버스 teardown은 버스 단위(ModbusBus.StopAsync) 1회로만 한다.
    /// </summary>
    public Task StopPollingAsync()
    {
        // 쓰기 큐 채널을 먼저 완료 → 쓰기 컨슈머가 결정적으로 종료(빈 채널 취소 경쟁 회피).
        _writeQueue?.Writer.TryComplete();
        return _polling.StopAsync();
    }

    // ── P3: OFFLINE 전이 이벤트 구독 지원 ────────────────────────────────────
    // API 계층이 OFFLINE 전이당 1건 alarm을 기록하기 위해 구독.
    // PlcPollingService.OnOfflineTransition(전이당 1회 발화)을 그대로 노출.

    /// <summary>
    /// 이 소터 폴링의 OFFLINE 전이 이벤트에 핸들러 등록.
    /// PlcPollingService.OnOfflineTransition — Online true→false 시 1회만 발화.
    /// </summary>
    public void SubscribeOffline(Action<Wcs.Core.PlcSnapshot> handler)
        => _polling.OnOfflineTransition += handler;

    // ── S-OBSERVABILITY: 관측 훅 구독 노출(부수 기록 전용 — 게이트웨이 의미 0 변경) ──

    /// <summary>ONLINE 복구 전이(false→true) 구독 — STATE/ONLINE 로그용.</summary>
    public void SubscribeOnline(Action<Wcs.Core.PlcSnapshot> handler)
        => _polling.OnOnlineTransition += handler;

    /// <summary>폴링 레지스터 전이(변화분) 구독 — (reg, old, new). POLL_CHANGE 로그용.</summary>
    public void SubscribeRegisterChange(Action<string, int, int> handler)
        => _polling.OnRegisterChange += handler;

    /// <summary>PLC 쓰기 완료 구독 — (action, detailJson). PLC_WRITE 로그용.</summary>
    public void SubscribeWrite(Action<string, string> handler)
        => _polling.OnWrite += handler;

    /// <summary>핸드셰이크 단계 구독 — (action, detailJson). HANDSHAKE 로그용.</summary>
    public void SubscribeHandshakeStage(Action<string, string> handler)
        => _handshake.OnStage += handler;
}

// ── ISorterGatewayRegistry ───────────────────────────────────────────────────

/// <summary>
/// destination.id 키로 소터 번들 핸들을 조회하는 단일 진입점.
/// P2b: N대 멀티소터 라우팅 — destination.id → SorterBundleHandle.
/// </summary>
public interface ISorterGatewayRegistry
{
    /// <summary>
    /// destination.id에 해당하는 소터의 최신 PLC 스냅샷 반환.
    /// destination.id가 등록되지 않은 소터면 null(OFFLINE 경로 유지).
    /// </summary>
    Wcs.Core.PlcSnapshot? GetLatest(long destinationId);

    /// <summary>
    /// destination.id에 해당하는 소터 번들 핸들 반환.
    /// 미등록 destination.id는 null.
    /// </summary>
    SorterBundleHandle? GetBundle(long destinationId);

    /// <summary>등록된 전체 소터 번들 열거 (IHostedService Start/Stop 위임용).</summary>
    IReadOnlyCollection<SorterBundleHandle> AllBundles { get; }
}

// ── MultiSorterGatewayRegistry ───────────────────────────────────────────────

/// <summary>
/// P2b 멀티소터 구현.
/// 기동 시 DB에서 dest_type=SORTER_3D 조회 → ChuteNo로 appsettings 소터 배열 매칭 →
/// 소터별 번들(IModbusMaster + PlcWriteQueue + PlcPollingService + HandshakeOrchestrator) N대 구성.
/// SORTER_3D인데 appsettings에 ChuteNo 누락 시 fail-loud(기동 에러).
/// </summary>
public sealed class MultiSorterGatewayRegistry : ISorterGatewayRegistry
{
    // destination.id → 번들 핸들 (불변 딕셔너리)
    private readonly IReadOnlyDictionary<long, SorterBundleHandle> _bundles;

    public MultiSorterGatewayRegistry(IReadOnlyDictionary<long, SorterBundleHandle> bundles)
    {
        _bundles = bundles;
    }

    /// <inheritdoc/>
    public Wcs.Core.PlcSnapshot? GetLatest(long destinationId) =>
        _bundles.TryGetValue(destinationId, out var h) ? h.Latest : null;

    /// <inheritdoc/>
    public SorterBundleHandle? GetBundle(long destinationId) =>
        _bundles.TryGetValue(destinationId, out var h) ? h : null;

    /// <inheritdoc/>
    public IReadOnlyCollection<SorterBundleHandle> AllBundles =>
        (IReadOnlyCollection<SorterBundleHandle>)_bundles.Values;
}
