import { useEffect, useMemo, useState } from 'react'
import { Package, PackageOpen } from 'lucide-react'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Table, THead, TBody, TR, TH, TD } from '@/components/ui/table'
import { Badge } from '@/components/ui/badge'
import { SearchInput } from '@/components/ui/search-input'
import { LoadingRow, ErrorRow, EmptyRow } from '@/components/StateMessage'
import { useBoxes, type BoxRow } from '@/lib/logs'
import { useUiMode } from '@/lib/uiMode'
import { fmtTime, dash } from '@/lib/format'
import { cn } from '@/lib/utils'

// ════════════════════════════════════════════════════════════════════════════
// 박스 조회(/boxes) — 마스터(박스 목록)→디테일(내품) master-detail(계약 §F). E6 GET /api/boxes.
//   전역 bizDay(필수) + 선택적 batch 필터. 좌측 행 클릭 → 우측 내품(items) 표시.
//   batch 변경 시 선택 해제(좌우 불일치 방지). items 는 BoxRow 에 중첩(별도 조회 없음).
// ════════════════════════════════════════════════════════════════════════════
export function BoxesPage() {
  const { bizDay, autoRefresh, refreshInterval } = useUiMode()
  const interval = autoRefresh ? refreshInterval : (false as const)

  const [batch, setBatch] = useState('')
  const [search, setSearch] = useState('')
  const [selectedId, setSelectedId] = useState<number | null>(null)

  const q = useBoxes(bizDay, batch.trim() || undefined, interval)
  const rows = useMemo(() => q.data ?? [], [q.data])

  // batch·bizDay 변경 시 선택 해제(좌측 목록 갱신 ↔ 우측 디테일 불일치 방지).
  useEffect(() => {
    setSelectedId(null)
  }, [batch, bizDay])

  const filtered = useMemo(() => {
    const s = search.trim().toLowerCase()
    if (!s) return rows
    return rows.filter((b) =>
      [b.boxNo, b.chuteNo, b.batch, fmtTime(b.createdAt), b.endTime ?? ''].join(' ').toLowerCase().includes(s),
    )
  }, [rows, search])

  const selected = selectedId != null ? (rows.find((b) => b.id === selectedId) ?? null) : null
  const error = q.isError ? ((q.error as Error)?.message ?? '박스 조회 실패') : null

  return (
    <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,1.5fr)_minmax(0,1fr)]">
      {/* 좌: 박스 목록(마스터) */}
      <Card className="flex min-w-0 flex-col">
        <CardHeader className="flex-wrap gap-2">
          <CardTitle>박스 목록</CardTitle>
          <div className="flex flex-wrap items-center gap-2">
            <input
              value={batch}
              onChange={(e) => setBatch(e.target.value)}
              placeholder="배치 필터(선택)"
              maxLength={10}
              aria-label="배치 필터"
              className="h-8 w-32 rounded-lg border border-line bg-panel px-2.5 text-[13px] text-ink placeholder:text-faint/70 focus-visible:outline-2 focus-visible:outline-ink"
            />
            <SearchInput value={search} onChange={setSearch} className="w-44" placeholder="통합 검색" />
          </div>
        </CardHeader>
        <CardContent className="max-h-[calc(100vh-220px)] min-w-0 overflow-auto p-0">
          {q.isLoading && rows.length === 0 ? (
            <LoadingRow label="박스 불러오는 중" />
          ) : error ? (
            <ErrorRow message={error} />
          ) : rows.length === 0 ? (
            <EmptyRow label="이 업무일자에 표시할 박스가 없습니다" />
          ) : filtered.length === 0 ? (
            <EmptyRow label="검색에 맞는 박스가 없습니다" />
          ) : (
            <Table>
              <THead>
                <tr>
                  <TH>박스번호</TH>
                  <TH className="text-right">슈트</TH>
                  <TH>마감시간</TH>
                  <TH>생성시각</TH>
                  <TH className="text-right">내품수</TH>
                </tr>
              </THead>
              <TBody>
                {filtered.map((b) => {
                  const isSelected = b.id === selectedId
                  return (
                    <TR
                      key={b.id}
                      onClick={() => setSelectedId(b.id)}
                      className={cn('cursor-pointer', isSelected && 'bg-accent/10 hover:bg-accent/10')}
                    >
                      <TD className="font-mono text-ink">{b.boxNo}</TD>
                      <TD className="text-right font-mono tabular-nums text-muted">{dash(b.chuteNo)}</TD>
                      <TD className="text-muted">{dash(b.endTime)}</TD>
                      <TD className="font-mono tabular-nums text-muted">{fmtTime(b.createdAt)}</TD>
                      <TD className="text-right font-mono tabular-nums text-ink">{b.items.length}</TD>
                    </TR>
                  )
                })}
              </TBody>
            </Table>
          )}
        </CardContent>
      </Card>

      {/* 우: 내품(디테일) */}
      <Card className="flex min-w-0 flex-col self-start">
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Package className="size-4 text-muted" />
            {selected ? `박스 ${selected.boxNo} 내품` : '내품'}
          </CardTitle>
          {selected && (
            <Badge tone="neutral" className="tabular-nums">
              {selected.items.length}건
            </Badge>
          )}
        </CardHeader>
        <CardContent className="max-h-[calc(100vh-220px)] min-w-0 overflow-auto p-0">
          <BoxDetail box={selected} />
        </CardContent>
      </Card>
    </div>
  )
}

// ── 선택 박스 내품 — 미선택 시 안내, 빈 박스 시 EmptyRow ─────────────────────────
function BoxDetail({ box }: { box: BoxRow | null }) {
  if (!box) {
    return (
      <div className="flex flex-col items-center justify-center gap-2 px-4 py-14 text-[13px] text-faint">
        <PackageOpen className="size-6" />
        박스를 선택하세요
      </div>
    )
  }
  if (box.items.length === 0) {
    return <EmptyRow label="이 박스에 내품이 없습니다" />
  }
  return (
    <Table>
      <THead>
        <tr>
          <TH>바코드</TH>
          <TH className="text-right">수량</TH>
        </tr>
      </THead>
      <TBody>
        {box.items.map((it, i) => (
          <TR key={`${it.barcode} ${i}`}>
            <TD className="font-mono text-ink">{it.barcode}</TD>
            <TD className="text-right font-mono tabular-nums text-muted">{it.qty}</TD>
          </TR>
        ))}
      </TBody>
    </Table>
  )
}
