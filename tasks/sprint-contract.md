# Sprint Contract — S-BACKEND-FOLDER (백엔드 .NET 세계 `backend/` 이전 · 순수 이동)

> 순수 폴더 이동 스프린트. **코드 의미 0 변경.** WHAT/WHERE/검증만 규정 — 구현 메커니즘(정확한 git mv 순서 등)은 Generator 재량.

## 0. 메타

| 항목 | 값 |
|------|-----|
| Sprint ID | S-BACKEND-FOLDER |
| Branch | `refactor/backend-folder` |
| Base | `develop` (= PR #26 병합, F1 프론트 포함) |
| Detected Project Type | **Full-stack** (.NET 백엔드 + Vite/React 프론트) |
| Scaling | **1 Planner / 1 Generator / 1 Evaluator** (순수 이동 — 팬아웃/멀티인스턴스 없음) |
| Test baseline | **161 GREEN** (146 + 신규 15 `MonitoringApiTests` = F1) |
| 선례 | `tasks/feedback-archive.md` **S-FOLDER-ORG**(순수 이동 검증 정석) — 단 이번엔 `Wcs.sln`도 함께 이동(그 스프린트는 sln 위치 유지, 이번엔 트리째 이동) |

## 1. 목표 (WHAT · 한 줄)

.NET 세계 전체(`Wcs.sln` + `src/` + `tests/`)를 **`backend/` 하위로 git rename(R100) 순수 이동**하고, **바깥(frontend/·scripts/·루트 문서)→안** 참조 경로만 갱신한다. `.cs/.csproj/.sln` 및 appsettings 내용은 단 1바이트도 바꾸지 않는다.

확정 목표 구조(사용자 본인 제안 — 재론 금지):
```
backend/
  Wcs.sln
  src/      (Wcs.Core, Wcs.PlcGateway, Wcs.Api, Wcs.Data, Wcs.Sim3ds, Wcs.Migrations.Sqlite, Wcs.Migrations.SqlServer = 7)
  tests/Wcs.Tests   (= 1)
frontend/  docs/  scripts/  tasks/   ← 그대로 (미이동)
```
sln 프로젝트 노드 8개 = src 7 + tests 1.

## 2. Scope IN

### 2A. 이동 (git rename · R100 · 내용 diff 0)
| 이동 전 | 이동 후 | 비고 |
|---------|---------|------|
| `Wcs.sln` | `backend/Wcs.sln` | sln 내부 프로젝트 경로가 `src\Wcs.*\...`(상대) — 트리째 이동으로 **유효 유지, 편집 불요** |
| `src/` (tracked 54파일, 7프로젝트) | `backend/src/` | SDK 암묵 글로빙(`**/*.cs`) → csproj 무편집으로 새 위치 자동 포착 |
| `tests/Wcs.Tests` (tracked 20파일) | `backend/tests/Wcs.Tests` | `Wcs.Tests.csproj`의 `..\..\src\Wcs.*` ProjectReference — `..\..`가 이동 후 `backend/`를 가리켜 **여전히 유효, 편집 불요** |

- 검증 근거: `Wcs.sln`(L6~24 상대경로 `src\...`)·`tests/Wcs.Tests/Wcs.Tests.csproj`(L3~9 `..\..\src\...`) 실독 — 이동 전후 상대관계 불변 확인.

### 2B. 참조 갱신 (바깥→안 · 경로 문자열만 · grep 전수 결과)
| # | 파일 | 위치 | 변경 |
|---|------|------|------|
| 1 | `frontend/vite.config.ts` | L32 `outDir: '../src/Wcs.Api/wwwroot'` | → `'../backend/src/Wcs.Api/wwwroot'` (⚠ `../` 유지 — frontend/ 기준 상대) |
| 1 | 〃 | L11 주석 `../src/Wcs.Api/wwwroot` | → `../backend/src/Wcs.Api/wwwroot` |
| 2 | `.gitignore` | L18 `src/Wcs.Api/wwwroot/` | → `backend/src/Wcs.Api/wwwroot/` (**1줄만**; `*.db`/`logs/`/`frontend/*` 등 전역 패턴은 불변) |
| 3 | `scripts/install-service.ps1` | L11 `dotnet publish src/Wcs.Api/Wcs.Api.csproj ...` | → `backend/src/Wcs.Api/Wcs.Api.csproj ...` |
| 4a | `CLAUDE.md` | 솔루션 구조 블록 L35~40 (`src/Wcs.*`, `tests/Wcs.Tests`) | → `backend/src/*`, `backend/tests/*` |
| 4a | 〃 | 빌드/테스트 명령 L45~49 | `dotnet build`→`dotnet build backend/Wcs.sln`; `dotnet test`→`dotnet test backend/Wcs.sln`; `dotnet test --filter Decider`→`dotnet test backend/Wcs.sln --filter Decider`; `dotnet run --project src/Wcs.Sim3ds`→`backend/src/Wcs.Sim3ds`; `--project src/Wcs.Api`→`backend/src/Wcs.Api` |
| 4b | `README.md` | 구조 표 L33~38 + 빌드/테스트 명령 L52~56 | CLAUDE.md와 동일 패턴 |
| 4c | `docs/FRONTEND.md` | L40, 43, 44, 45, 53, 58, 61, 241 (총 8곳) | 경로 토큰만: `src/Wcs.Api/wwwroot`→`backend/...`, `dotnet run --project src/Wcs.Api`→`backend/...`, `src/Wcs.*` 언급→`backend/src/Wcs.*`. **산문 재작성 금지 — 토큰 최소 치환** |
| 4d | `docs/SPEC.md` | L98 `` `IModbusMaster` 인터페이스(src/Wcs.PlcGateway/IModbusMaster.cs) `` | → `backend/src/Wcs.PlcGateway/IModbusMaster.cs` |
| 5 | `scripts/seed-field-16cells.sql` | — | **SQL — 경로 참조 0(grep 확인 완료). 변경 없음 · 확인만** |

`scripts/uninstall-service.ps1`: `src/` 경로 없음(grep 확인) — 0 변경.

### 2C. 이동 잔여물(고아) 정리
`git mv`는 **tracked만** 이동한다. 구 `src/`·`tests/`에 남는 untracked/ignored 산출물을 정리한다(전부 gitignore 대상 재생성물):
- `src/**/{bin,obj}/`, `tests/**/{bin,obj}/` (빌드 산출물)
- `src/Wcs.Api/logs/wcs-20260701.log`, `wcs-20260703.log` (런타임 로그)
- `src/Wcs.Api/wwwroot/` (구 vite 산출물 — stale)

→ git mv 후 구 `src/`·`tests/` 트리를 삭제(잔존물은 전부 재생성 가능). 신 wwwroot는 검증 ③의 `npm run build`가 `backend/src/Wcs.Api/wwwroot`에 재생성한다.

## 3. Scope OUT (0 변경 — 무변경 가드 대상)

- **모든 `.cs` / `.csproj`**: 내용 0 (SDK 글로빙 + 상대 ProjectReference가 이동을 자동 흡수).
- **`Wcs.sln` 내부**: 0 (내부 `src\...` 상대경로가 트리째 이동으로 유효 유지).
- **`appsettings*.json`**: 0.
- **frontend 소스 로직**: 0 (`vite.config.ts` 경로 2줄만; `package.json` L6 description의 "Wcs.Api"는 **이름**이지 경로 아님 → 무변경).
- **테스트 코드**: 0.
- **`tasks/**`, 루트 `TASKS.md`, `docs/*.html`, audit 문서, `feedback-archive`/`sprint-log`의 과거 기록**: 0 — 과거 실행 기록이므로 갱신 대상 아님(§4 ⑥ 잔존 grep의 **제외** 대상).
- **`.claude/settings.json`**: 0 (config·미승인 — §5 함정 6).
- **`.git/hooks/pre-commit`**: 0 (경로 의존 없음 — grep 확인, `tasks/sprint-contract.md` 존재만 검사).

## 4. Deliverables & Evaluation Criteria (Completion Gate)

> **Fresh evidence 의무**: 모든 PASS는 "지금 실제로 돌린" raw tool output(git 명령 원문 출력·`dotnet build/test` 라인·`npm run build` 출력·curl 응답 본문)을 `tasks/sprint-feedback.md`에 인용. Generator 성공 보고·추정만으론 PASS 금지.

**① 순수 이동 입증 (핵심 · S-FOLDER-ORG 5중 증거)** — 이동 대상 `backend/Wcs.sln`·`backend/src/**`·`backend/tests/**`에 대해:
- `git status --find-renames --porcelain` → 전 항목 `R `(순수 rename), `RM`(rename+modify)·`A`·`D` **0**.
- `git diff -M --cached --stat` → "N files changed, **0 insertions(+), 0 deletions(-)**".
- `git diff -M --cached --numstat` → 전 행 `0  0`.
- `git diff -M --cached --summary` → 전 항목 `rename ... (100%)`.
- ⚠ **`--cached` 필수** (staged 상태에서 unstaged `git diff -M`은 빈 출력 → R100 놓침, S-FOLDER-ORG 함정).

**② 빌드 + 테스트 (baseline 회귀 0)**
- `dotnet build backend/Wcs.sln --no-incremental` → **경고 0 / 오류 0** (csproj 무편집으로 SDK가 새 폴더 글로빙 입증).
- `dotnet test backend/Wcs.sln` → **161 GREEN / 실패 0 / 건너뜀 0 / exit 0**. 동시성 민감 테스트 존재 → **fresh ≥3회 반복** 결정성 + `--blame-hang-timeout`로 teardown 시퀀스 파일 0(teardown 클린).

**③ 프론트 빌드 → backend wwwroot 산출**
- `cd frontend && npm run build` → 산출물이 **`backend/src/Wcs.Api/wwwroot/`**(index.html + `assets/*`)에 생성. `find backend/src/Wcs.Api/wwwroot` 물리 확인. 구 `src/Wcs.Api/wwwroot` 재생성 안 됨(구 트리 삭제 확인).

**④ 단일 서버 스모크**
- `dotnet run --project backend/src/Wcs.Api` → `:5080` LISTENING.
- `curl :5080/` → 200 `text/html`(SPA index.html 셸). `curl :5080/api/monitor/sorters` → 200 JSON. IF-05 POST(바코드) → 정상 응답.
- 전제: 현재 base `appsettings.json` = SqlServer(F1 검증과 동일 field DB). SQL Server 부재 시 dev Sqlite override로 대체 가능(스모크 목적은 "이동 후 호스팅·정적 서빙·wwwroot 해석 무파손" 확인).

**⑤ EF design-time (이동이 디자인타임 발견 무영향)**
- `dotnet ef migrations has-pending-model-changes --project backend/src/Wcs.Migrations.Sqlite --startup-project backend/src/Wcs.Migrations.Sqlite` → **"No changes"**.
- 동일 명령 SqlServer(`backend/src/Wcs.Migrations.SqlServer`, project==startup-project) → **"No changes"**.

**⑥ 참조 갱신 전수 (구 경로 잔존 0)**
- `grep -rnE "src[/\\]Wcs|tests[/\\]Wcs|Wcs\.sln"` 를 **갱신 대상 파일에 한정** — `CLAUDE.md README.md docs/FRONTEND.md docs/SPEC.md frontend/vite.config.ts .gitignore scripts/install-service.ps1` → **구 경로 잔존 0**(전부 `backend/` 접두 확인).
- **제외 명시**(잔존 grep에서 제외 — 과거 기록/미승인 config): `tasks/**`, 루트 `TASKS.md`, `docs/*.html`, `.claude/**`, `feedback-archive`/`sprint-log` 등 이력 인용.

**⑦ 무변경 가드**
- `git diff -M --cached -- backend/Wcs.sln 'backend/src/**/*.cs' 'backend/src/**/*.csproj' 'backend/tests/**'` → rename만, 본문 diff 0.
- `backend/src/Wcs.Api/appsettings*.json` diff 0 · frontend 소스(vite.config.ts 제외) diff 0 · 테스트 코드 diff 0.

## 5. 함정 (Traps)

1. **한글·공백 경로**("…\회사 자료\프로그램\…") — `git mv`·sln 로드·`dotnet`/`npm` 호출 시 경로 항상 인용. (S-FOLDER-ORG 선례에서 정상 동작 확인됨.)
2. **루트에서 `dotnet build`/`dotnet test`가 sln 못 찾음** (MSB1009류) — 이동 후 루트에 sln 없음. 반드시 `backend/Wcs.sln` 명시 또는 `cd backend`. CLAUDE.md/README 명령 갱신(§2B 4a/4b)으로 문서화.
3. **git mv 후 구 `src/`·`tests/`에 untracked `bin/obj/logs/wwwroot` 고아 잔존** — §2C로 삭제. 특히 구 wwwroot는 stale 산출물이고 신 wwwroot는 ③ npm build가 재생성. 로그 2개(`wcs-20260701/03.log`)도 고아.
4. **`--cached` 누락** → R100 미검출(①의 핵심 함정, S-FOLDER-ORG 교훈).
5. **vite 상대경로 오치환** — frontend/ 기준 `../src/...`를 단순히 `src`→`backend/src`로 바꾸면 안 됨. `../`를 **유지**하고 `backend/`만 삽입: `../backend/src/Wcs.Api/wwwroot`.
6. **`.claude/settings.json` 권한 allowlist가 구 경로 참조** — `dotnet build "src/Wcs.Api/Wcs.Api.csproj"`·git-log의 `src/Wcs.Data/...`·`src/Wcs.Api/appsettings.json`. **스코프 밖**(config·미승인). 영향은 최악의 경우 추가 권한 프롬프트뿐(빌드/실행 실패 아님). 사용자 후속 결정 사항으로만 기록.
7. **pre-commit hook** — 경로 의존 없음(grep 확인). `tasks/sprint-contract.md` 존재만 검사하며 tasks/는 미이동 → 이동 무영향.
8. **프로젝트 수 혼동** — "8 프로젝트" = src 7(Core/PlcGateway/Api/Data/Sim3ds/Migrations.Sqlite/Migrations.SqlServer) + tests 1(Wcs.Tests). sln 8 노드 전부 `backend/` 내부 상대경로로 유효.

## 6. Planner Self-Check

- [x] **Scope IN** = 이동 3항목(sln + src + tests, R100) + 참조 갱신 7파일(grep 전수: vite.config·.gitignore·install-service.ps1·CLAUDE.md·README.md·FRONTEND.md·SPEC.md) + 잔여물 정리(2C). seed SQL·uninstall.ps1은 무변경 확인 대상으로 명시.
- [x] **Scope OUT** = `.cs`/`.csproj`/`.sln`/appsettings/테스트/frontend 로직 0 변경 · tasks/TASKS/이력/config/hook 0.
- [x] **검증 7기준** 각각 fresh 명령 + 기대 출력 명시(순수이동·빌드·프론트빌드·스모크·EF·잔존grep·무변경가드).
- [x] **함정 8개** (한글경로·루트sln·고아·--cached·vite상대·claude config·hook·프로젝트수).
- [x] **코드 구현 0** — WHAT/WHERE/VERIFY만. 구현 순서/명령 재량은 Generator.
- [x] **Detected Type** Full-stack, **Scaling** 1/1/1(팬아웃 없음) 명시.
- [x] baseline 161 GREEN 확정(sprint-log #1881: 146+15 = F1 MonitoringApiTests).
- [x] 실독 근거: `Wcs.sln`(상대경로)·`Wcs.Tests.csproj`(`..\..\src`)·`vite.config.ts`·`.gitignore`·`scripts/*.ps1`·`CLAUDE.md` + grep 전수(*.ts/json/md/ps1/html) + 고아 산출물/hook/root-config 확인.
