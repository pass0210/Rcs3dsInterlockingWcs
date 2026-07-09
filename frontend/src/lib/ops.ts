// ═══════════════════════════════════════════════════════════════════════════
// B2C 운영 제어(Ops) API 클라이언트 — /api/ops/* (S-F3b, F3a OpsController 소비).
//
// ⚠ 이 페이지의 버튼 클릭은 백엔드 단일 쓰기 큐를 경유해 실 PLC 동작을 유발한다.
//   UI는 오직 이 HTTP 표면만 호출한다(Modbus 직접 접근 0 — 절대규칙 #1은 백엔드가 강제).
//
// 읽기 클라이언트(lib/api.ts, /api/monitor)와 BASE·시맨틱이 다르므로 별도 파일로 분리.
//   · 성공 = HTTP 200 + 엔드포인트별 body(status:"paused"/"resumed"/"enqueued"…).
//   · 실패 = 400(공백 operator·범위 초과)·404(미등록 destId)·409(동시 전이 충돌) + { error }.
// Fail-Loud 계약: 실패를 성공으로 위장하지 않는다 — !ok면 body.error를 그대로 표면화하고,
//   O4 pingPongGuard(진행 중이라 컨슈머가 스킵 가능)도 성공으로 숨기지 않고 호출부에 전달한다.
// ═══════════════════════════════════════════════════════════════════════════

const BASE = '/api/ops'

// ── OpsLimits 클라이언트 미러(선제 UX) ───────────────────────────────────────
// 근거: 백엔드 backend/src/Wcs.Api/Infrastructure/WcsOptions.cs OpsWriteLimits 기본값
//   (MaxTgtFloor=20, MaxCellNo=1000, MaxCellSeq=30000)과 동기. F3a가 한계값 조회
//   엔드포인트를 제공하지 않으므로 프론트 상수 1곳으로 두고(근거 주석 명시), 서버 400이
//   최종 권위다(절대규칙 #7 — 클라 검증은 서버 경계를 미러할 뿐 대체하지 않는다).
export const OPS_LIMITS = {
  minTgtFloor: 1,
  maxTgtFloor: 20,
  minCellNo: 1,
  maxCellNo: 1000,
  minSeq: 1,
  maxSeq: 30000,
} as const

// ── 결과 형상(Fail-Loud) ─────────────────────────────────────────────────────
/** Ops 호출 결과 — 성공은 엔드포인트별 data, 실패는 status·message(정직 표면화). */
export type OpsResult<T> =
  | { ok: true; data: T }
  | { ok: false; status: number; message: string }

/** O2/O3 pause·resume 응답 — outcome(Transitioned/AlreadyInState). */
export interface TransitionData {
  status: string
  destId: number
  outcome: string // "Transitioned" | "AlreadyInState"
  operatorName: string
}

/** O4 SetTgtFloor 응답 — 정직 표면화 필드(currentTgtFloor·pingPongGuard). */
export interface SetTgtFloorData {
  status: string // "enqueued"
  destId: number
  floor: number
  currentTgtFloor: number
  pingPongGuard: boolean // true면 진행 중이라 컨슈머가 이 쓰기를 스킵할 수 있음.
  operatorName: string
}

/** O5 ClearR · O6 CellAssign 공통 enqueue 응답. */
export interface EnqueueData {
  status: string // "enqueued"
  destId: number
  operatorName: string
  cellNo?: number
  seq?: number
}

// ── 공통 POST 헬퍼 ───────────────────────────────────────────────────────────
const JSON_HEADERS = { 'Content-Type': 'application/json', Accept: 'application/json' }

/**
 * Ops POST 호출 — 성공(2xx)이면 body를 data로, 실패면 body.error를 message로 환원한다.
 * 네트워크/파싱 예외도 실패 OpsResult로 환원(status:0) — 호출부가 토스트로 노출.
 * 절대 실패를 삼켜 성공으로 위장하지 않는다(Fail-Loud).
 */
