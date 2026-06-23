# Agent Workflow (Detailed)

## Architecture Patterns

The default is Generate-Verify (3-agent). Select the appropriate pattern based on task characteristics.

| Pattern | Description | When to Use |
|---------|-------------|-------------|
| **Pipeline** | Sequential dependent tasks | Design → Build → Test → Deploy |
| **Fan-out/Fan-in** | Parallel independent tasks then merge | Multi-module development, parallel code review |
| **Expert Pool** | Select and call specialized agents by context | Security/Performance/Accessibility audits |
| **Generate-Verify** | Generate then independent evaluator reviews | Default 3-agent (Planner-Generator-Evaluator) |
| **Supervisor** | Central agent dynamically distributes work | Multi-team collaboration, large feature development |
| **Hierarchical Delegation** | Top-down recursive delegation | Microservices, domain separation |

> For detailed descriptions and execution recipes of each pattern, see `templates/reference/architecture-patterns.md`. Only the summary table above is loaded into every sprint; the full descriptions are kept out-of-band to save per-sprint tokens.

### Pattern Selection Guide

| Condition | Recommended Pattern |
|-----------|-------------------|
| Single feature, standard complexity | **Generate-Verify** (default) |
| Tasks have strict ordering (A must finish before B) | **Pipeline** |
| 2+ independent modules can be built simultaneously | **Fan-out/Fan-in** |
| Need specialized review (security, performance, a11y) | **Expert Pool** |
| Large scope with dynamic task allocation | **Supervisor** |
| Clear domain boundaries (microservices, mono-repo) | **Hierarchical Delegation** |

When unsure, start with **Generate-Verify** and escalate to a more complex pattern only if needed.

### Execution Pattern Reality Check

- **3-Tier (Planner Subagent + Generator/Evaluator Team) is the default** for all non-trivial tasks (see 3-Tier Mandatory Procedure).
- The Generator ↔ Evaluator loop uses Team Mode by default — direct communication is required for the iterative fix cycle.
- For orchestration patterns beyond Generate-Verify (Pipeline, Fan-out, etc.), use Subagent dispatch (Agent tool) for independent tasks, Team Mode for tasks requiring inter-agent communication.

### Parallel Work Rules (when using Fan-out/Fan-in or Supervisor patterns)
- Multiple agents must not modify the same file simultaneously.
- Parallel tasks are separated by module/package boundaries.
- On merge (fan-in), verify and resolve conflicts before completing.

## Multi-Instance Scaling (Per-Role Fan-out)

The default 3-Tier is **1 Planner / 1 Generator / 1 Evaluator** (Generate-Verify). Any role MAY be scaled to N instances **when a declared trigger condition is met** — scaling is opt-in and condition-gated, never a guess. The default stays 1/1/1; escalate only when the trigger fires. "When unsure, start with Generate-Verify."

**Decision authority — who decides to scale:**
- **Generator / Evaluator scaling** is declared by the **Planner inside the Sprint Contract** (`Parallel Modules` / `Evaluation Dimensions` fields). The orchestrator reads the declaration and executes the matching pattern. The orchestrator does NOT invent fan-out on its own.
- **Planner scaling** is decided by the **orchestrator before Phase 1**, since the Planner cannot multiply itself. Heuristic trigger only (below).

**Mechanism — how N instances run (do not deviate):**
- Parallel generation/planning uses **`Workflow` fan-out/fan-in or parallel subagent dispatch (Agent tool)**, NOT extra Team members. Team Mode's `SendMessage` handoff is bound to the single names `generator`/`evaluator`; adding members breaks routing. The **1:1 Generator↔Evaluator Team loop is preserved** for the iterative fix cycle — only the surrounding generate/evaluate fan-out is parallelized.
- Parallel file mutation → `isolation: "worktree"` per agent (or strict module partition). Fan-in merges and resolves conflicts before the loop continues.

### Planner — Multi-Candidate (divergent design → synthesis)
- **Trigger (orchestrator heuristic)**: wide/ambiguous design space, or a high-stakes architectural decision with no obvious single approach. NOT for routine sprints.
- **Pattern**: spawn N Planner subagents with distinct stances (e.g. MVP-first, risk-first, scalability-first) → one **synthesis Planner subagent** receives all N candidates, judges them, and merges into **exactly one** Sprint Contract. The orchestrator never does the synthesis itself (orchestrator does not plan).
- **Hard rule**: the user gate sees **one** contract. Candidates MUST converge before the Phase 1→2 user confirmation — never present N contracts. Optionally note "alternatives considered" in one line.

### Generator — Fan-out / Fan-in (parallel module build)
- **Trigger**: Planner declares 2+ **independent** modules in the Sprint Contract `Parallel Modules` field (module/package-boundary separated, no shared file writes).
- **Pattern**: one Generator instance per module, run concurrently (worktree-isolated when files could collide) → each implements + runs its own tests → **fan-in**: merge, resolve conflicts, run integration tests, THEN hand off to evaluation.
- **Hard rules**:
  - Two Generators MUST NOT write the same file. Partition is by module/package boundary, declared by the Planner.
  - No fan-in completes with conflicts unresolved or integration tests failing.
  - Per-module log goes to `tasks/sprint-log/{module}.md`; the orchestrator/fan-in step consolidates a single `## IMPLEMENTATION COMPLETE` summary into `tasks/sprint-log.md` (the handoff marker the Evaluator checks). Parallel writers must not append to the single `sprint-log.md` concurrently.

### Evaluator — Expert Pool (parallel dimension verification)
- **Trigger**: Planner declares `Evaluation Dimensions` (e.g. functional / security / performance) in the Sprint Contract, or the change touches a security/perf-sensitive surface.
- **Pattern**: on handoff, spawn one Evaluator per dimension, run concurrently. Each runs the mandatory verification for its dimension and writes `tasks/sprint-feedback/{dimension}.md`. The orchestrator aggregates into `tasks/sprint-feedback.md` (the file the pre-commit hook and Generator read).
- **Hard rules**:
  - **APPROVED requires ALL dimensions PASS (AND, not OR).** Any single FAIL → consolidated feedback → Generator fix cycle.
  - Each dimension still obeys ALL Evaluator rules (fresh evidence, click-through, console capture, port source-of-truth, no infra-skip).
  - This pool is the **runtime/behavioral** verification layer. It does NOT replace the Step 4.5 `code-reviewer` (static code-quality/security/architecture), which remains a separate post-APPROVED gate. The two layers stay non-overlapping.

