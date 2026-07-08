import { useEffect, useRef, useState } from 'react'
import JsBarcode from 'jsbarcode'
import type { BarcodeSymbology } from '@/lib/printSettings'
import { cn } from '@/lib/utils'

// ═══════════════════════════════════════════════════════════════════════════
// 바코드 렌더 컴포넌트 — 로컬 번들 JsBarcode(폐쇄망 안전·외부 요청 0)로 SVG 를 그린다.
//   · 값 텍스트는 JsBarcode 가 DOM API 로 삽입(innerHTML 미사용 — XSS 안전, A7 재현).
//   · JsBarcode 는 viewBox 를 세팅하므로 CSS width:100% 로 라벨 폭에 맞춰 비율 유지 스케일.
//   · 유효하지 않은 값/심볼로지 조합은 throw 대신 valid 콜백으로 포착 → 폴백 표기(크래시 0).
// ═══════════════════════════════════════════════════════════════════════════
export function Barcode({
  value,
  symbology,
  displayValue,
  height = 40,
  moduleWidth = 2,
  fontSize = 13,
  className,
}: {
  value: string
  symbology: BarcodeSymbology
  displayValue: boolean
  /** 막대 높이 px. */
  height?: number
  /** 모듈(가는 막대) 폭 px. */
  moduleWidth?: number
  fontSize?: number
  className?: string
}) {
  const ref = useRef<SVGSVGElement>(null)
  const [invalid, setInvalid] = useState(false)

  useEffect(() => {
    const svg = ref.current
    if (!svg) return
    let ok = true
    try {
      JsBarcode(svg, value, {
        format: symbology,
        displayValue,
        width: moduleWidth,
        height,
        margin: 0,
        fontSize,
        textMargin: 2,
        // valid 콜백 제공 시 잘못된 값에도 throw 하지 않는다(fail-loud 은 폴백 UI 로).
        valid: (v: boolean) => {
          ok = v
        },
      })
    } catch {
      ok = false
    }
    if (!ok) {
      // 유효하지 않음 — SVG 를 비우고(잔상 방지) 폴백 표기로 전환.
      while (svg.firstChild) svg.removeChild(svg.firstChild)
    }
    setInvalid(!ok)
  }, [value, symbology, displayValue, height, moduleWidth, fontSize])

  return (
    <span className={cn('inline-flex flex-col items-center', className)} data-barcode-invalid={invalid ? 'true' : 'false'}>
      {/* JsBarcode 가 style 속성을 덮어쓰므로 크기는 class 로 제어(width:100%·비율 유지). */}
      <svg
        ref={ref}
        className={cn('block h-auto w-full max-w-full', invalid && 'hidden')}
        role="img"
        aria-label={`바코드 ${value}`}
      />
      {invalid && (
        <span className="px-1 text-center text-[10px] leading-tight text-offline">
          {value || '(빈 값)'} — {symbology} 형식으로 인코딩 불가
        </span>
      )}
    </span>
  )
}
