import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { api, type TraceRecord } from '@/lib/api'
import { monitorHub, useMonitorState, type ConnStatus, type TraceEvent } from '@/lib/signalr'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Badge } from '@/components/ui/badge'
import { Select } from '@/components/ui/select'
import { fmtTime } from '@/lib/format'
import { cn } from '@/lib/utils'

// ═══════════════════════════════════════════════════════════════════════════
// TraceLogPage — 현장 추적용 전용 로그 뷰어(S-TRACE-LOG-VIEWER).
//   6개 이벤트(1~6)를 이벤트번호 태그로 표시. 접속 시 REST 백로그(최근 N) 시드 → SignalR로 실시간 append.
//   필터: 이벤트번호·pId·cSeq(한 피스 흐름으로 좁혀 3→4→5→6 을 이어 봄). 최신 하단·최대 500행.
//   마운트 시 trace 그룹 구독 / 언마운트·창닫힘 시 해제(서버 push no-op — 서비스는 계속). 읽기 전용.
// ═══════════════════════════════════════════════════════════════════════════

const MAX_ROWS = 500

// 이벤트 번호 → 라벨/색조(즉시 식별). 1·2=층-큐, 3~6=피스 흐름.
const EVENT_META: Record<number, { label: string; tone: 'neutral' | 'accent' | 'busy' | 'online' | 'warn' }> = {
  1: { label: 'TgtFloor 인큐', tone: 'neutral' },
  2: { label: 'TgtFloor 디큐', tone: 'neutral' },
  3: { label: 'IF-10 도착', tone: 'accent' },
  4: { label: 'C 인큐', tone: 'busy' },
  5: { label: 'C 디큐', tone: 'warn' },
  6: { label: 'C 클리어', tone: 'online' },
}

type Row = TraceRecord | TraceEvent

