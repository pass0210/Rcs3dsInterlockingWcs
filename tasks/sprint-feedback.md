# Sprint Feedback — S-BACKEND-FOLDER (.NET 세계 `backend/` 이전 · 순수 이동) — APPROVED

## Phase 3 Evaluate (Evaluator fresh evidence, branch `refactor/backend-folder`, staged, 2026-07-03)

**최종 판정: APPROVED** — 검증 7기준 전부 PASS. 전 증거는 Evaluator가 지금 직접 재실행한 raw tool output.
Generator 요약·"base develop 동일" 주장은 신뢰하지 않고 전부 fresh 재현. 코드 수정·커밋 없음.

핸드오프 마커 확인: `tasks/sprint-log.md:1898` `## IMPLEMENTATION COMPLETE (S-BACKEND-FOLDER)` 존재.

---

### ① 순수 이동 5중 증거 — PASS
- `git diff -M --cached --diff-filter=R --name-only | wc -l` → **75** rename.
- `git diff -M --cached --summary | grep -c "(100%)"` → **75** (전부 R100). 비-100% rename **0**.
- `git diff -M --cached --numstat` non-zero 행 = **오직 참조 7파일**(.gitignore 1/1, CLAUDE.md 11/11, README.md 11/11, docs/FRONTEND.md 8/8, docs/SPEC.md 1/1, frontend/vite.config.ts 2/2, scripts/install-service.ps1 1/1). → **75개 rename 전부 `0  0`**(본문 diff 0).
- `git diff -M --cached --stat` 합계 → **82 files changed, 35 insertions(+), 35 deletions(-)** (= 75 R100 + 7 ref M).
- `git status --find-renames --short` 코드 히스토그램: `R ` ×75, `M ` ×7(참조), ` M` ×2(tasks 산출물), `??` ×1(.claude). **RM/A/D 단독 0** (grep `^RM|^A |^D ` → 0).
- 물리 소멸: 루트 `src/`·`tests/`·`Wcs.sln` **전부 GONE**. `backend/{Wcs.sln, src(7 프로젝트), tests/Wcs.Tests}` 존재 확인. (⚠ `--cached` 사용 — S-FOLDER-ORG 함정4 회피.)

### ② 참조 갱신 7파일 diff 검사 — PASS (경로 문자열 치환만·로직 0)
- `.gitignore` L18: `src/Wcs.Api/wwwroot/` → `backend/src/Wcs.Api/wwwroot/` (그 1줄만; 전역 패턴 불변).
- `docs/SPEC.md` L98: `src/Wcs.PlcGateway/IModbusMaster.cs` → `backend/...` (산문 불변).
- `frontend/vite.config.ts`: L11 주석 + L32 `outDir` → `../backend/src/Wcs.Api/wwwroot` (**`../` 유지** — 함정5 회피 확인).
- `scripts/install-service.ps1` L11: publish csproj → `backend/src/Wcs.Api/...` (`-o C:\BOWOO\Wcs.Api` 배포경로 불변).
- `CLAUDE.md`·`README.md`: 솔루션 구조 블록 + 빌드/테스트/실행 명령 → `backend/src/*`·`backend/tests/*`·`backend/Wcs.sln` 접두 (산문 무재작성, 토큰 치환만).
- `docs/FRONTEND.md`: 8곳(L40·43·44·45·53·58·61·241) 경로 토큰 치환. (git 힌트 `@@ ... src/Wcs.Api/wwwroot/`는 hunk-label 휴리스틱 — 실제 L53은 `backend/src/...`로 확인.)
- 잔존 grep(스코프 한정: 7파일 + scripts + frontend/src): `src[/\\]Wcs|tests[/\\]Wcs|Wcs\.sln` 중 **비-backend 접두 0** (전부 `backend/`). ⑥과 동일 결과.

### ③ 빌드 + 테스트 — PASS
- `dotnet build backend/Wcs.sln --no-incremental` → **오류 0**, 8 프로젝트 전부 `backend/...\bin\Debug\net10.0`로 산출(csproj 무편집으로 SDK가 새 폴더 글로빙 입증). 경과 19.77s.
  - **경고 10 = NU1903** (SQLitePCLRaw.lib.e_sqlite3 2.1.10 known vulnerability advisory, GHSA-2m69-gcr7-jv3q). **NuGet audit 복원 advisory(컴파일 경고 아님)이며 본 스프린트가 도입한 것이 아님** — 경고를 내는 5개 csproj(Wcs.Data/Migrations.SqlServer/Migrations.Sqlite/Api/Tests)가 전부 R100 byte-identical(①의 내 자체 증거)이고 NU1903은 해석된 패키지 버전만으로 결정되므로 폴더 위치와 무관. 계약 §4② 문자 그대로의 "경고 0"은 미충족이나, 실질 의도("SDK 새 폴더 글로빙·0 오류")는 충족. → 근저 취약성은 스프린트 밖 기존 부채로 todo 등재.
- `dotnet test backend/Wcs.sln --no-build` **×3회 연속** → 매회 **실패 0 / 통과 161 / 건너뜀 0 / 전체 161 / exit 0** (기간 14s·12s·14s, 결정적). 1회차 `--blame-hang-timeout 300s` → "시퀀스 파일이 생성되지 않습니다"(teardown 클린·hang/dump 0).

