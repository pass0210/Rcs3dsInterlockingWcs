import { useCallback, useEffect, useRef, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { AlertTriangle } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'

// 다이얼로그 내 포커서블 요소(비활성·tabindex=-1 제외) 셀렉터.
const FOCUSABLE =
  'a[href], button:not([disabled]), textarea:not([disabled]), input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])'

// ═══════════════════════════════════════════════════════════════════════════
// shadcn-style 모달 다이얼로그(자작·radix 미의존 — 최소 인프라). 파괴적 액션 확인용.
//   · scrim(50% 검정) 백드롭 + 중앙 카드. Escape·백드롭 클릭으로 닫힘.
//   · 열림 동안 body 스크롤 잠금. document.body 로 portal.
//   · 포커스 관리(FIX-2): open 시 다이얼로그 내부로 포커스 이동 + Tab/Shift+Tab 트랩(배경 유출 차단)
//     + 닫힐 때 트리거(직전 activeElement)로 포커스 복원 → 배경 버튼 Enter/Space 재발동 위험 제거.
// ═══════════════════════════════════════════════════════════════════════════
export function Dialog({
  open,
  onClose,
  children,
  labelledBy,
}: {
  open: boolean
  onClose: () => void
  children: ReactNode
  labelledBy?: string
}) {
  const cardRef = useRef<HTMLDivElement>(null)
  const triggerRef = useRef<HTMLElement | null>(null)

  useEffect(() => {
    if (!open) return

    // 열기 직전 포커스(트리거 버튼) 기억 — 닫힐 때 복원.
    triggerRef.current = document.activeElement as HTMLElement | null

    const focusables = (): HTMLElement[] =>
      cardRef.current ? Array.from(cardRef.current.querySelectorAll<HTMLElement>(FOCUSABLE)) : []

    // 최초 포커스 이동 — 첫 포커서블(ConfirmDialog: 취소 버튼 = 파괴적 액션 안전 기본).
    // 활성 버튼이 없으면(예: busy 로 전부 disabled) 카드 컨테이너에 포커스.
    const items = focusables()
    ;(items[0] ?? cardRef.current)?.focus()

    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        onClose()
        return
      }
      if (e.key !== 'Tab') return
      // 포커스 트랩 — 경계에서 반대편으로 순환, 배경으로 새지 않게.
      const list = focusables()
      if (list.length === 0) {
        e.preventDefault()
        cardRef.current?.focus()
        return
      }
      const first = list[0]
      const last = list[list.length - 1]
      const active = document.activeElement as HTMLElement | null
      const inside = cardRef.current?.contains(active) ?? false
      if (e.shiftKey) {
        if (!inside || active === first) {
          e.preventDefault()
          last.focus()
        }
      } else if (!inside || active === last) {
        e.preventDefault()
        first.focus()
      }
    }

    document.addEventListener('keydown', onKey)
    // body 스크롤 잠금(모달 표준 동작).
    const prevOverflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    return () => {
      document.removeEventListener('keydown', onKey)
      document.body.style.overflow = prevOverflow
      // 닫힐 때 포커스 복원 — 우선 트리거(Esc/취소 경로: 상태 무변경이라 그대로 활성).
      // 단, 파괴적 확인(초기화/삭제) 성공 시 트리거가 선택 해제로 disabled 되면 focus() 가 no-op →
      // body 로 유실되므로, 그 경우 main 영역(tabindex=-1)에 복원해 키보드 포커스 유실 방지(a11y).
      const trigger = triggerRef.current
      const triggerFocusable =
        !!trigger &&
        document.contains(trigger) &&
        !(trigger as HTMLButtonElement).disabled &&
        trigger.offsetParent !== null
      if (triggerFocusable) {
        trigger!.focus()
      } else {
        const main = document.querySelector('main')
        if (main) {
          main.setAttribute('tabindex', '-1')
          ;(main as HTMLElement).focus()
        }
      }
    }
  }, [open, onClose])

  if (!open) return null

  return createPortal(
    <div
      className="fixed inset-0 z-[90] flex items-center justify-center p-4"
      role="dialog"
      aria-modal="true"
      aria-labelledby={labelledBy}
    >
      {/* 백드롭 — scrim 50% */}
      <div
        className="absolute inset-0 bg-[rgba(0,0,0,0.5)]"
        onClick={onClose}
        aria-hidden="true"
      />
      {/* 카드 — tabIndex=-1 로 포커서블(활성 버튼 부재 시 폴백 포커스 대상). */}
      <div
        ref={cardRef}
        tabIndex={-1}
        className="relative w-full max-w-[440px] rounded-[14px] border border-line bg-panel shadow-card focus-visible:outline-none"
      >
        {children}
      </div>
    </div>,
    document.body,
  )
}

// 파괴적 확인 다이얼로그 — reset/delete danger. 비동기 onConfirm 진행 중 버튼 잠금.
export function ConfirmDialog({
  open,
  title,
  description,
  confirmLabel = '확인',
  cancelLabel = '취소',
  danger = true,
  busy = false,
  onConfirm,
  onCancel,
}: {
  open: boolean
  title: string
  description: ReactNode
  confirmLabel?: string
  cancelLabel?: string
  danger?: boolean
  busy?: boolean
  onConfirm: () => void
  onCancel: () => void
}) {
  // 안정 onClose(FIX-2) — 매 렌더 새 함수(busy 삼항) 대신 useCallback + busyRef 로 고정해
  // Dialog 의 keydown/overflow effect 재실행 churn 제거. busy 중엔 Escape/백드롭 닫기 무시.
  const busyRef = useRef(busy)
  busyRef.current = busy
  const handleClose = useCallback(() => {
    if (!busyRef.current) onCancel()
  }, [onCancel])

  return (
    <Dialog open={open} onClose={handleClose} labelledBy="confirm-dialog-title">
      <div className="flex items-start gap-3 px-5 pt-5">
        {danger && (
          <span className="mt-0.5 flex size-9 shrink-0 items-center justify-center rounded-full bg-offline/10 text-offline">
            <AlertTriangle className="size-5" />
          </span>
        )}
        <div className="min-w-0 flex-1">
          <h2 id="confirm-dialog-title" className="text-[15px] font-semibold leading-tight text-ink">
            {title}
          </h2>
          <div className="mt-1.5 text-[13px] leading-relaxed text-muted">{description}</div>
        </div>
      </div>
      <div className="flex justify-end gap-2 px-5 py-4">
        <Button variant="outline" size="sm" onClick={onCancel} disabled={busy}>
          {cancelLabel}
        </Button>
        <Button
          variant="solid"
          size="sm"
          onClick={onConfirm}
          disabled={busy}
          className={cn(
            danger &&
              'border-transparent bg-offline text-white hover:bg-offline/90 disabled:bg-offline/40',
          )}
        >
          {busy ? '처리 중…' : confirmLabel}
        </Button>
      </div>
    </Dialog>
  )
}
