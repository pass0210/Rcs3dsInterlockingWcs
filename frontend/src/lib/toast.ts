import { createContext, useContext } from 'react'

// ═══════════════════════════════════════════════════════════════════════════
// 경량 토스트(비차단 알림) — 원본 UiContext 의 토스트 개념 재현(docs/B2B-DATAGEN.md §4.7).
//   tone: success | error | warning | info. 자동 소멸 + 수동 닫기.
// context/hook/타입만(컴포넌트 없음) — react-refresh 규칙 청정.
// ═══════════════════════════════════════════════════════════════════════════

export type ToastTone = 'success' | 'error' | 'warning' | 'info'

export interface ToastItem {
  id: number
  tone: ToastTone
  message: string
}

export interface ToastValue {
  /** 토스트 표출. 반환값은 생성된 토스트 id(수동 제어용). */
  toast: (tone: ToastTone, message: string) => number
  dismiss: (id: number) => void
}

export const ToastContext = createContext<ToastValue | null>(null)

export function useToast(): ToastValue {
  const ctx = useContext(ToastContext)
  if (!ctx) throw new Error('useToast 는 <ToastProvider> 내부에서만 사용할 수 있습니다.')
  return ctx
}
