import { cn } from '@/lib/utils'

// ── 오더 진행 바 — planned 대비 reserved(예약)·sorted(분류 확정) 2단계 스택 ──────
export function ProgressBar({
  planned,
  reserved,
  sorted,
}: {
  planned: number
  reserved: number
  sorted: number
}) {
  const total = Math.max(planned, reserved + sorted, 1)
  const sortedPct = Math.min(100, (sorted / total) * 100)
  const reservedPct = Math.min(100 - sortedPct, (Math.max(0, reserved) / total) * 100)
  return (
    <div className="flex items-center gap-2">
      <div className="h-1.5 w-28 overflow-hidden rounded-full bg-base">
        <div className="flex h-full">
          <div className="h-full bg-online" style={{ width: `${sortedPct}%` }} />
          <div className="h-full bg-busy/50" style={{ width: `${reservedPct}%` }} />
        </div>
      </div>
      <span className="font-mono text-[11px] tabular-nums text-muted">
        {sorted}/{planned}
      </span>
    </div>
  )
}

// ── 셀 용량 게이지 — 현재수량/용량. 세그먼트 채움 + 상태색(여유/근접/만재). ─────
export function CapacityMeter({
  current,
  capacity,
}: {
  current: number
  capacity: number | null
}) {
  // capacity NULL/≤0 = 무제한 — 채움 게이지 대신 현재수량만 표기.
  if (capacity === null || capacity <= 0) {
    return (
      <div className="flex items-center gap-2">
        <div className="h-1.5 w-20 rounded-full bg-base" />
        <span className="font-mono text-[11px] tabular-nums text-muted">{current}/∞</span>
      </div>
    )
  }
  const ratio = current / capacity
  const pct = Math.min(100, ratio * 100)
  const tone = ratio >= 1 ? 'bg-warn' : ratio >= 0.75 ? 'bg-busy' : 'bg-online'
  const text = ratio >= 1 ? 'text-warn' : 'text-muted'
  return (
    <div className="flex items-center gap-2">
      <div className="h-1.5 w-20 overflow-hidden rounded-full bg-base">
        <div className={cn('h-full transition-[width]', tone)} style={{ width: `${pct}%` }} />
      </div>
      <span className={cn('font-mono text-[11px] tabular-nums', text)}>
        {current}/{capacity}
      </span>
    </div>
  )
}
