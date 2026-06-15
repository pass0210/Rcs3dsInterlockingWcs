# Workflow Rules (Detailed)

## Design Document Synchronization (Mandatory)

- Design comes first — never modify code while the design document remains out of date.
- When applying external advice (web Claude, Gemini, etc.), compare with existing design documents and report conflicts.
- On confirmed change: update design document → user confirmation → implement code (strict order).
- Do not create new documents for the same topic — update the existing document directly and record the change date and reason at the top. Create new documents only for entirely new modules or features.
- If multiple documents exist on the same topic, confirm which is canonical and suggest cleaning up the rest.
- Completion checklist: design document ✅ → code ✅ → test ✅ → evaluator pass ✅

## Design Change Response

- If implementation conflicts with the original design, stop immediately and report to the user.
- Do not force-fit code to an outdated design — if direction changes, start clean.
- Preserve pre-change work via commit or branch — keep it available for reference or rollback.
- If the change scope is large, re-enter plan mode and assess the impact first.

### Design Change 6-Step Procedure
```
1. Detect change: When implementation requires a different direction from existing design docs
2. User confirmation: "Design change needed. [Current A] → [Proposed B]. Proceed?" Must confirm
3. Code change: Implement after approval
4. Immediate doc sync: Update relevant module design docs + project CLAUDE.md (if architecture-level)
5. Record change history: Log in docs/CHANGE_LOG.md
6. Lesson reflection: If change was caused by agent mistake/poor judgment, record in tasks/lessons.md
```

### Change Level Operations
- **LEVEL 1** (within module): Update only the relevant module docs. No user confirmation needed.
- **LEVEL 2** (cross-module): Update all related module docs + record in docs/CHANGE_LOG.md. User confirmation required.
- **LEVEL 3** (full architecture): Update project CLAUDE.md + all related modules + record in docs/CHANGE_LOG.md. User must confirm.
- Code changes and document updates must be in the same commit. Separate commits prohibited.

## Record File Classification

- **tasks/lessons.md** — Lessons from user correcting agent mistakes. "Don't do this" patterns.
- **docs/PAIN.md** — Technical troubleshooting. Error → cause → solution. Records the technical problem itself, regardless of who caused it.
- **docs/DECISIONS.md** — Technical choices. Why one option was chosen over others. Intentional judgment, not mistakes.
- When in doubt: mistake → lessons.md, error → PAIN.md, choice → DECISIONS.md

## Template Synchronization

On 3-Tier entry (not session start), **unconditionally copy** the global templates over the project workflow files:
- `cp ~/.claude/templates/workflow-rules.md tasks/workflow-rules.md`
- `cp ~/.claude/templates/workflow-agents.md tasks/workflow-agents.md`

이유: 덮어쓰기는 안전하다. 프로젝트 고유 커스터마이징은 `tasks/workflow-*.md`가 아니라 **프로젝트 루트 `CLAUDE.md`**에 두어야 하고, `tasks/workflow-*.md`는 매 sprint마다 최신 글로벌 규칙으로 리셋되어야 harness 진화가 모든 프로젝트에 전파된다. diff 비교는 불필요 — 항상 덮어쓰기가 운영 기본값. 구현: `skills/3tier-start/SKILL.md` Step 0.

## Context Management

The right information must reach the agent at the right time. Not everything at once — load progressively as the task demands.

### Context Loading Protocol

| Trigger | What to Load |
|---------|-------------|
| Session start | `~/.claude/CLAUDE.md` → project `CLAUDE.md` → `tasks/lessons.md` |
| Entering a module | `docs/modules/{module}/CLAUDE.md` + `design.md` for that module |
| Before implementing | Sprint contract + relevant module design docs |
| Before evaluating | Sprint contract + evaluation criteria + previous sprint feedback |
| Switching modules | Unload previous module context. Load new module's CLAUDE.md + design.md |
| Tech decision needed | `docs/DECISIONS.md` + relevant ADR in `docs/adr/` |
| Error encountered | `docs/PAIN.md` (check if solved before) → `tasks/lessons.md` (check if corrected before) |
| New module | Create `docs/modules/{module}/` directory + apply templates |

