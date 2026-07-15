import { Fragment, useCallback, useMemo, useState, type FormEvent, type ReactNode } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import {
  ChevronDown, ChevronRight, Eraser, LayoutGrid, Link2, Pause, Pencil, Play, Plus, Power, PowerOff, Unlink,
} from 'lucide-react'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import { Select } from '@/components/ui/select'
import { ConfirmDialog, Dialog } from '@/components/ui/dialog'
import { EmptyRow, ErrorRow, LoadingRow } from '@/components/StateMessage'
import { useToast } from '@/lib/toast'
import { useCells, useDestinations } from '@/lib/queries'
import type { Destination } from '@/lib/api'
import { b2cFacility, ORDERS_FETCH_MAX, useFacilityOrders, type FacilityOrder } from '@/lib/b2cFacility'
import { ops } from '@/lib/ops'

// ═══════════════════════════════════════════════════════════════════════════
// B2cFacilityPage — B2C 설비 관리(목적지 구성·소터 셀 설정·오더 할당·슈트 제어). docs/B2C-FACILITY.md.
//   ★ S-B2C-UX: 재테스트 초기화(reset)는 **데이터 생성 페이지로 이관**(여기서 제거). 오더 할당은
//     2패널(좌=배정 대상[슈트 리프 + 소터 셀 드롭다운] · 우=미할당 오더)로 재구성 — 양쪽 체크박스
//     다중 선택 → 배정(1:1 min(N,M) 인덱스 페어링) / 좌 상단 해제(다건 순차 unassign). 목적지 CRUD·
//     셀 설정·슈트 제어(clear/pause/resume·활성화)는 유지(OQ-2). 파괴/변경 = ConfirmDialog + 작업자 이름(감사).
// ═══════════════════════════════════════════════════════════════════════════

// 파괴/변경 확인 액션(ConfirmDialog 소비) — run 은 실행·표면화(force 재요청 체이닝 시 pending 교체).
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

  const invalidate = useCallback(() => {
    qc.invalidateQueries({ queryKey: ['destinations'] })
    qc.invalidateQueries({ queryKey: ['facility-orders'] })
    qc.invalidateQueries({ queryKey: ['cells'] })
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
                      <DestStatusBadges d={d} />
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
                          <Button variant="outline" size="sm" onClick={() => setCellTarget(d)}>
                            <LayoutGrid className="size-3.5" />셀 설정
                          </Button>
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

      {/* 오더 할당 — 2패널(좌 배정 대상 / 우 미할당 오더) */}
      <OrderAssign2Panel
        destinations={destinations}
        requireOperator={requireOperator}
        requestConfirm={setPending}
        invalidate={invalidate}
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
    </div>
  )
}

// ── 목적지 상태 배지(비활성 우선 표기 — 정지/비활성 혼동 해소, Minor #1/#7) ─────────
//   비활성이면 '비활성'만(정지·만재·정상 억제) + 소터는 online/offline 하드웨어 상태만 병기.
function DestStatusBadges({ d }: { d: Destination }) {
  if (!d.isActive) {
    return (
      <div className="flex flex-wrap gap-1">
        <Badge tone="offline">비활성</Badge>
        {d.destType === 'SORTER_3D' && (
          <Badge tone={d.online ? 'online' : 'offline'}>{d.online ? 'online' : 'offline'}</Badge>
        )}
      </div>
    )
  }
  return (
    <div className="flex flex-wrap gap-1">
      {d.paused && <Badge tone="warn">정지</Badge>}
      {d.full && <Badge tone="offline">만재</Badge>}
      {d.destType === 'SORTER_3D' && (
        <Badge tone={d.online ? 'online' : 'offline'}>{d.online ? 'online' : 'offline'}</Badge>
      )}
      {!d.paused && !d.full && d.destType === 'CHUTE' && <Badge tone="online">정상</Badge>}
    </div>
  )
}