### Iteration accounting under scaling
- The **5-iteration cap and "3 consecutive FAIL → re-plan"** are measured at the **sprint level** (the aggregated verdict per cycle), not per instance. One cycle = one full fan-out generate + one aggregated evaluate; all parallel instances within a cycle count as a single iteration.
- On aggregated FAIL, the orchestrator MAY re-dispatch **only the failed module(s)/dimension(s)**, not the whole fleet — but the cycle still increments the shared counter by one.

## Agent/Skill File Structure

Agent roles (Planner / Generator / Evaluator) are defined inline in this file — see the `## Planner`, `## Generator`, `## Evaluator` sections below. Those inline definitions are the canonical source the 3-Tier spawn prompts read from.

- `.claude/agents/` — **Optional, per-project.** A target project may create `.claude/agents/*.md` files to override or specialize the inline agent definitions for its own needs (e.g. domain-specific Evaluator criteria). See "Agent Definition Templates" section below for the expected structure. If the directory does not exist, the inline definitions in this file are used as-is.
- `.claude/skills/` — **Optional, per-project.** Custom skills the project adds on top of the harness's `3tier-start` skill. Not required for 3-Tier to function.
- `tasks/workflow-agents.md` — This file. Process rules + inline agent definitions. Canonical for runtime behavior.

```
project/
├── .claude/
│   ├── agents/          # Agent definition files
│   │   ├── planner.md
│   │   ├── generator.md
│   │   ├── evaluator.md
│   │   └── (add as needed: architect.md, security.md, etc.)
│   └── skills/          # Skill files
│       ├── code-quality/
│       │   └── SKILL.md
│       └── (add per project)
├── tasks/
│   ├── workflow-rules.md
│   ├── workflow-agents.md
│   └── ...
```

## Planner

- Defines **what** to build. Does **not** define **how** to build it.
- Does not finalize technical details (library choices, API design, data structures, etc.) at the planning stage.
- Getting technical details wrong at the planning stage propagates errors throughout the entire implementation.
- Focuses only on the big picture: goals, scope, user scenarios, success criteria.
- Expands the user's short request (1-4 sentences) into a full spec. Focuses on product context and high-level design rather than technical details.
- **When requirements are ambiguous, ask clarifying questions before producing a Sprint Contract. Do not guess intent — interview the user.**
  - What is the expected outcome? (not how, but what should exist when done)
  - What are the constraints? (tech stack, timeline, dependencies)
  - What should explicitly NOT be changed? (scope boundaries)
  - Only proceed to Sprint Contract when all ambiguity is resolved.
- Delegates detailed technical decisions to the Generator.
- **Never compromise requirements because implementation is difficult.** If it's hard, find a way — don't lower the bar. The Planner is isolated from implementation context precisely to prevent this bias.
- Only report to user when technically impossible. Never refuse because something is difficult.
- For difficult problems, find a solution and present the best recommendation with reasoning. Do not just list options.
- **Before writing the Sprint Contract, the Planner MUST:**
  1. **Detect the project type** from project signals — not from memory and not from the way the user phrased the request. Use file/folder and structural cues only; never name a specific framework or tool. The type is exactly one of `Web/UI`, `Backend/API`, `Library/CLI`, `Full-stack`.
     - `Web/UI` signal: a browser-facing entry point (e.g., an HTML shell or client-rendered view/component tree) with no server-side route/controller layer in the same repo.
     - `Backend/API` signal: server-side route/controller/handler files and a server entry point, with no browser-facing UI tree in the same repo.
     - `Library/CLI` signal: a published package manifest exposing a public API surface, or a CLI entry point, and no server routes or browser UI.
     - `Full-stack` signal: both a browser-facing entry point AND server-side route/controller files live in the same repo.
  2. **Enumerate per-type verification scenarios directly inside the Sprint Contract** using the slots in the Sprint Contract Template. The scenario count N is decided per-sprint from the actual surface area of this sprint's change — the Planner picks N; the harness does not hardcode a minimum. Leaving the slots generic, empty, or at the placeholder failure-marker text is a harness violation, not a stylistic choice.
  3. **Emit the Planner self-check line** at the bottom of the contract in the exact form shown in the template: detected type, required slot count N, the list of slot names actually filled, and an explicit `yes/no` flag for whether all slots are filled. This single line is the Evaluator's one-line sanity check on contract specificity.
- **May declare scaling in the Sprint Contract** (see §Multi-Instance Scaling): list independent `Parallel Modules` (→ Generator fan-out) and/or `Evaluation Dimensions` (→ Evaluator expert pool). Declare only real, boundary-clean partitions and genuinely distinct review dimensions — never pad to look thorough. Omit both when the work is single-module / single-dimension; the default 1/1/1 is the correct answer for routine sprints.
- Output: Planning document (sprint contract including goals, scope, evaluation criteria, detected project type, per-type verification scenarios, optional scaling declarations, and the Planner self-check line)

## Generator

- Based on **what** the Planner defined, autonomously decides **how** to build it.
- Technology stack, implementation patterns, and structural design are the Generator's decision. However, prefer proven technologies — stable APIs, high composability, and sufficient community and documentation lead to fewer failures.
- Innovate in business logic; keep infrastructure on proven technology.
- Do not over-engineer. Clean but simple.
- Works only when "what needs to be done" is clearly understood per the sprint contract.
- **Before implementing**: Read `tasks/lessons.md` and `tasks/feedback-archive.md` (if they exist). These contain mistakes that must not repeat and past evaluation history. Ignoring them and repeating a known issue is a harness violation.
- Before implementing a new feature, capture current behavior first:
  - Web/UI: screenshot of current state
  - API: record current responses
  - CLI: record current output
  - Library: record current test results
