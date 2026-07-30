[Sprint Contract] — S-TWO-FLOOR-WRITE-ON-CLEAR

──────────────────────────────────────────────────────────────────────────────
Goal
──────────────────────────────────────────────────────────────────────────────
Make WCS write the pending-floor to TgtFloor(D6) the instant it observes the PLC
clear it (TgtFloor 1→0 at sort-start), so the 3DS carriage never sits at
TgtFloor==0. Per the vendor-confirmed physical model (2026-07-29), TgtFloor==0 is
an ACTIVE "go to default (middle) floor" command, not "hold" — leaving it 0 makes
the carriage drift to the default floor and then round-trip back to the target.
The current observe loop (a) only writes while Ready==1 and (b) skips when
CurFloor==head, so at the clear moment (Ready==0) it writes nothing until the
sorter is idle again — producing the drift+round-trip. This sprint retargets the
write trigger and the pop trigger to the clear edge, writes even while busy and
even at the same floor, and preserves every absolute rule (#1/#2/#3/#7/#8).

This is safety-critical: a wrong floor write or an early pop causes misclassification
or machine damage. Zero behavioral regression is a completion condition.

──────────────────────────────────────────────────────────────────────────────
Background the Generator MUST internalize before touching code
──────────────────────────────────────────────────────────────────────────────
B1. Absolute rules (CLAUDE.md §절대규칙, re-read before Phase 2). Unchanged and
    binding: #1 (all TgtFloor writes go ONLY through the sorter's single write
    queue via bundle.EnqueueSetTgtFloorAsync — never direct Modbus), #2 (write
    TgtFloor ONLY when TgtFloor==0; NEVER overwrite a non-zero TgtFloor — the
    ping-pong guard; the fresh-read `D6!=0` skip in PlcGateway.ProcessWriteAsync
    SetTgtFloor case stays the dedup mechanism), #3 (WCS NEVER writes 0 to
    TgtFloor; the PLC owns the clear), #7 (all periods/thresholds from
    appsettings — ObserveIntervalMs, StallSuspectTicks), #8 (decision logic is a
    pure Wcs.Core function; the observe loop only does I/O + state).

B2. Vendor physical model (confirmed 2026-07-29):
    - PLC clears TgtFloor→0 ONLY at sort-start (Ready 1→0). On arrival it writes
      CurFloor and KEEPS TgtFloor. So a polled TgtFloor 1→0 transition is an
      unambiguous sort-start signal (WCS is the only writer of non-zero; WCS never
      writes 0). [Re-affirm with vendor — see Open Question 3.]
    - Writing TgtFloor while the sorter is busy (Ready==0, mid-sort) is SAFE: the
      PLC finishes the in-progress sort, then moves to the written floor. The
      existing Sim3ds already models this (SimSlave.RunSortSequenceAsync re-reads
      TgtFloor after the sort and moves if it differs) — so the honored-write path
      is exercisable end-to-end WITHOUT changing the Sim. Sim3ds stays UNCHANGED
      this sprint. (Note: the Sim models TgtFloor==0 as "stay", NOT as
      drift-to-default; therefore drift-prevention is verified at the WCS
      write-timing level, not via Sim drift — see Verification Scenarios.)

B3. Why the OLD cycle-detection pop existed and why the NEW trigger must not
    regress it (I-1 early-pop bug — MANDATORY understanding):
    The original design popped speculatively when "head floor == CurFloor" →
    immediately drained the queue for a floor before those pieces were ever
    deposited, so in [A,A,B] the sorter left floor A before the 2nd A-AGV
    deposited (2nd A-AGV stranded). The I-1 fix moved pop to an ACTUAL sort-cycle
    completion (Ready 1→0→1 in-place). The NEW pop trigger (TgtFloor 1→0 clear)
    is likewise gated on a REAL physical event — the PLC only clears when it
    begins sorting a genuinely-deposited piece (deposit → C_Flag → sort). So each
    clear == exactly one real piece consumed == one legitimate pop. It is NOT
    speculative (not a floor-equality guess). The contract REQUIRES the Generator
    to preserve the [A,A,B] invariant (K3): the sorter holds floor A until BOTH A
    pieces have actually started sorting (two clears → two pops), and only then is
    B the head.

