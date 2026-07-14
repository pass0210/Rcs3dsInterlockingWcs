import { useCallback, useMemo, useState, type FormEvent, type ReactNode } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { Plus } from 'lucide-react'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { EmptyRow, ErrorRow, LoadingRow } from '@/components/StateMessage'
import { useToast } from '@/lib/toast'
import { todayBizDay } from '@/lib/uiMode'
import { b2cTestData, useB2cBatches } from '@/lib/b2cTestData'

// ═══════════════════════════════════════════════════════════════════════════
// B2cDataGenPage — B2C(3D 소터) 데이터 생성(2a 슬림). 오더/바코드만 만든다(목적지 미할당).
//   좌: 5-파라미터 생성 폼(작업일자·배치명·차수·계획수량·바코드 접두).
//   우: 생성 결과 view(최근 배치 요약 — 미할당 오더 수). 목적지 배정은 "설비 관리" 페이지가 담당.
// docs/B2C-DATAGEN.md. 목적지 구성/셀/오더 할당/reset 은 설비 관리 페이지로 이관됨.
// ═══════════════════════════════════════════════════════════════════════════

export function B2cDataGenPage() {
  const qc = useQueryClient()
  const batchesQ = useB2cBatches(false)
  const batches = useMemo(() => batchesQ.data ?? [], [batchesQ.data])

  const invalidateBatches = useCallback(() => {
    qc.invalidateQueries({ queryKey: ['b2c-batches'] })
  }, [qc])

  return (
    <div className="grid grid-cols-1 gap-4 xl:grid-cols-[360px_minmax(0,1fr)]">
      {/* 좌: 생성 폼 */}
      <Card className="self-start">
        <CardHeader>
          <CardTitle>데이터 생성</CardTitle>
        </CardHeader>
        <CardContent>
          <B2cGenerateForm onGenerated={invalidateBatches} />
        </CardContent>
      </Card>

      {/* 우: 생성 결과(최근 배치) */}
      <Card className="flex min-w-0 flex-col">
        <CardHeader>
          <CardTitle>생성 결과 — 최근 배치</CardTitle>
        </CardHeader>
        <CardContent className="min-w-0 overflow-auto p-0">
          {batchesQ.isLoading ? (
            <LoadingRow />
          ) : batchesQ.isError ? (
            <ErrorRow message={(batchesQ.error as Error)?.message ?? '배치 조회 실패'} />
          ) : batches.length === 0 ? (
            <EmptyRow label="생성된 배치가 없습니다. 좌측에서 데이터를 생성하세요." />
          ) : (
            <table className="w-full text-[12px]">
              <thead className="sticky top-0 bg-panel text-left text-faint">
                <tr className="border-b border-line">
                  <th className="px-3 py-2 font-medium">작업일자</th>
                  <th className="px-3 py-2 font-medium">배치</th>
                  <th className="px-3 py-2 font-medium">차수</th>
                  <th className="px-3 py-2 font-medium">상태</th>
                  <th className="px-3 py-2 font-medium">오더(미할당)</th>
                  <th className="px-3 py-2 font-medium">항목</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-line">
                {batches.map((b) => (
                  <tr key={b.batchId} className="text-ink">
                    <td className="px-3 py-1.5 font-mono tabular-nums">{b.workDate}</td>
                    <td className="px-3 py-1.5">{b.batchNo}</td>
                    <td className="px-3 py-1.5 font-mono tabular-nums text-muted">{b.waveNo}</td>
                    <td className="px-3 py-1.5 text-faint">{b.status}</td>
                    <td className="px-3 py-1.5 font-mono tabular-nums">
                      {b.orderTotal}
                      <span className="ml-1 text-faint">(미할당 {b.orderUnassigned})</span>
                    </td>
                    <td className="px-3 py-1.5 font-mono tabular-nums text-muted">{b.itemTotal}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          <p className="border-t border-line px-3 py-2 text-[11px] text-faint">
            생성된 오더는 <b>목적지 미할당</b> 상태입니다. 목적지·셀 구성과 오더 배정은{' '}
            <b>설비 관리</b> 페이지에서 수행하세요.
          </p>
        </CardContent>
      </Card>
    </div>
  )
}

// ── 생성 폼(5 파라미터) ──────────────────────────────────────────────────────
function B2cGenerateForm({ onGenerated }: { onGenerated: () => void }) {
  const { toast } = useToast()

  const [workDate, setWorkDate] = useState(todayBizDay)
  const [batchNo, setBatchNo] = useState('')
  const [waveNo, setWaveNo] = useState('1')
  const [plannedQty, setPlannedQty] = useState('10')
  const [barcodePrefix, setBarcodePrefix] = useState('')
  const [submitting, setSubmitting] = useState(false)

  async function onSubmit(e: FormEvent) {
    e.preventDefault()
    const nums = { waveNo: Number(waveNo), plannedQty: Number(plannedQty) }
    if (!batchNo.trim() || !barcodePrefix.trim() || !workDate.trim()) {
      toast('warning', '작업일자·배치명·바코드 접두를 모두 입력하세요.')
      return
    }
    if (!/^[A-Za-z0-9_-]{1,50}$/.test(barcodePrefix.trim())) {
      toast('warning', '바코드 접두는 영문·숫자·하이픈·언더스코어만 허용합니다.')
      return
    }
    for (const [k, v] of Object.entries(nums)) {
      if (!Number.isInteger(v) || v < 1) {
        toast('warning', `${k} 는 1 이상의 정수여야 합니다.`)
        return
      }
    }
    // 상한(백엔드 B2cConstants.GenerateCountMax 미러 — 서버 400 이 최종 권위·절대규칙 #7).
    if (nums.plannedQty > GENERATE_COUNT_MAX) {
      toast('warning', `계획수량(생성 개수)은 ${GENERATE_COUNT_MAX} 이하여야 합니다.`)
      return
    }

    setSubmitting(true)
    try {
      const outcome = await b2cTestData.generate({
        workDate: workDate.trim(),
        batchNo: batchNo.trim(),
        waveNo: nums.waveNo,
        plannedQty: nums.plannedQty,
        barcodePrefix: barcodePrefix.trim(),
      })
      toast(outcome.ok ? 'success' : 'error', outcome.message)
      if (outcome.ok) onGenerated()
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <form onSubmit={onSubmit} noValidate className="flex flex-col gap-3">
      <Field label="작업일자">
        <input value={workDate} onChange={(e) => setWorkDate(e.target.value)} type="date" className={inputBase} />
      </Field>
      <Field label="배치명">
        <input value={batchNo} onChange={(e) => setBatchNo(e.target.value)}
          placeholder="예: FIELD-15" maxLength={100} className={inputBase} />
      </Field>
      <div className="grid grid-cols-2 gap-3">
        <Field label="차수">
          <input value={waveNo} onChange={(e) => setWaveNo(e.target.value)}
            type="number" min={1} className={inputBase} />
        </Field>
        <Field label="계획수량(생성 개수)">
          <input value={plannedQty} onChange={(e) => setPlannedQty(e.target.value)}
            type="number" min={1} max={GENERATE_COUNT_MAX} className={inputBase} />
        </Field>
      </div>
      <Field label="바코드 접두">
        <input value={barcodePrefix} onChange={(e) => setBarcodePrefix(e.target.value)}
          placeholder="예: 0714-A" maxLength={50} className={inputBase} />
        <p className="mt-1 text-[11px] text-faint">
          오더번호 = 바코드 = &quot;{barcodePrefix || '접두'}-NN&quot; ({plannedQty || 'N'}건, 각 계획 1).
        </p>
      </Field>
      <Button type="submit" variant="solid" disabled={submitting} className="mt-1 w-full">
        <Plus className="size-4" />
        {submitting ? '생성 중…' : '데이터 생성'}
      </Button>
      <p className="text-[11px] text-faint">
        멱등: 같은 파라미터 재실행 시 카운트 불변(upsert). <b>목적지 미할당</b> 오더/바코드만 생성합니다.
      </p>
    </form>
  )
}

// 생성 개수 상한 — 백엔드 B2cConstants.GenerateCountMax 미러(1곳·근거 주석). 서버 400 이 최종 권위.
const GENERATE_COUNT_MAX = 1000

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
