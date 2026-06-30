# Sprint Feedback — S-FIELD-SEED-16CELLS (실 3DS 16셀 작업 데이터 + 현장 구성, iter2 개정 SqlServer 전부 전환) — APPROVED

## Phase 3 Evaluate (Evaluator fresh evidence, branch `feat/field-16cell-seed`, 2026-06-30)

**최종 판정: APPROVED** — C1~C6 + §6 시나리오 전부 PASS. Generator 요약을 신뢰하지 않고 working tree 그대로(base=SqlServer) `dotnet test` 직접 재실행 + 실 SQL Server sqlcmd 전수 쿼리 + 앱 라이브 기동·실 IF-05 HTTP로 fresh 검증. sqlcmd OK·dotnet 10.0.300·DB `Rcs3dsInterlockingWcs`(localhost) 존재 확인.

### Ground-truth (직접 읽음·실행)
- 브랜치 `feat/field-16cell-seed`. `git diff --stat`: `src/Wcs.Api/appsettings.json`(7) + `tests/Wcs.Tests/{ApiIntegrationTests(6),E2E/E2EInfrastructure(5),RcsPushTests(6),ScenarioTests(10)}.cs` + untracked `scripts/seed-field-16cells.sql`. tasks 문서·`.claude/settings.json`은 하네스/세션 산출물(코드 범위 밖).
- sprint-log.md `## IMPLEMENTATION COMPLETE (S-FIELD-SEED-16CELLS) — iteration 2` 마커 + 실제 diff 존재 확인(거짓 핸드오프 아님). 계약 개정 블록(SqlServer 전부 전환·base appsettings 커밋·테스트 provider 결선만·인메모리 SQLite 더블 유지) 정독.
- IF-05 경로 = `POST /api/v1/destination-query`(RcsController.cs:37), 요청 DTO `pId,agvNo,barcode,inductionNo,qty,timeStamp`(Dtos.cs:11), 응답 `{result,chuteNo}`(절대규칙 #6 일치) 직접 확인.

### C6 (개정·이번 핵심·회귀 0) base=SqlServer로 146 GREEN — PASS
- working tree `src/Wcs.Api/appsettings.json` `"Provider": "SqlServer"`(grep L65 확인)·ConnectionString=`Server=localhost;Database=Rcs3dsInterlockingWcs;...`·`Transport=Rtu`·`SeedOnStartup=false`. 그 상태 그대로 `dotnet test Wcs.sln -p:NuGetAudit=false`:
  → **`통과! - 실패: 0, 통과: 146, 건너뜀: 0, 전체: 146`, exit 0**. iter1의 105실패가 0이 됨(직접 재실행 입증). 인메모리 SQLite 더블로 동작.
- tests diff 정밀 검사: 5개 `WebApplicationFactory` 팩토리 `ConfigureWebHost` 시작에 `builder.UseSetting("Database:Provider","Sqlite")` 1줄+주석씩만 추가. 단언/토폴로지/시드/146수 의미 변경 0(diff 확인).
- 무변경 가드: `git status --porcelain -- src/Wcs.Core src/Wcs.PlcGateway src/Wcs.Sim3ds src/Wcs.Data src/Wcs.Migrations.SqlServer src/Wcs.Migrations.Sqlite docs/ERD.md` = **빈 출력**(보호영역 0줄). src/Wcs.Api는 appsettings.json만 변경.

### C2 스키마(20%) — PASS
- `SELECT COUNT(*) FROM sys.tables WHERE name<>'__EFMigrationHistory'` = **16**(총 17 = 16 도메인 + `__EFMigrationHistory` 비표준명).
- FK delete_referential_action: `NO_ACTION 17`(전부). 비-NO_ACTION FK = **0건**(1785 캐스케이드 흔적 0). 207(존재하지 않는 컬럼) 흔적 0(스키마 정상 생성·DbInitializer Migrate 완료 로그 확인).

### C1 IF-05(최우선 30%) — PASS (라이브 실 HTTP)
- 앱 `ASPNETCORE_ENVIRONMENT=Production dotnet run --project src/Wcs.Api`(base=SqlServer+RTU)로 기동: `[DbInitializer] Migrate 시작(provider=...SqlServer) → Migrate 완료` → `Now listening on http://0.0.0.0:5080` → `Hosting environment: Production`. RTU COM1 미연결로 `FileNotFoundException: Could not find file 'COM1'` → 소터 OFFLINE 전이(예외 삼키지 않고 명시 처리). 기동은 막히지 않음.
- IF-05 실 HTTP 응답(fresh 캡처):
  - `0701-CELL-01` → `{"result":"OK","chuteNo":30}` (HTTP 200)
  - `0701-CELL-08` → `{"result":"OK","chuteNo":30}` (HTTP 200)
  - `0701-CELL-16` → `{"result":"OK","chuteNo":30}` (HTTP 200)
  - 미적재 `0701-CELL-99` → `{"result":"NG","chuteNo":null}` (HTTP 200) — 음성 대조, 가짜 OK 방지 입증.
- 소터 RTU OFFLINE이어도 IF-05가 DB dispatch라 OK 정상(계약 예측 일치). 검증 후 클린 종료(PID kill·포트 5080 해제 확인).

### C3 데이터 정확성(20%) — PASS (실 SQL Server 쿼리)
- 소터 destination: `ChuteNo=30·DestType=SORTER_3D·Floor=NULL·Status=NORMAL·IsActive=1`(1행).
- 셀: 소터 소속 16개·`Capacity=3·Enabled=1·CellNo BETWEEN 1 AND 16` 정확히 **16행**(min1/max16/distinct16).
- work_batch: `WorkDate=2026-07-01·BatchNo=FIELD-16·WaveNo=1·Status=RUNNING`(DbSeeder의 today,'SEED',1과 UQ 비충돌).
- order_item: PlannedQty=3 **16행**, 바코드 집합 = {`0701-CELL-01`..`16`} 정확.

### C4 N↔N + FK 무결성(15%) — PASS
- cell_assignment 총 16건·활성(ReleasedAt NULL) **16건**·released(NOT NULL) 0건.
- N↔N 결정적 매핑(`CellNo=N ↔ OrderNo=0701-CELL-N`) 활성 **16건** 1:1 정확(전체 매핑 표 육안 확인 — CellNo 1→0701-CELL-01 … 16→0701-CELL-16, ReleasedAt 전부 NULL).
- 고아 FK: orphan_cell 0·orphan_order 0.

### C5 멱등(10%) — PASS (스크립트 직접 2회 더 실행)
- `sqlcmd -S localhost -d Rcs3dsInterlockingWcs -E -C -f 65001 -i scripts/seed-field-16cells.sql` **2회 연속** 추가 실행 → 둘 다 exit 0, 중복/키충돌/오류 0. 요약 출력 cells_16=16·items_16=16·assignments_NtoN_16=16.
- 재실행 후 전수: dest30=1·cells=16·batch=1·orders=16·items=16·active_assign=16·piece=0 **불변**.

### 정리(클린 시드 상태 복원)
- IF-05 검증으로 생성된 산물(piece 4=3 RESERVED+1 DENIED·piece_event 8·ReservedQty 합 3) 정리: piece_event→piece 삭제 + ReservedQty/SortedQty 0 복원(QUOTED_IDENTIFIER ON 필요 — filtered index 테이블). 정리 후: **piece=0·piece_event=0·ReservedQty=0·SortedQty=0·cells=16·active_assign=16·items=16**(§4 명세 클린 상태).

### 감점 트리거 점검 — 전부 미발생
보호영역(Core·PlcGateway·Sim3ds·Data·양 마이그레이션·DbSeeder·WcsDbContext·ERD) 0줄 / 테스트 단언·토폴로지·시드·146수 의미 변경 0(provider 결선 1줄만) / 가짜 OK 0(미적재 NG 음성대조 입증) / 멱등 깨짐 0 / 1785·207 흔적 0.

**APPROVED** — C1~C6 + §6 V-DI-1~5 시나리오 전부 fresh evidence로 PASS. 회귀 0.

## Step 4.5 독립 코드리뷰 (orchestrator, opus, 팀 외부) — BLOCKING 0 / MAJOR 0 / MINOR 3

독립 Opus 코드리뷰어가 Evaluator 미커버 영역(아키텍처·명명·주석·유지보수성·의미정확성·보안) 검토 → **BLOCKING 0·머지 가능**. seed SQL 스키마 정합을 SqlServer 마이그레이션 원본(`20260630012916_Initial.cs`)과 직접 대조(일치). 정보성 4건(품질 양호): N↔N가 문자열 파싱이 아닌 **구성(construction) 조인**이라 파싱 취약점 0 / `SET XACT_ABORT ON`+TX 원자성·chuteNo=30 비정상 점유 시 `THROW 50001` fail-loud / `Trusted_Connection`로 시크릿 평문 0 / seed 헤더 자기설명 우수.

### Minor (비차단 — 전부 후속·범위 밖, 다음 sprint Generator 참고)
- **MINOR-1 (유지보수)**: 5개 WebApplicationFactory에 `builder.UseSetting("Database:Provider","Sqlite")` 1줄+주석 **중복**(`ApiIntegrationTests.cs:72`·`E2E/E2EInfrastructure.cs:160`·`RcsPushTests.cs:189`·`ScenarioTests.cs:113·1091`). 메커니즘은 정확(minimal-API에서 `ConfigureAppConfiguration`/디스크립터 제거는 무효 — `UseSetting`만 builder 생성 시점 반영). → 공통 베이스 팩토리/헬퍼(`ForceInMemorySqlite`)로 추출 권고(별도 정리 sprint).
- **MINOR-2 (테스트 커버리지 사각·중요)**: base=SqlServer + 전 팩토리 SQLite 강제 → 제품 `Program.cs:49-63`의 **SqlServer 부팅 분기(provider 선택·MigrationsAssembly 결선)를 어떤 `dotnet test`도 안 봄**. 회귀가 라이브 기동에서만 발현(이번엔 Evaluator가 라이브 C1·C2로 보강했으나 CI 자동화 빈틈). → SqlServer(LocalDB/Testcontainers) smoke 테스트 1개 후속 추가 검토.
- **MINOR-3 (dev/sim DX)**: base가 SqlServer+RTU라 `appsettings.Development.json`이 Provider/Transport를 오버라이드 안 하면(현재 SeedOnStartup만) 로컬 dev·Tcp 시뮬레이터 워크플로가 명시 오버라이드 필요. → `appsettings.Development.json`에 `Provider=Sqlite`·`Transport=Tcp` 추가 후속 검토.

→ MINOR 3건 전부 후속(범위 밖) → Step 5 커밋 진행.

---

# Sprint Feedback — S-M5-P1 (콜드스타트 프로비저닝 + Windows Service 호스팅) — APPROVED

## Phase 3 Evaluate (Evaluator fresh evidence, branch `feat/m5-coldstart-hosting`, 2026-06-29)

**최종 판정: APPROVED** — 5개 평가 차원 전부 PASS. generator "146/146" 주장을 신뢰하지 않고 fresh build + 3회 연속 테스트 + 라이브 콜드스타트 2종(Dev/Prod) 직접 재현으로 검증. SDK net10.0(10.0.300) 설치 확인.

### Ground-truth (직접 읽음·실행)
- 변경 파일(`git diff HEAD --stat`): `Program.cs`(+20)·`Wcs.Api.csproj`(+4)·`appsettings.json`(+6) + 신규 untracked `src/Wcs.Api/Startup/DbInitializer.cs`·`appsettings.Development.json`·`scripts/{install,uninstall}-service.ps1`. 계약 §3.6 무변경 가드 범위와 일치.
- DbInitializer·Program.cs·appsettings(.Development).json·scripts·DbSeeder·5개 테스트 팩토리 DB 배선 직접 정독.

### ① 콜드스타트 자동 provision 정확성 (30%) — PASS (라이브 직접 재현)
- **Development 콜드스타트**(빈 temp 디렉터리·`ASPNETCORE_ENVIRONMENT=Development`·temp DB 경로·`--Urls=http://127.0.0.1:5099`, `dotnet run --no-build`):
  로그 시퀀스 fresh 인용 = `[DbInitializer] 콜드스타트 자동 Migrate 시작 (provider=...Sqlite)` → `Migrate 완료 — 스키마 보장됨` → `dev 시드 적용됨 (트리거: Database:SeedOnStartup=true)` → `[ChuteCapacity] 인메모리 집계 초기화 완료. 슈트 수=6` → `[SorterRegistry] SORTER_3D destination 1대 조회됨` → 소터 폴링 시작 → `Now listening on: http://127.0.0.1:5099` → `Application started`. `coldstart.db`(+wal/shm) 파일 생성 확인.
- **직전 크래시 해소 입증**: `ChuteCapacityService`(직전 E2E `no such table: chute_detail` 사망 지점)가 정상 초기화. 로그 grep `no such table|Unhandled exception|SqliteException` = **0건**. Modbus `Could not connect`(시뮬레이터 미기동·예상 노이즈)·그에 따른 OFFLINE alarm만 존재.
- **라이브 IF-05 실 HTTP**(`POST /api/v1/destination-query`): `TEST-BARCODE-1`(chuteNo int pId) → `{"result":"OK","chuteNo":1}` HTTP 200 / `TEST-BARCODE-3` → `{"result":"OK","chuteNo":30}`(소터) / `NOPE-NOPE` → `{"result":"NG","chuteNo":null}`. 핸들러 로그 3건 정상 기록. → VS-1·VS-2 충족.

### ② 기존 테스트 회귀 0 (30%) — PASS
- `dotnet build Wcs.sln -p:NuGetAudit=false` → **경고 0 / 오류 0**(코드 경고 0 확인). NU1903은 SQLitePCLRaw transitive audit 노이즈(선재·EF Sqlite 상시 끌어옴·코드 무관) — generator 주장과 일치.
- `dotnet test Wcs.sln --no-build --blame-hang-timeout 180s` **3회 연속**: RUN1/2/3 = **실패 0·통과 146·건너뜀 0·전체 146·exit 0**. blame 시퀀스 파일 미생성(teardown 클린·hang 0). baseline 146 실측 일치·회귀 0. (S9·IT4b flake history 고려해 반복 실행 — 3/3 결정적.)
- **테스트 in-memory 경로 무파손 입증(코드+동작)**: 5개 팩토리 전부 `Data Source=...;Mode=Memory;Cache=Shared` + `EnsureCreated()` + `DbSeeder.Seed()` 패턴(grep 확인). `DbInitializer.IsInMemorySqlite`가 `SqliteConnectionStringBuilder.Mode==Memory`로 정확히 이 경로를 감지→Migrate·시드 전부 no-op. 테스트 배선 `git diff` = 0줄(VS-3 충족).

### ③ 호스팅 조건부 (15%) — PASS
- `builder.Host.UseWindowsService()`(Program.cs:27) + `Microsoft.Extensions.Hosting.WindowsServices` 9.0.5(csproj) 확인.
- 비-서비스 컨텍스트 no-op 입증: 콘솔 `dotnet run`이 Dev·Prod 양쪽에서 `Now listening`/`Application started`로 정상 기동(서비스 컨텍스트 에러 0) + WebApplicationFactory 테스트 146 GREEN. `UseWindowsService`는 `WindowsServiceHelpers.IsWindowsService()==false`면 no-op(VS-4 충족).
- 서비스 등록 스크립트 `scripts/install-service.ps1`(sc.exe create·start=auto·failure 재시작·Environment 멀티스트링 주입·플레이스홀더+주석)·`uninstall-service.ps1`(stop→delete·미존재 안전) 존재.

### ④ dev 시드 게이트 (운영 안전) (15%) — PASS (라이브 직접 입증)
- **Production 콜드스타트**(빈 temp DB·`ASPNETCORE_ENVIRONMENT=Production`·`--Urls=...5098`):
  로그 fresh 인용 = `Migrate 완료 — 스키마 보장됨` → `시드 게이트 off(운영 안전) — 빈 스키마만 프로비저닝` → `[ChuteCapacity] ... 슈트 수=0` → `[SorterRegistry] SORTER_3D destination 0대 조회됨` → `Now listening`/`Application started`. 크래시 0(`no such table` 등 0건).
- **테스트 시드 미삽입 직접 입증**: Prod에서 IF-05 `TEST-BARCODE-1` → `{"result":"NG","chuteNo":null}`(Dev는 OK/chuteNo=1) — 동일 시드 바코드가 Prod에선 미해석 = 시드 오더 부재. DB 바이너리 스캔 교차검증: `chute_detail` 스키마 테이블명은 Dev·Prod WAL 양쪽 11회(스키마 둘 다 마이그레이션됨), `TEST-BARCODE`는 Dev WAL 33회 vs Prod 사실상 0(IF-05 NG로 확증). 스키마는 생성·테스트 시드만 차단(VS-5 충족).
- 게이트 키 외부화: `Database:MigrateOnStartup`(기본 true)·`Database:SeedOnStartup`(appsettings.json 기본 **false**=운영 안전, `appsettings.Development.json`만 true 오버라이드). 하드코딩 0·`_comment_`로 문서화(절대규칙 #7 준수).

### ⑤ 재시작 레지스터 재독 동기화 (10%) — PASS ("이미 충족" 근거 코드 확인)
- `PlcPollingService._latest`(PlcGateway.cs:98-99)는 생성 시 **`Online:false`** fail-safe 스냅샷으로 초기화. `StartAsync`(173-178)가 `RunPollLoopAsync`를 띄워 매 `PollIntervalMs`마다 레지스터 재독→`_latest` 덮어씀. `_latest`는 in-memory `volatile` 필드 → 재시작 시 stale 잔존 불가, 첫 폴 전 요청은 `Online=false`(보수적)를 봄. generator "이미 충족·새 메커니즘 0" 판정은 코드에 근거함. 추측 신규 메커니즘 0(VS-6 충족).

### 무변경 가드 (§3.6) — PASS
- `git status --short -- src/Wcs.Core src/Wcs.PlcGateway src/Wcs.Data src/Wcs.Sim3ds src/Wcs.Migrations.Sqlite src/Wcs.Migrations.SqlServer tests/` = **빈 출력**(전 가드존 0변경). 새 마이그레이션 생성 0. `Program.cs` diff는 using 1·UseWindowsService 1·ProvisionAsync 1줄·주석으로 국한(SorterRegistryFactory/핸드셰이크 본문 무변경). appsettings diff는 게이트 키 2 + 주석만. API 필드/엔드포인트 무변경.

### 감점 트리거 점검 — 전부 미발생
새 마이그레이션 0 / 테스트 배선 0줄 / DbSeeder 토폴로지 0 / 운영 테스트 시드 무조건 삽입 0(Prod off 입증) / 판정·Modbus맵·핸드셰이크 본문 0 / 빈 DB 라이브 기동 미입증 → **입증됨**(Dev·Prod 2종).

### Static checks
- 빌드(컴파일러 경고 0)가 사실상 type-check. 프로젝트에 별도 린터/포매터 미구성 → `not configured`(평가 실패 아님).

### Evaluator 정리(라이브 검증 부산물)
- 임시 DB 디렉터리·임시 프로젝트 로그 전부 정리, Wcs.Api 프로세스 0 잔존, working tree에 stray db/log 0(스프린트 의도 변경만 잔존) — 확인 완료.

### Completion(§5) 점검
- build 경고0/오류0 ✔ · test 146 GREEN·회귀0 ✔ · 콜드스타트→정상(Migrate+dev시드+IF-05 OK·크래시 해소) ✔ · 라이브 빈DB 기동 성공 ✔(Dev·Prod 직접) · 서비스 스크립트 존재 ✔ · 변경 범위 국한 ✔.

---

# Sprint Feedback — S-E2E-MULTI-AGV — APPROVED 유지 (post-commit 재확인 + IT4b 독립 검증)

## Phase 3 Post-Commit 재확인 (Evaluator fresh evidence, HEAD `c47e790`·PR #19, 2026-06-26)

**최종 판정: APPROVED 유지** — 스프린트가 커밋(c47e790)·PR #19로 정착된 상태에서 generator가 IT4b 잔여 리스크를 정직 보고하며 재핸드오프. Evaluator 독립 재검증:

### Ground-truth
- HEAD `c47e790`(E2E 8파일·S9 stable-count fix `WaitUntilStableCountAsync`×3 **커밋 확인**). working tree = `tasks/sprint-log.md`+`.claude/`뿐. `git diff HEAD -- src/` 빈 출력(production 0). S9 blob `afc8941` 불변(직전 Rev.2 평가분과 동일 — 재검증 불요).
- IT4b는 `tests/Wcs.Tests/PlcGatewayIntegrationTests.cs`의 **기존 미수정** 테스트(`IT4b_WritesDuringReconnect_NoCorruption`) — 이 스프린트 코드 아님.

### build 함정(환경·정직 기록)
- 1차 빌드 "오류 2·경고 25"는 **MSB3021/MSB3027 파일 잠금** — 고아 `Wcs.Sim3ds.exe`(PID 49188·이전 세션 standalone exe 잔류)가 출력 바이너리를 잠가 복사 실패. **코드 오류 아님**. 프로세스 kill 후 클린 재빌드 **경고0/오류0**. (E2E 인-프로세스 SimServer는 정상 정리됨 — 내 15회 실행 후 잔류 Sim3ds 0.)

### IT4b 독립 검증 (generator 정직 보고 → Evaluator fresh 재현 시도)
- generator 보고: IT4b가 E2E 병렬 부하서 저빈도(초기 10회중 2회) flake(Success→RSeqMismatch). 근본=xUnit 기본 병렬 + 무거운 실 Sim E2E가 타이밍 민감 실 Sim 통합 테스트와 동시 실행(CPU/소켓 경합). team-lead 결정=**S9-only 스코프**(병렬 비활성 미채택), IT4b는 후속 finding.
- **Evaluator 직접 full-suite 15회 연속**(클린 슬레이트·런 간 testhost/Sim3ds 정리) → **PASS=15/15·146/146·exit0·IT4b flake 0회**. 직전 10/10 + generator 12/12 합산해도 IT4b 미발현. → IT4b는 **부하/스케줄러 의존 저빈도** — 현 머신 상태선 미재현이나, generator 보고가 거짓이라 단정 못 함(저빈도 특성상 25회로도 0 가능). **정직 보고로 수용**(은폐 아님 = 가점), S9-only 스코프 하 본 스프린트 PASS.

### 판정: Completion #1~#8·Evaluation ①~⑥ 전부 PASS 유지 (S9-only 스코프)
- ②④(전체 GREEN·flaky0): 스프린트 deliverable(S9 fix+E2E) 기준 15/15 GREEN. IT4b는 미수정 기존 테스트·team-lead 후속 결정·내 15회 미발현 → 본 스프린트 차단 아님.
- ⑥ 정직 보고: F1b(직렬화 갭)에 더해 **IT4b 잔여 리스크까지 은폐 없이 명시·team-lead 보고** — 정직성 추가 가점.

### ⚠ Evaluator 권고 (team-lead 판단)
- **IT4b finding이 `tasks/todo.md`에 아직 미등재**(grep 0). generator/team-lead 합의(S9-only·IT4b 후속)대로라면 todo.md에 IT4b 항목 추가 필요(나도 보고에 포함). 후속 처리 옵션: (A) `[CollectionDefinition·DisableTestParallelization]`로 실 Sim 통합 테스트를 E2E와 직렬화(즉효·team-lead 미승인), (B) IT4b를 WaitUntil 기반으로 견고화, (C) CI에서 별도 격리 실행.

---

# Sprint Feedback — S-E2E-MULTI-AGV (다중 AGV 전 플로우 경우의 수 E2E) — APPROVED (Rev.2 fix 재확인)

## Phase 3 Re-Evaluate 결과 (Evaluator fresh evidence, branch `test/e2e-multi-agv-scenarios`, 2026-06-26 Rev.2 — S9 stable-count 변형)

**최종 판정: APPROVED 유지** — Rev.2에서 generator가 S9 fix를 **더 강한 안정-관찰**로 정련(blob `afc8941`·mtime 10:40). Rev.1의 "D6 로그 ≥1 출현 대기" → Rev.2 **`WaitUntilStableCountAsync(D6Count, expected:1, stableCount:6)`**(d6At1·d6At2 양쪽에 적용·S7 no-flood 동형). Evaluator가 generator의 "10/10" 주장을 신뢰하지 않고 **직접 fresh 재실행**.

### Rev.2 fix 품질 (코드 직접 검사)
- **근본수정·테스트 전용·고정 sleep 아님**: 신규 헬퍼 `WaitUntilStableCountAsync`(ScenarioTests.cs:689) = `pollMs=20` 조건 폴링·타임아웃 시 `Assert.Fail`(fail-loud)·연속 일치 카운트. `Task.Delay` 밴드에이드 아님.
- **단언 의미 보존 + 마스킹 0**: `d6At2 - d6At1 == 0`(선점 D6 추가쓰기0=핑퐁 차단) 불변. **진성 추가 쓰기 발생 시** D6Count가 2가 되어 d6At2 안정-대기(expected=d6At1=1)가 영영 안정 안 됨 → **타임아웃 Assert.Fail**(결함 은폐 아님·여전히 fail-loud). production diff 0.

### Rev.2 Fresh evidence (Evaluator 직접 — raised bar ≥10회)
- build: 경고 0 / 오류 0 (deterministic — 1차 빌드 "경고 4개"는 testhost kill 동시 실행 중 일시 산출물이었고 클린 재빌드 2회 0 확정).
- **클린 슬레이트 full-suite 10회 연속 → 전부 146/146 GREEN·exit0·신규 Sequence 파일 0** (PASS=10/10·런 간 testhost 정리+TestResults 청소).
- 무변경: `git diff HEAD -- src/` 빈 출력. 변경 = ScenarioTests.cs S9 1블록뿐. E2E 8파일 untracked·Rev.1 평가분과 동일(매트릭스·ground-truth·동시성·F1b finding 회귀 0).

### Rev.2 판정: Completion #1~#8·Evaluation ①~⑥ 전부 PASS 유지. (findings·라이브 구동은 아래 Rev.1 기록과 동일.)

---

## Phase 3 Re-Evaluate 결과 (Evaluator fresh evidence, branch `test/e2e-multi-agv-scenarios`, 2026-06-26 Rev.1 fix)

**최종 판정: APPROVED** — Rev.1 FAIL(S9 flake) 재작업이 근본수정으로 해소됨. 계약 Completion #1~#8·Evaluation ①~⑥ 전부 충족. fresh 재검증:

### Ground-truth 확인 (fresh 재핸드오프 — stale 아님)
- HEAD `99a0c9d` 불변, 브랜치 `test/e2e-multi-agv-scenarios`. **이번엔 `tests/Wcs.Tests/ScenarioTests.cs`가 실제 변경됨**(mtime `2026-06-26 10:33`·`git status` ` M`) — 직전 stale 재핸드오프(코드 0변경)와 구분됨.
- `git diff HEAD -- src/` 빈 출력(production 무변경 유지). 변경 = ScenarioTests.cs S9 1블록(+11 line)뿐.

### S9 fix 품질 (코드 직접 검사 — 근본수정·테스트 전용·sleep 밴드에이드 아님)
- 수정: `d6At1` 캡처 **이전**에 첫 "WCS 쓰기 수신: D6" 타임라인 로그가 실제 append(≥1)될 때까지 `WaitUntilAsync` **조건 폴링** 추가. 고정 `Task.Delay` 아님 — 경합의 실제 조건(비동기 로그 append)을 기다림 = 근본수정.
- 근본원인 정합: `WaitUntilAsync(_gw.Latest.TgtFloor==2)`는 WCS 폴 스냅샷 갱신 시 반환하나 "D6 0→2" 로그는 SimServer 루프 스레드(`PullFromServerLocked`)가 비동기 append → 스냅샷 2여도 로그 미append 창에서 d6At1=0 캡처·직후 1건 → delta=1 거짓 실패. 로그 출현 후 baseline 캡처로 delta 결정적 0. (내 직전 진단과 정확 일치.)
- **단언 의미 보존**: `d6At2 - d6At1 == 0`(선점 구간 D6 추가 쓰기 0 = 핑퐁 차단·절대규칙 #3) 단언 **불변**. 진성 추가 쓰기 발생 시 여전히 실패 — 의미 약화 0.
- 테스트 전용: 변경은 `tests/`뿐. production 0.

### Fresh evidence (직접 실행 — 요약 신뢰 아님)
- **build**: `dotnet build Wcs.sln --no-incremental` → 경고 0 / 오류 0.
- **full-suite ≥10회 강검증(raised bar)**: 클린 슬레이트(직전 실행 잔류 testhost 프로세스 정리 + 묵은 TestResults 제거)에서 `dotnet test Wcs.sln --no-build --blame-hang-timeout 180s` **10회 연속 → 전부 146/146 GREEN·exit 0·신규 Blame 시퀀스 파일 0**. (PASS=10/10, FAIL=0.)
- **S9 단독 5회**: 전부 GREEN.
- **teardown 클린**: 클린 10회 실행에서 신규 hangdump/Sequence 파일 0(`find TestResults` 빈 출력).
  ⚠ 평가 절차 주의(은폐 아닌 정직 기록): **1차** 10x 시도에서 RUN8 16실패·RUN9/10 exit1이 관측됐으나, 원인은 **Evaluator 측 환경 소진**(연속 무정리 실행으로 testhost 프로세스 ~18개 누적·포트/핸들 경합)이었고 `blameSeqAbsent=0` 신호는 **`20260624` 묵은 산출물** 매칭 오탐이었음. 프로세스 정리 + 묵은 TestResults 제거 + 런 간 간격 후 재실행 시 **10/10 GREEN**. 스프린트 결함 아님(귀속 확정).

### Completion / Evaluation — 항목별 (Rev.1 fix 반영)
- **[PASS] #1 build 0/0** · **[PASS] #2 전체 GREEN·exit0·회귀 0**(10/10·S9 해소) · **[PASS] #3·⑤ teardown exit0**(신규 시퀀스 파일 0) · **[PASS] #4·④ flaky 0**(≥10회 강검증 — 직전 5회보다 강함) · **[PASS] #5·① 매트릭스 커버리지** · **[PASS] #6·② ground-truth 진정성** · **[PASS] ③ 동시성 진성 경합** · **[PASS] #7·⑥ 결함 정직보고**(F1b 직렬화 갭 finding·⚠항목 분류) · **[PASS] #8·무변경 가드**(src diff 0).
- ①②③⑥는 Rev.1에서 코드/source 직접 검사로 PASS 확정했고 production·E2E 코드가 그대로이므로 회귀 0(ScenarioTests.cs S9 1블록만 추가).

### 결함·finding (계약 §6 정직 보고 — 후속 인계)
- **[FINDING·진성·후속] 한 소터 concurrent 핸드셰이크 직렬화 부재** (`F1b`): `HandshakeOrchestrator.ExecuteAsync`가 동일 인스턴스 concurrent 호출을 직렬화하지 않아(인스턴스 락 0·공유 `_gw.Latest` RFlag/RSeq 폴링) 한 소터 동시 IF-10 시 R_Seq 교차 MISMATCH≥1. 순차 dispatch면 전부 COMPLETED(F1/F8). SPEC §6 물리 직렬 모델과 정합 → 직렬 dispatch가 현 지원 모델. 동시 IF-10 허용 명세 미정 → **orchestrator 직렬화는 범위 밖 후속**. 은폐 0·`mismatch>=1` 명시 단언(정직 보고 가점).
- **[⚠ SPEC §7 미확정 — 현 동작 단언]** D5(C_Flag 상한)·D6(핸드셰이크 중 OFFLINE)·D8(R_CellNo≠C_CellNo 주입 불가·Sim 한계)·G6(슈트 복구 재푸시 비대칭)·H4(TgtFloor 잔류)·H5(R_Flag 재시도 정책). 전부 "현 동작 단언/기대 미정"으로 올바로 분류 — 추측 단언 0.
- **[⚠ M5 이연]** E6(콜백 throw 셀 누수 — 호스트종료/DI오설정 한정·정상 경로 누수 0만 입증).

### 최종 판정: APPROVED. 라이브 구동(§7 orchestrator step)은 별도 진행.

---

<details><summary>이전: Rev.1 FAIL 기록 (S9 flake — 위 fix로 해소됨)</summary>

**최종 판정: FAIL (재작업 1건)** — 매트릭스 커버리지·ground-truth 진정성·동시성 진성 경합·정직 보고·무변경 가드(①②③⑥⑧)는 전부 PASS이고 구현 품질이 높으나, **Completion #2(전체 GREEN·exit 0) + Evaluation ④(flaky 0)** 가 미충족. fresh full-suite **5회 실행 중 1회 FAIL**(S9 — 기존 미수정 테스트가 E2E 추가 부하 하에서 간헐 실패). exit 1 관측. 단일 결함이며 국소 수정 가능.

### Ground-truth 확인 (stale 재핸드오프 아님)
- `git rev-parse HEAD` = `99a0c9d`, 브랜치 `test/e2e-multi-agv-scenarios`. E2E 8파일은 untracked(`tests/Wcs.Tests/E2E/`) — 미커밋 활성 핸드오프.
- `sprint-log.md` 최상단 `## IMPLEMENTATION COMPLETE (S-E2E-MULTI-AGV)` 마커·매핑 표 존재.
- working tree: `tests/Wcs.Tests/E2E/`(신규 8파일) + `tasks/sprint-contract.md`·`tasks/sprint-log.md`(하네스 산출물) + `.claude/`(선재). **`git diff HEAD -- src/` = 빈 출력**(production 0 변경) + `git ls-files --others -- src/` 빈 출력.

### Fresh evidence (직접 실행 — generator 주장 신뢰 아님)
- **clean build**: `dotnet build Wcs.sln --no-incremental` → **경고 0 / 오류 0** (재현 확인).
- **full test (결정적 검증)**: `dotnet test Wcs.sln --no-build --blame-hang-timeout 180s` 를 포함해 full-suite **총 5회** 실행:
  - RUN1: **실패! 실패:1 통과:145 전체:146, EXIT 1** — `S234_9GatewayScenarioTests.S9_MultiAgvContention_TgtFloorSingleOwnership_ThenYield` FAIL ("선점 구간 D6 추가 쓰기 1건 (0건이어야 함)").
  - RUN2~5: 통과! 146/146 exit 0. → **4 GREEN / 1 FAIL (flake rate ~20%)**.
  - 전 실행 Blame "시퀀스 파일이 생성되지 않습니다"(teardown hang/dump 0 — 채널 경쟁 회귀 0 = ⑤ PASS).
- **flake 귀속(stash 대조)**: E2E 디렉터리를 일시 제거하고 baseline full-suite **4회** → **99/99 GREEN 4/4, 6~8s**(S9 안정). E2E 복원 시 146 테스트·11~13s. S9 **단독 5회 전부 GREEN**(`--filter S9_MultiAgvContention`).
  → **근본 원인**: S9는 이 스프린트가 **수정하지 않은** 기존 타이밍-취약 테스트(마지막 변경 9ff57f6, 본 스프린트 무관). `d6At1`을 `WaitUntilAsync(_gw.Latest.TgtFloor==2)` 직후 캡처하나, "WCS 쓰기 수신: D6 0→2" 타임라인 로그는 **Sim 스레드가 비동기로** append → 게이트웨이 스냅샷 갱신과 로그 append 사이 경합. E2E 다수 테스트(실 Sim N대·Barrier 동시 HTTP)가 어셈블리 전체 부하·런타임을 6~8s→11~13s로 올려, 잠재 경합이 간헐 발현. **baseline에선 안정, E2E 부하가 임계로 밀어냄.**

### Completion Conditions — 항목별 PASS/FAIL
- **[PASS] #1 build 경고0/오류0**: clean build 0/0 재현.
- **[FAIL] #2 전체 GREEN·exit 0·기존 회귀 0**: full-suite 5회 중 1회 exit 1(S9 FAIL). "전체 GREEN·exit 0"이 결정적이지 않음. 기존 회귀로 분류 — S9는 미수정 기존 테스트이나 E2E 부하가 발현시킴.
- **[FAIL] #4 동시성/타이밍 표적 ≥5회 flaky 0**: full-suite 차원에서 flaky 발현(1/5). 단, **신규 E2E 테스트 자체는 flaky 0**(아래 ④ 참조) — 결함은 기존 S9.
- **[PASS] #3 teardown exit 0**: Blame 시퀀스 파일 0(전 5회)·채널 경쟁 회귀 0.
- **[PASS] #5 매트릭스 A~I 매핑**: 매핑 표 전 항목 ↔ 신규/기존 테스트 대응(아래 ① 참조).
- **[PASS] #6 ground-truth 진정성**: 핵심 단언 전부 실 Sim/실 EF DB/가짜 RCS push(아래 ② 참조).
- **[PASS] #7 결함 정직 보고**: F1b·⚠항목 정직 등재(아래 ⑥ 참조).
- **[PASS] #8 무변경 가드**: `git diff HEAD -- src/` 빈 출력.

### Evaluation Criteria — 항목별 (가중)
- **[PASS] ① 매트릭스 커버리지 0.25**: A1~A7·B1/B6/B10/B11·C5~C7·D3~D9·E2~E6·F1/F1b/F4~F8·G1~G6·H1/H4/H6·I1~I4 가 **실제 실행되는 단언**에 대응(코드 직접 확인). "기존 커버" 항목(B2~B5·B7~B9·B12~B16·C1~C4·D1/D2/D7·G4·H2/H3·F2/F3)은 매핑 표에 기존 테스트 명시. 빈 테스트·이름만 통과 0. 로드-베어링 단언원 source 확인: B11 `"OVER"`=`DbRepositories.cs:84-86`, B6 `"NO_DEST"`=`:54/114/132`, B1 `"FULL"`=`:151` (추측 문자열 아님).
- **[PASS] ② ground-truth 진정성 0.25**: `E2EWebApplicationFactory`가 실 Sim3ds N대(동적 포트)+production `SorterRegistryFactory`(Fake/Nop 교체 0)+실 EF SQLite+FakeRcs 결선. 단언이 sorter_command(COMPLETED/MISMATCH/TIMEOUT·R_Seq==C_Seq)·cell_assignment(released_at)·piece/piece_event·alarm·셀수량(COMPLETED JOIN piece.qty DISTINCT)·push payload(CountFor/LastFor/Ready)에 근거. 인메모리 카운터 단독 0.
- **[PASS] ③ 동시성 진성 경합 0.20**: F4/E3(Barrier 8병렬 실 HTTP→DB 정확히 1)·F5(실 Sim 2대 동시 핸드셰이크 교차 0)·F6(stableCount:8 전이당 1·무폭주)·F7(stableCount:5 alarm 1). F1/F8은 "순차"로 **정직 명명**(거짓 동시 주장 아님). F1b의 직렬화 부재는 `HandshakeOrchestrator.ExecuteAsync` source 확인(인스턴스 락 0·공유 `_gw.Latest` RFlag/RSeq 폴링) — 진성 결함 입증.
- **[FAIL] ④ flaky 0 0.15**: **신규 E2E 표적은 flaky 0이나**, full-suite 차원에서 기존 S9가 1/5 실패 → 계약 "타이밍·동시성 표적 ≥5회 반복 GREEN" 미충족(전체 스위트 기준). 핵심 수정 대상.
- **[PASS] ⑤ teardown exit 0 0.10**: 실 Sim/실 호스트 다중 기동에도 Blame 시퀀스 파일 0(전 5회). `DisposeAsync` 순서(Sim→base IHost→anchor) 적정.
- **[PASS] ⑥ 결함 정직 보고 0.05**: F1b(한 소터 동시 IF-10 직렬화 부재 MISMATCH≥1)를 은폐 없이 명시 입증·`mismatch>=1` 단언. ⚠항목(D5·D6·D8·E6·G6·H4·H5)은 "현 동작 단언/Sim 한계/기대 미정"으로 올바로 분류(추측 단언 0). D8은 R_CellNo==C_CellNo 단언+주입 불가 명시. **정직성 가점.**

### 재작업 지시 (FAIL — 1건, 국소)
1. **S9 flake 해소(필수·exit 0 차단)**: 기존 `S9_MultiAgvContention...`의 `d6At1` 캡처가 Sim 타임라인 로그 append와 경합. 권장: ① `d6At1`을 SetTgtFloor enqueue **이전**에 캡처하거나, ② D6 카운트 판정을 타임라인 로그 대신 게이트웨이 스냅샷 전이(또는 `WaitUntilExact`로 D6 카운트 안정화 후) 기준으로 변경. **production 변경 금지**(무변경 가드 — S9는 테스트). 수정 후 full-suite ≥10회 연속 GREEN·exit 0 입증. (S9는 본 스프린트가 만들지 않았으나, E2E 부하가 발현시킨 회귀이므로 이 스프린트 범위에서 해소하거나 — 범위 밖 판단 시 team-lead/사용자 협의로 별도 처리.)
2. 그 외 ①②③⑤⑥·무변경 가드는 재검증 불요(이미 PASS) — S9 한 건만 수정 후 재핸드오프.

</details>

---

# Sprint Feedback — S-FOLDER-ORG (src 폴더 구조 정리 / 순수 파일 이동) — APPROVED

## Phase 3 Evaluate 결과 (Evaluator fresh evidence, working tree `refactor/src-folder-structure`, 2026-06-25)

**최종 판정: APPROVED** — 계약 Completion Conditions #1~#7·Evaluation Criteria ①~⑤·Verification Scenarios(Backend/API 슬롯 a/b/c + 구조정리 1~8) 전부 fresh 직접 실행 증거로 충족. 순수 이동(rename R100·내용 diff 0)·동작 보존(build 0/0·test 99/99·exit 0·teardown 회귀 0)·네임스페이스 불변·무변경 프로젝트 가드 전부 통과. 회귀 0.

### Ground-truth 확인 (stale 재핸드오프 아님)
- `git rev-parse HEAD` = `1bb0a62`, 브랜치 `refactor/src-folder-structure`. 본 스프린트 이동 15파일은 **staged(미커밋)** 상태 — 이미 커밋된 스프린트가 아님 → 정당 활성 핸드오프.
- `sprint-log.md` 최상단 `## IMPLEMENTATION COMPLETE (S-FOLDER-ORG)` 마커 존재 확인.
- working tree 변경: src 이동 15파일(staged R) + `tasks/sprint-contract.md`·`tasks/sprint-log.md`(하네스 산출물) + `.claude/`(untracked, 선재). src 코드 표면 외 변경 없음.

### Fresh evidence (직접 실행 — generator 주장 신뢰 아님)
- **clean build**: `dotnet build Wcs.sln --no-incremental` → **경고 0개 / 오류 0개**. 8개 프로젝트 전부 빌드(Migrations 2종·Wcs.Tests 포함). csproj 미편집으로 SDK 글로빙이 새 폴더 `**/*.cs` 자동 포착 입증.
- **full test**: `dotnet test Wcs.sln --blame-hang-timeout 120s` → **통과! 실패:0 통과:99 건너뜀:0 전체:99, EXIT CODE 0**. Blame 수집기 "모든 테스트 실행이 완료되었지만, 시퀀스 파일이 생성되지 않습니다"(teardown hang/dump 0 — 채널 경쟁 회귀 lesson 미재발). baseline 99와 동일 = 회귀 0.
- **EF 디자인타임 강검증**: `dotnet ef migrations list --project src/Wcs.Migrations.Sqlite --no-build` → DbContext 정상 해석·마이그레이션 3건 나열(Initial·P2a·P1_If09Arrival), exit 0. 이동이 디자인타임 발견에 무영향.

### Completion Conditions — 항목별 PASS/FAIL
- **[PASS] #1 build 경고0/오류0**: clean build 경고 0개/오류 0개 (위 fresh evidence).
- **[PASS] #2 test 전체 GREEN·count=99·exit 0·hang 0**: 99/99 GREEN, exit 0, Blame 시퀀스 파일 0(teardown 채널 경쟁 회귀 0).
- **[PASS] #3 rename만(신규/삭제 단독 없음)**: `git status --find-renames --short` = src 15파일 전부 `R `(rename), `??`/`D` 단독 항목 0. porcelain raw에 `RM`(rename+modify) 없음·`git ls-files --others -- src/` 빈 출력(untracked src 0).
- **[PASS] #4 내용 diff 0**: `git diff -M --cached --stat -- src/` = **"15 files changed, 0 insertions(+), 0 deletions(-)"**. `--numstat` 전 항목 `0 0`. `--summary` 전 항목 `rename ... (100%)` = R100 byte-identical. `+`/`-` 본문 라인 0.
- **[PASS] #5 네임스페이스 grep 불변**: Wcs.Api 11×`namespace Wcs.Api;`(Dtos·SorterGatewayRegistry·WcsTeardownGuard·WcsOptions·DbRepositories·Repositories·DestinationStatusPusher·ChuteCapacityService·DestinationStatusService·RcsPushClient·SorterCellQty) + 1×`namespace Wcs.Api.Controllers;`(RcsController) + Program/ProgramPartial 네임스페이스 없음(grep 미출현=정상). PlcGateway 6×`namespace Wcs.PlcGateway;`. 이동 파일은 폴더와 무관하게 평면 선언 유지(예: `Services/SorterCellQty.cs`도 `namespace Wcs.Api;`). 계약 기준값 정확 일치(self-check line 36 "12개"는 11개 오기 — Completion #5·핸드오프가 정확).
- **[PASS] #6 EF 디자인타임 무영향**: Migrations 2종 컴파일 성공(clean build 포함) + `dotnet ef migrations list` 강검증 통과.
- **[PASS] #7 무변경 프로젝트 git status 0**: `git status --find-renames --short -- src/Wcs.Core src/Wcs.Data src/Wcs.Sim3ds src/Wcs.Migrations.Sqlite src/Wcs.Migrations.SqlServer tests/` = 빈 출력. csproj/.sln/appsettings 글로브 = 빈 출력(편집 0).

### Evaluation Criteria — 항목별
- **[PASS] ① 동작 보존 ★★★**: build 0/0·test 99/99·exit 0·teardown 회귀 0·count baseline 동일.
- **[PASS] ② 순수 이동 입증 ★★★**: rename R100 15건·내용 diff 0·네임스페이스 불변. add/delete 단독 0.
- **[PASS] ③ MVC 레이어 정확성 ★★**: working-tree 파일 배치 확인 — Wcs.Api Services/(5)·Repositories/(2)·Dtos/(1)·Infrastructure/(3)·Controllers/RcsController·Program/ProgramPartial 루트. PlcGateway Modbus/(4)·PlcGateway/HandshakeOrchestrator 루트. 구 평면 경로 잔존 파일 0.
- **[PASS] ④ csproj/솔루션 무결성 ★★**: csproj/.sln 편집 0으로 빌드/테스트 발견 정상(SDK 글로빙 검증).
- **[PASS] ⑤ 문서/참조 정합 ★(비차단)**: CLAUDE.md "솔루션 구조"는 프로젝트별 역할만 기술·내부 폴더 미언급 → 새 폴더와 모순 0. 정정 불요(계약 명시대로).

### Verification Scenarios (Backend/API + 구조정리)
- **[PASS] (a) 엔드포인트 목록**: IF-05/IF-09/IF-10 핸들러 `Controllers/RcsController.cs` 무변경(diff 0). 라우트/시그니처 불변.
- **[PASS] (b) happy path / DI 해석**: WebApplicationFactory 기반 통합 테스트 GREEN(99 전체에 포함) → 이동된 타입(DestinationStatusService·RcsPushClient·SorterGatewayRegistry 등) DI 해석·앱 부팅 정상.
- **[PASS] (c) 에러 케이스**: 기존 400/FULL·PAUSED NG/OFFLINE 경로 테스트 분포 0 변화(99/99 GREEN, 신규 에러 케이스 0).
- **[PASS] 1 빌드 무결성 / 2 테스트 보존 / 3 git rename 순수성 / 4 네임스페이스 불변 / 5 Api MVC 배치 / 6 PlcGateway Modbus 배치 / 7 EF 디자인타임 / 8 테스트 타입 발견**: 위 Completion 증거로 전부 충족(8=test 99 GREEN이 곧 Wcs.Api.* 타입 발견·참조·실행 입증).

### 결론
순수 파일 이동 스프린트의 핵심 명제 "위치만 바뀌고 동작/내용 0 변경"을 fresh tool output으로 입증. R100 rename 15건·numstat 0/0·build 0/0·test 99/99 exit 0·네임스페이스 grep 불변·무변경 프로젝트 가드 빈 출력. **BLOCKING/FAIL 0. APPROVED.**

---

# Sprint Feedback — S-소터push운영상태 (소터 IF-08 push ready를 운영상태로 좁힘) — APPROVED

## Phase 3 Evaluate 결과 (Evaluator fresh evidence, 미커밋 working tree `feat/sorter-push-operational`, 2026-06-25)

**최종 판정: APPROVED** — 계약 Verification Scenarios(VS-1~10)·Completion Conditions·Evaluation Criteria(35/25/20/15/5)·크로스-엔드포인트 정합(push·IF-05가 같은 Compute 산출을 분기 소비) 전부 fresh 직접 실행 증거로 충족. 무변경 가드 11개 경로 diff 0·IF-05 `r.Ready` 미소비 grep 0·teardown 회귀 0·baseline 귀속 명확(90→99=+9 신규).

### Ground-truth 확인 (stale 재핸드오프 아님)
- `git rev-parse HEAD` = `7b12098`(PR #15 머지 = 직전 스프린트). 본 스프린트 변경은 **working tree 미커밋/untracked**(이미 커밋된 스프린트 아님 → 정당 활성 핸드오프). 브랜치 `feat/sorter-push-operational`.
- `sprint-log.md` 최상단 `## IMPLEMENTATION COMPLETE (S-소터push운영상태)` 마커 존재 확인.
- 변경 파일(working tree): `docs/wcs_rcs_interface_kr.html`·`src/Wcs.Api/DestinationStatusService.cs`·`tests/Wcs.Tests/{ApiIntegrationTests,SorterCellFullnessTests}.cs`·신규 `tests/Wcs.Tests/SorterPushOperationalTests.cs`(+ tasks/ 하네스 산출물). 소스 변경 = `DestinationStatusService.cs` 1개뿐(계약 single-module 일치).

### Fresh evidence (직접 실행 — generator 주장 신뢰 아님)
- **build**: `dotnet build Wcs.sln` → **경고 0개 / 오류 0개, BUILD_EXIT=0**.
- **full test**: `dotnet test Wcs.sln --blame-hang-timeout 120s` → **통과! 실패:0 통과:99 건너뜀:0 전체:99, FULL_SUITE_EXIT=0**. Blame 수집기 "모든 테스트 실행이 완료되었지만, 시퀀스 파일이 생성되지 않습니다"(teardown hang/dump 0건). 중단/abort 라인 0.
- **baseline 귀속**: 본 스프린트 변경을 `git stash`로 제거 후 HEAD(`7b12098`) baseline 측정 → **통과:90 exit 0, teardown 클린**. 복원 후 99 = 90 baseline + 9 신규 SorterPushOperational. teardown은 baseline에서도 이미 클린 → 본 변경이 teardown 회귀 도입 0. (stash pop 클린 복원 확인.)
- **flaky 0(타이밍/동시성 표적 ≥5회 단독)**: `VS9|VS2|VS3|EC7|EC5|VS1` 필터(12건 — VS-9a barrier 16스레드 동시관찰·VS-9b no-flood·VS-2 전이·VS-3 offline·EC-7 만재 churn 무발화·EC-5 동시 churn·VS-1) **5/5회 연속 12/12 GREEN·exit 0·시퀀스 파일 0**. 비결정성 0.
  ```
  RUN 1: exit=0 통과:12 (4s)   RUN 2: exit=0 통과:12 (3s)   RUN 3: exit=0 통과:12 (4s)
  RUN 4: exit=0 통과:12 (3s)   RUN 5: exit=0 통과:12 (3s)
  ```
- **신규 suite 단독**: `SorterPushOperationalTests` 9/9 GREEN. `SorterCellFullnessTests`(반전 EC-1/3/5/7+HP-5 포함) 14/14 GREEN.

### Verification Scenarios — 항목별 PASS/FAIL (실 DB seed·게이트웨이 snapshot·가짜 RCS push payload ground-truth)
- **[PASS] VS-1 (소터 online·정렬·Ready=1 → push ready=true)**: `VS1_...` — `AlignSorterAsync`(SetReady/CurFloor=2/TgtFloor=0) → `Compute(SORTER_3D).Ready==true`·`Online==true`·`Reason==None` + 가짜 RCS 수신 본문 `LastFor(sorterChute).Ready==true`(실 HTTP push payload). `DestinationStatusService.cs:313` `ready = decision.Ready`.
- **[PASS] VS-2 (소터 busy → push ready=false, 2 하위)**: `VS2_...[Theory]` — (a) `SetReady(false)`→`Reason==Busy` (b) `SetCurFloor(1)`(미정렬)→`Reason==NotAligned`. 두 케이스 모두 `Compute().Ready==false` + push payload `LastFor.Ready==false`(true→false 전이 관찰).
- **[PASS] VS-3 (소터 offline → push ready=false)**: `VS3_...` — `SetFailReads(true)`(읽기 IOException 주입 = 진짜 OFFLINE, Disconnect 즉시재연결 회피) → snap.Online=false → `Compute().Online==false`·`Ready==false`·`Reason==Offline` + push payload `LastFor.Ready==false`.
- **[PASS] VS-4 [핵심회귀] (셀 만재여도 운영상태 ready → push ready=true)**: `VS4_...` — `MakeSorterFull`(Capacity=5·3셀 전부 점유·각 sorter_command COMPLETED+piece.qty=5 = 실 JOIN ground-truth) → **`Compute().Full==true` AND `Compute().Ready==true` 공존**·`Reason==None` + push payload `LastFor.Ready==true`. 만재가 push에 무영향 입증.
- **[PASS] VS-5 [핵심회귀] (PAUSED여도 운영상태 ready → push ready=true)**: `VS5_...` — `destination.Status=PAUSED`(실 DB) → **`Compute().Paused==true` AND `Compute().Ready==true` 공존**·`Reason==None` + push payload `LastFor.Ready==true`. paused가 push에 무영향 입증.
- **[PASS] VS-6 (슈트 push full/pause→false·비움/정지해제→true 현행 유지)**: 회귀 — `RcsPushTests.PUSH1_Chute_ReadyTransition`(만재→ready:false·비움→ready:true 전이당 1건)·부트스트랩 PAUSED 슈트6 `LastFor(6).Ready==false`. ComputeChute(`:247-252`) 무변경. 전체 99 GREEN에 포함·기존 단언 불변.
- **[PASS] VS-7 (IF-05 소터 3축 — r.Ready 무영향)**: `VS7_...` 실 HTTP `POST /api/v1/destination-query` — (a) offline(`SetFailReads`)+셀있음 → `Compute().Ready==false`인데 `result=="OK"`·chuteNo 반환(IF-05는 online 안 봄) (b) `Status=PAUSED` → `result=="NG"`·chuteNo=null (c) `MakeSorterFull`+정렬(`Compute().Ready==true`) → `SorterCanAcceptBarcode==false`·`result=="NG"`. 운영상태 ready 변화가 세 결과에 무영향(IF-05는 `r.Paused`+`SorterCanAcceptBarcode`만 소비) 입증.
- **[PASS] VS-8 (IF-05 슈트 full/pause여도 OK)**: 회귀 — `RcsController.cs:69-70`(슈트는 `DestinationBlock.None` 무조건 통과) + `SorterCellFullnessTests`의 슈트 IF-05 OK 단언(이전 스프린트 반전분) 불변. 전체 GREEN 포함.
- **[PASS] VS-9 (push 멱등 + 만재/paused 무발화)**: (a) `VS9a_...` — 부트스트랩 1건(ready=false) 안정 후 운영상태 전이(정렬) 발생, **16스레드 Barrier 동시관찰** `NotifyChuteChanged` → `WaitUntilExact(2, stableCount:8)`로 정확히 2건(부트1+전이1, 중복 0) = 클레임 경합 멱등 입증(중복억제만 아닌 동시관찰 프로브). (b) `VS9b_...`·`EC7_...` — 운영상태 불변인 채 `MakeSorterFull`+`Status=PAUSED` 전이 → `WaitUntilExact(baseReady, stableCount:8~10)`로 **소터 push 0건**(no-flood)·`LastFor.Ready==true` 유지. (c) 무변화 폴 폭주 0.
- **[PASS] VS-10 (무변경 가드 + grep)**: `git diff HEAD --stat`로 Wcs.Core·PlcGateway·Sim3ds·Data·Migrations.{Sqlite,SqlServer}·DestinationStatusPusher.cs·RcsPushClient.cs·RcsController.cs = **빈 출력(0줄)**. IF-05 `r.Ready` 미소비: `grep "\.Ready" RcsController.cs` → line 170 `decision.Ready`(IF-09 DepositDecision 로깅) 1건뿐 — `r.Ready`(DestinationReadiness) 소비 0. IF-05 콜백(`:64-79`)은 `r.Paused`+`SorterCanAcceptBarcode`만 소비(코드 직접 확인).

### Evaluation Criteria 충족 (가중치)
- **[PASS] ① 소터 push ready 운영상태 정확성 (★★★ 35%)**: `ready = decision.Ready`(`:313`). VS-1(true)·VS-2(busy false)·VS-3(offline false)·VS-4(만재+ready 공존)·VS-5(paused+ready 공존) 실 push payload로 입증.
- **[PASS] ② push 전이 발화 정확성 (★★★ 25%)**: VS-9a 전이당 1건(동시 16관찰 중복 0)·VS-9b·EC-7 만재/paused 전이 무발화(no-flood). 멱등 기계(DestinationStatusPusher) 무변경 보존.
- **[PASS] ③ 회귀 0 (★★ 20%)**: 슈트 push(VS-6)·IF-05 소터3축(VS-7)·IF-05 슈트(VS-8)·IF-09/IF-10 인바운드 불변. 반전 단언(EC-1/3/5/7·HP-5)은 삭제 아닌 정정(diff로 확인) — IF-05 NG·reason=FULL/Paused 회귀 가드 단언 보존. baseline 90 회귀 0.
- **[PASS] ④ 무변경 가드 (★★ 15%)**: VS-10 — 11개 경로 git diff 0줄.
- **[PASS] ⑤ 스펙 문서 정합 (★ 5%)**: `git diff HEAD -- docs/wcs_rcs_interface_kr.html` 실제 4개 라인영역(126·172·208·216~217) 정정 확인(diff 인용). 소터 push ready=운영상태·만재/정지는 IF-05 dispatch로 명문화. SPEC.md는 push-ready 정의 부재(폐지된 폴링 모델)라 무변경(계약 "있으면 정합·없으면 무변경" 준수 — 허위 deliverable 주장 0).

### Completion Conditions — 전부 충족
build 0/0·full GREEN exit 0·teardown 클린·표적 5회 flaky 0·무변경 가드 diff 0·docs diff 라인 확인·IF-05 r.Ready 미소비 grep 0.

### Minor 관찰 (비차단 — 다음 sprint Generator 참고)
- `DestinationStatusService.cs:34` `LoadedQtyByCell` 본문 주석에 "(LOADED)" 표현이 남았으나 실 산출원은 sorter_command(COMPLETED) JOIN — 코드는 정확, 주석 단어만. 비차단(이번 변경 표면 밖, 산출 로직 무변경 zone).
- 계약 §46에서 언급된 orphan `SorterHasAssignedCellWithRoomForBarcode`(인터페이스 `:84`)는 여전히 존재하나 계약이 "본 스프린트 표면 밖·무리하게 끌어오지 말 것"으로 명시 → 정당한 scope-out. 별도 정리 sprint 권장.

## Step 4.5 독립 코드리뷰 (orchestrator, opus, 팀 외부) — APPROVE (BLOCKING/MAJOR/MINOR 0)

독립 Opus 코드리뷰어가 7가설 적대적 검증 후 **APPROVE** — 차단 사유 0:
1. **크로스엔드포인트 분리**: `Compute()` 3소비처 grep — push=`.Ready`만/IF-05=`.Paused`만/`r.Ready` IF-05 소비 0(구조적 입증).
2. **Reason 무붕괴**: `Compute().Full/Reason` production 미소비(테스트만) + ComputeSorter가 hold=None 주입 → DepositDecider Full/Paused 분기 도달불가 → stale 누출 0. `ready=true ⟹ reason=None`.
3. **push 전이**: Pusher가 ready bool에 agnostic → 운영전이만 발화·만재/paused 무발화 구조 보장. full torn-read가 ready(=decision.Ready, DB 셀쿼리 미사용)에 무영향.
4. **무변경 가드**: 11파일 diff 0. 5. **엣지 6케이스**: `ready⟹online` 불변식. 6. **스펙 4라인** 과/소정정 0. 7. **반전 7건** 정정·IF-05 NG 가드 보존.

정보성 2건(비차단·todo 등재): ①`DestinationReadiness.Full/Paused/Reason` 현재 production 미소비(dead-but-consistent — 향후 소비처 생기면 ready=true&&Full=true 의미 재확인) ②`docs/SPEC.md` §2 구 IF-08(deposit-permission 폴링) 모델 기술 잔존(선재 문서부채·스코프밖).

→ BLOCKING 0 → Step 5 커밋 진행. 7회째 독립 리뷰 중 P2b에 이은 2번째 클린 통과(산출 단일지점 변경+소비처 grep+크로스엔드포인트 연결 테스트로 사전 방어).

---

# Sprint Feedback — S-M4-P4 (소터 셀 만재 판정 m4p4) — APPROVED

## Phase 3 Evaluate 결과 (Evaluator fresh evidence, 미커밋 working tree `feat/sorter-cell-fullness`, 2026-06-24)

**최종 판정: APPROVED** — 계약 Verification Scenarios(HP-1~3·EC-1~6)·Completion Conditions·Evaluation Criteria(40/25/15/10/10)·사용자 확정 4건(Q1~Q4) 전부 fresh 직접 실행 증거로 충족. 보호 zone diff 0·DB 스키마 무변경·teardown 회귀 0. 동시성 원자성은 SQL 단일 쿼리 구조 + EC-5 내부 불변식 + quiesce 등가성으로 입증.

### Fresh evidence (직접 실행 — generator 주장 신뢰 아님)
- **build**: `dotnet build Wcs.sln` → **경고 0개 / 오류 0개, BUILD_EXIT=0**.
- **full test**: `dotnet test Wcs.sln --blame-hang-timeout 120s` → **통과! 실패:0 통과:83 건너뜀:0 전체:83, TEST_EXIT=0**. Blame 수집기 "시퀀스 파일이 생성되지 않습니다"(teardown 전용 행 0). hangdump 생성 0건. **기존 76 회귀 0 + 신규 7 = 83.** (76 = Phase 2 baseline.)
- **flaky 0(타이밍/동시성 표적 ≥5회)**: `SorterCellFullnessTests | RcsPushTests` 필터 **5/5회 연속 13/13 GREEN·exit 0**. 비결정성 0.
  ```
  RUN 1: exit=0 통과:13 실패:0 (5s)
  RUN 2: exit=0 통과:13 실패:0 (5s)
  RUN 3: exit=0 통과:13 실패:0 (5s)
  RUN 4: exit=0 통과:13 실패:0 (5s)
  RUN 5: exit=0 통과:13 실패:0 (5s)
  ```

### 사용자 확정 4건(Q1~Q4) — 코드 직접 검증
- **[PASS] 확정1 (Q1 — IF-05 piece-aware 오더 재사용 예외)**: `RcsController.cs:65-74` availability 콜백 — `r.Paused`면 무조건 차단(우선), `r.Full && SORTER_3D && SorterHasActiveAssignmentForBarcode(id, barcode)`면 `DestinationBlock.None`(OK), 아니면 `Full`(NG). 슈트는 예외 미적용. `SorterHasActiveAssignmentForBarcode`(`DestinationStatusService.cs:107-110`)는 `EfCellSelector.SelectCell` ①분기(`DbRepositories.cs:578-585`)와 **predicate 완전 동형**(`ReleasedAt==null && Cell.DestinationId==id && Order.Items.Any(Barcode==bc)`), `.Any()` 읽기 전용 — 배정 부수효과 0. IF-05 OK→IF-10 SelectCell ①분기 재사용 일관(같은 셀 누적). 행동 입증: HP-2(OK·reason=NORMAL), EC-1(NG·reason=FULL).
- **[PASS] 확정2 (Q2 — 기존 소터 관찰 타이머 재사용, 별도 변화원 0)**: `git diff develop -- src/Wcs.Api/DestinationStatusPusher.cs` = **0줄**. `git diff develop -- src/Wcs.Api/ChuteCapacityService.cs` = **0줄**. `grep NotifyCellChanged|CellChanged|OnCellChanged src/ tests/` = **0건**(새 cell-change 변화원 없음). 기존 `RunSorterObserveLoopAsync`(`DestinationStatusPusher.cs:198`)가 매 주기 `_status.Compute`(line 233) 호출 → DB-aware ComputeSorter가 cell_assignment 변화를 자동 포착. 행동 입증: EC-3/HP-3(셀 점유/해제 전이가 타이머에 포착돼 푸시).
- **[PASS] 확정3 (Q3 — IServiceScopeFactory 주입, captive 회피)**: `DestinationStatusService` 생성자(`:76-86`)가 `IServiceScopeFactory`(싱글톤) 주입. `ComputeSorter`(`:156`)·`SorterHasActiveAssignmentForBarcode`(`:102`)가 `_scopeFactory.CreateScope()`로 scoped `WcsDbContext` 취득. `Program.cs` DI 등록은 `AddSingleton<IDestinationStatusService, DestinationStatusService>()` 불변(주석만 변경) — `IServiceScopeFactory` 자동 해석. 싱글톤이 scoped DbContext를 직접 주입하지 않음(안티패턴 회피).
- **[PASS] 확정4 (Q4 — 마이그레이션 0·DB 스키마 무변경)**: `git diff develop -- src/Wcs.Migrations.Sqlite src/Wcs.Migrations.SqlServer src/Wcs.Data` = **0줄**. 신규/untracked 마이그레이션 파일 0. cell/cell_assignment/`(cell_id) WHERE released_at IS NULL` 부분유니크 전부 기존. 읽기 전용 조회만 추가.

### Verification Scenarios — 항목별 PASS/FAIL (실 DB·가짜 RCS ground-truth)
- **[PASS] HP-1 (IF-05/Compute 빈셀 있음 + 정렬→ready)**: `HP1_EC6_...` — 빈셀 3개 시드(`FreeCellCount==3` DB 단언) → 미정렬 Compute `Full=false·Ready=false`(decision.Reason) → 정렬(CurFloor=2·Ready=1) Compute `Full=false·Ready=true`. 가짜 RCS·실 cell DB 기준.
- **[PASS] HP-2 (IF-05 오더 재사용 예외)**: `HP2_...` — ORD-003 3셀 전부 점유(`FreeCellCount==0`) + `SorterHasActiveAssignmentForBarcode(sorterId,"TEST-BARCODE-3")==true` → IF-05 실 HTTP `{result:"OK", chuteNo:30}`, piece_event reason=NORMAL·PieceStatus.RESERVED(DB 단언). 목적지 Compute는 여전히 `Full=true`(새 오더 수용 불가) — IF-05만 예외.
- **[PASS] HP-3 (푸시 full→!full 재푸시)**: `EC3_HP3_...` — 셀 1개 해제(`FreeCellCount==1` DB) → 관찰 타이머가 !full 전이 감지 → 가짜 RCS `LastFor(chute).Ready==true` + `CountFor` 전이당 정확히 1건(WaitUntilExact stableCount:6 폭주 0).
- **[PASS] EC-1 (소터 FULL→NG)**: `EC1_...` — 3셀 전부 점유 + 새 오더(ORD-SORTER-OTHER, barcode "SORTER-OTHER-BC", 활성 assignment 없음=재사용 불가) → Compute `Full=true·Ready=false·Reason=Full` + IF-05 실 HTTP `{result:"NG", chuteNo:null}`(200, 도메인 거부) + piece_event reason=FULL(DB 단언).
- **[PASS] EC-2 (소터 PAUSED/비활성→NG)**: `EC2_...` — 케이스A Status=PAUSED → Compute `Paused=true·Reason=Paused` + IF-05 실 HTTP NG·chuteNo=null. 케이스B IsActive=false → Compute `Paused=true·Reason=Paused`(직접 호출 단언). 비활성은 IF-05 와이어상 NO_DEST 경로(`DbRepositories.cs:120`가 availability 이전 차단 — 코드 확인)로 NG, ComputeSorter 비활성→paused 매핑 산출원 정확성은 Compute 직접 단언. 테스트 주석이 이 경로를 정확히 명시(허위 주장 없음).
- **[PASS] EC-3 (푸시 !full→full ready=false)**: `EC3_HP3_...` — 정렬 ready=true 안정 후 3셀 전부 점유(`FreeCellCount==0`) → 관찰 타이머 full 전이 → 가짜 RCS `Ready==false` + 전이당 정확히 1건(중복 0·무변화 폴 폭주 0, stableCount:6).
- **[PASS] EC-4 (paused 단독 전이)**: `EC4_...` — NORMAL ready=true → Status=PAUSED(셀 무변) → 가짜 RCS `Ready==false` 1건. full과 독립적 paused 단독 전이 입증.
- **[PASS] EC-5 (동시성 원자성)**: `EC5_...` — 6스레드 동시 배정/해제(각 40회 토글) + 관찰자가 Compute 반복 호출. **내부 불변식**(`full ⟹ !ready`, `ready ⟹ !full && !paused && online`) 위반 **0건**(observations>0). quiesce 후 full⟺빈셀0 등가성 확정(전부점유→full=true·ready=false / 전부해제→full=false). **원자성 구조 근거**: `ComputeSorter` full 쿼리(`:174-178`)는 `db.Cells.Any(c=> enabled && !db.CellAssignments.Any(active))` — EF가 단일 SQL(상관 서브쿼리)로 번역, 중간 materialize·check-then-act 분리 없음. `ready = !full && !paused && decision.Ready`는 두 read 완료 후 합성 → `full && ready` 구조적 불가. paused/full 두 쿼리는 동일 스코프 별도 round-trip이나, 불변식이 합성 후 자기일관 → torn-read 위험 없음.

### 무변경 가드 (git diff develop — 직접 실행)
- **보호 zone 0줄**: `git diff develop -- src/Wcs.PlcGateway src/Wcs.Sim3ds src/Wcs.Core/DepositDecider.cs src/Wcs.Data src/Wcs.Migrations.Sqlite src/Wcs.Migrations.SqlServer | wc -l` = **0**. `src/Wcs.Core` 전체 디렉터리 diff = **0줄**(DepositDecider 순수성·레지스터맵 불변).
- **RcsController 인바운드 무변경**: diff는 IF-05 `availability` 람다 + 주석 3줄에만 국한. IF-09(arrival)·IF-10(deposit) 핸들러 본문 0줄 변경 — 인바운드 회귀 0.
- **DenyReason 우선순위 변경(Full→Paused 우선)**: 와이어 결과 동일(둘 다 block!=None→NG). 소터가 full&&paused 동시일 때 내부 reason 라벨만 PAUSED(우선) — 계약 명시 우선순위(Offline>Paused>Full)와 일치, 와이어 회귀 0.

### Evaluation Criteria 충족 (가중치)
- **(40%) full/paused 산출 정확성**: 빈셀0→Full·ready=false / 빈셀≥1→!Full / PAUSED·비활성→Paused·ready=false. 단일 원자 쿼리. 오더 재사용 예외(Q1) 정확. HP-1/2·EC-1/2/5로 입증. **충족**.
- **(25%) 두 소비자 결선**: IF-05 소터 full/paused→NG(chuteNo=null), piece-aware 예외 정확. 푸시 ready 전이 반영(false/true 재푸시) 전이당 1회. EC-1/2/3/4·HP-2/3로 입증. **충족**.
- **(15%) 회귀 0**: 기존 76 전부 GREEN. 보호 zone diff 0. **충족**.
- **(10%) DI·경계 정합**: IServiceScopeFactory 경유(captive 0). DepositDecider 순수·Wcs.Core 의존성 0 불변. **충족**.
- **(10%) 동시성·예외 격리**: 빈셀 평가 SQL 원자. Observe/RunSorterObserveLoop 이중 try-catch(`:203-220`,`:231-241`)로 Compute 예외가 관찰 루프를 죽이지 않음. teardown exit 0. **충족**.

### Completion Conditions
- [x] dotnet build 경고 0, dotnet test 전체 GREEN(신규 VS 포함) — 83/83 exit 0.
- [x] VS 전부 자동화 테스트 통과 — 가짜 RCS 수신 본문·실 cell_assignment DB 상태 ground-truth(인메모리 카운터 단독 PASS 아님).
- [x] 무변경 영역 diff 0(Modbus·Sim3ds·DepositDecider·핸드셰이크·스키마).
- [x] Evaluator 독립 재실행으로 generator 주장 검증(fresh evidence).

### 메타 관찰(비차단)
- **EC-5 프로브 강도**: 내부 불변식 + quiesce 등가성만 검사(별도 free-count 재조회 비교는 읽기시점차 위양성이라 배제 — 정당). 진성 동시 클레임 경합(barrier 동시관찰)은 아님. **단 이번 경합 표면은 P2(아웃바운드 클레임)와 달리 "단일 SQL 원자 read"이므로 합성 모순 자체가 구조적 불가** — barrier 프로브로 추가 입증할 app-level claim 상태가 없음(공유 가변 상태는 DB 1테이블, 부분유니크가 일관성 보장). 따라서 P2 barrier 프로브 교훈은 이 스프린트엔 비적용(검토 후 판단). full⟹!ready 불변식이 SQL 원자성을 정확히 반영.
- **신규 테스트 파일 untracked**: `tests/Wcs.Tests/SorterCellFullnessTests.cs`는 아직 `git add` 전(working tree에 존재·컴파일·실행됨). 커밋은 team-lead — staging 포함 확인 권고.

### 결론
계약 전 항목 충족. 4-Tier 독립 코드리뷰(orchestrator Step 4.5)를 거쳐 commit 진행 권고. 커밋·push는 team-lead.

---

# Sprint Feedback — S-소터셀수량full + 슈트 IF-05 정정 — APPROVED

## Phase 3 Evaluate 결과 (Evaluator fresh evidence, 미커밋 working tree `feat/sorter-cell-qty-full`, 2026-06-25)

**최종 판정: APPROVED** — 계약 Verification Scenarios(HP-1~5·EC-1~7=12)·Completion Conditions·Evaluation Criteria(30/20/15/15/10/10)·사용자 확정 4건(Q1~Q4) 전부 fresh 직접 실행 증거로 충족. 보호 zone diff 0·DB 스키마/마이그레이션 무변경·teardown 회귀 0. 셀 수량 산출은 sorter_command(COMPLETED) JOIN piece.qty(piece별 1건·cell_assignment 비사용) DB ground-truth로 단언. 동시성은 단일 응답 내부 불변식(full⟹!ready) + EC-5 375회 관찰 0모순 + quiesce 수렴으로 입증.

### Fresh evidence (직접 실행 — generator 주장 신뢰 아님)
- **build**: `dotnet build Wcs.sln` → **경고 0개 / 오류 0개, BUILD_EXIT=0** (8개 프로젝트 — 마이그레이션 어셈블리 2개 포함).
- **full test**: `dotnet test Wcs.sln --no-build --blame-hang-timeout 120s` → **통과! 실패:0 통과:88 건너뜀:0 전체:88, TEST_EXIT=0**. Blame "시퀀스 파일이 생성되지 않습니다"(teardown 전용 행 0·hangdump 0). 기존 83 → 88 = +12 신규 SorterCellFullnessTests − 7 구. teardown hang 재발 0.
- **신규 SorterCellFullnessTests 단독**: 필터 실행 → **통과:12 exit 0**(HP-1/2/5·EC-1~7, EC-6은 Theory 3행).
- **flaky 0 (타이밍/동시성 표적 ≥5회)**: `SorterCellFullnessTests | RcsPushTests` 필터 **5/5회 연속 18/18 GREEN·exit 0**. 비결정성 0. (RUN 1~5 각 exit=0 통과:18 실패:0, 중단/hangdump 0.)
- **EC-5 동시성 프로브 강도**: console 캡처 → `[EC-5] 6스레드 동시 적재/배정/해제 + Compute 375회 — 내부 모순 0건`. 375 관찰은 단일 idle 경로(P3 함정)가 아닌 진성 경합 샘플.

### 사용자 확정 4건(Q1~Q4) — 코드 직접 검증
- **[PASS] Q1 (push ready도 셀 작업수량 반영)**: `ComputeSorterFull`(`DestinationStatusService.cs:192-226`) = `빈 enabled 셀 없음 AND 모든 활성 배정 셀 작업수량 도달`. 빈셀≥1→false(`:206`), 배정셀 중 미달≥1→false(`:217-221`), 둘 다 없으면 true(`:225`, enabled 0개도 true). `ComputeSorter`(`:289`)가 이 합성 full 산출, `ready = !full && !paused && decision.Ready`(`:292`). IF-05 piece-aware는 셀 수량 산출원(`LoadedQtyByCell`) 공유. 푸시 변화원=기존 150ms 관찰 타이머(`DestinationStatusPusher.cs` diff 0). 행동 입증: HP-5·EC-7.
- **[PASS] Q2 (sorter_command COMPLETED JOIN piece.qty 합)**: `LoadedQtyByCell`(`:157-172`) = COMPLETED sorter_command JOIN piece.qty, DISTINCT (CellId,PieceId,Qty)로 piece별 1건(재시도 중복 0), GroupBy(CellId) SUM. cell_assignment는 qty 산출 비사용(점유 판정·오더 셀 식별 전용 — grep 확인). EfSorterCommandJournal.Finalize Success→COMPLETED 정합. 행동 입증: EC-1(실 sorter_command 행 ground-truth)·EC-6.
- **[PASS] Q3 (Capacity NULL/≤0 = 무제한)**: `IsCellAtCapacity`(`:178-179`) = `capacity is int cap && cap > 0 && currentQty >= cap`. NULL/≤0→무제한, 양수일 때만 `>=`. 행동 입증: EC-4(NULL+현재100×3→Full=false·OK)·EC-6(경계 등호).
- **[PASS] Q4 (슈트 full·paused 둘 다 OK·소터 NG 유지, 차단점 2곳 dest 분기)**: 차단점①`DbRepositories.cs:73-79`(order-level PAUSED를 SORTER_3D일 때만)·`:128-129`(blocked=!IsActive||(SORTER_3D&&Status!=NORMAL)). 차단점②`RcsController.cs:67-79`(dt!=Sorter3D→None 슈트 통과, 소터만 Paused/Full 차단·piece-aware 예외). ChuteCapacity 집계 무변경(diff 0), OnReserved 슈트만(`:83`·초과 허용). 행동 입증: HP-3/4·EC-3.

### Verification Scenarios — 항목별 PASS/FAIL (실 sorter_command/cell.Capacity DB·가짜 RCS 본문 ground-truth)
- **[PASS] HP-1 (배정 셀 여유→OK·NORMAL)**: Capacity=10·cellNo1 현재3(실 sorter_command COMPLETED), 3셀 점유(FreeCellCount==0) → HasRoom==true + Compute.Full==false + IF-05 실 HTTP {OK,chuteNo} + piece_event reason=NORMAL.
- **[PASS] HP-2 (새 오더 빈 셀→OK)**: 빈셀3 + 셀 미보유 바코드 → Compute.Full==false + IF-05 {OK}. free-cell 회귀 가드.
- **[PASS] HP-3 (슈트 FULL→OK)**: `If05_Chute_Full_ThenCleared_Normal`(반전) — OnReserved(workFullQty)→GetHold==Full 확인 후에도 IF-05 {OK,chuteNo=1}, 비움 전후 둘 다 OK.
- **[PASS] HP-4 (슈트 PAUSED→OK)**: `If05_Chute_Paused_Ng`·`VS2_If05_PausedOrder_NgPaused`·`S8_Chute_Paused_Ng`(반전) — 슈트 PAUSED → IF-05 {OK,chuteNo=6}. order-level PAUSED 차단이 슈트 미적용.
- **[PASS] HP-5 (push ready 일부 여유→유지·Q1)**: 빈셀0·cell1·2 도달(현재5=Cap5)·cell3 미달(현재3) → Compute.Full==false + 가짜 RCS Ready==true 유지 + CountFor 무변화(stableCount:8 폭주 0).
- **[PASS] EC-1 (셀 full→NG·FULL)**: SORTER-FULL-BC 배정셀 현재5≥Cap5·전셀 도달·빈셀0 → HasRoom==false + Compute.Full==true·Ready==false·Reason==Full + IF-05 {NG,chuteNo=null}(200) + piece_event reason=FULL. 실 sorter_command 행 ground-truth.
- **[PASS] EC-2 (빈셀0+오더 미보유→NG)**: 3셀 점유+전부 도달·SORTER-NEW-BC(배정 없음) → 재사용 불가 + Full==true + IF-05 NG. m4p4 회귀 가드.
- **[PASS] EC-3 (소터 PAUSED NG 유지)**: 소터 PAUSED → Compute.Paused==true·Reason==Paused + IF-05 NG. (B) 정정이 소터 미파손.
- **[PASS] EC-4 (Capacity NULL=무제한)**: 시드 NULL 단언+3셀 점유+현재100×3 → HasRoom==true(무제한) + Full==false + IF-05 OK.
- **[PASS] EC-5 (동시성 원자성)**: 6스레드 적재(sorter_command COMPLETED)/배정/해제 churn + Compute 375회 → 내부 불변식(full⟹!ready, ready⟹!full&&!paused&&online) 위반 0건. quiesce 전부도달→Full=true·ready=false / 빈셀1→Full=false 수렴. 구조 근거: ready가 동일 materialized full에서 합성→full&&ready 단일 응답 구조적 불가. ComputeSorterFull 2쿼리이나 계약 명시 eventually-consistent + 응답내 불변식 자기일관 → torn-read가 모순 불생성. m4p4 경계(단일 SQL 원자 read=불변식 단언 충분, barrier 비적용) 동형.
- **[PASS] EC-6 (셀 경계 ≥ 등호)**: Theory 3행 — 4<5=OK / 5==5=NG / 6>5=NG. HasRoom==(currentQty<capacity) + IF-05 결과 일치.
- **[PASS] EC-7 (push ready=false 전이·Q1)**: 빈셀0+cell3만 여유(현재3)→ready=true 안정(stableCount:6)→cell3 +2(현재5 도달)→가짜 RCS Ready==false + CountFor 전이당 1건(폭주 0)→빈셀1 복귀(assignment 해제)→Ready==true 재푸시 1건.

### 무변경 가드 (git diff develop — 직접 실행)
- **보호 zone 0줄**: `git diff --stat develop -- src/Wcs.PlcGateway src/Wcs.Sim3ds src/Wcs.Core src/Wcs.Data src/Wcs.Migrations.Sqlite src/Wcs.Migrations.SqlServer` = **empty(0줄)**. DepositDecider 순수·레지스터맵·스키마·마이그레이션 불변.
- **ChuteCapacity 집계 0줄 / 푸시 멱등 기계(DestinationStatusPusher) 0줄**: 각 `git diff --stat develop` = 0. 전이추적·per-dest 락·in-flight·150ms 관찰 타이머·집계 모델 무변경 — Compute 내부 SorterFull 의미만 수량 반영 확장.
- **production diff**: RcsController(23줄·IF-05 availability 람다+주석)·DbRepositories(16줄·차단점 dest 분기)·DestinationStatusService(173줄·신규 산출 함수). 인바운드 IF-09/IF-10 핸들러 본문 0줄. merge-base==develop@HEAD(클린 descendant).
- **의도적 반전 5건(슈트 NG→OK)**: ApiIntegrationTests 3 + ScenarioTests 2 — assertion만 NG→OK 갱신(삭제 아님·HTTP 왕복 구조 유지). 계약은 3건 명시했으나 ScenarioTests 동일행위 2건 추가(미반전 시 RED)는 정당. 소터 NG는 EC-1/2/3 불변 단언.

### Evaluation Criteria 충족 (가중치)
- **(30%) 소터 셀 수량 full 산출 정확성**: COMPLETED JOIN piece.qty·DISTINCT piece·중복 0 ≥ Capacity(양수)→full, NULL/≤0=무제한. 동일 스코프 스냅샷. EC-1/4/5/6. **충족**.
- **(20%) IF-05 결합 모델 정확성**: 새 오더(빈셀≥1)=OK / 기존 오더 여유=OK / full+빈셀0=NG. read predicate가 IF-10 SelectCell ①분기와 동형(drift 0). HP-1/2·EC-1/2/6. **충족**.
- **(15%) push ready 수량 반영(Q1)**: SorterFull=빈셀0 AND 전배정셀 도달일 때만 false. 여유≥1→유지. 전이당 1건. HP-5·EC-7. **충족**.
- **(15%) (B) 슈트 OK + 소터 NG 유지**: 차단점①② 슈트 통과·소터 불변. 슈트 readiness 푸시 계속. HP-3/4·EC-3·반전 5건. **충족**.
- **(10%) 회귀 0 + 의도적 반전**: 88/88 GREEN·보호 zone diff 0·슈트 NG 5건 OK 반전(삭제 아님)·무변경 항목 diff 0. **충족**.
- **(10%) 동시성·경계·DI 정합**: 셀full⟹piece NG / SorterFull⟹push ready=false(EC-5 375회 0모순)·IServiceScopeFactory(captive 0)·DepositDecider 순수·Wcs.Core diff 0·예외 격리·teardown exit 0. **충족**.

### Completion Conditions
- [x] dotnet build 경고 0, dotnet test 전체 GREEN(신규/반전 포함) — 88/88 exit 0.
- [x] VS 전부 자동화 통과 — 실 sorter_command/cell_assignment/cell.Capacity DB + 가짜 RCS 수신 본문 ground-truth.
- [x] 무변경 영역 diff 0(Modbus·Sim3ds·DepositDecider·핸드셰이크·스키마·ChuteCapacity 집계).
- [x] Evaluator 독립 재실행 검증(fresh evidence — HTTP 본문·piece_event.reason·DB 셀수량·push 카운트 raw).

### 메타 관찰(비차단 — Minor)
- **스테일 테스트명 4건**: 반전된 슈트 테스트가 옛 `_Ng` 이름 유지(`If05_Chute_Paused_Ng`·`VS2_If05_PausedOrder_NgPaused`·`S8_Chute_Paused_Ng`·`If05_Chute_Full_ThenCleared_Normal`) — 본문은 OK 단언. 동작 무영향(주석에 반전 명시)이나 가독성 저하. 다음 정리 sprint에서 `_Ok` 개명 권고. (계약이 "삭제 금지·갱신"만 요구·assertion 정확 — 비차단.)
- **ComputeSorterFull 2-쿼리**: 단일 SQL 1문이 아닌 동일 스코프 2 round-trip(cells+occupancy, loaded-qty). 계약 "단일 원자 쿼리" 문구 대비 실제 요구 불변식(응답내 full⟹!ready + eventually-consistent)은 충족(EC-5 375회 0모순). 상관 서브쿼리로 단일 SQL화 가능하나 현 구조로 계약 불변식 위반 0 — 비차단. Step 4.5 코드리뷰에서 구조 재확인 권고.

### 결론
계약 전 항목(HP-1~5·EC-1~7·Completion·Criteria·확정 Q1~Q4) fresh 증거로 충족. 무변경 zone diff 0·teardown exit 0·flaky 0(5/5). 4-Tier 독립 코드리뷰(orchestrator Step 4.5)를 거쳐 commit 진행 권고 — 신규 SorterCellFullnessTests staging 포함 확인. 커밋·push는 team-lead.

**APPROVED**

---

## 재평가 — 독립 코드리뷰 BLOCK(MAJOR-1 + MINOR-2) 수정 후 (Evaluator fresh evidence, 2026-06-25)

**판정: APPROVED (수정 검증 완료)** — 독립 코드리뷰가 적발한 MAJOR-1(IF-05 room 게이트 ↔ IF-10 SelectCell 용량 무지 비대칭 → 오더 다중 셀 시 full 셀 재사용으로 Capacity 초과 적재)과 MINOR-2(주석)가 해소됨을 fresh 직접 실행으로 확인. 내 1차 APPROVE는 동형성을 assignment-finding predicate에만 검증하고 **room/capacity 차원·SelectСell 셀 선택**을 검증 안 한 사각이었음 — 정당한 BLOCK.

### 코드리뷰 지적 핵심(내 1차 사각)
- IF-05 room 게이트(`SorterHasAssignedCellWithRoomForBarcode`)는 "오더 배정 셀 중 ∃여유 셀"인데, `EfCellSelector.SelectCell` ①분기는 `FirstOrDefault`로 **임의(용량 무관)** 배정 셀 재사용 → 오더가 full 셀+여유 셀 동시 보유 시 IF-05 OK인데 SelectCell이 full 셀 골라 초과 적재. "IF-05 OK ⟹ 적재 가능" 위반. (내 "subset" 논리 오류: IF-05는 ∃여유셀, SelectCell은 임의 첫 셀 — 같은 셀 보장 없음.)

### 수정 검증 (코드 직접 + 동작)
- **[PASS] 셀 수량 로직 단일 추출(byte-consistent)**: 신규 `src/Wcs.Api/SorterCellQty.cs`(internal static)가 `LoadedQtyByCell`·`IsCellAtCapacity`를 한 곳에 두고 IF-05·SelectCell·SorterFull 3자 공유. `DestinationStatusService` private 복사본 제거·위임. → "동형"을 복제-주장이 아니라 **단일 구현 공유**로 구조 보장(drift 원천 차단).
- **[PASS] SelectCell ①분기 용량 인식**: `assignedCells`(그 오더 활성 배정 셀)를 `OrderBy(CellNo)`(결정적) 후 `SorterCellQty.IsCellAtCapacity`로 **여유 셀(현재<Capacity) 첫 셀만** 재사용. 전부 full이면 ②빈 셀 폴백, 없으면 ③null. (`DbRepositories.cs` SelectCell diff 확인 — `roomCell = assignedCells.FirstOrDefault(!IsCellAtCapacity(...))`.)
- **[PASS] 루트 원인 완결 — availability piece 단위 교정**: 기존 availability는 목적지-단위 `SorterFull`(다른 오더 여유 셀 포함)로 분기해 "A 셀 full·빈셀0이어도 B 오더 여유 셀 때문에 SorterFull=false면 A piece OK로 샘" 잔여 홀 존재. → `IDestinationStatusService.SorterCanAcceptBarcode` 신규 = `HasAssignedCellWithRoom(order) OR HasFreeEnabledCell` — **SelectCell 비-null 조건과 biconditional 동형**. `RcsController` availability: 소터 Paused 차단 후 `SorterCanAcceptBarcode ? None : Full`. 목적지-단위 SorterFull은 푸시 ready 전용 유지(피·목적지 단위 명확 분리). 동형성 검증: ∃여유배정셀→SelectCell① non-null / 없고 ∃빈셀→SelectCell② non-null / 둘 다 없음→SelectCell null & CanAccept false. биconditional 성립.
- **[PASS] EC-8(크로스-엔드포인트 정합)**: 오더 X가 cell1(full·현재5=Cap5)+cell2(여유·현재2) 보유·빈셀0 → IF-05 실 HTTP OK + `SelectCell("ORDX-BC")` == **cell2(여유, full 아님)** + 적재 후 현재수량 ≤ Capacity(실 sorter_command SUM ground-truth, 초과 0). **이 단언이 옛 FirstOrDefault 버그면 cell1 반환으로 FAIL** — 버그를 정확히 포착. console: `[EC-8] SelectCell 여유셀2 선택·초과 적재 0`.
- **[PASS] EC-9(piece 단위 — 다른 오더 여유 셀 무용)**: A(cell1 full)+B(cell2 여유)+빈셀0 → `Compute.Full==false`(B 여유) BUT `SorterCanAcceptBarcode("ORDA-BC")==false`·IF-05(A) 실 HTTP **NG**·`SelectCell("ORDA-BC")==null` / `SorterCanAcceptBarcode("ORDB-BC")==true`·IF-05(B) OK·`SelectCell("ORDB-BC")==2`. **EC-5 단일 Compute 관찰자로는 못 잡던 IF-05↔IF-10 크로스-엔드포인트 일관성**을 정면 단언. console: `[EC-9] IF-05(A) NG·SelectCell(A) null / IF-05(B) OK·SelectCell(B)=2 (동형)`.
- **[PASS] MINOR-2**: `ComputeSorterFull` 주석 "단일 원자 쿼리" → "같은 스코프 2-쿼리 순차(보수적 스냅샷)"로 정정 + record 내부 불변식(full⟹!ready) 성립 명시.

### Fresh evidence (직접 실행)
- **build**: `dotnet build Wcs.sln` → 경고 0/오류 0, BUILD_EXIT=0.
- **full test**: `dotnet test Wcs.sln --no-build --blame-hang-timeout 120s` → **실패:0 통과:90 전체:90, TEST_EXIT=0**(88→90 = +EC-8 +EC-9). Blame "시퀀스 파일 미생성"=teardown hang 재발 0·hangdump 0. **기존 88 회귀 0**.
- **flaky 0**: `SorterCellFullnessTests|RcsPushTests`(20건) **5/5회 연속 GREEN·exit 0**.
- **EC-8/EC-9 console ground-truth 캡처**(위 인용).

### 무변경 가드 재확인 (git diff develop)
- **보호 zone 0줄**: `git diff --stat develop -- src/Wcs.PlcGateway src/Wcs.Sim3ds src/Wcs.Core src/Wcs.Data src/Wcs.Migrations.Sqlite src/Wcs.Migrations.SqlServer src/Wcs.Api/ChuteCapacityService.cs src/Wcs.Api/DestinationStatusPusher.cs` = empty(0). 푸시 멱등 기계·capacity 집계·스키마·마이그레이션 불변.
- **DbRepositories 변경 국한**: 4 hunk — QueryDestination 차단점 2(원 스프린트)·EfCellSelector 클래스 doc 주석(`@@-547` 컨텍스트)·SelectCell ①분기 용량 인식. `EfDepositRecorder.RecordDeposit` 멱등 로직·`EfAlarmSink`·`EfSorterCommandJournal`·`EfArrivalRecorder`·`EfAgvFloorResolver` 본문 0줄.
- **신규 파일**: `src/Wcs.Api/SorterCellQty.cs`(?? untracked·컴파일·실행됨). 커밋 시 staging 포함 확인(team-lead).

### 결론(재평가)
독립 코드리뷰 MAJOR-1·MINOR-2 해소 확인. 크로스-엔드포인트 불변식("IF-05 OK ⟺ SelectCell 적재 가능 ⟹ Capacity 초과 0")이 단일 공유 로직(SorterCellQty) + piece 단위 게이트(SorterCanAcceptBarcode) + EC-8/EC-9 명시 단언으로 보강됨. 회귀 0(90/90)·teardown exit 0·flaky 0(5/5)·무변경 zone diff 0. team-lead가 수정분 재리뷰 후 커밋 권고 — 신규 SorterCellQty.cs·SorterCellFullnessTests staging 포함 확인.

**APPROVED (수정 후 재검증)**

---

# S-SQLSERVER-FK-CASCADE (SQL Server 1785 FK 캐스케이드 순환 제거 — 마이그레이션 스쿼시) — Evaluator 평가 (2026-06-30)

> Evaluator(독립) · 개정 2(스쿼시) 기준 · 모든 증거 fresh 재실행 · 코드 미수정.
> ground truth: branch=fix/sqlserver-fk-cascade, sprint-log.md L1596 `## IMPLEMENTATION COMPLETE (S-SQLSERVER-FK-CASCADE)` 마커 확인, diff 실재(57 ins / 7144 del) → 진성 핸드오프.

## 판정: APPROVED — §3 ①~⑤ + §5 시나리오 1~4 전부 PASS (1 iteration)

환경: dotnet SDK 10.0.300, EF CLI 9.0.10, sqlcmd 15.0, **SQL Server 2025 (RTM-GDR) 17.0.1115.1 Enterprise Developer @ localhost** (LocalDB 아님 — 계약 ① 요구 충족).

### ① SqlServer 콜드스타트 실제 적용 (40%) — PASS
독립 DB `WcsCascadeEval`(Generator의 WcsCascadeTest와 다른 이름)에 빈 DB부터 직접 적용:
- `dotnet ef database update --project/--startup-project=Wcs.Migrations.SqlServer --connection "...Database=WcsCascadeEval..."` →
  `Build succeeded.` → `Applying migration '20260630012916_Initial'.` → `Done.` **exit 0, 1785·207 0건**.
- sqlcmd 생성 DB 직접 검사 (fresh):
  - **사용자(도메인) 테이블 정확히 16개**: agv·alarm·cell·cell_assignment·chute_detail·destination·destination_event·induction·order_item·piece·piece_event·plc_event·printer·sorter_command·wcs_order·work_batch (+ 히스토리 테이블 `__EFMigrationHistory` 1개 = sys.tables 총 17. 히스토리명이 비표준 `__EFMigrationHistory`(중간 's' 없음)라 표준명 필터 미매칭 → 처음 17로 표시됐으나 실제 도메인 16 확정).
  - **FK 17개 전부 delete_referential_action_desc=NO_ACTION, CASCADE 0건**(group-by `NO_ACTION 17` + `cascade_count=0` 이중 확인). 원 1785 메시지의 대표 FK `FK_sorter_command_piece_PieceId` → NO_ACTION 확인. (명시 Restrict 10 + nullable FK 7개 EF 기본 ClientSetNull→DDL NO ACTION = 17.)
  - **filtered unique index 2개 모두 PascalCase**: `UQ_piece_pid_active_status = ([IsActive]=(1) AND ([Status] IN (...)))`, `UQ_cell_assignment_cell_active = ([ReleasedAt] IS NULL)`. snake `is_active` 0건 → 207 재발 불가.
- 검사 후 `DROP DATABASE [WcsCascadeEval]` 완료, `WcsCascade%` 잔여 DB 0(흔적 0).

### ② 146 테스트 GREEN·회귀 0 (25%) — PASS
`dotnet test Wcs.sln` (fresh, 백그라운드 독립 실행) → `통과! - 실패: 0, 통과: 146, 건너뜀: 0, 전체: 146, 기간: 12s`, exit 0.
경고는 기존 NU1903(SQLitePCLRaw transitive 취약성)뿐 — 본 변경 무관·신규 0. `git diff --stat -- tests/` 빈 출력 → 테스트/단언 코드 변경 0.

### ③ 양 provider No changes (20%) — PASS
- SqlServer: `has-pending-model-changes` → `No changes have been made to the model since the last migration.` exit 0.
- Sqlite: 동일 → `No changes...` exit 0.
→ 스쿼시된 단일 Initial이 현재 모델을 정확히 재현(스냅샷 정합).

### ④ 무변경 가드 (10%) — PASS
`git status --porcelain` 코드 범위 = `src/Wcs.Data/WcsDbContext.cs`(M) + 양 `Migrations/`(구 6.cs씩 D + 신규 Initial.cs/.Designer ?? + ModelSnapshot M)에 **국한**.
- 보호 zone `git status -- src/Wcs.Core src/Wcs.PlcGateway src/Wcs.Sim3ds src/Wcs.Api Entities.cs` = **빈 출력(0줄)**. DbSeeder·DbInitializer·appsettings = 0줄.
- WcsDbContext.cs 변경 본질 = 10개 FK에 `.OnDelete(DeleteBehavior.Restrict)` 체이닝 추가 + 주석. (diff의 `HasForeignKey(...);`→`HasForeignKey(...)` 라인은 `;` 종결자가 새 `.OnDelete()` 줄로 이동한 것뿐 — 구조 변경 아님.) 컬럼/테이블/인덱스/UNIQUE/CHECK/PK 변경 0.
- 양 ModelSnapshot diff = **정확히 Cascade→Restrict 10건씩**(SqlServer 10, Sqlite 10), 그 외 0줄.
- 신규 SqlServer Initial onDelete 분포 = Restrict 10 / Cascade 0. CreateTable 16(양 provider).

### ⑤ 스쿼시 무결성 (5%) — PASS
- provider당 단일 Initial(SqlServer `20260630012916_Initial`, Sqlite `20260630012926_Initial`). 구 3개(Initial·P2a·P1_If09Arrival)·구 스냅샷 전부 삭제.
- 새 SqlServer Initial `is_active`(snake) **0건**(grep).
- 구 마이그레이션 ID/클래스명(`20260616072550`·`P2a_PieceNullable`·`P1_If09Arrival`·`If09Arrival` 등) **src/tests 참조 0건**(docs/tasks 문서에만 — 코드 무영향).
- 최종 스키마 불변 입증: 신규 단일 Initial = 구 3 마이그레이션 누적 효과와 동일. 구 첫 Initial 대비 unique index 9→10 차이는 P2a가 도입한 `UQ_cell_assignment_cell_active` 1개(+`UQ_piece_pid_active_status` 필터/컬럼 정정)로, **has-pending-model-changes=No changes**가 "현 모델 정확 재현"을 권위 입증. 구 P2a의 버그 인덱스 `UQ_piece_pid_where_active filter:[is_active]=1`(snake, 207 원흉)은 재생성 Initial에서 제거되고 올바른 `[IsActive]`로 대체됨(라이브 DB 검사와 일치).

## §5 Verification Scenarios
1. **SqlServer 적용 성공(1785 0→drop)** — PASS(① 증거).
2. **SQLite 회귀 0(146 GREEN + 마이그레이션 무파손)** — PASS(② + Sqlite No changes + Sqlite Initial 16 CreateTable).
3. **양 snapshot 정합(둘 다 No changes)** — PASS(③).
4. **캐스케이드 의미 보존(앱 동작 불변·캐스케이드 의존 코드 0)** — PASS. Core/PlcGateway/Sim3ds/Api/Entities/DbSeeder diff 0줄(④). FK 삭제 거동(메타)만 Cascade→NoAction, 앱은 append-only+배치 퍼지로 캐스케이드 미의존(계약 §0 grep 확정 사실 재확인 — src 변경 0이 의존 미도입 입증).

## 결론
운영 provider(SqlServer 2025 실 인스턴스)에서 1785·207이 fresh 콜드스타트로 0건 입증됐고, 전 16 도메인 테이블 + 17 FK 전부 NO_ACTION + filtered index PascalCase가 ground-truth(sqlcmd sys.* 직접 조회)로 확인됨. 146/146 GREEN·회귀 0·양 provider No changes·무변경 가드(보호 zone 0줄)·스쿼시 단일 Initial 무결성 전부 PASS. 메타 교훈("SQLite 단일 경로는 SqlServer DDL 제약을 구조적으로 못 봄")을 실 SQL Server database update fresh 실행으로 정확히 닫음.

**APPROVED**

## Step 4.5 독립 코드리뷰 (orchestrator, opus, 팀 외부) — BLOCKING 0건

독립 Opus 코드리뷰어가 Evaluator 미커버 영역(아키텍처/명명/주석/유지보수성/의미정확성/보안)을 검토 → **BLOCKING(Critical) 0건, 머지 가능**. 중점 5항목 전부 PASS:
1. **FK 의미 정확성** — 필수 FK 정확히 10개 Restrict·nullable 7개 EF 기본 유지(1785 해소 필요·충분, 과소/과다 0). 앱 캐스케이드 미의존(.Remove/ExecuteDelete 0) → 런타임 무해. 부수 발견: 구 SqlServer Initial의 piece→destination Cascade 파일-스냅샷 드리프트를 스쿼시가 정정.
2. **주석 품질** — "왜 Restrict인지" 수렴 지점별 한국어 설명. 3. **스쿼시 위생** — Down() 자식→부모 역순 정합·Designer 순수 생성물·스냅샷 diff 정확히 10 FK. 4. **양 provider 일관성** — 차이가 의도된 고유부에만 국한. 5. **보안/성능** — 인덱스 구조 불변·207 오타 해소 확인.

### Minor (비차단 — 다음 sprint Generator 참고, fix-only iteration 불요)
- **MINOR-A** (`src/Wcs.Data/WcsDbContext.cs` OnModelCreating, 대표 L104-107): 주석에 "이 `.OnDelete(Restrict)` 10개를 Cascade로 환원/제거 금지 — SQL Server 1785 재발(다중 캐스케이드 경로), 변경 시 실 SQL Server `database update` 재검증 필수" **명시적 회귀 금지 경고 부재**. EF는 필수 관계 기본이 Cascade라 `.OnDelete()` 줄을 단순 삭제만 해도 즉시 회귀(이 스프린트가 1785·207로 두 번 겪음). 한 줄 경고 추가 권고(+ `tasks/lessons.md` 등재).
- **MINOR-B** (`docs/SPEC.md` L130): 스쿼시로 폐기된 마이그레이션명 `P2a_PieceNullableDestId_...`가 과거 완료 이력 로그에 잔존. forward 배포지시 아닌 과거 이력 항목이라 영향 경미 → "(2026-06-30 스쿼시로 단일 Initial 통합)" 각주 권고(필수 아님). 기존 `tasks/todo.md` SPEC.md 문서부채 항목과 함께 별도 문서 정리에서 처리.
- **INFO**: 빌드 NU1903 경고 2건(SQLitePCLRaw transitive 취약성)은 사전존재·본 변경 무관(Done "0 warning"은 신규 warning 0 기준 — Evaluator가 기존 transitive advisory로 분류 확인).
