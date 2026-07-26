import { Fragment, useEffect, useState } from 'react'
import {
  type ColumnDef,
  flexRender,
  getCoreRowModel,
  getExpandedRowModel,
  useReactTable,
} from '@tanstack/react-table'
import { ChevronRight } from 'lucide-react'
import { useBatches, useOrders, useOrderItems } from '@/lib/queries'
import type { OrderProgress } from '@/lib/api'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Table, THead, TBody, TR, TH, TD } from '@/components/ui/table'
import { Badge } from '@/components/ui/badge'
import { Select } from '@/components/ui/select'
import { ProgressBar } from '@/components/ui/meter'
import { LoadingRow, ErrorRow, EmptyRow } from '@/components/StateMessage'
import { statusTone } from '@/lib/status'
import { dash } from '@/lib/format'
import { cn } from '@/lib/utils'

const ORDER_STATUSES = ['RUNNING', 'WAITING', 'COMPLETED', 'CANCELLED']

// ── A. 작업 데이터 — 배치 선택 → 오더 테이블 → 행 확장 → 오더아이템 ────────────
export function WorkDataSection() {
  const { data: batches, isLoading: batchesLoading, isError: batchesError } = useBatches()
  const [batchId, setBatchId] = useState<number | undefined>(undefined)
  const [statusFilter, setStatusFilter] = useState<string>('')

  // 배치 도착 시 첫 배치를 기본 선택.
  useEffect(() => {
    if (batchId === undefined && batches && batches.length > 0) {
      setBatchId(batches[0].id)
    }
  }, [batches, batchId])

  const { data: orders, isLoading, isError, error } = useOrders(batchId, statusFilter || undefined)

  const columns: ColumnDef<OrderProgress>[] = [
    {
      id: 'expander',
      header: () => null,
      cell: ({ row }) => (
        <button
          onClick={row.getToggleExpandedHandler()}
          className="flex size-6 items-center justify-center rounded text-faint hover:bg-line/50 hover:text-ink"
          aria-label={row.getIsExpanded() ? '접기' : '아이템 펼치기'}
        >
          <ChevronRight
            className={cn('size-4 transition-transform', row.getIsExpanded() && 'rotate-90')}
          />
        </button>
      ),
    },
    {
      header: '오더번호',
      accessorKey: 'orderNo',
      cell: ({ getValue }) => <span className="font-mono text-ink">{getValue<string>()}</span>,
    },
    { header: '타입', accessorKey: 'orderType', cell: ({ getValue }) => <span className="text-muted">{getValue<string>()}</span> },
    {
      header: '슈트',
      accessorKey: 'destinationChuteNo',
      cell: ({ getValue }) => (
        <span className="font-mono tabular-nums text-muted">{dash(getValue<number | null>())}</span>
      ),
    },
    {
      header: '상태',
      accessorKey: 'status',
      cell: ({ getValue }) => {
        const s = getValue<string>()
        return <Badge tone={statusTone(s)}>{s}</Badge>
      },
    },
    {
      header: '진행 (분류/계획)',
      id: 'progress',
      cell: ({ row }) => (
        <ProgressBar
          planned={row.original.plannedQty}
          reserved={row.original.reservedQty}
          sorted={row.original.sortedQty}
        />
      ),
    },
  ]

  const table = useReactTable({
    data: orders ?? [],
    columns,
    getCoreRowModel: getCoreRowModel(),
    getRowCanExpand: () => true,
    getExpandedRowModel: getExpandedRowModel(),
  })

  return (
    // 뷰포트 맞춤(S-UI-LAYOUT) — 카드가 탭 본문을 채우고(flex-1 min-h-0), 헤더/필터는 고정, 테이블만 스크롤.
    <Card className="flex min-h-0 flex-1 flex-col">
      <CardHeader className="shrink-0">
        <CardTitle>작업 데이터 · 오더 진행</CardTitle>
        <div className="flex items-center gap-2">
          <label className="text-[12px] text-faint">배치</label>
          <Select
            value={batchId ?? ''}
            onChange={(e) => setBatchId(Number(e.target.value))}
            disabled={batchesLoading || !batches?.length}
          >
            {batches?.map((b) => (
              <option key={b.id} value={b.id}>
                {b.workDate} · {b.batchNo} (W{b.waveNo}) — {b.status}
              </option>
            ))}
          </Select>
          <label className="ml-2 text-[12px] text-faint">상태</label>
          <Select value={statusFilter} onChange={(e) => setStatusFilter(e.target.value)}>
            <option value="">전체</option>
            {ORDER_STATUSES.map((s) => (
              <option key={s} value={s}>{s}</option>
            ))}
          </Select>
        </div>
      </CardHeader>
      <CardContent className="min-h-0 flex-1 overflow-auto p-0">
        {batchesError && <ErrorRow message="배치 목록 조회 실패" />}
        {isLoading && <LoadingRow label="오더 불러오는 중" />}
        {isError && <ErrorRow message={(error as Error)?.message ?? '오더 조회 실패'} />}
        {!isLoading && !isError && (orders?.length ?? 0) === 0 && (
          <EmptyRow label="이 배치에 표시할 오더가 없습니다" />
        )}
        {!isLoading && !isError && (orders?.length ?? 0) > 0 && (
          <Table>
            <THead>
              {table.getHeaderGroups().map((hg) => (
                <tr key={hg.id}>
                  {hg.headers.map((h) => (
                    <TH key={h.id}>
                      {h.isPlaceholder ? null : flexRender(h.column.columnDef.header, h.getContext())}
                    </TH>
                  ))}
                </tr>
              ))}
            </THead>
            <TBody>
              {table.getRowModel().rows.map((row) => (
                <Fragment key={row.id}>
                  <TR className={cn(row.getIsExpanded() && 'bg-elevated/60')}>
                    {row.getVisibleCells().map((cell) => (
                      <TD key={cell.id}>{flexRender(cell.column.columnDef.cell, cell.getContext())}</TD>
                    ))}
                  </TR>
                  {row.getIsExpanded() && (
                    <tr className="bg-base/50">
                      <td colSpan={row.getVisibleCells().length} className="px-4 py-3">
                        <OrderItemsSubRow orderId={row.original.id} />
                      </td>
                    </tr>
                  )}
                </Fragment>
              ))}
            </TBody>
          </Table>
        )}
      </CardContent>
    </Card>
  )
}

