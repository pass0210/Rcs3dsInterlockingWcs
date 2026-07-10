import { useMemo, useState, type ChangeEvent } from 'react'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Table, THead, TBody, TR, TH, TD } from '@/components/ui/table'
import { Badge } from '@/components/ui/badge'
import { SearchInput } from '@/components/ui/search-input'
import { ArchiveSelect } from '@/components/ArchiveSelect'
import { FilterCell } from './sections/SummaryGrid'
import { LoadingRow, ErrorRow, EmptyRow } from '@/components/StateMessage'
import { useComparison, type ComparisonRow } from '@/lib/logs'
import { useUiMode } from '@/lib/uiMode'
import { fmtTime, dash } from '@/lib/format'
import { cn } from '@/lib/utils'
import type { ArchiveFilter } from '@/lib/testData'

// ════════════════════════════════════════════════════════════════════════════
// 결과 비교(/comparison) — 투입/분류/결과 3-way 표(계약 §E). E5 GET /api/test-data/comparison.
//   시각 구분: 일치=초록 / 불일치=빨강(행 위험색) / 누락=회색 배지·경고색 행 + "누락 셀" 표시.
//   상태 필터 버튼(전체/불일치/누락)+카운트 배지 · 아카이브 필터 · 컬럼 필터 · 통합검색.
// ════════════════════════════════════════════════════════════════════════════

type StatusFilter = 'all' | 'mismatch' | 'missing'

type CmpFilters = {
  batch: string
  barcode: string
  registeredChuteNo: string
  inputStatus: string
  sortChuteNo: string
  sortStatus: string
  resultChuteNo: string
}

const EMPTY_FILTERS: CmpFilters = {
  batch: '',
  barcode: '',
  registeredChuteNo: '',
  inputStatus: '',
  sortChuteNo: '',
  sortStatus: '',
  resultChuteNo: '',
}

const GROUP = 'border-l border-line' // 3-way 그룹 경계 시각 구분
const COL_COUNT = 10

// 상태 필터 술어(계약 §E — 버튼별 독립 필터, 상호 배타 아님).
const isMismatch = (r: ComparisonRow) => r.isMatch === false && r.hasSort && r.hasResult
const isMissing = (r: ComparisonRow) => r.isMissing === true

