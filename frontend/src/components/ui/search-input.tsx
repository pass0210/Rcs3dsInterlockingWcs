import { Search, X } from 'lucide-react'
import { cn } from '@/lib/utils'

// 통합 검색 입력 — 화면당 1개. 모든 표시 필드 부분일치(OR, 대소문자 무시)는 호출부가 수행.
// Esc 로 초기화(원본 통합검색 동작 재현). 밀집 운영툴 정서(잉크 포커스링·헤어라인).
export function SearchInput({
  value,
  onChange,
  placeholder = '통합 검색',
  className,
}: {
  value: string
  onChange: (v: string) => void
  placeholder?: string
  className?: string
}) {
  return (
    <div className={cn('relative inline-flex items-center', className)}>
      <Search className="pointer-events-none absolute left-2.5 size-3.5 text-faint" />
      <input
        value={value}
        onChange={(e) => onChange(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Escape') onChange('')
        }}
        placeholder={placeholder}
        aria-label={placeholder}
        className={cn(
          'h-8 w-full rounded-lg border border-line bg-panel pl-8 pr-7 text-[13px] text-ink placeholder:text-faint/70',
          'hover:border-ink/40 focus-visible:outline-2 focus-visible:outline-ink',
        )}
      />
      {value && (
        <button
          type="button"
          onClick={() => onChange('')}
          aria-label="검색 지우기"
          className="absolute right-2 text-faint hover:text-ink"
        >
          <X className="size-3.5" />
        </button>
      )}
    </div>
  )
}
