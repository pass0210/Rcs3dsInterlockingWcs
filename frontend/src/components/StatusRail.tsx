import { useSorters } from '@/lib/queries'
import type { SorterStatus } from '@/lib/api'
import { cn } from '@/lib/utils'

// ═══════════════════════════════════════════════════════════════════════════
// StatusRail — 시그니처 요소. 각 소터를 계기판 "슬롯 타일"로 표기:
//   chuteNo 판독값(모노) + 온라인 램프(펄스) + Ready/Full/Paused 상태 램프.
// 상단바에 상주해 어느 페이지에서도 소터 Online/Offline을 한눈에(고아 페이지 방지).
// ═══════════════════════════════════════════════════════════════════════════
export function StatusRail() {
  const { data: sorters, isLoading, isError } = useSorters()

  return (
    <div className="flex items-center gap-2" aria-label="소터 상태">
      {isLoading && <span className="text-[12px] text-faint">소터 상태 확인 중…</span>}
      {isError && <span className="text-[12px] text-offline">소터 상태 조회 실패</span>}
      {!isLoading && !isError && (sorters?.length ?? 0) === 0 && (
        <span className="text-[12px] text-faint">등록된 소터 없음</span>
      )}
      {sorters?.map((s) => <SorterTile key={s.destId} sorter={s} />)}
    </div>
  )
}

function SorterTile({ sorter }: { sorter: SorterStatus }) {
  const { online, ready, full, paused, chuteNo } = sorter
  return (
    <div
      className={cn(
        'flex items-center gap-2.5 rounded-md border px-2.5 py-1.5',
        online ? 'border-line bg-elevated' : 'border-offline/30 bg-offline/5',
      )}
      title={`소터 슈트 ${chuteNo} — ${online ? '온라인' : '오프라인'}`}
    >
      {/* 온라인 램프 */}
      <span
        className={cn(
          'size-2 rounded-full',
          online ? 'bg-online text-online lamp-live' : 'bg-offline text-offline',
        )}
      />
      <div className="flex items-baseline gap-1.5">
        <span className="text-[10px] uppercase tracking-wider text-faint">3DS</span>
        <span className="font-mono text-[13px] font-semibold leading-none text-ink">
          #{String(chuteNo).padStart(2, '0')}
        </span>
      </div>
      {/* 상태 램프 그룹 */}
      <div className="flex items-center gap-1.5 border-l border-line pl-2.5">
        <Lamp label="RDY" on={online && ready} tone="online" />
        <Lamp label="FULL" on={full} tone="warn" />
        <Lamp label="PAUSE" on={paused} tone="offline" />
      </div>
    </div>
  )
}

function Lamp({ label, on, tone }: { label: string; on: boolean; tone: 'online' | 'warn' | 'offline' }) {
  const color =
    tone === 'online' ? 'bg-online' : tone === 'warn' ? 'bg-warn' : 'bg-offline'
  return (
    <span className="flex items-center gap-1" title={`${label}: ${on ? 'ON' : 'off'}`}>
      <span className={cn('size-1.5 rounded-full', on ? color : 'bg-line')} />
      <span className={cn('text-[10px] font-medium tracking-wide', on ? 'text-muted' : 'text-faint/60')}>
        {label}
      </span>
    </span>
  )
}
