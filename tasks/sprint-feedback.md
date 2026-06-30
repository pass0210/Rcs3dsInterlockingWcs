# Sprint Feedback — S-OBSERVABILITY (현장 chuteNo 30→1 일원화 + 전 동작 Serilog 콘솔·operation_log DB 상세 로깅) — APPROVED

## Phase 3 Evaluate (Evaluator fresh evidence, branch `feat/field-observability`, 2026-06-30)

**최종 판정: APPROVED** — C1~C8 + 무변경 가드 전부 PASS. Generator 요약을 신뢰하지 않고 모든 증거를 fresh로 직접 재실행. 환경: dotnet 10.0.300 · ef 9.0.10 · sqlcmd 15.0 · SQL Server 2025(localhost) · 실 DB `Rcs3dsInterlockingWcs` 존재 확인.

### Ground-truth (직접 읽음·실행)
- 브랜치 `feat/field-observability`. `git diff --stat`: 계약 §2 IN 파일에 국한(docs/ERD·seed SQL·RcsController·SorterGatewayRegistry·Program·RcsPushClient·Wcs.Api.csproj·appsettings(.Development)·Entities·WcsDbContext·양 ModelSnapshot·PlcGateway·HandshakeOrchestrator) + untracked(OperationLogService·IOperationLogger·양 provider AddOperationLog 마이그레이션). `.claude/`·tasks 문서는 하네스/세션 산출물.
- sprint-log.md `## IMPLEMENTATION COMPLETE (S-OBSERVABILITY)`(L1802) 마커 + 실제 diff 존재 확인(거짓 핸드오프 아님). 계약 개정 블록(chuteNo 현장 데이터 2지점만·DbSeeder 불변·테스트 Sorters chuteNo=30 유지) 정독.

### C5 (신규 마이그레이션 빈 SQL Server fresh — 최우선) — PASS
- 빈 독립 DB `WcsObsEval`(Generator의 WcsObsTest와 다른 이름)에 fresh `dotnet ef database update`:
  ```
  Applying migration '20260630012916_Initial'.
  Applying migration '20260630060710_AddOperationLog'.
  Done.   (exit 0 — 1785/207/오류 0건)
  ```
- sqlcmd 검사(WcsObsEval): `operation_log` 테이블 1개 · 도메인 17테이블(__EFMigrationHistory 제외 — 16 기존 + operation_log) · **모든 FK 17개 delete_referential_action_desc=NO_ACTION**(Cascade 0) · operation_log FK 0개 · 인덱스 3(PK_operation_log CLUSTERED + IX_operation_log_at + IX_operation_log_sorter_at, 전부 has_filter=0 → 207 함정 비해당). 검증 후 `DROP DATABASE WcsObsEval` 완료.

### C8 (양 provider No changes) — PASS
- SqlServer: `dotnet ef migrations has-pending-model-changes --project/--startup-project src/Wcs.Migrations.SqlServer` → `No changes have been made to the model since the last migration.`
- Sqlite: 동일 명령(Wcs.Migrations.Sqlite) → `No changes...`. 모델↔마이그레이션 양 provider 정합.

### C6 (회귀 0) base=SqlServer 146 GREEN — PASS
- working tree `appsettings.json` `"Provider": "SqlServer"`(grep 확인) 상태 그대로 `dotnet test Wcs.sln`:
  - RUN 1: `통과! - 실패: 0, 통과: 146, 건너뜀: 0, 전체: 146`, exit 0 (기간 11s)
  - RUN 2(--no-build): `통과! - 실패: 0, 통과: 146, 건너뜀: 0` (결정성 확인)
- 로그 부가 회귀 0. NU1903(SQLitePCLRaw 2.1.10 transitive)은 선재 의존성 audit 경고(S-M5-P1 기록) — 코드 경고 아님.

### C1 (라이브 IF-05 chuteNo=1) — PASS
- Production + base=SqlServer + RTU(COM1 OFFLINE 전이 — 예상, IF-05는 DB dispatch라 무관) 기동, :5080 listen.
- 적재 `0701-CELL-01` → `{"result":"OK","chuteNo":1}` (HTTP 200) — **chuteNo=1(30 아님)** 실 HTTP 왕복 입증.
- 음성 대조 미적재 `0701-CELL-99` → `{"result":"NG","chuteNo":null}` (가짜 OK 방지).
- qty<=0 → HTTP 400(응답 형상 0 변경 — 로깅이 검증 안 깸).

