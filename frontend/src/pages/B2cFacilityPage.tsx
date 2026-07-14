import { useCallback, useMemo, useState, type FormEvent, type ReactNode } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { Eraser, LayoutGrid, Link2, Pause, Pencil, Play, Plus, Power, PowerOff, RotateCcw, Unlink } from 'lucide-react'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import { Select } from '@/components/ui/select'
import { ConfirmDialog, Dialog } from '@/components/ui/dialog'
import { EmptyRow, ErrorRow, LoadingRow } from '@/components/StateMessage'
import { useToast } from '@/lib/toast'
import { cn } from '@/lib/utils'
import { useDestinations } from '@/lib/queries'
import type { Destination } from '@/lib/api'
import { b2cFacility, useFacilityOrders, type FacilityOrder } from '@/lib/b2cFacility'
import { b2cTestData } from '@/lib/b2cTestData'
import { ops } from '@/lib/ops'

// ═══════════════════════════════════════════════════════════════════════════
// B2cFacilityPage — B2C 설비 관리(2b). 목적지 구성·소터 셀 설정·오더 할당·슈트 제어·재테스트 초기화.
//   운영자가 브라우저에서 혼합 토폴로지(소터+슈트)를 구성·배정·제어한다. docs/B2C-FACILITY.md.
//   파괴/변경 작업은 확인 다이얼로그 + 작업자 이름(감사 귀속) + operation_log(백엔드) 전수 기록.
//   슈트 제어(clear/pause/resume)는 기존 /api/ops 소비(단일 쓰기 큐·절대규칙 #1은 백엔드가 강제).
// ═══════════════════════════════════════════════════════════════════════════

// 파괴/변경 확인 액션(ConfirmDialog 소비) — run 은 검증된 작업자 이름을 받아 실행·표면화.
interface PendingConfirm {
  title: string
  description: ReactNode
  confirmLabel: string
  danger: boolean
  run: () => Promise<void>
}

