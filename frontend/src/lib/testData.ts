import { keepPreviousData, useQuery } from '@tanstack/react-query'

// ═══════════════════════════════════════════════════════════════════════════
// B2B test-data 관리 API 클라이언트 — /api/test-data/* (docs/B2B-DATAGEN.md §1·§7.1).
//
// ⚠ 성공 판정 = res.ok && body.status === "S" (§7.1 함정).
//   관리 액션(generate/reset/delete/upload)은 비즈니스 실패도 HTTP 200 + { status:"F", message }.
//   검증 실패(DataAnnotations·비존재 날짜·upload 3중 검증)만 HTTP 400 + 동일 body.
//   → 단순 res.ok 판정은 200 F 를 성공 오인하므로 금지. status 까지 본다.
// 기존 /api/monitor 클라이언트(lib/api.ts)와 분리 — BASE·시맨틱이 다르다.
// ═══════════════════════════════════════════════════════════════════════════

const BASE = '/api/test-data'

// ── 응답 형상(백엔드 DTO 미러 · camelCase) ───────────────────────────────────
export interface SummaryRow {
  bizDay: string
  batch: string
  count: number
  receiveTime: string | null
}

export interface DetailRow {
  id: number
  bizDay: string
  batch: string
  barcode: string
  barcode2: string | null
  chuteNo: string
  receiveTime: string | null
  createdAt: string
  inputStatus: string | null
  inTime: string | null
  sortStatus: string | null
  sortTime: string | null
}

export interface GenerateRequest {
  bizDay: string
  batch: string
  chuteNos: string
  barcodeCount: number
}

/** 아카이브 필터 3상태(§3.4) — detail 조회 archived 파라미터. */
export type ArchiveFilter = 'active' | 'all' | 'archivedOnly'

/** 요약 행 식별키 — (bizDay,batch) 조합. NUL 구분자로 값 충돌 회피. */
export function summaryKey(bizDay: string, batch: string): string {
  return `${bizDay}\u0000${batch}`
}

/** 관리 액션 결과 — ok(= res.ok && status "S") + 표면화할 message. */
export interface ActionOutcome {
  ok: boolean
  message: string
}

// ── 내부 헬퍼 ────────────────────────────────────────────────────────────────
interface RawApiResponse {
  status?: string
  message?: string
}

/**
 * 관리 액션 호출 — 성공 판정 res.ok && status "S". 200 F·400 은 실패로 표면화(body.message).
 * 네트워크/파싱 예외도 실패 Outcome 으로 환원(호출부가 토스트로 노출).
 */
async function runAction(path: string, init: RequestInit): Promise<ActionOutcome> {
  try {
    const res = await fetch(`${BASE}${path}`, init)
    let body: RawApiResponse = {}
    try {
      body = (await res.json()) as RawApiResponse
    } catch {
      // 비-JSON 응답(예: 413/500) — 상태코드로 메시지 구성.
    }
    const ok = res.ok && body.status === 'S'
    const message =
      body.message ?? (ok ? '완료되었습니다.' : `요청 실패 (HTTP ${res.status})`)
    return { ok, message }
  } catch (e) {
    return { ok: false, message: `네트워크 오류 — ${(e as Error).message}` }
  }
}

const JSON_HEADERS = { 'Content-Type': 'application/json', Accept: 'application/json' }

// 조회(read) — 실패는 throw 해 TanStack Query 가 에러 상태로 표면화.
async function getJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  const res = await fetch(`${BASE}${path}`, { headers: { Accept: 'application/json' }, signal })
  if (!res.ok) {
    // detail 필수 파라미터 누락(400) 등도 여기로 — body.message 가 있으면 노출.
    let msg = `요청 실패 (HTTP ${res.status})`
    try {
      const body = (await res.json()) as RawApiResponse
      if (body.message) msg = body.message
    } catch {
      /* ignore */
    }
    throw new Error(msg)
  }
  return res.json() as Promise<T>
}

function qs(params: Record<string, string | undefined>): string {
  const parts = Object.entries(params)
    .filter(([, v]) => v !== undefined && v !== '')
    .map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(String(v))}`)
  return parts.length ? `?${parts.join('&')}` : ''
}

// ── API 표면 (§1 표) ─────────────────────────────────────────────────────────
export const testData = {
  summary: (bizDay?: string, signal?: AbortSignal) =>
    getJson<SummaryRow[]>(`/summary${qs({ bizDay })}`, signal),

  detail: (bizDay: string, batch: string, archived: ArchiveFilter, signal?: AbortSignal) =>
    getJson<DetailRow[]>(`/detail${qs({ bizDay, batch, archived })}`, signal),

  generate: (req: GenerateRequest) =>
    runAction('/generate', { method: 'POST', headers: JSON_HEADERS, body: JSON.stringify(req) }),

  // reset/delete body 는 원시 JSON 배열([<long id>…]).
  reset: (ids: number[]) =>
    runAction('/reset', { method: 'POST', headers: JSON_HEADERS, body: JSON.stringify(ids) }),

  remove: (ids: number[]) =>
    runAction('', { method: 'DELETE', headers: JSON_HEADERS, body: JSON.stringify(ids) }),

  // multipart — Content-Type 은 브라우저가 boundary 와 함께 설정(수동 지정 금지).
  upload: (file: File) => {
    const fd = new FormData()
    fd.append('file', file)
    return runAction('/upload', { method: 'POST', body: fd })
  },
}

// ── TanStack Query 훅(조회) — refetchInterval 은 호출부가 autoRefresh 로 제어 ─────
export function useTestDataSummary(bizDay: string, refetchInterval: number | false) {
  return useQuery({
    queryKey: ['testdata-summary', bizDay],
    queryFn: ({ signal }) => testData.summary(bizDay, signal),
    refetchInterval,
    placeholderData: keepPreviousData,
  })
}

export function useTestDataDetail(
  selected: { bizDay: string; batch: string } | null,
  archived: ArchiveFilter,
  refetchInterval: number | false,
) {
  return useQuery({
    queryKey: ['testdata-detail', selected?.bizDay ?? null, selected?.batch ?? null, archived],
    queryFn: ({ signal }) => testData.detail(selected!.bizDay, selected!.batch, archived, signal),
    enabled: selected !== null,
    refetchInterval,
    placeholderData: keepPreviousData,
  })
}