export function ComparisonPage() {
  const { bizDay, autoRefresh, refreshInterval } = useUiMode()
  const interval = autoRefresh ? refreshInterval : (false as const)

  const [archived, setArchived] = useState<ArchiveFilter>('active')
  const [search, setSearch] = useState('')
  const [status, setStatus] = useState<StatusFilter>('all')
  const [filters, setFilters] = useState<CmpFilters>(EMPTY_FILTERS)

  const q = useComparison(bizDay, archived, interval)
  const rows = useMemo(() => q.data ?? [], [q.data])

  // base = 컬럼 필터 + 통합검색 적용(상태 필터·카운트의 모집단).
  const base = useMemo(() => {
    const f = filters
    const s = search.trim().toLowerCase()
    return rows.filter((r) => {
      if (!includes(r.batch, f.batch)) return false
      if (!includes(r.barcode, f.barcode)) return false
      if (!includes(r.registeredChuteNo, f.registeredChuteNo)) return false
      if (!includes(r.inputStatus ?? '', f.inputStatus)) return false
      if (!includes(r.sortChuteNo ?? '', f.sortChuteNo)) return false
      if (!includes(r.sortStatus ?? '', f.sortStatus)) return false
      if (!includes(r.resultChuteNo ?? '', f.resultChuteNo)) return false
      if (s && !haystack(r).includes(s)) return false
      return true
    })
  }, [rows, filters, search])

  const counts = useMemo(
    () => ({
      all: base.length,
      mismatch: base.filter(isMismatch).length,
      missing: base.filter(isMissing).length,
    }),
    [base],
  )

  const displayed = useMemo(() => {
    if (status === 'mismatch') return base.filter(isMismatch)
    if (status === 'missing') return base.filter(isMissing)
    return base
  }, [base, status])

  const set = (k: keyof CmpFilters) => (e: ChangeEvent<HTMLInputElement>) =>
    setFilters((prev) => ({ ...prev, [k]: e.target.value }))

  const error = q.isError ? ((q.error as Error)?.message ?? '비교 조회 실패') : null

  return (
    <Card className="flex min-w-0 flex-col">
      <CardHeader className="flex-wrap gap-2">
        <CardTitle>결과 비교</CardTitle>
        <div className="flex flex-wrap items-center gap-2">
          <ArchiveSelect value={archived} onChange={setArchived} />
          <SearchInput value={search} onChange={setSearch} className="w-48" placeholder="통합 검색" />
        </div>
      </CardHeader>

      {/* 상태 필터 버튼(카운트 배지) + 범례 — 스크롤 영역 밖 고정 툴바 */}
      <div className="flex flex-wrap items-center justify-between gap-3 border-b border-line px-4 py-2">
        <div className="flex rounded-lg border border-line bg-elevated p-0.5" role="group" aria-label="상태 필터">
          <StatusButton active={status === 'all'} onClick={() => setStatus('all')} label="전체" count={counts.all} tone="ink" />
          <StatusButton active={status === 'mismatch'} onClick={() => setStatus('mismatch')} label="불일치" count={counts.mismatch} tone="offline" />
          <StatusButton active={status === 'missing'} onClick={() => setStatus('missing')} label="누락" count={counts.missing} tone="warn" />
        </div>
        <Legend />
      </div>

      <CardContent className="max-h-[calc(100vh-280px)] min-w-0 overflow-auto p-0">
        {q.isLoading && rows.length === 0 ? (
          <LoadingRow label="비교 불러오는 중" />
        ) : error ? (
          <ErrorRow message={error} />
        ) : rows.length === 0 ? (
          <EmptyRow label="이 업무일자에 비교할 데이터가 없습니다" />
        ) : (
          <Table>
            <THead>
              <tr>
                <TH colSpan={3}>기본</TH>
                <TH colSpan={2} className={cn(GROUP, 'text-center')}>투입</TH>
                <TH colSpan={3} className={cn(GROUP, 'text-center')}>분류</TH>
                <TH colSpan={1} className={cn(GROUP, 'text-center')}>결과</TH>
                <TH colSpan={1} className={cn(GROUP, 'text-center')}>판정</TH>
              </tr>
              <tr>
                <TH>배치</TH>
                <TH>바코드</TH>
                <TH>등록슈트</TH>
                <TH className={GROUP}>상태</TH>
                <TH>시각</TH>
                <TH className={GROUP}>슈트</TH>
                <TH>상태</TH>
                <TH>시각</TH>
                <TH className={GROUP}>슈트</TH>
                <TH className={GROUP}>판정</TH>
              </tr>
              <tr className="bg-panel">
                <FilterCell value={filters.batch} onChange={set('batch')} placeholder="배치" />
                <FilterCell value={filters.barcode} onChange={set('barcode')} placeholder="바코드" />
                <FilterCell value={filters.registeredChuteNo} onChange={set('registeredChuteNo')} placeholder="슈트" />
                <FilterCell value={filters.inputStatus} onChange={set('inputStatus')} placeholder="상태" />
                <th className="px-3 py-1" />
                <FilterCell value={filters.sortChuteNo} onChange={set('sortChuteNo')} placeholder="슈트" />
                <FilterCell value={filters.sortStatus} onChange={set('sortStatus')} placeholder="상태" />
                <th className="px-3 py-1" />
                <FilterCell value={filters.resultChuteNo} onChange={set('resultChuteNo')} placeholder="슈트" />
                <th className="px-3 py-1" />
              </tr>
            </THead>
            <TBody>
              {displayed.length === 0 ? (
                <tr>
                  <td colSpan={COL_COUNT}>
                    <EmptyRow label="조건에 맞는 비교 행이 없습니다" />
                  </td>
                </tr>
              ) : (
                displayed.map((r, i) => {
                  const v = verdict(r)
                  // ComparisonRow 는 id 가 없고 (batch,barcode) 중복 가능(동일 batch 내 바코드 중복 허용
                  // 스키마) → 안정 유니크 키 부재. map 인덱스를 덧붙여 React 중복 키 콘솔 에러(BLOCKING)와
                  // auto-refresh reconciliation glitch 를 차단(BoxesPage 내품 키 패턴과 동형).
                  return (
                    <TR key={`${r.batch}-${r.barcode}-${i}`} className={v.rowClass}>
                      <TD className="font-mono text-muted">{r.batch}</TD>
                      <TD className="font-mono text-ink">{r.barcode}</TD>
                      <TD className="font-mono tabular-nums text-muted">{dash(r.registeredChuteNo)}</TD>

                      {/* 투입 group */}
                      <TD className={cn(GROUP, !r.hasInput && 'bg-warn/5')}>
                        {r.hasInput ? <span className="text-muted">{dash(r.inputStatus)}</span> : <MissingMark />}
                      </TD>
                      <TD className={cn('font-mono tabular-nums text-muted', !r.hasInput && 'bg-warn/5')}>
                        {r.hasInput ? fmtTime(r.inputTime) : ''}
                      </TD>

                      {/* 분류 group */}
                      <TD className={cn(GROUP, 'font-mono tabular-nums text-muted', !r.hasSort && 'bg-warn/5')}>
                        {r.hasSort ? dash(r.sortChuteNo) : <MissingMark />}
                      </TD>
                      <TD className={cn('text-muted', !r.hasSort && 'bg-warn/5')}>
                        {r.hasSort ? dash(r.sortStatus) : ''}
                      </TD>
                      <TD className={cn('font-mono tabular-nums text-muted', !r.hasSort && 'bg-warn/5')}>
                        {r.hasSort ? fmtTime(r.sortTime) : ''}
                      </TD>

                      {/* 결과 group */}
                      <TD className={cn(GROUP, 'font-mono tabular-nums text-muted', !r.hasResult && 'bg-warn/5')}>
                        {r.hasResult ? dash(r.resultChuteNo) : <MissingMark />}
                      </TD>

                      {/* 판정 */}
                      <TD className={GROUP}>
                        <Badge tone={v.tone} dot={v.tone === 'online'}>
                          {v.label}
                        </Badge>
                      </TD>
                    </TR>
                  )
                })
              )}
            </TBody>
          </Table>
        )}
      </CardContent>
    </Card>
  )
}

