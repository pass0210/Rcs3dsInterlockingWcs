# Sprint Feedback — S-CLEANUP-FIELD

**APPROVED** — Evaluator, 2026-07-07 (1 iteration to pass).
IN 16항목 전항 + 절대규칙·무변경 가드·동시성 사각 전부 PASS, fresh evidence.
브랜치 `fix/cleanup-field`. 검증자는 코드 수정/커밋/브랜치 전환 없이 독립 재실행·코드 판독만 수행.
핸드오프 마커 확인: `tasks/sprint-log.md:2249` `## IMPLEMENTATION COMPLETE (Orchestrator fan-in, 2026-07-07)` (+ m1/m2/m3.md).
평가 제외(사용자 추가 자료·untracked): `docs/PROGRAM_STRUCTURE.md`·`docs/api-spec-ko.html` — 이 스프린트 산출물 아님(확인).

──────────────────────────────────────────────────────────────────────
## 1. 전체 스위트 (독립 재실행 ×2, fresh)

```
RUN1: dotnet test backend/Wcs.sln → 통과!  실패: 0, 통과: 210, 건너뜀: 0, 전체: 210 (14s) EXIT=0
RUN2: dotnet test backend/Wcs.sln → 통과!  실패: 0, 통과: 210, 건너뜀: 0, 전체: 210 (17s) EXIT=0
```
- 189 baseline + M1 신규 19 + M2 신규 2 = 210. **F1b 선재 flake 미발현**(격리 재실행 불요).
- ⚠ 자기-사고 기록(귀속 명확·코드 무관): RUN2 1차 시도가 CS0006(metadata 파일 없음)으로 EXIT=1 →
  원인은 **검증자가 `dotnet build --no-incremental`를 test-run2와 동시 실행**해 obj/ref DLL을 놓고 경합한 것
  (e2e-parallel-load-surfaces flake 교훈의 동시 빌드 파일락). 직렬 재실행 시 210/210 GREEN 확정. 산출물 결함 아님.
- 정적검사: `dotnet build --no-incremental` → **오류 0 / 경고 10**, 10건 전부 선재 NU1903
  (`SQLitePCLRaw.lib.e_sqlite3 2.1.10` advisory·todo:4). **신규 컴파일 경고 0**(계약 조건 충족).

## 2. IN 16항목 개별 실증 (targeted 재실행 raw 인용 — `--no-build`)

```
통과 CleanupFieldM1_OfflineLogTests.D1_SustainedOffline_SuppressesLogSpam_OneTransitionAndRecovery [170ms]
통과 CleanupFieldM1_HttpTests.D3_Health_Returns200_WithStatusDbSorters [24ms]
통과 CleanupFieldM1_HttpTests.D4_If05_BarcodeTooLong_Returns400 [1s]
통과 CleanupFieldM1_HttpTests.D4_If05_QtyOverflow_Returns400_NoDataPollution [62ms]
통과 CleanupFieldM1_HttpTests.D4_If05_TimeStampTooLong_Returns400 [2ms]
통과 CleanupFieldM1_HttpTests.D4_If10_NegativeQty_Returns400 [13ms]
통과 CleanupFieldM1_HttpTests.D4_If05_ValidInput_StillOk_NoRegression [186ms]
통과 CleanupFieldM1_ClassifierTests.A1_Residue_IsWarn_NotInfo + A1_HandshakeStageLevels(×11 theory)
통과 Sim3dsRtuTests.A9_UnitId_OutOfRange_FailLoud [15ms]
통과 Sim3dsRtuTests.A10_SetRResidue_BeforeStart_FailLoud [1ms]
EXIT=0
```

- **D-1 (OFFLINE 로그 스팸 억제) — PASS.** 표적 테스트가 단정: SetFailReads(true)로 지속 OFFLINE 유도 →
  지속 라인 ≥12 관측 시점에 "OFFLINE 전이" ERROR **==1**, 총 ERROR **==1**, 예외(스택) 첨부 **==1**
  (폴마다 스택 반복 아님=스팸 억제), 주기 WARN 요약("OFFLINE 지속 — 누적") ≥1, 복구 시 "ONLINE" INFO 정확히 1회 증가.
  코드(`PlcGateway.cs:365-405`): `PublishOffline()`가 `bool` 반환(Online 1→0 성공=전이당 1회) → 전이는 `LogError(ex,…)` 1회,
  지속은 `LogDebug` 강등 + `OfflineLogSummaryEveryPolls`마다 `LogWarning` 요약. `_offlineFailureCount`는 정상 폴 성공/전이 시 리셋.