### Context Priority (when window is filling up)

Load in this order. Drop from the bottom first when context is tight.

1. **Active task** — Sprint contract, current module design doc, directly relevant code
2. **Guard rails** — `tasks/lessons.md`, project `CLAUDE.md` rules
3. **History** — Previous sprint feedback, CHANGE_LOG, DECISIONS
4. **Reference** — Naming rules, comment rules, other conventions

### CLAUDE.md Size Limit

- Keep project CLAUDE.md under ~200 lines. AI performance degrades sharply beyond this.
- When it grows beyond 200 lines, split topic-specific rules into `.claude/rules/*.md` with glob patterns.
- CLAUDE.md should contain only: project overview, key principles, tech stack, and references to detailed files.

### .claude/rules/ — Topic-Based Rule Splitting

When CLAUDE.md exceeds ~200 lines, split rules into `.claude/rules/` directory.
Claude Code auto-loads these files based on glob patterns — rules load only when relevant, saving context.

| File | Glob Pattern | Loaded When |
|------|-------------|-------------|
| `security.md` | `**/auth/**`, `*.sql` | Working on auth or DB files |
| `testing.md` | `*.test.*`, `__tests__/**` | Working on test files |
| `code-style.md` | `*.ts`, `*.tsx` | Working on TypeScript files |
| `api-design.md` | `**/api/**`, `**/routes/**` | Working on API endpoints |

- Each file should be focused on one topic (~50 lines max).
- Glob patterns ensure rules load only when relevant — this is Progressive Disclosure in practice.
- These override project CLAUDE.md when both cover the same topic.

### Progressive Disclosure

- Start with the minimum context needed to understand the task.
- Expand only when the task requires it — don't preload "just in case."
- When reading code: grep for the relevant symbol first → read only the surrounding function → expand to the file only if needed.
- When reading docs: read the section header/summary first → expand to full content only if the summary is insufficient.

### Context Recording

