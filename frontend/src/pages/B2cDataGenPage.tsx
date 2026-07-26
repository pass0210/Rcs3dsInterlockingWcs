import { useCallback, useId, useMemo, useRef, useState, type FormEvent, type ReactNode } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { ChevronRight, Download, Plus, RotateCcw, Upload } from 'lucide-react'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import { ConfirmDialog } from '@/components/ui/dialog'
import { ContextMenu } from '@/components/ui/context-menu'
import { EmptyRow, ErrorRow, LoadingRow } from '@/components/StateMessage'
import { useToast } from '@/lib/toast'
import { cn } from '@/lib/utils'
import { ROW_HIGHLIGHT_CLASS, useRowSelection } from '@/lib/useRowSelection'
import { todayBizDay } from '@/lib/uiMode'
import {
  b2cTestData,
  useB2cBatches,
  B2C_UPLOAD_TEMPLATE_URL,
  type BatchSummary,
  type UploadRowError,
} from '@/lib/b2cTestData'
import { ORDERS_FETCH_MAX, useFacilityBatchOrders } from '@/lib/b2cFacility'

// ═══════════════════════════════════════════════════════════════════════════
// B2cDataGenPage — B2C(3D 소터) 데이터 생성 + 생성 결과 마스터-디테일 + 배치 초기화(S-B2C-UX).
//   · 좌: 5-파라미터 생성 폼(작업일자·배치명·차수·계획수량·바코드 접두) — 미할당 오더/바코드 생성.
//   · 우: 생성 결과 그리드 = 마스터. 행별 체크박스(초기화 다중 선택) + 상단 초기화 버튼 + 행 선택(디테일 로드).
//   · 하단: 선택 배치의 오더/바코드/수량/할당 상태 디테일 그리드(GET /api/b2c/facility/orders?batchId=).
// ★ 초기화 이관(S-B2C-UX): reset 을 설비 관리 → 여기로 이관하고 스코프를 소터 → **배치**로 재정의.
//   "초기화 = 생성한 테스트 데이터를 되돌린다"는 도메인 판단. 파괴 액션 = ConfirmDialog + 작업자 이름(감사) +
//   in-flight 거부 시 강제 초기화(force) 체이닝. 다건은 체크된 배치별 순차 호출 + 집계 토스트. docs/B2C-DATAGEN.md.
// ═══════════════════════════════════════════════════════════════════════════

// 파괴 확인 액션(ConfirmDialog 소비) — run 은 실행·표면화(force 재요청 체이닝 시 pending 교체).
interface PendingConfirm {
  title: string
  description: ReactNode
  confirmLabel: string
  run: () => Promise<void>
}

