import { useEffect, useState } from 'react'
import { useSorters } from '@/lib/queries'
import { useMonitorState, type ConnStatus } from '@/lib/signalr'
import { Select } from '@/components/ui/select'
import { WordPanel } from './sections/WordPanel'
import { OpLogTail } from './sections/OpLogTail'
import { cn } from '@/lib/utils'

// ═══════════════════════════════════════════════════════════════════════════
// SortersPage — 페이지 ② "3DS 워드"(읽기 전용·F2).
//   소터 선택(useSorters 재사용) → 레지스터 워드 실시간 뷰(WordPanel) + operation_log 테일.
//   워드 상태는 SignalR 스트림(useMonitorState). 편집/제어 없음(F3).
// ═══════════════════════════════════════════════════════════════════════════
export function SortersPage() {
  const { data: sorters, isLoading: sortersLoading } = useSorters()
  const monitor = useMonitorState()
  const [destId, setDestId] = useState<number | null>(null)

  // 소터 목록 도착 시 첫 소터 기본 선택.
  useEffect(() => {
    if (destId === null && sorters && sorters.length > 0) setDestId(sorters[0].destId)
  }, [sorters, destId])

  const wordState = destId !== null ? monitor.sorters.get(destId) : undefined

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-2">
        <label className="text-[12px] text-muted">소터</label>
        <Select
          value={destId ?? ''}
          onChange={(e) => setDestId(Number(e.target.value))}
          disabled={sortersLoading || !sorters?.length}
        >
          {sorters?.map((s) => (
            <option key={s.destId} value={s.destId}>
              3DS #{String(s.chuteNo).padStart(2, '0')} {s.online ? '(온라인)' : '(오프라인)'}
            </option>
          ))}
        </Select>
        {!sortersLoading && !sorters?.length && (
          <span className="text-[12px] text-muted">등록된 소터가 없습니다</span>
        )}
        <div className="ml-auto">
          <ConnBadge status={monitor.status} />
        </div>
      </div>

      <WordPanel state={wordState} />
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
