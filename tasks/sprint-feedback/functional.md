# FUNCTIONAL: PASS — S-F3B-FOLLOWUP

> Functional Evaluator · 2026-07-09 · branch feat/f3b-followup-ops-guard
> Ground truth only: git diff + fresh `dotnet test` runs + browser (isolated stack). Generator summary not trusted.
> Handoff marker `## IMPLEMENTATION COMPLETE — S-F3B-FOLLOWUP` present in sprint-log.md → OK.
> SAFETY dimension is a separate evaluator's verdict (not duplicated here).

## Verdict
**FUNCTIONAL PASS.** All functional completion conditions met with fresh evidence. One honest gap noted (cFlagGuard live-toast — covered by automated test), non-blocking.

---

## 1. Test suite — full GREEN, deterministic

- **Full run (from-scratch build):** `dotnet test backend/Wcs.sln` → **실패 0, 통과 318, 건너뜀 0, 전체 318** (19s). = 312 baseline + 6 new. 0 regression, 0 skip.
- **Determinism (real-Sim I/O groups have load-flake history — repeated):**
  - `PlcGatewayIntegrationTests | OpsControllerTests` = 31 tests, **5/5 runs GREEN** (~8s each).
  - `ScenarioTests | HandshakeResidueTests | ApiIntegrationTests` = 35 tests, **3/3 runs GREEN** (~6s each).
  - No flake observed.
- **6 new tests genuine (verified by assertions + a live 6/6 GREEN re-run):**
  - `IT3d_RapidDoubleCellAssign_SecondRejected_ByFreshRead` — 2nd CellAssign REJECTED; C region D0/D1 keeps 1st value (11/101), C_Flag=1; skip-log asserted.
  - `O4_NotReady_Returns409_NoEnqueue` / `O6_NotReady_Returns409_NoEnqueue` — Ready==0 → 409, no PLC_WRITE, Sim unchanged.
  - `O5_ClearR_NotReady_StillAllowed` — Ready==0 → ClearR still OK (R area cleared via queue).
  - `O6_CellAssign_CFlagGuard_ReportsAdvisory` — 2nd response `cFlagGuard=true` when C_Flag=1.
  - `IF09_AutoAlign_WritesTgtFloor_EvenWhenReadyZero_NoRegression` — automatic IF-09 align WRITES D6=2 at Ready==0 (shared consumer path, no regression).
- **Automatic/orchestrated paths intact:** S1–S6 handshake (`HandshakeResidueTests`), `IT1_NormalHandshake_Succeeds`, and auto IF-09 align-at-Ready==0 all GREEN.

