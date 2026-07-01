# Sprint Contract — S-OBSERVABILITY (현장 chuteNo 30→1 일원화 + 전 동작 콘솔(Serilog)·DB(operation_log) 상세 로깅)

> 작성: Planner Subagent · 2026-06-30
> 본 계약은 **WHAT / WHERE / 검증(Acceptance)** 만 규정한다. **HOW(Serilog 부트스트랩 방식·싱크 구성 코드·operation_log 기록 서비스의 채널/배치 구현·변화분 감지 자료구조·각 동작 지점의 로그 호출 위치 세부)는 Generator가 결정**한다.
> 3-Tier: Planner(이 문서) → 사용자 확인 → Generator ↔ Evaluator 루프.
> 직전 스프린트(S-FIELD-SEED-16CELLS)는 PR #22로 **이미 머지됨**(실 3DS 16셀 데이터 + base appsettings=SqlServer 전환). 이 계약은 그 결과를 전제로 한다.

> ### 개정 (Amendment) — 2026-06-30 (사용자 확인, chuteNo 범위 = 현장 데이터만)
> **결정**: chuteNo 30→1은 **현장 데이터(appsettings + seed-field-16cells.sql) 2지점만** 적용. **`DbSeeder.cs`는 변경하지 않는다**(dev/테스트 소터 chuteNo=30 유지 — 테스트 placeholder·실 3DS와 무관). 사유: DbSeeder가 이미 CHUTE 1~5를 시드해 소터 chuteNo=1이 `UQ_destination_chute_no` 전역 유니크와 충돌하고, 146 테스트가 DbSeeder 토폴로지(chuteNo=30)에 의존하므로 dev까지 바꾸면 CHUTE 재배치·테스트 영향 발생. 현장 DB(`Rcs3dsInterlockingWcs`, SeedOnStartup=false)엔 DbSeeder가 안 돌아 충돌 없음.
> **영향**: §2 IN A의 DbSeeder 변경(item 3) 철회. 단 base appsettings `Sorters[0].ChuteNo=1`이 테스트에 누설돼 DbSeeder 소터(30)와 불일치하면 146이 깨질 수 있으므로, **테스트 Sorters 구성이 chuteNo=30을 쓰도록 보장**(테스트 결선만 — DbSeeder/단언 토폴로지 의미 0 변경)해 C6(146 GREEN)을 충족하는 것은 Generator 책임.

---

## 0. 배경 / 전제 (확정 사실 — 추측 아님, 코드/문서 직접 확인됨)

- **현장 데이터 정정**: 3D Sorter 슈트번호가 그동안 `30`(임의 placeholder)으로 시드/구성돼 있었으나, **실 현장 16셀 데이터의 슈트번호는 `1`**이다. 사용자 확정. chuteNo=30이 박힌 지점 3곳을 `1`로 일원화하고 실 DB를 클린 재적재한다.
  - `src/Wcs.Api/appsettings.json` → `Sorters[0].ChuteNo: 30`
  - `scripts/seed-field-16cells.sql` → `DECLARE @sorterChute int = 30`
  - `src/Wcs.Data/DbSeeder.cs` → `SeedDestinations`의 `chuteNo=30`(dev 콜드스타트 시드 전용) + 주석 문구
