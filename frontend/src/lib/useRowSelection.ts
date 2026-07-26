import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type Dispatch,
  type KeyboardEvent as ReactKeyboardEvent,
  type MouseEvent as ReactMouseEvent,
  type PointerEvent as ReactPointerEvent,
  type SetStateAction,
} from 'react'
import type { ContextMenuItem } from '@/components/ui/context-menu'

// ═══════════════════════════════════════════════════════════════════════════
// useRowSelection — 모든 B2C/B2B 체크박스 그리드가 공유하는 "행 선택 상호작용" 훅.
//   드래그 범위 하이라이트 + 우클릭 컨텍스트 메뉴(4항목) + 하이라이트/전체 일괄 체크·해제를
//   그리드별 중복 없이 단일 훅으로 제공한다(S-B2C-GRID-UX R3). 각 그리드는 자기 체크 Set 을
//   계속 소유하고(체크 모델 재작성 금지), 이 훅은 하이라이트 + 4액션을 그 Set 에 "연결(bridge)"만 한다.
//
// 설계 핵심(왜 DOM 기반인가):
//   · 선택 가능한 행은 각자 `data-rsid`(=String(id))·`data-rseligible`("1"/"0") 를 단다.
//   · 범위 계산·전체선택·하이라이트 대상은 **컨테이너 안 DOM 순서**에서 유도한다 →
//     (a) 지연 로딩되는 행(설비 소터 셀 = 펼침 시 렌더)도 자동 포함, (b) OQ-1 "로드(렌더)된 행만"
//     을 구조적으로 만족(가상화 없음·truncation 배너가 캡 고지). 그리드가 평평한 rows 배열을
//     따로 만들어 넘길 필요가 없어 결선이 최소 침습.
//   · 문자열 `data-rsid` → 타입 id 복원은 `parseId`(number 그리드=Number, string 그리드=identity).
//
// 하이라이트 vs 체크(별개 개념):
//   · 하이라이트 = 드래그로 만든 "연속 행 범위"(시각 표시). 체크박스 상태와 무관·시각 구분.
//   · 메뉴 ①전체선택/②전체해제 = 전체 행 / ③선택행 체크·④선택행 해제 = 하이라이트 행.
//   · 자격(eligibility) 존중: ①③(체크)는 `data-rseligible==='1'` 인 행만 체크(개별 체크박스 비활성 조건과 동일).
//     ②④(해제)는 무해하므로 자격 무관.
//
// 공존(coexistence) — 기존 행 클릭/펼침 무손상:
//   · 좌클릭만 드래그 후보(우클릭=컨텍스트 메뉴). 체크박스/버튼/링크 등 인터랙티브 자식에서 시작한
//     포인터는 무시(기존 토글 보존). · 이동 임계(THRESHOLD) 미만이면 "클릭"으로 간주 → 기존 onClick
//     (G1 디테일 로드·G2 소터 펼침) 그대로 발화. 임계 초과 "드래그"였으면 뒤따르는 click 을 캡처 단계에서
//     삼켜(onClickCapture) 기존 onClick 오발화를 차단.
//
// OQ 정합: OQ-1 로드된 행 · OQ-2 드래그 중 텍스트선택 억제 + 스크롤 추종(엣지 자동스크롤 없음) ·
//          OQ-3 컨테이너(그리드 본문) 내부에서만 네이티브 우클릭 대체 · OQ-4 id-키 하이라이트(refetch prune·
//          라우트/스코프/필터 변경 시 resetKey 로 전체 리셋).
//
// 이 파일은 컴포넌트를 export 하지 않는다(훅·상수·타입만) — react-refresh 규칙 청정.
// ═══════════════════════════════════════════════════════════════════════════

/** 하이라이트된 행에 적용하는 클래스(체크 tint 와 시각 구분 — teal 좌측 바 + 옅은 배경). 그리드 공통. */
export const ROW_HIGHLIGHT_CLASS = 'bg-busy/10 shadow-[inset_2px_0_0_0_var(--color-busy)]'

