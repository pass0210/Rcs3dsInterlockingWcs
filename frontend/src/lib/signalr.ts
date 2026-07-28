// ═══════════════════════════════════════════════════════════════════════════
// WcsMonitorHub SignalR 클라이언트 (F2 실시간) — /hubs/monitor
//
// 백엔드 WcsMonitorHub + MonitorRelayService와 1:1(카멜케이스 payload). 읽기 전용 관측.
//   · 접속/재연결 시 Bootstrap(전체 소터 워드 스냅샷) 수신 → 워드 상태 완전 복구.
//   · RegisterDelta(변화분)로 D0~D6 개별 갱신 + 마지막 변경 시각·하이라이트 트리거.
//   · SorterTransition(Online/Offline), Heartbeat(주기 스냅샷 재전송)로 갭 보정.
//   · OpLog(operation_log 테일) — 콜백 스트림(고빈도라 워드 상태와 분리해 리렌더 최소화).
//
// 운영: 동일 출처 상대 경로('/hubs/monitor'). dev: vite proxy(ws:true).
// ═══════════════════════════════════════════════════════════════════════════
import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr'
import { useSyncExternalStore } from 'react'

// ── 백엔드 payload 미러 타입 ─────────────────────────────────────────────────
export interface SorterWord {
  destId: number
  chuteNo: number
  online: boolean
  cCellNo: number // D0
  cSeq: number // D1
  rCellNo: number // D2
  rSeq: number // D3
  cFlag: boolean // D4.0
  rFlag: boolean // D4.1
  ready: boolean // D4.2
  curFloor: number // D5
  tgtFloor: number // D6
  at: string
}

export interface RegisterDelta {
  destId: number
  chuteNo: number
  reg: string
  oldValue: number
  newValue: number
  at: string
}

export interface SorterTransition {
  destId: number
  chuteNo: number
  online: boolean
  at: string
}

export interface OpLogEntry {
  id: number | null
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

// 전용 추적 로그 이벤트(S-TRACE-LOG-VIEWER) — 백엔드 TraceRecord 미러(카멜케이스).
//   eventNo(1~6) = 이벤트 종류 태그. 피스 흐름(3~6)은 pId+(chuteNo,cSeq)로 상관.
export interface TraceEvent {
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

export type ConnStatus = 'connecting' | 'connected' | 'reconnecting' | 'disconnected'

// 레지스터 키 — 백엔드 EmitRegisterChanges의 reg 문자열 + Online(전이).
export type RegKey =
  | 'C_CellNo'
  | 'C_Seq'
  | 'R_CellNo'
  | 'R_Seq'
  | 'C_Flag'
  | 'R_Flag'
  | 'Ready'
  | 'CurFloor'
  | 'TgtFloor'
  | 'Online'

// 소터별 워드 상태 — 값 + 필드별 마지막 변경 시각 + 하이라이트 재트리거 시퀀스.
export interface SorterWordState {
  word: SorterWord
  changedAt: Partial<Record<RegKey, string>>
  flashSeq: Partial<Record<RegKey, number>>
}

export interface MonitorState {
  status: ConnStatus
  sorters: Map<number, SorterWordState> // keyed by destId
  version: number
}

// ── 클라이언트 싱글톤 ────────────────────────────────────────────────────────
class MonitorHubClient {
  private connection: HubConnection | null = null
  private startPromise: Promise<void> | null = null
  private pollOptIn = false

  private state: MonitorState = { status: 'disconnected', sorters: new Map(), version: 0 }
  private stateListeners = new Set<() => void>()
  private opLogListeners = new Set<(e: OpLogEntry) => void>()
  // 전용 추적(trace) 그룹 — 뷰어 페이지 마운트 시만 옵트인 구독(창 닫히면 해제 → 서버 push no-op).
  private traceListeners = new Set<(e: TraceEvent) => void>()
  private traceOptIn = false

  // ── React 바인딩(useSyncExternalStore) ────────────────────────────────────
  getState = (): MonitorState => this.state
  subscribe = (l: () => void): (() => void) => {
    this.stateListeners.add(l)
    return () => {
      this.stateListeners.delete(l)
    }
  }

  // oplog는 고빈도라 워드 상태와 분리 — per-entry 콜백(테일 append + 쿼리 무효화).
  subscribeOpLog = (l: (e: OpLogEntry) => void): (() => void) => {
    this.opLogListeners.add(l)
    return () => {
      this.opLogListeners.delete(l)
    }
  }

  isPollChangeOptIn = (): boolean => this.pollOptIn