- **⚠ chuteNo=1 충돌 주의(현장 확인 §로 격상 — 아래 §8)**: 현재 `DbSeeder.SeedDestinations`는 **슈트 1~5를 CHUTE 타입**으로 시드한다(`chuteNo=1`은 이미 CHUTE 점유). `destination`은 `UQ_destination_chute_no`(chute_no 전역 유니크)라 **같은 chute_no=1을 CHUTE와 SORTER_3D 둘 다로 만들 수 없다**. 따라서 dev DbSeeder에서 소터를 chuteNo=1로 바꾸면 기존 CHUTE chuteNo=1과 충돌한다. 실 현장 DB(seed-field-16cells.sql 대상)에는 CHUTE 1~5 시드가 없으므로 충돌이 없을 수 있으나, **dev 시드와 실 DB의 데이터 형상이 다르다.** → 이 충돌 해소(HOW)는 Generator가 결정하되, **반드시 입증으로 드러내고 재적재가 클린 성공해야** 한다(§5 Acceptance C7). dev/실DB 형상 차이는 Generator가 명시 문서화.
- **로깅 현황**: 현재 전 계층이 `ILogger<T>` 기본 콘솔 로깅만 사용. 구조화 싱크·파일 싱크·DB 영속화 없음. `src/Wcs.Api/Program.cs:28`에 `// TODO(M5-P2): Serilog 구조화 로깅` 명시 마커 존재. 프로젝트 CLAUDE.md 코딩 컨벤션 = "M5에서 Serilog 도입(레지스터 변화 + API 원문 구조화 기록)". 본 스프린트가 그 M5-P2 항목을 이행한다.
- **DB 스키마**: `src/Wcs.Data` EF Core, ERD.md 16테이블. provider 분기(SqlServer 운영 / SQLite 테스트·dev). 마이그레이션은 **양 provider 독립 어셈블리**(`Wcs.Migrations.SqlServer` / `Wcs.Migrations.Sqlite`), 각각 독립 ModelSnapshot. base appsettings `Database:Provider=SqlServer`(운영), 테스트 5개 팩토리는 `UseSetting("Database:Provider","Sqlite")`로 인메모리 SQLite 더블 강제.
- **FK 거동(절대 — 회귀 0)**: S-SQLSERVER-FK-CASCADE에서 **필수 FK 10개를 `OnDelete(DeleteBehavior.Restrict)` 명시**해 SQL Server 1785(다중 캐스케이드 경로)를 제거했다. 신규 테이블의 FK가 **Cascade를 도입하면 1785 재발**. 신규 FK는 Restrict(또는 nullable FK의 EF 기본 NO_ACTION)여야 한다. filtered index 컬럼명은 **물리 PascalCase**(`[IsActive]` 등)를 쓴다(207 오타 재발 금지).
- **이미 존재하는 도메인 이벤트 테이블(의미 변경 0)**: `piece_event`(IF05/08/10 REQ/RES·IF09_ARRIVAL·DECISION), `plc_event`(REG_CHANGE·WRITE·ONLINE·OFFLINE), `alarm`, `destination_event`, `sorter_command`. **이들은 그대로 둔다.** operation_log는 이들과 **중복 구현이 아니라**, "모든 동작을 단일 시계열로 한 테이블에서 관측"하는 **횡단(cross-cutting) 운영 로그**다(도메인 이벤트는 정규화된 상태/이력, operation_log는 통합 관측 스트림). 기존 테이블에 행을 더 넣거나 빼지 않는다.
- **절대규칙 재확인**: #1 PLC 쓰기는 **여전히 단일 큐(Channel)** 통과 — 로깅 추가가 큐를 우회하거나 추가 Modbus 호출을 만들지 않는다(로그는 부수 기록). #7 모든 시간/경로/레벨/롤링 설정은 appsettings(하드코딩 금지) — Serilog 레벨·싱크·파일 경로·롤링 주기·보존도 전부 appsettings. #8 판정 로직(DepositDecider)은 순수함수 유지 — 로깅 의존 0.

---

## 1. Goal (목표)

**(a) 현장 데이터 chuteNo 30→1 일원화**: 3D Sorter 슈트번호를 실 현장값 `1`로 통일(appsettings·seed 스크립트·dev DbSeeder 3지점) + 실 SQL Server DB 클린 재적재. 그 결과 **IF-05가 해당 소터 오더에 대해 chuteNo=1을 반환**한다.

**(b) 전 동작 관측성 확보 — 콘솔(Serilog) + DB(operation_log) 상세·구조화 로깅**: WCS의 모든 핵심 동작을 ①Serilog 구조화 콘솔 + 롤링 파일 싱크, ②신규 `operation_log` 단일 테이블에 시각·종류·상세로 기록한다. 고빈도 폴링(150ms)은 **변화분만**(레지스터 값 전이 시), 모든 쓰기·핸드셰이크 단계·상태전이·API 원문은 **전수** 기록한다.

**불변 (절대규칙·동작 보존)**: PLC 쓰기는 여전히 단일 큐 통과. 판정/핸드셰이크/레지스터맵의 **의미는 0 변경**(로그 호출만 부가). 기존 도메인 이벤트 테이블 의미 0 변경. 로깅 추가가 폴링(150ms)·핸드셰이크 타이밍·API 3s를 지연시키지 않는다. 로그 기록 실패가 본 동작(PLC/배정/핸드셰이크)을 막지 않는다(fail-safe, 단 삼키지 말고 자체 경고).

---

## 2. Scope — IN / OUT (파일·모듈 구체)

### IN (이번 스프린트가 만지는 것)

