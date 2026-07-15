import { useState } from 'react'
import { type ColumnDef } from '@tanstack/react-table'
import { useInFlight } from '@/lib/queries'
import type { InFlightPiece } from '@/lib/api'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Badge } from '@/components/ui/badge'
import { DataGrid } from '@/components/DataGrid'
import { CursorPager } from '@/components/CursorPager'
import { LoadingRow, ErrorRow, EmptyRow } from '@/components/StateMessage'
import { statusTone } from '@/lib/status'
import { dash, fmtTime } from '@/lib/format'

// ── B. 로봇 이동중(in-flight piece) — 상태 QUERIED/RESERVED/PERMITTED, 최신순, 커서 페이징 ──
export function InFlightSection() {
  // 커서 스택 — [null]=1페이지, push=다음, pop=이전.
  const [stack, setStack] = useState<(number | null)[]>([null])
  const cursor = stack[stack.length - 1]
  const { data, isLoading, isError, error, isFetching } = useInFlight(cursor)

  const columns: ColumnDef<InFlightPiece>[] = [
    { header: 'pId', accessorKey: 'pId', cell: ({ getValue }) => <span className="font-mono tabular-nums text-ink">{getValue<number>()}</span> },
    { header: '바코드', accessorKey: 'barcode', cell: ({ getValue }) => <span className="font-mono text-ink">{getValue<string>()}</span> },
    { header: '수량', accessorKey: 'qty', meta: { align: 'right' }, cell: ({ getValue }) => <span className="font-mono tabular-nums text-muted">{getValue<number>()}</span> },
    { header: '슈트', accessorKey: 'destinationChuteNo', cell: ({ getValue }) => <span className="font-mono tabular-nums text-muted">{dash(getValue<number | null>())}</span> },
    { header: 'AGV', accessorKey: 'agvNo', cell: ({ getValue }) => <span className="font-mono tabular-nums text-muted">{dash(getValue<number | null>())}</span> },
    { header: '인덕션', accessorKey: 'inductionNo', cell: ({ getValue }) => <span className="font-mono tabular-nums text-muted">{dash(getValue<number | null>())}</span> },
    { header: '상태', accessorKey: 'status', cell: ({ getValue }) => { const s = getValue<string>(); return <Badge tone={statusTone(s)}>{s}</Badge> } },
    { header: '등록', accessorKey: 'createdAt', cell: ({ getValue }) => <span className="font-mono tabular-nums text-muted">{fmtTime(getValue<string>())}</span> },
  ]

  const items = data?.items ?? []

  return (
    // 뷰포트 맞춤(S-UI-LAYOUT) — 카드가 탭 본문을 채우고, 헤더/페이저는 고정 크롬, 그리드 본문만 스크롤.
    <Card className="flex min-h-0 flex-1 flex-col">
      <CardHeader className="shrink-0">
        <CardTitle>로봇 이동중 · in-flight piece</CardTitle>
        <Badge tone="busy" dot>수령~투입 대기</Badge>
      </CardHeader>
      <CardContent className="min-h-0 flex-1 overflow-auto p-0">
        {isLoading && <LoadingRow label="이동중 piece 불러오는 중" />}
        {isError && <ErrorRow message={(error as Error)?.message ?? '조회 실패'} />}
        {!isLoading && !isError && items.length === 0 && (
          <EmptyRow label="현재 이동중인 piece가 없습니다" />
        )}
        {!isLoading && !isError && items.length > 0 && <DataGrid columns={columns} data={items} />}
      </CardContent>
      {!isLoading && !isError && (
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
