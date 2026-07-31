import { createContext, useContext } from 'react'

// ═══════════════════════════════════════════════════════════════════════════
// UI 모드 전역 상태 (B2C/B2B 토글) — docs/B2B-DATAGEN.md §5.
//   · mode: 'b2c'(모니터링·소터) | 'b2b'(데이터 생성) — 메뉴 세트/헤더/기본 진입만 전환(UI 전용).
//   · bizDay: B2B 화면 업무일자('YYYY-MM-DD'). native <input type="date"> 값 형식.
//   · autoRefresh(+interval): B2B 그리드 자동 새로고침. 백엔드 모드 게이트 없음(양쪽 상시 활성).
// 영속화 = localStorage(손상값은 화이트리스트 폴백 — 앱 크래시 금지).
// 이 파일은 컴포넌트를 export 하지 않는다(context/hook/pure helper 만) — react-refresh 규칙 청정.
// ═══════════════════════════════════════════════════════════════════════════

export type UiMode = 'b2c' | 'b2b'

/** 자동 새로고침 간격 화이트리스트(ms). */
export const REFRESH_INTERVALS = [3000, 5000, 10000, 30000] as const
export const DEFAULT_REFRESH_INTERVAL = 5000

export interface UiModeState {
  mode: UiMode
  bizDay: string
  autoRefresh: boolean
  refreshInterval: number
}

export interface UiModeValue extends UiModeState {
  setMode: (mode: UiMode) => void
  setBizDay: (bizDay: string) => void
  setAutoRefresh: (on: boolean) => void
  setRefreshInterval: (ms: number) => void
}

export const UiModeContext = createContext<UiModeValue | null>(null)

/** UiModeProvider 하위에서 UI 모드 상태를 읽는다. Provider 밖 사용은 즉시 fail-loud. */
export function useUiMode(): UiModeValue {
  const ctx = useContext(UiModeContext)
  if (!ctx) throw new Error('useUiMode 는 <UiModeProvider> 내부에서만 사용할 수 있습니다.')
  return ctx
}

// ── localStorage 직렬화·화이트리스트 가드 ────────────────────────────────────
export const UI_MODE_STORAGE_KEY = 'wcs.ui'

const BIZDAY_RE = /^\d{4}-\d{2}-\d{2}$/

/** 오늘(로컬) 'YYYY-MM-DD'. bizDay 기본값. */
export function todayBizDay(): string {
  const d = new Date()
  const p = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`
}

function defaultState(): UiModeState {
  return {
    mode: 'b2c',
    bizDay: todayBizDay(),
    autoRefresh: false,
    refreshInterval: DEFAULT_REFRESH_INTERVAL,
  }
}

/**
 * localStorage 값을 화이트리스트로 검증해 안전한 상태로 정규화한다.
 * 파싱 실패·타입 손상·범위 이탈은 필드별 기본값으로 폴백(부분 손상도 앱 유지).
 */
export function loadUiModeState(): UiModeState {
  const base = defaultState()
  try {
    const raw = localStorage.getItem(UI_MODE_STORAGE_KEY)
    if (!raw) return base
    const parsed = JSON.parse(raw) as Record<string, unknown>

    const mode: UiMode = parsed.mode === 'b2b' ? 'b2b' : 'b2c'
    const bizDay =
      typeof parsed.bizDay === 'string' && BIZDAY_RE.test(parsed.bizDay)
        ? parsed.bizDay
        : base.bizDay
    const autoRefresh = typeof parsed.autoRefresh === 'boolean' ? parsed.autoRefresh : base.autoRefresh
    const refreshInterval =
      typeof parsed.refreshInterval === 'number' &&
      (REFRESH_INTERVALS as readonly number[]).includes(parsed.refreshInterval)
        ? parsed.refreshInterval
        : base.refreshInterval

    return { mode, bizDay, autoRefresh, refreshInterval }
  } catch {
    // JSON 손상·localStorage 접근 불가(프라이빗 모드 등) → 전체 기본값.
    return base
  }
}

export function saveUiModeState(state: UiModeState): void {
  try {
    localStorage.setItem(UI_MODE_STORAGE_KEY, JSON.stringify(state))
  } catch {
    // 저장 실패는 무해(세션 한정 동작) — 삼키되 앱은 계속.
  }
}

/** 모드별 기본 진입 경로 — 토글/리다이렉트 공용 단일 소스. */
//   b2c → /trace(추적 로그): 기본 랜딩을 관제용 실시간 추적 로그로(S-TRACE-READY-PUSH-AND-DEFAULT OQ4 확정).
//   (이전 /b2c/test-data 랜딩을 사용자 요청으로 대체 — /trace 는 b2c NAV 전용 페이지.) b2b 는 /data-generator 불변.
export function homePathFor(mode: UiMode): string {
  return mode === 'b2b' ? '/data-generator' : '/trace'
}
