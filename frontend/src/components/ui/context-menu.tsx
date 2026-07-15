import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { cn } from '@/lib/utils'

// ═══════════════════════════════════════════════════════════════════════════
// ContextMenu — 위치 지정 팝오버 컨텍스트 메뉴(자작·radix 미의존). 우클릭 그리드 상호작용용.
//   · Dialog(components/ui/dialog.tsx) 의 portal + 포커스 관리 규약과 정합하게 신설(신규 프리미티브).
//   · 좌표(x,y)에 fixed 배치 후 뷰포트 경계로 클램프(useLayoutEffect 로 측정→보정). document.body 로 portal.
//   · 키보드 접근성: role=menu / menuitem, ArrowUp/Down·Home/End 이동, Enter/Space 실행, Escape 닫힘.
//     open 시 첫 활성 항목에 포커스, 닫힐 때 직전 포커스(트리거) 복원. 비활성 항목은 포커스/실행 제외.
//   · 바깥 클릭(pointerdown)·다른 곳 우클릭·스크롤·리사이즈 시 닫힘(컨텍스트 메뉴 표준 동작).
//   · 이 파일은 컴포넌트만 export(+타입) — react-refresh/only-export-components 청정.
// ═══════════════════════════════════════════════════════════════════════════

/** 컨텍스트 메뉴 항목. disabled 면 렌더는 하되 포커스/실행에서 제외(회색 표기). */
export interface ContextMenuItem {
  /** 표시 라벨. */
  label: string
  /** 선택(클릭/Enter) 시 실행. 실행 후 메뉴는 자동으로 닫힌다. */
  onSelect: () => void
  /** 비활성(예: 하이라이트 0건일 때 "선택된 행" 항목). */
  disabled?: boolean
  /** 위험/파괴 계열 강조(해제 계열) — 선택적 색 힌트. */
  tone?: 'default' | 'muted'
}

export function ContextMenu({
  open,
  x,
  y,
  items,
  onClose,
  ariaLabel = '그리드 컨텍스트 메뉴',
}: {
  open: boolean
  x: number
  y: number
  items: ContextMenuItem[]
  onClose: () => void
  ariaLabel?: string
}) {
  const menuRef = useRef<HTMLDivElement>(null)
  const triggerRef = useRef<HTMLElement | null>(null)
  // 측정 후 클램프된 실제 좌표(초기엔 요청 좌표 → useLayoutEffect 에서 보정).
  const [pos, setPos] = useState({ left: x, top: y })

  // 열릴 때마다 요청 좌표로 리셋(다음 프레임에 클램프).
  useLayoutEffect(() => {
    if (!open) return
    const el = menuRef.current
    if (!el) return
    const { innerWidth, innerHeight } = window
    const rect = el.getBoundingClientRect()
    const pad = 8
    // 우측/하단 넘침 시 좌표를 안으로 당긴다(메뉴가 잘리지 않게).
    const left = Math.max(pad, Math.min(x, innerWidth - rect.width - pad))
    const top = Math.max(pad, Math.min(y, innerHeight - rect.height - pad))
    setPos({ left, top })
  }, [open, x, y])

  // 활성(비활성 제외) menuitem 버튼 목록.
  const enabledItems = useCallback(
    (): HTMLButtonElement[] =>
      menuRef.current
        ? Array.from(menuRef.current.querySelectorAll<HTMLButtonElement>('[role="menuitem"]:not([disabled])'))
        : [],
    [],
  )

  useEffect(() => {
    if (!open) return
    // 열기 직전 포커스(트리거) 기억 — 닫힐 때 복원.
    triggerRef.current = document.activeElement as HTMLElement | null
    // 첫 활성 항목에 포커스(마우스 없이도 즉시 탐색 가능 — a11y).
    const first = enabledItems()[0]
    first?.focus()

    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.preventDefault()
        onClose()
        return
      }
      const list = enabledItems()
      if (list.length === 0) return
      const idx = list.indexOf(document.activeElement as HTMLButtonElement)
      if (e.key === 'ArrowDown') {
        e.preventDefault()
        list[(idx + 1 + list.length) % list.length].focus()
      } else if (e.key === 'ArrowUp') {
        e.preventDefault()
        list[(idx - 1 + list.length) % list.length].focus()
      } else if (e.key === 'Home') {
        e.preventDefault()
        list[0].focus()
      } else if (e.key === 'End') {
        e.preventDefault()
        list[list.length - 1].focus()
      }
    }
    // 스크롤/리사이즈 시 메뉴가 엉뚱한 위치에 남지 않게 닫는다(표준 동작).
    const onDismiss = () => onClose()

    document.addEventListener('keydown', onKey)
    window.addEventListener('resize', onDismiss)
    // capture=true — 내부 스크롤(그리드 overflow) 포함 어떤 스크롤에도 닫힘.
    window.addEventListener('scroll', onDismiss, true)
    return () => {
      document.removeEventListener('keydown', onKey)
      window.removeEventListener('resize', onDismiss)
      window.removeEventListener('scroll', onDismiss, true)
      // 닫힐 때 포커스 복원(트리거가 여전히 포커스 가능하면).
      const t = triggerRef.current
      if (t && document.contains(t) && (t as HTMLButtonElement).disabled !== true && t.offsetParent !== null) {
        t.focus()
      }
    }
  }, [open, onClose, enabledItems])

  if (!open) return null

  const select = (item: ContextMenuItem) => {
    if (item.disabled) return
    onClose()
    item.onSelect()
  }

  return createPortal(
    <div className="fixed inset-0 z-[95]">
      {/* 투명 백드롭 — 바깥 클릭/다른 곳 우클릭으로 닫힘. */}
      <div
        className="absolute inset-0"
        aria-hidden="true"
        onPointerDown={onClose}
        onContextMenu={(e) => {
          // 메뉴 밖 우클릭은 기본 메뉴 대신 이 메뉴를 닫는다(연속 우클릭 UX).
          e.preventDefault()
          onClose()
        }}
      />
      <div
        ref={menuRef}
        role="menu"
        aria-label={ariaLabel}
        tabIndex={-1}
        style={{ left: pos.left, top: pos.top }}
        className="absolute min-w-[168px] overflow-hidden rounded-[10px] border border-line bg-panel py-1 shadow-card"
      >
        {items.map((item, i) => (
          <button
            key={i}
            type="button"
            role="menuitem"
            disabled={item.disabled}
            onClick={() => select(item)}
            className={cn(
              'block w-full cursor-pointer px-3.5 py-1.5 text-left text-[13px] text-ink',
              'hover:bg-elevated focus-visible:bg-elevated focus-visible:outline-none',
              'disabled:cursor-not-allowed disabled:text-faint/60 disabled:hover:bg-transparent',
              item.tone === 'muted' && 'text-muted',
            )}
          >
            {item.label}
          </button>
        ))}
      </div>
    </div>,
    document.body,
  )
}
