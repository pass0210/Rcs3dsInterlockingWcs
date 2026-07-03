import {
  type ColumnDef,
  flexRender,
  getCoreRowModel,
  useReactTable,
} from '@tanstack/react-table'
import { Table, THead, TBody, TR, TH, TD } from '@/components/ui/table'

// TanStack Table 기반 밀집 그리드(비확장 목록 — in-flight/sorter-command/cells 공용).
export function DataGrid<T>({ columns, data }: { columns: ColumnDef<T>[]; data: T[] }) {
  const table = useReactTable({ data, columns, getCoreRowModel: getCoreRowModel() })
  return (
    <Table>
      <THead>
        {table.getHeaderGroups().map((hg) => (
          <tr key={hg.id}>
            {hg.headers.map((h) => (
              <TH key={h.id} className={(h.column.columnDef.meta as { align?: string })?.align === 'right' ? 'text-right' : undefined}>
                {h.isPlaceholder ? null : flexRender(h.column.columnDef.header, h.getContext())}
              </TH>
            ))}
          </tr>
        ))}
      </THead>
      <TBody>
        {table.getRowModel().rows.map((row) => (
          <TR key={row.id}>
            {row.getVisibleCells().map((cell) => (
              <TD key={cell.id} className={(cell.column.columnDef.meta as { align?: string })?.align === 'right' ? 'text-right' : undefined}>
                {flexRender(cell.column.columnDef.cell, cell.getContext())}
              </TD>
            ))}
          </TR>
        ))}
      </TBody>
    </Table>
  )
}