/** 클릭↔드래그 판별 이동 임계(px). 이 이상 움직이면 드래그로 승격. */
const DRAG_THRESHOLD = 4

export interface RowSelection<Id extends string | number> {
  /** 특정 행이 현재 하이라이트되었는지(그리드가 className 에 반영). */
  isHighlighted: (id: Id) => boolean
  /** 현재 하이라이트된 행 수(라벨/디버그용). */
  highlightCount: number
  /** 선택 가능한 각 `<tr>` 에 스프레드 — 드래그 감지 + 클릭-후-드래그 억제 + 식별 data 속성. */
  getRowProps: (
    id: Id,
    eligible: boolean,
  ) => {
    'data-rsid': string
    'data-rseligible': '1' | '0'
    onPointerDown: (e: ReactPointerEvent) => void
    onClickCapture: (e: ReactMouseEvent) => void
  }
  /**
   * 비선택 인터랙티브 행(예: 소터 펼침 헤더행)에 스프레드 — 자기 pointerdown→click 이동거리로
   * 클릭/드래그를 독립 판별해, 그 행에서의 드래그가 onClick(펼침/접힘)을 오발화하지 않게 한다.
   * (선택-드래그 플래그 미의존 → 타행 드래그 직후의 정당한 클릭을 오억제하지 않음.)
   */
  expandableRowProps: {
    onPointerDown: (e: ReactPointerEvent) => void
    onClickCapture: (e: ReactMouseEvent) => void
  }
  /** 그리드 본문(스크롤) 컨테이너에 스프레드 — 우클릭 메뉴 + 키보드 열기 + 범위/스크롤 스코프 ref. */
  containerProps: {
    ref: (el: HTMLElement | null) => void
    onContextMenu: (e: ReactMouseEvent) => void
    onKeyDown: (e: ReactKeyboardEvent) => void
  }
  /** <ContextMenu {...selection.menu} /> 로 그대로 소비 — 4항목·좌표·닫기 핸들러·aria 라벨 포함. */
  menu: { open: boolean; x: number; y: number; items: ContextMenuItem[]; onClose: () => void; ariaLabel?: string }
  /** 4액션(마우스 없이 도달 가능한 버튼 등 a11y 대안용으로 그리드가 직접 소비 가능). */
  actions: {
    checkAll: () => void
    uncheckAll: () => void
    checkHighlighted: () => void
    uncheckHighlighted: () => void
  }
}

/**
 * @param setChecked  그리드가 소유한 체크 Set 의 setter(이 훅은 이것으로만 체크를 갱신 — 모델 재작성 0).
 * @param parseId     `data-rsid`(문자열) → 타입 id 복원. number 그리드=Number, string 그리드=identity.
 * @param resetKey    라우트/스코프/필터 시그니처. 값이 바뀌면 하이라이트 전체 리셋(OQ-4).
 * @param menuAriaLabel 컨텍스트 메뉴 aria-label(그리드별 식별).
 */
