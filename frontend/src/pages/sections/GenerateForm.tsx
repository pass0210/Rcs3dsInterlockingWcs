import { useRef, useState, type FormEvent, type ReactNode } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { Plus, Upload, FileSpreadsheet } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { useToast } from '@/lib/toast'
import { useUiMode } from '@/lib/uiMode'
import { testData } from '@/lib/testData'
import { cn } from '@/lib/utils'

// ── 좌측: 생성 폼(배치·슈트범위·바코드개수) + 엑셀 업로드 ─────────────────────
//   · 전역 bizDay 표시(읽기 전용). · Enter 제출. · 미입력 경고 토스트. · 성공 시 요약 리로드 + 폼 리셋.
export function GenerateForm() {
  const { bizDay } = useUiMode()
  const { toast } = useToast()
  const qc = useQueryClient()

  const [batch, setBatch] = useState('')
  const [chuteNos, setChuteNos] = useState('')
  const [barcodeCount, setBarcodeCount] = useState('')
  const [submitting, setSubmitting] = useState(false)

  const [file, setFile] = useState<File | null>(null)
  const [uploading, setUploading] = useState(false)
  const fileRef = useRef<HTMLInputElement>(null)

  const reloadSummary = () => qc.invalidateQueries({ queryKey: ['testdata-summary'] })

  async function onSubmit(e: FormEvent) {
    e.preventDefault()
    const count = Number(barcodeCount)
    // 미입력·형식 경고(서버 검증 이전 UX 가드).
    if (!batch.trim() || !chuteNos.trim() || !barcodeCount.trim()) {
      toast('warning', '배치·슈트 번호·바코드 개수를 모두 입력하세요.')
      return
    }
    if (!Number.isInteger(count) || count < 1) {
      toast('warning', '바코드 개수는 1 이상의 정수여야 합니다.')
      return
    }
    // 상한 10000(§1.1) — 서버 400 왕복 전 클라에서 차단(input max 와 정합).
    if (count > 10000) {
      toast('warning', '바코드 개수는 10000 이하여야 합니다.')
      return
    }

    setSubmitting(true)
    try {
      const outcome = await testData.generate({
        bizDay,
        batch: batch.trim(),
        chuteNos: chuteNos.trim(),
        barcodeCount: count,
      })
      toast(outcome.ok ? 'success' : 'error', outcome.message)
      if (outcome.ok) {
        await reloadSummary()
        setBatch('')
        setChuteNos('')
        setBarcodeCount('')
      }
    } finally {
      setSubmitting(false)
    }
  }

  async function onUpload() {
    if (!file) {
      toast('warning', '업로드할 엑셀 파일을 선택하세요.')
      return
    }
    setUploading(true)
    try {
      const outcome = await testData.upload(file)
      toast(outcome.ok ? 'success' : 'error', outcome.message)
      if (outcome.ok) {
        await reloadSummary()
        setFile(null)
        if (fileRef.current) fileRef.current.value = ''
      }
    } finally {
      setUploading(false)
    }
  }

  return (
    <div className="flex flex-col gap-5">
      {/* 수동 생성 폼 */}
      {/* noValidate — 검증은 앱 토스트로 일원화(브라우저 네이티브 말풍선이 onSubmit·토스트를
          선점하지 않게). min/max 는 스피너 힌트로 유지하되 제출 차단은 JS 가드가 담당. */}
      <form onSubmit={onSubmit} noValidate className="flex flex-col gap-3">
        <Field label="업무일자">
          <input
            value={bizDay}
            disabled
            className={cn(inputBase, 'cursor-not-allowed bg-elevated text-muted')}
            aria-label="업무일자(헤더에서 변경)"
          />
          <p className="mt-1 text-[11px] text-faint">헤더의 업무일자 컨트롤에서 변경합니다.</p>
        </Field>

        <Field label="배치">
          <input
            value={batch}
            onChange={(e) => setBatch(e.target.value)}
            placeholder="예: 001"
            maxLength={10}
            className={inputBase}
          />
        </Field>

        <Field label="슈트 번호">
          <input
            value={chuteNos}
            onChange={(e) => setChuteNos(e.target.value)}
            placeholder="예: 1-3, 5, 6"
            className={inputBase}
          />
          <p className="mt-1 text-[11px] text-faint">쉼표로 구분, 범위는 하이픈(라운드로빈 배분).</p>
        </Field>

        <Field label="바코드 개수">
          <input
            value={barcodeCount}
            onChange={(e) => setBarcodeCount(e.target.value)}
            type="number"
            min={1}
            max={10000}
            placeholder="예: 100"
            className={inputBase}
          />
        </Field>

        <Button type="submit" variant="solid" disabled={submitting} className="mt-1 w-full">
          <Plus className="size-4" />
          {submitting ? '생성 중…' : '데이터 생성'}
        </Button>
      </form>

      {/* 엑셀 업로드 */}
      <div className="flex flex-col gap-2 border-t border-line pt-4">
        <div className="flex items-center gap-2 text-[13px] font-semibold text-ink">
          <FileSpreadsheet className="size-4 text-muted" />
          엑셀 업로드
        </div>
        <p className="text-[11px] text-faint">컬럼: 날짜, 배치, 바코드, 슈트 (.xlsx / .xls)</p>
        <input
          ref={fileRef}
          type="file"
          accept=".xlsx,.xls"
          onChange={(e) => setFile(e.target.files?.[0] ?? null)}
          className={cn(
            'block w-full cursor-pointer rounded-lg border border-line bg-panel text-[12px] text-muted',
            'file:mr-3 file:cursor-pointer file:border-0 file:bg-elevated file:px-3 file:py-2 file:text-[12px] file:font-medium file:text-ink',
          )}
        />
        <Button
          type="button"
          variant="outline"
          onClick={onUpload}
          disabled={uploading || !file}
          className="w-full"
        >
          <Upload className="size-4" />
          {uploading ? '업로드 중…' : '업로드'}
        </Button>
      </div>
    </div>
  )
}

const inputBase =
  'h-10 w-full rounded-lg border border-line bg-panel px-3 text-[13px] text-ink placeholder:text-faint/70 focus-visible:outline-2 focus-visible:outline-ink'

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="block">
      <span className="mb-1 block text-[12px] font-medium text-muted">{label}</span>
      {children}
    </label>
  )
}