  // 전용 추적 스트림 구독 — 첫 구독자에서 서버 trace 그룹 가입, 마지막 해제에서 탈퇴(페이지 수명 종속).
  //   연결(monitorHub)은 앱 수명 유지하고 trace 구독만 여닫는다 → "창 닫히면 스트림 종료" 요건(W6).
  subscribeTrace = (l: (e: TraceEvent) => void): (() => void) => {
    this.traceListeners.add(l)
    if (this.traceListeners.size === 1) {
      this.traceOptIn = true
      void this.syncTraceOptIn()
    }
    return () => {
      this.traceListeners.delete(l)
      if (this.traceListeners.size === 0) {
        this.traceOptIn = false
        void this.syncTraceOptIn()
      }
    }
  }

  // ── 연결 수명 ──────────────────────────────────────────────────────────────
  connect(): void {
    const conn = this.ensureConnection()
    if (conn.state !== HubConnectionState.Disconnected) return
    if (this.startPromise) return
    this.setStatus('connecting')
    this.startPromise = conn
      .start()
      .then(async () => {
        // 성공 후 반드시 리셋 — 남겨두면 이후 onclose 안전망의 connect() 재진입이
        // 영구 차단돼 재접속 경로가 이중으로 막힌다(F2-CR-M2).
        this.startPromise = null
        this.setStatus('connected')
        await this.syncPollOptIn()
        await this.syncTraceOptIn()
      })
      .catch((err: unknown) => {
        this.startPromise = null
        this.setStatus('disconnected')
        console.error('[hub] 연결 실패 — 2s 후 재시도', err)
        setTimeout(() => this.connect(), 2000)
      })
  }

  async setPollChangeOptIn(on: boolean): Promise<void> {
    this.pollOptIn = on
    await this.syncPollOptIn()
  }

  private async syncPollOptIn(): Promise<void> {
    const conn = this.connection
    if (!conn || conn.state !== HubConnectionState.Connected) return
    try {
      await conn.invoke(this.pollOptIn ? 'SubscribePollChange' : 'UnsubscribePollChange')
    } catch (err) {
      console.warn('[hub] POLL_CHANGE 옵트인 동기화 실패', err)
    }
  }

  private async syncTraceOptIn(): Promise<void> {
    const conn = this.connection
    if (!conn || conn.state !== HubConnectionState.Connected) return
    try {
      await conn.invoke(this.traceOptIn ? 'SubscribeTrace' : 'UnsubscribeTrace')
    } catch (err) {
      console.warn('[hub] trace 옵트인 동기화 실패', err)
    }
  }

  private ensureConnection(): HubConnection {
    if (this.connection) return this.connection
    const conn = new HubConnectionBuilder()
      .withUrl('/hubs/monitor')
      // 무한 재연결(상한 백오프 0→2s→10s→30s, 이후 30s 고정 — F2-CR-M2).
      // 기본 정책(0/2/10/30s 4회 후 포기)은 백엔드 장시간 다운(>~42s) 후 영구 단절돼
      // 무인 관제 월보드가 새로고침 없이는 복구 불가. 항상 숫자를 반환해 포기하지 않는다.
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (ctx) => {
          const delays = [0, 2000, 10000, 30000]
          return ctx.previousRetryCount < delays.length ? delays[ctx.previousRetryCount] : 30000
        },
      })
      .configureLogging(LogLevel.Warning)
      .build()

    conn.on('Bootstrap', (words: SorterWord[]) => this.applySnapshot(words))
    conn.on('Heartbeat', (words: SorterWord[]) => this.applySnapshot(words))
    conn.on('RegisterDelta', (d: RegisterDelta) => this.applyDelta(d))
    conn.on('SorterTransition', (t: SorterTransition) => this.applyTransition(t))
    conn.on('OpLog', (e: OpLogEntry) => this.emitOpLog(e))
    conn.on('Trace', (e: TraceEvent) => this.emitTrace(e))

    conn.onreconnecting(() => this.setStatus('reconnecting'))
    conn.onreconnected(async () => {
      // 서버 OnConnectedAsync가 재접속 시 Bootstrap을 다시 보냄 → 워드 상태 자동 복구.
      this.setStatus('connected')
      await this.syncPollOptIn()
      await this.syncTraceOptIn()
    })
    conn.onclose(() => {
      // 안전망(F2-CR-M2): 무한 재시도 정책으로 재시도 소진 onclose는 정상 도달하지 않으나,
      // 비복구 종료(협상 단계 치명 오류 등)로 닫히면 백오프 후 스스로 재기동한다.
      // 이 클라이언트는 명시 stop() 경로가 없으므로 자동 재기동이 안전하다.
      // 재접속 성공 시 connect()의 시작 경로가 Bootstrap 수신 + syncPollOptIn을 동일하게 수행.
      this.setStatus('disconnected')
      setTimeout(() => this.connect(), 2000)
    })

