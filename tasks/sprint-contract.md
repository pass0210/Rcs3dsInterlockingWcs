[Sprint Contract] — S-AUDIT-E-DOCS (2026-07-01 전체 감사 묶음 E — 문서 일괄 정정)
Base: 최신 develop = 99d8038. feature 브랜치 feat/audit-e-docs.

════════════════════════════════════════════════════════════════════
## ★ RE-TRIAGE 결과 (현재 코드·문서 직접 판정)
════════════════════════════════════════════════════════════════════
SCOPE OUT — 정정 확인(Verification 재확인만):
- ② README stale → 해소(README.md:19-20 IF-08=푸시·폐지 폴링 명시·:8/:21 RTU/TCP·:55/:92 운영 SQL Server/개발 SQLite·:93 17테이블).
- ③ master_spec §05 → **master_spec.html은 해소**(:211/216/220 타입 분기·note interface_kr §6 정합).
- ④ appsettings.Development.json 주석 → 해소(:2 "launchSettings 부재로 기본=Production" 정확).

STILL VALID (IN SCOPE):
- ③-잔여: **docs/wcs_3ds_unified_sequence.html:194-196** 무조건 "FULL·PAUSED는 NG"(타입 분기 미반영·canonical 스펙 소스·코드/master_spec §05/interface_kr §6와 충돌).
- ⑤: **Dev 시드 chuteNo 미스매치 — 설정/시드 결함**(문서 아님). 자동-오염 벡터는 CLOSED(DbSeeder 명시 게이트). 근본 잔존: DbSeeder.cs:56-68 SORTER_3D chuteNo=30 ↔ appsettings.json:38 Sorters[0].ChuteNo=1 → 명시 SeedOnStartup=true 기동 시 SorterRegistry fail-loud. (feedback-archive:564·todo:18 추적.)
- ⑥: TASKS.md:41 "16테이블"(→17)·:34-38 M3 폐지 IF-08 폴링 모델 / WcsDbContext.cs:7 "16테이블"(내부 :44/:48/:698은 17).

ORCHESTRATOR-ONLY (Generator 편집 불가·안전규칙 "modify CLAUDE.md 금지" → main 직접):
- ① CLAUDE.md **대부분 정정됨**(L43 MVC·IF-05/09/10·IF-08 푸시·L33 17테이블·L66 Serilog 완료 ✓). 잔여: L35 "§6 투입 가부 표=판정 스펙"(→§05 IF-05 사유 표·§06 IF-08 푸시)·솔루션 구조에 Wcs.Migrations.SqlServer/Sqlite 미표기·(선택)L46 M0 "RED 정상" 잔재.

════════════════════════════════════════════════════════════════════
## Goal
묶음 E 잔여 유효만 정정해 "문서/설정 = 실제 코드 동작" 단일 진실 회복: (1) canonical HTML FULL/PAUSED stale 제거(③-잔여), (2) Dev 시드 chuteNo↔Sorters 정렬로 명시 dev 시드 기동 복원(⑤·회귀 0), (3) TASKS.md·WcsDbContext 주석 정정(⑥). 각 정정은 코드/설정 실제 동작과 **정확 일치**(over-claim·재-stale·새 규칙 도입 금지). ① CLAUDE.md 잔여는 main이 별도 수행(Verification 포함·구현자=main).