**A. chuteNo 30→1 일원화 (3지점 + 재적재)**
1. `src/Wcs.Api/appsettings.json` — `Sorters[0].ChuteNo`: `30` → `1`.
2. `scripts/seed-field-16cells.sql` — `@sorterChute` 기본값 `30` → `1`(+ 주석·요약 출력의 30 언급 정정).
3. ~~`src/Wcs.Data/DbSeeder.cs`~~ **(개정: 변경 안 함)** — DbSeeder 소터 `chuteNo=30` **유지**(dev/테스트 placeholder). CHUTE 1~5 전역유니크 충돌 회피 + 146 테스트 토폴로지 보존. **대신** base appsettings `Sorters[0].ChuteNo=1`이 테스트에 누설돼 DbSeeder 소터(30)와 불일치하지 않도록, **테스트 Sorters 구성이 chuteNo=30을 쓰도록 결선**(테스트 인프라만 — 단언/토폴로지 의미 0 변경)해 C6 충족. HOW는 Generator.
4. **실 SQL Server DB 클린 재적재**: 기존 chuteNo=30 데이터를 정리하고 chuteNo=1로 재적재해 §5 클린 상태(소터 destination 1개·셀 16·오더 16·order_item 16·active cell_assignment 16·piece 0) 달성. 재적재 방법(스크립트 재실행·기존 행 정리 절차)은 Generator 결정.

