import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'
import {
  PrintSettingsContext,
  loadPrintSettings,
  savePrintSettings,
  type BarcodeSymbology,
  type PrintPreset,
  type PrintSettingsState,
  type PrintSettingsValue,
} from '@/lib/printSettings'

// 인쇄 설정 전역 Provider — localStorage 영속화(화이트리스트 가드는 loadPrintSettings).
// UiModeProvider 와 동형: 상태 변경 시마다 자동 영속화 → 새로고침 후에도 값 유지.
// main.tsx 의 UiModeProvider 하위에 삽입한다.
export function PrintSettingsProvider({ children }: { children: ReactNode }) {
  // 최초 1회 localStorage 에서 안전 복원(손상값은 폴백).
  const [state, setState] = useState<PrintSettingsState>(() => loadPrintSettings())

  // 상태 변경 시마다 영속화(저장 실패는 무해) — 별도 저장 버튼 없이 즉시 반영.
  useEffect(() => {
    savePrintSettings(state)
  }, [state])

  const setSymbology = useCallback((symbology: BarcodeSymbology) => setState((s) => ({ ...s, symbology })), [])
  const setShowValueText = useCallback((showValueText: boolean) => setState((s) => ({ ...s, showValueText })), [])
  const setPreset = useCallback((preset: PrintPreset) => setState((s) => ({ ...s, preset })), [])

  const value = useMemo<PrintSettingsValue>(
    () => ({ ...state, setSymbology, setShowValueText, setPreset }),
    [state, setSymbology, setShowValueText, setPreset],
  )

  return <PrintSettingsContext.Provider value={value}>{children}</PrintSettingsContext.Provider>
}
