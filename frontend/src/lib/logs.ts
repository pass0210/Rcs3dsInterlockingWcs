import { keepPreviousData, useQuery } from '@tanstack/react-query'
import type { ArchiveFilter } from '@/lib/testData'

// ═══════════════════════════════════════════════════════════════════════════
// B2B 조회 전용 API 클라이언트 — 로그(투입/분류/API 호출)·3-way 비교·박스(S-B2B-3b).
//   소비 백엔드: S-B2B-3a(develop 병합) — LogController·TestDataController·BoxesController.
//   응답 형상은 backend/src/Wcs.Api/B2B/QueryDtos.cs 와 **정확히 일치**(camelCase · System.Text.Json Web 기본).
//
// ⚠ 조회(read)는 실패를 throw 해 TanStack Query 가 에러 상태로 표면화(lib/testData.ts getJson 관용구 미러).
//   관리 액션(status "S/F")과 달리 여기 6개 엔드포인트는 전부 순수 조회 — 성공=200/실패=400(#17 등).
// ⚠ E4 export 만 바이너리 다운로드 — blob + Content-Disposition 파일명 파싱(관리 액션과 별개 반환형).
// 아카이브 필터 3상태(active|all|archivedOnly)는 lib/testData.ts 의 ArchiveFilter 어휘 재사용.
// ═══════════════════════════════════════════════════════════════════════════

// ── 응답 형상(백엔드 QueryDtos.cs 미러 · camelCase) ──────────────────────────

/** E1/E2 — 투입(INPUT)/분류(SORT) 로그 행. equipmentNo: INPUT=인덕션 / SORT=슈트. archivedAt: null=활성. */
export interface TestLogRow {
  id: number
  bizDay: string
  batch: string
  barcode: string
  equipmentNo: string | null
  pid: string | null
  status: string | null
  reason: string | null
  logTime: string | null
  chuteNo: string | null // 등록 test_data 슈트(파생)
  receiveTime: string | null // 등록 test_data 수신시각(파생)
  archivedAt: string | null // null=활성 · 세팅=보관
}

/** E3 — RCS API 호출 이력 행(§9.6). 최대 500건 · archived 없음. */
export interface ApiCallLogRow {
  id: number
  endpoint: string
  httpMethod: string
  requestBody: string | null
  responseStatus: string | null
  responseBody: string | null
  httpStatusCode: number
  durationMs: number
  clientIp: string | null
  errorMessage: string | null
  calledAt: string
}

/** E5 — 투입/분류/결과 3-way 비교 행. isMatch=3자 존재+슈트 일치 / isMissing=셋 중 하나라도 없음. */
export interface ComparisonRow {
  bizDay: string
  batch: string
  barcode: string
  registeredChuteNo: string
  hasInput: boolean
  hasSort: boolean
  hasResult: boolean
  inputStatus: string | null
  inputTime: string | null
  sortChuteNo: string | null
  sortStatus: string | null
  sortTime: string | null
  resultChuteNo: string | null
  isMatch: boolean
  isMissing: boolean
}

/** E6 — 박스 내품 행. */
export interface BoxItemRow {
  barcode: string
  qty: number
}

/** E6 — 박스 헤더 + 내품(items 중첩 배열). */
export interface BoxRow {
  id: number
  bizDay: string
  batch: string
  boxNo: string
  chuteNo: string
  endTime: string | null
  createdAt: string
  items: BoxItemRow[]
}

/** 투입/분류 로그 종류 — E1/E2 라우트 분기 키. */
export type TestLogKind = 'input' | 'sort'

// ── 내부 헬퍼 ────────────────────────────────────────────────────────────────
const XLSX_MIME = 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet'

interface RawApiResponse {
  message?: string
}

