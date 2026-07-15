import { useEffect, useMemo, useState } from 'react'
import { useSorters } from '@/lib/queries'
import { useMonitorState, type ConnStatus } from '@/lib/signalr'
import { Select } from '@/components/ui/select'
import { Badge } from '@/components/ui/badge'
import { WordPanel } from './sections/WordPanel'
import { OpsControls } from './sections/OpsControls'
import { cn } from '@/lib/utils'

// ═══════════════════════════════════════════════════════════════════════════
// OpsPage — 페이지 ③ "운영 제어"(B2C·F3b). 신규 전용 라우트 /ops.
//   소터 선택 → 현재 상태(WordPanel 재사용 · readiness 스트립)를 편집 전 근거로 표시하고,
//   그 아래 소터 대상 제어(Pause/Resume · SetTgtFloor · Clear-R · Cell-Assign)를 수행한다.
//   모든 조작은 확인 다이얼로그 + 필수 작업자 이름 + 규칙/위험 경고를 거쳐 /api/ops/*를 호출한다.
//
//   작업자 이름은 페이지 레벨 in-memory state(세션 기억) — 매 확인 다이얼로그에 프리필(수정 가능),
//   공백이면 확인 비활성. localStorage 영속화 없음(세션 간 잘못된 이름 재사용 감사 위험 회피 — 계약 Q2).
//   슈트 clear(O1)·CHUTE pause/resume은 F3b 스코프 제외(읽기 열거 엔드포인트 부재 — 후속 이관).
// ═══════════════════════════════════════════════════════════════════════════
export function OpsPage() {
  const { data: sorters, isLoading: sortersLoading } = useSorters()
  const monitor = useMonitorState()
  const [destId, setDestId] = useState<number | null>(null)
  // 작업자 이름 — 세션 기억(in-memory). 새로고침/재진입 시 비어 있고, 첫 입력 후 세션 동안 유지.
  const [operatorName, setOperatorName] = useState('')

  // 소터 목록 도착 시 첫 소터 기본 선택(SortersPage 패턴 준용).
  useEffect(() => {
    if (destId === null && sorters && sorters.length > 0) setDestId(sorters[0].destId)
  }, [sorters, destId])

  // 선택 소터 readiness(online/ready/full/paused) — api.sorters()의 SorterStatus.
  const selected = useMemo(
    () => sorters?.find((s) => s.destId === destId) ?? null,
    [sorters, destId],
  )
  // 선택 소터 워드(D0~D6) — SignalR 실시간(useMonitorState). 편집 전 근거·currentTgtFloor.
  const wordState = destId !== null ? monitor.sorters.get(destId) : undefined

  const noSorters = !sortersLoading && !sorters?.length

  return (
    // 뷰포트 맞춤(S-UI-LAYOUT) — 소터 선택 바=shrink-0 크롬, WordPanel+OpsControls 는 하단 스크롤 본문에
    // 담는다(그리드형 아님 → OQ-2 하한: 본문 flex-1 min-h-0 overflow-auto, 짧은 뷰포트에선 본문만 스크롤).
    <div className="flex min-h-0 flex-1 flex-col gap-4">
      {/* 소터 선택 + 연결 배지 */}
      <div className="flex shrink-0 flex-wrap items-center gap-2">
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
        {sortersLoading && <span className="text-[12px] text-faint">소터 목록 불러오는 중…</span>}
        {noSorters && <span className="text-[12px] text-muted">등록된 소터가 없습니다</span>}
        {selected && <ReadinessStrip sorter={selected} />}
        <div className="ml-auto">
          <ConnBadge status={monitor.status} />
        </div>
      </div>

      {noSorters ? (
        <div className="shrink-0 rounded-[14px] border border-line bg-panel px-4 py-10 text-center text-[13px] text-faint shadow-card">
          등록된 소터가 없어 운영 제어를 표시할 수 없습니다.
        </div>
      ) : (
        // 상태 근거(WordPanel) + 제어(OpsControls) 스택 = 스크롤 본문. 짧은 뷰포트에서 크롬은 고정되고
        // 이 영역만 스크롤(overflow-auto). 넓은 뷰포트에선 자연 높이로 표시(강제 채움 없음).
        <div className="flex min-h-0 flex-1 flex-col gap-4 overflow-auto">
          {/* 현재 상태(편집 전 근거) — WordPanel 재사용(무수정). */}
          <WordPanel state={wordState} />
          {/* 소터 대상 제어 — 선택 소터가 확정된 뒤에만 렌더(destId·selected 결선). */}
          {selected && (
            <OpsControls
              sorter={selected}
              wordState={wordState}
              operatorName={operatorName}
              setOperatorName={setOperatorName}
            />
          )}
        </div>
      )}
    </div>
  )
}

// 선택 소터 readiness 스트립 — online/ready/full/paused를 한눈에(편집 전 상태 근거).
function ReadinessStrip({
  sorter,
}: {
  sorter: { online: boolean; ready: boolean; full: boolean; paused: boolean }
}) {
  return (
    <div className="flex items-center gap-1.5">
      <Badge tone={sorter.online ? 'online' : 'offline'} dot>
        {sorter.online ? '온라인' : '오프라인'}
      </Badge>
      <Badge tone={sorter.online && sorter.ready ? 'online' : 'neutral'} dot>
        {sorter.ready ? 'Ready' : 'Busy'}
      </Badge>
      {sorter.full && (
        <Badge tone="warn" dot>
          만재
        </Badge>
      )}
      {sorter.paused && (
        <Badge tone="warn" dot>
          일시정지
        </Badge>
      )}
    </div>
  )
}

// 실시간 연결 상태 배지 — 조작 후 라이브 반영 신뢰용(SortersPage와 동일 계기판 톤).
function ConnBadge({ status }: { status: ConnStatus }) {
  const map: Record<ConnStatus, { label: string; cls: string; live: boolean }> = {
    connected: { label: '실시간 연결됨', cls: 'border-online/30 bg-online/10 text-online', live: true },
    connecting: { label: '연결 중…', cls: 'border-busy/30 bg-busy/10 text-busy', live: false },
    reconnecting: { label: '재연결 중…', cls: 'border-warn/30 bg-warn/10 text-warn', live: false },
    disconnected: { label: '연결 끊김', cls: 'border-offline/30 bg-offline/10 text-offline', live: false },
  }
  const s = map[status]
  return (
    <span
      className={cn(
        'inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5 text-[11px] font-medium',
        s.cls,
      )}
    >
      <span className={cn('size-1.5 rounded-full bg-current', s.live && 'lamp-live')} />
      {s.label}
    </span>
  )
}
