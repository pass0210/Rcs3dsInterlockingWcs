import { useEffect } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { monitorHub, type OpLogEntry } from './signalr'

// ═══════════════════════════════════════════════════════════════════════════
// useHubLifecycle — 앱 수명 동안 SignalR 연결 유지 + TanStack Query 이벤트 무효화(§2.3).
//
// 원칙: 고빈도·저지연(워드·oplog)=push, 집계·목록=폴링+이벤트 무효화. 행 단위 push 남발 금지 —
//   API/HANDSHAKE/STATE/PLC_WRITE oplog 이벤트 수신 시 관련 쿼리를 invalidate해 근실시간 보정.
//   버스트를 코얼레싱하기 위해 짧은 디바운스로 배치 무효화(무효화 폭주 방지).
// ═══════════════════════════════════════════════════════════════════════════

// oplog 카테고리 → 무효화 대상 쿼리키(선두 세그먼트 부분 매칭).
const INVALIDATION_MAP: Record<string, string[]> = {
  // IF-05/09/10 인바운드·아웃바운드 → 배정·오더·in-flight 진행.
  API: ['orders', 'inFlight', 'batches'],
  // C/R 핸드셰이크 → 적재 이력·셀 현황·오더 분류 진행·in-flight.
  HANDSHAKE: ['sorterCommands', 'cells', 'orders', 'inFlight'],
  // 상태 전이(FULL/PAUSED/ONLINE/OFFLINE) → 소터 readiness·셀 현황.
  STATE: ['sorters', 'cells'],
  // PLC 쓰기(SetTgtFloor/CellAssign/ClearR) → 셀 현황.
  PLC_WRITE: ['cells'],
}

export function useHubLifecycle(): void {
  const queryClient = useQueryClient()

  useEffect(() => {
    monitorHub.connect()

    // 디바운스된 배치 무효화 — 버스트 동안 dirty 키를 모아 한 번에 flush.
    const dirty = new Set<string>()
    let timer: ReturnType<typeof setTimeout> | null = null

    const flush = () => {
      timer = null
      const keys = [...dirty]
      dirty.clear()
      for (const key of keys) {
        queryClient.invalidateQueries({ queryKey: [key] })
      }
    }

    const onOpLog = (e: OpLogEntry) => {
      const keys = INVALIDATION_MAP[e.category]
      if (!keys) return // POLL_CHANGE 등 고빈도는 무효화 안 함(워드는 push로 이미 갱신).
      for (const k of keys) dirty.add(k)
      if (timer === null) timer = setTimeout(flush, 500)
    }

    const unsub = monitorHub.subscribeOpLog(onOpLog)
    return () => {
      unsub()
      if (timer !== null) clearTimeout(timer)
    }
  }, [queryClient])
}