- Output: Working code + tests passing

## Evaluator

- Judges deliverables according to "what to evaluate against" as specified in the sprint contract.
- Evaluator must be separate from the Generator. Never self-evaluate your own code.
- Gates quality from a skeptical perspective. Does not pass leniently.
- Reviews with the mindset: "Find only problems. It's fine to skip the positives."
- Validates by interacting with the actual running application. Does not judge by code alone.
  - Web/UI: screenshots via Playwright MCP (configure by default; suggest setup if missing)
  - API: actual request/response verification
  - CLI: actual command output verification
  - Library: test suite execution and result verification
- **"코드 존재" ≠ PASS. 사용자 여정을 검증하라.**
  Evaluator는 코드/API가 존재하는지가 아니라, 사용자가 실제로 도달하고 사용할 수 있는지를 검증해야 한다.
  - 사용자가 기능을 발견할 수 있는가? (네비게이션, 메뉴, 링크, 버튼이 존재하는가)
  - 사용자가 정상 사용 경로로 기능에 도달할 수 있는가? (직접 URL/API 호출이 아닌)
  - 기능이 기존 플로우에 연결되어 있는가? (고아 페이지/엔드포인트가 아닌)
  - **위반 예시**: API 엔드포인트는 있지만 호출할 UI가 없음 → FAIL. 페이지는 있지만 네비게이션에 없음 → FAIL.
  - 이 규칙은 모든 프로젝트 타입에 적용된다. "코드를 읽어보니 있다"는 PASS 근거가 될 수 없다.
- **"인프라 미실행"은 검증 스킵 사유가 아니다.**
  API 서버, DB, 백엔드 등 검증에 필요한 인프라가 실행 중이지 않으면 — 스킵하지 말고 직접 시작하라.
  - 서버가 안 돌아가면 → 서버를 시작하라 (`npm run dev`, `python app.py`, etc.)
  - DB가 없으면 → 마이그레이션을 실행하라
  - "코드 리뷰로 대체"는 절대 허용되지 않는다. E2E 검증의 대체재가 아니다.
  - 인프라를 시작할 수 없는 기술적 문제가 있으면 → FAIL 처리하고 구체적 이유를 sprint-feedback.md에 기록하라. "스킵"이 아니라 "블로커"로 보고하라.
  - **위반 예시**: "백엔드 API 미실행으로 E2E save flow는 코드 리뷰로 대체" → 이것은 검증이 아니다. harness violation.
- **Browser verification mandate (Web/UI and Full-stack)**: after static checks, Evaluator MUST run browser verification via Playwright MCP or Playwright CLI — navigate, screenshot, and READ the screenshot to verify visually. Skipping browser verification is a harness violation.
- **Console / dev-mode warning capture (Web/UI and Full-stack) — BLOCKING.**
  브라우저 검증 시 `page.on('console', ...)` 와 `page.on('pageerror', ...)` 를 등록해 모든 콘솔 출력을 `screenshots/{sprint}/console.log` 로 저장한다. 다음 항목은 스크린샷이 정상이어도 무관하게 **BLOCKING** 분류로 sprint-feedback.md 에 FAIL 기록:
  - React dev-mode warning (`validateDOMNesting`, `Each child in a list should have a unique "key"`, `Cannot update a component while rendering a different component`, `Maximum update depth exceeded` 등)
  - `pageerror` 이벤트 일체 (uncaught exception)
  - Network 4xx/5xx (애플리케이션이 의도적으로 처리·표시하는 경우는 예외 — sprint-feedback.md 에 의도임을 명시)
  - 이유: HTML 구조 위반·React 안티패턴·useEffect cascade 같은 결함은 click-through 의 최종 스크린샷에는 보이지 않지만 콘솔에는 노출된다. 콘솔을 무시하면 이 결함군이 통과 — 검증 누락이다.
- **Fresh evidence 의무 — "should/probably/Done!" 금지.**
  PASS 작성 전 "지금 실제로 돌렸다"는 증거가 sprint-feedback.md 에 인용되어야 한다. 이전 sprint 결과·가설·추정·agent 의 success 보고 문구만 보고 PASS 금지. 모든 PASS 는 fresh tool output (스크린샷 파일 경로, HTTP 응답 본문 발췌, 명령 출력 raw line, console.log 발췌) 로 뒷받침. (`superpowers:verification-before-completion` 흡수)
- **Port source of truth**: before navigating, Evaluator MUST read `{project}/.claude/ports.local.json` and construct URLs from the recorded port. Hardcoded `localhost:3000`/`5173` in verification is a harness violation — it invites false-PASS from a sibling project's server running the same port. If the file is missing while a dev server is running, fail with "port allocation missing, orchestrator violated Port Management policy" — do not guess. → `templates/reference/port-policy.md` §Evaluator Integration.
- **Screenshot policy**: save screenshots to `screenshots/{sprint-name}_{YYYYMMDD-HHMMSS}/` (e.g., `screenshots/S-DEV-01_20260413-150000/`). Timestamp is Asia/Seoul local time per `workflow-rules.md` §Time Convention — no TZ suffix. Never overwrite or delete previous screenshots — they are evaluation evidence.
- **Click-through 의무 — 최종 상태 확인만으로 PASS 금지.**
  Evaluator의 가장 흔한 실패는 "페이지가 렌더링됐다 → PASS" 같은 얕은 검증이다.
  Sprint Contract의 각 Verification Scenario는 **사용자 상호작용을 코드로 재현**해야 한다. 최종 DOM 스냅샷이나 헬스체크 핑 하나로 대체 불가.
  - 각 scenario: navigate → click → fill → submit → assert 사이클을 빠짐없이 코드에 기록. 예) "장비 삭제" = (1) 설정 페이지 이동 → (2) 추가 클릭 → (3) 폼 입력 → (4) 저장 → (5) 새 row 확인 → (6) 삭제 버튼 클릭 → (7) 확인 다이얼로그 → (8) row 제거 확인. 중간을 건너뛰면 FAIL.
  - 각 단계마다 번호 스크린샷 (`01-before.png`, `02-filled.png`, … `NN-end.png`) + 콘솔 로그 `[N/total] 설명`. 번호가 중간에 비면 step skip → harness violation.
  - 실패 시 `screenshots/…/FAIL-trace.zip` 저장 (Playwright tracing으로 DOM/network/video 사후 감사 가능).
  - URL은 프로젝트 `.claude/ports.local.json`에서 읽음 — `localhost:3000` 하드코딩 금지 (port-policy 위반).
  - Backend/API에도 동일 원칙: 각 scenario = 실제 HTTP 요청→응답 왕복, 결과 JSON을 증거로 저장. 코드 리뷰로 대체 금지.
  - 권장 템플릿: `templates/reference/evaluator-verify-script.md` (`.mjs + Playwright trace` 스켈레톤).
