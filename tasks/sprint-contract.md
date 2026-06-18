# Sprint Contract — S-M4-P3 (시나리오 S1~S9 자동화 + 알람/sorter_command 영속화 결선)
> M4 (phase 3 / 3, 최종). 전제: P2b APPROVED(59 GREEN). 사용자 확정 2026-06-15:
> Q1 alarm/sorter_command DB 영속화 **P3 최소 결선 포함** / Q2 S9 경합 = **단일 TgtFloor 선점·타층 쓰기 0·분류 시작 클리어 후 양보** / Q3 OFFLINE 알람 = **전이당 1건**(폴마다 아님) / Q4 S6 타임아웃 = **포기(TIMEOUT 1행)로 단언, 재시도 이연**(SPEC §7-B).
> 성격: 검증(테스트) 주도 + 갭 1건 최소 프로덕션 결선. 새 프로덕션 로직이 갭 외로 번지면 = P1~P2 누락 신호 → 보강·독립 코드리뷰.

## Goal
M4 검증 phase. P1~P2b가 구현한 동작을 9개 엔드투엔드 시나리오(xUnit 통합, 실 Sim3ds 고장 주입)로 입증한다. 시나리오가 표면화하는 **유일한 프로덕션 갭** — 핸드셰이크 결과/OFFLINE → `alarm`·`sorter_command` DB 영속화(현재 쓰기 경로 0, 스키마만 존재) — 을 **API 계층에서만** 최소 결선한다(HandshakeOrchestrator/PlcGateway는 DB 무지, 단방향 경계 유지). 핵심 입증: **"TgtFloor≠0일 때 WCS의 D6 쓰기 0건"(S4)**. 기존 59 회귀 0, Wcs.Core 판정 무변경.

## Scope (IN) — S1~S9 각 PASS 기준
- **S1 정상**: 정상 IF-05→IF-08(allowed)→IF-10→(3D면 IF-11 핸드셰이크) 1왕복. PASS = 핸드셰이크 Outcome=Success, R_Seq==C_Seq, 종료 시 C_Flag·R_Flag=0, sorter_command 1행 status=COMPLETED.
- **S2 층 다름(차트②)**: Ready=1·CurFloor≠agvFloor·TgtFloor==0 → IF-08 allowed=false·WRONG_FLOOR + WCS가 TgtFloor=agvFloor 기입 → Sim 이동 → CurFloor=agvFloor·TgtFloor 유지 → 재IF-08 allowed=true. PASS = 첫 응답 WRONG_FLOOR, SimServer 타임라인에 D6 기입 1건, 이동 완료 후 allowed=true.
- **S3 분류 중 선기입·복귀·분류시작 클리어(차트③)**: 분류 진행 중(Ready=0) TgtFloor 선기입(행4 BUSY+기입) → 분류 시작 시 Sim이 TgtFloor=0 클리어 → 분류 후 복귀 이동 → 도착. PASS = BUSY 응답, 선기입 D6 1건, 분류 시작 시 TgtFloor=0 관찰, 복귀 완료.
- **S4 핑퐁 차단(핵심·쓰기 이력 입증)**: TgtFloor≠0 구간 동안 추가 IF-08(타 층) 다수 → 전부 WRONG_FLOOR(행3)·SetTgtFloor 큐 스킵. PASS = TgtFloor≠0 전 구간에서 SimServer 타임라인의 "WCS 쓰기 수신: D6" 이벤트가 최초 1건 외 **0건**. ← 본 스프린트 핵심 단언.
- **S5 R_Seq 불일치 알람**: InjectRSeqOverride로 R_Seq≠C_Seq → Outcome=RSeqMismatch. PASS = 핸드셰이크 RSeqMismatch + alarm 1행(code=R_SEQ_MISMATCH) + sorter_command status=MISMATCH. ClearR 투입돼 R_Flag=0 복귀.
- **S6 R_Flag 타임아웃**: InjectRFlagDelayMs ≫ RFlagTimeoutMs → Outcome=RFlagTimeout. PASS = 핸드셰이크 RFlagTimeout + alarm 1행(code=RFLAG_TIMEOUT) + sorter_command status=TIMEOUT(**재시도 없음 — Q4 포기 1행**). 타이밍은 설정 유도값(고정 sleep 금지).
- **S7 OFFLINE**: SimServer.StopAsync() → 연속 폴 실패 N회 → Online=false. PASS = 게이트웨이 Online=false 전이, IF-08 응답 reason=OFFLINE, alarm 1행(code=OFFLINE, **전이당 1건** — Q3). 재기동 시 Online 복구.
- **S8 FULL·PAUSED**: (CHUTE) capacity 집계로 Full → IF-08 FULL / destination.status=PAUSED·비활성 → PAUSED. (SORTER_3D 빈 셀 없음 → IF-11 트리거 생략). PASS = FULL이면 reason=FULL, PAUSED면 reason=PAUSED, OnCleared 후 READY 복귀.
- **S9 다중 AGV 경합(Q2 확정)**: 2개 층 AGV가 단일 소터에 경합 → **먼저 TgtFloor 차지한 층 처리 동안 타 층 IF-08은 WRONG_FLOOR·D6 쓰기 0건, 분류 시작 클리어 후에야 타 층이 TgtFloor 차지**. PASS = 경합 구간 동안 TgtFloor 차지 층 1개·타 층 D6 쓰기 0건, 클리어 후 타 층 기입 1건.