- **D-2 (Serilog rollOnFileSizeLimit) — PASS.** `appsettings.json`(retain 14)·`appsettings.Development.json`(retain 7) 둘 다
  File Args에 `rollOnFileSizeLimit:true` + `fileSizeLimitBytes:104857600`(100MB) 추가(diff 확인). base/dev 정합.
- **D-3 (/health) — PASS.** 실 HTTP 왕복: `GET /health` → 200, `{status:"ok", db:true, sorters:[{chuteNo:30,online,lastPollAt}]}`.
  **부수효과 0**: 두 번째 호출도 200 + `TgtFloor` 불변 단정. 코드(`Program.cs:120-145`)는 `Latest`(논블로킹 스냅샷)·`CanConnect`(읽기 전용)만.
  CanConnect 예외도 liveness 200 유지(db=false로만 저하 표시 — 예외 삼킴 아님). 항상 200(Q2 확정).
- **D-4 (입력 상한) — PASS.** 실 HTTP: barcode 201자→400, IF-05 qty=int.MaxValue→400 + **piece 미생성(오염 0 DB 단정)**,
  timeStamp 31자→400, IF-10 qty=-5→400, 정상 입력→200 OK(회귀 없음). 전부 **500 아님**(SQLite 더블 provider-gap을
  컨트롤러 검증이 선제 차단). qty 상한은 `WcsOptions.MaxQtyPerRequest`(appsettings 100000·설정값), barcode 200/timeStamp 30은
  스키마 미러 const(`WcsDbContext.HasMaxLength`와 정합·주석 명시). IF-05/IF-09/IF-10 세 핸들러 전부 배선(코드 확인).
- **A-1 (HS_R_RESIDUE WARN 승격) — PASS.** 분류기 12케이스: `HS_R_RESIDUE`→WARN, `HS_R_RESIDUE_TIMEOUT`/MISMATCH/OFFLINE/TIMEOUT/CFLAG_TIMEOUT→ERROR,
  정상 단계(C_SENT/R_RECV/RSEQ_MATCH/R_ARMED/CLEAR_R)→INFO. 배선 경로 판독: `Program.cs:469` `SubscribeHandshakeStage`가
  `OperationLogClassifier.ForHandshakeStage(action)`로 위임(단일 진실). Nop 팩토리 주의사항 정당: 테스트 호스트는
  `NopSorterRegistryFactory`라 HANDSHAKE→opLog 미배선 → 레벨 결정 함수를 단위 테스트로 검증(운영 배선이 이 레벨 그대로 opLog.Log 전달).
  **ERROR 승격 회귀 0**: 기존 ERROR 집합 불변, RESIDUE만 INFO→WARN(ERROR로 승격 아님), RESIDUE_TIMEOUT은 TIMEOUT 우선 매칭→ERROR 유지.
- **A-2 (spurious RFlagRaised 에지 억제 + F-3 주석) — PASS(코드 판독).** `PlcGateway.cs:315-355`: reconcile 폴에서 ClearR 큐 투입 시
  `suppressRFlagEdge=true` → `if(!prevRFlag && snap.RFlag && !suppressRFlagEdge)`로 상승 에지 게시 억제. ClearR은 `_writeQueue.Writer.TryWrite`
  경유(절대규칙 #1 준수·직접 Modbus 호출 아님). F-3 주석: `RFlagRaised` 채널 소비자 부재 명시(HandshakeOrchestrator는 `ArmRFlagZeroAsync`에서
  `_gw.Latest` 직접 폴링). 소비자 없어 무해·핸드셰이크 의미 영향 0.
- **C-2 (UnitId 경계) — PASS.** A9 테스트: 0·248·300 거부(InvalidOperationException), 1·247 유효. 코드: `ParsedUnitId`(1~247, 형제
  ParsedParity/ParsedStopBits 동형) — 생성자에서 `(byte)opt.UnitId` 무음 절단(300→44) 대신 fail-loud.
- **C-3 (pre-StartAsync 예외) — PASS.** A10 테스트: StartAsync 전 SetRResidue → "StartAsync 먼저 호출" InvalidOperationException.
  코드: `RequireTransport([CallerMemberName])` 가드가 Flush/Pull의 `_transport!` NRE 대체.
