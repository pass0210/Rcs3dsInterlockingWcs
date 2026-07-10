import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Select } from '@/components/ui/select'
import { Barcode } from '@/components/Barcode'
import {
  usePrintSettings,
  BARCODE_SYMBOLOGIES,
  SYMBOLOGY_LABELS,
  PRINT_PRESETS,
  type BarcodeSymbology,
  type PrintPreset,
} from '@/lib/printSettings'
import { A4_LABEL, padChute } from '@/lib/labelLayout'

// ═══════════════════════════════════════════════════════════════════════════
// 설정 — 인쇄 설정 전용(docs/PROGRAM_STRUCTURE.md §6.4 재현, 인쇄 파트만).
//   · 바코드 심볼로지(CODE128 기본) · 값 텍스트 표시 on/off · 라벨 프리셋(정본 A4 2×4).
//   · 값은 usePrintSettings 단일 소스에 저장(localStorage 자동 영속) → 다음 인쇄에 즉시 반영.
//   · 자동생성 규칙 UI 는 이식 대상 아님(불변 결정) — 이 화면에 절대 추가하지 않는다.
// ═══════════════════════════════════════════════════════════════════════════
export function SettingsPage() {
  const { symbology, showValueText, preset, setSymbology, setShowValueText, setPreset } = usePrintSettings()

  // 미리보기 표본 — 설정 변경이 렌더에 반영됨을 그 자리에서 확인(듀얼 바코드 예시).
  const sample = { chuteNo: '1', barcode: 'BOWOO-1234567', barcode2: 'LOT-20260708' }

  return (
    <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,1fr)_360px]">
      {/* ── 인쇄 설정 폼 ─────────────────────────────────────────────────── */}
      <Card className="self-start">
        <CardHeader>
          <CardTitle>인쇄 설정</CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col gap-5">
          {/* 바코드 심볼로지 */}
          <label className="flex flex-col gap-1.5">
            <span className="text-[13px] font-medium text-ink">바코드 심볼로지</span>
            <span className="text-[12px] text-faint">라벨 바코드를 인코딩하는 방식. 기본 CODE128.</span>
            <Select
              value={symbology}
              onChange={(e) => setSymbology(e.target.value as BarcodeSymbology)}
              aria-label="바코드 심볼로지"
              className="mt-1 w-full max-w-[280px]"
            >
              {BARCODE_SYMBOLOGIES.map((s) => (
                <option key={s} value={s}>
                  {SYMBOLOGY_LABELS[s]}
                </option>
              ))}
            </Select>
          </label>

          {/* 바코드 값 텍스트 표시 */}
          <label className="flex cursor-pointer items-start gap-2.5">
            <input
              type="checkbox"
              checked={showValueText}
              onChange={(e) => setShowValueText(e.target.checked)}
              className="mt-0.5 size-4 cursor-pointer accent-[var(--color-brand-active)]"
              aria-label="바코드 값 텍스트 표시"
            />
            <span className="flex flex-col">
              <span className="text-[13px] font-medium text-ink">바코드 값 텍스트 표시</span>
              <span className="text-[12px] text-faint">바코드 아래 사람이 읽는 값(숫자/문자)을 표시합니다.</span>
            </span>
          </label>

          {/* 라벨 프리셋 */}
          <label className="flex flex-col gap-1.5">
            <span className="text-[13px] font-medium text-ink">라벨 프리셋</span>
            <span className="text-[12px] text-faint">
              정본 = A4 2×4 (99.14×67.48mm). 그 외 프리셋의 레이아웃 재구성은 후속 예정입니다.
            </span>
            <Select
              value={preset}
              onChange={(e) => setPreset(e.target.value as PrintPreset)}
              aria-label="라벨 프리셋"
              className="mt-1 w-full max-w-[280px]"
            >
              {PRINT_PRESETS.map((p) => (
                <option key={p.id} value={p.id} disabled={!p.enabled}>
                  {p.label}
                </option>
              ))}
            </Select>
          </label>

          <p className="rounded-lg border border-line bg-elevated px-3 py-2 text-[12px] text-muted">
            변경 사항은 자동 저장되어(브라우저 저장소) 다음 인쇄에 즉시 적용됩니다.
          </p>
        </CardContent>
      </Card>

      {/* ── 미리보기 ─────────────────────────────────────────────────────── */}
      <Card className="self-start">
        <CardHeader>
          <CardTitle>미리보기</CardTitle>
        </CardHeader>
        <CardContent>
          <p className="mb-3 text-[12px] text-faint">현재 설정이 적용된 라벨 예시(듀얼 바코드).</p>
          <div
            className="mx-auto flex flex-col overflow-hidden bg-white text-black shadow-card"
            style={{
              width: '260px',
              aspectRatio: `${A4_LABEL.labelWidthMm} / ${A4_LABEL.labelHeightMm}`,
              borderRadius: '10px',
              border: '1px solid #999',
              boxSizing: 'border-box',
              padding: '10px',
            }}
          >
            <div className="flex items-baseline justify-between gap-2">
              <span className="text-[13px] font-bold tracking-tight">슈트 {padChute(sample.chuteNo)}</span>
              <span className="font-mono text-[9px] text-[#555]">미리보기</span>
            </div>
            <div className="mt-1 flex min-h-0 flex-1 flex-col justify-center gap-1">
              <div className="flex min-h-0 flex-1 flex-col items-center justify-center">
                <Barcode value={sample.barcode} symbology={symbology} displayValue={showValueText} height={30} />
              </div>
              <div className="flex min-h-0 flex-1 flex-col items-center justify-center">
                <Barcode value={sample.barcode2} symbology={symbology} displayValue={showValueText} height={30} />
              </div>
            </div>
          </div>
        </CardContent>
      </Card>
    </div>
  )
}
