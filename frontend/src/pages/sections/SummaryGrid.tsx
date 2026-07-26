import { useMemo, useState, type ChangeEvent, type Dispatch, type SetStateAction } from 'react'
import { Table, THead, TBody, TR, TH, TD } from '@/components/ui/table'
import { ContextMenu } from '@/components/ui/context-menu'
import { LoadingRow, ErrorRow, EmptyRow } from '@/components/StateMessage'
import { summaryKey, type SummaryRow } from '@/lib/testData'
import { fmtTime } from '@/lib/format'
import { cn } from '@/lib/utils'
import { ROW_HIGHLIGHT_CLASS, useRowSelection } from '@/lib/useRowSelection'

const identityId = (s: string) => s

// ── 중앙: 요약 그리드(날짜·배치·수량·수신시간) ───────────────────────────────
//   · 컬럼 텍스트 필터(부분일치·대소문자 무시). · 행 체크박스 다중선택. · 행 클릭 → 상세 로드.
//   · 공용 행 선택(S-B2C-GRID-UX): 드래그 범위 하이라이트 + 우클릭 4항목(전체/선택 체크·해제) — useRowSelection.
//     기존 checked 집합(부모 소유·string 키)에 브리지. 전 행 체크 가능(eligible=true).
export function SummaryGrid({
  rows,
  loading,
  error,
  selectedKey,
  checked,
  setChecked,
  onRowClick,
  onToggleCheck,
  onToggleVisible,
}: {
  rows: SummaryRow[]
  loading: boolean
  error: string | null
  selectedKey: string | null
  checked: Set<string>
  setChecked: Dispatch<SetStateAction<Set<string>>>
  onRowClick: (row: SummaryRow) => void
  onToggleCheck: (key: string) => void
  onToggleVisible: (keys: string[], allChecked: boolean) => void
}) {
  const [filters, setFilters] = useState({ bizDay: '', batch: '', count: '', receiveTime: '' })
  // OQ-4: 필터 변경 시 하이라이트 전체 리셋(스코프/필터 시그니처). 배치(bizDay) 변경은 키 자체가 바뀌어 prune.
  const resetKey = useMemo(() => JSON.stringify(filters), [filters])
  const sel = useRowSelection<string>({ setChecked, parseId: identityId, resetKey, menuAriaLabel: '배치 요약 메뉴' })

  const filtered = useMemo(() => {
    const f = filters
    return rows.filter((r) => {
      return (
        includes(r.bizDay, f.bizDay) &&
        includes(r.batch, f.batch) &&
        includes(String(r.count), f.count) &&
        includes(fmtTime(r.receiveTime), f.receiveTime)
      )
    })
  }, [rows, filters])

  const visibleKeys = filtered.map((r) => summaryKey(r.bizDay, r.batch))
  const allChecked = visibleKeys.length > 0 && visibleKeys.every((k) => checked.has(k))

  return (
    // 그리드 본문 — 우클릭 메뉴/드래그 스코프(OQ-3: 이 컨테이너 내부에서만 네이티브 우클릭 대체).
    <div {...sel.containerProps}>
      {loading && rows.length === 0 ? (
        <LoadingRow label="요약 불러오는 중" />
      ) : error ? (
        <ErrorRow message={error} />
      ) : rows.length === 0 ? (
        <EmptyRow label="이 업무일자에 생성된 배치가 없습니다" />
      ) : (
        <Table>
          <THead>
            <tr>
              <TH className="w-9">
                <input
                  type="checkbox"
                  aria-label="보이는 배치 전체 선택"
                  className="size-3.5 cursor-pointer accent-[var(--color-brand-active)]"
                  checked={allChecked}
                  onChange={() => onToggleVisible(visibleKeys, allChecked)}
                />
              </TH>
              <TH>날짜</TH>
              <TH>배치</TH>
              <TH className="text-right">수량</TH>
              <TH>수신시간</TH>
            </tr>
            <FilterRow filters={filters} onChange={setFilters} />
          </THead>
          <TBody>
            {filtered.length === 0 ? (
              <tr>
                <td colSpan={5}>
                  <EmptyRow label="필터에 맞는 배치가 없습니다" />
                </td>
              </tr>
            ) : (
              filtered.map((r) => {
                const key = summaryKey(r.bizDay, r.batch)
                const isSelected = key === selectedKey
                return (
                  <TR
                    key={key}
                    {...sel.getRowProps(key, true)}
                    onClick={() => onRowClick(r)}
                    className={cn(
                      'cursor-pointer',
                      isSelected && 'bg-accent/10 hover:bg-accent/10',
                      sel.isHighlighted(key) && ROW_HIGHLIGHT_CLASS,
                    )}
                  >
                    <TD onClick={(e) => e.stopPropagation()}>
                      <input
                        type="checkbox"
                        aria-label={`배치 ${r.batch} 선택`}
                        className="size-3.5 cursor-pointer accent-[var(--color-brand-active)]"
                        checked={checked.has(key)}
                        onChange={() => onToggleCheck(key)}
                      />
                    </TD>
                    <TD className="font-mono tabular-nums text-muted">{r.bizDay}</TD>
                    <TD className="font-mono text-ink">{r.batch}</TD>
                    <TD className="text-right font-mono tabular-nums text-ink">{r.count}</TD>
                    <TD className="font-mono tabular-nums text-muted">{fmtTime(r.receiveTime)}</TD>
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

// 필터 입력 행 — 헤더 아래. 값 변경 시 부모 filters 갱신.
function FilterRow({
  filters,
  onChange,
}: {
  filters: { bizDay: string; batch: string; count: string; receiveTime: string }
  onChange: (next: typeof filters) => void
}) {
  const set = (k: keyof typeof filters) => (e: ChangeEvent<HTMLInputElement>) =>
    onChange({ ...filters, [k]: e.target.value })
  return (
    <tr className="bg-panel">
      <th className="px-3 py-1" />
      <FilterCell value={filters.bizDay} onChange={set('bizDay')} placeholder="날짜" />
      <FilterCell value={filters.batch} onChange={set('batch')} placeholder="배치" />
      <FilterCell value={filters.count} onChange={set('count')} placeholder="수량" align="right" />
      <FilterCell value={filters.receiveTime} onChange={set('receiveTime')} placeholder="시간" />
    </tr>
  )
}

export function FilterCell({
  value,
  onChange,
  placeholder,
  align,
}: {
  value: string
  onChange: (e: ChangeEvent<HTMLInputElement>) => void
  placeholder: string
  align?: 'right'
}) {
  return (
    <th className="px-2 pb-1.5 pt-0.5 font-normal">
      <input
        value={value}
        onChange={onChange}
        placeholder={placeholder}
        className={cn(
          'w-full rounded border border-line bg-panel px-1.5 py-0.5 text-[11px] font-normal text-ink placeholder:text-faint/70',
          'focus-visible:outline-1 focus-visible:outline-ink',
          align === 'right' && 'text-right',
        )}
      />
    </th>
  )
}

function includes(haystack: string, needle: string): boolean {
  if (!needle) return true
  return haystack.toLowerCase().includes(needle.toLowerCase())
}