export function TraceLogPage() {
  const monitor = useMonitorState()

  const [eventNo, setEventNo] = useState('')
  const [pId, setPId] = useState('')
  const [cSeq, setCSeq] = useState('')
  const [autoScroll, setAutoScroll] = useState(true)
  const [rows, setRows] = useState<Row[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const scrollRef = useRef<HTMLDivElement>(null)

  // 파싱된 필터(숫자) — 빈 문자열/비수치는 undefined.
  const filter = useMemo(() => {
    const num = (s: string) => (s.trim() !== '' && Number.isFinite(Number(s)) ? Number(s) : undefined)
    return { eventNo: num(eventNo), pId: num(pId), cSeq: num(cSeq) }
  }, [eventNo, pId, cSeq])

  // 라이브 이벤트 표시 여부(클라이언트 필터 — 백로그 서버 필터와 동일 규칙).
  const matches = (e: Row): boolean => {
    if (filter.eventNo !== undefined && e.eventNo !== filter.eventNo) return false
    if (filter.pId !== undefined && e.pId !== filter.pId) return false
    if (filter.cSeq !== undefined && e.cSeq !== filter.cSeq) return false
    return true
  }

  // REST 백로그 시드(필터 변경 시 재로드). 백엔드가 시계열 오름차순 반환(최신 하단).
  useEffect(() => {
    let cancelled = false
    setLoading(true)
    setError(null)
    api
      .trace({ take: 200, eventNo: filter.eventNo, pId: filter.pId, cSeq: filter.cSeq })
      .then((items) => {
        if (cancelled) return
        setRows(items)
        setLoading(false)
      })
      .catch((err: unknown) => {
        if (cancelled) return
        setError(err instanceof Error ? err.message : '조회 실패')
        setLoading(false)
      })
    return () => {
      cancelled = true
    }
  }, [filter.eventNo, filter.pId, filter.cSeq])

  // 라이브 스트림 — 마운트 시 trace 그룹 구독, 언마운트/창닫힘 시 해제(서비스는 계속). 필터 통과분만 append.
  useEffect(() => {
    const unsub = monitorHub.subscribeTrace((e: TraceEvent) => {
      if (!matches(e)) return
      setRows((prev) => {
        const next = prev.length >= MAX_ROWS ? prev.slice(prev.length - MAX_ROWS + 1) : prev.slice()
        next.push(e)
        return next
      })
    })
    return unsub
    // matches는 filter에 의존 — 최신 클로저 유지 위해 필터를 의존성에 명시.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filter.eventNo, filter.pId, filter.cSeq])

  // 자동 스크롤 — 새 행 도착 시 하단 고정.
  useLayoutEffect(() => {
    if (autoScroll && scrollRef.current) {
      scrollRef.current.scrollTop = scrollRef.current.scrollHeight
    }
  }, [rows, autoScroll])

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-4">
      <div className="flex shrink-0 flex-wrap items-center gap-2">
        <span className="text-[12px] text-muted">전용 추적 로그(6개 이벤트) 실시간 스트림</span>
        <div className="ml-auto">
          <ConnBadge status={monitor.status} />
        </div>
      </div>

      <Card className="flex min-h-0 flex-1 flex-col">
        <CardHeader className="shrink-0">
          <CardTitle>핸드셰이크·2층 제어 추적 로그</CardTitle>
          {/* 알려진 한계(계약 수용): pId↔cSeq 상관은 소터별 순차 dispatch 를 전제한다. 한 소터에 동시 IF-10 이
              겹치면 상관이 교차될 수 있음(현 코드베이스는 동시 IF-10 직렬화 미보장 — SPEC §6 물리 직렬 전제). */}
          <p className="text-[11px] text-faint">
            상관(pId↔cSeq)은 소터별 순차 dispatch 전제 — 한 소터 동시 투입 시 교차 가능
          </p>
          <div className="flex flex-wrap items-center gap-2">
            <label className="text-[12px] text-muted">이벤트</label>
            <Select value={eventNo} onChange={(e) => setEventNo(e.target.value)}>
              <option value="">전체</option>
              {[1, 2, 3, 4, 5, 6].map((n) => (
                <option key={n} value={n}>
                  {n} · {EVENT_META[n].label}
                </option>
              ))}
            </Select>
            <label className="ml-1 text-[12px] text-muted">pId</label>
            <input
              type="number"
              value={pId}
              onChange={(e) => setPId(e.target.value)}
              placeholder="전체"
              className="h-8 w-24 rounded-lg border border-line bg-panel px-2 text-[13px] text-ink focus-visible:outline-2 focus-visible:outline-ink"
            />
            <label className="ml-1 text-[12px] text-muted">cSeq</label>
            <input
              type="number"
              value={cSeq}
              onChange={(e) => setCSeq(e.target.value)}
              placeholder="전체"
              className="h-8 w-24 rounded-lg border border-line bg-panel px-2 text-[13px] text-ink focus-visible:outline-2 focus-visible:outline-ink"
            />
            <label className="ml-1 flex items-center gap-1.5 text-[12px] text-muted">
              <input
                type="checkbox"
                checked={autoScroll}
                onChange={(e) => setAutoScroll(e.target.checked)}
                className="size-3.5 accent-[var(--color-accent)]"
              />
              자동 스크롤
            </label>
          </div>
        </CardHeader>
        <CardContent className="flex min-h-0 flex-1 flex-col overflow-hidden p-0">
          {error && (
            <div className="px-4 py-6 text-center text-[13px] text-offline">데이터를 불러오지 못했습니다 — {error}</div>
          )}
          {!error && loading && <div className="px-4 py-6 text-center text-[13px] text-muted">불러오는 중…</div>}
          {!error && !loading && rows.length === 0 && (
            <div className="px-4 py-6 text-center text-[13px] text-muted">표시할 추적 로그가 없습니다</div>
          )}
          {!error && rows.length > 0 && (
            <div ref={scrollRef} className="min-h-0 flex-1 overflow-y-auto">
              <table className="w-full border-collapse text-[12px]">
                <thead className="sticky top-0 bg-panel">
                  <tr className="border-b border-line text-left text-[11px] text-faint">
                    <th className="px-3 py-1.5 font-medium">#</th>
                    <th className="px-2 py-1.5 font-medium">시각</th>
                    <th className="px-2 py-1.5 font-medium">이벤트</th>
                    <th className="px-2 py-1.5 font-medium">pId</th>
                    <th className="px-2 py-1.5 font-medium">cSeq</th>
                    <th className="px-2 py-1.5 font-medium">chuteNo</th>
                    <th className="px-2 py-1.5 font-medium">cellNo</th>
                    <th className="px-2 py-1.5 font-medium">floor</th>
                    <th className="px-3 py-1.5 font-medium">detail</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((r, i) => (
                    <TraceLine key={`${i}-${r.at}-${r.eventNo}`} row={r} />
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  )
}

function TraceLine({ row }: { row: Row }) {
  const meta = EVENT_META[row.eventNo] ?? { label: row.event, tone: 'neutral' as const }
  return (
    <tr className="border-b border-line/50 align-top hover:bg-elevated/50">
      <td className="whitespace-nowrap px-3 py-1 font-mono font-semibold tabular-nums text-ink">{row.eventNo}</td>
      <td className="whitespace-nowrap px-2 py-1 font-mono tabular-nums text-muted">{fmtTime(row.at)}</td>
      <td className="px-2 py-1">
        <Badge tone={meta.tone}>{meta.label}</Badge>
      </td>
      <td className="px-2 py-1 font-mono tabular-nums text-ink">{row.pId ?? '—'}</td>
      <td className="px-2 py-1 font-mono tabular-nums text-muted">{row.cSeq ?? '—'}</td>
      <td className="px-2 py-1 font-mono tabular-nums text-muted">{row.chuteNo ?? '—'}</td>
      <td className="px-2 py-1 font-mono tabular-nums text-muted">{row.cellNo ?? '—'}</td>
      <td className="px-2 py-1 font-mono tabular-nums text-muted">{row.floor ?? '—'}</td>
      <td className="px-3 py-1 font-mono text-muted">
        <span className="line-clamp-1 break-all" title={row.detail ?? ''}>
          {row.detail ?? ''}
        </span>
      </td>
    </tr>
  )
}

// 실시간 연결 상태 배지 — 재연결/끊김 표시(W5 disconnected 안내).
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
