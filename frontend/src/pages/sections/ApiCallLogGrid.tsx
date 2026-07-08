import { useMemo, useState, type ChangeEvent } from 'react'
import { Table, THead, TBody, TR, TH, TD } from '@/components/ui/table'
import { Badge } from '@/components/ui/badge'
import { LoadingRow, ErrorRow, EmptyRow } from '@/components/StateMessage'
import { FilterCell } from './SummaryGrid'
import type { ApiCallLogRow } from '@/lib/logs'
import { fmtTime, dash } from '@/lib/format'

// ── API 호출 이력 그리드(§9.6) — RCS↔WCS 왕복 로그. 긴 req/res 본문은 폭·높이 제한 스크롤 셀 ─────
//   컬럼: 호출시각·메서드·엔드포인트·상태코드·소요(ms)·client ip·요청본문·응답본문·에러.

type ApiFilters = {
  endpoint: string
  httpMethod: string
  httpStatusCode: string
  clientIp: string
}

const EMPTY_FILTERS: ApiFilters = {
  endpoint: '',
  httpMethod: '',
  httpStatusCode: '',
  clientIp: '',
}

const COL_COUNT = 9

export function ApiCallLogGrid({
  rows,
  loading,
  error,
  search,
}: {
  rows: ApiCallLogRow[]
  loading: boolean
  error: string | null
  search: string
}) {
  const [filters, setFilters] = useState<ApiFilters>(EMPTY_FILTERS)

  const filtered = useMemo(() => {
    const f = filters
    const s = search.trim().toLowerCase()
    return rows.filter((r) => {
      if (!includes(r.endpoint, f.endpoint)) return false
      if (!includes(r.httpMethod, f.httpMethod)) return false
      if (!includes(String(r.httpStatusCode), f.httpStatusCode)) return false
      if (!includes(r.clientIp ?? '', f.clientIp)) return false
      if (s && !haystack(r).includes(s)) return false
      return true
    })
  }, [rows, filters, search])

  if (loading && rows.length === 0) return <LoadingRow label="API 호출 이력 불러오는 중" />
  if (error) return <ErrorRow message={error} />
  if (rows.length === 0) return <EmptyRow label="표시할 API 호출 이력이 없습니다" />

  const set = (k: keyof ApiFilters) => (e: ChangeEvent<HTMLInputElement>) =>
    setFilters((prev) => ({ ...prev, [k]: e.target.value }))

  return (
    <Table>
      <THead>
        <tr>
          <TH>호출시각</TH>
          <TH>메서드</TH>
          <TH>엔드포인트</TH>
          <TH>상태</TH>
          <TH className="text-right">소요</TH>
          <TH>client ip</TH>
          <TH>요청 본문</TH>
          <TH>응답 본문</TH>
          <TH>에러</TH>
        </tr>
        <tr className="bg-panel">
          <th className="px-3 py-1" />
          <FilterCell value={filters.httpMethod} onChange={set('httpMethod')} placeholder="메서드" />
          <FilterCell value={filters.endpoint} onChange={set('endpoint')} placeholder="엔드포인트" />
          <FilterCell value={filters.httpStatusCode} onChange={set('httpStatusCode')} placeholder="코드" />
          <th className="px-3 py-1" />
          <FilterCell value={filters.clientIp} onChange={set('clientIp')} placeholder="ip" />
          <th className="px-3 py-1" />
          <th className="px-3 py-1" />
          <th className="px-3 py-1" />
        </tr>
      </THead>
      <TBody>
        {filtered.length === 0 ? (
          <tr>
            <td colSpan={COL_COUNT}>
              <EmptyRow label="필터에 맞는 호출 이력이 없습니다" />
            </td>
          </tr>
        ) : (
          filtered.map((r) => (
            <TR key={r.id} className="align-top">
              <TD className="whitespace-nowrap font-mono tabular-nums text-muted">{fmtTime(r.calledAt)}</TD>
              <TD className="font-mono text-muted">{r.httpMethod}</TD>
              <TD className="max-w-[220px] break-all font-mono text-[12px] text-ink">{r.endpoint}</TD>
              <TD>
                <span className="flex items-center gap-1.5">
                  <Badge tone={statusTone(r.httpStatusCode)}>{r.httpStatusCode}</Badge>
                  {r.responseStatus && <span className="text-[11px] text-faint">{r.responseStatus}</span>}
                </span>
              </TD>
              <TD className="whitespace-nowrap text-right font-mono tabular-nums text-muted">{r.durationMs}ms</TD>
              <TD className="font-mono text-muted">{dash(r.clientIp)}</TD>
              <TD>
                <BodyCell text={r.requestBody} />
              </TD>
              <TD>
                <BodyCell text={r.responseBody} />
              </TD>
              <TD className="max-w-[200px] break-all text-[12px] text-offline">{dash(r.errorMessage)}</TD>
            </TR>
          ))
        )}
      </TBody>
    </Table>
  )
}

// 긴 본문 셀 — 폭(max-w) + 높이(max-h overflow) 제한 · monospace · 줄바꿈 보존. 페이지 가로 스크롤 유발 안 함.
function BodyCell({ text }: { text: string | null }) {
  if (!text) return <span className="text-faint">—</span>
  return (
    <div className="max-h-24 max-w-[260px] overflow-auto whitespace-pre-wrap break-all rounded bg-elevated/60 px-1.5 py-1 font-mono text-[11px] leading-snug text-muted">
      {text}
    </div>
  )
}

// 상태코드 → 배지 톤. 2xx=녹 / 4xx=황 / 5xx·기타=적.
function statusTone(code: number): 'online' | 'warn' | 'offline' {
  if (code >= 200 && code < 300) return 'online'
  if (code >= 400 && code < 500) return 'warn'
  return 'offline'
}

function includes(haystackStr: string, needle: string): boolean {
  if (!needle) return true
  return haystackStr.toLowerCase().includes(needle.toLowerCase())
}

function haystack(r: ApiCallLogRow): string {
  return [
    fmtTime(r.calledAt),
    r.httpMethod,
    r.endpoint,
    String(r.httpStatusCode),
    r.responseStatus ?? '',
    r.clientIp ?? '',
    r.requestBody ?? '',
    r.responseBody ?? '',
    r.errorMessage ?? '',
  ]
    .join(' ')
    .toLowerCase()
}
