namespace Wcs.Data;

// ════════════════════════════════════════════════════════════════════════════
// IOperationLogger — operation_log 기록 진입점(S-OBSERVABILITY).
//
// 본 처리(150ms 폴·핸드셰이크·API 3s)를 블로킹하지 않도록 호출은 **즉시 반환**해야 한다
// (구현은 백그라운드 채널에 enqueue만). 기록 실패가 본 동작을 막지 않는다(fail-safe).
//
// 이 추상화는 Wcs.Data(EF 계층)에 둔다 — OperationLog 엔티티의 자연스러운 동거 위치.
// PlcGateway/HandshakeOrchestrator(EF 비의존 계층)는 이 인터페이스조차 참조하지 않는다.
// 그 계층은 ILogger·콜백 이벤트만 발화하고, Wcs.Api 측 싱크가 IOperationLogger로 DB 기록한다.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 운영 로그 기록 진입점 — 비동기·fail-safe. 호출은 즉시 반환(논블로킹).
/// 구현(<see cref="OperationLog"/> 백그라운드 채널 싱크)이 별도 스코프로 DB에 영속화한다.
/// </summary>
public interface IOperationLogger
{
    /// <summary>
    /// 운영 로그 1건을 기록 큐에 투입(즉시 반환·논블로킹). 큐가 닫혔거나 가득 차도 예외를 던지지 않는다
    /// (fail-safe — 본 동작 비차단). At이 default면 구현이 UtcNow로 채운다.
    /// </summary>
    void Log(OperationLog entry);

    /// <summary>편의 — 카테고리/액션/상세로 1건 기록(스냅샷 식별자 선택).</summary>
    void Log(
        OperationLogCategory category,
        string               action,
        OperationLogLevel    level         = OperationLogLevel.INFO,
        int?                 sorterChuteNo = null,
        long?                destinationId = null,
        string?              barcode       = null,
        int?                 pId           = null,
        string?              detail        = null);
}