**B. Serilog 도입 (콘솔 + 롤링 파일 구조화 싱크)**
5. `src/Wcs.Api/Wcs.Api.csproj` — Serilog 패키지 추가(Serilog.AspNetCore + Console/File 싱크 + Settings.Configuration 류 — 구체 패키지는 Generator 결정, 안정 버전 우선).
6. `src/Wcs.Api/Program.cs` — `// TODO(M5-P2): Serilog` 마커 지점에 Serilog 부트스트랩(`UseSerilog` 등). **레벨·싱크·파일 경로·롤링 주기·보존·출력 템플릿은 appsettings의 `Serilog` 섹션에서 읽는다(하드코딩 금지, 절대규칙 #7).** 기존 `ILogger<T>` 호출부는 Serilog 백엔드로 흘러가므로 호출 코드 대량 변경 불요(구조화 메시지 템플릿은 이미 다수 사용 중 — 보존).
7. `src/Wcs.Api/appsettings.json` + `appsettings.Development.json` — `Serilog` 설정 섹션 추가(MinimumLevel·WriteTo Console/File·rollingInterval·retainedFileCountLimit·outputTemplate·경로 등). 파일 경로는 운영/개발 분리 가능.

**C. operation_log 테이블 신설 (DB 상세 로깅 — 단일 횡단 테이블)**
8. `src/Wcs.Data/Entities.cs` — `OperationLog` 엔티티 + 카테고리/액션 enum(또는 string + CHECK). ERD 원칙 준수: 대리키 `Id bigint identity`, `At datetime2(UTC)` 선두 인덱스, **append-only(이력 — UPDATE 없음)**.
9. `src/Wcs.Data/WcsDbContext.cs` — `DbSet<OperationLog>` + `ConfigureOperationLog` 매핑(테이블명 snake_case `operation_log`, enum→string+길이, 인덱스, **FK는 신중히 — pieceId/destinationId 등을 참조한다면 nullable FK(NO_ACTION) 또는 FK 없이 스냅샷 컬럼만**으로 1785 회피. Generator는 FK 도입 시 Restrict 명시).
10. **양 provider 마이그레이션 신규 추가**: `Wcs.Migrations.SqlServer` + `Wcs.Migrations.Sqlite` 각각에 `operation_log` 테이블 추가 마이그레이션. **`--project`/`--startup-project` 둘 다 마이그레이션 어셈블리 지정**(S-M4-P1 교훈). 각 provider 독립 ModelSnapshot 갱신. (HOW: `dotnet ef migrations add` 명령 세부는 Generator.)
11. `docs/ERD.md` — operation_log를 16테이블 목록에 **17번째로 추가 정의**(컬럼·인덱스·보존·"도메인 이벤트와의 관계: 횡단 관측 스트림이지 중복 아님" 명문화). 테이블 수 16→17 갱신.

**D. DB 기록 서비스/싱크 (비동기·단일 경로·fail-safe)**
12. operation_log 기록 진입점(예: `IOperationLogger` 인터페이스 + EF 구현 — 위치는 `src/Wcs.Api` 또는 `src/Wcs.Data`, Generator 결정). **본 처리(150ms 폴·핸드셰이크·API 3s)를 블로킹하지 않도록 비동기·배치 또는 백그라운드 채널**로 기록. 기록 실패가 본 동작을 막지 않음(fail-safe — 예외를 삼키지 말고 Serilog로 자체 경고). DB 컨텍스트 수명(Scoped vs 백그라운드 스코프)은 기존 패턴(IServiceScopeFactory) 준수.

**E. 각 동작 지점에 로그 호출 부가 (의미 변경 0 — 부수 기록만)**
13. **API 원문(전수)**: `src/Wcs.Api/Controllers/RcsController.cs` — IF-05(destination-query)·IF-09(arrival-report)·IF-10(deposit-report) 요청/응답 원문. **IF-08은 폐지된 폴링이 아니라 WCS→RCS 푸시**(`src/Wcs.Api/Services/DestinationStatusPusher.cs`·`RcsPushClient.cs`)이므로 그 **아웃바운드 푸시 전송**을 IF-08 동작으로 기록.
14. **PLC 쓰기(전수)**: `src/Wcs.PlcGateway/PlcGateway.cs` — 단일 쓰기 큐 컨슈머의 SetTgtFloor(D6)·CellAssign(D0/D1+C_Flag)·ClearR(D2/D3+R_Flag)·**D4 RMW**(before→after) 각 쓰기. (게이트웨이 본문 의미 변경 0 — 로그 호출만. 게이트웨이는 Core/PlcGateway 계층이라 DB 직접 의존 금지 → 로깅 훅 방식은 Generator 결정: 이벤트/콜백 또는 ILogger 경유 후 Serilog→DB 싱크. **operation_log DB 기록이 PlcGateway에 EF 의존을 새로 끌어들이지 않게** 한다 — 의존성 방향 보존.)
15. **핸드셰이크 단계(전수)**: `src/Wcs.PlcGateway/HandshakeOrchestrator.cs` — C단계 투입·R_Flag 수신·R_Seq 대사(일치/불일치)·ClearR·타임아웃/OFFLINE outcome 각 단계.
16. **상태 전이(전수)**: OFFLINE(`PlcPollingService.PublishOffline`/`OnOfflineTransition` — 전이당 1회)·FULL/PAUSED(`ChuteCapacityService` 상태 변화·소터 full)·ONLINE 복구.
17. **고빈도 폴링 변화분(변화 시에만)**: `src/Wcs.PlcGateway/PlcGateway.cs`의 폴 루프 — R_Flag·Ready·CurFloor·TgtFloor·R_Seq·C_Flag·R_CellNo·C_CellNo·C_Seq 등 레지스터 값이 **직전 스냅샷과 달라진 경우에만** 1행 기록(전이 old→new). 매 폴링 스냅샷(무변화)은 **기록하지 않는다.** (현재 폴 루프는 `prevRFlag`만 추적 — 전체 레지스터 전이 감지로 확장. 단 게이트웨이 의미·타이밍 보존.)

### OUT (이번 스프린트가 절대 건드리지 않는 것)
- **판정 로직**: `src/Wcs.Core/DepositDecider`(순수함수)·`RegisterMap`·`PlcSnapshot` 모델 — 의미 0 변경(로깅 의존 0, 절대규칙 #8).
- **핸드셰이크 의미**: C/R 시퀀스·C_Seq 증가·R_Seq 대사 로직·타임아웃 값 산출 — 0 변경(로그 호출만 부가).
- **단일 쓰기 큐 구조**: PlcWriteQueue Channel 단일화 — 위반 0(절대규칙 #1). 로깅이 큐를 우회하거나 별도 Modbus 호출 추가 0.
- **기존 도메인 이벤트 테이블**: piece_event·plc_event·alarm·destination_event·sorter_command 의 스키마·기록 의미·행 수 — 0 변경.
- **기존 16테이블 스키마**: operation_log 외 어떤 기존 테이블 컬럼/인덱스/FK도 변경 0(신규 17번째 테이블만 추가).
- **RTU 시리얼 파라미터 현장 실측값**(PortName·BaudRate·Parity 등) — 직전 스프린트와 동일 placeholder 유지(이 스프린트 범위 아님, §8).
- **테스트를 실 SqlServer로 이전**: 테스트는 인메모리 SQLite 더블 유지(S-FIELD-SEED 결정 계승). operation_log 마이그레이션은 SQLite 테스트 더블에서도 EnsureCreated로 생성돼야 하고, 실 SqlServer fresh database update로 별도 검증(§5 C5).

---

## 3. Detected Project Type

**Backend/API** — 근거(프로젝트 신호, 사용자 표현 아님): 서버측 라우트/컨트롤러(`src/Wcs.Api/Controllers/RcsController.cs`, ASP.NET Core `MapControllers`·`app.Run()`) + 서버 진입점(`Program.cs`)이 존재하고, 같은 레포에 브라우저 대면 UI 트리(HTML 셸/클라이언트 렌더 컴포넌트)가 없다. 멀티-스택 요소(C# 백엔드 + Modbus 게이트웨이 + EF DB)는 전부 서버측이며 단일 언어(C#)다.

---

## 4. 로깅 대상 동작 명세 (operation_log 필드 + 콘솔 출력)

> **HOW 아님 — WHAT을 남길지의 명세.** 정확한 컬럼 타입·enum vs string·채널 구현은 Generator 결정. 아래는 "어떤 동작을 어떤 의미로 기록해야 PASS인지"의 스펙.

### 4-1. operation_log 권장 필드(의미 — 컬럼명·타입은 Generator 미세조정 가능, 의미는 고정)
| 필드 | 의미 |
|---|---|
| `Id` | 대리키 bigint identity (ERD 원칙 1) |
| `At` | 동작 발생 시각 datetime2(UTC) — **선두 인덱스**(시계열 조회·퍼지) |
| `Category` | 동작 대분류: `API`(IF-05/08/09/10) · `PLC_WRITE`(D4 RMW·D6·CellAssign·ClearR) · `POLL_CHANGE`(레지스터 전이) · `HANDSHAKE`(C/R 단계) · `STATE`(OFFLINE/ONLINE/FULL/PAUSED) |
| `Action` | 동작 세부: 예) `IF05_REQ`/`IF05_RES`/`IF08_PUSH`/`IF09`/`IF10`/`SET_TGTFLOOR`/`RMW_D4`/`CELL_ASSIGN`/`CLEAR_R`/`REG_CHANGE`/`HS_C_SENT`/`HS_R_RECV`/`HS_RSEQ_MATCH`/`HS_RSEQ_MISMATCH`/`HS_TIMEOUT`/`OFFLINE`/`ONLINE`/`FULL`/`PAUSED` |
| `Level` | 심각도: INFO/WARN/ERROR (Serilog 레벨과 정합) |
| `SorterChuteNo` 또는 `DestinationId` | 어느 소터/목적지 동작인가(멀티 소터 식별, nullable) |
| `Barcode` / `PId` | 어느 piece 동작인가(API·핸드셰이크 시, nullable) |
| `Detail` (JSON) | 동작 상세 — 레지스터 전이는 `{reg, old, new}`, API는 요청/응답 원문 발췌, 핸드셰이크는 `{cSeq, rSeq, cellNo, outcome}` 등 |

- **FK 정책**: PId/DestinationId를 FK로 걸 경우 **nullable FK(EF 기본 NO_ACTION)** 또는 **FK 없이 스냅샷 값 컬럼**만(1785 회피·이력 불변 원칙 5). Generator가 FK 도입 시 Restrict 명시.

### 4-2. 기록 정책(이것이 PASS/FAIL 핵심)
- **전수 기록(매 발생)**: API 요청/응답 원문(IF-05/08-push/09/10)·모든 PLC 쓰기(SetTgtFloor·CellAssign·ClearR·D4 RMW before→after)·핸드셰이크 각 단계·상태 전이(OFFLINE/ONLINE/FULL/PAUSED).
- **변화분만 기록(고빈도 폴링 150ms)**: 레지스터(R_Flag·Ready·CurFloor·TgtFloor·R_Seq·C_Flag·R_CellNo·C_CellNo·C_Seq) 값이 **직전 스냅샷과 다를 때만** `POLL_CHANGE` 1행(old→new). **무변화 폴링 스냅샷은 0행** — 이게 핵심 검증 포인트(150ms × 무변화 = DB 폭주 방지).

### 4-3. 콘솔(Serilog) 출력 예시(의미 — 정확한 템플릿은 appsettings outputTemplate)
```
[12:00:01 INF] [IF-05] pId=101 barcode=0701-CELL-01 → result=OK chuteNo=1
[12:00:01 DBG] [RMW D4] 0004 → set=0001 clear=0000 → 0005
[12:00:02 INF] [핸드셰이크] R_Flag=1 수신: R_CellNo=1 R_Seq=1 (기대 C_Seq=1)
[12:00:02 WRN] [폴링] REG_CHANGE Ready 1→0
[12:00:05 ERR] [상태] 소터 OFFLINE 전이 destId=1 chuteNo=1
```
- 구조화: 메시지 템플릿 속성(pId·barcode·chuteNo·cSeq·reg·old·new 등)이 **구조화 속성**으로 보존돼야 한다(단순 문자열 보간 아님 — Serilog 구조화 로깅의 핵심).

---

## 5. Evaluation Criteria (가중치) — Evaluator가 fresh evidence로 판정

> 모든 PASS는 **"지금 실제로 돌렸다"는 fresh tool output**(HTTP 응답 본문·실 SqlServer sqlcmd 결과·콘솔 로그 발췌·파일 싱크 tail·`dotnet test` raw line)을 sprint-feedback.md에 인용해야 한다. Generator의 success 보고·이전 스프린트 결과·추정만으론 PASS 금지.

### ★★★ API Design Quality (가중치 25%)
- IF-05가 chuteNo **1** 반환(30 아님) — 실 HTTP 왕복으로 입증.
- operation_log 기록이 API 응답 계약(`{result, chuteNo}`·`{result:"OK"}`)을 **변경하지 않음**(로그는 부수 — 응답 형상 0 변경).

### ★★★ Architecture Originality (가중치 25%)
- operation_log가 기존 도메인 이벤트 테이블의 **중복이 아니라 횡단 관측 스트림**임이 ERD.md에 명문화되고 구현에 반영(piece_event/plc_event 행 수·의미 0 변경).
- DB 기록이 **비동기·단일 경로·fail-safe**로, 절대규칙 #1(단일 쓰기 큐)·의존성 방향(PlcGateway가 EF에 새로 의존 안 함)을 보존.

### ★★ Craft (가중치 20%)
- **변화분 정책 동작**: 무변화 폴링이 operation_log에 `POLL_CHANGE` 0행, 레지스터 전이 시에만 1행 — 실 관찰(라이브 기동 후 일정 시간 무변화 구간 카운트 0 + 전이 1건 입증).
- **하드코딩 0**: Serilog 레벨·경로·롤링·보존이 전부 appsettings `Serilog` 섹션(절대규칙 #7) — appsettings 값 변경이 실제 반영됨을 입증(예: MinimumLevel 변경 또는 파일 경로 확인).
- **성능 가드**: 로깅 추가 후에도 폴링 주기·핸드셰이크·API 응답이 지연 없이 동작(라이브 기동에서 폴 루프 정상·핸드셰이크 완료 관찰).

### ★★ Functionality (가중치 30%)
- **라이브 기동 후 IF-05/핸드셰이크 시 콘솔에 구조화 로그 출현** + **롤링 파일 싱크에 동일 로그 기록**(파일 생성·tail 확인).
- **operation_log에 행 적재**(실 SqlServer 직접 쿼리) — API 원문·PLC 쓰기·핸드셰이크·상태전이가 전수 기록되고 변화분 정책이 적용됨.
- chuteNo=1로 재적재된 실 DB에서 IF-05 OK·chuteNo=1(음성 대조: chuteNo=30 더는 매칭 안 됨 또는 미적재 바코드 NG).

### Completion Conditions (최소 통과 — 전부 충족해야 APPROVED)
- **C1**: 라이브 기동(base=SqlServer) 후 IF-05 실 HTTP `{result:"OK", chuteNo:1}` (30 아님). 음성 대조 1건(미적재/오매칭 → NG 또는 chuteNo≠1 미발생).
- **C2**: 콘솔에 Serilog 구조화 로그 출현(IF-05·RMW·핸드셰이크·상태전이) + **롤링 파일 싱크 파일 생성·해당 라인 존재**(fresh tail).
- **C3**: 실 SqlServer `SELECT ... FROM operation_log`로 행 적재 확인 — 전수 대상(API/PLC 쓰기/핸드셰이크/상태전이) 각 ≥1행 + Category/Action/At/Detail 채워짐.
- **C4 (변화분 정책)**: 무변화 폴링 구간에서 `POLL_CHANGE` 행이 **늘지 않음**(일정 시간 카운트 불변) + 실제 레지스터 전이 시 1행 증가 — 둘 다 입증(거짓 PASS 방지 음성 대조).
- **C5 (신규 마이그레이션 실 SQL Server fresh)**: operation_log 신규 마이그레이션이 **빈 SQL Server에 `dotnet ef database update` fresh 적용 성공**(exit 0·**1785/207/오류 0**) + sqlcmd로 `operation_log` 테이블·인덱스·(FK 있으면)NO_ACTION 확인. (S-SQLSERVER-FK-CASCADE 교훈 — provider 고유 DDL 제약은 실 SqlServer fresh로만 닫힌다.)
- **C6 (회귀 0)**: `dotnet test` **146 GREEN**(또는 현 baseline 수)·exit 0 — 로그 부가가 회귀 0, DB 기록 서비스가 인메모리 SQLite 테스트 더블에서 무해(테스트 블로킹·예외 0). 동시성/타이밍 민감 테스트가 있으므로 **fresh ≥3회 반복**으로 결정성 확인(간헐 flake 적발 — S-E2E 교훈).
- **C7 (재적재 클린)**: 재적재 후 실 SqlServer에서 소터 destination 1개(chuteNo=1)·셀 16·오더 16·order_item 16·active cell_assignment 16·piece 0(또는 검증 산물 복원). chuteNo=30 잔존 0. dev DbSeeder chuteNo=1 충돌이 해소돼 콜드스타트(dev SQLite)도 기동 성공.
- **C8 (양 provider No changes)**: operation_log 추가 후 `dotnet ef migrations has-pending-model-changes`가 **양 provider 모두 "No changes"**(모델 ↔ 마이그레이션 정합). 보호 zone(Core·DepositDecider·RegisterMap·기존 16테이블 매핑·핸드셰이크 의미·단일 큐) git diff 의미 변경 0.

---

## 6. 성능 / 볼륨 가드 (명시)
- **본 처리 비지연**: DB(operation_log) 기록은 **비동기·배치 또는 별도 채널**로 수행해 150ms 폴링·핸드셰이크 타이밍·API 3s 응답을 지연시키지 않는다. 동기 EF SaveChanges를 폴 루프/핸드셰이크/HTTP 핸들러 핫패스에서 블로킹 호출하지 않는다.
- **변화분으로 볼륨 억제**: 150ms 폴링 × 무변화 = operation_log 0행(전이 시만 기록). 이게 DB 폭주의 1차 방어선.
- **Fail-safe**: operation_log 기록 실패(DB 다운·연결 끊김 등)가 PLC 쓰기·배정·핸드셰이크·API 응답을 막지 않는다. **단 예외를 삼키지 않고** Serilog로 자체 경고(절대규칙 Fail Loud — 조용한 실패 금지).
- **파일 싱크 롤링·보존**: Serilog 파일 싱크는 rollingInterval·retainedFileCountLimit으로 디스크 무한 증가 방지(전부 appsettings). plc_event 7~14일 / piece_event 30~90일 보존 원칙(ERD §보존)과 정합하는 operation_log 보존 정책을 ERD.md에 명기(퍼지 구현은 본 스프린트 범위 밖이나 보존 기간 정의는 포함).

---

## 7. Parallel Modules / Evaluation Dimensions

- **Parallel Modules**: N/A (single sprint, single Generator). chuteNo 정정·Serilog·operation_log·로그 호출 부가는 강하게 상호 의존(같은 Program.cs·appsettings·DbContext·마이그레이션을 공유)하므로 모듈 경계로 깨끗이 분할 불가 → 순차 단일 Generator가 정답.
- **Evaluation Dimensions**: functional only (단일 차원). 보안·성능 민감 신규 표면 없음(로깅은 기존 동작에 부수·DB 추가 1테이블). 성능 가드(§6)는 functional Evaluator가 라이브 관찰로 흡수. 4-Tier 독립 code-reviewer(Step 4.5)가 아키텍처·의존성 방향·중복 여부를 별도 검토(런타임 Evaluator와 비중복).

---

## 8. 현장 확인 / 미확정 (구현 중 추측 금지 — 기록·필요 시 질문)
- **chuteNo=1 충돌(우선 확인 대상)**: dev `DbSeeder`의 CHUTE 1~5와 소터 chuteNo=1이 `UQ_destination_chute_no`에서 충돌 가능. **실 현장 DB(seed-field-16cells.sql)에는 CHUTE 1~5 시드가 없으므로** 충돌 없이 chuteNo=1 소터가 들어갈 수 있으나, **dev 시드 형상과 실 DB 형상이 달라진다.** Generator는 dev 충돌을 어떻게 해소할지(예: dev에서 CHUTE를 2~5로 조정, 또는 소터를 별도 처리) 결정하고 **입증과 함께 문서화**. 의미상 모호하면(현장 CHUTE가 정말 chuteNo 1을 안 쓰는지 등) docs/SPEC.md 미확정에 기록하고 사용자에게 질문.
- **RTU 시리얼 파라미터**: PortName·BaudRate·Parity·StopBits·UnitId 실측값은 직전 스프린트와 동일(이 스프린트 범위 아님 — placeholder 유지).
- **operation_log 보존·퍼지 배치**: 보존 기간 정의는 ERD.md에 포함하되, 자동 퍼지 일배치 구현은 본 스프린트 범위 밖(후속 — 정의만).

---

> Planner self-check — Detected project type: Backend/API. Required scenario slots: 3 (endpoints touched [IF-05 destination-query / IF-09 arrival-report / IF-10 deposit-report / IF-08 outbound push], happy path per endpoint, relevant error cases per endpoint). All slots filled: yes.

---

## Verification Scenarios (Backend/API — mandatory)

### Slot 1 — Explicit list of endpoints touched by this sprint (method + path)
1. `POST /api/v1/destination-query` (IF-05) — chuteNo=1 반환 + operation_log API_REQ/RES 전수 기록.
2. `POST /api/v1/arrival-report` (IF-09) — 도착 기록 + (소터면) 운영층 정렬 쓰기 → PLC_WRITE 로그.
3. `POST /api/v1/deposit-report` (IF-10) — 투입 보고 + (3D면) IF-11 핸드셰이크 트리거 → HANDSHAKE 단계 로그.
4. **아웃바운드** `POST {RcsBaseUrl}/api/v1/destination-status` (IF-08 push, WCS→RCS) — 푸시 전송 시 operation_log `API`/`IF08_PUSH` 기록(BaseUrl 미설정 시 비활성이므로 설정된 환경 또는 Fake RCS로 관찰).

### Slot 2 — Happy path per endpoint (expected input → expected output shape + 로그 부수효과)
- **IF-05**: `{pId,agvNo,barcode(=0701-CELL-01),inductionNo,qty,timeStamp}` → `200 {result:"OK", chuteNo:1}`. 부수: operation_log에 `API/IF05_REQ`+`API/IF05_RES`(barcode·pId·chuteNo=1·result) 2행 이상 + 콘솔/파일 Serilog 구조화 라인.
- **IF-09**: `{pId,chuteNo:1,agvNo,timeStamp}` → `200 {result:"OK"}`. 부수: 소터면 SetTgtFloor 큐 투입 시 `PLC_WRITE/SET_TGTFLOOR`(조건 충족 시) + 도착 기록 로그.
- **IF-10**: `{pId,barcode,chuteNo:1,agvNo}` → `200 {result:"OK"}`. 부수: 3D면 핸드셰이크 트리거 → `HANDSHAKE/HS_C_SENT`→`HS_R_RECV`→`HS_RSEQ_MATCH`(또는 mismatch/timeout) + `PLC_WRITE/CELL_ASSIGN`·`CLEAR_R`·`RMW_D4` 행들. (실 Sim 또는 라이브 핸드셰이크로 관찰.)
- **IF-08 push**: 소터 상태 전이 시 WCS가 RCS로 푸시 → operation_log `API/IF08_PUSH`(destinationId·payload 발췌) 1행/전이.

### Slot 3 — Relevant error cases per endpoint (Planner가 해당되는 것만 — 패딩 없음)
- **IF-05 400**: `pId` 범위 밖(`<1 || >30000`)·`barcode` 공백·`qty<=0` → `400 {error}`. 부수: 검증 실패도 operation_log에 기록되는지(또는 의도적 미기록인지) Generator 정책 명시 — Evaluator는 응답 형상(400)이 로깅으로 안 바뀌는지 확인.
- **IF-05 NG(200)**: 미적재/오매칭 바코드 → `200 {result:"NG", chuteNo:null}`(음성 대조 — chuteNo=1이 잘못 나오지 않음). 부수: piece DENIED + operation_log `API/IF05_RES` reason 기록.
- **IF-09 / IF-10 400**: `pId` 범위 밖·`chuteNo<=0`·(IF-10)`barcode` 공백 → `400`. 미존재/비활성 chuteNo는 **500 금지**(200 + 기록만) — 로깅이 이 정책을 깨지 않음 확인.
- **상태 전이 ERROR**: 소터 OFFLINE(읽기 실패 주입) → operation_log `STATE/OFFLINE` ERROR 1행(전이당 1회 — 폴마다 반복 0). R_Seq 불일치 → `HANDSHAKE/HS_RSEQ_MISMATCH` ERROR + 기존 alarm 테이블 의미 불변(중복 기록이 alarm 행 수를 바꾸지 않음).
