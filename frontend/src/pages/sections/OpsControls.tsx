import { useCallback, useId, useRef, useState, type ReactNode } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, ArrowUpToLine, Eraser, LayoutGrid, Pause, Play } from 'lucide-react'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import { Dialog } from '@/components/ui/dialog'
import { useToast } from '@/lib/toast'
import type { SorterStatus } from '@/lib/api'
import type { SorterWordState } from '@/lib/signalr'
import { ops, validateCellNo, validateFloor, validateSeq } from '@/lib/ops'
import { cn } from '@/lib/utils'

// ═══════════════════════════════════════════════════════════════════════════
// OpsControls — 소터 대상 운영 제어(S-F3b, F3a /api/ops/* 소비).
//   Pause/Resume(O2/O3) · SetTgtFloor(O4) · Clear-R(O5) · Cell-Assign(O6).
//
// ★ 안전: 모든 조작은 확인 다이얼로그(ui/dialog Dialog 재사용 — 포커스 트랩·body 잠금·Esc)를
//   거치며, 필수 작업자 이름 입력(공백이면 확인 비활성 = F3a 400 미러) + 규칙/위험 경고를 동반한다.
//   버튼 클릭은 백엔드 단일 쓰기 큐를 경유해 실 PLC를 움직인다(프론트는 HTTP만 호출).
//   결과(pingPongGuard·400·404·409·AlreadyInState)는 성공 위장 없이 정직히 토스트로 표면화한다.
// ═══════════════════════════════════════════════════════════════════════════

// 대기 중인 조작 — 확인 다이얼로그가 소비. run은 검증된 작업자 이름을 받아 실제 호출·표면화.
interface PendingOp {
  title: string
  confirmLabel: string
  danger: boolean
  summary: ReactNode // 대상·요청값 요약
  warning: ReactNode // 규칙 위반/위험 경고
  run: (operatorName: string) => Promise<void>
}

// 네이티브 입력 공통 스타일(신규 프리미티브 회피 — 기존 계기판 톤 준용).
const INPUT_CLS =
  'h-8 w-full rounded-lg border border-line bg-panel px-2.5 text-[13px] text-ink tabular-nums focus-visible:outline-2 focus-visible:outline-ink'