export function B2cFacilityPage() {
  const { toast } = useToast()
  const qc = useQueryClient()

  // 작업자 이름 — 파괴/변경 작업의 감사 귀속(공백이면 액션 차단). 페이지 세션 유지.
  const [operatorName, setOperatorName] = useState('')

  const destQ = useDestinations(false)
  const destinations = useMemo(() => destQ.data ?? [], [destQ.data])

  const [pending, setPending] = useState<PendingConfirm | null>(null)
  const [busy, setBusy] = useState(false)
  const [createOpen, setCreateOpen] = useState(false)
  const [cellTarget, setCellTarget] = useState<Destination | null>(null)
  const [editTarget, setEditTarget] = useState<Destination | null>(null)
  const [assignTarget, setAssignTarget] = useState<FacilityOrder | null>(null)

  const invalidate = useCallback(() => {
    qc.invalidateQueries({ queryKey: ['destinations'] })
    qc.invalidateQueries({ queryKey: ['facility-orders'] })
    qc.invalidateQueries({ queryKey: ['b2c-summary'] })
    qc.invalidateQueries({ queryKey: ['b2c-batches'] })
  }, [qc])

  // 작업자 이름 게이트 — 공백이면 경고 후 액션 미개시.
  const requireOperator = useCallback((): string | null => {
    const op = operatorName.trim()
    if (op.length === 0) {
      toast('warning', '작업자 이름을 입력하세요(감사 귀속).')
      return null
    }
    return op
  }, [operatorName, toast])

  const onConfirm = useCallback(async () => {
    if (!pending) return
    const justRan = pending
    setBusy(true)
    try {
      await justRan.run()
    } catch (e) {
      toast('error', `작업 실패 — ${(e as Error).message}`)
    } finally {
      setBusy(false)
      // run() 이 후속 다이얼로그로 교체했으면 보존(force 재요청 체이닝) — 자기 자신일 때만 닫음.
      setPending((cur) => (cur === justRan ? null : cur))
    }
  }, [pending, toast])

  // ── 슈트/소터 제어 ──────────────────────────────────────────────────────────
  const requestPauseToggle = (d: Destination) => {
    const op = requireOperator()
    if (!op) return
    const resume = d.paused
    setPending({
      title: resume ? '목적지 재개' : '목적지 일시정지',
      confirmLabel: resume ? '재개' : '일시정지',
      danger: !resume,
      description: (
        <>
          대상 <b>chuteNo {d.chuteNo}</b>({d.destType}) · 작업자 <b>{op}</b>
          <br />
          {resume ? '이 목적지로의 투입을 다시 허용합니다.' : '신규 투입 지시가 중단됩니다(진행 중 작업은 유지).'}
        </>
      ),
      run: async () => {
        const r = resume ? await ops.resume(d.id, op) : await ops.pause(d.id, op)
        if (!r.ok) {
          toast(r.status === 409 ? 'warning' : 'error', `${resume ? '재개' : '정지'} 실패 — ${r.message}`)
          return
        }
        toast('success', resume ? '재개되었습니다.' : '일시정지되었습니다.')
        invalidate()
      },
    })
  }

  const requestClear = (d: Destination) => {
    const op = requireOperator()
    if (!op) return
    setPending({
      title: '슈트 비움 (Clear)',
      confirmLabel: '비움',
      danger: true,
      description: (
        <>
          슈트 <b>chuteNo {d.chuteNo}</b> 를 비웁니다(만재 집계 리셋 · last_cleared_at 갱신). 작업자 <b>{op}</b>.
        </>
      ),
      run: async () => {
        const r = await ops.clearChute(d.id, op)
        if (!r.ok) {
          toast('error', `슈트 비움 실패 — ${r.message}`)
          return
        }
        toast('success', `슈트 ${d.chuteNo} 비움 완료.`)
        invalidate()
      },
    })
  }

  const requestSetActive = (d: Destination, isActive: boolean, force = false) => {
    const op = requireOperator()
    if (!op) return
    setPending({
      title: isActive ? '목적지 활성화' : force ? '강제 비활성화' : '목적지 비활성화',
      confirmLabel: isActive ? '활성화' : force ? '강제 비활성화' : '비활성화',
      danger: !isActive,
      description: (
        <>
          대상 <b>chuteNo {d.chuteNo}</b>({d.destType}) · 작업자 <b>{op}</b>
          {!isActive && (
            <p className="mt-2 text-[12px] text-offline">
              비활성 목적지는 IF-05 라우팅에서 차단됩니다{force ? ' (진행 중 작업 포함 강제).' : '.'}
            </p>
          )}
        </>
      ),
      run: async () => {
        const r = await b2cFacility.setActive(d.id, isActive, force, op)
        if (r.ok) {
          toast('success', r.message)
          invalidate()
          return
        }
        // 비활성화 거부(진행 중) → 강제 재요청 다이얼로그로 안내(OQ-2).
        const blocking = (r.counts?.inFlight ?? 0) + (r.counts?.activeAssignments ?? 0)
        if (!isActive && !force && blocking > 0) {
          requestSetActive(d, false, true)
        } else {
          toast('error', r.message)
        }
      },
    })
  }

  const requestReset = (d: Destination, force = false) => {
    const op = requireOperator()
    if (!op) return
    setPending({
      title: force ? '강제 초기화' : '재테스트 초기화',
      confirmLabel: force ? '강제 초기화' : '초기화',
      danger: true,
      description: (
        <>
          소터 <b>chuteNo {d.chuteNo}</b> 재테스트 초기화 · 작업자 <b>{op}</b>
          <ul className="mt-2 list-disc pl-4 text-[12px] leading-relaxed">
            <li>피스·이력·소터명령 보관 처리(soft-delete)</li>
            <li>예약/분류 수량 0 리셋 · 완료 오더 재개(오더·셀 배정 보존)</li>
          </ul>
          <p className="mt-2 text-offline">이 작업은 되돌릴 수 없습니다{force ? ' (진행 중 피스까지 보관).' : '.'}</p>
        </>
      ),
      run: async () => {
        const r = await b2cTestData.reset({ sorterChuteNo: d.chuteNo, force })
        if (r.ok) {
          toast('success', r.message)
          invalidate()
          return
        }
        const inFlight = r.counts?.inFlight ?? 0
        if (!force && inFlight > 0) {
          requestReset(d, true)
        } else {
          toast('error', r.message)
        }
      },
    })
  }

  const requestUnassign = (o: FacilityOrder) => {
    const op = requireOperator()
    if (!op) return
    setPending({
      title: '오더 할당 해제',
      confirmLabel: '해제',
      danger: true,
      description: (
        <>
          오더 <b>{o.orderNo}</b> 의 목적지 할당을 해제합니다(미시작 오더만 · OQ-3). 작업자 <b>{op}</b>.
        </>
      ),
      run: async () => {
        const r = await b2cFacility.unassignOrder(o.orderId, op)
        toast(r.ok ? 'success' : 'error', r.message)
        if (r.ok) invalidate()
      },
    })
  }

  return (
    <div className="flex flex-col gap-4">
      {/* 작업자 이름 + 새 목적지 */}
      <Card>
        <CardContent className="flex flex-wrap items-center gap-3 py-3">
          <label className="flex items-center gap-2 text-[12px] font-medium text-ink">
            작업자 이름 <span className="text-offline">*</span>
            <input
              value={operatorName}
              onChange={(e) => setOperatorName(e.target.value)}
              placeholder="예: 홍길동"
              maxLength={100}
              aria-label="작업자 이름"
              className="h-9 w-48 rounded-lg border border-line bg-panel px-3 text-[13px] text-ink focus-visible:outline-2 focus-visible:outline-ink"
            />
          </label>
          <span className="text-[11px] text-faint">파괴/변경 작업의 감사 귀속에 사용됩니다.</span>
          <Button variant="solid" size="sm" className="ml-auto" onClick={() => setCreateOpen(true)}>
            <Plus className="size-4" />새 목적지
          </Button>
        </CardContent>
      </Card>

      {/* 목적지 목록 + 제어 */}
      <Card className="flex min-w-0 flex-col">
        <CardHeader>
          <CardTitle>목적지 구성 · 제어</CardTitle>
          <Badge tone="warn">실 하드웨어 제어</Badge>
        </CardHeader>
        <CardContent className="min-w-0 overflow-auto p-0">
          {destQ.isLoading ? (
            <LoadingRow />
          ) : destQ.isError ? (
            <ErrorRow message={(destQ.error as Error)?.message ?? '목적지 조회 실패'} />
          ) : destinations.length === 0 ? (
            <EmptyRow label="목적지가 없습니다. '새 목적지'로 슈트/소터를 생성하세요." />
          ) : (
            <table className="w-full text-[12px]">
              <thead className="sticky top-0 bg-panel text-left text-faint">
                <tr className="border-b border-line">
                  <th className="px-3 py-2 font-medium">chuteNo</th>
                  <th className="px-3 py-2 font-medium">타입</th>
                  <th className="px-3 py-2 font-medium">상태</th>
                  <th className="px-3 py-2 font-medium">셀 / 만재</th>
                  <th className="px-3 py-2 font-medium">제어</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-line">
                {destinations.map((d) => (
                  <tr key={d.id} className="text-ink">
                    <td className="px-3 py-2 font-mono tabular-nums font-semibold">{d.chuteNo}</td>
                    <td className="px-3 py-2">
                      <Badge tone={d.destType === 'SORTER_3D' ? 'accent' : 'neutral'}>{d.destType}</Badge>
                    </td>
                    <td className="px-3 py-2">
                      <div className="flex flex-wrap gap-1">
                        {!d.isActive && <Badge tone="offline">비활성</Badge>}
                        {d.paused && <Badge tone="warn">정지</Badge>}
                        {d.full && <Badge tone="offline">만재</Badge>}
                        {d.destType === 'SORTER_3D' && (
                          <Badge tone={d.online ? 'online' : 'offline'}>{d.online ? 'online' : 'offline'}</Badge>
                        )}
                        {d.isActive && !d.paused && !d.full && d.destType === 'CHUTE' && (
                          <Badge tone="online">정상</Badge>
                        )}
                      </div>
                    </td>
                    <td className="px-3 py-2 font-mono tabular-nums text-muted">
                      {d.destType === 'SORTER_3D'
                        ? `${d.cellEnabled ?? 0}/${d.cellTotal ?? 0} 셀`
                        : `풀 ${d.workFullQty ?? '—'}`}
                    </td>
                    <td className="px-3 py-2">
                      <div className="flex flex-wrap items-center gap-1.5">
                        <Button variant="outline" size="sm" onClick={() => requestPauseToggle(d)}
                          className={d.paused ? undefined : 'border-warn/50 text-warn hover:bg-warn/5'}>
                          {d.paused ? <Play className="size-3.5" /> : <Pause className="size-3.5" />}
                          {d.paused ? '재개' : '정지'}
                        </Button>
                        {d.destType === 'CHUTE' && (
                          <>
                            <Button variant="outline" size="sm" onClick={() => setEditTarget(d)}>
                              <Pencil className="size-3.5" />수정
                            </Button>
                            <Button variant="outline" size="sm" onClick={() => requestClear(d)}>
                              <Eraser className="size-3.5" />비움
                            </Button>
                          </>
                        )}
                        {d.destType === 'SORTER_3D' && (
                          <>
                            <Button variant="outline" size="sm" onClick={() => setCellTarget(d)}>
                              <LayoutGrid className="size-3.5" />셀 설정
                            </Button>
                            <Button variant="outline" size="sm" onClick={() => requestReset(d)}
                              className="border-offline/50 text-offline hover:bg-offline/5">
                              <RotateCcw className="size-3.5" />초기화
                            </Button>
                          </>
                        )}
                        {d.isActive ? (
                          <Button variant="outline" size="sm" onClick={() => requestSetActive(d, false)}
                            className="border-offline/50 text-offline hover:bg-offline/5">
                            <PowerOff className="size-3.5" />비활성
                          </Button>
                        ) : (
                          <Button variant="outline" size="sm" onClick={() => requestSetActive(d, true)}>
                            <Power className="size-3.5" />활성
                          </Button>
                        )}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          <p className="border-t border-line px-3 py-2 text-[11px] text-faint">
            소터 신설은 재기동 + appsettings Sorters[] 항목 추가 후 폴링이 시작됩니다(DB 레코드는 즉시 생성).
          </p>
        </CardContent>
      </Card>

      {/* 오더 할당 */}
      <OrderAssignPanel
        onAssign={(o) => setAssignTarget(o)}
        onUnassign={requestUnassign}
      />

      {/* 확인 다이얼로그(파괴/변경) */}
      <ConfirmDialog
        open={pending !== null}
        title={pending?.title ?? ''}
        description={pending?.description}
        confirmLabel={pending?.confirmLabel ?? '확인'}
        danger={pending?.danger ?? true}
        busy={busy}
        onConfirm={onConfirm}
        onCancel={() => { if (!busy) setPending(null) }}
      />

      {/* 목적지 생성 다이얼로그 */}
      <CreateDestinationDialog
        open={createOpen}
        onClose={() => setCreateOpen(false)}
        operatorName={operatorName}
        onDone={invalidate}
      />

      {/* 목적지 수정 다이얼로그(CHUTE floor·workFullQty) */}
      <EditDestinationDialog
        dest={editTarget}
        onClose={() => setEditTarget(null)}
        operatorName={operatorName}
        onDone={invalidate}
      />

      {/* 셀 설정 다이얼로그 */}
      <CellConfigDialog
        sorter={cellTarget}
        onClose={() => setCellTarget(null)}
        operatorName={operatorName}
        onDone={invalidate}
      />

      {/* 오더 할당 다이얼로그 */}
      <AssignOrderDialog
        order={assignTarget}
        destinations={destinations}
        onClose={() => setAssignTarget(null)}
        operatorName={operatorName}
        onDone={invalidate}
      />
    </div>
  )
}

// ── 오더 할당 패널(미할당/할당 오더 목록) ─────────────────────────────────────
function OrderAssignPanel({
  onAssign,
  onUnassign,
}: {
  onAssign: (o: FacilityOrder) => void
  onUnassign: (o: FacilityOrder) => void
}) {
  const [tab, setTab] = useState<'unassigned' | 'assigned'>('unassigned')
  const ordersQ = useFacilityOrders(tab === 'unassigned' ? false : true, false)
  const orders = useMemo(() => ordersQ.data ?? [], [ordersQ.data])

  return (
    <Card className="flex min-w-0 flex-col">
      <CardHeader>
        <CardTitle>오더 할당</CardTitle>
        <div className="flex rounded-lg border border-line bg-elevated p-0.5" role="tablist" aria-label="오더 필터">
          {(['unassigned', 'assigned'] as const).map((t) => (
            <button
              key={t}
              role="tab"
              aria-selected={tab === t}
              onClick={() => setTab(t)}
              className={cn(
                'rounded-md px-2.5 py-1 text-[12px] font-semibold transition-colors',
                tab === t ? 'bg-panel text-ink shadow-card' : 'text-muted hover:text-ink',
              )}
            >
              {t === 'unassigned' ? '미할당' : '할당됨'}
            </button>
          ))}
        </div>
      </CardHeader>
      <CardContent className="min-w-0 overflow-auto p-0">
        {ordersQ.isLoading ? (
          <LoadingRow />
        ) : ordersQ.isError ? (
          <ErrorRow message={(ordersQ.error as Error)?.message ?? '오더 조회 실패'} />
        ) : orders.length === 0 ? (
          <EmptyRow label={tab === 'unassigned' ? '미할당 오더가 없습니다.' : '할당된 오더가 없습니다.'} />
        ) : (
          <table className="w-full text-[12px]">
            <thead className="sticky top-0 bg-panel text-left text-faint">
              <tr className="border-b border-line">
                <th className="px-3 py-2 font-medium">오더</th>
                <th className="px-3 py-2 font-medium">배치</th>
                <th className="px-3 py-2 font-medium">예약/분류</th>
                {tab === 'assigned' && <th className="px-3 py-2 font-medium">목적지</th>}
                <th className="px-3 py-2 font-medium">작업</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-line">
              {orders.map((o) => (
                <tr key={o.orderId} className="text-ink">
                  <td className="px-3 py-2 font-mono">{o.orderNo}</td>
                  <td className="px-3 py-2 text-muted">{o.batchLabel}</td>
                  <td className="px-3 py-2 font-mono tabular-nums text-muted">
                    {o.reservedQty} / {o.sortedQty}
                  </td>
                  {tab === 'assigned' && (
                    <td className="px-3 py-2">
                      {o.destinationChuteNo != null ? (
                        <span>
                          {o.destType} #{o.destinationChuteNo}
                          {o.assignedCellNo != null && <span className="text-faint"> · 셀 {o.assignedCellNo}</span>}
                        </span>
                      ) : (
                        '—'
                      )}
                    </td>
                  )}
                  <td className="px-3 py-2">
                    <div className="flex items-center gap-1.5">
                      <Button variant="outline" size="sm" onClick={() => onAssign(o)} disabled={!o.canReassign}
                        title={o.canReassign ? undefined : '진행 중 오더는 배정/재배정할 수 없습니다(OQ-3).'}>
                        <Link2 className="size-3.5" />{tab === 'unassigned' ? '배정' : '재배정'}
                      </Button>
                      {tab === 'assigned' && (
                        <Button variant="outline" size="sm" onClick={() => onUnassign(o)} disabled={!o.canReassign}
                          className="border-offline/50 text-offline hover:bg-offline/5">
                          <Unlink className="size-3.5" />해제
                        </Button>
                      )}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </CardContent>
    </Card>
  )
}

// ── 목적지 생성 다이얼로그 ────────────────────────────────────────────────────
function CreateDestinationDialog({
  open,
  onClose,
  operatorName,
  onDone,
}: {
  open: boolean
  onClose: () => void
  operatorName: string
  onDone: () => void
}) {
  const { toast } = useToast()
  const [chuteNo, setChuteNo] = useState('')
  const [destType, setDestType] = useState<'CHUTE' | 'SORTER_3D'>('CHUTE')
  const [workFullQty, setWorkFullQty] = useState('100')
  const [busy, setBusy] = useState(false)

  async function submit(e: FormEvent) {
    e.preventDefault()
    const n = Number(chuteNo)
    if (!Number.isInteger(n) || n < 1) {
      toast('warning', 'chuteNo 는 1 이상의 정수여야 합니다.')
      return
    }
    setBusy(true)
    try {
      const r = await b2cFacility.createDestination({
        chuteNo: n,
        destType,
        workFullQty: destType === 'CHUTE' ? Number(workFullQty) || undefined : undefined,
        operatorName: operatorName.trim() || undefined,
      })
      toast(r.ok ? 'success' : 'error', r.message)
      if (r.ok) {
        setChuteNo('')
        onDone()
        onClose()
      }
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog open={open} onClose={() => { if (!busy) onClose() }} labelledBy="create-dest-title">
      <form onSubmit={submit} className="flex flex-col gap-3 p-5">
        <h2 id="create-dest-title" className="text-[15px] font-semibold text-ink">새 목적지 생성</h2>
        <label className="block">
          <span className="mb-1 block text-[12px] font-medium text-muted">타입</span>
          <Select value={destType} onChange={(e) => setDestType(e.target.value as 'CHUTE' | 'SORTER_3D')} className="w-full">
            <option value="CHUTE">CHUTE (슈트)</option>
            <option value="SORTER_3D">SORTER_3D (3D 소터)</option>
          </Select>
        </label>
        <label className="block">
          <span className="mb-1 block text-[12px] font-medium text-muted">chuteNo</span>
          <input value={chuteNo} onChange={(e) => setChuteNo(e.target.value)} type="number" min={1}
            className={dlgInput} placeholder="예: 2" />
        </label>
        {destType === 'CHUTE' && (
          <label className="block">
            <span className="mb-1 block text-[12px] font-medium text-muted">만재 임계(workFullQty)</span>
            <input value={workFullQty} onChange={(e) => setWorkFullQty(e.target.value)} type="number" min={1}
              className={dlgInput} />
          </label>
        )}
        {destType === 'SORTER_3D' && (
          <p className="text-[11px] text-faint">소터는 셀 설정을 별도로 진행합니다. 폴링은 재기동 후 시작됩니다.</p>
        )}
        <div className="mt-1 flex justify-end gap-2">
          <Button type="button" variant="outline" size="sm" onClick={onClose} disabled={busy}>취소</Button>
          <Button type="submit" variant="solid" size="sm" disabled={busy}>{busy ? '생성 중…' : '생성'}</Button>
        </div>
      </form>
    </Dialog>
  )
}

// ── 목적지 수정 다이얼로그(CHUTE floor·workFullQty) ───────────────────────────
//   백엔드 POST /destinations/{id} 수정 결선(고아 엔드포인트 해소). status 는 제외 —
//   실 pause/resume(인메모리·IF-08 push 동기)은 행의 정지/재개(=/api/ops)가 정본이므로 여기서 다루지 않는다.
//   SORTER_3D 는 이 엔드포인트로 수정할 필드(floor NULL·workFullQty 없음)가 없어 수정 버튼을 노출하지 않는다.
function EditDestinationDialog({
  dest,
  onClose,
  operatorName,
  onDone,
}: {
  dest: Destination | null
  onClose: () => void
  operatorName: string
  onDone: () => void
}) {
  const { toast } = useToast()
  const [floor, setFloor] = useState('')
  const [workFullQty, setWorkFullQty] = useState('')
  const [busy, setBusy] = useState(false)
  const [loadedId, setLoadedId] = useState<number | null>(null)

  // 다이얼로그가 새 대상으로 열릴 때 현재값 프리필(대상 변경 시 1회). 닫히면 리셋 →
  // 재오픈 시 최신 데이터로 항상 재프리필(React 렌더-중 파생상태 패턴).
  if (dest && dest.id !== loadedId) {
    setLoadedId(dest.id)
    setFloor(dest.floor != null ? String(dest.floor) : '')
    setWorkFullQty(dest.workFullQty != null ? String(dest.workFullQty) : '')
  } else if (!dest && loadedId !== null) {
    setLoadedId(null)
  }

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (!dest) return
    const floorNum = floor.trim() === '' ? undefined : Number(floor)
    const workFullNum = workFullQty.trim() === '' ? undefined : Number(workFullQty)
    if (floorNum !== undefined && (!Number.isInteger(floorNum) || floorNum < 0)) {
      toast('warning', '층은 0 이상의 정수여야 합니다.')
      return
    }
    if (workFullNum !== undefined && (!Number.isInteger(workFullNum) || workFullNum < 1)) {
      toast('warning', '만재 임계는 1 이상의 정수여야 합니다.')
      return
    }
    setBusy(true)
    try {
      const r = await b2cFacility.updateDestination(dest.id, {
        floor: floorNum,
        workFullQty: workFullNum,
        operatorName: operatorName.trim() || undefined,
      })
      toast(r.ok ? 'success' : 'error', r.message)
      if (r.ok) {
        onDone()
        onClose()
      }
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog open={dest !== null} onClose={() => { if (!busy) onClose() }} labelledBy="edit-dest-title">
      <form onSubmit={submit} className="flex flex-col gap-3 p-5">
        <h2 id="edit-dest-title" className="text-[15px] font-semibold text-ink">
          목적지 수정 — 슈트 #{dest?.chuteNo}
        </h2>
        <label className="block">
          <span className="mb-1 block text-[12px] font-medium text-muted">층(floor · 선택)</span>
          <input value={floor} onChange={(e) => setFloor(e.target.value)} type="number" min={0}
            className={dlgInput} placeholder="미지정" />
        </label>
        <label className="block">
          <span className="mb-1 block text-[12px] font-medium text-muted">만재 임계(workFullQty)</span>
          <input value={workFullQty} onChange={(e) => setWorkFullQty(e.target.value)} type="number" min={1}
            className={dlgInput} />
        </label>
        <p className="text-[11px] text-faint">
          정지/재개 상태는 행의 정지·재개 버튼(운영 제어)에서 변경합니다. 만재 임계 변경은 재기동 후 인메모리 집계에 완전 반영됩니다.
        </p>
        <div className="mt-1 flex justify-end gap-2">
          <Button type="button" variant="outline" size="sm" onClick={onClose} disabled={busy}>취소</Button>
          <Button type="submit" variant="solid" size="sm" disabled={busy}>{busy ? '수정 중…' : '수정'}</Button>
        </div>
      </form>
    </Dialog>
  )
}

// ── 셀 설정 다이얼로그(행×열) ─────────────────────────────────────────────────
function CellConfigDialog({
  sorter,
  onClose,
  operatorName,
  onDone,
}: {
  sorter: Destination | null
  onClose: () => void
  operatorName: string
  onDone: () => void
}) {
  const { toast } = useToast()
  const [rows, setRows] = useState('5')
  const [cols, setCols] = useState('4')
  const [capacity, setCapacity] = useState('3')
  const [busy, setBusy] = useState(false)

  const total = (Number(rows) || 0) * (Number(cols) || 0)

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (!sorter) return
    if (total < 1) {
      toast('warning', '행·열은 1 이상의 정수여야 합니다.')
      return
    }
    setBusy(true)
    try {
      const r = await b2cFacility.configureCells(sorter.id, {
        rows: Number(rows),
        cols: Number(cols),
        capacity: Number(capacity) || undefined,
        enabled: true,
        operatorName: operatorName.trim() || undefined,
      })
      toast(r.ok ? 'success' : 'error', r.message)
      if (r.ok) {
        onDone()
        onClose()
      }
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog open={sorter !== null} onClose={() => { if (!busy) onClose() }} labelledBy="cell-config-title">
      <form onSubmit={submit} className="flex flex-col gap-3 p-5">
        <h2 id="cell-config-title" className="text-[15px] font-semibold text-ink">
          셀 설정 — 소터 #{sorter?.chuteNo}
        </h2>
        <div className="grid grid-cols-2 gap-3">
          <label className="block">
            <span className="mb-1 block text-[12px] font-medium text-muted">행(rows)</span>
            <input value={rows} onChange={(e) => setRows(e.target.value)} type="number" min={1} className={dlgInput} />
          </label>
          <label className="block">
            <span className="mb-1 block text-[12px] font-medium text-muted">열(cols)</span>
            <input value={cols} onChange={(e) => setCols(e.target.value)} type="number" min={1} className={dlgInput} />
          </label>
        </div>
        <label className="block">
          <span className="mb-1 block text-[12px] font-medium text-muted">셀 용량(capacity)</span>
          <input value={capacity} onChange={(e) => setCapacity(e.target.value)} type="number" min={1} className={dlgInput} />
        </label>
        <p className="text-[11px] text-faint">
          순차 셀번호 1..{total || 'N'} 생성(멱등 — 기존 셀은 용량/활성 보정, 축소 시 초과 셀은 보존).
        </p>
        <div className="mt-1 flex justify-end gap-2">
          <Button type="button" variant="outline" size="sm" onClick={onClose} disabled={busy}>취소</Button>
          <Button type="submit" variant="solid" size="sm" disabled={busy}>{busy ? '설정 중…' : `${total || 0}셀 설정`}</Button>
        </div>
      </form>
    </Dialog>
  )
}

// ── 오더 할당 다이얼로그(목적지+셀 선택) ──────────────────────────────────────
function AssignOrderDialog({
  order,
  destinations,
  onClose,
  operatorName,
  onDone,
}: {
  order: FacilityOrder | null
  destinations: Destination[]
  onClose: () => void
  operatorName: string
  onDone: () => void
}) {
  const { toast } = useToast()
  const [destId, setDestId] = useState('')
  const [cellNo, setCellNo] = useState('')
  const [busy, setBusy] = useState(false)

  const selected = destinations.find((d) => d.id === Number(destId))
  const isSorter = selected?.destType === 'SORTER_3D'
  const activeDests = destinations.filter((d) => d.isActive)

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (!order) return
    const did = Number(destId)
    if (!Number.isInteger(did) || did < 1) {
      toast('warning', '목적지를 선택하세요.')
      return
    }
    const cell = isSorter && cellNo.trim() !== '' ? Number(cellNo) : undefined
    setBusy(true)
    try {
      const r = await b2cFacility.assignOrder({
        orderId: order.orderId,
        destinationId: did,
        cellNo: cell,
        operatorName: operatorName.trim() || undefined,
      })
      toast(r.ok ? 'success' : 'error', r.message)
      if (r.ok) {
        setDestId(''); setCellNo('')
        onDone()
        onClose()
      }
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog open={order !== null} onClose={() => { if (!busy) onClose() }} labelledBy="assign-order-title">
      <form onSubmit={submit} className="flex flex-col gap-3 p-5">
        <h2 id="assign-order-title" className="text-[15px] font-semibold text-ink">
          오더 배정 — {order?.orderNo}
        </h2>
        <label className="block">
          <span className="mb-1 block text-[12px] font-medium text-muted">목적지</span>
          <Select value={destId} onChange={(e) => setDestId(e.target.value)} className="w-full">
            <option value="">— 선택 —</option>
            {activeDests.map((d) => (
              <option key={d.id} value={d.id}>
                {d.destType} #{d.chuteNo}
                {d.destType === 'SORTER_3D' ? ` (셀 ${d.cellEnabled ?? 0}/${d.cellTotal ?? 0})` : ''}
              </option>
            ))}
          </Select>
        </label>
        {isSorter && (
          <label className="block">
            <span className="mb-1 block text-[12px] font-medium text-muted">셀 번호(선택 — 미지정 시 셀 미배정)</span>
            <input value={cellNo} onChange={(e) => setCellNo(e.target.value)} type="number" min={1}
              className={dlgInput} placeholder="예: 1" />
          </label>
        )}
        <div className="mt-1 flex justify-end gap-2">
          <Button type="button" variant="outline" size="sm" onClick={onClose} disabled={busy}>취소</Button>
          <Button type="submit" variant="solid" size="sm" disabled={busy}>{busy ? '배정 중…' : '배정'}</Button>
        </div>
      </form>
    </Dialog>
  )
}

const dlgInput =
  'h-10 w-full rounded-lg border border-line bg-panel px-3 text-[13px] text-ink placeholder:text-faint/70 focus-visible:outline-2 focus-visible:outline-ink'
