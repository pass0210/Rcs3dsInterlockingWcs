import { useState } from 'react'
import { ArrowDownToLine, Split, Radio, Download } from 'lucide-react'
import { Tabs, TabsList, TabsTrigger, TabsContent } from '@/components/ui/tabs'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { SearchInput } from '@/components/ui/search-input'
import { ArchiveSelect } from '@/components/ArchiveSelect'
import { TestLogGrid } from './sections/TestLogGrid'
import { ApiCallLogGrid } from './sections/ApiCallLogGrid'
import { useToast } from '@/lib/toast'
import { useUiMode } from '@/lib/uiMode'
import { exportLogs, useApiCallLogs, useTestLogs, type TestLogKind } from '@/lib/logs'
import type { ArchiveFilter } from '@/lib/testData'

type LogTab = TestLogKind | 'apicalls'

// ════════════════════════════════════════════════════════════════════════════
// 로그 조회(/logs) — 투입(INPUT)·분류(SORT)·API 호출 이력 3탭(계약 §D).
//   투입/분류: 전역 bizDay + 아카이브 필터 + 컬럼 필터 + 통합검색. Excel 다운로드(투입+분류 통합).
//   API 호출 이력: date(전역 bizDay 또는 전체) 기반, 아카이브 필터 비노출(E3 계약).
// ════════════════════════════════════════════════════════════════════════════
export function LogsPage() {
  // 탭 상태를 제어 — Excel 다운로드 툴바를 투입/분류 탭에서만 노출(API 호출 이력엔 무의미).
  const [tab, setTab] = useState<LogTab>('input')

  return (
    // 뷰포트 맞춤(S-UI-LAYOUT) — Tabs 가 가용 높이를 채우고(flex-1 min-h-0), 탭바+Excel 툴바=고정 크롬,
    // 활성 탭 카드가 본문을 채워 그리드만 스크롤한다. 기존 calc(100vh-260px) 매직값을 flex 패턴으로 대체.
    <Tabs value={tab} onValueChange={(v) => setTab(v as LogTab)} className="flex min-h-0 flex-1 flex-col gap-3">
      <div className="flex shrink-0 flex-wrap items-center justify-between gap-3">
        <TabsList>
          <TabsTrigger value="input">
            <ArrowDownToLine className="size-4" />
            투입
          </TabsTrigger>
          <TabsTrigger value="sort">
            <Split className="size-4" />
            분류
          </TabsTrigger>
          <TabsTrigger value="apicalls">
            <Radio className="size-4" />
            API 호출 이력
          </TabsTrigger>
        </TabsList>
        {tab !== 'apicalls' && <ExcelDownload />}
      </div>

      <TabsContent value="input" className="mt-0 flex min-h-0 flex-1 flex-col">
        <TestLogTab kind="input" title="투입(INPUT) 로그" equipmentLabel="인덕션" />
      </TabsContent>
      <TabsContent value="sort" className="mt-0 flex min-h-0 flex-1 flex-col">
        <TestLogTab kind="sort" title="분류(SORT) 로그" equipmentLabel="슈트" />
      </TabsContent>
      <TabsContent value="apicalls" className="mt-0 flex min-h-0 flex-1 flex-col">
        <ApiCallLogTab />
      </TabsContent>
    </Tabs>
  )
}

// ── 투입/분류 탭 — E1/E2. 아카이브·통합검색은 탭이 소유, 컬럼 필터는 그리드가 소유 ──────────────
function TestLogTab({
  kind,
  title,
  equipmentLabel,
}: {
  kind: TestLogKind
  title: string
  equipmentLabel: string
}) {
  const { bizDay, autoRefresh, refreshInterval } = useUiMode()
  const interval = autoRefresh ? refreshInterval : (false as const)
  const [archived, setArchived] = useState<ArchiveFilter>('active')
  const [search, setSearch] = useState('')

  const q = useTestLogs(kind, bizDay, archived, interval)

  return (
    <Card className="flex min-h-0 min-w-0 flex-1 flex-col">
      <CardHeader className="shrink-0 flex-wrap gap-2">
        <CardTitle>{title}</CardTitle>
        <div className="flex flex-wrap items-center gap-2">
          <ArchiveSelect value={archived} onChange={setArchived} />
          <SearchInput value={search} onChange={setSearch} className="w-48" placeholder="통합 검색" />
        </div>
      </CardHeader>
      <CardContent className="min-h-0 min-w-0 flex-1 overflow-auto p-0">
        <TestLogGrid
          rows={q.data ?? []}
          loading={q.isLoading}
          error={q.isError ? ((q.error as Error)?.message ?? '로그 조회 실패') : null}
          equipmentLabel={equipmentLabel}
          search={search}
        />
      </CardContent>
    </Card>
  )
}

// ── API 호출 이력 탭 — E3. date=전역 bizDay(기본) 또는 전체(최대 500건). 아카이브 필터 없음 ─────
function ApiCallLogTab() {
  const { bizDay, autoRefresh, refreshInterval } = useUiMode()
  const interval = autoRefresh ? refreshInterval : (false as const)
  const [allDates, setAllDates] = useState(false)
  const [search, setSearch] = useState('')

  const date = allDates ? undefined : bizDay
  const q = useApiCallLogs(date, interval)

  return (
    <Card className="flex min-h-0 min-w-0 flex-1 flex-col">
      <CardHeader className="shrink-0 flex-wrap gap-2">
        <CardTitle>API 호출 이력</CardTitle>
        <div className="flex flex-wrap items-center gap-3">
          <label className="flex cursor-pointer items-center gap-1.5 text-[12px] text-muted">
            <input
              type="checkbox"
              checked={allDates}
              onChange={(e) => setAllDates(e.target.checked)}
              className="size-3.5 cursor-pointer accent-[var(--color-brand-active)]"
            />
            전체 기간(최근 500건)
          </label>
          <SearchInput value={search} onChange={setSearch} className="w-48" placeholder="통합 검색" />
        </div>
      </CardHeader>
      <CardContent className="min-h-0 min-w-0 flex-1 overflow-auto p-0">
        <ApiCallLogGrid
          rows={q.data ?? []}
          loading={q.isLoading}
          error={q.isError ? ((q.error as Error)?.message ?? 'API 호출 이력 조회 실패') : null}
          search={search}
        />
      </CardContent>
    </Card>
  )
}

// ── Excel 다운로드 — E4(투입+분류 통합). 전역 bizDay + 선택적 batch. 실패 토스트 ──────────────
function ExcelDownload() {
  const { bizDay } = useUiMode()
  const { toast } = useToast()
  const [batch, setBatch] = useState('')
  const [busy, setBusy] = useState(false)

  async function onDownload() {
    setBusy(true)
    try {
      const outcome = await exportLogs(bizDay, batch.trim() || undefined)
      toast(
        outcome.ok ? 'success' : 'error',
        outcome.ok ? 'Excel 다운로드를 시작했습니다.' : (outcome.message ?? '내보내기에 실패했습니다.'),
      )
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="flex items-center gap-2">
      <input
        value={batch}
        onChange={(e) => setBatch(e.target.value)}
        placeholder="배치(선택)"
        maxLength={10}
        aria-label="내보낼 배치(선택)"
        className="h-8 w-28 rounded-lg border border-line bg-panel px-2.5 text-[13px] text-ink placeholder:text-faint/70 focus-visible:outline-2 focus-visible:outline-ink"
      />
      <Button variant="outline" size="sm" onClick={onDownload} disabled={busy}>
        <Download className="size-4" />
        {busy ? '내보내는 중…' : 'Excel 다운로드'}
      </Button>
    </div>
  )
}
