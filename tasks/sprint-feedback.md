# Sprint Feedback — S-AUDIT-E-DOCS

(Evaluator가 문서/설정 정확성 단일 차원 검증 → APPROVED/FAIL. ① CLAUDE.md는 구현자=main·S6로 검증.)

---

## APPROVED — 2026-08-03 (Evaluator, 독립 검증·fresh evidence)

전 항목 지상진실 1:1 정합, 회귀 0, 코드 로직 diff 0(주석/문서/dev-config만), 스코프 준수. 6개 정정 파일(③⑤⑥=Generator, ①=main) 모두 통과. 근거는 아래 — 전부 이번 세션 fresh tool output(git diff·grep 재스캔·dotnet build/test·콜드스타트 실증·node JS parse).

### E-③ FULL/PAUSED 무조건-NG → 타입 분기 (PASS)
- **지상진실 대조**: 실코드 `RcsController.QueryDestination`(RcsController.cs:91-119) — `if (dt != DestinationType.Sorter3D) return DestinationBlock.None;`(슈트 full/paused 통과·OK) / 소터는 `Paused→차단`, else `SorterCanAcceptBarcode ? None : Full`. 정정문(슈트=IF-05 OK·보내고 대기, 소터만 PAUSED·셀만재 NG)과 의미 정확 일치.
- **정정문↔canonical 정합**: master_spec §05(:211/216/220)·interface_kr §6(:264)·영문 interface(:281)가 이미 동일 타입 분기 — 정정된 unified_sequence.html:194-196 + master_spec:263(§08)이 이들과 문구·의미 일치.
- **★ 독립 전수 재스캔**(`grep -rniE "FULL.{0,60}PAUSED|PAUSED.{0,60}FULL" docs/*.html`, 6개 html): 무조건-NG 잔존 = **0건**. 이번 스프린트가 손댄 2건(unified:194-196, master_spec:263/§08)이 유일한 무조건-NG였고 둘 다 정정됨(Generator 재스캔 주장을 독립 재스캔으로 확인). 나머지 FULL/PAUSED 동시출현은 전부 비-NG 맥락 — TgtFloor 쓰기 정책(3ds_interface:220/339·master:286 "쓰지 않고 대기"), WCS 판단 서술(:138/338·master:285), IF-08 푸시(:327·master:225/230), §09 예외표 중립 "IF-05/IF-08에서 처리"(master:276), 파킹존 우회(unified:207) — 무조건 NG 단정 없음.
- **§05 무접촉**: git diff상 master_spec 변경 hunk는 line 263(§08 `<section id="s8">`) 단 1곳. §05(:208-224)는 -/+ 없음 → 무편집 확인. Generator의 §08 추가 정정은 완료조건("docs/*.html 무조건-NG 잔존 0"·"전 문서 동일 진실")에 부합·§05 무접촉이라 정당.
- **HTML well-formedness**: master_spec:263 태그 균형(`<b>`3/3·`<code>`4/4·`<li>`1/1). unified:194-196 JS 문자열 single-quote parity 전부 짝수(32/20/12). `node`로 `const S=[...]` 배열 전체 파싱 성공 → 편집이 시퀀스 JS를 깨지 않음.

### E-⑤ dev 시드 chuteNo↔Sorters 단일 진실 정렬 (PASS — 콜드스타트 실증)
- **단일 진실 대조(S1)**: DbSeeder.cs SORTER_3D chuteNo=**30**(코드값 :61 무변경, 주석만 정정 — :32/:54-56이 실코드·:186와 정합). appsettings.Development.json에 dev 전용 `Sorters:[{ChuteNo:30, Transport:Tcp}]` 오버라이드 추가 → .NET 인덱스0 병합으로 base(ChuteNo:1→30·Transport:Rtu→Tcp)만 덮고 Host/Port(127.0.0.1:1502) 상속. base appsettings.json Sorters[0].ChuteNo=**1** 무변경(확인).
- **fail-loud 로직 확인**: Program.cs:509-518 — 각 SORTER_3D dest를 `configByChuteNo.TryGetValue(dest.ChuteNo)` 조회, 미스매치 시 `LogCritical + throw InvalidOperationException("appsettings Sorters[] 항목 없음 — 기동 불가")`. base만이면 configByChuteNo={1}, 시드 chuteNo=30 → 미스매치 → fail-loud. dev 오버라이드로 {30} → 매칭.
- **★ 콜드스타트 직접 재현(S2/E1 · fresh)**: `ASPNETCORE_ENVIRONMENT=Development` + `--Database:Provider=Sqlite --Database:SeedOnStartup=true --Database:MigrateOnStartup=true --ConnectionStrings:WcsDb=<temp>` 로 빌드 dll 실행(port 5399). 로그:
  - `[DbInitializer] 콜드스타트 자동 Migrate 시작(provider=Sqlite)` → `Migrate 완료`
  - `[DbInitializer] dev 시드 실행됨(트리거: Database:SeedOnStartup=true)`
  - `[SorterRegistry] SORTER_3D destination 1대 조회` → `공유 버스 구성 busKey=127.0.0.1:1502 멤버 1대 transport=Tcp` → **`[SorterRegistry] 초기화 완료 — 소터 1대 / 공유 버스 1개`(fail-loud 0·throw 없음)**
  - `Now listening on: http://127.0.0.1:5399` + `Application started` + `Hosting environment: Development` → **기동 완주**
  - 이후 `Could not connect...`(Sim 미기동) → `[SorterRegistry] OFFLINE alarm: destId=6 chuteNo=30` = 문서화된 fail-safe(지연 Open·소터만 OFFLINE). transport=Tcp·busKey=127.0.0.1:1502 = 오버라이드/상속 정확 반영.