- **A-3 (volatile 정렬) — PASS(코드 판독).** `InjectRFlagDelayMs`(int)·`InjectStickyRResidue`(bool)는 volatile 백킹 필드.
  `InjectRSeqOverride`(int?)는 volatile 대상 아니라 `_hasRSeqOverride`(volatile bool)+`_rSeqOverride`(volatile int) 2필드 분해.
  공개 시그니처(int?/int/bool·이름) 불변 → 기존 사용처 무영향(전체 스위트 GREEN이 방증).
- **B-1 (seed 매핑확장 주석 정정) — PASS(SQL 전문 판독으로 재검증 — 핵심).** 정정된 3단계 절차가 실 SQL 로직과 정확히 일치:
  (1) `@availMax 15→20`: §2 셀 MERGE(`Enab=CASE WHEN n<=@availMax`)·§5 order_item(`BETWEEN 1 AND @availMax`)·§6 cell_assignment
  (`CellNo BETWEEN 1 AND @availMax`) 전부 @availMax 참조 확인 → 자동 연동. (2) §4 오더 VALUES: 하드코딩 `(1)..(15)`로 @availMax 미참조 →
  16~20 오더 생성 위해 `(1)..(20)` 수동 확장 필요 확인. (3) §7 셀16 CANCELLED 블록: 유지 시 §7a가 셀16 배정 해제·§7b가 오더16 CANCELLED
  → 셀16 가용화 위해 제거 필요 확인. **옛 안내("UPDATE cell SET Enabled=1 한 줄")가 틀린 이유도 정확**: §2 MERGE `WHEN MATCHED … Enabled=src.Enab`가
  재실행 시 @availMax(=15) 기준으로 수동 UPDATE를 클로버함(로직 확인). **SQL 로직 무변경**: diff는 주석 블록(L22-32) + B-6 진단 SELECT만.
- **B-2/B-4 (테스트 주석) — PASS.** PlannedQty=100(OVER 간섭 배제·게이트만 검증)·pId 41000대(LOADED 직삽입 합성 pId·20000대 IF-05와 비충돌)
  주석 3곳 추가. diff는 주석 라인만(로직 무변경).
- **B-6 (cells_enabled 술어 분리) — PASS.** 진단 SELECT를 `cells_enabled_15`(Enabled=1만)·`cells_cap3_20`(Capacity만)로 분리.
  검증 편의 SELECT·출력 전용(트랜잭션 로직 무영향). 전 20셀 Capacity=3이라 cap 열=20, enabled 열=15로 진단 명료.
- **A-5 (master_spec §05 FULL/PAUSED 타입 분기) — PASS(실코드 대조).** 정정 표가 `RcsController.DestinationQuery` availability 델리게이트와
  정합: 슈트(dt!=Sorter3D)→`DestinationBlock.None`(OK), 소터 PAUSED→`Block.Paused`(NG), 소터 `SorterCanAcceptBarcode` 실패→`Block.Full`(NG),
  OFFLINE은 IF-05 미검사. `DbRepositories.cs:68-135`도 소터만 PAUSED 차단(슈트 통과) 확인. interface_kr 정합 참조 추가.
- **A-20 (README 전면 현행화) — PASS(표본 대조).** 엔드포인트(destination-query/arrival-report/deposit-report) 실 라우트 일치,
  ASP.NET Core MVC·IF-08 푸시(deposit-permission 폐지)·Migrations 2종·17테이블·.NET10·SQL Server/SQLite·포트(:5080/:1502/:5173)
  전부 코드/appsettings와 일치. 참조 링크 12개 파일 전수 존재 확인(docs 8·scripts 3·TASKS.md).
- **E-SPEC (SPEC.md IF-08 푸시 명료화) — PASS.** §2 상단 노트 + §3 `deposit-permission` 폐지 마킹. **판정표 2-A/2-B는 내부
  DepositDecider 스펙으로 보존**(삭제 안 함) 확인. IF-09 신설 언급.
- **A-6 (CLAUDE.md drift) — PASS(오케스트레이터 몫·Team 미수정 확인).** `git diff HEAD -- CLAUDE.md` **빈 출력** — Team이 보호파일
  미접촉(정상). 실제 정정은 오케스트레이터가 커밋 단계에서 적용(계약 Q1 확정).

## 3. 절대규칙 · 무변경 가드

- **#7 하드코딩 금지**: 신규 임계값 전부 appsettings 바인딩 — `OfflineLogSummaryEveryPolls`(Timing, 소터별 override 병합)·
  `MaxQtyPerRequest`(Wcs)·`fileSizeLimitBytes`(Serilog). barcode 200/timeStamp 30은 DB 스키마 미러 const(단일 진실·의도).
