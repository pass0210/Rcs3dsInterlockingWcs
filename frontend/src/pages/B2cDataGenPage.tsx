import { useCallback, useMemo, useState, type FormEvent, type ReactNode } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { Plus, RotateCcw } from 'lucide-react'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { ConfirmDialog } from '@/components/ui/dialog'
import { EmptyRow, ErrorRow, LoadingRow } from '@/components/StateMessage'
import { useToast } from '@/lib/toast'
import { todayBizDay } from '@/lib/uiMode'
import { cn } from '@/lib/utils'
import {
  b2cTestData,
  useB2cDetail,
  useB2cSummary,
  type SorterSummary,
} from '@/lib/b2cTestData'

// ═══════════════════════════════════════════════════════════════════════════
// B2cDataGenPage — B2C(3D 소터) 테스트 데이터 생성·초기화(재테스트 준비) 페이지.
//   좌: 생성 폼(대상 소터·배치·셀·수량·패턴) — 멱등 생성(OQ4).
//   중: 소터 요약(셀/오더/수량/진행중 피스) + 초기화 버튼(danger + force 경로·OQ3).
//   우: 선택 소터의 셀 상세(현재수량·배정 오더).
// docs/B2C-DATAGEN.md. 기존 DataGeneratorPage/ConfirmDialog/useToast/TanStack Query 패턴 재사용.
// ═══════════════════════════════════════════════════════════════════════════

interface PendingAction {
  title: string
  description: ReactNode
  confirmLabel: string
  run: () => Promise<void>
}

