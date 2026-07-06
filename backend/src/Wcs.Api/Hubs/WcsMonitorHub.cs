using Microsoft.AspNetCore.SignalR;

namespace Wcs.Api.Hubs;

// ════════════════════════════════════════════════════════════════════════════
// WcsMonitorHub — F2 실시간 관측 허브 (/hubs/monitor). 읽기 전용(쓰기/제어/인증은 F3).
//
// 역할:
//   · 부트스트랩(OnConnectedAsync): 접속/재연결 시 전체 소터 워드 스냅샷(AllBundles.Latest)을
//     그 클라이언트에게 1회 전송 → 늦게 접속한 클라이언트도 즉시 완전 상태 확보.
//   · 구독 그룹: 접속 시 소터 워드 스트림(sorters)·oplog 기본 스트림(oplog)에 자동 가입.
//     고빈도 POLL_CHANGE는 별도 그룹(oplog-poll) 명시 옵트인(콘솔/테일 폭주 방지).
//   · 실제 push(델타·전이·하트비트·oplog)는 MonitorRelayService가 IHubContext로 수행.
//
// AllBundles는 SorterRegistryFactory.StartAsync 완료 후 채워진다 — 허브 접속은 기동 완료 후이므로
// 부트스트랩 시 항상 유효(빈 세트면 소터 0대 — 정상). relay 구독 시점과 무관(허브는 조회만).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>실시간 관측 허브 — 부트스트랩 + 구독 그룹 관리. push는 relay가 담당.</summary>
public sealed class WcsMonitorHub : Hub
{
    /// <summary>소터 워드 스트림(델타·전이·하트비트) 그룹.</summary>
    public const string GroupSorters = "sorters";
    /// <summary>operation_log 기본 테일 그룹(POLL_CHANGE 제외).</summary>
    public const string GroupOpLog = "oplog";
    /// <summary>POLL_CHANGE 옵트인 그룹(고빈도 — 명시 구독 클라이언트만).</summary>
    public const string GroupOpLogPoll = "oplog-poll";

    private readonly ISorterGatewayRegistry _registry;

    public WcsMonitorHub(ISorterGatewayRegistry registry) => _registry = registry;

    public override async Task OnConnectedAsync()
    {
        // 기본 구독: 소터 워드 + oplog(POLL_CHANGE 제외).
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupSorters);
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupOpLog);

        // 부트스트랩 스냅샷 — 이 클라이언트에게만 전송(재연결 시에도 동일 경로로 복구).
        var snapshot = _registry.AllBundles
            .OrderBy(b => b.ChuteNo)
            .Select(b =>
            {
                var s = b.Latest;
                return new SorterWordDto(
                    b.DestinationId, b.ChuteNo, s.Online,
                    s.CCellNo, s.CSeq, s.RCellNo, s.RSeq,
                    s.CFlag, s.RFlag, s.Ready,
                    s.CurFloor, s.TgtFloor, s.At);
            })
            .ToArray();

        await Clients.Caller.SendAsync("Bootstrap", snapshot);
        await base.OnConnectedAsync();
    }

    /// <summary>POLL_CHANGE 테일 옵트인 — 이 커넥션을 oplog-poll 그룹에 가입.</summary>
    public Task SubscribePollChange() =>
        Groups.AddToGroupAsync(Context.ConnectionId, GroupOpLogPoll);

    /// <summary>POLL_CHANGE 테일 옵트아웃 — oplog-poll 그룹에서 탈퇴.</summary>
    public Task UnsubscribePollChange() =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupOpLogPoll);
}
