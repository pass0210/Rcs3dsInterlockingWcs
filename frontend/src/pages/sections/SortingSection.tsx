import { useEffect, useState } from 'react'
import { type ColumnDef } from '@tanstack/react-table'
import { useCells, useSorterCommands, useSorters } from '@/lib/queries'
import type { CellStatus, SorterCommand } from '@/lib/api'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Badge } from '@/components/ui/badge'
import { Select } from '@/components/ui/select'
import { CapacityMeter } from '@/components/ui/meter'
import { DataGrid } from '@/components/DataGrid'
import { CursorPager } from '@/components/CursorPager'
import { LoadingRow, ErrorRow, EmptyRow } from '@/components/StateMessage'
import { statusTone } from '@/lib/status'
import { dash, fmtTime } from '@/lib/format'
import { cn } from '@/lib/utils'

// ── C. 분류 현황 — 소터 선택 → 셀 현황(색상 태그) + sorter_command 적재 이력 ──────
export function SortingSection() {
  const { data: sorters, isLoading: sortersLoading } = useSorters()
  const [destId, setDestId] = useState<number | null>(null)

  useEffect(() => {
    if (destId === null && sorters && sorters.length > 0) setDestId(sorters[0].destId)
  }, [sorters, destId])

  return (
    <div className="flex flex-col gap-4">
      <div className="flex items-center gap-2">
        <label className="text-[12px] text-faint">소터</label>
        <Select
          value={destId ?? ''}
          onChange={(e) => setDestId(Number(e.target.value))}
          disabled={sortersLoading || !sorters?.length}
        >
          {sorters?.map((s) => (
            <option key={s.destId} value={s.destId}>
              3DS #{String(s.chuteNo).padStart(2, '0')} {s.online ? '(온라인)' : '(오프라인)'}
            </option>
          ))}
        </Select>
        {!sortersLoading && !sorters?.length && (
          <span className="text-[12px] text-faint">등록된 소터가 없습니다</span>
        )}
      </div>

      <CellsCard destId={destId} />
      <CommandsCard destId={destId} />
    </div>
  )
}

// ── 셀 현황 — 색상 태그 그리드 ──────────────────────────────────────────────
function CellsCard({ destId }: { destId: number | null }) {
  const { data: cells, isLoading, isError, error } = useCells(destId)

  return (
    <Card>
      <CardHeader>
        <CardTitle>셀 현황</CardTitle>
        <div className="flex items-center gap-2">
          <Badge tone="online" dot>여유</Badge>
          <Badge tone="busy" dot>근접</Badge>
          <Badge tone="warn" dot>만재</Badge>
          <Badge tone="neutral" dot>비활성</Badge>
        </div>
      </CardHeader>
      <CardContent>
        {(destId === null || isLoading) && <LoadingRow label="셀 현황 불러오는 중" />}
        {isError && <ErrorRow message={(error as Error)?.message ?? '셀 조회 실패'} />}
        {destId !== null && !isLoading && !isError && (cells?.length ?? 0) === 0 && (
          <EmptyRow label="이 소터에 등록된 셀이 없습니다" />
        )}
        {destId !== null && !isLoading && !isError && (cells?.length ?? 0) > 0 && (
          // 물리 배치 미러링: 5열 고정(현장 소터 20셀 = 4행×5열). 좁은 폭에서는 컨테이너가
          // 가로 스크롤하고 그리드는 최소폭을 유지해 5열이 깨지지 않는다(타일 뭉개짐 방지).
          <div className="overflow-x-auto">
            <div className="grid grid-cols-5 gap-2 min-w-[600px]">
              {cells!.map((c) => <CellTile key={c.cellNo} cell={c} />)}
            </div>
          </div>
        )}
      </CardContent>
    </Card>
  )
}

