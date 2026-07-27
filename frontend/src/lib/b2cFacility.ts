import { keepPreviousData, useQuery } from '@tanstack/react-query'

// ═══════════════════════════════════════════════════════════════════════════
// B2C 설비 관리 API 클라이언트 — /api/b2c/facility/* (docs/B2C-FACILITY.md).
//
// ⚠ 성공 판정 = res.ok && body.status === "S" (b2cTestData 와 동일 함정 — 200 F 오인 금지).
//   관리 액션(create/activate/update/cells/assign/unassign)은 비즈니스 실패·거부도 HTTP 200 + {status:"F"}.
//   파라미터 검증 실패만 HTTP 400 + 동일 body.
// 조회(orders)는 원시 JSON(camelCase) — 실패는 throw(TanStack Query 에러 표면).
// ═══════════════════════════════════════════════════════════════════════════

const BASE = '/api/b2c/facility'

// ★ S-B2C-UX FIX ITER 2: 오더 조회 상한 — 백엔드 B2cConstants.GenerateCountMax(한 배치 생성 상한) 미러.
//   조회 훅은 항상 이 값을 take 로 명시 전달해 백엔드 기본(과거 200) 침묵 절단을 제거한다. 반환 건수가
//   이 상한과 같으면(>=) 초과분이 있을 수 있으므로 UI 가 절단 힌트를 띄운다(Fail-Loud). 서버 400 이 최종 권위.
export const ORDERS_FETCH_MAX = 1000

// ── 응답 형상(백엔드 DTO 미러 · camelCase) ───────────────────────────────────
export interface FacilityOrder {
  orderId: number
  orderNo: string
  batchId: number
  batchLabel: string
  barcode: string
  plannedQty: number
  reservedQty: number
  sortedQty: number
  status: string
  destinationId: number | null
  destinationChuteNo: number | null
  destType: string | null
  assignType: string | null
  assignedCellNo: number | null
  hasActivePiece: boolean
  canReassign: boolean
}

// ── 배치 상세(데이터 생성 페이지 하단 그리드) per-item 행 — 백엔드 B2cBatchItemDto 미러(camelCase) ──
//   Fix 1(S-B2C-BARCODE-MULTI-FIX): 행 = order_item(바코드). 1 오더:N 바코드 → N행. 수량은 항목별,
//   상태·목적지·할당셀은 오더 레벨(오더에서 반복). row key = orderItemId.
export interface FacilityBatchItem {
  orderItemId: number
  orderId: number
  orderNo: string
  barcode: string
  plannedQty: number
  reservedQty: number
  sortedQty: number
  status: string
  destinationId: number | null
  destinationChuteNo: number | null
  destType: string | null
  assignedCellNo: number | null
}

/** 관리 액션 결과 — ok(= res.ok && status "S") + message + counts(선택). */
export interface ActionOutcome {
  ok: boolean
  message: string
  counts?: Record<string, number>
}

interface RawApiResponse {
  status?: string
  message?: string
  counts?: Record<string, number>
}