// 판정 — missing(누락·회색배지/경고행) → mismatch(불일치·빨강) → match(일치·초록) 우선순위.
function verdict(r: ComparisonRow): { label: string; tone: 'online' | 'offline' | 'neutral'; rowClass: string } {
  if (r.isMissing) return { label: '누락', tone: 'neutral', rowClass: 'bg-warn/5 hover:bg-warn/5' }
  if (!r.isMatch) return { label: '불일치', tone: 'offline', rowClass: 'bg-offline/5 hover:bg-offline/5' }
  return { label: '일치', tone: 'online', rowClass: '' }
}

function MissingMark() {
  return <span className="rounded bg-elevated px-1.5 py-0.5 text-[11px] font-medium text-faint">누락</span>
}

function StatusButton({
  active,
  onClick,
  label,
  count,
  tone,
}: {
  active: boolean
  onClick: () => void
  label: string
  count: number
  tone: 'ink' | 'offline' | 'warn'
}) {
  const countClass =
    tone === 'offline'
      ? 'bg-offline/10 text-offline'
      : tone === 'warn'
        ? 'bg-warn/10 text-warn'
        : 'bg-elevated text-muted'
  return (
    <button
      type="button"
      aria-pressed={active}
      onClick={onClick}
      className={cn(
        'flex items-center gap-1.5 rounded-md px-2.5 py-1 text-[12px] font-medium transition-colors',
        active ? 'bg-panel text-ink shadow-card' : 'text-muted hover:text-ink',
      )}
    >
      {label}
      <span className={cn('rounded-full px-1.5 text-[10px] font-semibold tabular-nums', countClass)}>{count}</span>
    </button>
  )
}

function Legend() {
  return (
    <div className="flex items-center gap-2.5 text-[11px] text-faint">
      <Badge tone="online" dot>
        일치
      </Badge>
      <Badge tone="offline">불일치</Badge>
      <Badge tone="neutral">누락</Badge>
    </div>
  )
}

function includes(haystackStr: string, needle: string): boolean {
  if (!needle) return true
  return haystackStr.toLowerCase().includes(needle.toLowerCase())
}

function haystack(r: ComparisonRow): string {
  return [
    r.batch,
    r.barcode,
    r.registeredChuteNo,
    r.inputStatus ?? '',
    fmtTime(r.inputTime),
    r.sortChuteNo ?? '',
    r.sortStatus ?? '',
    fmtTime(r.sortTime),
    r.resultChuteNo ?? '',
    r.isMissing ? '누락' : r.isMatch ? '일치' : '불일치',
  ]
    .join(' ')
    .toLowerCase()
}