- Evaluation timing: evaluate per module or feature. Do not defer until the entire build is complete.
- Uses project-appropriate validation tools to verify actual behavior.
- Select evaluation criteria based on project type. All types share the same 4-criterion structure (★★★ = top priority, ★★ = important):

### Web/UI Projects
  1. **Design Quality** (★★★) — Do color, typography, and layout form a cohesive atmosphere?
  2. **Originality** (★★★) — Are there intentional creative choices, or is it an AI slop pattern?
  3. **Craft** (★★) — Technical execution quality: type hierarchy, spacing consistency, contrast
  4. **Functionality** (★★) — Usability independent of aesthetics. Can the user complete their task?

### Backend/API Projects
  1. **API Design Quality** (★★★) — Consistent naming, RESTful/GraphQL convention adherence, error response structure, versioning
  2. **Architecture Originality** (★★★) — Scalable structure, appropriate pattern selection, intentional design rather than AI slop
  3. **Craft** (★★) — Input validation, error handling, edge cases, logging, transaction management
  4. **Functionality** (★★) — Requirements met, performance, security, data integrity

### Library/CLI Projects
  1. **API Ergonomics** (★★★) — Intuitive interface, consistent naming, clear error messages, minimal boilerplate
  2. **Architecture Originality** (★★★) — Composable design, appropriate abstractions, intentional structure
  3. **Craft** (★★) — Edge case handling, backward compatibility, documentation, type safety
  4. **Reliability** (★★) — Test coverage, error recovery, cross-platform behavior, performance

### Full-stack Projects
  Evaluate frontend and backend separately using their respective criteria above, then combine:
  1. **Integration Quality** (★★★) — API contract consistency, data flow coherence, error propagation between layers
  2. **Per-layer Quality** (★★★) — Apply Web/UI criteria to frontend, Backend/API criteria to backend independently
  3. **Craft** (★★) — End-to-end type safety, shared validation logic, consistent error handling across layers
  4. **Functionality** (★★) — Full user journey works end-to-end, no layer-boundary gaps

