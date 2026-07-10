import { useEffect, useRef, useState } from 'react'
import type { RegKey, SorterWordState } from '@/lib/signalr'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Badge } from '@/components/ui/badge'
import { fmtTime } from '@/lib/format'
import { cn } from '@/lib/utils'

// ═══════════════════════════════════════════════════════════════════════════
// WordPanel — 3DS 레지스터 워드 실시간 뷰(읽기 전용·F2).
//   D0 C_CellNo · D1 C_Seq · D2 R_CellNo · D3 R_Seq · D4 비트(C_Flag/R_Flag/Ready)
//   · D5 CurFloor · D6 TgtFloor · Online. SignalR push로 갱신, 변경값 하이라이트 +
//   각 값 마지막 변경 시각. 편집/쓰기 컨트롤 없음(F3).
// ═══════════════════════════════════════════════════════════════════════════
export function WordPanel({ state }: { state: SorterWordState | undefined }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>3DS 레지스터 워드</CardTitle>
        <div className="flex items-center gap-2">
          <Badge tone="neutral">읽기 전용</Badge>
          <OnlineIndicator state={state} />
        </div>
      </CardHeader>
      <CardContent>
        {!state ? (
          <div className="px-1 py-8 text-center text-[13px] text-muted">
            소터 워드 스냅샷 대기 중… (접속 시 부트스트랩 수신)
          </div>
        ) : (
          <div className="flex flex-col gap-3">
            <div className="grid grid-cols-2 gap-2 sm:grid-cols-3 lg:grid-cols-4">
              <RegTile addr="D0" name="C_CellNo" desc="지정 셀" value={state.word.cCellNo} reg="C_CellNo" state={state} />
              <RegTile addr="D1" name="C_Seq" desc="명령 순번" value={state.word.cSeq} reg="C_Seq" state={state} />
              <RegTile addr="D2" name="R_CellNo" desc="적재 셀" value={state.word.rCellNo} reg="R_CellNo" state={state} />
              <RegTile addr="D3" name="R_Seq" desc="처리 순번" value={state.word.rSeq} reg="R_Seq" state={state} />
              <RegTile addr="D5" name="CurFloor" desc="현재 층" value={state.word.curFloor} reg="CurFloor" state={state} />
              <RegTile addr="D6" name="TgtFloor" desc="목표 층" value={state.word.tgtFloor} reg="TgtFloor" state={state} />
            </div>

            {/* D4 플래그/상태 워드 — 비트 분해(C_Flag·R_Flag·Ready) */}
            <div className="rounded-[14px] border border-line bg-panel p-3">
              <div className="mb-2 flex items-center gap-2">
                <span className="rounded border border-line px-1.5 py-0.5 font-mono text-[10px] text-muted">D4</span>
                <span className="font-mono text-[13px] font-semibold text-ink">Flags</span>
                <span className="text-[11px] text-muted">플래그/상태 워드 (비트 분해)</span>
              </div>
              <div className="grid grid-cols-1 gap-2 sm:grid-cols-3">
                <BitLamp name="C_Flag" desc="WCS set / PLC clear" on={state.word.cFlag} reg="C_Flag" state={state} />
                <BitLamp name="R_Flag" desc="PLC set / WCS clear" on={state.word.rFlag} reg="R_Flag" state={state} />
                <BitLamp name="Ready" desc="1=수용가능 / 0=분류·이동중" on={state.word.ready} reg="Ready" state={state} tone="ready" />
              </div>
            </div>
          </div>
        )}
      </CardContent>
    </Card>
  )
}

// 변경 하이라이트 훅 — flashSeq 증가 시 0.9s 플래시 클래스 부여(재트리거).
function useFlash(reg: RegKey, state: SorterWordState | undefined): boolean {
  const seq = state?.flashSeq[reg] ?? 0
  const prev = useRef(seq)
  const [flash, setFlash] = useState(false)
  useEffect(() => {
    if (seq !== prev.current) {
      prev.current = seq
      setFlash(true)
      const t = setTimeout(() => setFlash(false), 900)
      return () => clearTimeout(t)
    }
  }, [seq])
  return flash
}

function RegTile({
  addr,
  name,
  desc,
  value,
  reg,
  state,
}: {
  addr: string
  name: string
  desc: string
  value: number
  reg: RegKey
  state: SorterWordState
}) {
  const flash = useFlash(reg, state)
  const changedAt = state.changedAt[reg]
  return (
    <div className={cn('rounded-[14px] border border-line bg-panel p-3', flash && 'value-flash')}>
      <div className="flex items-center gap-1.5">
        <span className="rounded border border-line px-1.5 py-0.5 font-mono text-[10px] text-muted">{addr}</span>
        <span className="font-mono text-[12px] font-semibold text-ink">{name}</span>
      </div>
      <div className="mt-1.5 font-mono text-[24px] font-semibold leading-none tabular-nums text-ink">{value}</div>
      <div className="mt-1.5 text-[11px] text-muted">{desc}</div>
      <div className="mt-0.5 font-mono text-[10px] tabular-nums text-muted">
        {changedAt ? `변경 ${fmtTime(changedAt)}` : '변경 없음'}
      </div>
    </div>
  )
}

function BitLamp({
  name,
  desc,
  on,
  reg,
  state,
  tone = 'flag',
}: {
  name: string
  desc: string
  on: boolean
  reg: RegKey
  state: SorterWordState
  tone?: 'flag' | 'ready'
}) {
  const flash = useFlash(reg, state)
  const changedAt = state.changedAt[reg]
  const dotColor = on ? (tone === 'ready' ? 'bg-online text-online' : 'bg-accent text-accent') : 'bg-line'
  return (
    <div className={cn('flex items-center gap-2.5 rounded-[10px] border border-line bg-base px-2.5 py-2', flash && 'value-flash')}>
      <span className={cn('size-2.5 rounded-full', dotColor, on && 'lamp-live')} />
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-1.5">
          <span className="font-mono text-[12px] font-semibold text-ink">{name}</span>
          <span className="font-mono text-[11px] tabular-nums text-muted">= {on ? 1 : 0}</span>
        </div>
        <div className="truncate text-[10px] text-muted">{desc}</div>
        <div className="font-mono text-[10px] tabular-nums text-muted">
          {changedAt ? `변경 ${fmtTime(changedAt)}` : '변경 없음'}
        </div>
      </div>
    </div>
  )
}

function OnlineIndicator({ state }: { state: SorterWordState | undefined }) {
  const flash = useFlash('Online', state)
  const online = state?.word.online ?? false
  return (
    <span
      className={cn(
        'inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5 text-[11px] font-medium',
        online ? 'border-online/30 bg-online/10 text-online' : 'border-offline/30 bg-offline/10 text-offline',
        flash && 'value-flash',
      )}
    >
      <span className={cn('size-1.5 rounded-full', online ? 'bg-online text-online lamp-live' : 'bg-offline')} />
      {online ? '온라인' : '오프라인'}
    </span>
  )
}
