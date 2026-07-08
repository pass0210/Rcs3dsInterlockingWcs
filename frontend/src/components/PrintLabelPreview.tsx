import { useEffect, useRef } from 'react'
import { createPortal } from 'react-dom'
import { Printer, X } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Barcode } from '@/components/Barcode'
import { usePrintSettings, SYMBOLOGY_LABELS, type BarcodeSymbology } from '@/lib/printSettings'
import { A4_LABEL, COLUMN_GAP_MM, chunk, padChute } from '@/lib/labelLayout'
import type { DetailRow } from '@/lib/testData'

// 모달 내 포커서블 요소 셀렉터(dialog.tsx 와 동일 — 비활성·tabindex=-1 제외).
const FOCUSABLE =
  'a[href], button:not([disabled]), textarea:not([disabled]), input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])'

// ═══════════════════════════════════════════════════════════════════════════
// A4 라벨 인쇄 미리보기 + 인쇄 뷰 — docs/PROGRAM_STRUCTURE.md §6.1·§7.1 재현.
//   · 화면에서는 모달 미리보기로 보이고(스크린샷 검증 가능), 인쇄 시엔 라벨 문서만 정밀 치수로 출력.
//   · document.body 로 portal → 인쇄 CSS(@media print)가 `body > .print-overlay` 만 남기고 앱 본체를 숨김.
//   · 2열×4행(8칸/페이지), 라벨 99.14×67.48mm, 8칸 초과 시 페이지 분할.
//   · DUAL 바코드: barcode2 가 있으면 상/하 두 블록으로 분기(barcode 상 + barcode2 하), 없으면 단일.
//   · 심볼로지·값표시 는 인쇄 설정(usePrintSettings) 단일 소스를 소비.
// ═══════════════════════════════════════════════════════════════════════════
export function PrintLabelPreview({
  open,
  rows,
  onClose,
}: {
  open: boolean
  rows: DetailRow[]
  onClose: () => void
}) {
  const { symbology, showValueText } = usePrintSettings()

  const overlayRef = useRef<HTMLDivElement>(null)
  const triggerRef = useRef<HTMLElement | null>(null)
  // 매 렌더 새 함수로 오는 onClose 를 ref 로 고정 — effect 재실행 churn(포커스 트리거 오캡처) 방지.
  const onCloseRef = useRef(onClose)
  onCloseRef.current = onClose

  // 열림 동안: 인쇄 CSS 게이트용 body 클래스 토글 + 스크롤 잠금 + Escape 닫기 + 포커스 이동/트랩/복원(모달 a11y).
  useEffect(() => {
    if (!open) return

    // 인쇄 억제 규칙(@media print)은 이 클래스가 있을 때만 활성 — 다른 페이지 Ctrl+P 무영향.
    document.body.classList.add('has-print-overlay')
    // 열기 직전 포커스(트리거 버튼) 기억 — 닫힐 때 복원.
    triggerRef.current = document.activeElement as HTMLElement | null

    const focusables = (): HTMLElement[] =>
      overlayRef.current ? Array.from(overlayRef.current.querySelectorAll<HTMLElement>(FOCUSABLE)) : []

    // 최초 포커스 이동 — 모달 내부 첫 포커서블(툴바 인쇄 버튼). 없으면 컨테이너.
    const items = focusables()
    ;(items[0] ?? overlayRef.current)?.focus()

    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        onCloseRef.current()
        return
      }
      if (e.key !== 'Tab') return
      // 포커스 트랩 — 경계에서 반대편으로 순환(배경으로 새지 않게, dialog.tsx 동형).
      const list = focusables()
      if (list.length === 0) {
        e.preventDefault()
        overlayRef.current?.focus()
        return
      }
      const first = list[0]
      const last = list[list.length - 1]
      const active = document.activeElement as HTMLElement | null
      const inside = overlayRef.current?.contains(active) ?? false
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
    const prevOverflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    return () => {
      document.body.classList.remove('has-print-overlay')
      document.removeEventListener('keydown', onKey)
      document.body.style.overflow = prevOverflow
      // 닫힐 때 트리거(직전 activeElement)로 포커스 복원 — 배경 키보드 포커스 유실 방지.
      const trigger = triggerRef.current
      if (trigger && document.contains(trigger) && !(trigger as HTMLButtonElement).disabled) {
        trigger.focus()
      }
    }
  }, [open])

  if (!open) return null

  const pages = chunk(rows, A4_LABEL.perPage)

  return createPortal(
    <div
      ref={overlayRef}
      role="dialog"
      aria-modal="true"
      aria-label="라벨 인쇄 미리보기"
      tabIndex={-1}
      className="print-overlay fixed inset-0 z-[85] flex flex-col bg-[rgba(0,0,0,0.55)] focus-visible:outline-none"
    >
      {/* ── 상단 툴바(인쇄 시 숨김) ─────────────────────────────────────────── */}
      <div className="print-overlay__chrome flex items-center justify-between gap-4 border-b border-line bg-panel px-5 py-3">
        <div className="min-w-0">
          <h2 className="text-[15px] font-semibold leading-tight text-ink">라벨 인쇄 미리보기</h2>
          <p className="text-[12px] text-faint">
            {rows.length}건 · {pages.length}페이지 · A4 2×4 (99.14×67.48mm) · 심볼로지{' '}
            {SYMBOLOGY_LABELS[symbology]} · 값표시 {showValueText ? 'ON' : 'OFF'}
          </p>
        </div>
        <div className="flex shrink-0 items-center gap-2">
          <Button variant="solid" size="sm" onClick={() => window.print()}>
            <Printer className="size-4" />
            인쇄
          </Button>
          <Button variant="outline" size="sm" onClick={onClose}>
            <X className="size-4" />
            닫기
          </Button>
        </div>
      </div>

      {/* ── 미리보기 스크롤 영역(인쇄 시 스크롤/배경 해제) ───────────────────── */}
      <div className="print-overlay__scroll flex-1 overflow-auto bg-[#525659] p-6">
        <div className="print-doc mx-auto flex w-fit flex-col items-center gap-6">
          {pages.map((pageRows, pi) => (
            <div
              key={pi}
              className="print-page bg-white text-black shadow-card"
              data-page={pi + 1}
              style={{
                width: `${A4_LABEL.pageWidthMm}mm`,
                height: `${A4_LABEL.pageHeightMm}mm`,
                paddingTop: `${A4_LABEL.marginTopMm}mm`,
                paddingBottom: `${A4_LABEL.marginBottomMm}mm`,
                paddingLeft: `${A4_LABEL.marginLeftMm}mm`,
                paddingRight: `${A4_LABEL.marginRightMm}mm`,
                boxSizing: 'border-box',
              }}
            >
              <div
                className="print-grid"
                style={{
                  display: 'grid',
                  gridTemplateColumns: `${A4_LABEL.labelWidthMm}mm ${A4_LABEL.labelWidthMm}mm`,
                  columnGap: `${COLUMN_GAP_MM}mm`,
                  rowGap: 0,
                  alignContent: 'start',
                  justifyContent: 'space-between',
                }}
              >
                {pageRows.map((r) => (
                  <LabelCell key={r.id} row={r} symbology={symbology} showValueText={showValueText} />
                ))}
              </div>
            </div>
          ))}
        </div>
      </div>
    </div>,
    document.body,
  )
}