### Multi-language / Multi-stack Projects
  When a project uses multiple languages (e.g., TypeScript frontend + C# backend, or Kotlin + Swift):
  - Evaluate each language/stack boundary independently using the matching project type criteria.
  - Add cross-boundary checks: API contract consistency, shared schema validation, serialization compatibility.
  - Ensure consistent naming conventions and error handling patterns across language boundaries.

- Performance checks: Evaluator verifies N+1 queries, memory leaks, unnecessary repeated calls, and missing indexes.
- Deliver evaluation results as specific feedback (what's lacking and how to improve) to the Generator.
- Record feedback in `tasks/sprint-feedback.md`.
- **Minor feedback handling**: Issues that don't block APPROVED must be registered in `tasks/todo.md` with the sprint name as context (e.g., `- [ ] [S-MULTI-03] ProgramSwitcher 전환 실패 시 빈 catch 블록`). Minor items are not forgotten — they are tracked and addressed before the next sprint begins.
- **On APPROVED**: Append key feedback summary to `tasks/feedback-archive.md` (preserve evaluation history across sprints).
- **Repeat detection**: Before writing feedback, read `tasks/feedback-archive.md`. If the same issue appeared in a previous sprint → add it to `tasks/lessons.md` as a rule. Feedback that repeats is no longer feedback — it's a lesson.

## Sprint Contract Template

```
[Sprint Contract]
- Goal: (defined by Planner)
- Implementation Scope: (list of what Generator must do)
- Evaluation Criteria: (specific criteria + weights for Evaluator to judge)
- Completion Conditions: (minimum conditions for Evaluator to pass)

- Parallel Modules (optional — enables Generator fan-out; see §Multi-Instance Scaling):
    List module/package boundaries that can be built CONCURRENTLY with NO shared file writes.
    Declare only real, boundary-clean partitions — never pad. Each entry = one parallel Generator.
    Omit or write "N/A (single module)" when the work is not partitionable.
- Evaluation Dimensions (optional — enables Evaluator expert pool; see §Multi-Instance Scaling):
    List dimensions to verify in PARALLEL (e.g. functional, security, performance).
    Each entry = one parallel Evaluator; APPROVED requires ALL to PASS.
    Omit or write "functional only" for standard single-dimension review.

- Detected Project Type: <Web/UI | Backend/API | Library/CLI | Full-stack>
  (Planner fills exactly one from project signals — not from memory, not from user's phrasing.)

- Verification Scenarios (per-type, mandatory — empty = harness violation):
  Planner enumerates the slots for the detected type below. Count N is decided
  per-sprint from the actual surface area of this sprint's change. Leaving any
  slot as the placeholder text is a failure, not an acceptable default.

  === If Web/UI ===
  - Default state of each surface touched by this sprint:
      MISSING — Planner must fill per detected type
  - Each alternate state the sprint introduces (loading / selected / expanded / …):
      MISSING — Planner must fill per detected type
  - Relevant empty / error state surfaced by this sprint:
      MISSING — Planner must fill per detected type
  - Dark mode variant (only if the project supports dark mode; otherwise mark N/A with reason):
      MISSING — Planner must fill per detected type
  - Key interaction flow after the change (the user-visible behavior the sprint is meant to produce):
      MISSING — Planner must fill per detected type

  === If Backend/API ===
  - Explicit list of endpoints touched by this sprint (method + path):
      MISSING — Planner must fill per detected type
  - Happy path per endpoint (expected input → expected output shape):
      MISSING — Planner must fill per detected type
  - Relevant error cases per endpoint (401 / 403 / 404 / 422 / 500 as applicable — Planner picks which apply, does not pad):
      MISSING — Planner must fill per detected type

  === If Library/CLI ===
  - Explicit public API surface touched by this sprint (function signatures / command names / flag names):
      MISSING — Planner must fill per detected type
  - Edge cases per public entry (boundary values, invalid input, empty / oversized input as applicable):
      MISSING — Planner must fill per detected type

  === If Full-stack ===
  - Applicable Web/UI scenarios (as above) for the frontend surface this sprint touches:
      MISSING — Planner must fill per detected type
  - Applicable Backend/API scenarios (as above) for the backend surface this sprint touches:
      MISSING — Planner must fill per detected type
  - At least one end-to-end data-flow scenario crossing two or more layers (describe the flow, not the tooling):
      MISSING — Planner must fill per detected type

> Planner self-check — Detected project type: <type>. Required scenario slots: <N> (<short comma-separated list of the slot names Planner filled>). All slots filled: <yes | no>.
```

The `MISSING — Planner must fill per detected type` text is a deliberate failure
marker: a Sprint Contract containing this string after the Planner's turn is
not a "draft," it is a broken contract. Evaluator may reject the contract on
sight. The self-check line at the bottom gives Evaluator a single-line check
that either confirms or exposes the state of the contract without scanning
every slot.

## 3-Tier Mandatory Procedure

The Planner → Generator → Evaluator flow is **not optional**. Planner runs as a Subagent (one-shot). Generator and Evaluator run as a Team Agent (iterative loop). The main agent acts only as an orchestrator — it must not perform planning, implementation, or evaluation itself. Skipping a phase or combining roles is a harness violation.

### Global Instructions Check

At every phase transition, re-read the relevant section of the project CLAUDE.md:
- **Entering Phase 1**: Re-read project CLAUDE.md evaluation criteria and constraints.
- **Entering Phase 2**: Re-read project CLAUDE.md tech stack, coding rules, and banned practices.
- **Entering Phase 3**: Re-read project CLAUDE.md evaluation criteria + any project-specific verification rules (e.g., "E2E: Playwright", test requirements).

This prevents drift — the agent must not rely on memory of rules read at session start. Re-confirm before each phase.

### Phase 1 — Plan (Planner)

**Gate: Do not enter Phase 2 without user confirmation of the Sprint Contract.**

1. Analyze the user's request.
2. If ambiguous, ask clarifying questions (do not guess).
3. **Detect the project type from project signals** (file/folder structure, presence of browser entry point, server route/controller files, published package manifest, CLI entry, or a combination) — exactly one of `Web/UI | Backend/API | Library/CLI | Full-stack`. Do not infer the type from the user's phrasing; read the repo. Do not name specific frameworks or tools.
4. **Enumerate per-type verification scenarios directly in the Sprint Contract** by filling the per-type slots from the Sprint Contract Template. Scenario count N is decided per-sprint from the actual surface area of this sprint's change. Generic text, empty slots, or the placeholder failure-marker text remaining after this step is a harness violation.
5. Produce a Sprint Contract (goal, scope, evaluation criteria, completion conditions, detected project type, per-type verification scenarios, and Planner self-check line).
6. **Emit the Planner self-check line** at the bottom of the contract in the form specified by the template (detected type, slot count N, slot-name list, `all slots filled: yes/no`). If the self-check line cannot honestly say `yes`, the Planner's job is not done — do not hand off.
7. Present to user and get confirmation.
8. Only then proceed to Phase 2.

**Exception — No Sprint Contract needed for**:
- Bug fixes with clear reproduction
- Formatting, linting, typo fixes
- Adding logs, improving error messages
- Simple changes within established patterns (single file, no architectural impact)

For these exceptions, create `tasks/sprint-skip.txt` with the reason (e.g., `echo "typo fix in README" > tasks/sprint-skip.txt`) and proceed directly to Phase 2. The pre-commit hook accepts this file as a valid exception.

> **Freshness 규칙**: `sprint-skip.txt`는 **수정 후 10분 이내**에만 유효. 오래된 파일은 이전 커밋용 잔존물로 간주되어 예외로 인정되지 않는다 (stale skip → 차단). 커밋 직전에 다시 `echo '사유' > tasks/sprint-skip.txt`로 갱신하거나, 새 trivial 변경마다 사유를 재기록하라. 이 규칙은 **Gate 1 (Sprint proof) · Gate 2 (Test evidence) · Gate 3 (Multi-file 요구) 모두에 공통 적용**된다 — fresh `sprint-skip.txt` 하나가 세 gate 전부를 bypass.
>
> **값의 source of truth**: 10분(600초) 상수는 `hooks/pre-commit-3tier.sh`의 `SKIP_VALID` 계산 로직에 정의되어 있고 본 문서는 교육용 복제다. 값 변경 시 hook 상수와 본 문서(+ `CLAUDE.md`, `hooks/enforce-3tier.sh`)를 함께 갱신하라.

### Phase 2 — Generate (Generator)

**Gate: Do not enter Phase 3 without working code + tests passing.**

1. Implement strictly within the Sprint Contract scope.
2. Do not expand scope beyond what was agreed.
3. Run existing tests (unit, E2E, any type) to confirm implementation works. Fix until passing.
4. When implementation is complete: summarize changes + test results.
5. Proceed to Phase 3.

### Testing Boundary Between Generator and Evaluator

The split is not by test type (unit vs E2E) but by **purpose**:
- **Generator**: Runs all existing tests to confirm "my code works." Reports results.
- **Evaluator**: Re-runs the **same tests independently** to verify Generator's claim + judges against Sprint Contract criteria.

Evaluator must not trust Generator's test results. Even if Generator says "all tests pass," Evaluator runs them again from scratch. The value is independent verification, not different test types.

In small projects where E2E is the only test, both Generator and Evaluator run E2E — but Evaluator is the one who **judges pass/fail against the Sprint Contract**.

### Phase 3 — Evaluate (Evaluator)

**Gate: Do not declare completion without evaluation.**

1. **Re-read project CLAUDE.md** — confirm all project-specific verification rules before evaluating.
2. **Re-run all tests independently** — do not trust Generator's reported results. Run from scratch.
3. Switch perspective — review as a skeptical evaluator, not the author.
4. Evaluate against the Sprint Contract criteria (not general impressions).
5. **Execute mandatory verification by project type** (cannot be skipped):

| Project Type | Mandatory Verification | Manual check is NOT sufficient |
|---|---|---|
| **Web/UI** | Project-configured browser E2E test runner + screenshot confirmation of each visual scenario listed in the Sprint Contract | Must test actual user flows in browser |
| **Backend/API** | Automated test code execution (not manual curl) | Must have reproducible test, not one-off command |
| **Library/CLI** | Full test suite execution + result recording | Must run all existing tests + new tests for changes |
| **Full-stack** | All of the above + integration test across layers | Must verify end-to-end data flow |
| **All types — Static checks** | Independently run the project-configured linter, type checker, and formatter (check mode). Record pass/fail/not-configured in `tasks/sprint-feedback.md` | Must execute tools from scratch; do not trust Generator's reported static-check results |

> If the project has no linter / type checker / formatter configured, Evaluator records `not configured` for that tool in `tasks/sprint-feedback.md` and may suggest introducing one (per `workflow-rules.md` §1 Linter fallback). Absence of configuration is not an evaluation failure, but silently skipping a configured tool is.

6. Record evaluation in `tasks/sprint-feedback.md`.
7. If passing: declare completion.
8. If failing: deliver specific feedback → return to Phase 2.

**Violation example**: Checking API response with curl only and declaring "complete" — this is a Phase 3 violation. Verification must use automated test code.

### Enforcement

**Document rules alone don't work.** Agents skip phases to optimize for speed. Physical enforcement is required.

#### Pre-commit Hook (Physical Enforcement)

`~/.claude/templates/hooks/pre-commit-3tier.sh` — auto-installed during Session Start.

This hook checks at commit time:
1. **Code changes + no test results** → commit blocked. Phase 3 required.
2. **3+ files changed + no sprint-feedback.md** → commit blocked. Evaluator feedback required.
3. **Docs-only changes** → pass (no code verification needed).

Does not depend on agent compliance. No file = no commit. Physically enforced.

#### 3-Agent Execution Model

Phase 1 (Plan) uses a **Subagent** (one-shot). Phase 2-3 (Generate ↔ Evaluate) uses a **Team Agent** (iterative loop).

```
Phase 1: Planner Subagent (one-shot)
  → Sprint Contract → User confirmation

Phase 2-3: Team Agent (iterative loop)
  Generator ←→ Evaluator (direct communication, repeat until passing)
  → 3 consecutive failures → escalate to Planner Subagent (re-plan)
```

**Why this hybrid:**
- **Planner as Subagent**: Planning is one-shot. No iteration needed. Subagent returns Sprint Contract and exits. Isolated from implementation context — cannot be pressured to weaken requirements.
- **Generator ↔ Evaluator as Team**: The fix loop requires direct communication. Evaluator tells Generator "fix this" directly. Generator responds "done, review again" directly. No orchestrator bottleneck. No information loss.

**No inter-agent compromise. Each agent must be faithful to its own role:**
- **Planner**: Does not weaken requirements because Generator says it's hard.
- **Generator**: Does not ask Evaluator to relax criteria. Implements until it passes.
- **Evaluator**: Does not pass work out of sympathy for Generator's effort. Judges only against Sprint Contract.

Agents must not negotiate with each other to lower the bar. If the standard cannot be met, escalate to the user — don't agree among themselves to accept less.

#### Phase 1 — Planner (Subagent)

Spawn prompt and execution details: see `skills/3tier-start/SKILL.md` Step 1.

Orchestrator presents Sprint Contract to user for confirmation before proceeding.

#### Phase 2-3 — Generator ↔ Evaluator (Team Agent)

Requires: `CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS=1`

TeamCreate + Agent spawn + kick-off sequence: see `skills/3tier-start/SKILL.md` Step 3.

**Flow:**
```
1. Generator reads sprint-contract.md, lessons.md, feedback-archive.md directly
2. Generator implements + runs tests
3. Generator appends "## IMPLEMENTATION COMPLETE" + summary to sprint-log.md
4. Generator → SendMessage(to: "evaluator") → "Implementation complete."
5. Evaluator starts IMMEDIATELY — reads code + sprint-log.md directly
6. Evaluator runs mandatory verification independently
7. Evaluator checks feedback-archive.md for repeat issues → promotes to lessons.md
8. Evaluator writes results to sprint-feedback.md
9. PASS → append to feedback-archive.md → "APPROVED"
10. FAIL → SendMessage specific feedback to generator
11. Generator fixes → back to step 3
12. After 3 consecutive failures → escalate to Planner (re-plan)
```

### Handoff Protocol (핸드오프 규약)

핸드오프 실패는 가장 흔한 3-Tier 장애다. 이 규약은 물리적으로 강제한다.

**Generator 완료 의무 (2단계, 둘 다 필수):**
1. `tasks/sprint-log.md`에 `## IMPLEMENTATION COMPLETE` 마커 기록 (파일 기반 증거)
2. `SendMessage(to: "evaluator")`로 Evaluator 활성화 (트리거)
- 둘 중 하나라도 누락 시 harness violation.
- 마커 없이 메시지만 보내면: Evaluator가 확인할 근거가 없음.
- 메시지 없이 마커만 쓰면: Evaluator가 활성화되지 않음.

**Evaluator 활성화 규칙:**
- Spawn 시점에는 평가를 시작하지 않는다. **Generator로부터 'Implementation complete' 메시지를 받아야만** 활성화된다.
- 활성화 후: sprint-log.md에서 `## IMPLEMENTATION COMPLETE` 마커 존재를 확인한 뒤 평가를 시작한다. 마커가 없으면 평가하지 않는다.
- "무엇을 평가해야 하나요?" 같은 질문 금지 — sprint-log.md와 코드를 직접 읽어라.
- Generator의 메시지 내용은 신뢰하지 않는다. 파일을 직접 확인한다.
- 활성화 메시지를 받고 평가를 시작하지 않는 것은 harness violation.

**Orchestrator 안전망 (3층 방어):**
- Orchestrator는 Generator 턴 종료 후 `tasks/sprint-log.md`를 확인한다.
- `## IMPLEMENTATION COMPLETE` 마커가 있는데 Evaluator가 시작하지 않았으면:
  → Orchestrator가 직접 `SendMessage(to: "evaluator", message: "Generator completed. Start evaluation now.")`
- 이 안전망은 Generator의 SendMessage 누락을 보완한다.

**Key principles:**
- Generator and Evaluator read project files **directly** — no information passed through orchestrator
- Evaluator runs actual tests (Playwright, API tests, test suite) — not just code review
- Evaluator writes sprint-feedback.md — generator cannot write it
- Direct communication — no bottleneck, no information loss
- File-based evidence (sprint-log.md marker) + message-based trigger (SendMessage) = 이중 보장

#### Orchestrator Role

The main agent acts as orchestrator. It does not plan, implement, or evaluate.

1. Receive user request
2. Spawn Planner Subagent → get Sprint Contract. (Optional: if the design space is wide/high-stakes, spawn N Planner candidates + a synthesis Planner per §Multi-Instance Scaling → still one contract.)
3. Present Sprint Contract to user → get confirmation
4. Read the contract's scaling declarations (`Parallel Modules` / `Evaluation Dimensions`):
   - Single-module + single-dimension → create the standard 1/1 Team (Generator + Evaluator).
   - 2+ Parallel Modules → fan out Generators via `Workflow`/parallel dispatch, fan-in (merge + integration tests), THEN run the Evaluator loop on the merged result.
   - 2+ Evaluation Dimensions → on handoff, spawn one Evaluator per dimension, aggregate to APPROVED only if ALL pass.
   - See §Multi-Instance Scaling for hard rules. → team runs autonomously
5. **Proactive Monitoring (방치 금지)**: 팀 spawn 후 Orchestrator는 능동적으로 점검해야 한다. 유저가 "진행중?" 하고 물어봐야 비로소 확인하는 것은 모니터링 실패다.
   - **점검 타이밍**: Generator 또는 Evaluator의 각 턴이 끝날 때마다 즉시 `tasks/sprint-log.md`와 `tasks/sprint-feedback.md`를 읽는다.
   - **유저 보고**: 매 사이클마다 1줄 요약을 유저에게 보고한다. 형식: `[사이클 N] Generator: 완료/진행중 | Evaluator: PASS/FAIL/대기 — 핵심 내용`
   - **교착 감지**: 팀 spawn 후 합리적 시간 내에 sprint-log.md에 변화가 없으면 팀 상태를 확인하고 유저에게 보고한다.
   - **Handoff enforcement**: Generator 턴 종료 후 sprint-log.md에 `## IMPLEMENTATION COMPLETE` 마커가 있는데 Evaluator가 시작하지 않으면 → 즉시 `SendMessage(to: "evaluator", message: "Generator completed. Start evaluation now.")` 전송
   - If iteration count reaches 3: warn user before escalating to Planner
   - User can intervene at any time based on these reports
6. On 3 consecutive failures: stop team, re-spawn Planner Subagent for re-planning → restart team with a fresh Sprint Contract. On 5 total iterations: stop team, report to user. All three agents run on the best model (`opus`), so 3 consecutive failures signal a planning problem, not a model-tier gap. See SKILL.md Step 4 for the full procedure.
7. On approval — **Code Review Pass (4-Tier — code quality gate)** before commit:
   - Evaluator 가 APPROVED 를 작성하면 commit 직전 `Skill({ skill: "superpowers:requesting-code-review" })` 를 발동해 Evaluator 가 보지 않는 영역 (아키텍처·추상화·네이밍·보안·내부 복잡도·가독성·유지보수성) 을 독립 검토한다. Evaluator (사용자 여정·브라우저·lint/tsc·BE/DB diff) 와 영역 비중복.
   - Critical/BLOCKING 결함 발견 → Generator 에게 fix-only 1 iter 추가 (5-iteration cap 에 합산, 별도 카운터 만들지 말 것). fix 후 Step 4 의 evaluator 검증을 다시 받고 Step 4.5 재진입.
   - MAJOR/MINOR 만 있으면 → sprint-feedback.md "Minor" 섹션에 등재 (todo.md 가 아니라 sprint-feedback.md 에 직접 추가, 다음 sprint Generator 가 읽음) 후 commit 진행.
   - 결과 1줄 메트릭을 `tasks/feedback-archive.md` 에 `[CODE-REVIEW] sprint=<id> critical=N major=M minor=K iter=K` 형식으로 기록. 형식 spec: `templates/reference/4tier-code-review-metrics.md`.
8. On final approval: report completion to user with final sprint-feedback.md summary.

#### Orchestrator Push Rule

Pushing to remote is NEVER automatic or inferred. The orchestrator must:
- **Commit locally** after Evaluator approval (this is the automated part).
- **Pause before `git push`**. Explicit user authorization is required for each push separately — prior approvals do NOT transfer to new commits.
- **Do not interpret ambiguous input as push authorization.** Typos, keyboard-shift artifacts (e.g. Korean `커밋` typed with hand shifted one key right → `xjsly`), or single-character responses to a push question must be **clarified**, not guessed. The cost of asking "did you mean push?" is one turn; the cost of a premature push is a public history artifact that requires force-push to undo.
- **If a premature push is discovered**, report it clearly and ask the user whether to force-push revert or accept, rather than silently continuing.
- **Broader rule**: the same principle applies to all one-way / destructive actions — force-push, branch delete, hard reset, `--no-verify`, `rm -rf`, database drop. These sit on the other side of a blast-radius line and require unambiguous authorization.

```
User → Orchestrator → Planner (Subagent, one-shot)
                       ↓
                    [Sprint Contract] → User confirmation
                       ↓
                    TeamCreate(Generator, Evaluator)
                       ↓
                    Generator ←→ Evaluator (autonomous loop)
                       ↓
                    Pass → Orchestrator reports to user
                    3 Fails → Planner re-plan → restart team
```

#### Exception — Single agent allowed

Only for Sprint Contract skip targets:
- Simple bug fix (1 file, clear reproduction)
- Formatting, typo, log additions
- Must create `tasks/sprint-skip.txt` with reason — **수정 10분 이내 freshness 필요** (pre-commit hook checks file + mtime)

All other cases require **3-Agent separation (Planner Subagent + Generator/Evaluator Team)**.

#### Team Agent Safety Rules

Team Agents introduce risks that Subagents don't have. These rules constrain team behavior:

**Scope control:**
- Generator must not modify files outside the Sprint Contract scope. If a change requires files not listed in scope, Generator must request scope expansion from Evaluator → Evaluator escalates to orchestrator → user confirms.
- Evaluator must not fix code directly. Evaluator only provides feedback — Generator implements fixes.

**Communication control:**
- Generator and Evaluator must communicate only about the current Sprint Contract. No side tasks, no "while we're at it" additions.
- All SendMessage content must reference specific Sprint Contract criteria or specific code locations. No vague feedback like "looks good" or "needs improvement."

**Iteration control:**
- Maximum 5 iterations per Sprint before mandatory escalation to Planner. Prevents infinite fix loops.
- Each Evaluator feedback must be more specific than the previous — if the same feedback repeats twice, escalate to Planner.

**Protected operations:**
- Neither Generator nor Evaluator may execute: `git push`, `git commit --no-verify`, delete branches, modify `.git/hooks/`, modify `CLAUDE.md`, or modify `tasks/workflow-*.md`.
- Only the orchestrator (main agent) may commit — after Evaluator approval.

**Observability:**
- Generator must log each implementation change to `tasks/sprint-log.md` (append-only).
- Evaluator must log each review to `tasks/sprint-feedback.md` (append-only).
- Orchestrator can read these files to monitor team progress without intervening.

#### Phase Transition Rules

- **Phase 1→2-3**: After user confirms Sprint Contract, orchestrator creates Team (Generator + Evaluator).
- **Generator ↔ Evaluator**: Direct communication via SendMessage. No orchestrator involvement in the loop.
- **Escalation**: After 3 consecutive Evaluator rejections, Orchestrator re-spawns the Planner for re-planning. At 5 total iterations → mandatory user escalation.
- **Commit**: After Evaluator writes "APPROVED" in sprint-feedback.md, orchestrator runs the Code Review Pass (Orchestrator Role step 7) — only if Code Review produces no Critical/BLOCKING does the orchestrator commit. Critical/BLOCKING → Generator fix-only 1 iter (counted toward 5-iteration cap).
- **Phase skipping**: Pre-commit hook blocks commits without sprint-feedback.md. Only Evaluator writes sprint-feedback.md.

### When 3-Tier Applies

| Task Type | 3-Tier Required? | Notes |
|-----------|-----------------|-------|
| New feature | **Yes** | Full Sprint Contract |
| Bug fix (complex) | **Yes** | Sprint Contract with reproduction steps as criteria |
| Bug fix (simple, 1 file) | **No** | State exception reason, skip to Phase 2 |
| Refactoring (3+ files) | **Yes** | Sprint Contract with before/after quality criteria |
| Formatting, typo, log | **No** | State exception reason, skip to Phase 2 |
| Design document changes | **Yes** | Sprint Contract focused on document quality |

## Iteration Loop

```
Plan → Build → Test → Evaluate → Feedback → Fix → Re-evaluate → ...
        └──────────────── Repeat until passing ────────────────┘
```

### Inter-Agent Handoff Format
- Planner → Generator: Sprint contract
- Generator → Evaluator: Change summary + test results + runnable state
- Evaluator → Generator: Specific feedback in tasks/sprint-feedback.md

- After 3 **consecutive** rejections (PASS resets the counter; non-consecutive FAIL patterns do not trigger re-planning), the Orchestrator stops the team and escalates to the Planner to re-examine the plan itself. All three agents already run on the best model (`opus`), so there is no model-tier fallback — 3 consecutive failures signal a planning problem, not a capability gap.
- On escalation, the Planner must check:
  - Is the scope too broad? → Split into smaller tasks.
  - Are the assumptions wrong? → Re-verify prerequisites.
  - Are there technical constraints? → Explore alternative approaches.
  - Are the evaluation criteria unrealistic? → Adjust criteria with justification.
- If still failing after 1 additional attempt post re-planning, report to the user and request a decision. Never enter an infinite loop.
- **5-iteration cap**: Total iterations must not exceed 5 before mandatory user escalation.

## Feedback Management

- **tasks/sprint-feedback.md**: Records evaluator feedback. Operated per sprint.
- **tasks/feedback-archive.md**: Key lessons archived on sprint completion. Permanent storage.

## Templates Reference

Agent definition templates, file templates (tasks/), project document templates (docs/), and the project CLAUDE.md skeleton are in `templates/reference/project-templates.md`. Loaded during Session Start Phase 3 (project initialization), not during sprints.
