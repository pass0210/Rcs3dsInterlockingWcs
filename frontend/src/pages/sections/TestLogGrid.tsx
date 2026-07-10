import { useMemo, useState, type ChangeEvent } from 'react'
import { Table, THead, TBody, TR, TH, TD } from '@/components/ui/table'
import { Badge } from '@/components/ui/badge'
import { LoadingRow, ErrorRow, EmptyRow } from '@/components/StateMessage'
import { FilterCell } from './SummaryGrid'
import type { TestLogRow } from '@/lib/logs'
import { fmtTime, dash } from '@/lib/format'

// ── 투입/분류 로그 그리드 — TestLogRow 표시. 컬럼별 텍스트 필터 + (부모)통합검색 결합 ─────────
//   equipmentLabel: 투입=인덕션 / 분류=슈트(E1/E2 동형 형상, 헤더 라벨만 상이).
//   컬럼: 바코드·(인덕션|슈트)·PID·상태·사유·로그시각·등록슈트(파생)·수신시각(파생)·보관.

type LogFilters = {
  barcode: string
  equipmentNo: string
  pid: string
  status: string
  reason: string
  logTime: string
  chuteNo: string
  receiveTime: string
}

const EMPTY_FILTERS: LogFilters = {
  barcode: '',
  equipmentNo: '',
  pid: '',
  status: '',
  reason: '',
  logTime: '',
  chuteNo: '',
  receiveTime: '',
}

const COL_COUNT = 9 // 8 필터 컬럼 + 보관 배지 컬럼

export function TestLogGrid({
  rows,
  loading,
  error,
  equipmentLabel,
  search,
}: {
  rows: TestLogRow[]
  loading: boolean
  error: string | null
  equipmentLabel: string
  search: string
}) {
  const [filters, setFilters] = useState<LogFilters>(EMPTY_FILTERS)

  const filtered = useMemo(() => {
    const f = filters
    const s = search.trim().toLowerCase()
    return rows.filter((r) => {
      // 컬럼별 필터(AND).
      if (!includes(r.barcode, f.barcode)) return false
      if (!includes(r.equipmentNo ?? '', f.equipmentNo)) return false
      if (!includes(r.pid ?? '', f.pid)) return false
      if (!includes(r.status ?? '', f.status)) return false
      if (!includes(r.reason ?? '', f.reason)) return false
      if (!includes(fmtTime(r.logTime), f.logTime)) return false
      if (!includes(r.chuteNo ?? '', f.chuteNo)) return false
      if (!includes(fmtTime(r.receiveTime), f.receiveTime)) return false
      // 통합검색(모든 표시 필드 OR).
      if (s && !haystack(r, equipmentLabel).includes(s)) return false
      return true
    })
  }, [rows, filters, search, equipmentLabel])

  if (loading && rows.length === 0) return <LoadingRow label="로그 불러오는 중" />
  if (error) return <ErrorRow message={error} />
  if (rows.length === 0) return <EmptyRow label="이 업무일자에 표시할 로그가 없습니다" />

  const set = (k: keyof LogFilters) => (e: ChangeEvent<HTMLInputElement>) =>
    setFilters((prev) => ({ ...prev, [k]: e.target.value }))

  return (
    <Table>
      <THead>
        <tr>
          <TH>바코드</TH>
          <TH>{equipmentLabel}</TH>
          <TH>PID</TH>
          <TH>상태</TH>
          <TH>사유</TH>
          <TH>로그시각</TH>
          <TH className="text-right">등록슈트</TH>
          <TH>수신시각</TH>
          <TH>보관</TH>
        </tr>
        <tr className="bg-panel">
          <FilterCell value={filters.barcode} onChange={set('barcode')} placeholder="바코드" />
          <FilterCell value={filters.equipmentNo} onChange={set('equipmentNo')} placeholder={equipmentLabel} />
          <FilterCell value={filters.pid} onChange={set('pid')} placeholder="PID" />
          <FilterCell value={filters.status} onChange={set('status')} placeholder="상태" />
          <FilterCell value={filters.reason} onChange={set('reason')} placeholder="사유" />
          <FilterCell value={filters.logTime} onChange={set('logTime')} placeholder="시각" />
          <FilterCell value={filters.chuteNo} onChange={set('chuteNo')} placeholder="슈트" align="right" />
          <FilterCell value={filters.receiveTime} onChange={set('receiveTime')} placeholder="시각" />
          <th className="px-3 py-1" />
        </tr>
      </THead>
      <TBody>
        {filtered.length === 0 ? (
          <tr>
            <td colSpan={COL_COUNT}>
              <EmptyRow label="필터에 맞는 로그가 없습니다" />
            </td>
          </tr>
        ) : (
          filtered.map((r) => (
            <TR key={r.id}>
              <TD className="font-mono text-ink">{r.barcode}</TD>
              <TD className="font-mono tabular-nums text-muted">{dash(r.equipmentNo)}</TD>
              <TD className="font-mono tabular-nums text-muted">{dash(r.pid)}</TD>
              <TD className="text-muted">{dash(r.status)}</TD>
              <TD className="text-muted">{dash(r.reason)}</TD>
              <TD className="font-mono tabular-nums text-muted">{fmtTime(r.logTime)}</TD>
              <TD className="text-right font-mono tabular-nums text-muted">{dash(r.chuteNo)}</TD>
              <TD className="font-mono tabular-nums text-muted">{fmtTime(r.receiveTime)}</TD>
              <TD>
                {r.archivedAt ? (
                  <Badge tone="neutral" title={fmtTime(r.archivedAt)}>
                    보관
                  </Badge>
                ) : (
                  <Badge tone="online" dot>
                    활성
                  </Badge>
                )}
              </TD>
            </TR>
          ))
        )}
      </TBody>
    </Table>
  )
}

function includes(haystackStr: string, needle: string): boolean {
  if (!needle) return true
  return haystackStr.toLowerCase().includes(needle.toLowerCase())
}

// 통합검색 대상 — 표시되는 모든 필드(포맷 적용값 포함).
function haystack(r: TestLogRow, equipmentLabel: string): string {
  return [
    r.barcode,
    r.equipmentNo ?? '',
    r.pid ?? '',
    r.status ?? '',
    r.reason ?? '',
    fmtTime(r.logTime),
    r.chuteNo ?? '',
    fmtTime(r.receiveTime),
    r.archivedAt ? '보관' : '활성',
    equipmentLabel,
  ]
    .join(' ')
    .toLowerCase()
}
