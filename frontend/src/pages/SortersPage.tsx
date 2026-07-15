import { useMonitorState, type ConnStatus } from '@/lib/signalr'
import { OpLogTail } from './sections/OpLogTail'
import { cn } from '@/lib/utils'

// ═══════════════════════════════════════════════════════════════════════════
// SortersPage — 페이지 ② "운영 로그"(operation_log 라이브 테일, 읽기 전용).
//   ★ S-UI-LAYOUT: 3DS 레지스터 워드(WordPanel)는 /ops 와 중복이라 여기서 제거(레지스터 표시는 /ops
//     단일 인스턴스로 일원화). WordPanel 의 destId 만 구동하던 소터 Select 도 고아가 되어 함께 제거.
//     이 페이지는 이제 앱에서 유일한 operation_log 테일 뷰(전역·소터 비종속)만 담는다. 라우트 /sorters 유지.
//   useMonitorState 는 SignalR 스트림 연결 상태(ConnBadge)만 소비 — useHubLifecycle(Layout)이 앱 수명
//   동안 연결을 유지하므로 WordPanel 제거가 스트림에 무영향(구독 생성·누수 0).
//   레이아웃: 상단 크롬(스트림 상태 배지)=shrink-0 · OpLogTail=flex-1 min-h-0(본문만 스크롤·뷰포트 맞춤).
// ═══════════════════════════════════════════════════════════════════════════
export function SortersPage() {
  const monitor = useMonitorState()

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-4">
      <div className="flex shrink-0 flex-wrap items-center gap-2">
        <span className="text-[12px] text-muted">실시간 operation_log 이벤트 스트림</span>
        <div className="ml-auto">
          <ConnBadge status={monitor.status} />
        </div>
      </div>

      <OpLogTail />
    </div>
  )
}

// 실시간 연결 상태 배지 — 재연결/끊김을 육안 확인(부트스트랩 복구 관찰용).
function ConnBadge({ status }: { status: ConnStatus }) {
  const map: Record<ConnStatus, { label: string; cls: string; live: boolean }> = {
    connected: { label: '실시간 연결됨', cls: 'border-online/30 bg-online/10 text-online', live: true },
    connecting: { label: '연결 중…', cls: 'border-busy/30 bg-busy/10 text-busy', live: false },
    reconnecting: { label: '재연결 중…', cls: 'border-warn/30 bg-warn/10 text-warn', live: false },
    disconnected: { label: '연결 끊김', cls: 'border-offline/30 bg-offline/10 text-offline', live: false },
  }
  const s = map[status]
  return (
    <span className={cn('inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5 text-[11px] font-medium', s.cls)}>
      <span className={cn('size-1.5 rounded-full bg-current', s.live && 'lamp-live')} />
      {s.label}
    </span>
  )
}