### C2 (Serilog 콘솔 + 파일 싱크, appsettings 외부화) — PASS
- 콘솔: 기동 로그가 Serilog 구조화 형식 `[15:35:30 INF] ...`(Console outputTemplate from appsettings).
- 파일: `src/Wcs.Api/logs/wcs-20260630.log` 신규 생성(Day 롤링, 경로 `logs/wcs-.log` from appsettings). IF-05 라인이 `{Properties:j}` 구조화 속성 보존:
  `[2026-06-30 15:35:48.483 +09:00 INF] [IF-05] ... result=OK chuteNo=1 ... {"SourceContext":"...RcsController","ActionName":"...DestinationQuery","RequestId":"...","RequestPath":"/api/v1/destination-query",...}`
- 레벨·경로·롤링·보존·outputTemplate 전부 appsettings `Serilog` 섹션(git diff 확인 — 하드코딩 0·절대규칙 #7). Dev override(Debug·wcs-dev-.log·7일) 별도 존재.

### C3 (operation_log 전수 — 실 SqlServer sqlcmd) — PASS
- Sim3ds(:1502) 백업 + API `Sorters__0__Transport=Tcp` override로 ONLINE 핸드셰이크 유발(계약 허용). IF-05→IF-09→IF-10 1사이클 후 실 DB 카테고리/액션 전수:
  - **API**: IF05_REQ·IF05_RES(INFO/WARN)·IF09·IF10
  - **PLC_WRITE**: SET_TGTFLOOR `{"reg":"D6","floor":2}` · CELL_ASSIGN `{"cellNo":1,"cSeq":1,"cFlag":1}` · RMW_D4 before→after `{"before":4,"set":1,"clear":0,"after":5}`·`{"before":6,"set":0,"clear":2,"after":4}` · CLEAR_R
  - **HANDSHAKE**: HS_C_SENT `{cellNo:1,cSeq:1}` → HS_R_RECV `{rSeq:1,cSeq:1}` → HS_RSEQ_MATCH `{cSeq:1,rSeq:1}` → HS_CLEAR_R(Success) — R_Seq 대사 기록 확인
  - **POLL_CHANGE**: REG_CHANGE ×13, old→new(`TgtFloor 0→2`·`Ready 1→0`·`CurFloor 1→2` 등)
  - **STATE**: OFFLINE(ERROR)·ONLINE(INFO)
  - 각 카테고리 ≥1행 · Category/Action/At/Level/Detail 채워짐.

### C4 (변화분 정책 — 핵심) — PASS
- 무변화 idle ①: POLL_CHANGE 0 → (~6s/~40폴) → 0 (delta=0, 무폭주).
- 전이 발생: 0 → 13 (delta>0, 레지스터 전이 시에만 기록).
- 무변화 idle ②(활동 후): 13 → (~7s/~45폴) → 13 (delta=0). 양방향+활동 후까지 무폭주 입증.

### C7 (재적재 클린) — PASS
- 실 DB `Rcs3dsInterlockingWcs`: 소터 destination chuteNo=1 1개 · chuteNo=30 잔존 0 · 셀 16 · 활성 cell_assignment 16 · order_item 16(ReservedQty/SortedQty 0) · 소터 오더 16 · piece 0 · operation_log 테이블 present.
- 검증이 남긴 산물(operation_log 32·piece 3·piece_event 8·sorter_command 1·alarm 1·reserved 1·released_assign 1)은 정리해 위 클린 상태 복원(QUOTED_IDENTIFIER ON 트랜잭션).

### 무변경 가드 — PASS
- `src/Wcs.Core/` git diff **0줄**(Core/tests untracked도 0). `tests/` **0줄** — 테스트 결선 변경 0(누설 없음).
- PlcGateway diff = **가산만**: OnOnlineTransition/OnRegisterChange/OnWrite 이벤트 + EmitRegisterChanges/EmitWrite(전부 try 격리, fail-safe). 단일 쓰기 큐 경로·RMW 임계구역·기존 폴 루프 로직 라인 삭제·변경 0.
- HandshakeOrchestrator diff = **가산만**: OnStage 이벤트 + 각 기존 분기점에 EmitStage 삽입. C/R 시퀀스·R_Seq 대사·ClearR enqueue·타임아웃 의미 0 변경.
- WcsDbContext: operation_log 매핑만(+ 기존 16테이블 매핑 0 변경). FK 0(스냅샷 컬럼만) → 1785 회피. filtered index 아님.
- **의존성 방향 보존**: `Wcs.PlcGateway.csproj`는 EF Core·Wcs.Data 미참조(grep IOperationLogger/EntityFramework/Wcs.Data = 0). IOperationLogger는 Wcs.Data, OperationLogService는 Wcs.Api. 게이트웨이는 콜백만 발화.
- **로깅이 Modbus 추가 호출/큐 우회 0**: PLC 쓰기는 여전히 EnqueueAsync→단일 큐 컨슈머. 로그는 컨슈머/핸드셰이크 분기의 부수 발화. RcsController/RcsPushClient 응답·반환값 형상 0 변경(IF-05 `{result,chuteNo}` 유지).
- **DB 기록 비동기·단일경로·fail-safe**: OperationLogService = unbounded Channel TryWrite(논블로킹) + 단일 백그라운드 컨슈머 배치(≤256) AddRange+SaveChanges(IServiceScopeFactory 스코프). 실패는 Serilog 경고 후 드롭(삼키지 않음). StopAsync는 Writer.TryComplete로 결정적 종료(teardown 채널 경쟁 교훈 적용) — 146 GREEN으로 무해 입증.

### 정리(산물 0 잔존)
- WcsObsEval DROP · 실 DB 클린 시드 복원 · 오펀 dotnet/Sim 프로세스 kill(포트 5080/1502 free) · 라이브 로그(gitignored runtime byproduct) 제거. 최종 `git status`가 핸드오프와 동일(평가 산물 누출 0).

---
**APPROVED** — 8/8 Completion Conditions + 무변경 가드 전부 fresh evidence로 PASS.

## Step 4.5 독립 코드리뷰 (orchestrator, opus, 팀 외부) — BLOCKING 0 / MAJOR 0 / MINOR 4

독립 Opus 코드리뷰어가 Evaluator 미커버 영역(아키텍처·동시성/스레드안전·보안·성능·명명) 검토 → **BLOCKING 0·머지 가능**. 빌드 0오류. 절대규칙 #1/#7/#8 위반 0.
- **소프트 의존 fail-silent = MAJOR/BLOCKING 아님(정당)**: 운영 경로 `Program.cs:94-98`가 `IOperationLogger`(OperationLogService 싱글톤)를 **환경 분기 없이 무조건 등록** → 운영 조용히 꺼짐 불가. `RcsController:30`은 생성자 하드 의존(API Fail Loud). soft(GetService+null-skip)는 부트스트랩 2곳뿐(SorterGatewayRegistry·FULL/PAUSED 훅)이고 누락은 레지스트레이션을 의도 제거한 최소 단위테스트 호스트에서만 → 테스트 격리(soft)와 운영 fail-loud(hard) 양립.
- **이벤트 훅·동시성·의존성 방향 = 양호**: Emit* 전부 try 격리(구독자 예외가 폴/쓰기/핸드셰이크 본동작 안 죽임)·PlcGateway/Handshake는 `event Action`만(EF·IOperationLogger 미참조, 의존성 방향 보존)·teardown Writer.TryComplete+drain·IServiceScopeFactory captive 회피.
- **보안/PII = 무해**: barcode가 Detail에 보간 아닌 EF 파라미터 값으로만 전달(SQL/JSON 인젝션 0)·Serilog 구조화 속성(CRLF 무해)·시크릿 누설 0.

### Minor (비차단 — 후속, 다음 sprint Generator 참고)
- **MINOR-1**: 소프트 의존(GetService+null-skip) 2곳은 테스트 격리상 정당하나, 운영 등록 보장을 주석/시작 헬스체크로 더 명시하면 fail-silent 오해 방지.
- **MINOR-2**: `OperationLogService`가 **unbounded Channel** — 컨슈머 영구 지연/DB 다운 시 이론상 OOM(변화분 정책+드롭 fail-safe가 1차 방어). → `BoundedChannel(DropOldest/DropWrite)` + appsettings capacity 권고.
- **MINOR-3 (운영 전 필요)**: operation_log **보존 14일 정의만 있고 퍼지 일배치 없음** → 무한 증가. 운영 투입 전 퍼지 배치 추가 필요(ERD.md "후속" 명시·계약 §8 범위 밖).
- **MINOR-4**: Detail JSON이 수기 문자열 보간(현재 고정 토큰·숫자라 파손 0). 장차 free-text/barcode를 Detail에 넣으면 JSON 파손 위험 → `System.Text.Json` 직렬화 권고.
- **정보성**: ①`Program.cs:176` 중괄호 없는 단일문(가독성) ②FULL/PAUSED `lastHold` read-then-write 비원자(동시 전이 시 STATE 중복 이론상·영향 경미) → AddOrUpdate 원자화 ③`MaxBatch=256` const appsettings화 일관성.

→ BLOCKING 0 → Step 5 커밋 진행. MINOR 4건 후속.