export function B2cDataGenPage() {
  const { toast } = useToast()
  const qc = useQueryClient()

  const [selectedChute, setSelectedChute] = useState<number | null>(null)
  const [pending, setPending] = useState<PendingAction | null>(null)
  const [busy, setBusy] = useState(false)

  const summaryQ = useB2cSummary(false)
  const detailQ = useB2cDetail(selectedChute, false)
  const sorters = useMemo(() => summaryQ.data ?? [], [summaryQ.data])
  const cells = useMemo(() => detailQ.data ?? [], [detailQ.data])

  const invalidateAll = useCallback(() => {
    qc.invalidateQueries({ queryKey: ['b2c-summary'] })
    qc.invalidateQueries({ queryKey: ['b2c-detail'] })
  }, [qc])

  // ── 초기화(danger + force 재요청 경로·OQ3) ────────────────────────────────
  const requestForceReset = useCallback(
    (sorter: SorterSummary, inFlight: number) => {
      setPending({
        title: '강제 초기화',
        confirmLabel: '강제 초기화',
        description: (
          <>
            소터 <b>{sorter.chuteNo}</b> 에 진행 중(in-flight) 작업 <b>{inFlight}</b>건이 있습니다.
            <br />
            강제로 초기화하면 진행 중 피스까지 보관 처리됩니다. 계속하시겠습니까?
          </>
        ),
        run: async () => {
          const outcome = await b2cTestData.reset({ sorterChuteNo: sorter.chuteNo, force: true })
          toast(outcome.ok ? 'success' : 'error', outcome.message)
          if (outcome.ok) invalidateAll()
        },
      })
    },
    [toast, invalidateAll],
  )

  const requestReset = useCallback(
    (sorter: SorterSummary) => {
      const inFlight = sorter.inFlightPieces
      setPending({
        title: '테스트 데이터 초기화',
        confirmLabel: '초기화',
        description: (
          <>
            소터 <b>{sorter.chuteNo}</b> 의 재테스트 준비 초기화를 수행합니다.
            <ul className="mt-2 list-disc pl-4 text-[12px] leading-relaxed">
              <li>피스·이력·소터명령을 보관 처리(soft-delete)</li>
              <li>예약/분류 수량을 0으로 리셋</li>
              <li>완료된 오더를 재개(RUNNING) · 오더·셀 배정은 보존</li>
            </ul>
            {inFlight > 0 && (
              <p className="mt-2 font-medium text-offline">
                ⚠ 진행 중(in-flight) 작업 {inFlight}건이 있어 기본 초기화는 거부됩니다(강제 옵션 안내됨).
              </p>
            )}
            <p className="mt-2">이 작업은 되돌릴 수 없습니다.</p>
          </>
        ),
        run: async () => {
          const outcome = await b2cTestData.reset({ sorterChuteNo: sorter.chuteNo, force: false })
          if (outcome.ok) {
            toast('success', outcome.message)
            invalidateAll()
            return
          }
          // 진행 중 거부(counts.inFlight) → force 재요청 다이얼로그로 안내(OQ3).
          const inFlightCount = outcome.counts?.inFlight ?? 0
          if (inFlightCount > 0) {
            requestForceReset(sorter, inFlightCount)
          } else {
            toast('error', outcome.message)
          }
        },
      })
    },
    [toast, invalidateAll, requestForceReset],
  )

  const onConfirm = useCallback(async () => {
    if (!pending) return
    const justRan = pending   // 방금 실행한 액션 캡처 — 후속(강제) 다이얼로그 보존 판정용.
    setBusy(true)
    try {
      await justRan.run()
    } catch (e) {
      toast('error', `작업 실패 — ${(e as Error).message}`)
    } finally {
      setBusy(false)
      // ★ Eval FAIL#1 fix: run() 이 후속 다이얼로그(강제 초기화 재요청)로 pending 을 교체했으면
      //   닫지 않는다 — 무조건 setPending(null) 은 방금 연 force 다이얼로그를 같은 틱에 덮어써
      //   silent no-op(OQ3 force 경로 봉쇄·Fail-Loud 위반)이 된다. 자기 자신일 때만 닫는다.
      setPending((cur) => (cur === justRan ? null : cur))
    }
  }, [pending, toast])

  const closePending = useCallback(() => setPending(null), [])

  return (
    <div className="grid grid-cols-1 gap-4 xl:grid-cols-[340px_minmax(0,1fr)_minmax(0,1fr)]">
      {/* 좌: 생성 폼 */}
      <Card className="self-start">
        <CardHeader>
          <CardTitle>데이터 생성</CardTitle>
        </CardHeader>
        <CardContent>
          <B2cGenerateForm onGenerated={invalidateAll} />
        </CardContent>
      </Card>

      {/* 중: 소터 요약 */}
      <Card className="flex min-w-0 flex-col">
        <CardHeader>
          <CardTitle>소터 요약</CardTitle>
        </CardHeader>
        <CardContent className="min-w-0 overflow-auto p-0">
          {summaryQ.isLoading ? (
            <LoadingRow />
          ) : summaryQ.isError ? (
            <ErrorRow message={(summaryQ.error as Error)?.message ?? '요약 조회 실패'} />
          ) : sorters.length === 0 ? (
            <EmptyRow label="생성된 3D 소터가 없습니다. 좌측에서 데이터를 생성하세요." />
          ) : (
            <div className="flex flex-col divide-y divide-line">
              {sorters.map((s) => (
                <SorterCard
                  key={s.destinationId}
                  sorter={s}
                  selected={selectedChute === s.chuteNo}
                  onSelect={() => setSelectedChute(s.chuteNo)}
                  onReset={() => requestReset(s)}
                />
              ))}
            </div>
          )}
        </CardContent>
      </Card>

      {/* 우: 셀 상세 */}
      <Card className="flex min-w-0 flex-col">
        <CardHeader>
          <CardTitle>셀 상세{selectedChute !== null ? ` — 소터 ${selectedChute}` : ''}</CardTitle>
        </CardHeader>
        <CardContent className="max-h-[calc(100vh-220px)] min-w-0 overflow-auto p-0">
          {selectedChute === null ? (
            <EmptyRow label="요약에서 소터를 선택하면 셀 상세가 표시됩니다." />
          ) : detailQ.isLoading ? (
            <LoadingRow />
          ) : detailQ.isError ? (
            <ErrorRow message={(detailQ.error as Error)?.message ?? '상세 조회 실패'} />
          ) : cells.length === 0 ? (
            <EmptyRow label="셀이 없습니다." />
          ) : (
            <CellTable cells={cells} />
          )}
        </CardContent>
      </Card>

      <ConfirmDialog
        open={pending !== null}
        title={pending?.title ?? ''}
        description={pending?.description}
        confirmLabel={pending?.confirmLabel ?? '확인'}
        busy={busy}
        onConfirm={onConfirm}
        onCancel={closePending}
      />
    </div>
  )
}

