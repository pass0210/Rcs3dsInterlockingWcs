# Sprint Log — S-AUDIT-E-DOCS

(Generator가 `## IMPLEMENTATION COMPLETE` + 변경 요약 + 검증 결과 기록. ① CLAUDE.md는 오케스트레이터(main) 별도 편집.)

## IMPLEMENTATION COMPLETE (Generator — 2026-08-03)

### 파일별 변경 (6개 파일 · 코드 로직 변경 0 · 문서/주석/dev-config만)

**E-③ FULL/PAUSED 무조건-NG → 타입 분기 정정 (지상진실 = master_spec §05 :211/216/220 · interface_kr §6 :264 · RcsController.QueryDestination)**
- `docs/wcs_3ds_unified_sequence.html:194` (IF-05 det.why) — "FULL·PAUSED는 NG" → "슈트=IF-05 OK(보내고 대기·readiness는 IF-08 푸시) / 3D 소터만 PAUSED·셀 만재면 NG".
- `docs/wcs_3ds_unified_sequence.html:195` (OK 분기 det.res+why) — 내부 사유에 `슈트 FULL·PAUSED` 추가(§05 :220 "OK·진행=NORMAL·BUSY+슈트 FULL/PAUSED"와 일치). why에 슈트 통과·소터만 NG 명시.
- `docs/wcs_3ds_unified_sequence.html:196` (NG 분기 det.res+why) — 내부 사유 `FULL/PAUSED` → `소터 FULL / 소터 PAUSED`. why "둘 다 IF-05에서 막음" → "소터만 IF-05에서 막음(슈트는 OK로 보내고 대기)". OVER/COMPLETED/NO_DEST/OFFLINE 서술 무변경(정정 대상 아님).
- **★ 재스캔 추가 발견 → 정정**: `docs/wcs_rcs_3ds_master_spec.html:263` (§08 핵심 플로우 ① 요약 bullet) — "NG·대기(FULL·PAUSED·…)" 무조건 그룹핑이 §05 타입 분기와 **내부 모순**. re-triage의 "현 조사상 이 1건(unified만)" 전제가 재스캔으로 반증됨(§05는 무편집·§08 요약은 별개 문장). §05 :220 문구에 맞춰 "OK(NORMAL·BUSY+슈트 FULL/PAUSED) / NG(소터 FULL/PAUSED·…) — 타입별 분기(§05)"로 정정. **완료조건 "docs/*.html 무조건-NG 잔존 0" + 평가축 "전 문서 동일 진실"을 위해 §08만 손댐(§05 무접촉 유지).**

**E-⑤ dev 시드 chuteNo 미스매치 해소 (설정만 · DbSeeder chuteNo=30 코드값 무변경 · base appsettings Sorters 무변경)**
- `backend/src/Wcs.Api/appsettings.Development.json` — dev 전용 `Sorters[]` 오버라이드 + `_comment_Sorters` 추가.
  - **오버라이드 shape**: `Sorters:[{ "ChuteNo":30, "Transport":"Tcp" }]`. .NET config 인덱스 병합 → 인덱스0의 ChuteNo(1→30)·Transport(Rtu→Tcp)만 base를 덮고 Host/Port(127.0.0.1:1502)·PollIntervalMs·OfflineAfterFailures 등은 base 상속. ChuteNo=30=시드된 SORTER_3D와 매칭(SorterRegistry fail-loud 해소). Transport=Tcp=dev가 Sim3ds(base Host/Port=127.0.0.1:1502)와 통신하는 vehicle(연결은 지연 Open이라 Sim 미기동이어도 기동 완주·소터만 OFFLINE=fail-safe). base ChuteNo=1(실 현장 슈트번호)·④ SeedOnStartup 주석은 무접촉.
- `backend/src/Wcs.Data/DbSeeder.cs` (주석만) — `:32` "3D Sorter 슈트 3"→"chuteNo=30", `:54-55` 구 stale 주석("chute_no=3 / ChuteNo=3 Sorter3D / 슈트 3번은 SORTER_3D로도 등록")을 실코드·`:185`(TEST-BARCODE-3→ORD-003→ChuteNo=30)와 정합하게 재작성. **코드값 30 무변경**(게이트 지시).

**E-⑥ 테이블수·M3 구모델 주석 정정**
- `TASKS.md:41` — "docs/ERD.md의 16테이블" → "17테이블"(ERD.md:17 헤딩 `## 테이블 (17)`·WcsDbContext 내부 정합).
- `TASKS.md:33` (M3 헤딩 아래) — 구 IF-08 폴링 모델 서술 **보존 + 주석**(Q4): "⚠ 구 모델(당시 기록)… 현행은 IF-08=WCS→RCS 상태 푸시(2026-07-21·SPEC §06)" 블록쿼트 추가(재작성 아님·폴링 오인 방지).
- `backend/src/Wcs.Data/WcsDbContext.cs:7` — "ERD.md 16테이블" → "17테이블". `:24` — "DbSet (16 테이블)" → "DbSet (16 코어 + operation_log = 17 테이블 · ERD.md)"(내부 :44/:48/:698 "17" 정합·임의 수치 도입 0).