    this.connection = conn
    return conn
  }

  // ── 상태 변이 ──────────────────────────────────────────────────────────────
  private setStatus(status: ConnStatus): void {
    if (this.state.status === status) return
    this.commit({ ...this.state, status })
  }

  private commit(next: Omit<MonitorState, 'version'>): void {
    this.state = { ...next, version: this.state.version + 1 }
    for (const l of this.stateListeners) l()
  }

  // Bootstrap/Heartbeat — 전체 스냅샷 병합(값 갱신, changedAt는 보존 — 실제 변경 시각 미상).
  private applySnapshot(words: SorterWord[]): void {
    if (!Array.isArray(words)) return
    const sorters = new Map(this.state.sorters)
    for (const word of words) {
      const prev = sorters.get(word.destId)
      sorters.set(word.destId, {
        word,
        changedAt: prev?.changedAt ?? {},
        flashSeq: prev?.flashSeq ?? {},
      })
    }
    this.commit({ status: this.state.status, sorters })
  }

  // RegisterDelta — 해당 필드 1개 갱신 + 마지막 변경 시각 + 하이라이트 재트리거.
  private applyDelta(d: RegisterDelta): void {
    const prev = this.state.sorters.get(d.destId)
    if (!prev) return // 부트스트랩 전 델타 — 부트스트랩/하트비트가 곧 보정.
    const word = applyRegToWord(prev.word, d.reg, d.newValue)
    const reg = d.reg as RegKey
    const sorters = new Map(this.state.sorters)
    sorters.set(d.destId, {
      word,
      changedAt: { ...prev.changedAt, [reg]: d.at },
      flashSeq: { ...prev.flashSeq, [reg]: (prev.flashSeq[reg] ?? 0) + 1 },
    })
    this.commit({ status: this.state.status, sorters })
  }

  private applyTransition(t: SorterTransition): void {
    const prev = this.state.sorters.get(t.destId)
    const sorters = new Map(this.state.sorters)
    const base: SorterWord = prev?.word ?? emptyWord(t.destId, t.chuteNo)
    sorters.set(t.destId, {
      word: { ...base, online: t.online, at: t.at },
      changedAt: { ...(prev?.changedAt ?? {}), Online: t.at },
      flashSeq: { ...(prev?.flashSeq ?? {}), Online: (prev?.flashSeq.Online ?? 0) + 1 },
    })
    this.commit({ status: this.state.status, sorters })
  }

  private emitOpLog(e: OpLogEntry): void {
    for (const l of this.opLogListeners) {
      try {
        l(e)
      } catch (err) {
        console.warn('[hub] oplog 리스너 예외', err)
      }
    }
  }

  private emitTrace(e: TraceEvent): void {
    for (const l of this.traceListeners) {
      try {
        l(e)
      } catch (err) {
        console.warn('[hub] trace 리스너 예외', err)
      }
    }
  }
}

// reg 문자열 → 워드 필드 갱신(불변 복사본 반환). 비트는 0/1 → boolean.
function applyRegToWord(word: SorterWord, reg: string, v: number): SorterWord {
  switch (reg) {
    case 'C_CellNo':
      return { ...word, cCellNo: v }
    case 'C_Seq':
      return { ...word, cSeq: v }
    case 'R_CellNo':
      return { ...word, rCellNo: v }
    case 'R_Seq':
      return { ...word, rSeq: v }
    case 'C_Flag':
      return { ...word, cFlag: v !== 0 }
    case 'R_Flag':
      return { ...word, rFlag: v !== 0 }
    case 'Ready':
      return { ...word, ready: v !== 0 }
    case 'CurFloor':
      return { ...word, curFloor: v }
    case 'TgtFloor':
      return { ...word, tgtFloor: v }
    default:
      return word
  }
}

function emptyWord(destId: number, chuteNo: number): SorterWord {
  return {
    destId,
    chuteNo,
    online: false,
    cCellNo: 0,
    cSeq: 0,
    rCellNo: 0,
    rSeq: 0,
    cFlag: false,
    rFlag: false,
    ready: false,
    curFloor: 0,
    tgtFloor: 0,
    at: new Date().toISOString(),
  }
}

export const monitorHub = new MonitorHubClient()

// ── React 훅 ─────────────────────────────────────────────────────────────────
/** 전체 실시간 상태(연결 상태 + 소터 워드 맵) 구독. */
export function useMonitorState(): MonitorState {
  return useSyncExternalStore(monitorHub.subscribe, monitorHub.getState, monitorHub.getState)
}
