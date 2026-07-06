import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { api, type OperationLog } from '@/lib/api'
import { monitorHub, type OpLogEntry } from '@/lib/signalr'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Badge } from '@/components/ui/badge'
import { Select } from '@/components/ui/select'
import { fmtTime } from '@/lib/format'
import { cn } from '@/lib/utils'

// ═══════════════════════════════════════════════════════════════════════════
// OpLogTail — operation_log 라이브 테일(§2.2). 접속 시 REST 백로그 로드 후 SignalR로 append.
//   category/level 필터, 자동 스크롤 토글, POLL_CHANGE 기본 접힘(옵트인). 읽기 전용.
//   표시는 시계열(오래된→최신, 최신이 하단) — tail -f 정서. 최대 500행 유지(앞에서 드롭).
// ═══════════════════════════════════════════════════════════════════════════
const CATEGORIES = ['API', 'PLC_WRITE', 'HANDSHAKE', 'STATE', 'POLL_CHANGE']
const LEVELS = ['INFO', 'WARN', 'ERROR']
const MAX_ROWS = 500

type Row = OperationLog | (OpLogEntry & { id: number | null })

export function OpLogTail() {
  const [category, setCategory] = useState('')
  const [level, setLevel] = useState('')
  const [includePoll, setIncludePoll] = useState(false)
  const [autoScroll, setAutoScroll] = useState(true)
  const [rows, setRows] = useState<Row[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const scrollRef = useRef<HTMLDivElement>(null)

  // POLL_CHANGE 서버 구독 필요 여부 — 명시 category=POLL_CHANGE 또는 전체+포함 체크.
  const pollNeeded = category === 'POLL_CHANGE' || (category === '' && includePoll)
  useEffect(() => {
    monitorHub.setPollChangeOptIn(pollNeeded)
  }, [pollNeeded])

  // 라이브 엔트리 표시 여부(클라이언트 필터).
  const matches = (e: { category: string; level: string }): boolean => {
    if (category) {
      if (e.category !== category) return false
    } else if (e.category === 'POLL_CHANGE' && !includePoll) {
      return false
    }
    if (level && e.level !== level) return false
    return true
  }

  // REST 백로그 로드(필터 변경 시 재로드). 최신순 → 시계열로 뒤집어 표시.
  useEffect(() => {
    let cancelled = false
    setLoading(true)
    setError(null)
    api
      .operationLog({ category: category || undefined, level: level || undefined, take: 100 })
      .then((res) => {
        if (cancelled) return
        setRows([...res.items].reverse())
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
  }, [category, level])

  // 라이브 스트림 — 필터 통과분만 append(최대 MAX_ROWS).
  useEffect(() => {
    const unsub = monitorHub.subscribeOpLog((e: OpLogEntry) => {
      if (!matches(e)) return
      setRows((prev) => {
        const next = prev.length >= MAX_ROWS ? prev.slice(prev.length - MAX_ROWS + 1) : prev.slice()
        next.push(e)
        return next
      })
    })
    return unsub
    // matches는 category/level/includePoll에 의존 — 최신 클로저 유지 위해 의존성 명시.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [category, level, includePoll])

  // 자동 스크롤 — 새 행 도착 시 하단 고정.
  useLayoutEffect(() => {
    if (autoScroll && scrollRef.current) {
      scrollRef.current.scrollTop = scrollRef.current.scrollHeight
    }
  }, [rows, autoScroll])

  return (
    <Card>
      <CardHeader>
        <CardTitle>operation_log 라이브 테일</CardTitle>
        <div className="flex flex-wrap items-center gap-2">
          <label className="text-[12px] text-muted">카테고리</label>
          <Select value={category} onChange={(e) => setCategory(e.target.value)}>
            <option value="">전체</option>
            {CATEGORIES.map((c) => (
              <option key={c} value={c}>
                {c}
              </option>
            ))}
          </Select>
          <label className="ml-1 text-[12px] text-muted">레벨</label>
          <Select value={level} onChange={(e) => setLevel(e.target.value)}>
            <option value="">전체</option>
            {LEVELS.map((l) => (
              <option key={l} value={l}>
                {l}
              </option>
            ))}
          </Select>
          {category === '' && (
            <label className="ml-1 flex items-center gap-1.5 text-[12px] text-muted">
              <input
                type="checkbox"
                checked={includePoll}
                onChange={(e) => setIncludePoll(e.target.checked)}
                className="size-3.5 accent-[var(--color-accent)]"
              />
              POLL_CHANGE 포함
            </label>
          )}
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
      <CardContent className="p-0">
        {error && (
          <div className="px-4 py-6 text-center text-[13px] text-offline">데이터를 불러오지 못했습니다 — {error}</div>
        )}
        {!error && loading && <div className="px-4 py-6 text-center text-[13px] text-muted">불러오는 중…</div>}
        {!error && !loading && rows.length === 0 && (
          <div className="px-4 py-6 text-center text-[13px] text-muted">표시할 로그가 없습니다</div>
        )}
        {!error && rows.length > 0 && (
          <div ref={scrollRef} className="max-h-[340px] overflow-y-auto">
            <table className="w-full border-collapse text-[12px]">
              <tbody>
                {rows.map((r, i) => (
                  <LogLine key={r.id ?? `live-${i}-${r.at}`} row={r} />
                ))}
              </tbody>
            </table>
          </div>
        )}
      </CardContent>
    </Card>
  )
}

function LogLine({ row }: { row: Row }) {
  const levelTone =
    row.level === 'ERROR' ? 'text-offline' : row.level === 'WARN' ? 'text-warn' : 'text-muted'
  return (
    <tr className="border-b border-line/50 align-top hover:bg-elevated/50">
      <td className="whitespace-nowrap px-3 py-1 font-mono tabular-nums text-muted">{fmtTime(row.at)}</td>
      <td className="px-2 py-1">
        <Badge tone={categoryTone(row.category)}>{row.category}</Badge>
      </td>
      <td className={cn('whitespace-nowrap px-2 py-1 font-mono font-medium', levelTone)}>{row.level}</td>
      <td className="whitespace-nowrap px-2 py-1 font-mono text-ink">{row.action}</td>
      <td className="px-2 py-1 font-mono tabular-nums text-muted">{row.sorterChuteNo ?? '—'}</td>
      <td className="px-3 py-1 font-mono text-muted">
        <span className="line-clamp-1 break-all" title={row.detail ?? ''}>
          {row.detail ?? ''}
        </span>
      </td>
    </tr>
  )
}

function categoryTone(cat: string): 'neutral' | 'accent' | 'busy' | 'online' | 'warn' {
  switch (cat) {
    case 'API':
      return 'accent'
    case 'HANDSHAKE':
      return 'busy'
    case 'STATE':
      return 'online'
    case 'PLC_WRITE':
      return 'warn'
    default:
      return 'neutral'
  }
}