async function runAction(path: string, body: unknown): Promise<ActionOutcome> {
  try {
    const res = await fetch(`${BASE}${path}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
      body: JSON.stringify(body),
    })
    let parsed: RawApiResponse = {}
    try {
      parsed = (await res.json()) as RawApiResponse
    } catch {
      /* 비-JSON 응답(예: 500) */
    }
    const ok = res.ok && parsed.status === 'S'
    const message = parsed.message ?? (ok ? '완료되었습니다.' : `요청 실패 (HTTP ${res.status})`)
    return { ok, message, counts: parsed.counts }
  } catch (e) {
    return { ok: false, message: `네트워크 오류 — ${(e as Error).message}` }
  }
}

async function getJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  const res = await fetch(`${BASE}${path}`, { headers: { Accept: 'application/json' }, signal })
  if (!res.ok) throw new Error(`요청 실패 (HTTP ${res.status})`)
  return res.json() as Promise<T>
}

// ── 요청 형상 ────────────────────────────────────────────────────────────────
export interface CreateDestinationRequest {
  chuteNo: number
  destType: 'CHUTE' | 'SORTER_3D'
  floor?: number | null
  workFullQty?: number | null
  operatorName?: string
}

export interface UpdateDestinationRequest {
  floor?: number | null
  workFullQty?: number | null
  operatorName?: string
  // status 는 제외 — 실 pause/resume(인메모리·IF-08 push 동기)은 /api/ops 가 정본(ops.pause/resume).
}

export interface CellBulkRequest {
  rows: number
  cols: number
  capacity?: number
  enabled?: boolean
  operatorName?: string
}

export interface AssignOrderRequest {
  orderId: number
  destinationId: number
  cellNo?: number | null
  operatorName?: string
}

// ── API 표면 ─────────────────────────────────────────────────────────────────
export const b2cFacility = {
  orders: (assigned?: boolean, batchId?: number, take?: number, signal?: AbortSignal) => {
    const parts: string[] = []
    if (assigned !== undefined) parts.push(`assigned=${assigned}`)
    if (batchId !== undefined) parts.push(`batchId=${batchId}`)
    if (take !== undefined) parts.push(`take=${take}`)
    return getJson<FacilityOrder[]>(`/orders${parts.length ? `?${parts.join('&')}` : ''}`, signal)
  },

  // 배치 상세 per-item(order_item 단위) — 데이터 생성 페이지 하단 그리드 전용(Fix 1). orders 와 별개 경로.
  batchItems: (batchId: number, take?: number, signal?: AbortSignal) => {
    const parts = [`batchId=${batchId}`]
    if (take !== undefined) parts.push(`take=${take}`)
    return getJson<FacilityBatchItem[]>(`/batch-items?${parts.join('&')}`, signal)
  },

  createDestination: (req: CreateDestinationRequest) => runAction('/destinations', req),

  // 목적지 수정(floor·workFullQty) — status 는 제외(pause/resume 이 정본). 백엔드 POST /destinations/{id}.
  updateDestination: (destId: number, req: UpdateDestinationRequest) =>
    runAction(`/destinations/${destId}`, req),

  setActive: (destId: number, isActive: boolean, force: boolean, operatorName: string) =>
    runAction(`/destinations/${destId}/activate`, { isActive, force, operatorName }),

  configureCells: (sorterId: number, req: CellBulkRequest) =>
    runAction(`/sorters/${sorterId}/cells`, req),

  assignOrder: (req: AssignOrderRequest) => runAction('/orders/assign', req),

  unassignOrder: (orderId: number, operatorName: string) =>
    runAction('/orders/unassign', { orderId, operatorName }),
}

// ── TanStack Query 훅 ─────────────────────────────────────────────────────────
export function useFacilityOrders(assigned: boolean | undefined, refetchInterval: number | false = false) {
  return useQuery({
    queryKey: ['facility-orders', assigned ?? 'all'],
    // take=ORDERS_FETCH_MAX 명시(FIX ITER 2) — 미할당 목록·할당 집계(슈트 단위 해제/카운트)가 200 에서
    // 침묵 절단되지 않도록. 반환 건수가 상한이면 소비 컴포넌트가 절단 힌트를 표면화.
    queryFn: ({ signal }) => b2cFacility.orders(assigned, undefined, ORDERS_FETCH_MAX, signal),
    refetchInterval,
    placeholderData: keepPreviousData,
  })
}

// ★ 데이터 생성 페이지 하단 배치 상세 그리드 소스 — 선택 배치의 **바코드(order_item)당 1행**(Fix 1).
//   전용 per-item 엔드포인트(GET /batch-items?batchId=)를 쓴다 — 오더 단위 집계(orders)는 첫 바코드만
//   보여 1 오더:N 바코드가 1행으로 접혔던 근본을 해소(신규 엔드포인트 · 근거 sprint-log). batchId=null 이면
//   비활성(미선택). queryKey 는 'facility-orders' 접두를 공유 → 배정/해제/초기화 invalidate 가 함께 갱신.
export function useFacilityBatchItems(batchId: number | null, refetchInterval: number | false = false) {
  return useQuery({
    queryKey: ['facility-orders', 'batch-items', batchId],
    queryFn: ({ signal }) => b2cFacility.batchItems(batchId!, ORDERS_FETCH_MAX, signal),
    enabled: batchId !== null,
    refetchInterval,
    placeholderData: keepPreviousData,
  })
}
