import { useCallback, useMemo, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { CheckCircle2, AlertTriangle, XCircle, Info, X } from 'lucide-react'
import { ToastContext, type ToastItem, type ToastTone, type ToastValue } from '@/lib/toast'
import { cn } from '@/lib/utils'

const AUTO_DISMISS_MS = 4500

// 톤별 아이콘·색 — index.css 상태 의미색 재사용(브랜드 Rausch 와 톤 분리).
const TONE_STYLE: Record<ToastTone, { icon: typeof Info; ring: string; text: string }> = {
  success: { icon: CheckCircle2, ring: 'border-online/40', text: 'text-online' },
  error: { icon: XCircle, ring: 'border-offline/40', text: 'text-offline' },
  warning: { icon: AlertTriangle, ring: 'border-warn/40', text: 'text-warn' },
  info: { icon: Info, ring: 'border-accent/40', text: 'text-accent' },
}

export function ToastProvider({ children }: { children: ReactNode }) {
  const [items, setItems] = useState<ToastItem[]>([])
  const seq = useRef(0)
  const timers = useRef(new Map<number, ReturnType<typeof setTimeout>>())

  const dismiss = useCallback((id: number) => {
    setItems((list) => list.filter((t) => t.id !== id))
    const timer = timers.current.get(id)
    if (timer) {
      clearTimeout(timer)
      timers.current.delete(id)
    }
  }, [])

  const toast = useCallback(
    (tone: ToastTone, message: string) => {
      const id = ++seq.current
      setItems((list) => [...list, { id, tone, message }])
      timers.current.set(
        id,
        setTimeout(() => dismiss(id), AUTO_DISMISS_MS),
      )
      return id
    },
    [dismiss],
  )

  const value = useMemo<ToastValue>(() => ({ toast, dismiss }), [toast, dismiss])

  return (
    <ToastContext.Provider value={value}>
      {children}
      {createPortal(
        <div
          className="pointer-events-none fixed right-4 top-4 z-[100] flex w-[360px] max-w-[calc(100vw-2rem)] flex-col gap-2"
          role="region"
          aria-label="알림"
        >
          {items.map((t) => {
            const style = TONE_STYLE[t.tone]
            const Icon = style.icon
            // error 는 assertive(즉시 낭독), 그 외는 polite(FIX-4 접근성).
            const isError = t.tone === 'error'
            return (
              <div
                key={t.id}
                role={isError ? 'alert' : 'status'}
                aria-live={isError ? 'assertive' : 'polite'}
                className={cn(
                  'pointer-events-auto flex items-start gap-2.5 rounded-[14px] border bg-panel px-3.5 py-3 shadow-card',
                  style.ring,
                )}
              >
                <Icon className={cn('mt-px size-4 shrink-0', style.text)} />
                <p className="flex-1 whitespace-pre-line text-[13px] leading-snug text-ink">{t.message}</p>
                <button
                  onClick={() => dismiss(t.id)}
                  className="shrink-0 rounded text-faint hover:text-ink"
                  aria-label="알림 닫기"
                >
                  <X className="size-4" />
                </button>
              </div>
            )
          })}
        </div>,
        document.body,
      )}
    </ToastContext.Provider>
  )
}
