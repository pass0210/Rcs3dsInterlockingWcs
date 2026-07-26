import { useMemo, useState, type ChangeEvent, type Dispatch, type SetStateAction } from 'react'
import { Table, THead, TBody, TR, TH, TD } from '@/components/ui/table'
import { ContextMenu } from '@/components/ui/context-menu'
import { LoadingRow, ErrorRow, EmptyRow } from '@/components/StateMessage'
import { FilterCell } from './SummaryGrid'
import type { DetailRow } from '@/lib/testData'
import { fmtTime, dash } from '@/lib/format'
import { cn } from '@/lib/utils'
import { ROW_HIGHLIGHT_CLASS, useRowSelection } from '@/lib/useRowSelection'

const numberId = (s: string) => Number(s)

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
//   · 컬럼 텍스트 필터. · 행 체크박스 다중선택(id) + 전체선택. 아카이브 필터는 페이지 카드 헤더에서 전달.
//   · 공용 행 선택(S-B2C-GRID-UX): 드래그 범위 하이라이트 + 우클릭 4항목 — useRowSelection.
//     ★ 기존 S-B2B-2c 의 드래그/Shift/Ctrl 페인트-선택은 이 통합 모델(드래그=하이라이트 + 메뉴 적용)로 대체됨
//        (전 그리드 일관·중복 로직 0). 개별 체크박스 토글·헤더 전체선택은 그대로 유지.
export function DetailGrid({
  rows,
  loading,
  error,
  hasSelection,
  checked,
  setChecked,
  onToggleCheck,
  onToggleVisible,
}: {
  rows: DetailRow[]
  loading: boolean
  error: string | null
  hasSelection: boolean
  checked: Set<number>
  setChecked: Dispatch<SetStateAction<Set<number>>>
  onToggleCheck: (id: number) => void
  onToggleVisible: (ids: number[], allChecked: boolean) => void
}) {
  const [filters, setFilters] = useState<DetailFilters>(EMPTY_FILTERS)
  // OQ-4: 필터 변경 시 하이라이트 전체 리셋. 배치/아카이브 변경은 id 집합이 바뀌어 prune 으로 정리됨.
  const resetKey = useMemo(() => JSON.stringify(filters), [filters])
  const sel = useRowSelection<number>({ setChecked, parseId: numberId, resetKey, menuAriaLabel: '상세 바코드 메뉴' })

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

  const set =
    (k: keyof DetailFilters) => (e: ChangeEvent<HTMLInputElement>) =>
      setFilters((prev) => ({ ...prev, [k]: e.target.value }))

  return (
    // 그리드 본문 — 우클릭 메뉴/드래그 스코프(OQ-3).
    <div {...sel.containerProps}>
      {!hasSelection ? (
        <EmptyRow label="왼쪽 요약에서 배치를 선택하면 상세가 표시됩니다" />
      ) : loading && rows.length === 0 ? (
        <LoadingRow label="상세 불러오는 중" />
      ) : error ? (
        <ErrorRow message={error} />
      ) : rows.length === 0 ? (
        <EmptyRow label="이 배치에 표시할 상세가 없습니다" />
      ) : (
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
                  <TR
                    key={r.id}
                    {...sel.getRowProps(r.id, true)}
                    className={cn(
                      'cursor-default select-none',
                      isChecked && 'bg-accent/10 hover:bg-accent/10',
                      sel.isHighlighted(r.id) && ROW_HIGHLIGHT_CLASS,
                    )}
                  >
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
      )}
      {/* 우클릭 컨텍스트 메뉴(4항목). */}
      <ContextMenu {...sel.menu} />
    </div>
  )
}

function includes(haystack: string, needle: string): boolean {
  if (!needle) return true
  return haystack.toLowerCase().includes(needle.toLowerCase())
}