// ── 소터 요약 카드(선택·초기화) ──────────────────────────────────────────────
function SorterCard({
  sorter,
  selected,
  onSelect,
  onReset,
}: {
  sorter: SorterSummary
  selected: boolean
  onSelect: () => void
  onReset: () => void
}) {
  return (
    <div
      className={cn(
        'cursor-pointer px-4 py-3 transition-colors hover:bg-elevated',
        selected && 'bg-elevated shadow-[inset_3px_0_0_0_var(--color-brand)]',
      )}
      onClick={onSelect}
    >
      <div className="flex items-center justify-between gap-2">
        <div className="flex items-center gap-2 text-[14px] font-semibold text-ink">
          소터 {sorter.chuteNo}
          <span
            className={cn(
              'rounded border px-1.5 py-0.5 text-[10px] font-medium',
              sorter.status === 'PAUSED' || !sorter.isActive
                ? 'border-offline/40 text-offline'
                : 'border-line text-faint',
            )}
          >
            {sorter.isActive ? sorter.status : 'INACTIVE'}
          </span>
        </div>
        <Button
          variant="outline"
          size="sm"
          onClick={(e) => {
            e.stopPropagation()
            onReset()
          }}
          className="border-offline/50 text-offline hover:bg-offline/5"
        >
          <RotateCcw className="size-4" />
          초기화
        </Button>
      </div>
      <div className="mt-2 grid grid-cols-2 gap-x-4 gap-y-1 text-[12px] text-muted sm:grid-cols-3">
        <Stat label="셀" value={`${sorter.cellEnabled}/${sorter.cellTotal} (배정 ${sorter.cellAssigned})`} />
        <Stat label="오더" value={`${sorter.orderRunning}R / ${sorter.orderCompleted}C / ${sorter.orderTotal}`} />
        <Stat label="진행중 피스" value={String(sorter.inFlightPieces)} highlight={sorter.inFlightPieces > 0} />
        <Stat label="계획" value={String(sorter.plannedSum)} />
        <Stat label="예약" value={String(sorter.reservedSum)} />
        <Stat label="분류" value={String(sorter.sortedSum)} />
      </div>
    </div>
  )
}

function Stat({ label, value, highlight }: { label: string; value: string; highlight?: boolean }) {
  return (
    <div className="flex items-baseline gap-1.5">
      <span className="text-faint">{label}</span>
      <span className={cn('font-mono tabular-nums', highlight ? 'font-semibold text-offline' : 'text-ink')}>
        {value}
      </span>
    </div>
  )
}

