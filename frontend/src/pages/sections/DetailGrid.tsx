import { useMemo, useState, type ChangeEvent } from 'react'
import { Table, THead, TBody, TR, TH, TD } from '@/components/ui/table'
import { LoadingRow, ErrorRow, EmptyRow } from '@/components/StateMessage'
import { FilterCell } from './SummaryGrid'
import type { DetailRow } from '@/lib/testData'
import { fmtTime, dash } from '@/lib/format'
import { cn } from '@/lib/utils'

type DetailFilters = {
  barcode: string
  chuteNo: string
  inputStatus: string
  inTime: string
  sortStatus: string
  sortTime: string
}

const EMPTY_FILTERS: DetailFilters = {
  barcode: '',
  chuteNo: '',
  inputStatus: '',
  inTime: '',
  sortStatus: '',
  sortTime: '',
}

// ── 우측: 상세 그리드(바코드·슈트·투입상태·투입시간·분류상태·분류시간) ──────────
//   · 컬럼 텍스트 필터. · 행 체크박스 다중선택(id). 아카이브 필터는 페이지 카드 헤더에서 전달.
export function DetailGrid({
  rows,
  loading,
  error,
  hasSelection,
  checked,
  onToggleCheck,
  onToggleVisible,
}: {
  rows: DetailRow[]
  loading: boolean
  error: string | null
  hasSelection: boolean
  checked: Set<number>
  onToggleCheck: (id: number) => void
  onToggleVisible: (ids: number[], allChecked: boolean) => void
}) {
  const [filters, setFilters] = useState<DetailFilters>(EMPTY_FILTERS)

  const filtered = useMemo(() => {
    const f = filters
    return rows.filter(
      (r) =>
        includes(r.barcode, f.barcode) &&
        includes(r.chuteNo, f.chuteNo) &&
        includes(r.inputStatus ?? '', f.inputStatus) &&
        includes(fmtTime(r.inTime), f.inTime) &&
        includes(r.sortStatus ?? '', f.sortStatus) &&
        includes(fmtTime(r.sortTime), f.sortTime),
    )
  }, [rows, filters])

  const visibleIds = filtered.map((r) => r.id)
  const allChecked = visibleIds.length > 0 && visibleIds.every((id) => checked.has(id))

  if (!hasSelection) return <EmptyRow label="왼쪽 요약에서 배치를 선택하면 상세가 표시됩니다" />
  if (loading && rows.length === 0) return <LoadingRow label="상세 불러오는 중" />
  if (error) return <ErrorRow message={error} />
  if (rows.length === 0) return <EmptyRow label="이 배치에 표시할 상세가 없습니다" />

  const set =
    (k: keyof DetailFilters) => (e: ChangeEvent<HTMLInputElement>) =>
      setFilters((prev) => ({ ...prev, [k]: e.target.value }))

  return (
    <Table>
      <THead>
        <tr>
          <TH className="w-9">
            <input
              type="checkbox"
              aria-label="보이는 상세 전체 선택"
              className="size-3.5 cursor-pointer accent-[var(--color-brand-active)]"
              checked={allChecked}
              onChange={() => onToggleVisible(visibleIds, allChecked)}
            />
          </TH>
          <TH>바코드</TH>
          <TH className="text-right">슈트</TH>
          <TH>투입상태</TH>
          <TH>투입시간</TH>
          <TH>분류상태</TH>
          <TH>분류시간</TH>
        </tr>
        <tr className="bg-panel">
          <th className="px-3 py-1" />
          <FilterCell value={filters.barcode} onChange={set('barcode')} placeholder="바코드" />
          <FilterCell value={filters.chuteNo} onChange={set('chuteNo')} placeholder="슈트" align="right" />
          <FilterCell value={filters.inputStatus} onChange={set('inputStatus')} placeholder="투입" />
          <FilterCell value={filters.inTime} onChange={set('inTime')} placeholder="시간" />
          <FilterCell value={filters.sortStatus} onChange={set('sortStatus')} placeholder="분류" />
          <FilterCell value={filters.sortTime} onChange={set('sortTime')} placeholder="시간" />
        </tr>
      </THead>
      <TBody>
        {filtered.length === 0 ? (
          <tr>
            <td colSpan={7}>
              <EmptyRow label="필터에 맞는 상세가 없습니다" />
            </td>
          </tr>
        ) : (
          filtered.map((r) => {
            const isChecked = checked.has(r.id)
            return (
              <TR key={r.id} className={cn(isChecked && 'bg-accent/10 hover:bg-accent/10')}>
                <TD>
                  <input
                    type="checkbox"
                    aria-label={`바코드 ${r.barcode} 선택`}
                    className="size-3.5 cursor-pointer accent-[var(--color-brand-active)]"
                    checked={isChecked}
                    onChange={() => onToggleCheck(r.id)}
                  />
                </TD>
                <TD className="font-mono text-ink">
                  {r.barcode}
                  {r.barcode2 && <span className="ml-1.5 text-faint">/ {r.barcode2}</span>}
                </TD>
                <TD className="text-right font-mono tabular-nums text-muted">{r.chuteNo}</TD>
                <TD className="text-muted">{dash(r.inputStatus)}</TD>
                <TD className="font-mono tabular-nums text-muted">{fmtTime(r.inTime)}</TD>
                <TD className="text-muted">{dash(r.sortStatus)}</TD>
                <TD className="font-mono tabular-nums text-muted">{fmtTime(r.sortTime)}</TD>
              </TR>
            )
          })
        )}
      </TBody>
    </Table>
  )
}

function includes(haystack: string, needle: string): boolean {
  if (!needle) return true
  return haystack.toLowerCase().includes(needle.toLowerCase())
}
