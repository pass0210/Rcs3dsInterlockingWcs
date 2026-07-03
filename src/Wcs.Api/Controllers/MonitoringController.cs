using Microsoft.AspNetCore.Mvc;
using Wcs.Api.Monitoring;
using Wcs.Data;

namespace Wcs.Api.Controllers;

// ════════════════════════════════════════════════════════════════════════════
// MonitoringController — F1 읽기 전용 모니터링 표면 (/api/monitor/*).
//
// RcsController(/api/v1, IF-05/09/10)와 완전 분리된 신규 라우트. 읽기 전용·부수효과 0.
// 조회는 IMonitoringQueries(AsNoTracking)로 위임 — 기존 리포지토리 무변경.
//
// DI 배선 결정(계약 C7 준수): IMonitoringQueries를 Program.cs에 등록하지 않고, 이미 등록된
//   요청 스코프 WcsDbContext + 싱글톤(ISorterGatewayRegistry·IDestinationStatusService)을
//   주입받아 요청당 조립한다. → Program.cs 변경은 정적 서빙 삽입에만 한정(무변경 가드).
//   RcsController도 동일하게 WcsDbContext·레지스트리·상태서비스를 직접 주입받는 패턴.
//
// 페이징(E4·E7): take 상한 clamp(MonitoringQueries.TakeMax=200) + 키셋 커서(Id 내림차순).
//   잘못된 커서(비-정수)는 [ApiController] 모델 바인딩이 자동 400(long? 파싱 실패).
// 미존재 id/destId(E2·E3·E6): 빈 배열 + 200(정책 일관 — 500 아님).
// ════════════════════════════════════════════════════════════════════════════

[ApiController]
[Route("api/monitor")]
public sealed class MonitoringController : ControllerBase
{
    private readonly IMonitoringQueries _queries;

    public MonitoringController(
        WcsDbContext              db,
        ISorterGatewayRegistry    registry,
        IDestinationStatusService status)
    {
        _queries = new MonitoringQueries(db, registry, status);
    }

    // ── E1 GET /api/monitor/batches ────────────────────────────────────────────
    [HttpGet("batches")]
    public IActionResult GetBatches([FromQuery] int? take)
        => Ok(_queries.GetBatches(MonitoringQueries.ClampTake(take)));

    // ── E2 GET /api/monitor/orders?batchId=&status=&take= ───────────────────────
    [HttpGet("orders")]
    public IActionResult GetOrders(
        [FromQuery] long? batchId,
        [FromQuery] string? status,
        [FromQuery] int? take)
        => Ok(_queries.GetOrders(batchId, status, MonitoringQueries.ClampTake(take)));

    // ── E3 GET /api/monitor/orders/{id}/items ───────────────────────────────────
    [HttpGet("orders/{id:long}/items")]
    public IActionResult GetOrderItems([FromRoute] long id)
        => Ok(_queries.GetOrderItems(id));

    // ── E4 GET /api/monitor/pieces/in-flight?take=&cursor= ──────────────────────
    [HttpGet("pieces/in-flight")]
    public IActionResult GetInFlightPieces(
        [FromQuery] int? take,
        [FromQuery] long? cursor)
        => Ok(_queries.GetInFlightPieces(MonitoringQueries.ClampTake(take), cursor));

    // ── E5 GET /api/monitor/sorters ─────────────────────────────────────────────
    [HttpGet("sorters")]
    public IActionResult GetSorters()
        => Ok(_queries.GetSorters());

    // ── E6 GET /api/monitor/sorters/{destId}/cells ──────────────────────────────
    [HttpGet("sorters/{destId:long}/cells")]
    public IActionResult GetCells([FromRoute] long destId)
        => Ok(_queries.GetCells(destId));

    // ── E7 GET /api/monitor/sorter-commands?destId=&take=&cursor= ───────────────
    [HttpGet("sorter-commands")]
    public IActionResult GetSorterCommands(
        [FromQuery] long? destId,
        [FromQuery] int? take,
        [FromQuery] long? cursor)
        => Ok(_queries.GetSorterCommands(destId, MonitoringQueries.ClampTake(take), cursor));
}
