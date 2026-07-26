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

// 정적 양식(.xlsx) 경로 — frontend/public/ 에 커밋된 정적 자산(빌드 시 wwwroot 로 복사·동일 출처 서빙).
//   ⚠ 동적 GET /template 엔드포인트는 없다(확정 결정 2026-07-26 = 정적 파일). 다운로드 버튼은 이 링크.
export const B2C_UPLOAD_TEMPLATE_URL = '/b2c-order-upload-template.xlsx'

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

/** 생성 요청(2a 슬림 · 5 파라미터). plannedQty = 생성할 오더/바코드 개수 N(OQ-4·각 order_item planned_qty=1). */
export interface B2cGenerateRequest {
  workDate: string
  batchNo: string
  waveNo: number
  plannedQty: number
  barcodePrefix: string
}

/** 생성 결과 view — 최근 배치 요약(미할당 오더 수 포함). */
export interface BatchSummary {
  batchId: number
  workDate: string
  batchNo: string
  waveNo: number
  status: string
  orderTotal: number
  orderUnassigned: number
  itemTotal: number
}

/**
 * 초기화 요청 — ★ S-B2C-UX: 스코프를 **배치(batchId)** 로 재정의(소터 스코프 폐지).
 *   대상 배치의 오더 piece 를 아카이브·수량 리셋·COMPLETED→RUNNING 재개(백엔드 B2cResetRequest 미러).
 *   다건은 체크된 batchId 별 순차 호출 + 집계 토스트(force 체이닝). operatorName = 감사 귀속(OQ-3).
 */
export interface B2cResetRequest {
  batchId: number
  force: boolean
  operatorName?: string
}

/** 관리 액션 결과 — ok(= res.ok && status "S") + message + counts(선택). */
export interface ActionOutcome {
  ok: boolean
  message: string
  counts?: Record<string, number>
}

/** 엑셀 업로드 행별 오류(백엔드 B2cUploadRowError 미러) — row=엑셀 실제 행번호, message=사유. */
export interface UploadRowError {
  row: number
  message: string
}

/** 엑셀 업로드 결과 — ok(= res.ok && status "S") + message + counts + rowErrors(실패 시 행별). */
export interface UploadOutcome {
  ok: boolean
  message: string
  counts?: Record<string, number>
  rowErrors?: UploadRowError[]
}

interface RawApiResponse {
  status?: string
  message?: string
  counts?: Record<string, number>
  rowErrors?: UploadRowError[]
}

/**
 * 엑셀 업로드(multipart) — POST /api/b2c/test-data/upload.
 *   ⚠ Content-Type 을 수동 지정하지 않는다(FormData 가 multipart boundary 를 자동 설정 — 지정 시 파싱 실패).
 *   성공 판정 = res.ok && status "S". 파일 400·구조/행오류 200 F 모두 실패로 표면화(message + rowErrors).
 */
async function uploadExcel(file: File): Promise<UploadOutcome> {
  try {
    const fd = new FormData()
    fd.append('file', file)
    const res = await fetch(`${BASE}/upload`, {
      method: 'POST',
      headers: { Accept: 'application/json' }, // Content-Type 미지정(FormData 자동)
      body: fd,
    })
    let parsed: RawApiResponse = {}
    try {
      parsed = (await res.json()) as RawApiResponse
    } catch {
      /* 비-JSON 응답(예: 500) — 상태코드로 메시지 구성 */
    }
    const ok = res.ok && parsed.status === 'S'
    const message = parsed.message ?? (ok ? '업로드 완료.' : `업로드 실패 (HTTP ${res.status})`)
    return { ok, message, counts: parsed.counts, rowErrors: parsed.rowErrors }
  } catch (e) {
    return { ok: false, message: `네트워크 오류 — ${(e as Error).message}` }
  }
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
  batches: (signal?: AbortSignal) => getJson<BatchSummary[]>(`/batches`, signal),

  summary: (sorterChuteNo?: number, signal?: AbortSignal) =>
    getJson<SorterSummary[]>(
      `/summary${sorterChuteNo ? `?sorterChuteNo=${sorterChuteNo}` : ''}`,
      signal,
    ),

  detail: (sorterChuteNo: number, signal?: AbortSignal) =>
    getJson<CellDetail[]>(`/detail?sorterChuteNo=${sorterChuteNo}`, signal),

  generate: (req: B2cGenerateRequest) => runAction('/generate', req),

  reset: (req: B2cResetRequest) => runAction('/reset', req),

  upload: (file: File) => uploadExcel(file),
}

// ── TanStack Query 훅(조회) ──────────────────────────────────────────────────
export function useB2cBatches(refetchInterval: number | false) {
  return useQuery({
    queryKey: ['b2c-batches'],
    queryFn: ({ signal }) => b2cTestData.batches(signal),
    refetchInterval,
    placeholderData: keepPreviousData,
  })
}

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
