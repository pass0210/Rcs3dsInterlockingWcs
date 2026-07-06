import { ChevronLeft, ChevronRight } from 'lucide-react'
import { Button } from '@/components/ui/button'

// 키셋 커서 페이저 — 서버 커서 기반 이전/다음(백엔드 nextCursor 계약과 정합).
export function CursorPager({
  page,
  count,
  hasNext,
  hasPrev,
  onNext,
  onPrev,
  fetching,
}: {
  page: number
  count: number
  hasNext: boolean
  hasPrev: boolean
  onNext: () => void
  onPrev: () => void
  fetching?: boolean
}) {
  return (
    <div className="flex items-center justify-between gap-3 border-t border-line px-4 py-2.5">
      <span className="font-mono text-[11px] tabular-nums text-muted">
        페이지 {page} · {count}건{fetching ? ' · 갱신 중…' : ''}
      </span>
      <div className="flex items-center gap-1.5">
        <Button size="sm" variant="ghost" onClick={onPrev} disabled={!hasPrev}>
          <ChevronLeft className="size-4" />
          이전
        </Button>
        <Button size="sm" variant="ghost" onClick={onNext} disabled={!hasNext}>
          다음
          <ChevronRight className="size-4" />
        </Button>
      </div>
    </div>
  )
}
