import { keepPreviousData, useQuery } from '@tanstack/react-query'

// ═══════════════════════════════════════════════════════════════════════════
// B2C(3D 소터) 테스트 데이터 관리 API 클라이언트 — /api/b2c/test-data/* (docs/B2C-DATAGEN.md).
//
// ⚠ 성공 판정 = res.ok && body.status === "S" (B2B-DATAGEN §7.1 과 동일 함정).
//   관리 액션(generate/reset)은 비즈니스 실패·거부도 HTTP 200 + { status:"F", message }.
//   파라미터 검증 실패만 HTTP 400 + 동일 body. → 단순 res.ok 판정 금지(200 F 오인).
// 기존 /api/monitor·/api/test-data 클라이언트와 분리(BASE·시맨틱 상이).
// ═══════════════════════════════════════════════════════════════════════════

const BASE = '/api/b2c/test-data'

// ── 응답 형상(백엔드 DTO 미러 · camelCase) ───────────────────────────────────
export interface SorterSummary {
  destinationId: number
  chuteNo: number
  status: string
  isActive: boolean
  cellTotal: number
  cellEnabled: number
  cellAssigned: number
  orderTotal: number
  orderRunning: number
  orderCompleted: number
  orderCancelled: number
  plannedSum: number
  reservedSum: number
  sortedSum: number
  inFlightPieces: number
}

export interface CellDetail {
  cellNo: number
  capacity: number | null
  enabled: boolean
  currentQty: number
  assignedOrderNo: string | null
  reservedQty: number | null
  sortedQty: number | null
}

export interface B2cGenerateRequest {
  sorterChuteNo: number
  workDate: string
  batchNo: string
  waveNo: number
  cellCount: number
  cellCapacity: number
  plannedQty: number
  orderPrefix: string
}

export interface B2cResetRequest {
  sorterChuteNo: number
  force: boolean
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

/**
 * 관리 액션 호출 — 성공 판정 res.ok && status "S". 200 F·400 은 실패로 표면화(body.message).
 * 네트워크/파싱 예외도 실패 Outcome 으로 환원(호출부가 토스트로 노출).
 */
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
      /* 비-JSON 응답(예: 500) — 상태코드로 메시지 구성 */
    }
    const ok = res.ok && parsed.status === 'S'
    const message = parsed.message ?? (ok ? '완료되었습니다.' : `요청 실패 (HTTP ${res.status})`)
    return { ok, message, counts: parsed.counts }
  } catch (e) {
    return { ok: false, message: `네트워크 오류 — ${(e as Error).message}` }
  }
}

// 조회(read) — 실패는 throw 해 TanStack Query 가 에러 상태로 표면화.
async function getJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  const res = await fetch(`${BASE}${path}`, { headers: { Accept: 'application/json' }, signal })
  if (!res.ok) {
    let msg = `요청 실패 (HTTP ${res.status})`
    try {
      const b = (await res.json()) as RawApiResponse
      if (b.message) msg = b.message
    } catch {
      /* ignore */
    }
    throw new Error(msg)
  }
  return res.json() as Promise<T>
}

// ── API 표면 ─────────────────────────────────────────────────────────────────
export const b2cTestData = {
  summary: (sorterChuteNo?: number, signal?: AbortSignal) =>
    getJson<SorterSummary[]>(
      `/summary${sorterChuteNo ? `?sorterChuteNo=${sorterChuteNo}` : ''}`,
      signal,
    ),

  detail: (sorterChuteNo: number, signal?: AbortSignal) =>
    getJson<CellDetail[]>(`/detail?sorterChuteNo=${sorterChuteNo}`, signal),

  generate: (req: B2cGenerateRequest) => runAction('/generate', req),

  reset: (req: B2cResetRequest) => runAction('/reset', req),
}

// ── TanStack Query 훅(조회) ──────────────────────────────────────────────────
export function useB2cSummary(refetchInterval: number | false) {
  return useQuery({
    queryKey: ['b2c-summary'],
    queryFn: ({ signal }) => b2cTestData.summary(undefined, signal),
    refetchInterval,
    placeholderData: keepPreviousData,
  })
}

export function useB2cDetail(sorterChuteNo: number | null, refetchInterval: number | false) {
  return useQuery({
    queryKey: ['b2c-detail', sorterChuteNo],
    queryFn: ({ signal }) => b2cTestData.detail(sorterChuteNo!, signal),
    enabled: sorterChuteNo !== null,
    refetchInterval,
    placeholderData: keepPreviousData,
  })
}