## Scope (IN) — 프로덕션 갭 결선 (최소, API 계층 한정)
1. **신규 인터페이스(Wcs.Api)**: `IAlarmSink`(append, code별 행 기록) + `ISorterCommandJournal`(SENT 생성 + COMPLETED/MISMATCH/TIMEOUT 상태 전이) + EF 구현(트랜잭션). Decider/게이트웨이 분리 원칙과 동형(P2a hold 산출 분리 패턴).
2. **IF-10 핸드셰이크 콜백(Program.cs ContinueWith)**: HandshakeResult.Outcome 기준 — SENT 시점 sorter_command 생성, 종결 시 COMPLETED/MISMATCH/TIMEOUT 전이 + 실패(MISMATCH/TIMEOUT) 시 alarm 기록. piece.status MISMATCH/TIMEOUT/LOADED 전이(ERD CHECK 기존값).
3. **IF-08 SORTER_3D OFFLINE 응답 지점**: alarm(code=OFFLINE) **전이당 1건**(폴마다 폭주 금지 — "이미 발행" 상태 추적). 위치는 게이트웨이 OFFLINE 전이 1회 신호를 API가 구독하는 형태 권고(IF-08 핫패스에서 매 폴 기록 금지).
4. **HandshakeOrchestrator/PlcGateway 본문 무변경**: HandshakeResult에 SentCSeq/ReceivedRSeq/ReceivedRCellNo/Outcome 이미 존재 — 시그니처·본문 불변. OFFLINE 전이 1회 신호가 없으면 최소 노출(이벤트/콜백)만 추가하되 본문 동작 변경 0 목표.

## Scope (OUT) — 이연
- `plc_event`(REG_CHANGE/WRITE/ONLINE/OFFLINE) DB 기록 → M5 운영 로깅(SimServer 타임라인이 P3 검증 충당).
- alarm acked_at 운영 흐름(ack/resume UI·API), destination_event(CLEARED 외) 확장.
- **S6 타임아웃 재시도**(Q4) — SPEC §7-B(3DS 협의) 후 별도. P3은 TIMEOUT 1행 포기 단언.
- C_Flag 타임아웃·TgtFloor 영구 잔류 해소책(SPEC §7-B).
- Sim3ds 고장 주입 확장(현 3종 InjectRSeqOverride·InjectRFlagDelayMs·InjectNoResponse으로 S5/S6/S7 충분 — IT-2b/IT-5/IT-4 선례).
- 증분 마이그레이션(alarm/sorter_command/plc_event 테이블 P1 Initial에 이미 존재 — pending 0).
- **HandshakeOrchestrator/PlcGateway 본문에 DB 결선**(단방향 참조 위반 — 금지).

## Detected Project Type: Backend/API (E2E 시나리오 검증 + 소량 영속화 결선 + 동시성/타이밍 민감)
검증 = 59 회귀 0 + S1~S9 신규 GREEN + alarm/sorter_command DB 행 ground-truth 단언 + S4/S9 D6 쓰기 이력 0건 입증.

