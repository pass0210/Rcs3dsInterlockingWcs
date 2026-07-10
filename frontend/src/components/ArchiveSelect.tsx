import { Archive } from 'lucide-react'
import { Select } from '@/components/ui/select'
import type { ArchiveFilter } from '@/lib/testData'

// 아카이브 필터 3상태 라벨(DataGeneratorPage 와 동일 어휘) — 보관 데이터 포함 여부 제어.
// 이 파일은 컴포넌트만 export 한다(react-refresh 규칙 청정) — 라벨은 모듈 내부 전용.
const ARCHIVE_LABELS: Record<ArchiveFilter, string> = {
  active: '활성만',
  all: '전체(보관 포함)',
  archivedOnly: '보관만',
}

// 보관(아카이브) 필터 셀렉트 — 로그(투입/분류)·비교 화면 공용. Excel/박스처럼 archived 없는 화면엔 미사용.
export function ArchiveSelect({
  value,
  onChange,
}: {
  value: ArchiveFilter
  onChange: (v: ArchiveFilter) => void
}) {
  return (
    <span className="flex items-center gap-1.5">
      <span className="flex items-center gap-1 text-[12px] text-faint">
        <Archive className="size-3.5" />
        보관
      </span>
      <Select
        value={value}
        onChange={(e) => onChange(e.target.value as ArchiveFilter)}
        aria-label="아카이브 필터"
      >
        {(Object.keys(ARCHIVE_LABELS) as ArchiveFilter[]).map((k) => (
          <option key={k} value={k}>
            {ARCHIVE_LABELS[k]}
          </option>
        ))}
      </Select>
    </span>
  )
}