async function postOps<T>(path: string, body: unknown): Promise<OpsResult<T>> {
  try {
    const res = await fetch(`${BASE}${path}`, {
      method: 'POST',
      headers: JSON_HEADERS,
      body: JSON.stringify(body),
    })
    let json: Record<string, unknown> = {}
    try {
      json = (await res.json()) as Record<string, unknown>
    } catch {
      // 비-JSON 응답(예: 500 HTML) — 상태코드로 메시지 구성.
    }
    if (!res.ok) {
      const message = typeof json.error === 'string' ? json.error : `요청 실패 (HTTP ${res.status})`
      return { ok: false, status: res.status, message }
    }
    return { ok: true, data: json as T }
  } catch (e) {
    return { ok: false, status: 0, message: `네트워크 오류 — ${(e as Error).message}` }
  }
}

// ── 클라이언트 범위 검증(선제 UX — 서버 400 미러) ─────────────────────────────
/** floor 검증: 정수 · 1..maxTgtFloor. 위반 시 사용자 메시지, 정상은 null. */
export function validateFloor(floor: number): string | null {
  if (!Number.isInteger(floor)) return '목표층은 정수여야 합니다.'
  if (floor < OPS_LIMITS.minTgtFloor)
    return `목표층은 ${OPS_LIMITS.minTgtFloor} 이상이어야 합니다(수동 클리어 floor=0은 미노출).`
  if (floor > OPS_LIMITS.maxTgtFloor)
    return `목표층은 ${OPS_LIMITS.maxTgtFloor} 이하여야 합니다(PLC 레지스터 상한).`
  return null
}

/** cellNo 검증: 정수 · 1..maxCellNo. */
export function validateCellNo(cellNo: number): string | null {
  if (!Number.isInteger(cellNo)) return '셀 번호는 정수여야 합니다.'
  if (cellNo < OPS_LIMITS.minCellNo) return `셀 번호는 ${OPS_LIMITS.minCellNo} 이상이어야 합니다.`
  if (cellNo > OPS_LIMITS.maxCellNo)
    return `셀 번호는 ${OPS_LIMITS.maxCellNo} 이하여야 합니다(PLC 레지스터 상한).`
  return null
}

/** seq(C_Seq) 검증: 정수 · 1..maxSeq. */
export function validateSeq(seq: number): string | null {
  if (!Number.isInteger(seq)) return '명령 순번은 정수여야 합니다.'
  if (seq < OPS_LIMITS.minSeq) return `명령 순번은 ${OPS_LIMITS.minSeq} 이상이어야 합니다.`
  if (seq > OPS_LIMITS.maxSeq)
    return `명령 순번은 ${OPS_LIMITS.maxSeq} 이하여야 합니다(PLC 레지스터 상한).`
  return null
}

// ── API 표면 (O2/O3 pause·resume, O4 tgtfloor, O5 clear-r, O6 cell-assign) ────
// 슈트 clear(O1)·CHUTE pause/resume은 F3b 스코프 제외(읽기 열거 엔드포인트 부재 — 후속 이관).
export const ops = {
  /** O2 목적지 정지 — POST /api/ops/destinations/{destId}/pause */
  pause: (destId: number, operatorName: string) =>
    postOps<TransitionData>(`/destinations/${destId}/pause`, { operatorName }),

  /** O3 목적지 재개 — POST /api/ops/destinations/{destId}/resume */
  resume: (destId: number, operatorName: string) =>
    postOps<TransitionData>(`/destinations/${destId}/resume`, { operatorName }),

  /** O4 소터 TgtFloor 쓰기 — POST /api/ops/sorters/{destId}/tgtfloor */
  setTgtFloor: (destId: number, floor: number, operatorName: string) =>
    postOps<SetTgtFloorData>(`/sorters/${destId}/tgtfloor`, { floor, operatorName }),

  /** O5 소터 R 영역 강제 클리어(진단) — POST /api/ops/sorters/{destId}/clear-r */
  clearR: (destId: number, operatorName: string) =>
    postOps<EnqueueData>(`/sorters/${destId}/clear-r`, { operatorName }),

  /** O6 소터 셀 지정(고위험 진단) — POST /api/ops/sorters/{destId}/cell-assign */
  cellAssign: (destId: number, cellNo: number, seq: number, operatorName: string) =>
    postOps<EnqueueData>(`/sorters/${destId}/cell-assign`, { cellNo, seq, operatorName }),
}