// 확장 행 — 해당 오더의 order_item (E3). 확장 시에만 마운트되어 조회.
function OrderItemsSubRow({ orderId }: { orderId: number }) {
  const { data: items, isLoading, isError } = useOrderItems(orderId)

  if (isLoading) return <LoadingRow label="아이템 불러오는 중" />
  if (isError) return <ErrorRow message="오더아이템 조회 실패" />
  if (!items || items.length === 0) return <EmptyRow label="아이템 없음" />

  return (
    <div className="rounded-[14px] border border-line bg-panel">
      <div className="px-3 py-1.5 text-[11px] font-semibold uppercase tracking-wider text-faint">
        오더아이템 ({items.length})
      </div>
      <Table>
        <THead>
          <tr>
            <TH>바코드</TH>
            <TH className="text-right">계획</TH>
            <TH className="text-right">예약</TH>
            <TH className="text-right">분류</TH>
          </tr>
        </THead>
        <TBody>
          {items.map((it) => (
            <TR key={it.id}>
              <TD className="font-mono text-ink">{it.barcode}</TD>
              <TD className="text-right font-mono tabular-nums text-muted">{it.plannedQty}</TD>
              <TD className="text-right font-mono tabular-nums text-busy">{it.reservedQty}</TD>
              <TD className="text-right font-mono tabular-nums text-online">{it.sortedQty}</TD>
            </TR>
          ))}
        </TBody>
      </Table>
    </div>
  )
}