// 조회(read) — 실패는 throw 해 TanStack Query 가 에러 상태로 표면화(fail-loud, 삼키지 않음).
async function getJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  const res = await fetch(path, { headers: { Accept: 'application/json' }, signal })
  if (!res.ok) {
    // 비존재 날짜(400 #17) 등 — body.message 가 있으면 노출.
    let msg = `요청 실패 (HTTP ${res.status})`
    try {
      const body = (await res.json()) as RawApiResponse
      if (body.message) msg = body.message
    } catch {
      /* 비-JSON 응답 — 상태코드 메시지 유지 */
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

// ── API 표면(E1~E6) ──────────────────────────────────────────────────────────
export const logsApi = {
  // E1/E2 — 라우트만 다르고 형상 동일(TestLogRow[]).
  testLogs: (kind: TestLogKind, bizDay: string, archived: ArchiveFilter, signal?: AbortSignal) =>
    getJson<TestLogRow[]>(`/api/logs/${kind}${qs({ bizDay, archived })}`, signal),

  // E3 — date 미지정 시 전체(최대 500건). archived 없음.
  apiCalls: (date: string | undefined, signal?: AbortSignal) =>
    getJson<ApiCallLogRow[]>(`/api/logs/api-calls${qs({ date })}`, signal),

  // E5 — 3-way 비교(TestDataController 라우트).
  comparison: (bizDay: string, archived: ArchiveFilter, signal?: AbortSignal) =>
    getJson<ComparisonRow[]>(`/api/test-data/comparison${qs({ bizDay, archived })}`, signal),

  // E6 — 박스 + 내품(bizDay 필수, batch 선택).
  boxes: (bizDay: string, batch: string | undefined, signal?: AbortSignal) =>
    getJson<BoxRow[]>(`/api/boxes${qs({ bizDay, batch })}`, signal),
}

// ── E4 Excel 내보내기 — 바이너리 다운로드(blob + Content-Disposition 파일명) ────
export interface ExportOutcome {
  ok: boolean
  message?: string
}

/**
 * Content-Disposition 헤더에서 파일명 추출. RFC 5987(filename*=UTF-8'') 우선, 없으면 filename=.
 * 동일 출처(dev=vite proxy·운영=단일 서버)라 헤더 판독 가능.
 */
function parseContentDispositionFileName(header: string | null): string | null {
  if (!header) return null
  const star = /filename\*=(?:UTF-8'')?([^;]+)/i.exec(header)
  if (star?.[1]) {
    try {
      return decodeURIComponent(star[1].trim().replace(/^"|"$/g, ''))
    } catch {
      /* 폴백 아래 filename= 로 */
    }
  }
  const plain = /filename="?([^";]+)"?/i.exec(header)
  return plain?.[1]?.trim() ?? null
}

// <a download> 트리거 — object URL 생성 후 클릭·정리(즉시 revoke 는 일부 브라우저 다운로드 취소 위험 → 지연).
function triggerDownload(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = fileName
  document.body.appendChild(a)
  a.click()
  a.remove()
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}

/**
 * E4 GET /api/logs/export?bizDay=&batch= — xlsx 바이너리 다운로드.
 * 성공 시 파일 다운로드 트리거 + ok. 실패(400 JSON message·네트워크)는 message 로 환원(호출부 토스트).
 */
export async function exportLogs(bizDay: string, batch?: string): Promise<ExportOutcome> {
  try {
    const res = await fetch(`/api/logs/export${qs({ bizDay, batch })}`, {
      headers: { Accept: XLSX_MIME },
    })
    if (!res.ok) {
      // export bizDay 누락·비존재 날짜·생성 오류 → 400 + { message }.
      let msg = `내보내기 실패 (HTTP ${res.status})`
      try {
        const body = (await res.json()) as RawApiResponse
        if (body.message) msg = body.message
      } catch {
        /* 비-JSON — 상태코드 메시지 유지 */
      }
      return { ok: false, message: msg }
    }
    const blob = await res.blob()
    const fileName =
      parseContentDispositionFileName(res.headers.get('Content-Disposition')) ??
      `input_sort_logs_${bizDay}.xlsx`
    triggerDownload(blob, fileName)
    return { ok: true }
  } catch (e) {
    return { ok: false, message: `네트워크 오류 — ${(e as Error).message}` }
  }
}

// ── TanStack Query 훅 — refetchInterval 은 호출부가 autoRefresh 로 제어(DataGenerator 동형) ──
export function useTestLogs(
  kind: TestLogKind,
  bizDay: string,
  archived: ArchiveFilter,
  refetchInterval: number | false,
) {
  return useQuery({
    queryKey: ['logs-testlog', kind, bizDay, archived],
    queryFn: ({ signal }) => logsApi.testLogs(kind, bizDay, archived, signal),
    refetchInterval,
    placeholderData: keepPreviousData,
  })
}

export function useApiCallLogs(date: string | undefined, refetchInterval: number | false) {
  return useQuery({
    queryKey: ['logs-apicalls', date ?? 'all'],
    queryFn: ({ signal }) => logsApi.apiCalls(date, signal),
    refetchInterval,
    placeholderData: keepPreviousData,
  })
}

export function useComparison(
  bizDay: string,
  archived: ArchiveFilter,
  refetchInterval: number | false,
) {
  return useQuery({
    queryKey: ['comparison', bizDay, archived],
    queryFn: ({ signal }) => logsApi.comparison(bizDay, archived, signal),
    refetchInterval,
    placeholderData: keepPreviousData,
  })
}

export function useBoxes(bizDay: string, batch: string | undefined, refetchInterval: number | false) {
  return useQuery({
    queryKey: ['boxes', bizDay, batch ?? ''],
    queryFn: ({ signal }) => logsApi.boxes(bizDay, batch, signal),
    refetchInterval,
    placeholderData: keepPreviousData,
  })
}