function CellTile({ cell }: { cell: CellStatus }) {
  const full = cell.capacity !== null && cell.capacity > 0 && cell.currentQty >= cell.capacity
  const near = !full && cell.capacity !== null && cell.capacity > 0 && cell.currentQty / cell.capacity >= 0.75
  const border = !cell.enabled
    ? 'border-line/60 opacity-60'
    : full
      ? 'border-warn/40'
      : near
        ? 'border-busy/40'
        : cell.occupied
          ? 'border-online/30'
          : 'border-line'

  return (
    <div className={cn('rounded-[14px] border bg-panel p-2.5', border)}>
      <div className="flex items-center justify-between">
        <span className="font-mono text-[13px] font-semibold text-ink">
          셀 {String(cell.cellNo).padStart(2, '0')}
        </span>
        {!cell.enabled ? (
          <Badge tone="neutral">비활성</Badge>
        ) : full ? (
          <Badge tone="warn">만재</Badge>
        ) : cell.occupied ? (
          <Badge tone="online">점유</Badge>
        ) : (
          <Badge tone="neutral">여유</Badge>
        )}
      </div>
      <div className="mt-2">
        <CapacityMeter current={cell.currentQty} capacity={cell.capacity} />
      </div>
      <div className="mt-1.5 truncate font-mono text-[11px] text-muted" title={cell.assignedOrderNo ?? ''}>
        {cell.assignedOrderNo ? `▸ ${cell.assignedOrderNo}` : '미배정'}
      </div>
    </div>
  )
}

// ── sorter_command 적재 이력 — 최신순, 커서 페이징 ──────────────────────────
function CommandsCard({ destId }: { destId: number | null }) {
  const [stack, setStack] = useState<(number | null)[]>([null])
  const cursor = stack[stack.length - 1]
  const { data, isLoading, isError, error, isFetching } = useSorterCommands(destId, cursor)

  // 소터 변경 시 페이지 초기화.
  useEffect(() => { setStack([null]) }, [destId])

  const columns: ColumnDef<SorterCommand>[] = [
    { header: 'pId', accessorKey: 'pId', cell: ({ getValue }) => <span className="font-mono tabular-nums text-ink">{dash(getValue<number | null>())}</span> },
    { header: '바코드', accessorKey: 'barcode', cell: ({ getValue }) => <span className="font-mono text-muted">{dash(getValue<string | null>())}</span> },
    { header: '셀', accessorKey: 'cellNo', cell: ({ getValue }) => <span className="font-mono tabular-nums text-muted">{getValue<number>()}</span> },
    { header: 'C_Seq', accessorKey: 'cSeq', cell: ({ getValue }) => <span className="font-mono tabular-nums text-muted">{getValue<number>()}</span> },
    { header: 'R_Seq', accessorKey: 'rSeq', cell: ({ getValue }) => <span className="font-mono tabular-nums text-muted">{dash(getValue<number | null>())}</span> },
    { header: '상태', accessorKey: 'status', cell: ({ getValue }) => { const s = getValue<string>(); return <Badge tone={statusTone(s)}>{s}</Badge> } },
    { header: 'C 기입', accessorKey: 'cWrittenAt', cell: ({ getValue }) => <span className="font-mono tabular-nums text-muted">{fmtTime(getValue<string>())}</span> },
    { header: 'R 수신', accessorKey: 'rFlagAt', cell: ({ getValue }) => <span className="font-mono tabular-nums text-muted">{fmtTime(getValue<string | null>())}</span> },
  ]

  const items = data?.items ?? []

  return (
    <Card>
      <CardHeader>
        <CardTitle>적재 이력 · sorter_command</CardTitle>
      </CardHeader>
      <CardContent className="p-0">
        {(destId === null || isLoading) && <LoadingRow label="적재 이력 불러오는 중" />}
        {isError && <ErrorRow message={(error as Error)?.message ?? '조회 실패'} />}
        {destId !== null && !isLoading && !isError && items.length === 0 && (
          <EmptyRow label="적재 이력이 없습니다" />
        )}
        {destId !== null && !isLoading && !isError && items.length > 0 && (
          <DataGrid columns={columns} data={items} />
        )}
      </CardContent>
      {destId !== null && !isLoading && !isError && (
        <CursorPager
          page={stack.length}
          count={items.length}
          hasPrev={stack.length > 1}
          hasNext={data?.nextCursor != null}
          onPrev={() => setStack((s) => s.slice(0, -1))}
          onNext={() => data?.nextCursor != null && setStack((s) => [...s, data.nextCursor])}
          fetching={isFetching}
        />
      )}
    </Card>
  )
}
