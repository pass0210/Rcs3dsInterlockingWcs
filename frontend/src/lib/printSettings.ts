import { createContext, useContext } from 'react'

// ═══════════════════════════════════════════════════════════════════════════
// 인쇄 설정 전역 상태 (A4 라벨 인쇄 전용) — docs/PROGRAM_STRUCTURE.md §6.4 재현.
//   · symbology : 바코드 심볼로지(CODE128 기본). 인쇄/미리보기 바코드 렌더에 실제 반영.
//   · showValueText : 바코드 아래 사람이 읽는 값 텍스트 표시 on/off.
//   · preset : 라벨 프리셋. 정본(canonical) = 'a4-2x4'(99.14×67.48mm). 그 외는 레이아웃 후속(선택 불가).
// 영속화 = localStorage(uiMode.ts 화이트리스트-가드 패턴 미러링 — 손상값은 기본값 폴백, 앱 크래시 금지).
//   · 자동생성 규칙 설정은 이식 대상 아님(불변 결정) — 이 상태에 포함하지 않는다.
// 이 파일은 컴포넌트를 export 하지 않는다(context/hook/pure helper 만) — react-refresh 규칙 청정.
// ═══════════════════════════════════════════════════════════════════════════

/** 지원 심볼로지 화이트리스트 — JsBarcode 가 렌더 가능한 형식. 기본 = CODE128. */
export const BARCODE_SYMBOLOGIES = ['CODE128', 'CODE39', 'EAN13', 'ITF', 'MSI', 'codabar'] as const
export type BarcodeSymbology = (typeof BARCODE_SYMBOLOGIES)[number]

/** 심볼로지 표시 라벨. */
export const SYMBOLOGY_LABELS: Record<BarcodeSymbology, string> = {
  CODE128: 'CODE128 (기본)',
  CODE39: 'CODE39',
  EAN13: 'EAN-13',
  ITF: 'ITF',
  MSI: 'MSI',
  codabar: 'Codabar',
}

/**
 * 라벨 프리셋 — 정본 A4 2×4 만 활성(must-work). 원본 100×100/100×150/80×40 은
 * 레이아웃 재-플로우가 이번 스코프 밖이라 선택 불가 항목으로만 노출(로드맵 표기).
 */
export const PRINT_PRESETS = [
  { id: 'a4-2x4', label: 'A4 2×4 (99.14×67.48mm)', enabled: true },
  { id: 'label-100x100', label: '라벨 100×100mm (레이아웃 후속)', enabled: false },
  { id: 'label-100x150', label: '라벨 100×150mm (레이아웃 후속)', enabled: false },
  { id: 'label-80x40', label: '라벨 80×40mm (레이아웃 후속)', enabled: false },
] as const
export type PrintPreset = (typeof PRINT_PRESETS)[number]['id']

/** 현재 선택 가능한(활성) 프리셋 id 집합 — 화이트리스트 가드에 사용. */
const ENABLED_PRESETS = new Set<PrintPreset>(
  PRINT_PRESETS.filter((p) => p.enabled).map((p) => p.id),
)

export interface PrintSettingsState {
  symbology: BarcodeSymbology
  showValueText: boolean
  preset: PrintPreset
}

export interface PrintSettingsValue extends PrintSettingsState {
  setSymbology: (s: BarcodeSymbology) => void
  setShowValueText: (on: boolean) => void
  setPreset: (p: PrintPreset) => void
}

export const PrintSettingsContext = createContext<PrintSettingsValue | null>(null)

/** PrintSettingsProvider 하위에서 인쇄 설정을 읽는다. Provider 밖 사용은 즉시 fail-loud. */
export function usePrintSettings(): PrintSettingsValue {
  const ctx = useContext(PrintSettingsContext)
  if (!ctx) throw new Error('usePrintSettings 는 <PrintSettingsProvider> 내부에서만 사용할 수 있습니다.')
  return ctx
}

// ── localStorage 직렬화·화이트리스트 가드 ────────────────────────────────────
export const PRINT_SETTINGS_STORAGE_KEY = 'wcs.print'

export function defaultPrintSettings(): PrintSettingsState {
  return { symbology: 'CODE128', showValueText: true, preset: 'a4-2x4' }
}

/**
 * localStorage 값을 화이트리스트로 검증해 안전한 상태로 정규화한다.
 * 파싱 실패·타입 손상·범위 이탈(미지원 심볼로지, 비활성 프리셋)은 필드별 기본값으로 폴백.
 */
export function loadPrintSettings(): PrintSettingsState {
  const base = defaultPrintSettings()
  try {
    const raw = localStorage.getItem(PRINT_SETTINGS_STORAGE_KEY)
    if (!raw) return base
    const parsed = JSON.parse(raw) as Record<string, unknown>

    const symbology: BarcodeSymbology =
      typeof parsed.symbology === 'string' &&
      (BARCODE_SYMBOLOGIES as readonly string[]).includes(parsed.symbology)
        ? (parsed.symbology as BarcodeSymbology)
        : base.symbology
    const showValueText =
      typeof parsed.showValueText === 'boolean' ? parsed.showValueText : base.showValueText
    const preset: PrintPreset =
      typeof parsed.preset === 'string' && ENABLED_PRESETS.has(parsed.preset as PrintPreset)
        ? (parsed.preset as PrintPreset)
        : base.preset

    return { symbology, showValueText, preset }
  } catch {
    // JSON 손상·localStorage 접근 불가(프라이빗 모드 등) → 전체 기본값.
    return base
  }
}

export function savePrintSettings(state: PrintSettingsState): void {
  try {
    localStorage.setItem(PRINT_SETTINGS_STORAGE_KEY, JSON.stringify(state))
  } catch {
    // 저장 실패는 무해(세션 한정 동작) — 삼키되 앱은 계속.
  }
}
