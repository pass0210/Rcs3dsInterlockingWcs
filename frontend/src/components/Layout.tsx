import { NavLink, Outlet } from 'react-router-dom'
import { Activity, Cpu, SlidersHorizontal, Radio } from 'lucide-react'
import { useEffect, useState } from 'react'
import { StatusRail } from './StatusRail'
import { fmtClock } from '@/lib/format'
import { POLL_MS } from '@/lib/queries'
import { cn } from '@/lib/utils'

// 좌측 내비 항목 — 모니터링(F1)만 활성. F2/F3는 배지로 예고(고아 링크 아님).
const NAV = [
  { to: '/monitor', label: '모니터링', icon: Activity, enabled: true, phase: null as string | null },
  { to: '#', label: '3DS 워드', icon: Cpu, enabled: false, phase: 'F2' },
  { to: '#', label: '운영 제어', icon: SlidersHorizontal, enabled: false, phase: 'F3' },
]

export function Layout() {
  return (
    <div className="flex h-full">
      {/* ── 좌측 내비 ─────────────────────────────────────────────── */}
      <aside className="flex w-56 shrink-0 flex-col border-r border-line bg-panel/60">
        <div className="flex items-center gap-2.5 border-b border-line px-4 py-4">
          <div className="flex size-8 items-center justify-center rounded-md border border-accent/30 bg-accent/10">
            <Radio className="size-4 text-accent" />
          </div>
          <div className="leading-tight">
            <div className="text-[14px] font-semibold text-ink">WCS 관제</div>
            <div className="font-mono text-[10px] tracking-wide text-faint">3DS INTERLOCKING</div>
          </div>
        </div>

        <nav className="flex flex-col gap-0.5 p-2">
          {NAV.map((item) => {
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
                      ? 'bg-accent/15 text-accent shadow-[0_0_0_1px_rgba(56,189,248,0.25)_inset]'
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
        <header className="flex items-center justify-between gap-4 border-b border-line bg-panel/40 px-5 py-3">
          <div>
            <h1 className="text-[15px] font-semibold text-ink">실시간 모니터링</h1>
            <p className="text-[12px] text-faint">작업 데이터 · 로봇 이동중 · 분류 현황</p>
          </div>
          <StatusRail />
        </header>

        <main className="min-w-0 flex-1 overflow-auto p-5">
          <Outlet />
        </main>
      </div>
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
