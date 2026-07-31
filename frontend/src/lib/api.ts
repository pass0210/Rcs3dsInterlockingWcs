// ═══════════════════════════════════════════════════════════════════════════
// WCS 모니터링 API 클라이언트 — /api/monitor/* (읽기 전용, F1)
//
// 백엔드 MonitoringController(카멜케이스 JSON)와 형상 1:1. 동일 출처 상대 경로("/api")로
// 호출 — 운영은 Wcs.Api 단일 서버, dev는 vite proxy(:5173 → :5205).
// ═══════════════════════════════════════════════════════════════════════════

// ── 백엔드 DTO 미러 타입 ─────────────────────────────────────────────────────
export interface Batch {
  id: number
  workDate: string
  batchNo: string
  waveNo: number
  status: string
  openedAt: string | null
  closedAt: string | null
}

export interface OrderProgress {
  id: number
  orderNo: string
  orderType: string
  destinationChuteNo: number | null
  status: string
  plannedQty: number
  reservedQty: number
  sortedQty: number
}

export interface OrderItem {
  id: number
  barcode: string
  plannedQty: number
  reservedQty: number
  sortedQty: number
}

export interface InFlightPiece {
  id: number
  pId: number
  barcode: string
  qty: number
  destinationChuteNo: number | null
  agvNo: number | null
  inductionNo: number | null
  status: string
  depositedAt: string | null
  createdAt: string
}

export interface SorterStatus {
  destId: number
  chuteNo: number
  online: boolean
  ready: boolean
  full: boolean
  paused: boolean
}

/** 전 목적지(CHUTE + SORTER_3D) 열거 — GET /api/monitor/destinations(설비 관리·슈트 제어 소스). */
export interface Destination {
  id: number
  chuteNo: number
  destType: string // "CHUTE" | "SORTER_3D"
  floor: number | null
  status: string // "NORMAL" | "PAUSED"
  isActive: boolean
  online: boolean
  ready: boolean
  full: boolean
  paused: boolean
  workFullQty: number | null // CHUTE 전용
  lastClearedAt: string | null // CHUTE 전용
  cellTotal: number | null // SORTER_3D 전용
  cellEnabled: number | null // SORTER_3D 전용
}

export interface CellStatus {
  cellNo: number
  capacity: number | null
  currentQty: number
  occupied: boolean
  enabled: boolean
  assignedOrderNo: string | null
}

export interface SorterCommand {
  id: number
  pId: number | null
  barcode: string | null
  cellNo: number
  cSeq: number
  rSeq: number | null
  status: string
  cWrittenAt: string
  rFlagAt: string | null
}

export interface OperationLog {
  id: number
  at: string
  category: string
  action: string
  level: string
  sorterChuteNo: number | null
  destinationId: number | null
  barcode: string | null
  pId: number | null
  detail: string | null
}

export interface Paged<T> {
  items: T[]
  nextCursor: number | null
}

// 평균 사이클 시간(분류시작~복귀) — GET /api/monitor/cycle-time-avg(S-SORT-CYCLE-TIME-METRIC).
//   avgSeconds = Σ(복귀−분류시작)/n(초·raw double). n=0 → avgSeconds=null(측정 데이터 없음).
export interface CycleTimeAvg {
  avgSeconds: number | null
  n: number
}

// 전용 추적 로그 백로그 레코드(S-TRACE-LOG-VIEWER) — 백엔드 TraceRecord 미러(SignalR TraceEvent와 동형).
export interface TraceRecord {
  eventNo: number
  event: string
  at: string
  pId: number | null
  cSeq: number | null
  chuteNo: number | null
  destId: number | null
  cellNo: number | null
  floor: number | null
  inductionNo: number | null
  trigger: string | null
  detail: string | null
}

// ── fetch 래퍼 (실패 시 명확한 에러 — TanStack Query가 에러 상태로 표면화) ─────
const BASE = '/api/monitor'

async function getJson<T>(path: string): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    headers: { Accept: 'application/json' },
  })
  if (!res.ok) {
    throw new Error(`요청 실패 (${res.status} ${res.statusText}) — ${path}`)
  }
  return res.json() as Promise<T>
}

function qs(params: Record<string, string | number | null | undefined>): string {
  const parts = Object.entries(params)
    .filter(([, v]) => v !== null && v !== undefined && v !== '')
    .map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(String(v))}`)
  return parts.length ? `?${parts.join('&')}` : ''
}

// ── 엔드포인트 (E1~E7) ───────────────────────────────────────────────────────
export const api = {
  batches: (take = 50) => getJson<Batch[]>(`/batches${qs({ take })}`),

  orders: (batchId?: number, status?: string, take = 100) =>
    getJson<OrderProgress[]>(`/orders${qs({ batchId, status, take })}`),

  orderItems: (orderId: number) => getJson<OrderItem[]>(`/orders/${orderId}/items`),

  inFlight: (take = 50, cursor?: number | null) =>
    getJson<Paged<InFlightPiece>>(`/pieces/in-flight${qs({ take, cursor })}`),

  sorters: () => getJson<SorterStatus[]>(`/sorters`),

  destinations: () => getJson<Destination[]>(`/destinations`),

  cells: (destId: number) => getJson<CellStatus[]>(`/sorters/${destId}/cells`),

  sorterCommands: (destId?: number, take = 50, cursor?: number | null) =>
    getJson<Paged<SorterCommand>>(`/sorter-commands${qs({ destId, take, cursor })}`),

  // operation_log 테일 백로그(F2). category 미지정 시 POLL_CHANGE 기본 제외(옵트인).
  operationLog: (
    opts: {
      category?: string
      level?: string
      sorterChuteNo?: number
      take?: number
      cursor?: number | null
    } = {},
  ) =>
    getJson<Paged<OperationLog>>(
      `/operation-log${qs({
        category: opts.category,
        level: opts.level,
        sorterChuteNo: opts.sorterChuteNo,
        take: opts.take ?? 100,
        cursor: opts.cursor,
      })}`,
    ),

  // 전용 추적 로그 백로그(S-TRACE-LOG-VIEWER). 최근 N개 트레이스 레코드(시계열 오름차순).
  // 필터: eventNo(1~6)·pId·cSeq. 로그 디렉터리 부재 시 빈 배열(200).
  trace: (
    opts: { take?: number; eventNo?: number; pId?: number; cSeq?: number } = {},
  ) =>
    getJson<TraceRecord[]>(
      `/trace${qs({
        take: opts.take ?? 100,
        eventNo: opts.eventNo,
        pId: opts.pId,
        cSeq: opts.cSeq,
      })}`,
    ),

  // 평균 사이클 시간(분류시작~복귀). 파라미터 없음(전 행 집계·ArchivedAt 무필터). n=0 → avgSeconds=null.
  cycleTimeAvg: () => getJson<CycleTimeAvg>(`/cycle-time-avg`),
}