export function B2cDataGenPage() {
  const qc = useQueryClient()
  const { toast } = useToast()

  const batchesQ = useB2cBatches(false)
  const batches = useMemo(() => batchesQ.data ?? [], [batchesQ.data])

  // 작업자 이름 — 초기화 감사 귀속(공백이면 초기화 차단 · 설비 페이지와 동형).
  const [operatorName, setOperatorName] = useState('')
  // 체크박스(초기화 다중 선택 대상 batchId)와 행 선택(디테일 단건 로드)은 목적이 달라 분리(OQ-5).
  const [checked, setChecked] = useState<Set<number>>(() => new Set())
  const [selectedBatchId, setSelectedBatchId] = useState<number | null>(null)
  // 공용 행 선택 상호작용(드래그 하이라이트 + 우클릭 4항목) — 자기 체크 Set(checked)에 브리지.
  //   전 행 체크 가능(비활성 행 없음)이라 getRowProps eligible=true 고정. 필터 없어 resetKey 불요.
  const rowSel = useRowSelection<number>({ setChecked, parseId: numberId, menuAriaLabel: '생성 결과 배치 메뉴' })
  const [pending, setPending] = useState<PendingConfirm | null>(null)
  const [busy, setBusy] = useState(false)

  const invalidateAll = useCallback(() => {
    qc.invalidateQueries({ queryKey: ['b2c-batches'] })
    qc.invalidateQueries({ queryKey: ['facility-orders'] }) // 디테일 그리드(배치 오더) 갱신.
  }, [qc])

  // 존재하지 않는(초기화 등으로 사라진 게 아니라 목록 변동) 체크는 실재 배치로 정리 — 안정성.
  const validBatchIds = useMemo(() => new Set(batches.map((b) => b.batchId)), [batches])

  const requireOperator = useCallback((): string | null => {
    const op = operatorName.trim()
    if (op.length === 0) {
      toast('warning', '작업자 이름을 입력하세요(초기화 감사 귀속).')
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
      toast('error', `초기화 실패 — ${(e as Error).message}`)
    } finally {
      setBusy(false)
      // run() 이 force 재요청으로 pending 을 교체했으면 유지(React 배칭 silent-close 회피) — 자기 자신일 때만 닫음.
      setPending((cur) => (cur === justRan ? null : cur))
    }
  }, [pending, toast])

  const toggleCheck = (batchId: number) =>
    setChecked((prev) => {
      const next = new Set(prev)
      if (next.has(batchId)) next.delete(batchId)
      else next.add(batchId)
      return next
    })

  const allChecked = batches.length > 0 && batches.every((b) => checked.has(b.batchId))
  const toggleAll = () =>
    setChecked((prev) => (prev.size >= batches.length && batches.length > 0 ? new Set() : new Set(batches.map((b) => b.batchId))))

  // ── 배치 초기화(다건 · 순차 호출 + 집계 + force 체이닝) ──────────────────────────
  //   재귀(plain 함수 — 설비 페이지 패턴): in-flight 로 거부된 배치는 force=true 로 재확인 다이얼로그를 띄운다.
  const requestReset = (targetIds: number[], force: boolean) => {
    const op = requireOperator()
    if (!op) return
    const targets = batches.filter((b) => targetIds.includes(b.batchId))
    if (targets.length === 0) return
    setPending({
      title: force ? '강제 초기화' : '배치 초기화',
      confirmLabel: force ? '강제 초기화' : '초기화',
      description: (
        <>
          {force
            ? `진행 중(in-flight) 작업이 있는 배치 ${targets.length}건을 강제로 초기화합니다.`
            : `선택한 배치 ${targets.length}건을 초기화합니다(생성한 테스트 데이터 되돌리기).`}
          <ul className="mt-2 max-h-40 list-disc overflow-auto pl-4 text-[12px] leading-relaxed">
            {targets.map((b) => (
              <li key={b.batchId}>
                {fmtDate(b.workDate)} / {b.batchNo} #{b.waveNo} — 오더 {b.orderTotal}건
              </li>
            ))}
          </ul>
          <p className="mt-2 text-[12px] text-muted">
            피스·이력·소터명령 보관 처리(soft-delete) · 예약/분류 수량 0 · 완료 오더 재개(오더·셀 배정 보존). 작업자 <b>{op}</b>.
          </p>
          <p className="mt-1 text-offline">이 작업은 되돌릴 수 없습니다{force ? ' (진행 중 피스까지 보관).' : '.'}</p>
        </>
      ),
      run: async () => {
        let success = 0
        let failed = 0
        const refused: number[] = []
        for (const id of targetIds) {
          const r = await b2cTestData.reset({ batchId: id, force, operatorName: op })
          if (r.ok) success++
          else if (!force && (r.counts?.inFlight ?? 0) > 0) refused.push(id)
          else failed++
        }
        invalidateAll()
        // 성공 처리된 배치는 체크 해제(거부된 것은 force 재확인 위해 유지).
        setChecked((prev) => {
          const next = new Set(prev)
          for (const id of targetIds) if (!refused.includes(id)) next.delete(id)
          return next
        })
        if (!force && refused.length > 0) {
          toast('warning', `${success}건 초기화, ${refused.length}건 진행 중 — 강제 초기화 재확인이 필요합니다.`)
          requestReset(refused, true) // pending 교체 → onConfirm 가드가 유지.
          return
        }
        toast(success > 0 ? 'success' : failed > 0 ? 'error' : 'info', `초기화 ${success}건 완료${failed > 0 ? `, 실패 ${failed}건` : ''}.`)
      },
    })
  }

  const checkedCount = useMemo(() => [...checked].filter((id) => validBatchIds.has(id)).length, [checked, validBatchIds])

  return (
    // 뷰포트 맞춤(S-UI-LAYOUT-FIX) — master-detail: 상단(생성 폼 + 마스터 그리드) / 하단(배치 상세).
    //   ★ 낮은 뷰포트 폼-오버랩 근본수정: 상단 그리드 <div>에서 고정 하한(구 min-h-[220px])을 제거한다.
    //   높이 하한(floor)의 실제 출처 = 이 상단 <div>가 min-h-0 을 갖지 않아 그 flex min-height:auto 가
    //   콘텐츠 기반 최소(= grid min-content)로 잡히는 것. 그 min-content 는 마스터 카드가 min-h-0 라 0 을 기여하고
    //   폼은 self-start 자연높이라, 곧 폼 카드 자연높이와 같아진다 → 상단이 폼보다 작게 줄지 않음(폼 오버랩 제거).
    //   폼은 self-start 자연높이로 '+ 데이터 생성' 버튼까지 온전히 보인다.
    //   ★ S-B2C-DATAGEN-UPLOAD(A): 좌측 폼 안 "엑셀 업로드" 블록을 disclosure(기본 접힘)로 감싸 폼의
    //   자연높이를 더 줄였다 → 하단 "배치 상세" 그리드가 헤더만이 아니라 오더 행을 실제로 표시(회귀 계약:
    //   접힘이 기본, 이 블록 펼침은 사용자 토글 시에만). 폼 오버랩 0 불변은 그대로 유지(약화 금지).
    //   xl:grid-rows-1 = minmax(0,1fr)(min-track=0, auto 아님)은 하한이 아니라: 위에서 확정된 상단 높이를 1fr 로 채우되
    //   행이 표 전체 높이로 부풀지 않게 캡해, 마스터 그리드(min-h-0·overflow-auto)가 그 확정 높이 안에서 내부 스크롤하게 한다.
    <div className="flex min-h-0 flex-1 flex-col gap-4">
      <div className="grid flex-1 grid-cols-1 gap-4 xl:grid-cols-[360px_minmax(0,1fr)] xl:grid-rows-1">
        {/* 좌: 생성 폼 */}
        <Card className="self-start">
          <CardHeader>
            <CardTitle>데이터 생성</CardTitle>
          </CardHeader>
          <CardContent>
            <B2cGenerateForm onGenerated={invalidateAll} />
            <B2cExcelUpload onUploaded={invalidateAll} />
          </CardContent>
        </Card>

        {/* 우: 생성 결과(마스터 그리드) + 초기화 */}
        <Card className="flex min-h-0 min-w-0 flex-col">
          <CardHeader className="shrink-0">
            <CardTitle>생성 결과 — 최근 배치</CardTitle>
            <div className="flex flex-wrap items-center gap-2">
              <label className="flex items-center gap-1.5 text-[12px] font-medium text-ink">
                작업자 <span className="text-offline">*</span>
                <input
                  value={operatorName}
                  onChange={(e) => setOperatorName(e.target.value)}
                  placeholder="예: 홍길동"
                  maxLength={100}
                  aria-label="작업자 이름"
                  className="h-8 w-36 rounded-lg border border-line bg-panel px-2.5 text-[13px] text-ink focus-visible:outline-2 focus-visible:outline-ink"
                />
              </label>
              <Button
                variant="outline"
                size="sm"
                disabled={checkedCount === 0}
                onClick={() => requestReset([...checked].filter((id) => validBatchIds.has(id)), false)}
                className="border-offline/50 text-offline hover:bg-offline/5"
              >
                <RotateCcw className="size-3.5" />초기화 ({checkedCount})
              </Button>
            </div>
          </CardHeader>
          <CardContent className="flex min-h-0 flex-1 flex-col overflow-hidden p-0">
            {/* 그리드 본문 — 우클릭 메뉴/드래그 스코프(OQ-3) + 스크롤 컨테이너(sticky thead 고정처). 하단 안내는 고정. */}
            <div {...rowSel.containerProps} className="min-h-0 min-w-0 flex-1 overflow-auto">
            {batchesQ.isLoading ? (
              <LoadingRow />
            ) : batchesQ.isError ? (
              <ErrorRow message={(batchesQ.error as Error)?.message ?? '배치 조회 실패'} />
            ) : batches.length === 0 ? (
              <EmptyRow label="생성된 배치가 없습니다. 좌측에서 데이터를 생성하세요." />
            ) : (
              <table className="w-full text-[12px]">
                <thead className="sticky top-0 bg-panel text-left text-faint">
                  <tr className="border-b border-line">
                    <th className="px-3 py-2">
                      <input
                        type="checkbox"
                        checked={allChecked}
                        onChange={toggleAll}
                        aria-label="전체 선택"
                        className="size-3.5 cursor-pointer accent-[var(--color-brand-active)]"
                      />
                    </th>
                    <th className="px-3 py-2 font-medium">작업일자</th>
                    <th className="px-3 py-2 font-medium">배치</th>
                    <th className="px-3 py-2 font-medium">차수</th>
                    <th className="px-3 py-2 font-medium">상태</th>
                    <th className="px-3 py-2 font-medium">오더(미할당)</th>
                    <th className="px-3 py-2 font-medium">항목</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-line">
                  {batches.map((b) => (
                    <tr
                      key={b.batchId}
                      {...rowSel.getRowProps(b.batchId, true)}
                      onClick={() => setSelectedBatchId(b.batchId)}
                      className={cn(
                        'cursor-pointer text-ink hover:bg-elevated',
                        selectedBatchId === b.batchId && 'bg-elevated',
                        rowSel.isHighlighted(b.batchId) && ROW_HIGHLIGHT_CLASS,
                      )}
                    >
                      {/* 체크박스 셀 — 행 선택(디테일)과 분리(propagation 차단). */}
                      <td className="px-3 py-1.5" onClick={(e) => e.stopPropagation()}>
                        <input
                          type="checkbox"
                          checked={checked.has(b.batchId)}
                          onChange={() => toggleCheck(b.batchId)}
                          aria-label={`배치 ${b.batchNo} 초기화 선택`}
                          className="size-3.5 cursor-pointer accent-[var(--color-brand-active)]"
                        />
                      </td>
                      <td className="px-3 py-1.5 font-mono tabular-nums">{fmtDate(b.workDate)}</td>
                      <td className="px-3 py-1.5">{b.batchNo}</td>
                      <td className="px-3 py-1.5 font-mono tabular-nums text-muted">{b.waveNo}</td>
                      <td className="px-3 py-1.5 text-faint">{b.status}</td>
                      <td className="px-3 py-1.5 font-mono tabular-nums">
                        {b.orderTotal}
                        <span className="ml-1 text-faint">(미할당 {b.orderUnassigned})</span>
                      </td>
                      <td className="px-3 py-1.5 font-mono tabular-nums text-muted">{b.itemTotal}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
            </div>
            <p className="shrink-0 border-t border-line px-3 py-2 text-[11px] text-faint">
              행을 클릭하면 하단에 그 배치의 오더 상세가 표시됩니다. 체크 후 <b>초기화</b>로 여러 배치를 되돌릴 수 있습니다.
              그리드를 <b>드래그</b>하면 연속 범위가 하이라이트되고, <b>우클릭</b> 메뉴로 전체/선택 행을 일괄 체크·해제할 수 있습니다.
            </p>
          </CardContent>
        </Card>
      </div>

      {/* 우클릭 컨텍스트 메뉴(4항목) — 생성 결과 배치 그리드 공용. */}
      <ContextMenu {...rowSel.menu} />

      {/* 하단: 선택 배치 상세(마스터-디테일) */}
      <BatchDetailGrid batch={batches.find((b) => b.batchId === selectedBatchId) ?? null} />

      {/* 초기화 확인 다이얼로그(파괴 액션) */}
      <ConfirmDialog
        open={pending !== null}
        title={pending?.title ?? ''}
        description={pending?.description}
        confirmLabel={pending?.confirmLabel ?? '확인'}
        danger
        busy={busy}
        onConfirm={onConfirm}
        onCancel={() => {
          if (!busy) setPending(null)
        }}
      />
    </div>
  )
}

// ── 하단 디테일 그리드(선택 배치의 오더/바코드/수량/할당 상태) ────────────────────
function BatchDetailGrid({ batch }: { batch: BatchSummary | null }) {
  const ordersQ = useFacilityBatchOrders(batch?.batchId ?? null, false)
  const orders = useMemo(() => ordersQ.data ?? [], [ordersQ.data])
  // Fail-Loud(FIX ITER 2): 반환수가 조회 상한과 같으면 초과 오더가 표시에서 누락됐을 수 있음 → 힌트 표면화.
  //   (초기화는 배치키 서버 스코프라 표시 절단과 무관하게 배치 전량에 적용됨 — 별도 명시.)
  const truncated = orders.length >= ORDERS_FETCH_MAX

  return (
    // 하단 상세 = 남은 높이를 갖는 flex-1. 낮은 뷰포트에서 상단(폼 자연높이 하한)에 자리를 양보하고
    //   자체 바운드 스크롤(아래 overflow-auto)로 접힌다 — 빈/짧은 상세가 상단을 밀어내거나 폼을 덮지 않음(폼 오버랩 0).
    //   상단이 폼 자연높이 하한을 가지므로 detail.height <= topRegion.height 가 항상 성립(빈 상세 50% 점유 해소).
    //   ★ S-B2C-DATAGEN-UPLOAD(A · 사용자 결정 2026-07-26 "짧은 창 페이지-스크롤 허용"): 상세 Card 에 min-h-[10rem]
    //   (≈160px = 헤더 + 오더 수 행) 하한을 부여한다. 좌 폼 자연높이(≈540px)가 큰 낮은 뷰포트(대략 ≤~800px, 예 700px)
    //   에서는 폼(≈540)+갭(16)+상세하한(160) 합이 <main>(overflow-auto) 가용을 넘어 페이지 스크롤로 에스컬레이션한다
    //   (폼을 줄이는 대신 페이지가 스크롤 — 접힘 기본 상태에서도 오더 행이 스크롤로 도달 가능). ≥~900px(예 900/1080)에서는
    //   상세 자연높이가 하한을 넘어 하한이 비활성이라 상세 본문이 자체 바운드 스크롤로 in-place 표시된다
    //   (C3: 700px 페이지-스크롤 도달 · 900/1080 in-place). Layout.tsx <main> 주석의 "min-height 하한 합 초과 시 스크롤"
    //   에스컬레이션 계약과 정합. (구 주석의 "620px/680px/700px≈98px" 수치는 disclosure 도입 전·부정확 측정이라 정정.)
    <Card className="flex min-h-[10rem] min-w-0 flex-1 flex-col">
      <CardHeader className="shrink-0">
        <CardTitle>
          배치 상세{batch ? ` — ${batch.batchNo} #${batch.waveNo}` : ''}
        </CardTitle>
        {batch && orders.length > 0 && (
          <span className="text-[11px] text-faint tabular-nums">
            {truncated ? `상위 ${ORDERS_FETCH_MAX.toLocaleString()}건 표시` : `${orders.length.toLocaleString()}건`}
          </span>
        )}
      </CardHeader>
      <CardContent className="flex min-h-0 flex-1 flex-col overflow-hidden p-0">
        {truncated && (
          <p className="shrink-0 border-b border-warn/30 bg-warn/10 px-3 py-2 text-[11px] text-warn">
            표시 상한 {ORDERS_FETCH_MAX.toLocaleString()}건에 도달했습니다 — 이 배치에 초과 오더가 있으면 목록에 표시되지 않을 수 있습니다.
            (초기화는 배치 전량에 적용되므로 표시 절단과 무관합니다.)
          </p>
        )}
        <div className="min-h-0 min-w-0 flex-1 overflow-auto">
        {batch === null ? (
          <EmptyRow label="상단에서 배치 행을 선택하면 오더 상세가 표시됩니다." />
        ) : ordersQ.isLoading ? (
          <LoadingRow />
        ) : ordersQ.isError ? (
          <ErrorRow message={(ordersQ.error as Error)?.message ?? '오더 상세 조회 실패'} />
        ) : orders.length === 0 ? (
          <EmptyRow label="이 배치에 오더가 없습니다." />
        ) : (
          <table className="w-full text-[12px]">
            <thead className="sticky top-0 bg-panel text-left text-faint">
              <tr className="border-b border-line">
                <th className="px-3 py-2 font-medium">오더</th>
                <th className="px-3 py-2 font-medium">바코드</th>
                <th className="px-3 py-2 font-medium">계획</th>
                <th className="px-3 py-2 font-medium">예약</th>
                <th className="px-3 py-2 font-medium">분류</th>
                <th className="px-3 py-2 font-medium">상태</th>
                <th className="px-3 py-2 font-medium">목적지</th>
                <th className="px-3 py-2 font-medium">할당</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-line">
              {orders.map((o) => (
                <tr key={o.orderId} className="text-ink">
                  <td className="px-3 py-1.5 font-mono">{o.orderNo}</td>
                  <td className="px-3 py-1.5 font-mono text-muted">{o.barcode}</td>
                  <td className="px-3 py-1.5 font-mono tabular-nums text-muted">{o.plannedQty}</td>
                  <td className="px-3 py-1.5 font-mono tabular-nums">{o.reservedQty}</td>
                  <td className="px-3 py-1.5 font-mono tabular-nums">{o.sortedQty}</td>
                  <td className="px-3 py-1.5 text-faint">{o.status}</td>
                  <td className="px-3 py-1.5">
                    {o.destinationChuteNo != null ? (
                      <span>
                        {o.destType} #{o.destinationChuteNo}
                        {o.assignedCellNo != null && <span className="text-faint"> · 셀 {o.assignedCellNo}</span>}
                      </span>
                    ) : (
                      <span className="text-faint">미할당</span>
                    )}
                  </td>
                  <td className="px-3 py-1.5">
                    {o.destinationId != null ? <Badge tone="online">배정</Badge> : <Badge tone="neutral">미할당</Badge>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
        </div>
      </CardContent>
    </Card>
  )
}

// 작업일자 표시 — batches view 의 workDate 는 ISO(yyyy-MM-dd[THH:...]) 문자열. 앞 10자만.
function fmtDate(v: string): string {
  return typeof v === 'string' && v.length >= 10 ? v.slice(0, 10) : v
}

// ── 생성 폼(5 파라미터) ──────────────────────────────────────────────────────
function B2cGenerateForm({ onGenerated }: { onGenerated: () => void }) {
  const { toast } = useToast()

  const [workDate, setWorkDate] = useState(todayBizDay)
  const [batchNo, setBatchNo] = useState('')
  const [waveNo, setWaveNo] = useState('1')
  const [plannedQty, setPlannedQty] = useState('10')
  const [barcodePrefix, setBarcodePrefix] = useState('')
  const [submitting, setSubmitting] = useState(false)

  async function onSubmit(e: FormEvent) {
    e.preventDefault()
    const nums = { waveNo: Number(waveNo), plannedQty: Number(plannedQty) }
    if (!batchNo.trim() || !barcodePrefix.trim() || !workDate.trim()) {
      toast('warning', '작업일자·배치명·바코드 접두를 모두 입력하세요.')
      return
    }
    if (!/^[A-Za-z0-9_-]{1,50}$/.test(barcodePrefix.trim())) {
      toast('warning', '바코드 접두는 영문·숫자·하이픈·언더스코어만 허용합니다.')
      return
    }
    for (const [k, v] of Object.entries(nums)) {
      if (!Number.isInteger(v) || v < 1) {
        toast('warning', `${k} 는 1 이상의 정수여야 합니다.`)
        return
      }
    }
    // 상한(백엔드 B2cConstants.GenerateCountMax 미러 — 서버 400 이 최종 권위·절대규칙 #7).
    if (nums.plannedQty > GENERATE_COUNT_MAX) {
      toast('warning', `계획수량(생성 개수)은 ${GENERATE_COUNT_MAX} 이하여야 합니다.`)
      return
    }

    setSubmitting(true)
    try {
      const outcome = await b2cTestData.generate({
        workDate: workDate.trim(),
        batchNo: batchNo.trim(),
        waveNo: nums.waveNo,
        plannedQty: nums.plannedQty,
        barcodePrefix: barcodePrefix.trim(),
      })
      toast(outcome.ok ? 'success' : 'error', outcome.message)
      if (outcome.ok) onGenerated()
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <form onSubmit={onSubmit} noValidate className="flex flex-col gap-3">
      <Field label="작업일자">
        <input value={workDate} onChange={(e) => setWorkDate(e.target.value)} type="date" className={inputBase} />
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
        <Field label="계획수량(생성 개수)">
          <input value={plannedQty} onChange={(e) => setPlannedQty(e.target.value)}
            type="number" min={1} max={GENERATE_COUNT_MAX} className={inputBase} />
        </Field>
      </div>
      <Field label="바코드 접두">
        <input value={barcodePrefix} onChange={(e) => setBarcodePrefix(e.target.value)}
          placeholder="예: 0714-A" maxLength={50} className={inputBase} />
        <p className="mt-1 text-[11px] text-faint">
          오더번호 = 바코드 = &quot;{barcodePrefix || '접두'}-NN&quot; ({plannedQty || 'N'}건, 각 계획 1).
        </p>
      </Field>
      <Button type="submit" variant="solid" disabled={submitting} className="mt-1 w-full">
        <Plus className="size-4" />
        {submitting ? '생성 중…' : '데이터 생성'}
      </Button>
      <p className="text-[11px] text-faint">
        멱등: 같은 파라미터 재실행 시 카운트 불변(upsert). <b>목적지 미할당</b> 오더/바코드만 생성합니다.
      </p>
    </form>
  )
}

// ── 엑셀 업로드 블록(S-B2C-DATAGEN-UPLOAD · 접기/펼치기 disclosure) ──────────────
//   생성 폼의 대안 입력 경로 — 정적 양식 다운로드 + 6열 .xlsx 업로드(1 오더:N 바코드, 미할당).
//   ★ 레이아웃(S-B2C-DATAGEN-UPLOAD A): 본문을 disclosure(기본 접힘)로 감싸 좌측 폼의 자연높이를
//     줄인다 → 하단 "배치 상세" 그리드가 헤더만이 아니라 오더 행을 실제로 표시(회귀 계약: 접힘 기본).
//     신규 공용 UI 컴포넌트 없이 이 파일 내 로컬 구현(버튼 + 조건부 렌더). 접근성: 토글은 native
//     <button type="button"> · aria-expanded · aria-controls(본문 id) · 키보드 Enter/Space(native).
//   성공 시 배치 그리드 invalidate + 입력 리셋. 실패 시 에러 토스트 + 행별 오류 목록(Fail-Loud).
function B2cExcelUpload({ onUploaded }: { onUploaded: () => void }) {
  const { toast } = useToast()
  const [open, setOpen] = useState(false)   // 기본 접힘(좌측 폼 자연높이 최소화 — 회귀 계약).
  const [file, setFile] = useState<File | null>(null)
  const [uploading, setUploading] = useState(false)
  const [rowErrors, setRowErrors] = useState<UploadRowError[]>([])
  const inputRef = useRef<HTMLInputElement>(null)
  const bodyId = useId()   // aria-controls 로 토글↔본문 연결.

  async function onUpload() {
    if (!file || uploading) return
    setUploading(true)
    setRowErrors([])
    try {
      const res = await b2cTestData.upload(file)
      if (res.ok) {
        toast('success', res.message)
        onUploaded()
        setFile(null)
        if (inputRef.current) inputRef.current.value = ''
      } else {
        toast('error', res.message)
        setRowErrors(res.rowErrors ?? [])
      }
    } finally {
      setUploading(false)
    }
  }

  return (
    <div className="mt-3 border-t border-line pt-3">
      {/* 헤더 행(항상 표시): 토글(라벨+chevron) + 양식 다운로드(접힘에서도 접근 가능). */}
      <div className="flex items-center justify-between gap-2">
        <button
          type="button"
          aria-expanded={open}
          aria-controls={bodyId}
          onClick={() => setOpen((o) => !o)}
          className="-mx-1 inline-flex items-center gap-1 rounded-lg px-1 py-0.5 text-[12px] font-semibold text-ink hover:bg-elevated focus-visible:outline-2 focus-visible:outline-ink"
        >
          <ChevronRight className={cn('size-3.5 shrink-0 transition-transform', open && 'rotate-90')} />
          엑셀 업로드
        </button>
        {/* 양식 다운로드 — 정적 파일 링크(동일 출처). download 속성으로 파일 저장 트리거. */}
        <a
          href={B2C_UPLOAD_TEMPLATE_URL}
          download
          className="inline-flex items-center gap-1.5 rounded-lg border border-line bg-panel px-2.5 py-1.5 text-[12px] font-medium text-ink hover:bg-elevated focus-visible:outline-2 focus-visible:outline-ink"
        >
          <Download className="size-3.5" />양식 다운로드
        </a>
      </div>

      {/* 본문 — 펼침(open)일 때만 렌더/표시(접힘 시 좌측 폼 자연높이 최소화). */}
      {open && (
        <div id={bodyId} className="mt-3 flex flex-col gap-3">
          <input
            ref={inputRef}
            type="file"
            accept=".xlsx"
            aria-label="엑셀 파일 선택"
            onChange={(e) => {
              setFile(e.target.files?.[0] ?? null)
              setRowErrors([])
            }}
            className="block w-full text-[12px] text-muted file:mr-3 file:cursor-pointer file:rounded-lg file:border file:border-line file:bg-panel file:px-3 file:py-1.5 file:text-[12px] file:font-medium file:text-ink hover:file:bg-elevated"
          />
          <Button type="button" variant="solid" disabled={!file || uploading} onClick={onUpload} className="w-full">
            <Upload className="size-4" />
            {uploading ? '업로드 중…' : '업로드'}
          </Button>
          <p className="text-[11px] leading-relaxed text-faint">
            컬럼: 작업일자·배치명·차수·<b>오더번호</b>·바코드·수량. 같은 오더번호를 여러 행에 반복하면
            <b> 한 오더에 여러 바코드</b>(1 오더:N)가 묶입니다. <b>목적지 미할당</b> 오더로 추가됩니다(멱등 —
            같은 데이터 재업로드 시 중복 0). 배치 안에서 바코드는 유일해야 하며, 한 행이라도 오류가 있으면
            전체가 취소됩니다(원자성).
          </p>
          {rowErrors.length > 0 && (
            <div className="rounded-lg border border-offline/40 bg-offline/5 p-2.5">
              <p className="text-[12px] font-semibold text-offline">
                행 오류 {rowErrors.length}건 — 전체 취소됨(반영 0건)
              </p>
              <ul className="mt-1.5 max-h-40 list-disc overflow-auto pl-4 text-[11px] leading-relaxed text-offline">
                {rowErrors.map((e, i) => (
                  <li key={`${e.row}-${i}`}>
                    <b className="tabular-nums">{e.row}행</b>: {e.message}
                  </li>
                ))}
              </ul>
            </div>
          )}
        </div>
      )}
    </div>
  )
}

// data-rsid(문자열) → 숫자 id 복원(useRowSelection parseId). 모듈 상수(렌더마다 새 함수 생성 방지).
const numberId = (s: string) => Number(s)

// 생성 개수 상한 — 백엔드 B2cConstants.GenerateCountMax 미러(1곳·근거 주석). 서버 400 이 최종 권위.
const GENERATE_COUNT_MAX = 1000

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
