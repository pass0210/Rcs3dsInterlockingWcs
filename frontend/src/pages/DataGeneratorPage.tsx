import { useCallback, useMemo, useState, type ReactNode } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { RotateCcw, Trash2, Archive } from 'lucide-react'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Select } from '@/components/ui/select'
import { ConfirmDialog } from '@/components/ui/dialog'
import { GenerateForm } from './sections/GenerateForm'
import { SummaryGrid } from './sections/SummaryGrid'
import { DetailGrid } from './sections/DetailGrid'
import { useToast } from '@/lib/toast'
import { useUiMode } from '@/lib/uiMode'
import {
  summaryKey,
  testData,
  useTestDataDetail,
  useTestDataSummary,
  type ArchiveFilter,
  type SummaryRow,
} from '@/lib/testData'

interface PendingAction {
  title: string
  description: ReactNode
  confirmLabel: string
  run: () => Promise<void>
}

const ARCHIVE_LABELS: Record<ArchiveFilter, string> = {
  active: '활성만',
  all: '전체(보관 포함)',
  archivedOnly: '보관만',
}

export function DataGeneratorPage() {
  const { bizDay, autoRefresh, refreshInterval } = useUiMode()
  const { toast } = useToast()
  const qc = useQueryClient()
  const interval = autoRefresh ? refreshInterval : (false as const)

  // 선택 상태.
  const [selectedBatch, setSelectedBatch] = useState<{ bizDay: string; batch: string } | null>(null)
  const [archived, setArchived] = useState<ArchiveFilter>('active')
  const [summaryChecked, setSummaryChecked] = useState<Set<string>>(new Set())
  const [detailChecked, setDetailChecked] = useState<Set<number>>(new Set())
  const [pending, setPending] = useState<PendingAction | null>(null)
  const [busy, setBusy] = useState(false)

  // 조회.
  const summaryQ = useTestDataSummary(bizDay, interval)
  const detailQ = useTestDataDetail(selectedBatch, archived, interval)
  // 안정 식별자(useMemo 의존성 안정화 — react-hooks/exhaustive-deps).
  const summaryRows = useMemo(() => summaryQ.data ?? [], [summaryQ.data])
  const detailRows = detailQ.data ?? []

  const selectedKey = selectedBatch ? summaryKey(selectedBatch.bizDay, selectedBatch.batch) : null

  const invalidateAll = useCallback(() => {
    qc.invalidateQueries({ queryKey: ['testdata-summary'] })
    qc.invalidateQueries({ queryKey: ['testdata-detail'] })
  }, [qc])

  // 요약 행 클릭 → 상세 로드 + 상세 선택 초기화(§4.4).
  const onRowClick = useCallback((row: SummaryRow) => {
    setSelectedBatch({ bizDay: row.bizDay, batch: row.batch })
    setDetailChecked(new Set())
  }, [])

  const toggleSummaryCheck = useCallback((key: string) => {
    setSummaryChecked((prev) => {
      const next = new Set(prev)
      if (next.has(key)) next.delete(key)
      else next.add(key)
      return next
    })
  }, [])

  const toggleSummaryVisible = useCallback((keys: string[], allChecked: boolean) => {
    setSummaryChecked((prev) => {
      const next = new Set(prev)
      if (allChecked) keys.forEach((k) => next.delete(k))
      else keys.forEach((k) => next.add(k))
      return next
    })
  }, [])

  const toggleDetailCheck = useCallback((id: number) => {
    setDetailChecked((prev) => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }, [])

  const toggleDetailVisible = useCallback((ids: number[], allChecked: boolean) => {
    setDetailChecked((prev) => {
      const next = new Set(prev)
      if (allChecked) ids.forEach((id) => next.delete(id))
      else ids.forEach((id) => next.add(id))
      return next
    })
  }, [])

  // ── 수신 초기화(요약 체크 배치) ─────────────────────────────────────────────
  const summarySelectedRows = useMemo(
    () => summaryRows.filter((r) => summaryChecked.has(summaryKey(r.bizDay, r.batch))),
    [summaryRows, summaryChecked],
  )

  function requestReset() {
    const targets = summarySelectedRows
    if (targets.length === 0) {
      toast('warning', '초기화할 배치를 선택하세요.')
      return
    }
    setPending({
      title: '수신 초기화',
      confirmLabel: '초기화',
      description: (
        <>
          선택한 <b>{targets.length}</b>개 배치의 수신시간을 초기화하고 연관 로그·결과를 보관 처리합니다.
          <br />
          이 작업은 되돌릴 수 없습니다.
        </>
      ),
      run: async () => {
        // 각 배치 상세를 병렬 조회(archived=all — test_data 원장 id 전량) → id 취합.
        const details = await Promise.all(targets.map((r) => testData.detail(r.bizDay, r.batch, 'all')))
        const ids = details.flat().map((d) => d.id)
        if (ids.length === 0) {
          toast('warning', '초기화할 데이터가 없습니다.')
          return
        }
        const outcome = await testData.reset(ids)
        toast(outcome.ok ? 'success' : 'error', outcome.message)
        if (outcome.ok) {
          invalidateAll()
          setSummaryChecked(new Set())
        }
      },
    })
  }

  // ── 삭제(상세 체크 행) ──────────────────────────────────────────────────────
  function requestDelete() {
    const ids = [...detailChecked]
    if (ids.length === 0) {
      toast('warning', '삭제할 상세 행을 선택하세요.')
      return
    }
    setPending({
      title: '선택 삭제',
      confirmLabel: '삭제',
      description: (
        <>
          선택한 <b>{ids.length}</b>개 항목을 삭제하고 연관 로그·결과를 보관 처리합니다.
          <br />
          삭제분은 아카이브 필터 <b>보관만</b>에서 확인할 수 있습니다.
        </>
      ),
      run: async () => {
        const outcome = await testData.remove(ids)
        toast(outcome.ok ? 'success' : 'error', outcome.message)
        if (outcome.ok) {
          invalidateAll()
          setDetailChecked(new Set())
        }
      },
    })
  }

  async function onConfirm() {
    if (!pending) return
    setBusy(true)
    try {
      await pending.run()
    } catch (e) {
      toast('error', `작업 실패 — ${(e as Error).message}`)
    } finally {
      setBusy(false)
      setPending(null)
    }
  }

  // 안정 onCancel(FIX-2) — 다이얼로그 열린 동안 Dialog effect 재실행 churn 제거.
  const closePending = useCallback(() => setPending(null), [])

  return (
    <div className="grid grid-cols-1 gap-4 xl:grid-cols-[320px_minmax(0,1fr)_minmax(0,1.35fr)]">
      {/* 좌: 생성 폼 + 업로드 */}
      <Card className="self-start">
        <CardHeader>
          <CardTitle>데이터 생성</CardTitle>
        </CardHeader>
        <CardContent>
          <GenerateForm />
        </CardContent>
      </Card>

      {/* 중: 요약 그리드 */}
      <Card className="flex min-w-0 flex-col">
        <CardHeader>
          <CardTitle>배치 요약</CardTitle>
          <Button
            variant="outline"
            size="sm"
            onClick={requestReset}
            disabled={summaryChecked.size === 0}
          >
            <RotateCcw className="size-4" />
            수신 초기화{summaryChecked.size > 0 ? ` (${summaryChecked.size})` : ''}
          </Button>
        </CardHeader>
        <CardContent className="max-h-[calc(100vh-220px)] min-w-0 overflow-auto p-0">
          <SummaryGrid
            rows={summaryRows}
            loading={summaryQ.isLoading}
            error={summaryQ.isError ? ((summaryQ.error as Error)?.message ?? '요약 조회 실패') : null}
            selectedKey={selectedKey}
            checked={summaryChecked}
            onRowClick={onRowClick}
            onToggleCheck={toggleSummaryCheck}
            onToggleVisible={toggleSummaryVisible}
          />
        </CardContent>
      </Card>

      {/* 우: 상세 그리드 */}
      <Card className="flex min-w-0 flex-col">
        <CardHeader className="flex-wrap gap-2">
          <CardTitle>상세</CardTitle>
          <div className="flex items-center gap-2">
            <span className="flex items-center gap-1 text-[12px] text-faint">
              <Archive className="size-3.5" />
              보관
            </span>
            <Select
              value={archived}
              onChange={(e) => setArchived(e.target.value as ArchiveFilter)}
              aria-label="아카이브 필터"
            >
              {(Object.keys(ARCHIVE_LABELS) as ArchiveFilter[]).map((k) => (
                <option key={k} value={k}>
                  {ARCHIVE_LABELS[k]}
                </option>
              ))}
            </Select>
            <Button
              variant="outline"
              size="sm"
              onClick={requestDelete}
              disabled={detailChecked.size === 0}
              className="border-offline/50 text-offline hover:bg-offline/5"
            >
              <Trash2 className="size-4" />
              삭제{detailChecked.size > 0 ? ` (${detailChecked.size})` : ''}
            </Button>
          </div>
        </CardHeader>
        <CardContent className="max-h-[calc(100vh-220px)] min-w-0 overflow-auto p-0">
          <DetailGrid
            rows={detailRows}
            loading={detailQ.isLoading}
            error={detailQ.isError ? ((detailQ.error as Error)?.message ?? '상세 조회 실패') : null}
            hasSelection={selectedBatch !== null}
            checked={detailChecked}
            onToggleCheck={toggleDetailCheck}
            onToggleVisible={toggleDetailVisible}
          />
        </CardContent>
      </Card>

      <ConfirmDialog
        open={pending !== null}
        title={pending?.title ?? ''}
        description={pending?.description}
        confirmLabel={pending?.confirmLabel ?? '확인'}
        busy={busy}
        onConfirm={onConfirm}
        onCancel={closePending}
      />
    </div>
  )
}