- **회귀 blast radius = 0**: DbSeeder chuteNo=30 코드값 무변경이라 chuteNo=30 전제 자산(DbSeeder 의존 테스트·seed-field SQL·Field20Cells) 무영향. 테스트는 Production 환경+자체 주입 config → Development.json Sorters 오버라이드 미로드(blast=0). 전체 534 GREEN으로 입증.

### E-⑥ 테이블수·M3 구모델 주석 (PASS)
- TASKS.md:41 16→**17**(ERD.md 헤딩 `## 테이블 (17)` 정합·확인). :33 M3 헤딩 아래 "⚠ 구 모델(당시 기록)…현행 IF-08=WCS→RCS 상태 푸시(2026-07-21·SPEC §06)" 블록쿼트 보존형 주석(Q4 — 재작성 아님·폴링 오인 방지).
- WcsDbContext.cs:7 16→**17** · :24 "16 코어 + operation_log = 17 테이블 · ERD.md" — 내부 :44("17번째 테이블")·:48/:698("기존 17테이블")과 정합. 임의 수치 도입 0. (B2B 6테이블은 docs/B2B-SCHEMA.md 별도 스키마·":48 완전 분리" 명시라 ERD 17 카운트와 무충돌.)

### E-① CLAUDE.md (구현자=main·S6) (PASS)
- L35 포인터 → "§05 IF-05 사유 표 = 판정 스펙 · §06 IF-08 상태 푸시 정의"(§05/§06 실문서 정합).
- 솔루션 구조에 `Wcs.Migrations.SqlServer`(:45)·`Wcs.Migrations.Sqlite`(:46) 표기 — 두 프로젝트 물리 실재 확인(`ls backend/src/Wcs.Migrations.*`).
- 기정정 유지: L43 MVC Controllers IF-05/09/10 + IF-08 상태 푸시 ✓ / L33 17테이블 ✓ / L66·68 Serilog 도입 완료 ✓. 재-drift 0.
- L48 "처음엔 RED가 정상" stale 제거 → "Decide 판정·ToWire 전부 GREEN — 구현 완료"(grep "RED" CLAUDE.md = 0건).

### 회귀/규칙 (PASS)
- **build**: `dotnet build backend/Wcs.sln` = **0 오류**, 경고 10개 전부 NU1903(SQLitePCLRaw 2.1.10 advisory·전 프로젝트 공통·Migrations 2종 포함) = 선재·신규 0.
- **test**: `dotnet test backend/Wcs.sln`(독립 재실행) = **534/534 GREEN**(실패 0·건너뜀 0·1m30s·exit 0). Generator가 경고한 N1 TraceLog E2E flake 미재현(내 단일 full-run에서 GREEN). 귀속 근거: .cs diff 전부 `//` 주석(IL byte-identical)·테스트 Production 환경 Development.json 미소비·선재 flake 교훈 다수(s9-flake·sim-timeline-race·e2e-parallel-load). 단일 RED 부재로 회귀 0 확정.
- **코드 로직 diff 0**: DbSeeder.cs·WcsDbContext.cs 변경 전부 주석 라인(git diff 확인) → IL 무변경. appsettings.Development.json은 dev-config(Q1 승인 범위). #7(하드코딩 금지)·#8(순수 함수) 위반 없음.

### 스코프 준수 (PASS)
- git diff 변경 파일 = CLAUDE.md(①=main)·TASKS.md·appsettings.Development.json·DbSeeder.cs·WcsDbContext.cs·wcs_3ds_unified_sequence.html·wcs_rcs_3ds_master_spec.html(§08만) + tasks/*(프로세스).
- 무접촉 확인: README.md(변경목록 부재)·appsettings.Development.json ④ SeedOnStartup 주석(diff상 context)·master_spec §05·base appsettings.json Sorters(ChuteNo=1). Generator는 CLAUDE.md 무접촉(①=main).

**결론: APPROVED.** 정확성·회귀0·재-stale0·스코프 전 축 충족.