// ── 셀 상세 테이블 ────────────────────────────────────────────────────────────
function CellTable({ cells }: { cells: import('@/lib/b2cTestData').CellDetail[] }) {
  return (
    <table className="w-full text-[12px]">
      <thead className="sticky top-0 bg-panel text-left text-faint">
        <tr className="border-b border-line">
          <th className="px-3 py-2 font-medium">셀</th>
          <th className="px-3 py-2 font-medium">용량</th>
          <th className="px-3 py-2 font-medium">현재</th>
          <th className="px-3 py-2 font-medium">배정 오더</th>
          <th className="px-3 py-2 font-medium">예약/분류</th>
          <th className="px-3 py-2 font-medium">상태</th>
        </tr>
      </thead>
      <tbody className="divide-y divide-line">
        {cells.map((c) => (
          <tr key={c.cellNo} className="text-ink">
            <td className="px-3 py-1.5 font-mono tabular-nums">{c.cellNo}</td>
            <td className="px-3 py-1.5 font-mono tabular-nums text-muted">{c.capacity ?? '∞'}</td>
            <td className="px-3 py-1.5 font-mono tabular-nums">{c.currentQty}</td>
            <td className="px-3 py-1.5 font-mono">{c.assignedOrderNo ?? '—'}</td>
            <td className="px-3 py-1.5 font-mono tabular-nums text-muted">
              {c.assignedOrderNo ? `${c.reservedQty ?? 0} / ${c.sortedQty ?? 0}` : '—'}
            </td>
            <td className="px-3 py-1.5">
              {c.enabled ? (
                <span className="text-faint">enabled</span>
              ) : (
                <span className="text-offline">disabled</span>
              )}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}

// ── 생성 폼 ───────────────────────────────────────────────────────────────────
function B2cGenerateForm({ onGenerated }: { onGenerated: () => void }) {
  const { toast } = useToast()

  const [sorterChuteNo, setSorterChuteNo] = useState('1')
  const [workDate, setWorkDate] = useState(todayBizDay)
  const [batchNo, setBatchNo] = useState('')
  const [waveNo, setWaveNo] = useState('1')
  const [cellCount, setCellCount] = useState('15')
  const [cellCapacity, setCellCapacity] = useState('3')
  const [plannedQty, setPlannedQty] = useState('3')
  const [orderPrefix, setOrderPrefix] = useState('CELL')
  const [submitting, setSubmitting] = useState(false)

  async function onSubmit(e: FormEvent) {
    e.preventDefault()
    const nums = {
      sorterChuteNo: Number(sorterChuteNo),
      waveNo: Number(waveNo),
      cellCount: Number(cellCount),
      cellCapacity: Number(cellCapacity),
      plannedQty: Number(plannedQty),
    }
    if (!batchNo.trim() || !orderPrefix.trim() || !workDate.trim()) {
      toast('warning', '작업일자·배치·오더 접두를 모두 입력하세요.')
      return
    }
    if (!/^[A-Za-z0-9_-]{1,50}$/.test(orderPrefix.trim())) {
      toast('warning', '오더 접두는 영문·숫자·하이픈·언더스코어만 허용합니다.')
      return
    }
    for (const [k, v] of Object.entries(nums)) {
      if (!Number.isInteger(v) || v < 1) {
        toast('warning', `${k} 는 1 이상의 정수여야 합니다.`)
        return
      }
    }
    if (nums.cellCount > 200) {
      toast('warning', '셀 개수는 200 이하여야 합니다.')
      return
    }

    setSubmitting(true)
    try {
      const outcome = await b2cTestData.generate({
        sorterChuteNo: nums.sorterChuteNo,
        workDate: workDate.trim(),
        batchNo: batchNo.trim(),
        waveNo: nums.waveNo,
        cellCount: nums.cellCount,
        cellCapacity: nums.cellCapacity,
        plannedQty: nums.plannedQty,
        orderPrefix: orderPrefix.trim(),
      })
      toast(outcome.ok ? 'success' : 'error', outcome.message)
      if (outcome.ok) onGenerated()
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <form onSubmit={onSubmit} noValidate className="flex flex-col gap-3">
      <Field label="대상 소터 슈트번호">
        <input value={sorterChuteNo} onChange={(e) => setSorterChuteNo(e.target.value)}
          type="number" min={1} className={inputBase} />
        <p className="mt-1 text-[11px] text-faint">없으면 SORTER_3D 로 생성합니다.</p>
      </Field>
      <Field label="작업일자">
        <input value={workDate} onChange={(e) => setWorkDate(e.target.value)}
          type="date" className={inputBase} />
      </Field>
      <Field label="배치명">
        <input value={batchNo} onChange={(e) => setBatchNo(e.target.value)}
          placeholder="예: FIELD-15" maxLength={100} className={inputBase} />
      </Field>
      <div className="grid grid-cols-2 gap-3">
        <Field label="차수">
          <input value={waveNo} onChange={(e) => setWaveNo(e.target.value)}
            type="number" min={1} className={inputBase} />
        </Field>
        <Field label="셀 개수">
          <input value={cellCount} onChange={(e) => setCellCount(e.target.value)}
            type="number" min={1} max={200} className={inputBase} />
        </Field>
        <Field label="셀 용량">
          <input value={cellCapacity} onChange={(e) => setCellCapacity(e.target.value)}
            type="number" min={1} className={inputBase} />
        </Field>
        <Field label="계획 수량">
          <input value={plannedQty} onChange={(e) => setPlannedQty(e.target.value)}
            type="number" min={1} className={inputBase} />
        </Field>
      </div>
      <Field label="오더/바코드 접두">
        <input value={orderPrefix} onChange={(e) => setOrderPrefix(e.target.value)}
          placeholder="예: 0701-CELL" maxLength={50} className={inputBase} />
        <p className="mt-1 text-[11px] text-faint">오더번호 = 바코드 = &quot;{orderPrefix || '접두'}-NN&quot; (셀 N ↔ 오더 N).</p>
      </Field>
      <Button type="submit" variant="solid" disabled={submitting} className="mt-1 w-full">
        <Plus className="size-4" />
        {submitting ? '생성 중…' : '데이터 생성'}
      </Button>
      <p className="text-[11px] text-faint">
        멱등: 같은 파라미터 재실행 시 카운트 불변(upsert). 소터·셀·오더·배정을 생성합니다.
      </p>
    </form>
  )
}

const inputBase =
  'h-10 w-full rounded-lg border border-line bg-panel px-3 text-[13px] text-ink placeholder:text-faint/70 focus-visible:outline-2 focus-visible:outline-ink'

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="block">
      <span className="mb-1 block text-[12px] font-medium text-muted">{label}</span>
      {children}
    </label>
  )
}