### docs/*.html 전수 재스캔 결과 (무조건 "FULL·PAUSED는 NG" 잔존)
- 정규식 `FULL↔PAUSED` 인접 + `FULL/PAUSED↔NG` 근접으로 6개 html 전수 스캔.
- **무조건-NG 잔존 = 2건** → **둘 다 정정**: (1) unified_sequence.html:194-196, (2) master_spec.html:263(§08). ※ re-triage는 (1)만 예상했으나 재스캔이 (2)를 추가 발견.
- **이미 타입 분기(정정 불요)**: master_spec §05(:211/216/220), interface_kr §6(:264), wcs_rcs_interface.html(영문 :281), wcs_3ds_interface.html:220/339·master_spec:286(=TgtFloor 쓰기 정책 "FULL/PAUSED/OFFLINE은 쓰지 않고 대기" — IF-05 NG와 무관), interface_kr:214·master_spec:229(=IF-08 next_state 2/3 상태). api-spec-ko.html=FULL/PAUSED 무매치(clean).

### 검증 기록 (fresh evidence)
- `dotnet build backend/Wcs.sln`: **0 오류**. 경고 13개=전부 NU1903(SQLitePCLRaw 2.1.10 취약성 advisory·전 프로젝트 공통) **선재 경고·신규 0**(변경 전부 주석/문서/config라 컴파일 IL 무변경).
- `dotnet test backend/Wcs.sln`: **534/534 GREEN**(내 변경 적용 상태·1회 클린 통과).
  - ⚠ N1(`E2EGroupN_TraceLogTests.N1_SinglePiece_SixTraceEvents`)이 full 병렬 부하에서 **간헐 실패**(12s WaitUntil 타임아웃 — 전용 trace 파일 비동기 append). 귀속: **선재 부하-타이밍 flake**(교훈 s9-flake-under-e2e-load·sim-timeline-log-vs-snapshot-race·e2e-parallel-load-surfaces-integration-flakes). 근거 4중: ① 격리 재실행 3/3 GREEN(4s), ② full 4회 중 2 FAIL / 2 GREEN(**baseline stash 후 534/534 GREEN 포함** + 내 변경 상태 534/534 GREEN 포함), ③ 내 compiled 변경(DbSeeder/WcsDbContext) `git diff`가 **전부 `//` 주석 라인**=IL byte-identical, ④ 테스트는 Production 환경+자체 주입 config로 구동 → Development.json 오버라이드 미로드. ⇒ 내 변경과 무관·본 스프린트 회귀 0.
- **E-⑤ 콜드스타트 실증(S2/E1)**: `ASPNETCORE_ENVIRONMENT=Development` + `--Database:Provider=Sqlite --ConnectionStrings:WcsDb=<temp> --Database:SeedOnStartup=true --Database:MigrateOnStartup=true`(④ 주석대로 Provider/ConnectionStrings dev 오버라이드) 콜드스타트:
  - Migrate 완료 → dev 시드 적용(chuteNo=30) → `[SorterRegistry] SORTER_3D destination 1대 조회됨` → `공유 버스 구성 busKey=127.0.0.1:1502 transport=Tcp` → **`[SorterRegistry] 초기화 완료 — 소터 1대` (fail-loud 0)** → `Now listening on http://127.0.0.1:5299` + `Application started` + `Hosting environment: Development`.
  - 정정 전이면 `SORTER_3D(chuteNo=30) ↔ appsettings Sorters[](ChuteNo=1)` 미스매치로 `appsettings Sorters[] 항목 없음 — 기동 불가(fail-loud)` 발생했을 것. **정정 후 완주 확인.** (로그: scratchpad/coldstart2.log)
- **E-⑤ 회귀 blast radius**: chuteNo=30 전제 자산(DbSeeder 의존 테스트·seed-field-20cells.sql·Field20CellsGate 등)은 **DbSeeder chuteNo=30 코드값 무변경**이라 무영향. 테스트는 Development.json 미소비(자체 config 주입·Production 환경)라 dev Sorters 오버라이드 blast=0. 전체 534 GREEN으로 입증.

### 스코프 준수
- ① CLAUDE.md **무접촉**(main 처리). ② README·④ Development.json SeedOnStartup 주석·③ master_spec §05·base appsettings Sorters(ChuteNo=1)·DbSeeder chuteNo 코드값(30) **전부 무변경**.
- 커밋/푸시 안 함. Evaluator에 SendMessage 안 함(main이 ① 편집 후 dispatch).