## Evaluation Criteria
1. **빌드/테스트**: `dotnet build` exit0(경고0/오류0 — P2a 교훈). `dotnet test` 59 회귀 0 + S1~S9 신규 GREEN.
2. **타이밍 민감 신규(특히 S6·S7) flaky 0**: 표적 단독 ≥5회 연속 GREEN. 폴링/WaitUntilAsync 동기화, **고정 sleep 금지**.
3. **무변경 가드**: `git diff HEAD -- src/Wcs.Core src/Wcs.PlcGateway/HandshakeOrchestrator.cs src/Wcs.PlcGateway/PlcGateway.cs src/Wcs.Sim3ds` 동작 변경 0(PlcGateway는 OFFLINE 전이 신호 노출만 허용 — 본문 동작 불변, diff 시 정당화).
4. **단방향 경계**: src/Wcs.PlcGateway에 Wcs.Data/DbContext 참조 0(grep).
5. **갭 결선 정합**: alarm/sorter_command 영속화는 API 계층(Program.cs 콜백·OFFLINE 구독)에서만. IAlarmSink/ISorterCommandJournal EF 구현 실DB 경로.
6. **S4 핵심 단언**: SimServer timelineLog에서 "WCS 쓰기 수신: D6" 카운트 — TgtFloor≠0 구간 최초 1건 외 0건.
7. **DB 행 ground-truth**: 시나리오 후 WcsDbContext 스코프로 alarm/sorter_command 행 카운트·status 직접 조회(로그 단언 아님 — P2a 정량 프로브 교훈).
8. 커밋 전 `git rev-parse --abbrev-ref HEAD` = feat/m4-p3-scenarios 확인(lessons.md: develop 직접 커밋 0).

## Completion Conditions (회귀 0)
- build exit0 / `dotnet test` 59 회귀 0 + S1~S9 신규 GREEN. 타이밍 민감분 ≥5회 연속.
- S1~S9 각 PASS 기준 충족. S4 "TgtFloor≠0 D6 쓰기 0건" + S9 경합 양보 입증.
- alarm/sorter_command DB 영속화 결선(IAlarmSink/ISorterCommandJournal) — S1 COMPLETED·S5 MISMATCH·S6 TIMEOUT·S7 OFFLINE(전이당 1건) 행 단언.
- Wcs.Core 판정·게이트웨이 클래스 본문 무변경. 단방향 경계(PlcGateway→Data 참조 0). pending 0(증분 마이그레이션 불요).
- **독립 코드리뷰 통과**(동시성·재시작·실DB 경로·OFFLINE 전이당-1건 멱등 — 4회 반복 메타교훈: 인메모리 GREEN ≠ 결함 없음).
- feature 브랜치 커밋(HEAD 확인).

## Verification Scenarios (S1~S9 자동화 형태)
- **S1~S4·S9**: 실 SimServer(동적 포트) + 번들 1세트, PlcGatewayIntegrationTests/P2bSimHandshake의 IAsyncLifetime·WaitUntilAsync 패턴 재사용.
- **S5·S6**: SimServer 고장 주입(InjectRSeqOverride/InjectRFlagDelayMs) + 짧은 RFlagTimeoutMs(설정 유도값).
- **S7**: SimServer.StopAsync → OFFLINE 대기 = WriteTimeoutMs×(OfflineAfterFailures+1)+여유. 재기동 복구.
- **S8**: ApiIntegrationTests(WebApplicationFactory) capacity·destination 시드 경로(FULL/PAUSED).
- **alarm/sorter_command 단언**: 시나리오 후 WcsDbContext 스코프 DB 행 카운트·status 직접 조회.
- **S4 핵심 단언**: SimServer timelineLog 캡처 "WCS 쓰기 수신: D6" 카운트.

## 미확정 (추측 금지)
- OFFLINE 전이당 1건 구현 위치: 게이트웨이 OFFLINE 전이 1회 이벤트를 API가 구독(권고, IF-08 핫패스 매폴 기록 회피) vs API에서 "직전 상태" 추적. 본문 무변경 우선해 구현 시 확정 — 게이트웨이가 전이 신호를 노출하지 않으면 최소 노출(이벤트) 추가 가부를 Evaluator와 확정.
- IAlarmSink/ISorterCommandJournal 인터페이스 경계: 핸드셰이크 콜백이 단일 메서드(Record(outcome))로 alarm+sorter_command 동시 기록 vs 분리 — 트랜잭션 일관성(둘 다 한 tx) 우선해 확정.

> Planner self-check — Backend/API. S1~S9 슬롯 전부 PASS 기준 명시. 갭 1건(alarm/sorter_command 영속화) API 계층 한정 + 독립 코드리뷰. Core/게이트웨이 본문 무변경 + 단방향 경계 + pending 0. 사용자 확정 Q1~Q4(최소 결선·S9 선점·S6 포기·OFFLINE 전이당 1건) 반영. 회귀 baseline 59.