- **#1 PLC 단일 큐**: A-2 ClearR = `_writeQueue.Writer.TryWrite` 경유(직접 Modbus 0).
- **무변경 가드 diff 0**(git diff HEAD --stat 빈 출력): `HandshakeOrchestrator.cs` · `Wcs.Core/`(DepositDecider·RegisterMap) ·
  `frontend/` · `CLAUDE.md` · `DbRepositories.cs`. 판정·핸드셰이크·TgtFloor 규칙 의미 변경 0.
- **예외 삼킴 0**: fail-loud(UnitId·pre-StartAsync 명시 예외)·400 응답. logger try/catch는 disposed 로거 보호(선재 패턴).

## 4. 동시성 사각 (코드 직접 판독)

- **D-1 전이 카운터/상태의 스레드 경계 — 안전.** `_offlineFailureCount`는 폴 루프 catch/success 블록에서만 접근(PublishOffline
  미접근) → 단일 스레드 전용. `_online`은 `PublishOffline`(폴 루프 :365 + 쓰기 컨슈머 :470) 양쪽에서 `Interlocked.Exchange`로 원자 전환 →
  전이당 1 이벤트/1 상세로그 보장.
- **[관찰·비차단] 쓰기 컨슈머 전이 경합 극단 케이스**: 쓰기 컨슈머(:470)가 OFFLINE 전이를 선점하면(CAS 승리) 폴 루프의 다음
  PublishOffline은 false 반환 → "OFFLINE 전이" ERROR 대신 "지속"(Debug/요약) 경로. 단 쓰기 컨슈머는 그 직전 `LogError(ex,"[쓰기 큐]…")`로
  자체 상세 로그를 이미 남기고, 결과는 **더 조용한** 방향(스팸 회귀 아님). 선재 dual-call 패턴이며 D-1 목표(스팸 억제) 보존 → 결함 아님.
- **/health 스냅샷 읽기 경합 — 안전.** `Latest`(불변 record 참조 스왑)·`CanConnect` 읽기 전용, 상태전이/쓰기 0. 테스트가 2회 호출 후 TgtFloor 불변 단정.
- **A-1 분류기 추출 ERROR 승격 회귀 0**: 기존 인라인 삼항과 ERROR 집합 동일, RESIDUE→WARN(INFO→WARN, ERROR 아님), 실패 계열 de-escalation 없음.

## 5. 프로세스 정리
- 검증 종료 후 포트 :1502·:5080 LISTENING 0, 고아 `Wcs.Sim3ds`/`testhost`/`vstest` 0. 실 PLC/RTU 실선 미기동(Sim TCP·더블만).

──────────────────────────────────────────────────────────────────────
## 최종 판정: **APPROVED**
IN 16항목 · 절대규칙 · 무변경 가드 · 동시성 · 문서-코드 정합 전부 PASS. FAIL 0. 재작업 지시 없음.
Minor(비차단) 1건 등재: 쓰기 컨슈머 OFFLINE 전이 선점 시 폴 루프가 전이 ERROR를 남기지 않는 극단 케이스(결함 아님·더 조용한 방향).

## Code Review Minor (4-Tier Step 4.5 — S-CLEANUP-FIELD, 병합 비차단·다음 스프린트/todo 참조)

1. **입력 상한이 공유 단일-진실 아닌 문서화 미러** — RcsController.cs:31-34 const가 WcsDbContext.cs 산재 리터럴(Barcode=200/ClientTs=30)의 복제. 동기화 주석 의존. 기존 스타일과 일관·과확장 금지로 수용 — B2B 이식 전 SchemaLimits 공유 상수 검토(todo).
2. **/health CanConnect() 동기·타임아웃 부재** — Program.cs:246. DB 무응답 시 기본 15초 점유로 liveness 프로브 역설. DB-down은 빠른 실패라 실위험 낮음. CanConnectAsync+짧은 CT(2초) 검토(readiness 정교화는 B2B-1 이연).
3. **지속 OFFLINE 로깅이 예외 원인 변화 은폐** — PlcGateway.cs:374-384. 스팸 억제 트레이드오프. 주기 WARN에 ex.GetType().Name 저비용 노출 검토.

리뷰어 권고(todo): 입력검증 400 거부의 operation_log 미기록(규약 비대칭 방지 위해 별도 스프린트), 쓰기 컨슈머 OFFLINE 전이 선점 시 폴링 ERROR 라인 부재(알람·큐오류로 관측 가능, 주석 명시 권장).