### ④ 프론트 빌드 체인 — PASS
- `cd frontend && npm run build` (tsc --noEmit + vite build) exit 0 → 산출 `../backend/src/Wcs.Api/wwwroot/`: index.html(0.48KB) + assets/index-*.css(21.46KB) + assets/index-*.js(391.43KB). `find` 물리 확인(3 파일).
- 구 `src/Wcs.Api/wwwroot` 재생성 없음(구 트리 소멸·`../` 상대경로가 backend/로 정확 해석). `git check-ignore` → wwwroot ignored, git status에 wwwroot 미출현(오추적 0).

### ⑤ 단일 서버 스모크 — PASS (Production, curl 검증)
- `ASPNETCORE_ENVIRONMENT=Production dotnet run --project backend/src/Wcs.Api --no-build` → `:5080` LISTENING. **ContentRoot = `...\backend\src\Wcs.Api`** / Hosting environment: Production.
- `GET /` → **200 text/html** (SPA `<!doctype html><html lang="ko">` 셸). `GET /monitor` → **200**, 본문이 `/`와 byte-identical(SPA fallback 정상). `GET /api/monitor/sorters` → **200 application/json** `[{"destId":1,"chuteNo":1,"online":false,...}]`. `GET /api/monitor/nonexistent` → **404**.
- IF-05 `POST /api/v1/destination-query` (barcode `0701-CELL-16`) → **`{"result":"OK","chuteNo":1}` 200**.
- COM1 FileNotFoundException 반복 = RTU 시리얼 부재(HW 없음·RTU OFFLINE 예상; IF-05는 DB dispatch라 무관). 종료 후 `:5080` FREE 확인.
- **Playwright 갈음 사유**: 순수 이동 스프린트로 UI 소스(frontend/src) R100 무변경(numstat 0/0), UI 렌더 회귀는 F1(PR #26)에서 이미 브라우저 검증됨. 본 이동이 건드릴 수 있는 유일 위험 표면 = "새 ContentRoot에서 wwwroot 정적 해석 + SPA fallback"이며 이를 curl로 전수 입증(index 셸 200 + /monitor fallback identical + API JSON). 저회귀 R100 UI라 계약이 명시 허용한 curl 갈음 적용.

### ⑥ EF design-time — PASS
- Sqlite: `has-pending-model-changes --project backend/src/Wcs.Migrations.Sqlite --startup-project 동일` → **"No changes have been made to the model since the last migration."**
- SqlServer: 동일 명령(`backend/src/Wcs.Migrations.SqlServer`, project==startup) → **"No changes..." · exit 0**. (dotnet-ef 9.0.10)

### ⑦ 무변경 가드 — PASS
- ①의 unfiltered `--numstat`에서 `backend/` 이동분 non-zero **0건** = 전 이동 `.cs/.csproj/.sln/appsettings*/tests` 본문 diff 0을 커버.
- `git diff --numstat -- '*.csproj' '*.sln'` non-zero → **none**. appsettings 2종(`appsettings.json`·`appsettings.Development.json`) = `rename {src => backend/src}/... (100%)` R100 확인.
- ⚠ 함정 기록: 계약 §4⑦의 `git diff -M --cached -- backend/src/**/*.cs ...` 경로한정 diff는 rename source(`src/`)를 필터로 배제해 **rename 짝맺기가 깨져 add로 오표시**(23940 insertions·"new file"). 이는 **path-filter 아티팩트**이며 실제 변경 아님 — 권위 증거는 ①의 unfiltered pairing(R100·0/0). (후속 스프린트 계약 작성 시 이 명령은 양측 경로 포함 또는 unfiltered numstat로 대체 권장.)

---

## Minor / 후속 (APPROVED 비차단)
- **[S-BACKEND-FOLDER][기존부채] SQLitePCLRaw.lib.e_sqlite3 2.1.10 NU1903 high-severity advisory (GHSA-2m69-gcr7-jv3q)** — 5개 csproj 빌드 경고 10건. 본 이동과 무관·base develop 선재. EF Core Sqlite provider 갱신 또는 명시적 패키지 pin으로 해소 필요. todo.md 등재.
- `.claude/settings.json` 권한 allowlist 구 `src/Wcs.Api/...` 경로 참조(계약 함정6) — 미승인 config·스코프 밖. 영향은 최악의 경우 권한 프롬프트 추가뿐(빌드/실행 실패 아님). 사용자 후속 결정.

## 핸드오프 상태 (검증 후 원복 유지)
- 프로세스/포트 정리: `:5080` FREE, 스모크 서버 kill 완료. 신규 orphan 없음.
- git 상태 불변: 75 R100 staged rename + 7 참조 M(staged) + `tasks/sprint-contract.md`·`tasks/sprint-log.md`( M unstaged·tasks 산출물) + `.claude/`(??). 커밋/브랜치 조작 없음.

## Step 4.5 코드리뷰 — 생략 (orchestrator 판단)

순수 이동 스프린트: 이동분 75파일 전원 R100(내용 0 ins/0 del)이라 신규/변경 코드가 존재하지 않음 — 리뷰 도메인(아키텍처·명명·주석·보안) 무대상. 참조 갱신 7파일은 경로 토큰 치환뿐임을 Evaluator가 diff 직접 판독으로 확인(로직 0). S-FOLDER-ORG 전례 동일. 리뷰 생략이 은폐가 아님을 위해 사유를 여기 명기.
