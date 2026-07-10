import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'
import {
  UiModeContext,
  loadUiModeState,
  saveUiModeState,
  type UiMode,
  type UiModeState,
  type UiModeValue,
} from '@/lib/uiMode'

// UI 모드 전역 Provider — localStorage 영속화(화이트리스트 가드는 loadUiModeState).
// main.tsx 의 QueryClientProvider > BrowserRouter 계층에 삽입한다.
export function UiModeProvider({ children }: { children: ReactNode }) {
  // 최초 1회 localStorage 에서 안전 복원(손상값은 폴백).
  const [state, setState] = useState<UiModeState>(() => loadUiModeState())

  // 상태 변경 시마다 영속화(저장 실패는 무해).
  useEffect(() => {
    saveUiModeState(state)
  }, [state])

  const setMode = useCallback((mode: UiMode) => setState((s) => ({ ...s, mode })), [])
  const setBizDay = useCallback((bizDay: string) => setState((s) => ({ ...s, bizDay })), [])
  const setAutoRefresh = useCallback((autoRefresh: boolean) => setState((s) => ({ ...s, autoRefresh })), [])
  const setRefreshInterval = useCallback(
    (refreshInterval: number) => setState((s) => ({ ...s, refreshInterval })),
    [],
  )

  const value = useMemo<UiModeValue>(
    () => ({ ...state, setMode, setBizDay, setAutoRefresh, setRefreshInterval }),
    [state, setMode, setBizDay, setAutoRefresh, setRefreshInterval],
  )

  return <UiModeContext.Provider value={value}>{children}</UiModeContext.Provider>
}