- When to write: Record in docs/ after the work is done, not during.
- Tech decision: Add one line to `docs/DECISIONS.md`. Write detailed ADR in `docs/adr/` for important decisions.
- Error/troubleshooting: Record in `docs/PAIN.md` + reflect in `tasks/lessons.md` (only if agent's mistake).

## Git Rules

- Commit after each logical unit of work (one feature, one fix, one refactor).
- Never mix unrelated changes in a single commit.
- Commit message format: `type(scope): short description`
- Types: feat, fix, refactor, docs, test, chore
- Always read the current state of a file before editing.
- For large or risky changes: ensure there's a clean commit to revert to.
- File deletion only after user confirmation.
- On design changes, preserve pre-change work via branch.
- Always include docs/ files in commits. Never commit code without its documentation updates.

## Rollback Procedure

- On post-deployment issues, immediately rollback to the last stable commit.
- After rollback, analyze the root cause and record in tasks/lessons.md.
- Hotfixes are worked on a separate branch and merged only after tests pass.

## Time Convention

All timestamps produced by the harness — registry entries, log headers,
sprint-feedback records, `allocated_at` fields, directory names with
embedded times — are **Asia/Seoul (KST) local time**. No timezone suffix
is appended.

- Human-readable form: `YYYY-MM-DD HH:MM:SS` (space between date and time,
  24-hour clock). Example: `2026-04-17 16:31:42`.
- Path-safe compact form (directory and file names where `:` is illegal):
  `YYYYMMDD-HHMMSS`. Example: `screenshots/S-DEV-01_20260417-163142/`.
- Date-only contexts (change log section headings, daily lesson entries):
  `YYYY-MM-DD`.
- Do **not** write `Z`, `+09:00`, `UTC`, or any other timezone marker. The
  harness convention is implicit — all times are Asia/Seoul, always.
- If Claude ever reads a timestamp from an external system that emits UTC
  or another zone, convert to KST before recording.
- **DB-layer caveat — verify server timezone before using `getdate()` etc.**
  Cloud-hosted managed databases (Azure SQL, AWS RDS, GCP Cloud SQL)
  typically run in UTC; `getdate()`, `CURRENT_TIMESTAMP`, `SYSDATETIME()`,
  `NOW()` therefore return UTC on those platforms. Local/on-prem SQL Server
  usually returns OS local time (KST here). Mixing app-layer timestamps
  (KST) with DB-layer server-function timestamps (UTC on cloud) in the same
  table produces silent 9-hour-off rows with no TZ marker — a past project
  had `created_at` correct but `approved_at` off by 9 hours because of this.
  - Before trusting server-side time functions, run `SELECT GETDATE();` and
    compare to the wall clock.
  - Prefer app-layer timestamps (Node/.NET/Python `new Date()`) so the
    source TZ is controlled.
  - If DB-layer is unavoidable and the server is UTC, convert explicitly
    (`SWITCHOFFSET(SYSDATETIMEOFFSET(), '+09:00')`,
    `CONVERT_TZ(NOW(), 'UTC', 'Asia/Seoul')`, or equivalent) before INSERT.
  - Audit any table receiving timestamps from both layers — those are the
    ones at risk.

This convention prioritizes readability for the solo Korean-speaking
developer over international portability. If the harness is ever shared
across timezones, revisit.

## Port Management

When multiple projects run concurrently, port collisions produce silent
failures — the worst being an Evaluator that screenshots the wrong project's
server and reports PASS. Mandatory for any project that starts a dev server,
mock, Redis, Playwright preview, or similar.

- **Stable per-project allocation**: each project owns ports recorded in
  `{project}/.claude/ports.local.json` (gitignored). First-time allocation
  deconflicts against the global live registry; subsequent startups reuse the
  recorded values for muscle-memory stability.
- **Global live registry**: `~/.claude/docs/port-registry.md` tracks which
  PIDs currently hold which ports. Observability + first-time allocation
  deconfliction only — it is not the source of truth for a given project's
  ports.
- **Port injection**: inject allocated ports via `.env.local` (gitignored) or
  CLI flag. Never modify committed config files (`vite.config.ts`,
  `next.config.js`, tracked `.env`, `package.json` scripts). Session-scoped
  port state must not land in git.
- **Collision search limit**: try framework default, then default+1..+10. If
  all 11 are taken → halt and report. Do not pick random ports.
- **Redis**: single shared instance at 6379, projects separated by DB number
  (`redis_db` in `ports.local.json`). Key-prefix namespacing is defense in
  depth, not a replacement for DB separation.
- **Evaluator mandate**: before any browser verification, Evaluator MUST read
  `{project}/.claude/ports.local.json` and construct URLs from the recorded
  port. Hardcoded `localhost:3000` or `localhost:5173` in verification is a
  harness violation — it invites false-PASS on a sibling project's server.
- **Staleness**: before allocating, purge registry rows whose PID is dead or
  whose port is not actually bound.

→ Full protocol, schemas, and examples: `templates/reference/port-policy.md`.

## Dependency Management

- Never add a dependency without verifying it is actively maintained and widely adopted.
- Always commit lockfiles (package-lock.json, yarn.lock, etc.). Never .gitignore them.
- When adding a new dependency: check bundle size impact, license compatibility, and security advisories.
- Prefer dependencies with zero or minimal transitive dependencies.
- Update dependencies in a dedicated commit — never mix dependency updates with feature changes.
- If a dependency update breaks something, revert first, then investigate.

## Protected Zones — Ask Before Touching

- Database schema changes (migrations, table alterations)
- Production configuration and environment files
- Shared libraries consumed by multiple projects
- CI/CD pipeline definitions
- Security-related code (auth, encryption, permissions)
- Logic changes affecting 5+ files (excludes automated changes like formatting and lint fixes)

## Basic Security Principles

- Never hardcode secrets (API keys, passwords, tokens) in source code. Use environment variables or a secret manager.
- Validate all external input. Defend against SQL injection, XSS, path traversal, and other basic attacks.
- For auth/authorization logic, use proven libraries/frameworks over custom implementations.
- Never log sensitive data.

## Error Recovery

- Same approach fails 3 times → stop, re-analyze from scratch, try a different strategy.
- Build error → read the FULL error message, don't guess from the first line.
- If stuck after 3 attempts, report what you tried and ask for guidance.
- Never silently swallow errors or warnings.
- **Systematic Debugging 4-Phase** (3회 실패 시 의무 적용 — `superpowers:systematic-debugging` 흡수):
  1. **Root cause** — 증상 (symptom) 이 아니라 원인 (cause) 을 찾는다. "이걸 바꾸니 동작함" 은 원인이 아니라 우회.
  2. **Pattern** — 같은 결함이 다른 위치에도 있는지 확인. 1건 fix 가 N건 결함의 일부일 가능성.
  3. **Hypothesis** — 단일 변경으로 검증 가능한 가설로 환원. 한 번에 여러 변경 금지 — 어느 것이 효과인지 모름.
  4. **Implement** — 가설 검증 후 최소 변경으로 적용. 효과 측정 → 문서화.
  - **Phase 4.5 (3회 실패 후)**: 가설이 아니라 **아키텍처/전제** 자체를 의심. "이 구조에서 이 기능이 가능한가" 수준의 재질문.

## No Hallucinated Answers

- Do not guess API or library usage when uncertain.
- If you don't know, say so and check official documentation first.
- Never fabricate functions, options, or parameters that don't exist.
- Never move on with "it will probably work" — run it and prove it.

## Knowledge Accessibility

- Documents the agent needs to reference (API docs, external references, style guides, etc.) must be included in the project or have their access paths explicitly specified.
- Knowledge not included in the project does not exist for the agent.
- If external documents are needed, store URLs or copies in a references directory.

## Context Window Discipline

- Never read entire large files — use grep/ripgrep to locate relevant sections first.
- When reading files, use line ranges.
- Summarize findings before moving on.
- If context feels bloated, spawn a subagent for the next subtask.
- Even when context is filling up, never rush or skip steps — spawn a subagent to continue.
- **Threshold**: When context usage reaches ~20-30%, consider starting a fresh session or using handoff. At 100+ exchanges, start fresh.
- See **Context Management** section above for what to load and when.

## Self-Improvement Loop

- After ANY correction from the user: immediately update tasks/lessons.md.
- Write rules for yourself that prevent the same mistake.
- Review lessons at session start.
- tasks/lessons.md records user correction lessons only. Do not confuse with evaluator feedback (tasks/sprint-feedback.md → tasks/feedback-archive.md).

## Testing Strategy

- Before writing new code, check if tests exist for the area you're modifying.
- If tests exist: run them before AND after your change.
- If no tests exist for a critical path: write at minimum a happy-path test.
- For bug fixes: write a regression test that reproduces the bug first, then fix.
- Don't write tests for trivial getters/setters or pure configuration.
- Test command failures count as build failures — don't ignore them.

## When to Ask vs. Act

**Just do it** (no Sprint Contract needed):
- Bug fixes with clear reproduction
- Code formatting, linting, typo fixes
- Adding logs, improving error messages
- Writing tests for existing code
- Simple changes within established patterns (single file, no architectural impact)

**Ask first** (Sprint Contract required for non-trivial cases):
- Changes touching 3+ files or introducing new patterns
- New architectural patterns or libraries
- Database schema changes
- Deleting or renaming public APIs
- Changes that break backward compatibility
- Anything touching the Protected Zones above
- When requirements are ambiguous and two interpretations lead to very different implementations

## Automated Enforcement System

### 1. Linter
- If a linter is configured in the project, always use it.
- Treat linter rule violations as errors — not warnings.
- If no linter exists, suggest an appropriate linter setup for the project.
- Enforced by: `workflow-agents.md` Phase 3 Evaluator table ("All types — Static checks" row).

### 2. Pre-commit Hooks
- Automatically run lint, formatting, and basic tests before each commit.
- If pre-commit hooks fail, do not commit.
- If pre-commit hooks are not configured, suggest introducing them.
- Enforced by: `templates/hooks/pre-commit-3tier.sh` Gate 4 (Static checks).

### 3. Auto-fix Loop
- Linter error → agent self-fixes → re-validate → repeat until passing.
- Resolve automatically without human intervention.
- If unresolved after 3 iterations, report to the user.
- Do not report on linter/test/build/evaluation passes. Only report failures.

## Garbage Collection

### Periodic Cleanup
- Check for mismatches between design documents and actual code.
- Verify no code violates existing rules.
- Identify dead code, unreferenced files, and unnecessary dependencies, and suggest cleanup.

### Architecture Invariant Enforcement
- Enforce architecture boundaries and dependency direction via linter, not documentation.
- Include fix instructions in linter error messages so the agent can correct immediately.
- Connect linter rules to CI so they apply immediately to agent-generated code.
- Pay down technical debt continuously in small amounts before it accumulates explosively.

## Harness Evolution
- Failure → root cause analysis → new rule creation → add to linter rules, tests, and constraints.
- When a bad code pattern is found, block it immediately before it spreads — register the pattern as a linter rule to prevent recurrence automatically.
- When a good code pattern is confirmed, lock it in via linter rule or test to enforce its preservation.
- The harness becomes more refined with each iteration.
- Verify new rules don't conflict with existing rules before adding.
- Success passes silently. Only report failures.

### Promotion Pipeline (Record → Trigger → Infrastructure)

`tasks/lessons.md` and `tasks/sprint-feedback.md` are **records**. Records alone don't prevent recurrence. Promote them to infrastructure when patterns emerge:

- **3 repeated mistakes** (same type in lessons.md or feedback) → promote to a **Rule** in `.claude/rules/` or add to CLAUDE.md. The rule must be automatically enforced, not just documented.
- **3 repeated manual patterns** (same workflow done manually) → promote to a **Skill** in `.claude/skills/`. Automate what you keep doing by hand.
- **Promotion check trigger (per-write, not per-session)**: 기록 시점 self-check — `tasks/lessons.md`에 새 엔트리를 추가할 때, 같은 유형 엔트리가 기존에 2개 있으면 이번이 3번째 → 즉시 Rule 승격. `tasks/sprint-feedback.md`에 같은 유형 이슈를 기록할 때도 동일. 세션 종료 시점에 의존하지 말고 **write 시점마다** 판정. 이유: AI는 세션 종료를 감지할 수 없지만 write 순간은 항상 안다.

## Harness Health Check

Periodically assess whether the harness is helping or hurting.

**Healthy signs:**
- Same instruction is never given twice — it's already in a rule or skill
- The number of rules is stable or decreasing
- Work feels lighter and faster over time
- Unnecessary constraints are being removed

**Warning signs:**
- Tasks are taking longer than before
- Results don't match intent despite detailed instructions
- Skills/agents produce output you can't trust
- Guide files are growing unmanageably large
- You're spending more time configuring the harness than doing actual work

If 3+ warning signs are present: stop adding rules. Audit and simplify.
"Good harness becomes simpler over time."

## Task Management

- tasks/todo.md and tasks/lessons.md are always per-project (project root), never global.
- If the tasks/ directory does not exist, create it automatically.

1. **Plan First**: Write plan to tasks/todo.md with checkable items
2. **Verify Plan**: Check in before starting implementation
3. **Track Progress**: Mark items complete as you go
4. **Explain Changes**: High-level summary at each step
5. **Document Results**: Add review section to tasks/todo.md
6. **Capture Lessons**: Update tasks/lessons.md after corrections