export function OpsControls({
  sorter,
  wordState,
  operatorName,
  setOperatorName,
}: {
  sorter: SorterStatus
  wordState: SorterWordState | undefined
  operatorName: string
  setOperatorName: (name: string) => void
}) {
  const { toast } = useToast()
  const qc = useQueryClient()

  const [floorInput, setFloorInput] = useState('')
  const [cellNoInput, setCellNoInput] = useState('')
  const [seqInput, setSeqInput] = useState('')
  const [pending, setPending] = useState<PendingOp | null>(null)
  const [busy, setBusy] = useState(false)

  const destId = sorter.destId
  const chuteLabel = `3DS #${String(sorter.chuteNo).padStart(2, '0')}`
  // 현재 TgtFloor(D6) — SetTgtFloor 사전 경고 근거(≠0이면 진행 중 → 핑퐁 차단 가능).
  const currentTgt = wordState?.word.tgtFloor ?? 0

  // #2 가시 라벨 ↔ 입력 연결용 id(useId — 인스턴스 고유, 중복 id 회피). 기존 aria-label은 유지.
  const floorId = useId()
  const cellNoId = useId()
  const seqId = useId()

  // #3-D 수동 워드 쓰기(O4/O6) Ready 게이트 — Ready==0(분류/이동 중)·OFFLINE이면 차단(사수 오조작 방지).
  //   wordState(SignalR 실시간) 우선, 부트스트랩 전이면 sorter(SorterStatus 쿼리)로 폴백.
  //   O5 Clear-R은 복구 도구라 게이트하지 않는다(계약 Q1). 최종 권위는 백엔드 409 사전점검(3-B).
  const sorterOnline = wordState?.word.online ?? sorter.online
  const sorterReady = wordState?.word.ready ?? sorter.ready
  const writeBlocked = !sorterOnline || !sorterReady
  const writeBlockReason = !sorterOnline
    ? '소터 OFFLINE — 수동 쓰기 불가'
    : !sorterReady
      ? 'Ready 아님(분류/이동 중) — 수동 쓰기 차단'
      : ''

  // busy 중 Esc/백드롭 닫기 무시(진행 중 조작 보호) — 안정 onClose로 Dialog effect churn 방지.
  const busyRef = useRef(busy)
  busyRef.current = busy
  const closePending = useCallback(() => {
    if (!busyRef.current) setPending(null)
  }, [])

  const invalidate = useCallback(
    (keys: string[]) => {
      for (const k of keys) qc.invalidateQueries({ queryKey: [k] })
    },
    [qc],
  )

  // ── O2/O3 Pause / Resume ────────────────────────────────────────────────
  function requestPauseToggle() {
    const resume = sorter.paused
    setPending({
      title: resume ? '목적지 재개' : '목적지 일시정지',
      confirmLabel: resume ? '재개' : '일시정지',
      danger: !resume,
      summary: (
        <>
          대상 <b>{chuteLabel}</b> · 현재 상태 <b>{sorter.paused ? '일시정지됨' : '운영 중'}</b>
        </>
      ),
      warning: resume
        ? '이 목적지로의 투입을 다시 허용합니다.'
        : '이 목적지로의 신규 투입 지시가 중단됩니다(진행 중 작업은 유지).',
      run: async (op) => {
        const r = resume ? await ops.resume(destId, op) : await ops.pause(destId, op)
        if (!r.ok) {
          // 409 동시 전이 충돌은 경고 톤(재시도 안내), 그 외(404 등)는 오류.
          toast(r.status === 409 ? 'warning' : 'error', `${resume ? '재개' : '일시정지'} 실패 — ${r.message}`)
          return
        }
        if (r.data.outcome === 'AlreadyInState') {
          toast('info', `이미 ${resume ? '운영 중' : '정지'} 상태입니다(변경 없음).`)
        } else {
          toast('success', resume ? '재개되었습니다.' : '일시정지되었습니다.')
        }
        invalidate(['sorters', 'cells'])
      },
    })
  }

  // ── O4 SetTgtFloor ──────────────────────────────────────────────────────
  function requestSetTgtFloor() {
    // #3-D: not-Ready/OFFLINE이면 FE에서 선차단(다이얼로그 미개시). 백엔드 409가 최종 권위.
    if (writeBlocked) {
      toast('warning', `목표층 설정 차단 — ${writeBlockReason}.`)
      return
    }
    const floor = Number(floorInput)
    if (floorInput.trim() === '') {
      toast('warning', '목표층을 입력하세요.')
      return
    }
    const err = validateFloor(floor)
    if (err) {
      toast('warning', err)
      return
    }
    setPending({
      title: '목표층 설정 (SetTgtFloor)',
      confirmLabel: '설정',
      danger: currentTgt !== 0,
      summary: (
        <>
          대상 <b>{chuteLabel}</b> · 목표층 <b>{floor}</b>층 · 현재 TgtFloor(D6){' '}
          <b className="tabular-nums">{currentTgt}</b>
        </>
      ),
      warning:
        currentTgt !== 0 ? (
          <span className="text-warn">
            ⚠ 현재 TgtFloor={currentTgt} (진행 중) — 이 쓰기는 컨슈머의 핑퐁 차단(TgtFloor==0 가드)으로
            스킵될 수 있습니다.
          </span>
        ) : (
          '정지·비분류 상태에서 목표층이 반영됩니다(컨슈머가 TgtFloor==0 재확인 후 기입).'
        ),
      run: async (op) => {
        const r = await ops.setTgtFloor(destId, floor, op)
        if (!r.ok) {
          // 400(범위)·409(Ready 아님)는 경고 톤(오조작 안내), 그 외(404·500·네트워크)는 오류. 성공 위장 0.
          toast(r.status === 400 || r.status === 409 ? 'warning' : 'error', `목표층 설정 실패 — ${r.message}`)
          return
        }
        // 정직 표면화: pingPongGuard면 성공으로 위장하지 않고 스킵 가능성을 경고한다.
        if (r.data.pingPongGuard) {
          toast(
            'warning',
            `큐 수락됨 — 진행 중(현재 TgtFloor=${r.data.currentTgtFloor})이라 컨슈머가 이 쓰기를 스킵할 수 있습니다.`,
          )
        } else {
          toast('success', `목표층 ${r.data.floor}층 설정 큐 수락됨.`)
        }
        invalidate(['sorters'])
      },
    })
  }

  // ── O5 Clear-R (진단) ───────────────────────────────────────────────────
  function requestClearR() {
    setPending({
      title: 'R 영역 강제 클리어 (Clear-R)',
      confirmLabel: 'R 클리어',
      danger: true,
      summary: (
        <>
          대상 <b>{chuteLabel}</b> · R_Flag / R 영역(D2·D3) 강제 클리어
        </>
      ),
      warning: (
        <span className="text-offline">
          ⚠ 진단 전용 — 진행 중 핸드셰이크(C/R) 상태가 오염될 수 있습니다. 정상 운영 중에는 사용하지
          마세요.
        </span>
      ),
      run: async (op) => {
        const r = await ops.clearR(destId, op)
        if (!r.ok) {
          toast('error', `R 클리어 실패 — ${r.message}`)
          return
        }
        toast('success', 'R 영역 클리어 큐 수락됨.')
        invalidate(['cells'])
      },
    })
  }

  // ── O6 Cell-Assign (고위험 진단) ────────────────────────────────────────
  function requestCellAssign() {
    // #3-D: not-Ready/OFFLINE이면 FE에서 선차단(다이얼로그 미개시). 백엔드 409가 최종 권위.
    if (writeBlocked) {
      toast('warning', `셀 지정 차단 — ${writeBlockReason}.`)
      return
    }
    const cellNo = Number(cellNoInput)
    const seq = Number(seqInput)
    if (cellNoInput.trim() === '' || seqInput.trim() === '') {
      toast('warning', '셀 번호와 명령 순번을 입력하세요.')
      return
    }
    const cellErr = validateCellNo(cellNo)
    if (cellErr) {
      toast('warning', cellErr)
      return
    }
    const seqErr = validateSeq(seq)
    if (seqErr) {
      toast('warning', seqErr)
      return
    }
    setPending({
      title: '셀 수동 지정 (Cell-Assign)',
      confirmLabel: '셀 지정',
      danger: true,
      summary: (
        <>
          대상 <b>{chuteLabel}</b> · 셀 <b>{cellNo}</b> · 명령 순번 <b>{seq}</b>
        </>
      ),
      warning: (
        <span className="text-offline">
          ⚠ 고위험 진단 — 정상 셀 지정은 IF-10 핸드셰이크가 수행합니다. 수동 지정은 셀 회계·핸드셰이크와
          경합할 수 있습니다. 관리자 확인 후에만 사용하세요.
        </span>
      ),
      run: async (op) => {
        const r = await ops.cellAssign(destId, cellNo, seq, op)
        if (!r.ok) {
          // 400(범위)·409(Ready 아님)는 경고 톤, 그 외는 오류. 성공 위장 0.
          toast(r.status === 400 || r.status === 409 ? 'warning' : 'error', `셀 지정 실패 — ${r.message}`)
          return
        }
        // 정직 표면화: cFlagGuard면 성공으로 위장하지 않고 스킵 가능성을 경고(O4 pingPongGuard 미러).
        if (r.data.cFlagGuard) {
          toast(
            'warning',
            `큐 수락됨 — 이미 C_Flag=1(진행 중)이라 컨슈머가 이 쓰기를 스킵할 수 있습니다.`,
          )
        } else {
          toast('success', `셀 지정 큐 수락됨(셀 ${cellNo} · 순번 ${seq}).`)
        }
        invalidate(['cells'])
      },
    })
  }

  // ── 확인 실행 ─────────────────────────────────────────────────────────────
  async function onConfirm() {
    if (!pending) return
    const op = operatorName.trim()
    if (op.length === 0) return // 이중 가드 — 버튼은 비활성이지만 방어적으로 차단.
    setBusy(true)
    try {
      await pending.run(op)
    } catch (e) {
      toast('error', `작업 실패 — ${(e as Error).message}`)
    } finally {
      setBusy(false)
      setPending(null)
    }
  }

  const operatorBlank = operatorName.trim().length === 0

  return (
    <Card>
      <CardHeader>
        <CardTitle>운영 제어</CardTitle>
        <Badge tone="warn">실 하드웨어 동작</Badge>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        {/* O2/O3 Pause/Resume */}
        <ControlRow
          title="일시정지 / 재개"
          desc="이 목적지로의 신규 투입 허용 여부(순수 WCS 게이트 · PLC 쓰기 없음)."
        >
          <div className="flex items-center gap-2">
            <Badge tone={sorter.paused ? 'warn' : 'online'}>
              {sorter.paused ? '일시정지됨' : '운영 중'}
            </Badge>
            <Button
              variant="outline"
              size="sm"
              onClick={requestPauseToggle}
              className={sorter.paused ? undefined : 'border-warn/50 text-warn hover:bg-warn/5'}
            >
              {sorter.paused ? <Play className="size-4" /> : <Pause className="size-4" />}
              {sorter.paused ? '재개' : '일시정지'}
            </Button>
          </div>
        </ControlRow>

        {/* O4 SetTgtFloor */}
        <ControlRow
          title="목표층 설정 (SetTgtFloor · D6)"
          desc={`허용 범위 1~20. 현재 TgtFloor=${currentTgt}${currentTgt !== 0 ? ' (진행 중 — 핑퐁 차단될 수 있음)' : ''}.`}
        >
          <div className="flex flex-wrap items-center gap-2">
            <label htmlFor={floorId} className="text-[12px] font-medium text-ink">
              층
            </label>
            <input
              id={floorId}
              type="number"
              min={1}
              max={20}
              inputMode="numeric"
              value={floorInput}
              onChange={(e) => setFloorInput(e.target.value)}
              placeholder="층"
              aria-label="목표층"
              className={cn(INPUT_CLS, 'w-24')}
            />
            <Button
              variant="outline"
              size="sm"
              onClick={requestSetTgtFloor}
              disabled={writeBlocked}
              title={writeBlocked ? writeBlockReason : undefined}
            >
              <ArrowUpToLine className="size-4" />
              설정
            </Button>
            {writeBlocked && <span className="w-full text-[11px] text-warn">⚠ {writeBlockReason}</span>}
          </div>
        </ControlRow>

        {/* O5 Clear-R */}
        <ControlRow
          title="R 영역 클리어 (Clear-R)"
          desc="R_Flag/R 영역 강제 클리어 — 진단 전용. 핸드셰이크 오염 위험."
        >
          <Button
            variant="outline"
            size="sm"
            onClick={requestClearR}
            className="border-offline/50 text-offline hover:bg-offline/5"
          >
            <Eraser className="size-4" />
            R 클리어
          </Button>
        </ControlRow>

        {/* O6 Cell-Assign */}
        <ControlRow
          title="셀 수동 지정 (Cell-Assign)"
          desc="고위험 진단 — 셀 1~1000 · 순번 1~30000. 핸드셰이크·셀 회계와 경합 가능."
        >
          <div className="flex flex-wrap items-center gap-2">
            <label htmlFor={cellNoId} className="text-[12px] font-medium text-ink">
              셀 번호
            </label>
            <input
              id={cellNoId}
              type="number"
              min={1}
              max={1000}
              inputMode="numeric"
              value={cellNoInput}
              onChange={(e) => setCellNoInput(e.target.value)}
              placeholder="셀 번호"
              aria-label="셀 번호"
              className={cn(INPUT_CLS, 'w-28')}
            />
            <label htmlFor={seqId} className="text-[12px] font-medium text-ink">
              명령 순번
            </label>
            <input
              id={seqId}
              type="number"
              min={1}
              max={30000}
              inputMode="numeric"
              value={seqInput}
              onChange={(e) => setSeqInput(e.target.value)}
              placeholder="명령 순번"
              aria-label="명령 순번"
              className={cn(INPUT_CLS, 'w-28')}
            />
            <Button
              variant="outline"
              size="sm"
              onClick={requestCellAssign}
              disabled={writeBlocked}
              title={writeBlocked ? writeBlockReason : undefined}
              className="border-offline/50 text-offline hover:bg-offline/5"
            >
              <LayoutGrid className="size-4" />
              셀 지정
            </Button>
            {writeBlocked && <span className="w-full text-[11px] text-warn">⚠ {writeBlockReason}</span>}
          </div>
        </ControlRow>
      </CardContent>

      {/* 확인 다이얼로그 — Dialog 재사용(포커스 트랩·Esc·백드롭). 작업자 이름 필수 입력 게이트. */}
      <Dialog open={pending !== null} onClose={closePending} labelledBy="ops-confirm-title">
        <div className="flex items-start gap-3 px-5 pt-5">
          {pending?.danger && (
            <span className="mt-0.5 flex size-9 shrink-0 items-center justify-center rounded-full bg-offline/10 text-offline">
              <AlertTriangle className="size-5" />
            </span>
          )}
          <div className="min-w-0 flex-1">
            <h2 id="ops-confirm-title" className="text-[15px] font-semibold leading-tight text-ink">
              {pending?.title}
            </h2>
            <div className="mt-1.5 text-[13px] leading-relaxed text-muted">{pending?.summary}</div>
            <div className="mt-2 text-[13px] leading-relaxed text-muted">{pending?.warning}</div>

            {/* 작업자 이름 — 세션 기억값 프리필(수정 가능), 공백이면 확인 비활성(F3a 400 미러). */}
            <label className="mt-3 block text-[12px] font-medium text-ink">
              작업자 이름 <span className="text-offline">*</span>
              <input
                type="text"
                value={operatorName}
                onChange={(e) => setOperatorName(e.target.value)}
                placeholder="예: 홍길동"
                aria-label="작업자 이름"
                maxLength={100}
                className={cn(INPUT_CLS, 'mt-1 font-normal')}
              />
            </label>
            {operatorBlank && (
              <p className="mt-1 text-[11px] text-offline">
                작업자 이름은 필수입니다(감사 귀속) — 입력해야 확인할 수 있습니다.
              </p>
            )}
          </div>
        </div>
        <div className="flex justify-end gap-2 px-5 py-4">
          <Button variant="outline" size="sm" onClick={closePending} disabled={busy}>
            취소
          </Button>
          <Button
            variant="solid"
            size="sm"
            onClick={onConfirm}
            disabled={busy || operatorBlank}
            className={cn(
              pending?.danger &&
                'border-transparent bg-offline text-white hover:bg-offline/90 disabled:bg-offline/40',
            )}
          >
            {busy ? '처리 중…' : (pending?.confirmLabel ?? '확인')}
          </Button>
        </div>
      </Dialog>
    </Card>
  )
}

// 개별 제어 행 — 제목·설명(좌) + 액션(우). 밀집 운영툴 톤 일관.
function ControlRow({
  title,
  desc,
  children,
}: {
  title: string
  desc: string
  children: ReactNode
}) {
  return (
    <div className="flex flex-wrap items-center justify-between gap-3 rounded-[12px] border border-line bg-base px-3.5 py-3">
      <div className="min-w-0">
        <div className="text-[13px] font-semibold text-ink">{title}</div>
        <div className="text-[11px] text-muted">{desc}</div>
      </div>
      <div className="shrink-0">{children}</div>
    </div>
  )
}
