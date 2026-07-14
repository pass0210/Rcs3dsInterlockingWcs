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
  orders: (assigned?: boolean, batchId?: number, signal?: AbortSignal) => {
    const parts: string[] = []
    if (assigned !== undefined) parts.push(`assigned=${assigned}`)
    if (batchId !== undefined) parts.push(`batchId=${batchId}`)
    return getJson<FacilityOrder[]>(`/orders${parts.length ? `?${parts.join('&')}` : ''}`, signal)
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
    queryFn: ({ signal }) => b2cFacility.orders(assigned, undefined, signal),
    refetchInterval,
    placeholderData: keepPreviousData,
  })
}