export function useRowSelection<Id extends string | number>({
  setChecked,
  parseId,
  resetKey,
  menuAriaLabel,
}: {
  setChecked: Dispatch<SetStateAction<Set<Id>>>
  parseId: (raw: string) => Id
  resetKey?: string
  menuAriaLabel?: string
}): RowSelection<Id> {
  const [highlighted, setHighlighted] = useState<Set<Id>>(() => new Set())
  // eligible = 메뉴 열림 시점의 자격(체크 가능) 행 수(전체 선택 라벨 카운트). DOM 유도라 열 때 캡처(렌더 memo 는 stale).
  const [menu, setMenu] = useState<{ open: boolean; x: number; y: number; eligible: number }>({
    open: false,
    x: 0,
    y: 0,
    eligible: 0,
  })

  // 최신 값 참조용 ref(window 이벤트 핸들러·메뉴 액션 클로저가 stale 없이 읽도록).
  const highlightedRef = useRef(highlighted)
  highlightedRef.current = highlighted
  const parseIdRef = useRef(parseId)
  parseIdRef.current = parseId

  // 컨테이너(범위 계산·스크롤 추종·userSelect 스코프) + 행 add/remove 관찰(prune).
  const containerRef = useRef<HTMLElement | null>(null)
  const observerRef = useRef<MutationObserver | null>(null)

  // 드래그 제스처 상태(리렌더 없이 유지).
  const anchorIdRef = useRef<string | null>(null) // 앵커 행 data-rsid
  const pendingRef = useRef(false) // 좌 pointerdown 후 아직 클릭/드래그 미확정
  const draggingRef = useRef(false) // 임계 초과로 드래그 확정
  const draggedRef = useRef(false) // 직전 제스처가 드래그였음 → 뒤따르는 click 억제
  const downXRef = useRef(0)
  const downYRef = useRef(0)
  const lastXRef = useRef(0)
  const lastYRef = useRef(0)
  const prevUserSelectRef = useRef('')
  const dragVisualRef = useRef(false) // body user-select 잠금 활성 여부(prevUserSelect 재저장 오염 방지)

  // ── DOM 유도 헬퍼 ───────────────────────────────────────────────────────────
  // 컨테이너 안 "선택 가능 행" 을 DOM 순서대로.
  const rowEls = useCallback((): HTMLElement[] => {
    const c = containerRef.current
    if (!c) return []
    return Array.from(c.querySelectorAll<HTMLElement>('[data-rsid]'))
  }, [])

  // 뷰포트 좌표 아래의 선택 가능 행 index(컨테이너 밖·행 아님이면 null).
  const indexAtPoint = useCallback(
    (cx: number, cy: number): number | null => {
      const c = containerRef.current
      if (!c) return null
      const el = document.elementFromPoint(cx, cy)
      if (!el || !c.contains(el)) return null
      const row = el.closest<HTMLElement>('[data-rsid]')
      if (!row || !c.contains(row)) return null
      const list = rowEls()
      const idx = list.indexOf(row)
      return idx >= 0 ? idx : null
    },
    [rowEls],
  )

  // 앵커~현재 index 연속 범위를 하이라이트 Set 으로 확정.
  const applyRange = useCallback(
    (curIndex: number) => {
      const list = rowEls()
      if (list.length === 0) return
      const anchorId = anchorIdRef.current
      const anchorIndex = anchorId == null ? curIndex : list.findIndex((el) => el.dataset.rsid === anchorId)
      const a = anchorIndex >= 0 ? anchorIndex : curIndex
      const [lo, hi] = a <= curIndex ? [a, curIndex] : [curIndex, a]
      const next = new Set<Id>()
      for (let i = lo; i <= hi; i++) {
        const raw = list[i].dataset.rsid
        if (raw != null) next.add(parseIdRef.current(raw))
      }
      setHighlighted(next)
    },
    [rowEls],
  )

  const beginDragVisual = useCallback(() => {
    // 드래그 중 네이티브 텍스트 선택 억제(OQ-2) — body user-select 를 잠갔다 종료 시 복원.
    // ★FIX2: 이미 시각 드래그가 활성이면 prevUserSelect 를 재저장하지 않는다 —
    //   창 밖 릴리스로 종료를 놓친 상태에서 새 드래그가 오염된 'none' 을 prevUserSelect 로 저장해
    //   앱 전역 텍스트 선택이 영구 잠기는 회귀 방지(멱등 begin).
    if (dragVisualRef.current) return
    dragVisualRef.current = true
    prevUserSelectRef.current = document.body.style.userSelect
    document.body.style.userSelect = 'none'
    window.getSelection?.()?.removeAllRanges()
  }, [])

  const endDragVisual = useCallback(() => {
    if (!dragVisualRef.current) return
    dragVisualRef.current = false
    document.body.style.userSelect = prevUserSelectRef.current
  }, [])

  // ── window 레벨 포인터/스크롤 리스너(마운트 1회 — 그리드 밖에서 떼도 종료) ─────────
  useEffect(() => {
    const onUp = () => {
      if (!pendingRef.current) return
      pendingRef.current = false
      if (draggingRef.current) {
        draggingRef.current = false
        draggedRef.current = true // 뒤따르는 click 억제(공존)
      }
      endDragVisual() // 시각 잠금은 항상 해제(멱등) — 창 밖 릴리스 복구 경로 포함.
    }
    const onMove = (e: PointerEvent) => {
      if (!pendingRef.current) return
      // ★FIX2: 버튼이 눌려있지 않으면(창 밖에서 릴리스돼 pointerup 을 놓친 경우) 지금 종료 처리.
      //   창으로 되돌아온 첫 이동에서 걸려 body user-select 를 복원 → 다음 드래그 오염 원천 차단.
      if (e.buttons === 0) {
        onUp()
        return
      }
      lastXRef.current = e.clientX
      lastYRef.current = e.clientY
      if (!draggingRef.current) {
        const dx = e.clientX - downXRef.current
        const dy = e.clientY - downYRef.current
        if (Math.hypot(dx, dy) < DRAG_THRESHOLD) return
        draggingRef.current = true
        beginDragVisual()
      }
      const idx = indexAtPoint(e.clientX, e.clientY)
      if (idx != null) applyRange(idx)
    }
    // 드래그 중 스크롤(휠 등) 시 포인터 아래 행까지 범위 추종(OQ-2·엣지 자동스크롤 없음).
    const onScroll = () => {
      if (!draggingRef.current) return
      const idx = indexAtPoint(lastXRef.current, lastYRef.current)
      if (idx != null) applyRange(idx)
    }
    window.addEventListener('pointermove', onMove)
    window.addEventListener('pointerup', onUp)
    window.addEventListener('pointercancel', onUp)
    window.addEventListener('scroll', onScroll, true)
    return () => {
      window.removeEventListener('pointermove', onMove)
      window.removeEventListener('pointerup', onUp)
      window.removeEventListener('pointercancel', onUp)
      window.removeEventListener('scroll', onScroll, true)
    }
  }, [applyRange, indexAtPoint, beginDragVisual, endDragVisual])

  // ── OQ-4: refetch prune(사라진 id 제거) — DOM 행 add/remove 를 MutationObserver 로 감지해 정리. ──
  //   행 class(하이라이트) 변화는 attribute 라 미관측 → 프룬 루프 없음. 드래그 중엔 스킵(범위가 자체 갱신).
  const pruneHighlight = useCallback(() => {
    if (draggingRef.current) return
    setHighlighted((prev) => {
      if (prev.size === 0) return prev
      const present = new Set(rowEls().map((el) => el.dataset.rsid))
      let changed = false
      const next = new Set<Id>()
      prev.forEach((id) => {
        if (present.has(String(id))) next.add(id)
        else changed = true
      })
      return changed ? next : prev
    })
  }, [rowEls])

  // ── OQ-4: 라우트/스코프/필터 변경 → 하이라이트 전체 리셋. ──
  useEffect(() => {
    setHighlighted(new Set())
  }, [resetKey])

  // ── 드래그-후 click 억제 가드(선택 가능 행 내부용) ────────────────────────────
  //   직전 제스처가 드래그였으면 뒤따르는 click 을 캡처 단계에서 삼켜 기존 onClick 오발화 차단.
  //   getRowProps(선택 가능 행)에서만 사용 — 그 행은 onPointerDown 에서 draggedRef 를 리셋하므로 stale 억제 없음.
  const dragClickGuard = useCallback((e: ReactMouseEvent) => {
    if (draggedRef.current) {
      draggedRef.current = false
      e.preventDefault()
      e.stopPropagation() // 기존 onClick(디테일 로드/펼침) 오발화 차단
    }
  }, [])

  // ── 비선택 인터랙티브 행(예: 소터 펼침 헤더행)용 "클릭 vs 드래그" 가드 ─────────────
  //   이 행은 선택 대상이 아니라 자체 드래그 머신(draggedRef)이 없다 → 선택-드래그 draggedRef 를 재사용하면
  //   직전 타행 드래그의 stale=true 가 정당한 펼침 클릭을 오억제한다(회귀). 대신 **자기 pointerdown→click 이동거리**
  //   로 클릭/드래그를 독립 판별: 이동이 임계 미만이면 정당한 클릭(펼침 실행), 이상이면 드래그로 보고 click 억제.
  const expandDownRef = useRef({ x: 0, y: 0 })
  const onExpandPointerDown = useCallback((e: ReactPointerEvent) => {
    draggedRef.current = false // 그리드 내 새 press — 선택-드래그 stale 플래그도 정리(방어)
    expandDownRef.current = { x: e.clientX, y: e.clientY }
  }, [])
  const onExpandClickCapture = useCallback((e: ReactMouseEvent) => {
    const dx = e.clientX - expandDownRef.current.x
    const dy = e.clientY - expandDownRef.current.y
    if (Math.hypot(dx, dy) >= DRAG_THRESHOLD) {
      e.preventDefault()
      e.stopPropagation() // 드래그였음 → 펼침/접힘 오발화 차단
    }
  }, [])
  const expandableRowProps = useMemo(
    () => ({ onPointerDown: onExpandPointerDown, onClickCapture: onExpandClickCapture }),
    [onExpandPointerDown, onExpandClickCapture],
  )

  // ── 행 핸들러 ────────────────────────────────────────────────────────────────
  const getRowProps = useCallback(
    (id: Id, eligible: boolean) => {
      const onPointerDown = (e: ReactPointerEvent) => {
        draggedRef.current = false // 이전 제스처의 stale 억제 플래그 해제(체크박스 오억제 방지)
        if (e.button !== 0) return // 좌클릭만(우클릭=contextmenu)
        // 인터랙티브 자식(체크박스/버튼/링크/입력)에서 시작 → 기존 상호작용 보존(드래그 개시 안 함).
        if ((e.target as HTMLElement).closest('input, button, label, a, select, textarea')) return
        anchorIdRef.current = String(id)
        pendingRef.current = true
        draggingRef.current = false
        downXRef.current = e.clientX
        downYRef.current = e.clientY
        lastXRef.current = e.clientX
        lastYRef.current = e.clientY
        // preventDefault 하지 않음 — 클릭/포커스 보존. 텍스트 선택 억제는 드래그 확정 시점(beginDragVisual).
      }
      return {
        'data-rsid': String(id),
        'data-rseligible': (eligible ? '1' : '0') as '1' | '0',
        onPointerDown,
        onClickCapture: dragClickGuard,
      }
    },
    [dragClickGuard],
  )

  // ── 4액션(체크 Set 브리지) ────────────────────────────────────────────────────
  const checkAll = useCallback(() => {
    const rows = rowEls()
    setChecked((prev) => {
      const n = new Set(prev)
      for (const el of rows) {
        if (el.dataset.rseligible === '1' && el.dataset.rsid != null) n.add(parseIdRef.current(el.dataset.rsid))
      }
      return n
    })
  }, [rowEls, setChecked])

  const uncheckAll = useCallback(() => {
    // ★FIX3: "전체 해제" 는 그리드 소유 체크 Set 을 완전히 비운다(new Set).
    //   렌더된 행만 지우면 접힌 소터 셀처럼 체크 후 접힌 항목이 Set 에 잔존해 배정에 포함되는 모호성이 생김.
    //   각 체크 Set 은 해당 그리드 전용이라 전량 클리어가 곧 "전체 해제"의 명확한 의미.
    setChecked((prev) => (prev.size === 0 ? prev : new Set<Id>()))
  }, [setChecked])

  const checkHighlighted = useCallback(() => {
    // 하이라이트 ∩ 자격(eligible) 만 체크(개별 체크박스 비활성 조건 존중).
    const eligible = new Set(
      rowEls().filter((el) => el.dataset.rseligible === '1' && el.dataset.rsid != null).map((el) => el.dataset.rsid!),
    )
    setChecked((prev) => {
      const n = new Set(prev)
      highlightedRef.current.forEach((id) => {
        if (eligible.has(String(id))) n.add(id)
      })
      return n
    })
  }, [rowEls, setChecked])

  const uncheckHighlighted = useCallback(() => {
    setChecked((prev) => {
      if (prev.size === 0) return prev
      const n = new Set(prev)
      highlightedRef.current.forEach((id) => n.delete(id))
      return n
    })
  }, [setChecked])

  // ── 컨테이너 핸들러(우클릭 메뉴 · 키보드 열기) ────────────────────────────────────
  const setContainerRef = useCallback(
    (el: HTMLElement | null) => {
      containerRef.current = el
      // 이전 관찰 해제 후 재부착(콜백 ref — 마운트/언마운트·재부착 대칭 · 누수 0).
      observerRef.current?.disconnect()
      observerRef.current = null
      if (el && typeof MutationObserver !== 'undefined') {
        const obs = new MutationObserver(pruneHighlight)
        obs.observe(el, { childList: true, subtree: true })
        observerRef.current = obs
      }
    },
    [pruneHighlight],
  )

  const eligibleNow = useCallback(
    () => rowEls().filter((el) => el.dataset.rseligible === '1').length,
    [rowEls],
  )

  const onContainerContextMenu = useCallback(
    (e: ReactMouseEvent) => {
      // OQ-3: 그리드 본문(이 컨테이너) 내부에서만 네이티브 우클릭 대체. 밖은 브라우저 기본 유지.
      e.preventDefault()
      setMenu({ open: true, x: e.clientX, y: e.clientY, eligible: eligibleNow() })
    },
    [eligibleNow],
  )

  const onContainerKeyDown = useCallback(
    (e: ReactKeyboardEvent) => {
      // 키보드로 컨텍스트 메뉴 열기(마우스 대안) — ContextMenu 키 또는 Shift+F10(표준).
      if (e.key === 'ContextMenu' || (e.shiftKey && e.key === 'F10')) {
        e.preventDefault()
        const r = (e.target as HTMLElement).getBoundingClientRect?.()
        setMenu({ open: true, x: r ? r.left : 0, y: r ? r.bottom : 0, eligible: eligibleNow() })
      }
    },
    [eligibleNow],
  )

  const closeMenu = useCallback(() => setMenu((m) => (m.open ? { ...m, open: false } : m)), [])

  // ── 메뉴 4항목(하이라이트 수·자격 수 반영 라벨) ────────────────────────────────
  const menuItems: ContextMenuItem[] = useMemo(
    () => [
      { label: `전체 선택${menu.eligible > 0 ? ` (${menu.eligible})` : ''}`, onSelect: checkAll },
      { label: '전체 해제', onSelect: uncheckAll, tone: 'muted' },
      {
        label: `선택된 행 체크${highlighted.size > 0 ? ` (${highlighted.size})` : ''}`,
        onSelect: checkHighlighted,
        disabled: highlighted.size === 0,
      },
      {
        label: '선택된 행 해제',
        onSelect: uncheckHighlighted,
        disabled: highlighted.size === 0,
        tone: 'muted',
      },
    ],
    [menu.eligible, highlighted.size, checkAll, uncheckAll, checkHighlighted, uncheckHighlighted],
  )

  const isHighlighted = useCallback((id: Id) => highlighted.has(id), [highlighted])

  return {
    isHighlighted,
    highlightCount: highlighted.size,
    getRowProps,
    expandableRowProps,
    containerProps: {
      ref: setContainerRef,
      onContextMenu: onContainerContextMenu,
      onKeyDown: onContainerKeyDown,
    },
    menu: {
      open: menu.open,
      x: menu.x,
      y: menu.y,
      items: menuItems,
      onClose: closeMenu,
      ariaLabel: menuAriaLabel,
    },
    actions: { checkAll, uncheckAll, checkHighlighted, uncheckHighlighted },
  }
}