## Implementation Scope (Generator — CLAUDE.md 제외·③-잔여·⑤·⑥만)
- **E-③** docs/wcs_3ds_unified_sequence.html:194-196 — 무조건 "FULL·PAUSED NG"를 타입 분기로 정정(슈트 FULL/PAUSED=IF-05 OK·보내고 대기·readiness는 IF-08 푸시 / 3D 소터만 PAUSED·셀만재 NG). 이미 정합된 master_spec §05·interface_kr §6·실코드(RcsController.QueryDestination)와 표현·의미 일치. 확정4 범위 내·새 규칙/수치 금지. HTML well-formedness 보존(태그 닫힘 대조).
- **E-⑤** Dev 시드↔Sorters 단일 진실 정렬(설정/시드·코드 로직 무변경) — 명시 dev 시드가 fail-loud 없이 기동되게. 방식은 Q1 확정. 부수: DbSeeder 주석·appsettings 주석 정합. ★ **회귀 0(#7·#8)**: chuteNo=30 전제 자산(DbSeeder 의존 테스트·seed-field-*.sql·0701-CELL 검증) blast radius 열거+전 테스트 GREEN. 넓은 파급이면 최소-침습 대안.
- **E-⑥** TASKS.md:41 16→17·:34-38 M3 IF-08 폴링→현행 푸시 모델(Q4)·WcsDbContext.cs:7 16→17(내부 정합·임의 수치 금지).

## Orchestrator(main) Scope — ① CLAUDE.md (Generator 금지·Verification 포함)
- L35 §6 포인터→"§05 IF-05 사유 표(판정)·§06 IF-08 푸시". 솔루션 구조에 Wcs.Migrations.SqlServer/Sqlite 추가. (선택)L46 정리. 기정정 L43/L33/L66 유지 확인.

## Parallel Modules: N/A (single). ## Evaluation Dimensions: functional/accuracy only(단일·⑤ 회귀0은 이 축 내 게이트).
## Detected Project Type: Full-stack (단 본 스프린트 프론트 무접촉·API 런타임 무변경 — 문서+백엔드 시드/설정).

## Verification Scenarios
- Web/UI: N/A(frontend/wwwroot 무접촉·사유 명시).
- Backend/API(시드/설정 부트스트랩 + 주석 대조):
  · S1[⑤] DbSeeder SORTER_3D chuteNo == appsettings Sorters[].ChuteNo(단일 진실) 코드/설정 대조.
  · S2[⑤] 명시 SeedOnStartup=true + dev Provider/ConnectionStrings 오버라이드 콜드스타트 시 SORTER_3D가 매칭 Sorters[]로 SorterRegistry fail-loud 없이 provision.
  · S3[③] unified_sequence.html:194-196 정정문 ↔ 실코드(슈트 full/paused OK·소터 PAUSED·셀만재 NG)·master_spec §05·interface_kr §6 일치. docs/*.html 무조건-NG 잔존 0 재스캔.
  · S4[⑥] TASKS.md 테이블수==ERD 17·IF-08 폴링 오인 소지 제거.
  · S5[⑥] WcsDbContext.cs 주석 테이블수 ERD 17·내부 L44/L48/L698 정합.
  · S6[①·구현자=main] CLAUDE.md L35 포인터 §05/§06 정합·솔루션 구조 Migrations 2종·기정정 L43/L33/L66 유지.
- E2E(2+ 계층): E1[⑤] appsettings(Provider/ConnectionStrings/Sorters)→DbInitializer 시드(SORTER_3D)→SorterRegistryFactory 매칭→app 기동. 정정 전 fail-loud→정정 후 완주. 회귀 게이트=DbSeeder 의존 테스트+전체 dotnet test GREEN.

## Evaluation Criteria (가중)
- 정확성 45%(정정문↔지상진실 1:1·over-claim/폐지모델/임의수치 0·③ 확정4 범위) · ⑤ 회귀 0 25%(blast radius 열거+전 테스트 GREEN·코드 로직 무변경) · 재-stale/일관성 15%(FULL/PAUSED·테이블수·IF-08 모델 전 문서 동일 진실·HTML well-formed) · 스코프 준수 15%(SCOPE OUT ②④·master_spec §05 무편집·Generator CLAUDE.md 무편집·①=main).

## Completion Conditions
- ③-잔여: unified_sequence.html 타입 분기 정정·docs/*.html 무조건-NG 잔존 0·코드 대조 통과.
- ⑤: DbSeeder chuteNo↔Sorters 단일 진실·S2/E1 성립·전체 테스트 GREEN(회귀 0)·코드 로직 무변경.
- ⑥: TASKS.md·WcsDbContext 테이블수 17·IF-08 구모델 정정.
- ①(main): L35 포인터·Migrations 표기·기정정 유지(Verification 통과).
- dotnet build 성공·dotnet test 전건 GREEN(신규 경고 0).

## Open Questions (★ 사용자 게이트 확정 2026-08-03)
Q1/Q2 [⑤] ✅ **appsettings.Development.json에 dev 전용 Sorters[] 항목(ChuteNo=30) 오버라이드 추가**(본 스프린트 포함·최소 침습·시드/테스트 자산 무변경·회귀 0). DbSeeder chuteNo 변경 안 함(30 유지). base appsettings Sorters(ChuteNo=1)도 무변경.
Q3 [③] ✅ **unified_sequence.html:194-196 한정**(+docs/*.html 무조건-NG 잔존 0 재스캔으로 다른 곳 없음 확인).
Q4 [⑥ M3] ✅ **"구 모델(당시 기록)" 주석 보존**(마일스톤 이력 정직성 — 재작성 대신 폐지 모델임을 명시해 IF-08 폴링 오인 방지).
Q5 [① 처리] ✅ **CLAUDE.md 정정 = 오케스트레이터(main) 직접**(Generator 금지·안전규칙). Generator 완료 후 main이 편집→Evaluator S6 검증.

> Planner self-check — Detected project type: Full-stack. Required scenario slots: 8 (Web/UI[N/A], Backend/API S1~S6, E2E E1). All slots filled: yes.