──────────────────────────────────────────────────────────────────────────────
Implementation Scope (WHAT — the Generator decides HOW)
──────────────────────────────────────────────────────────────────────────────
S1. Retarget the WRITE trigger in SorterFloorReturnService.ObserveSorter:
    - New rule: whenever the observed snapshot has TgtFloor==0 AND the sorter's
      pending-floor queue is non-empty AND Online AND not Paused → write the
      current queue HEAD floor to TgtFloor (via the single write queue, rule #1).
    - This MUST fire even when Ready==0 (write-during-busy) and even when
      CurFloor==head (write same floor to hold position and prevent drift). Remove
      the two current guards that block this: the `!ready` early-return and the
      `snap.CurFloor == f` skip (SorterFloorReturnService.cs ~line 282).
    - Paused/Offline → no write (Paused via low-cost IsPaused, Online via the
      existing snap.Online early-return). FULL is NOT a block for the physical
      alignment write (큐 피스는 IF-05 수용 확정분 — FULL blocks only at IF-05
      dispatch; unchanged from B/Q5 decision).
    - The write value is supplied by the QUEUE (head), not by any sort-completion
      event. Rule #2 is preserved structurally: WCS still only writes when
      TgtFloor==0; it never overwrites a non-zero TgtFloor; the write-consumer
      fresh-read `D6!=0` skip remains the dedup so re-asserting the same head on
      every 0-tick is self-limiting (idempotent) and does not spam.

S2. Retarget the POP trigger:
    - New rule: pop exactly ONE head on the TgtFloor 1→0 clear edge (sort-start),
      NOT on the Ready 1→0→1 in-place cycle.
    - The popped head is the piece whose sort just started (== CurFloor at clear
      time). After popping, the NEW head becomes the write target for S1 in the
      same observe tick (this is the write-during-busy of the NEXT piece's floor —
      see Open Question 1 for the exact "which floor" resolution).
    - Deterministic edge detection (see Open Question 3): track the previous
      observed TgtFloor per sorter in ObserveState; pop only on a non-zero→zero
      transition. Establish the baseline on the first observation and re-sync it
      on OFFLINE (mirror the existing PrevReady handling) so that (a) the
      post-StartupClear initial TgtFloor==0 is NOT mistaken for a clear edge (no
      spurious pop) and (b) OFFLINE recovery does not fabricate an edge. The pop
      is missed-edge-safe by construction: WCS is the only writer of non-zero and
      only writes AFTER observing the 0, so the 0 state persists until WCS reacts.

S3. Adjust the safety-critical observability to the new triggers (meaning preserved):
    - Trace event 2 (EventNo=2, "TGTFLOOR_DEQUEUE"): still emitted exactly once per
      pop (one per real piece consumed). Fire it on the new clear-edge pop; update
      the Trigger/Detail fields to reflect the clear-edge origin (event NUMBER,
      NAME, and one-per-piece semantics are preserved — the trace viewer keeps
      working; only the wall-clock timing shifts earlier to sort-start).
    - DetectStall (SORTER_STALL_SUSPECT WARN + operation_log, once-per-episode,
      re-arming, observe-only, no corrective action): re-derive its condition for
      the new model. Under the new write model a non-empty queue no longer sits at
      TgtFloor==0 (WCS writes head immediately), so the OLD condition
      (idle ∧ TgtFloor==0 ∧ head present) can no longer detect a genuine
      under-pop stall. The re-derived condition MUST still fire once-per-episode
      for a real under-pop stall (head present but not being consumed — e.g. AGV
      abandonment: aligned/held at head, no deposit, head unchanged for
      StallSuspectTicks) and MUST keep ZERO false positives on the legitimate
      states already covered (empty queue, normal cycling, offline, paused,
      detector disabled). Keep it observe-only (no writes/pops/re-dispatch — that
      is Sub-Sprint D scope) and off the pure core.

S4. Pure decision function (Wcs.Core.DepositDecider) — see Open Question 4:
    The write-decision must yield "write = head" for TgtFloor==0 in ALL non-hold,
    online cases, INCLUDING the aligned-idle case (Ready==1 && CurFloor==head &&
    TgtFloor==0) and the busy case (Ready==0 && TgtFloor==0). Recommended: keep the
    logic in the pure function (rule #8). HARD CONSTRAINT: the push-facing outputs
    consumed by DestinationStatusService.ComputeSorter and DestinationStatusPusher
    — namely DepositDecision.Ready and DepositDecision.Reason — MUST remain
    byte-identical (those callers pass floor=snap.CurFloor and read ONLY .Ready /
    .Reason; only .WriteTgtFloor / .TgtFloorValue may change). Enumerate and update
    the unit tests that encode the superseded "aligned = no write" contract (see
    Completion Conditions test list).

S5. Out of scope / no change (confirm untouched):
    - PlcGateway.cs: the clear is observed via existing poll snapshots; the
      SetTgtFloor fresh-read guard stays. No gateway change.
    - RcsController IF-05 enqueue + trace event 1: unchanged (still enqueues each
      sorter piece's floor in FIFO order).
    - PendingFloorQueueRestorer: unchanged. VERIFY the restart interplay: after
      StartupClear (D6=0) + restore, the observe loop's first observation
      establishes the TgtFloor baseline at 0 and does NOT spuriously pop; it then
      writes the restored head normally.
    - SorterGatewayRegistry / SorterBundleHandle: no new hook — the write path uses
      the existing EnqueueSetTgtFloorAsync. Sim3ds: unchanged.
    - Absolutely NO WCS write of 0 to TgtFloor (rule #3). NO overwrite of non-zero
      TgtFloor (rule #2).

──────────────────────────────────────────────────────────────────────────────
Open Questions — ✅ 사용자 게이트 확정(2026-07-29): OQ1=다음 피스 층(pop 후 새 head) 기입 ·
OQ2=빈 큐 시 TgtFloor 0 유지(디폴트층 park) · OQ3=PLC는 분류 시작 때만 클리어(도착 시 유지, 1→0 에지=
분류시작 신호로 안전). 아래 권장 기본값 그대로 확정 — Generator는 이 확정대로 구현.
(원문 이력)
──────────────────────────────────────────────────────────────────────────────
OQ1 ★ WHICH floor to write at the clear moment.
    Queue semantics (verified from code): IF-05 enqueues each sorter piece's floor
    in FIFO order; the head is the oldest not-yet-consumed piece; at a clear, the
    piece being sorted == head (the sorter was aligned to head to receive it, so
    head == CurFloor at clear).
    RESOLUTION (recommended default): pop the head, then write the NEW head (the
    NEXT piece's floor). Rationale traced against [A],[1,2],[1,1,2]:
      • [1,1,2] @floor1: A1 clear → pop first 1 → new head 1 → write 1 (hold at 1
        for A2). A2 clear → pop second 1 → new head 2 → write 2 (move to 2 after A2
        sort — write-during-busy). B clear → pop 2 → empty. Matches K3 intent.
      • Writing the POPPED head instead (= CurFloor) would only be correct for
        same-floor-consecutive and DEADLOCKS on a floor change: the held non-zero
        TgtFloor blocks the next differently-floored write (rule #2) and no further
        clear arrives to release it. So "write new head" is the only FIFO-correct
        choice, and it IS the vendor-confirmed write-during-busy (commit the sorter
        to the next floor while the current piece finishes).
    CONFIRM with user: that "그 층" means the post-pop new head (next piece), not
    the popped floor. (Physical analysis is decisive; confirmation is a safety
    checkpoint given the ambiguous phrasing.)

OQ2 Empty queue at the clear moment (the just-sorted piece was the last).
    RESOLUTION (recommended default): write NOTHING — leave TgtFloor==0. Rationale:
    (a) no pending work → no round-trip waste, so drift-to-default is harmless
    "park at default"; (b) it PRESERVES the "TgtFloor==0 → write head" trigger for
    the next enqueued piece. The alternative "write CurFloor to hold" would set
    TgtFloor non-zero and then DEADLOCK a subsequent differently-floored piece
    (rule #2 forbids overwriting non-zero; rule #3 forbids WCS writing 0). K2
    already asserts `TgtFloor:0, Ready:true` between sequential pieces — this
    default keeps K2 green.
    CONFIRM with user: (i) that the "default (middle) floor" is a SAFE park and
    drift-on-empty-queue is operationally acceptable; (ii) if NOT acceptable,
    holding position requires relaxing rule #2 or introducing an operator/WCS
    re-target path — a larger design change that must escalate to re-planning.

OQ3 Determinism of catching the clear (poll-snapshot based).
    RESOLUTION (contract requirement, not really optional): track prevTgtFloor per
    sorter; pop on non-zero→0 edge; baseline on first obs; re-sync on OFFLINE;
    never pop the post-StartupClear initial 0. Missed-edge-safe by construction (0
    persists until WCS reacts; poll samples the seconds-long window many times at
    150ms).
    CONFIRM with user/vendor: that the PLC clears TgtFloor→0 ONLY at sort-start and
    at no other time (arrival keeps it) — this is the one assumption the edge
    detector depends on. Already stated in SPEC §6 / rule #3; re-affirm because it
    is safety-critical.

OQ4 DepositDecider role. RESOLUTION: yes, the pure function needs the aligned-idle
    case (Ready==1 && CurFloor==head && TgtFloor==0) to return write=head; the
    busy case (Ready==0 && TgtFloor==0) already returns write. Push-facing .Ready /
    .Reason stay identical (constraint S4). Test-update range is enumerated in
    Completion Conditions. (No user decision needed — documented for transparency
    since the user asked for the update scope.)

──────────────────────────────────────────────────────────────────────────────
Evaluation Criteria (Backend/API weights per workflow-agents.md)
──────────────────────────────────────────────────────────────────────────────
1. Functionality / Data integrity (★★★): write-on-clear fires at the 1→0 edge even
   when Ready==0 and CurFloor==head; exactly one pop per real piece (no early pop,
   no double pop, no under-pop); FIFO order preserved; drift/round-trip eliminated;
   all absolute rules (#1/#2/#3/#7/#8) provably intact (no direct Modbus, no
   overwrite of non-zero TgtFloor, no WCS write of 0).
2. Architecture / intentional design (★★★): decision stays pure (Wcs.Core); the
   observe loop stays thin I/O + state; push-facing outputs untouched; change
   localized to the observe loop + the pure write-decision; no new plumbing.
3. Craft (★★): deterministic edge detection (baseline/OFFLINE re-sync, no spurious
   pop); trace event 2 and stall detector correctly retargeted with meaning
   preserved; no write spam (fresh-read dedup); exception isolation per sorter
   preserved; config-driven (rule #7).
4. Regression safety (★★): the FULL existing suite is green, with only the
   explicitly-listed unit tests updated (and their INTENT — fire-once, FIFO,
   no-false-positive, push readiness — preserved), plus new deterministic tests
   for the new scenarios.

──────────────────────────────────────────────────────────────────────────────
Completion Conditions (minimum bar for Evaluator PASS)
──────────────────────────────────────────────────────────────────────────────
C1. `dotnet test backend/Wcs.sln` fully green (Evaluator re-runs from scratch,
    independent of Generator's report).
C2. Behavioral two-floor suites GREEN with NO weakening of their invariants:
    E2EGroupK_TwoFloorReturnTests (K1 single-floor return, K2 FIFO 1-2-1
    one-at-a-time, K3 [A,A,B] hold-until-both-classified), E2EGroupL/M
    (host push / cold-start), TwoFloorHostRoutingTests, SorterPushOperationalTests,
    RcsPushTests, ChuteStatePushTests. K3's [A,A,B] hold invariant is the primary
    non-regression guard for I-1.
C3. Updated unit tests reflect the NEW contract with intent preserved:
    - DepositDeciderTests: Row1_Ready_AtOperationalFloor_IsReady and
      FloorParam_F1_AtFloor1_IsReady flip WriteTgtFloor false→true (TgtFloor==0
      aligned now writes), with .Ready still true and .Reason still None. Cases
      with non-zero TgtFloor (C1_Row1_TgtFloorResidual_StillReady,
      Row3/Row5/FloorParam ping-pong) stay write=false (rule #2). Audit all
      DepositDecider.Decide call-sites in ScenarioTests for the same shift.
    - SorterStallDetectorTests: re-target the stall condition to the new model
      (the CC1.2/1.3 fixtures that set TgtFloor==0 + aligned head and expect a fire
      encode the OLD condition; update them to the abandonment signature — head
      present + held/aligned + no pop for N ticks). Preserve: once-per-episode,
      re-arm, observe-only (D6 unchanged, no pop), zero false positives on empty /
      cycling / offline / paused / disabled, cross-layer operation_log persistence.
C4. New deterministic tests (observe-loop level, using FakeModbusMasterForApi so
    timing is controllable — same harness as SorterStallDetectorTests.BuildService):
    - write-on-clear: at a TgtFloor 1→0 edge while Ready==0, a SetTgtFloor(new head)
      is enqueued (write-during-busy) — assert the D6 write happens during the
      Ready==0 window.
    - same-floor hold: with new head == CurFloor, WCS still writes that floor
      (CurFloor==head no longer skipped) — the drift-prevention assertion.
    - one-pop-per-clear: a single 1→0 edge pops exactly one head and emits exactly
      one trace event 2; steady TgtFloor==0 (no new edge) does not re-pop.
    - empty-queue: at a clear that empties the queue, no write is enqueued and
      TgtFloor stays 0 (OQ2 default); a subsequently enqueued differently-floored
      piece is then written on the next TgtFloor==0 observation (next-piece
      recovery).
    - no missed / no spurious edge: baseline-on-first-obs and OFFLINE re-sync
      produce no spurious pop (esp. post-StartupClear initial 0).
C5. Rule proofs in evidence: Sim/Fake timeline shows WCS writes to D6 are only
    1/2 (never →0); no direct-Modbus write path added; no non-zero TgtFloor
    overwrite (fresh-read skip logged when applicable).
C6. Static checks: run the project's build/analyzers/formatter (check mode); record
    pass/fail/not-configured in sprint-feedback.md.

──────────────────────────────────────────────────────────────────────────────
Parallel Modules: N/A (single module — the change is localized to
SorterFloorReturnService + the pure DepositDecider write-decision + their tests;
no boundary-clean partition exists and the pieces are causally coupled).

Evaluation Dimensions: functional only (regression-safety is folded into the
functional re-run per C1–C5; safety-criticality raises the bar, not the dimension
count).

──────────────────────────────────────────────────────────────────────────────
Detected Project Type: Full-stack
  (Repo signal: browser-facing entry point `frontend/index.html` + Vite/TS client
  tree AND server-side controllers `backend/src/Wcs.Api/Controllers` with an
  ASP.NET Core host. This sprint's SURFACE is backend-only — gateway/decision
  timing — with a display-only consequence on the frontend TraceLogPage, where
  trace event 2 now renders at the earlier clear-edge timing. No frontend code
  changes.)

──────────────────────────────────────────────────────────────────────────────
Verification Scenarios (per-type, mandatory)
──────────────────────────────────────────────────────────────────────────────
=== Applicable Web/UI scenarios (frontend surface this sprint touches — display-only) ===
- Default state — TraceLogPage (`frontend/src/pages/TraceLogPage.tsx`): navigate to
  the trace log view; the event list renders with no console errors/pageerror; the
  legend/columns for EventNo/Event are intact (no shape change to event 2).
- Alternate state introduced by this sprint — event 2 timing: after driving a
  closed-loop scenario (below), the viewer shows a TGTFLOOR_DEQUEUE (EventNo 2)
  entry emitted at the sort-start/clear moment rather than at sort-completion;
  event 2 still appears exactly once per consumed piece, correlated to its sorter
  + floor. (Browser verification via Playwright MCP: navigate → run loop → screenshot
  the trace list → READ it; capture console.log; URL from
  frontend `.claude/ports.local.json`.)

=== Applicable Backend/API scenarios (the real surface — automated test code, not curl) ===
- Endpoints touched (no signature change — behavior/timing reached through them):
    IF-05  POST /api/v1/destination-query  (enqueues sorter piece floor — unchanged)
    IF-09  POST /api/v1/arrival-report      (arrival record — drives the loop)
    IF-10  POST /api/v1/deposit-report      (deposit → C_Flag → PLC sort-start →
                                             TgtFloor clear — the trigger under test)
    Trace read endpoint backing TraceLogPage (GET — event 2 timing surfaced)
- Happy path:
    IF-05 sorter piece (mapped inductionNo) → 200 {result:"OK"} + queue enqueue F.
    IF-10 on a deposited piece at the aligned floor → 200 {result:"OK"} → handshake
    → PLC clears TgtFloor → WCS observes 1→0 → pops head → writes new head
    (write-during-busy) → Sim finishes sort then moves/holds → CurFloor tracks the
    written floor. Assert: exactly one D6 write per piece, values ∈ {1,2}, never →0.
- Relevant error / edge cases (Planner-selected — not padded):
    • Empty-queue clear (OQ2): no D6 write, TgtFloor stays 0, next enqueued
      differently-floored piece is served on the next TgtFloor==0 observation.
    • Paused sorter: at a clear, NO write (DepositDecider Paused block) — still no
      write while busy.
    • Offline sorter: snapshot distrusted → no pop, no write, PrevReady/prevTgtFloor
      re-synced (no spurious edge on recovery).
    • Unmapped inductionNo (unchanged): IF-05 NG + IF05_NO_FLOOR (regression guard —
      no behavioral change expected).

=== End-to-end data-flow scenario(s) crossing layers (HTTP ↔ in-memory queue ↔ Modbus
    gateway ↔ real Sim3ds ↔ DB/trace) ===
- Write-on-clear / no round-trip: IF-05→(align)→IF-09→IF-10→PLC sort-start clears
  TgtFloor→WCS writes the (new head or same head) floor DURING Ready==0→Sim honors
  the write after the sort→CurFloor lands on the target with NO intermediate write
  of 0 and NO drift-then-return. (Drift-prevention is asserted at the WCS write
  timing: a D6 write is emitted at the clear while Ready==0, including when
  CurFloor==head — since the current Sim models TgtFloor==0 as "stay", the WCS-side
  write-timing assertion is the definitive drift-prevention proof.)
- [A,A,B] multi-AGV hold (K3, primary I-1 guard): enqueue [1,1,2] at floor 1; after
  A1's sort the sorter HOLDS floor 1 and the queue is [1,2] (exactly one pop, no
  D6→2 yet); only after A2's sort does the sorter move to floor 2; then B empties
  the queue. Proves the new clear-edge pop does not reintroduce early-pop.
- Empty-queue park + next-piece recovery: a single piece is deposited/sorted, queue
  empties, TgtFloor stays 0; a later differently-floored IF-05 is then written and
  served — proving the empty-queue default (OQ2) does not deadlock the next piece.

> Planner self-check — Detected project type: Full-stack. Required scenario slots: 8
  (Web/UI: default-state TraceLogPage, event-2-timing alternate state; Backend/API:
  endpoints-touched, happy-path, error-edge-cases; End-to-end: write-on-clear-no-roundtrip,
  AAB-hold, empty-queue-recovery). All slots filled: yes.
