// 상태 문자열 → Badge 톤 매핑 (계기판 상태색 체계 일관 적용).
type Tone = 'neutral' | 'online' | 'offline' | 'warn' | 'busy' | 'accent'

const TONE: Record<string, Tone> = {
  // 완료/적재 성공 = online
  COMPLETED: 'online',
  LOADED: 'online',
  // 진행 중 = busy/accent
  RUNNING: 'busy',
  PERMITTED: 'busy',
  RESERVED: 'accent',
  QUERIED: 'accent',
  CELL_ASSIGNED: 'busy',
  DEPOSITED: 'busy',
  SENT: 'accent',
  // 대기 = neutral
  WAITING: 'neutral',
  // 실패/취소 = offline
  MISMATCH: 'offline',
  TIMEOUT: 'offline',
  DENIED: 'offline',
  CANCELLED: 'offline',
}

export function statusTone(status: string): Tone {
  return TONE[status] ?? 'neutral'
}