// 단일 라벨 셀 — 헤더(슈트/배치) + 본문(단일/듀얼 바코드).
function LabelCell({
  row,
  symbology,
  showValueText,
}: {
  row: DetailRow
  symbology: BarcodeSymbology
  showValueText: boolean
}) {
  const dual = !!row.barcode2
  return (
    <div
      className="print-label flex flex-col overflow-hidden"
      data-dual={dual ? 'true' : 'false'}
      style={{
        width: `${A4_LABEL.labelWidthMm}mm`,
        height: `${A4_LABEL.labelHeightMm}mm`,
        borderRadius: `${A4_LABEL.cornerRadiusMm}mm`,
        border: '0.3mm solid #999',
        boxSizing: 'border-box',
        padding: '3mm',
      }}
    >
      <div className="flex items-baseline justify-between gap-2">
        <span className="text-[13px] font-bold tracking-tight">슈트 {padChute(row.chuteNo)}</span>
        <span className="font-mono text-[9px] text-[#555]">
          {row.bizDay} · {row.batch}
        </span>
      </div>
      <div className="mt-1 flex min-h-0 flex-1 flex-col justify-center gap-1">
        {dual ? (
          <>
            <div className="flex min-h-0 flex-1 flex-col items-center justify-center">
              <Barcode value={row.barcode} symbology={symbology} displayValue={showValueText} height={30} />
            </div>
            <div className="flex min-h-0 flex-1 flex-col items-center justify-center">
              <Barcode value={row.barcode2 ?? ''} symbology={symbology} displayValue={showValueText} height={30} />
            </div>
          </>
        ) : (
          <div className="flex min-h-0 flex-1 flex-col items-center justify-center">
            <Barcode value={row.barcode} symbology={symbology} displayValue={showValueText} height={48} />
          </div>
        )}
      </div>
    </div>
  )
}