### Vacuity / contrast check (task-mandated) — CONFIRMED REAL
Backed up `PlcGateway.cs` (md5 0907498…), temporarily reverted the `CellAssign` guard to the OLD `_latest` stale-snapshot read, rebuilt, ran `IT3d`:
- **RED under old guard** — `WaitUntilAsync("2번째 CellAssign skip 로그")` timed out (write#2 NOT rejected → would overwrite C region). `실패 1, 통과 0`.
- Restored from backup (md5 re-verified identical, 0 temp markers), rebuilt → `IT3d` + 5 Ops-new = **6/6 GREEN**.
- ⇒ the fresh-read fix is **load-bearing**, not vacuous. Working tree restored exactly.

---

## 2. Build / static checks — 0 errors, 0 NEW warnings

- **Backend build:** 0 errors. Warnings = 10× NU1903 (SQLitePCLRaw advisory) — all **pre-existing**, no new warnings.
- **Frontend:** `npm run typecheck` exit 0; `npm run lint` (eslint) exit 0; `npm run build` exit 0 (only pre-existing library warnings: signalr Utils.js annotation + >500kB chunk-size hint).
- **Migrations:** 0 (schema unchanged — `cFlagGuard` is response-DTO only). Migrations dirs 0 diff.

---

## 3. Browser click-through — isolated stack (Playwright)

**Stack (SAFE — startup logs verified):** API `http://127.0.0.1:5216`, `provider=Microsoft.EntityFrameworkCore.Sqlite`, `dataSource=…/func-eval.db` (scratch), `transport=Tcp host=127.0.0.1:1513`. Sim3ds TCP :1513. **No COM1/RTU, no field DB, own port.** (Base appsettings trap confirmed Rtu/COM1/SqlServer/:5205 — all overridden.)

- **Item #2 labels (screenshots 01/02):** O4 visible **"층"**; O6 visible **"셀 번호"** + **"명령 순번"**. Each `<label htmlFor>` resolves to an existing input (a11y maintained, no duplicate labeling; original `aria-label` kept). Labels **persist after typing** values (5 / 10 / 3) — DOM-verified + screenshot.
- **Ready state:** O4 (설정), O5 (R 클리어), O6 (셀 지정) all enabled.
- **Not-Ready (Busy) — live `Ready=0` mid-move (screenshot 03):**
  - O4 & O6 `disabled=true`, `title="Ready 아님(분류/이동 중) — 수동 쓰기 차단"`, plus two visible `⚠ …차단` reason spans.
  - **O5 stays enabled** and opens its dialog "R 영역 강제 클리어 (Clear-R)" while not-Ready (screenshot 04) — recovery tool per Q1.
  - **Backend 409 fresh evidence:** direct `POST /api/ops/sorters/6/tgtfloor` while Ready=0 → **HTTP 409** + audit `[WRN] OPS_SET_TGTFLOOR 거부(Ready==0, 분류/이동 중)`.
  - **FE pre-block honest:** network capture shows only `GET /api/monitor/sorters` + hub negotiate — **no write POST fired from the FE** while blocked (the 409 came from my direct curl, not the browser). No fake success.
- **Console (BLOCKING):** **0 errors, 0 warnings** across /ops, /monitor, /sorters, /data-generator (all=true). Saved to `screenshots/S-F3B-FOLLOWUP_20260709-204900/console.log`.
- **Network / closed-network:** every request same-origin `127.0.0.1:5216`; **0 external requests**.
- **Regression nav:** /monitor, /sorters (F2 3DS-word read), B2B mode toggle (→ /data-generator) all render real content, 0 console errors.

Screenshots dir: `screenshots/S-F3B-FOLLOWUP_20260709-204900/` (01-ops-ready, 02-labels-persist-with-values, 03-not-ready-o4o6-disabled-o5-enabled, 04-o5-clearr-available-while-not-ready, 05-b2b-mode-nav-regression, console.log).

---

## 4. Honest gaps (non-blocking)

- **O6 `cFlagGuard` advisory toast not reproduced live in browser.** Triggering it needs C_Flag=1 while the sorter is Ready=1 (so O6 isn't pre-blocked), not deterministically forceable through the live Sim in a browser window. Covered by automated `O6_CellAssign_CFlagGuard_ReportsAdvisory` (backend returns `cFlagGuard=true`) + FE toast is a direct structural mirror of the already-live `pingPongGuard` path (`ops.ts` + `OpsControls.tsx` diff reviewed). Accepted.
- **Disabled-button visual:** authoritatively evidenced by DOM `evaluate` (o4/o6 `disabled=true` + reason title, o5 `disabled=false`) captured at the Ready=0 moment; screenshot 03 shows the Busy / D4 Ready=0 context. Not a blocker.

## 5. Working-tree integrity
- `PlcGateway.cs` diff = 20 ins / 8 del (fresh-read guard swap only). `HandshakeOrchestrator.cs` 0 diff. `Wcs.Core/**` 0 diff. No stray/temp files. No orphaned `Wcs.Sim3ds.exe`/`Wcs.Api`/`testhost`; no listeners left on :5216/:1513.

**Tally: 318 passed / 0 failed / 0 skipped (312 baseline + 6 new).**
