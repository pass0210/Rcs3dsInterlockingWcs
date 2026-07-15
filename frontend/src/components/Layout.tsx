import { NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom'
import {
  Activity,
  SlidersHorizontal,
  Radio,
  Database,
  ScrollText,
  GitCompare,
  Package,
  Settings,
  RefreshCw,
  Warehouse,
} from 'lucide-react'
import { useEffect, useState, type ComponentType } from 'react'
import { StatusRail } from './StatusRail'
import { fmtClock } from '@/lib/format'
import { POLL_MS } from '@/lib/queries'
import { useHubLifecycle } from '@/lib/useMonitorHub'
import { REFRESH_INTERVALS, homePathFor, useUiMode, type UiMode } from '@/lib/uiMode'
import { Select } from '@/components/ui/select'
import { cn } from '@/lib/utils'

interface NavItem {
  to: string
  label: string
  icon: ComponentType<{ className?: string }>
  enabled: boolean
  phase: string | null
  title: string
  subtitle: string
}

// ── 모드별 메뉴 세트(docs/B2B-DATAGEN.md §5) ──────────────────────────────────
//   B2C: 관리 흐름(생성→설비) 순으로 데이터 생성·설비 관리를 최상단(S-B2C-UX), 이어서
//        모니터링(F1)·운영 로그(operation_log 테일)·운영 제어(F3b). 항목 정의·경로·enabled 불변.
//        ★ S-UI-LAYOUT: /sorters 라벨을 '3DS 워드'→'운영 로그'로 개명(3DS 레지스터 워드 표시는
//          중복 제거로 /ops 에만 잔존, 이 페이지는 operation_log 라이브 테일 전용으로 재정의). 라우트 유지.
//   B2B: 데이터 생성(S-B2B-2b) 활성 + 로그·비교·박스·설정(B2B-3 배지 예고).
const NAV_SETS: Record<UiMode, NavItem[]> = {
  b2c: [
    { to: '/b2c/test-data', label: '데이터 생성', icon: Database, enabled: true, phase: null, title: '데이터 생성', subtitle: '미할당 오더/바코드 생성 · 생성 결과 열람 · 배치 초기화' },
    { to: '/b2c/facility', label: '설비 관리', icon: Warehouse, enabled: true, phase: null, title: '설비 관리', subtitle: '목적지 구성 · 소터 셀 설정 · 오더 할당 · 슈트 제어' },
    { to: '/monitor', label: '모니터링', icon: Activity, enabled: true, phase: null, title: '실시간 모니터링', subtitle: '작업 데이터 · 로봇 이동중 · 분류 현황' },
    { to: '/sorters', label: '운영 로그', icon: ScrollText, enabled: true, phase: null, title: '운영 로그', subtitle: 'operation_log 실시간 테일 · category/level 필터' },
    { to: '/ops', label: '운영 제어', icon: SlidersHorizontal, enabled: true, phase: null, title: '운영 제어', subtitle: 'Pause/Resume · 워드 편집(안전 3종)' },
  ],
  b2b: [
    { to: '/data-generator', label: '데이터 생성', icon: Database, enabled: true, phase: null, title: '데이터 생성', subtitle: '테스트 데이터 생성 · 업로드 · 관리' },
    { to: '/logs', label: '로그 조회', icon: ScrollText, enabled: true, phase: null, title: '로그 조회', subtitle: '투입 · 분류 · API 호출 이력 · Excel 내보내기' },
    { to: '/comparison', label: '결과 비교', icon: GitCompare, enabled: true, phase: null, title: '결과 비교', subtitle: '투입 · 분류 · 결과 3-way 대조' },
    { to: '/boxes', label: '박스 조회', icon: Package, enabled: true, phase: null, title: '박스 조회', subtitle: '박스 목록 · 내품 상세' },
    { to: '/settings', label: '설정', icon: Settings, enabled: true, phase: null, title: '설정', subtitle: '인쇄 설정 · 바코드 심볼로지 · 라벨 프리셋' },
  ],
}

const REFRESH_LABEL: Record<number, string> = { 3000: '3초', 5000: '5초', 10000: '10초', 30000: '30초' }

export function Layout() {
  // 앱 수명 동안 SignalR 실시간 연결 유지 + oplog 이벤트 → TanStack Query 무효화(§2.3·B2C).
  // 모드와 무관하게 상시 유지 — 기존 B2C 동작 무접촉(회귀 0).
  useHubLifecycle()

  const { mode } = useUiMode()
  const location = useLocation()
  const nav = NAV_SETS[mode]

  // 헤더 타이틀 — 현재 경로에 매칭되는 활성 메뉴 기준(없으면 모드 기본 항목).
  const active = nav.find((n) => n.enabled && n.to === location.pathname)
  const header = active ?? nav.find((n) => n.enabled) ?? nav[0]

  return (
    <div className="flex h-full">
      {/* ── 좌측 내비 ─────────────────────────────────────────────── */}
      <aside className="flex w-56 shrink-0 flex-col border-r border-line bg-panel">
        <div className="flex items-center gap-2.5 border-b border-line px-4 py-4">
          <div className="flex size-8 items-center justify-center rounded-lg bg-brand">
            <Radio className="size-4 text-white" />
          </div>
          <div className="leading-tight">
            <div className="text-[14px] font-semibold text-ink">WCS 관제</div>
            <div className="font-mono text-[10px] tracking-wide text-faint">3DS INTERLOCKING</div>
          </div>
        </div>

        {/* B2C/B2B 모드 토글 */}
        <div className="border-b border-line px-2 py-2">
          <ModeToggle />
        </div>

        <nav className="flex flex-col gap-0.5 p-2">
          {nav.map((item) => {
            const Icon = item.icon
            if (!item.enabled) {
              return (
                <div
                  key={item.label}
                  className="flex cursor-not-allowed items-center gap-3 rounded-md px-3 py-2 text-[13px] text-faint/70"
                  title={`${item.phase} 예정`}
                >
                  <Icon className="size-4" />
                  <span className="flex-1">{item.label}</span>
                  <span className="rounded border border-line px-1 py-0.5 font-mono text-[10px] text-faint">
                    {item.phase}
                  </span>
                </div>
              )
            }
            return (
              <NavLink
                key={item.label}
                to={item.to}
                className={({ isActive }) =>
                  cn(
                    'flex items-center gap-3 rounded-md px-3 py-2 text-[13px] font-medium transition-colors',
                    isActive
                      ? 'bg-elevated font-semibold text-ink shadow-[inset_3px_0_0_0_var(--color-brand)]'
                      : 'text-muted hover:bg-elevated hover:text-ink',
                  )
                }
              >
                <Icon className="size-4" />
                {item.label}
              </NavLink>
            )
          })}
        </nav>

        <div className="mt-auto border-t border-line px-4 py-3">
          <PollIndicator />
        </div>
      </aside>

      {/* ── 메인 ──────────────────────────────────────────────────── */}
      <div className="flex min-w-0 flex-1 flex-col">
        <header className="flex items-center justify-between gap-4 border-b border-line bg-panel px-5 py-3">
          <div>
            <h1 className="text-[16px] font-semibold leading-tight tracking-[-0.01em] text-ink">
              {header.title}
            </h1>
            {header.subtitle && <p className="text-[12px] text-faint">{header.subtitle}</p>}
          </div>
          {/* 우측 컨트롤 — B2C: 소터 상태 레일 / B2B: 업무일자 + 자동 새로고침 */}
          {mode === 'b2c' ? <StatusRail /> : <B2bHeaderControls />}
        </header>

        {/* 뷰포트 바운드 높이 컨텍스트(S-UI-LAYOUT) — 사이드바·상단 헤더는 이 main 밖이라 항상 고정.
            main 은 flex 컬럼으로 자식 페이지에 "가시영역에 갇힌 높이"를 제공한다. 각 페이지는 헤더/툴바/
            필터를 shrink-0 크롬으로, 그리드 본문을 flex-1 min-h-0 overflow-auto 로 두어 본문만 스크롤한다.
            overflow-auto 는 폴백 전용: 페이지들이 flex-1 로 정확히 채우면 스크롤이 없고, 매우 작은 뷰포트에서
            내부 min-height 하한 합이 가용 높이를 넘을 때만 페이지(main)가 스크롤한다(하한 미붕괴 보장). */}
        <main className="flex min-h-0 min-w-0 flex-1 flex-col overflow-auto p-5">
          <Outlet />
        </main>
      </div>
    </div>
  )
}

// B2C/B2B 세그먼트 토글 — 전환 시 해당 모드 기본 페이지로 이동.
function ModeToggle() {
  const { mode, setMode } = useUiMode()
  const navigate = useNavigate()

  const select = (m: UiMode) => {
    if (m === mode) return
    setMode(m)
    navigate(homePathFor(m))
  }

  return (
    <div className="flex rounded-lg border border-line bg-elevated p-0.5" role="tablist" aria-label="UI 모드">
      {(['b2c', 'b2b'] as UiMode[]).map((m) => (
        <button
          key={m}
          role="tab"
          aria-selected={mode === m}
          onClick={() => select(m)}
          className={cn(
            'flex-1 rounded-md px-2 py-1 text-[12px] font-semibold transition-colors',
            mode === m ? 'bg-panel text-ink shadow-card' : 'text-muted hover:text-ink',
          )}
        >
          {m === 'b2c' ? 'B2C' : 'B2B'}
        </button>
      ))}
    </div>
  )
}

// B2B 헤더 컨트롤 — 전역 업무일자(native date) + 자동 새로고침 토글(+간격).
function B2bHeaderControls() {
  const { bizDay, setBizDay, autoRefresh, setAutoRefresh, refreshInterval, setRefreshInterval } = useUiMode()
  return (
    <div className="flex items-center gap-3">
      <label className="flex items-center gap-2 text-[12px] text-faint">
        업무일자
        <input
          type="date"
          value={bizDay}
          onChange={(e) => setBizDay(e.target.value)}
          className="h-8 rounded-lg border border-line bg-panel px-2 text-[13px] text-ink focus-visible:outline-2 focus-visible:outline-ink"
        />
      </label>
      <label className="flex cursor-pointer items-center gap-1.5 text-[12px] text-muted">
        <input
          type="checkbox"
          checked={autoRefresh}
          onChange={(e) => setAutoRefresh(e.target.checked)}
          className="size-3.5 cursor-pointer accent-[var(--color-brand-active)]"
        />
        <RefreshCw className={cn('size-3.5', autoRefresh && 'text-online')} />
        자동 새로고침
      </label>
      <Select
        value={refreshInterval}
        onChange={(e) => setRefreshInterval(Number(e.target.value))}
        disabled={!autoRefresh}
        aria-label="새로고침 간격"
      >
        {REFRESH_INTERVALS.map((ms) => (
          <option key={ms} value={ms}>
            {REFRESH_LABEL[ms]}
          </option>
        ))}
      </Select>
    </div>
  )
}

// 폴링 상태 표시 — 데이터가 주기 갱신됨을 시각화(하단 좌측).
function PollIndicator() {
  const [now, setNow] = useState(() => new Date())
  useEffect(() => {
    const t = setInterval(() => setNow(new Date()), 1000)
    return () => clearInterval(t)
  }, [])
  return (
    <div className="flex items-center gap-2 text-[11px] text-faint">
      <span className="size-1.5 rounded-full bg-online text-online lamp-live" />
      <span>폴링 {POLL_MS / 1000}s</span>
      <span className="ml-auto font-mono tabular-nums">{fmtClock(now)}</span>
    </div>
  )
}