// ── 배정 대상 키(좌 패널 선택 단위) ──────────────────────────────────────────────
//   슈트 = 리프(`chute:{destId}`) / 소터 셀 = `cell:{destId}:{cellNo}`. 소터 헤더 자체는 대상 아님.
type Target = { key: string; destId: number; cellNo?: number }
function parseTargetKey(key: string): Target {
  const p = key.split(':')
  return p[0] === 'chute'
    ? { key, destId: Number(p[1]) }
    : { key, destId: Number(p[1]), cellNo: Number(p[2]) }
}

// ── 오더 할당 2패널 (OQ-4: 좌 배정 대상 ↔ 우 미할당 오더 · min(N,M) 인덱스 페어링) ──
function OrderAssign2Panel({
  destinations,
  requireOperator,
  requestConfirm,
  invalidate,
}: {
  destinations: Destination[]
  requireOperator: () => string | null
  requestConfirm: (p: PendingConfirm) => void
  invalidate: () => void
}) {
  const { toast } = useToast()
  const unassignedQ = useFacilityOrders(false, false)
  const assignedQ = useFacilityOrders(true, false)
  const unassigned = useMemo(() => unassignedQ.data ?? [], [unassignedQ.data])
  const assigned = useMemo(() => assignedQ.data ?? [], [assignedQ.data])
  // Fail-Loud(FIX ITER 2): 반환수가 조회 상한과 같으면 초과분이 목록/집계에서 누락됐을 수 있음.
  //   특히 할당 목록 절단 시 슈트 단위 해제/현재 배정 카운트가 실제보다 적을 수 있어 경고를 띄운다.
  const unassignedTruncated = unassigned.length >= ORDERS_FETCH_MAX
  const assignedTruncated = assigned.length >= ORDERS_FETCH_MAX

  const [checkedTargets, setCheckedTargets] = useState<Set<string>>(() => new Set())
  const [checkedOrders, setCheckedOrders] = useState<Set<number>>(() => new Set())
  const [expanded, setExpanded] = useState<Set<number>>(() => new Set())
  const [assigning, setAssigning] = useState(false)

  // 목적지별 배정 오더(좌 패널 슈트 현재 배정 정보) + (destId,cellNo) → 오더(소터 셀 점유·해제 대상).
  const assignedByDest = useMemo(() => {
    const m = new Map<number, FacilityOrder[]>()
    for (const o of assigned) {
      if (o.destinationId == null) continue
      const arr = m.get(o.destinationId)
      if (arr) arr.push(o)
      else m.set(o.destinationId, [o])
    }
    return m
  }, [assigned])
  const orderByCell = useMemo(() => {
    const m = new Map<string, FacilityOrder>()
    for (const o of assigned) {
      if (o.destinationId != null && o.assignedCellNo != null) m.set(`${o.destinationId}:${o.assignedCellNo}`, o)
    }
    return m
  }, [assigned])

  const sortedDests = useMemo(() => [...destinations].sort((a, b) => a.chuteNo - b.chuteNo), [destinations])
  const chuteNoOf = useCallback(
    (destId: number) => destinations.find((d) => d.id === destId)?.chuteNo ?? 0,
    [destinations],
  )

  // 인덱스 페어링용 안정 정렬 — 대상: (chuteNo, cellNo) / 오더: orderNo.
  const orderedTargets = useMemo(() => {
    return [...checkedTargets]
      .map(parseTargetKey)
      .sort((a, b) => {
        const c = chuteNoOf(a.destId) - chuteNoOf(b.destId)
        return c !== 0 ? c : (a.cellNo ?? -1) - (b.cellNo ?? -1)
      })
  }, [checkedTargets, chuteNoOf])
  const sortedUnassigned = useMemo(
    () => [...unassigned].sort((a, b) => a.orderNo.localeCompare(b.orderNo)),
    [unassigned],
  )
  const selectedOrders = useMemo(
    () => sortedUnassigned.filter((o) => checkedOrders.has(o.orderId)),
    [sortedUnassigned, checkedOrders],
  )

  const pairCount = Math.min(orderedTargets.length, selectedOrders.length)
  const canAssign = orderedTargets.length >= 1 && selectedOrders.length >= 1

  const toggleTarget = (key: string) =>
    setCheckedTargets((prev) => {
      const n = new Set(prev)
      if (n.has(key)) n.delete(key)
      else n.add(key)
      return n
    })
  const toggleOrder = (id: number) =>
    setCheckedOrders((prev) => {
      const n = new Set(prev)
      if (n.has(id)) n.delete(id)
      else n.add(id)
      return n
    })
  const toggleExpand = (destId: number) =>
    setExpanded((prev) => {
      const n = new Set(prev)
      if (n.has(destId)) n.delete(destId)
      else n.add(destId)
      return n
    })

  // ── 배정: 좌 선택 대상 ↔ 우 선택 오더 1:1 인덱스 페어링 min(N,M) · 순차 assign 호출(가드·감사 보존) ──
  async function doAssign() {
    const op = requireOperator()
    if (!op) return
    const n = pairCount
    if (n === 0) return
    setAssigning(true)
    try {
      let ok = 0
      let fail = 0
      for (let i = 0; i < n; i++) {
        const t = orderedTargets[i]
        const o = selectedOrders[i]
        const r = await b2cFacility.assignOrder({
          orderId: o.orderId,
          destinationId: t.destId,
          cellNo: t.cellNo ?? undefined,
          operatorName: op,
        })
        if (r.ok) ok++
        else fail++
      }
      const remainder = Math.abs(orderedTargets.length - selectedOrders.length)
      toast(
        ok > 0 ? 'success' : 'error',
        `배정 ${ok}건 완료${fail > 0 ? `, 실패 ${fail}건` : ''}${remainder > 0 ? ` (미페어링 ${remainder}건 유지)` : ''}.`,
      )
      setCheckedTargets(new Set())
      setCheckedOrders(new Set())
      invalidate()
    } finally {
      setAssigning(false)
    }
  }

  // ── 해제: 체크한 대상(슈트=배정 오더 전부 / 셀=점유 오더)의 미시작 오더 순차 unassign(진행 중 스킵) ──
  function requestUnassign() {
    const op = requireOperator()
    if (!op) return
    const orderIds: number[] = []
    const seen = new Set<number>()
    for (const t of orderedTargets) {
      if (t.cellNo == null) {
        for (const o of assignedByDest.get(t.destId) ?? []) {
          if (!seen.has(o.orderId)) { seen.add(o.orderId); orderIds.push(o.orderId) }
        }
      } else {
        const o = orderByCell.get(`${t.destId}:${t.cellNo}`)
        if (o && !seen.has(o.orderId)) { seen.add(o.orderId); orderIds.push(o.orderId) }
      }
    }
    if (orderIds.length === 0) {
      toast('warning', '해제할 배정이 없습니다(선택 대상에 배정된 오더 없음).')
      return
    }
    requestConfirm({
      title: '오더 배정 해제',
      confirmLabel: '해제',
      danger: true,
      description: (
        <>
          선택 대상 <b>{orderedTargets.length}</b>건 · 해제될 오더 <b>{orderIds.length}</b>건 · 작업자 <b>{op}</b>.
          <p className="mt-2 text-[12px] text-muted">미시작 오더만 해제됩니다(진행 중은 스킵 · OQ-3).</p>
          <p className="mt-1 text-offline">해제된 오더는 미할당으로 돌아갑니다.</p>
        </>
      ),
      run: async () => {
        let ok = 0
        let skipped = 0
        let fail = 0
        for (const oid of orderIds) {
          const r = await b2cFacility.unassignOrder(oid, op)
          if (r.ok) ok++
          else if ((r.counts?.reserved ?? 0) > 0 || (r.counts?.sorted ?? 0) > 0) skipped++
          else fail++
        }
        toast(
          ok > 0 ? 'success' : skipped > 0 ? 'warning' : 'error',
          `해제 ${ok}건 완료${skipped > 0 ? `, 진행 중 스킵 ${skipped}건` : ''}${fail > 0 ? `, 실패 ${fail}건` : ''}.`,
        )
        setCheckedTargets(new Set())
        invalidate()
      },
    })
  }

  const loading = unassignedQ.isLoading || assignedQ.isLoading
  const errored = unassignedQ.isError || assignedQ.isError

  return (
    <Card className="flex min-w-0 flex-col">
      <CardHeader>
        <CardTitle>오더 할당 (2패널)</CardTitle>
        <div className="flex flex-wrap items-center gap-3">
          <span className="text-[12px] text-muted">
            선택 대상 <b className="text-ink tabular-nums">{checkedTargets.size}</b> · 오더{' '}
            <b className="text-ink tabular-nums">{checkedOrders.size}</b>
            {pairCount > 0 && <span className="ml-1.5 text-faint">→ {pairCount}건 배정</span>}
          </span>
          <Button variant="solid" size="sm" disabled={!canAssign || assigning} onClick={doAssign}>
            <Link2 className="size-4" />{assigning ? '배정 중…' : `배정 (${pairCount})`}
          </Button>
        </div>
      </CardHeader>
      <CardContent>
        <div className="grid grid-cols-1 gap-3 lg:grid-cols-2">
          {/* 좌: 배정 대상(슈트 리프 + 소터 셀) + 상단 해제 */}
          <div className="min-w-0 rounded-lg border border-line">
            <div className="flex items-center justify-between gap-2 border-b border-line px-3 py-2">
              <span className="text-[12px] font-semibold text-ink">배정 대상 (슈트 · 소터 셀)</span>
              <Button
                variant="outline"
                size="sm"
                disabled={checkedTargets.size === 0}
                onClick={requestUnassign}
                className="border-offline/50 text-offline hover:bg-offline/5"
              >
                <Unlink className="size-3.5" />해제 ({checkedTargets.size})
              </Button>
            </div>
            {assignedTruncated && (
              <p className="border-b border-warn/30 bg-warn/10 px-3 py-2 text-[11px] text-warn">
                배정 오더가 조회 상한 {ORDERS_FETCH_MAX.toLocaleString()}건에 도달 — 현재 배정 카운트·슈트 단위 해제가 실제보다 적게 처리될 수 있습니다.
              </p>
            )}
            <div className="max-h-[440px] min-w-0 overflow-auto">
              {loading ? (
                <LoadingRow />
              ) : errored ? (
                <ErrorRow message="오더 조회 실패" />
              ) : sortedDests.length === 0 ? (
                <EmptyRow label="목적지가 없습니다. 먼저 목적지를 생성하세요." />
              ) : (
                <table className="w-full text-[12px]">
                  <thead className="sticky top-0 bg-panel text-left text-faint">
                    <tr className="border-b border-line">
                      <th className="w-8 px-3 py-2" />
                      <th className="px-3 py-2 font-medium">대상</th>
                      <th className="px-3 py-2 font-medium">상태</th>
                      <th className="px-3 py-2 font-medium">현재 배정</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-line">
                    {sortedDests.map((d) =>
                      d.destType === 'CHUTE' ? (
                        <tr key={`c${d.id}`} className="text-ink">
                          <td className="px-3 py-1.5">
                            <input
                              type="checkbox"
                              checked={checkedTargets.has(`chute:${d.id}`)}
                              onChange={() => toggleTarget(`chute:${d.id}`)}
                              disabled={!d.isActive}
                              aria-label={`슈트 ${d.chuteNo} 선택`}
                              className="size-3.5 cursor-pointer accent-[var(--color-brand-active)] disabled:cursor-not-allowed"
                            />
                          </td>
                          <td className="px-3 py-1.5 font-mono tabular-nums">
                            #{d.chuteNo} <Badge tone="neutral">CHUTE</Badge>
                          </td>
                          <td className="px-3 py-1.5"><DestStatusBadges d={d} /></td>
                          <td className="px-3 py-1.5 text-muted">
                            <ChuteAssignInfo orders={assignedByDest.get(d.id) ?? []} />
                          </td>
                        </tr>
                      ) : (
                        <Fragment key={`s${d.id}`}>
                          <tr onClick={() => toggleExpand(d.id)} className="cursor-pointer text-ink hover:bg-elevated">
                            <td className="px-3 py-1.5 text-faint">
                              {expanded.has(d.id) ? <ChevronDown className="size-3.5" /> : <ChevronRight className="size-3.5" />}
                            </td>
                            <td className="px-3 py-1.5 font-mono tabular-nums">
                              #{d.chuteNo} <Badge tone="accent">SORTER_3D</Badge>
                            </td>
                            <td className="px-3 py-1.5"><DestStatusBadges d={d} /></td>
                            <td className="px-3 py-1.5 text-muted tabular-nums">
                              {d.cellEnabled ?? 0}/{d.cellTotal ?? 0} 셀 · 배정 {assignedByDest.get(d.id)?.length ?? 0}
                            </td>
                          </tr>
                          {expanded.has(d.id) && (
                            <SorterCellRows
                              sorter={d}
                              checkedTargets={checkedTargets}
                              onToggle={toggleTarget}
                            />
                          )}
                        </Fragment>
                      ),
                    )}
                  </tbody>
                </table>
              )}
            </div>
          </div>

          {/* 우: 미할당 오더 */}
          <div className="min-w-0 rounded-lg border border-line">
            <div className="border-b border-line px-3 py-2">
              <span className="text-[12px] font-semibold text-ink">미할당 오더</span>
            </div>
            {unassignedTruncated && (
              <p className="border-b border-warn/30 bg-warn/10 px-3 py-2 text-[11px] text-warn">
                미할당 오더가 조회 상한 {ORDERS_FETCH_MAX.toLocaleString()}건에 도달 — 초과분은 목록에 표시되지 않습니다.
              </p>
            )}
            <div className="max-h-[440px] min-w-0 overflow-auto">
              {loading ? (
                <LoadingRow />
              ) : errored ? (
                <ErrorRow message="오더 조회 실패" />
              ) : unassigned.length === 0 ? (
                <EmptyRow label="미할당 오더가 없습니다." />
              ) : (
                <table className="w-full text-[12px]">
                  <thead className="sticky top-0 bg-panel text-left text-faint">
                    <tr className="border-b border-line">
                      <th className="w-8 px-3 py-2" />
                      <th className="px-3 py-2 font-medium">오더</th>
                      <th className="px-3 py-2 font-medium">배치</th>
                      <th className="px-3 py-2 font-medium">예약/분류</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-line">
                    {sortedUnassigned.map((o) => (
                      <tr key={o.orderId} className="text-ink">
                        <td className="px-3 py-1.5">
                          <input
                            type="checkbox"
                            checked={checkedOrders.has(o.orderId)}
                            onChange={() => toggleOrder(o.orderId)}
                            disabled={!o.canReassign}
                            aria-label={`오더 ${o.orderNo} 선택`}
                            title={o.canReassign ? undefined : '진행 중 오더는 배정할 수 없습니다(OQ-3).'}
                            className="size-3.5 cursor-pointer accent-[var(--color-brand-active)] disabled:cursor-not-allowed"
                          />
                        </td>
                        <td className="px-3 py-1.5 font-mono">{o.orderNo}</td>
                        <td className="px-3 py-1.5 text-muted">{o.batchLabel}</td>
                        <td className="px-3 py-1.5 font-mono tabular-nums text-muted">
                          {o.reservedQty} / {o.sortedQty}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </div>
          </div>
        </div>
        <p className="mt-2 text-[11px] text-faint">
          좌측에서 슈트/소터 셀을, 우측에서 미할당 오더를 각각 체크한 뒤 <b>배정</b>하면 선택 순서대로 1:1(부족분은 미배정 유지)
          로 배정됩니다. 소터는 행을 눌러 셀을 펼쳐 선택합니다.
        </p>
      </CardContent>
    </Card>
  )
}

// 슈트 현재 배정 요약(오더 수 + 오더번호 발췌).
function ChuteAssignInfo({ orders }: { orders: FacilityOrder[] }) {
  if (orders.length === 0) return <span className="text-faint">—</span>
  const head = orders.slice(0, 3).map((o) => o.orderNo).join(', ')
  return (
    <span>
      {orders.length}건
      <span className="ml-1 text-faint">{head}{orders.length > 3 ? ' …' : ''}</span>
    </span>
  )
}

// ── 소터 셀 행(드롭다운 펼침 — 각 셀 체크박스 + 점유 오더 표시) ─────────────────────
function SorterCellRows({
  sorter,
  checkedTargets,
  onToggle,
}: {
  sorter: Destination
  checkedTargets: Set<string>
  onToggle: (key: string) => void
}) {
  const cellsQ = useCells(sorter.id)
  const cells = useMemo(() => cellsQ.data ?? [], [cellsQ.data])

  if (cellsQ.isLoading)
    return (
      <tr>
        <td colSpan={4} className="px-3 py-2 pl-9 text-[11px] text-faint">셀 불러오는 중…</td>
      </tr>
    )
  if (cellsQ.isError)
    return (
      <tr>
        <td colSpan={4} className="px-3 py-2 pl-9 text-[11px] text-offline">셀 조회 실패</td>
      </tr>
    )
  if (cells.length === 0)
    return (
      <tr>
        <td colSpan={4} className="px-3 py-2 pl-9 text-[11px] text-faint">셀이 없습니다('셀 설정'으로 생성).</td>
      </tr>
    )

  return (
    <>
      {cells.map((c) => {
        const key = `cell:${sorter.id}:${c.cellNo}`
        return (
          <tr key={key} className="bg-elevated/40 text-ink">
            <td className="px-3 py-1.5 pl-8">
              <input
                type="checkbox"
                checked={checkedTargets.has(key)}
                onChange={() => onToggle(key)}
                disabled={!c.enabled}
                aria-label={`소터 ${sorter.chuteNo} 셀 ${c.cellNo} 선택`}
                className="size-3.5 cursor-pointer accent-[var(--color-brand-active)] disabled:cursor-not-allowed"
              />
            </td>
            <td className="px-3 py-1.5 font-mono text-[11px] text-muted" colSpan={2}>
              └ 셀 {c.cellNo}
              {!c.enabled && <span className="ml-1 text-faint">(비활성)</span>}
            </td>
            <td className="px-3 py-1.5 text-[11px]">
              {c.assignedOrderNo ? (
                <span className="text-ink">배정: {c.assignedOrderNo}</span>
              ) : (
                <span className="text-faint">비어있음</span>
              )}
              {c.currentQty > 0 && <span className="ml-1 text-faint">적재 {c.currentQty}</span>}
            </td>
          </tr>
        )
      })}
    </>
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
//   백엔드 POST /destinations/{id} 수정 결선. status 는 제외 — 실 pause/resume(인메모리·IF-08 push 동기)은
//   행의 정지/재개(=/api/ops)가 정본. SORTER_3D 는 수정 필드(floor NULL·workFullQty 없음)가 없어 미노출.
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

const dlgInput =
  'h-10 w-full rounded-lg border border-line bg-panel px-3 text-[13px] text-ink placeholder:text-faint/70 focus-visible:outline-2 focus-visible:outline-ink'
