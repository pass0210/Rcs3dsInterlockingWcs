# Sprint Contract — S-E2E-MULTI-AGV (다중 AGV 동시 제품수령→셀이동 전 플로우 경우의 수 E2E)

> 작성: Planner Subagent · 2026-06-26 · 방식 확정(재질문 금지): **둘 다**(① 자동 xUnit E2E 스위트 백본 ② 라이브 dotnet run 관찰), **Sim3ds 기반**(실 3DS HW는 사용자 후속). 라이브 구동은 자동 스위트 APPROVED 후 orchestrator step.
> 이 계약은 WHAT/WHERE/검증만 규정한다. 구현 방법(파일 분할 방식·헬퍼 시그니처·동기화 디테일)은 Generator가 결정한다.

---

## 1. Goal

다중 AGV(RCS HTTP 클라이언트 N개)가 동시에 3DS로 제품을 받아 셀로 이동·적재하는 **전(全) 플로우의 경우의 수(매트릭스 A~I)**를, **실 stack ground-truth**로 검증하는 자동 E2E 스위트를 추가한다.

대상 플로우(재설계 현행):
`①IF-05 목적지 조회(셀/슈트 배정·OK/NG) → ②AGV 이동 → ③IF-09 도착(소터 운영층(2) 정렬 트리거) → ④소터 C/R 핸드셰이크(틸트·셀 적재) → ⑤IF-10 투입 보고(IF-11 셀 지정 트리거) → ⑥셀 수량 반영` + 병행 IF-08 상태 push(WCS→RCS).

실 stack 구성(인메모리 카운터 단독 금지 — 메타교훈):
- **WebApplicationFactory**(실 `Program.cs` 호스트) + 실 EF DB(named in-memory SQLite, 인스턴스별 Guid).
- **실 Sim3ds**(`SimServer`, 동적 포트) — 핸드셰이크·정렬·고장주입 상대역. 다중 소터 케이스(A6·F5)는 Sim N대.
- **다중 AGV RCS HTTP 클라이언트 드라이버**(동시 IF-05/IF-09/IF-10, AGV별 pId/agvNo/barcode 부여, barrier 동시성 제어).
- **가짜 RCS 수신 서버**(`FakeRcsServer`) — IF-08 push payload 캡처.

라이브 구동(orchestrator step, Completion·APPROVED 후 별도):
- orchestrator가 `dotnet run --project src/Wcs.Sim3ds`(+ 다중 소터면 포트별 N개) + `dotnet run --project src/Wcs.Api`(appsettings `Sorters[]`·`Wcs:RcsPush:BaseUrl` 구성)를 기동하고, **이 스프린트가 만든 다중 AGV 드라이버**(라이브 진입점)로 동시 부하를 걸어 로그·DB·push 수신을 육안 관찰한다.

---

## 2. Detected Project Type

**Backend / API (.NET, ASP.NET Core Minimal-host + MVC Controller, EF Core, Modbus 게이트웨이).**
신호: `src/Wcs.Api`(ASP.NET Core, `Program.cs`+`Controllers/RcsController.cs`), `src/Wcs.Data`(EF Core), `src/Wcs.PlcGateway`(FluentModbus), `src/Wcs.Sim3ds`(TCP 시뮬레이터), `tests/Wcs.Tests`(xUnit, 현 93 테스트 메서드). TargetFramework `net10.0`.
→ Verification Scenarios 슬롯 = **E2E 시나리오(매트릭스 A~I)**. UI/Playwright 슬롯 = N/A(헤드리스 백엔드).

---

## 3. Implementation Scope (WHAT / WHERE)

### 3.1 신규 — 자동 E2E 스위트 (tests/Wcs.Tests/)
- **신규 테스트 파일(들)** — 예: `E2EMultiAgvScenarioTests.cs`. 매트릭스가 크므로 단계별 분할 허용(예: 정상·게이트(A,B) / 정렬·핸드셰이크(C,D) / 적재·동시성(E,F) / 장애·경계·순서(G,H,I)). 분할 방식·파일명은 Generator 재량(VS↔테스트 매핑 표만 충족).
- **재사용 가능한 다중 AGV RCS 클라이언트 드라이버**(WHERE: tests/Wcs.Tests/ 내 공용 헬퍼 — 신규 파일 또는 기존 헬퍼 확장):
  - AGV별 `pId/agvNo/barcode/inductionNo/qty` 부여, IF-05→IF-09→IF-10 한 사이클을 실행하는 단일 AGV 워크플로 함수.
  - N개 동시 실행(`Barrier` 동시 도달 + 독립 `HttpClient`) 진입점.
  - **자동 스위트와 라이브 구동 양쪽에서 호출 가능**하도록 작성(라이브 진입점은 3.3 참조).
- **다중 실 Sim 소터 E2E 팩토리**(WHERE: tests/Wcs.Tests/ — 신규 또는 `SimWebApplicationFactory` 확장):
  - 단일 소터: 기존 `SimWebApplicationFactory`(실 Sim 1대 + 실 `SorterRegistryFactory` + config `Sorters[0]`) 재사용/확장.
  - **다중 소터(A6·F5 전용)**: SORTER_3D destination 2대 + 각 소속 셀 시드 + 각 Sim(동적 포트) + config `Sorters[]` 2항목을 배선하는 팩토리. ⚠ 현재 `DbSeeder`는 단일 소터(chuteNo=30)·3셀만 시드하므로 **테스트 측 추가 시드 헬퍼**가 필요(production `DbSeeder` 변경 금지 — 3.2). 둘째 소터 destination/cell/order는 테스트 시드로 추가.

### 3.2 production 코드 변경 — 최소(원칙 0 — Minimal Impact)
이 스프린트는 **기존 동작 검증** 스프린트다. production 코드(`src/`) 변경은 원칙적으로 **0**.
- **무변경 가드(테스트가 바꾸지 않음)**: Modbus 레지스터맵(`RegisterMap`), 판정 로직 의미(`DepositDecider`·`DestinationStatusService`), DB 스키마/마이그레이션, `DbSeeder` 시드 토폴로지, 핸드셰이크(`HandshakeOrchestrator`·`SimServer` 동작 스펙).
- **예외(허용되는 production 변경)**:
  - 테스트가 **진성 결함(RED)**을 드러내면 **은폐·skip·억지 GREEN 금지**. 결함은 §6 정직 보고 의무에 따라 sprint-log/feedback에 명시 등재한다.
  - 결함 수정은 **별도 판단**: 사소·명백·국소(원칙 Minimal Impact 충족)면 fix 가능. 범위를 넘는(스키마·판정 의미·다파일) 결함은 **fix하지 말고 finding으로 등재** 후 사용자/후속 스프린트로 이연. 어느 쪽이든 결함의 존재·재현·영향 범위를 문서화한다.
  - ⚠ 스펙 미확정 항목(SPEC §7: D5 C_Flag 상한, E6/G6 ReleaseCell 셀누수·슈트 복구 재푸시 비대칭, H4 TgtFloor 잔류, H5 R_Flag 타임아웃 재시도 정책)은 **현재 동작을 단언**하거나 "기대동작 미정 → TODO/협의"로 분류한다. **추측 단언 금지**(현 코드 동작을 ground-truth로 고정하되, 그것이 "올바른 명세"라고 단언하지 않음).

### 3.3 라이브 구동 진입점 (자동 스위트와 공유)
- 다중 AGV 드라이버를 라이브에서도 구동할 수 있는 진입(콘솔 앱/스크립트/테스트 스위치/`dotnet run` 인자 중 택1) — **방식은 Generator 재량**. 단 자동 스위트의 드라이버 로직과 **코드 공유**(중복 구현 금지)할 것. 라이브 실행 자체는 orchestrator step(§7)에서 수행.

---

## 4. 검증할 경우의 수 매트릭스 → Verification Scenarios (A~I)

> 각 VS는 **실제 실행되는 단언**이어야 한다("이름만 통과" 금지). 각 항목에 기대 입출력 + **ground-truth 단언원**을 명시한다.
> ⚠ 표시는 SPEC §7 미확정 — "현 동작 단언" 또는 "기대 미정→협의"로 분류(추측 단언 금지).
> 과패딩 금지: 시드·인프라로 **실제 구성 가능한 것만** 적용. 일부 항목은 기존 단위/통합 테스트가 이미 ground-truth로 커버 → "기존 커버" 표기 시 E2E에서 중복 재현 대신 다중 AGV 맥락 통합만 추가 가능(Generator 판단, 단 매핑 표에 근거 명시).

### A. 정상 플로우
- **A1** 새 오더·빈 셀 → IF-09 정렬 → 핸드셰이크 → 적재. GT: `sorter_command.status=COMPLETED`·`R_Seq==C_Seq`·`cell_assignment` 활성·셀 수량 반영.
- **A2** 기존 오더 셀 누적(같은 배정 셀 재사용). GT: 동일 cell에 sorter_command 2건·동일 cellId·셀 수량 누적.
- **A3** 이미 운영층 정렬(CurFloor=2) → IF-09 즉시(TgtFloor 쓰기 0). GT: Sim 타임라인 D6 쓰기 0건 / 스냅샷 불변.
- **A4** 미정렬(CurFloor=1) → IF-09 → TgtFloor=2 기입 → 이동 → CurFloor=2 → 핸드셰이크. GT: Sim 타임라인 D6=2 1건 + 이동 후 CurFloor=2·Ready=1.
- **A5** 슈트 정상 플로우(IF-05 OK→IF-09 무정렬→IF-10 OK, IF-11 트리거 0). GT: 핸드셰이크 없음·`piece` DEPOSITED·C_Flag 불변.
- **A6** 멀티소터 라우팅(소터2대) — 바코드별 올바른 소터로. GT: 각 소터 destId 핸드셰이크 교차 0·각 sorter_command가 올바른 destId/cell. (다중 Sim 팩토리 필요 — §3.1)
- **A7** 한 슈트 다중 송장 / 고객별 한 송장(다중 IF-05/IF-10 같은 슈트). GT: piece N건·슈트 수량 합산.

### B. IF-05 게이트
- **B1** 소터 셀 만재 → NG·reason=FULL (배정 셀 작업수량 도달+빈셀0). GT: 응답 NG·chuteNo=null + `piece_event IF05_RES.Reason="FULL"`. (기존 `SorterCellFullnessTests.EC1` 커버 — E2E에선 다중 AGV 맥락)
- **B2** 새 오더·빈셀0 → NG. GT: 응답 NG. (기존 EC2 커버)
- **B3** 보유 셀 여유 → OK. GT: 응답 OK·reason=NORMAL. (기존 HP1 커버)
- **B4** 보유 셀 전부 full → NG. (기존 EC9 A 경로 커버)
- **B5** 소터 PAUSED → NG. GT: 응답 NG(소터 paused 예외 없이 차단). (기존 EC3 커버)
- **B6** 소터 비활성(IsActive=false 또는 미등록) → NG/NO_DEST. GT: 응답 NG. ⚠ 비활성 소터 destination 시드 가능 여부 확인 — 불가면 "기대동작 미정"으로 분류. (Q1)
- **B7** 슈트 full → **OK**(반전 — 슈트는 보냄). GT: 응답 OK·chuteNo 배정. (기존 `S8`·`If05_Chute_Full` 커버)
- **B8** 슈트 pause → **OK**(반전). GT: 응답 OK. (기존 커버)
- **B9** 소터 offline + 셀 있음 → **OK**(IF-05는 online을 안 봄·셀 기준). GT: 응답 OK. (기존 `VS7(a)` 커버)
- **B10** dest NULL(상위 등록 송장) → 빈 슈트 AUTO 할당 OR NO_DEST. GT: 빈 슈트 있으면 OK·`dest_assign_type=AUTO` / 없으면 NG·NO_DEST. (시드 `TEST-BARCODE-AUTO`/ORD-004 활용)
- **B11** 오더 OVER/COMPLETED → NG. GT: 응답 NG. ⚠ OVER/COMPLETED 상태 오더 시드/전이 가능 여부 확인. (Q2)
- **B12** barcode 미매칭 → NG·NO_DEST. GT: 응답 NG·chuteNo=null. (기존 `VS2_UnknownBarcode` 커버)
- **B13** Capacity NULL=무제한 → 수량-full 미적용. GT: 빈셀0·대량 적재여도 OK. (기존 `EC4` 커버)
- **B14** OK 시 예약 차감(슈트 capacity 반영). GT: `OnReserved` 후 hold/집계 반영 — 슈트 한정. (기존 capacity 테스트 커버)
- **B15** NG여도 piece DENIED 기록. GT: NG 응답 후 `piece.status=DENIED` + piece_event 존재.
- **B16** 검증 실패(pId 범위·barcode 공백·qty≤0) → **400**. GT: HTTP 400. (기존 `VS2`·`MINOR1` 커버)

### C. IF-09 / 정렬
- **C1** 도착 → TgtFloor=2 기입(미정렬 시). GT: Sim D6=2 1건. (= A4 / 기존 `S2`)
- **C2** 이미 정렬 → 안 씀. GT: D6 쓰기 0. (= A3 / 기존 `If09_AlreadyAligned`)
- **C3** 진행 중(TgtFloor≠0) → 덮어쓰기 안 함(핑퐁 차단). GT: D6 추가 쓰기 0. (기존 `S4`·`S9`)
- **C4** 슈트 도착 → 정렬 없음. GT: D6 쓰기 0. (기존 `If09_ChuteArrival` 커버)
- **C5** 미존재 chuteNo → 200 + 기록만(500 금지). GT: HTTP 200·정렬 스킵. (기존 `If09_UnknownChuteNo` 커버)
- **C6** IF-05 선행 없이 IF-09 → 경고 로깅·활성 piece 없음(도착 기록 생략). GT: 응답 200·`RecordArrival` false 경로(로그/piece_event 부재). 현 동작 단언.
- **C7** 도착 후 OFFLINE → 정렬 미수행. GT: 번들 OFFLINE(snap.Online=false)에서 IF-09 → D6 쓰기 0·정렬 스킵 로그.

### D. C/R 핸드셰이크
- **D1** 정상 C(C_CellNo·C_Seq·C_Flag=1). GT: Sim 타임라인 "C 수신" + sorter_command CSeq 기록. (기존 `S1`)
- **D2** 정상 R 대사(R_Seq==C_Seq). GT: COMPLETED·R_Seq==C_Seq. (기존 `S1`)
- **D3** R_Seq 불일치 → MISMATCH 알람. GT: `alarm.code=R_SEQ_MISMATCH` + `sorter_command.status=MISMATCH`(Sim `InjectRSeqOverride`). (기존 `S5`)
- **D4** R_Flag 타임아웃 → RFLAG_TIMEOUT. GT: `alarm.code=RFLAG_TIMEOUT` + status=TIMEOUT·1행(Sim `InjectRFlagDelayMs`). (기존 `S6`)
- **D5** ⚠ C_Flag=1 대기 상한 미정의(SPEC §7-B). 현 동작: `CFlagTimeoutMs` 설정값 존재(`HandshakeOrchestrator`)·초과 시 outcome=CFlagTimeout. GT: Sim이 C를 영영 안 비우게(`InjectNoResponse`로 C 미소비) → CFLAG_TIMEOUT 도달 단언. **현 동작 단언**(스펙 "상한 정책"은 미정 → finding/협의 표기).
- **D6** 핸드셰이크 중 OFFLINE(Sim StopAsync). GT: outcome=Offline + alarm `OFFLINE`(또는 핸드셰이크 OFFLINE 분기) — 현 동작 단언.
- **D7** 분류 시작 시 Ready 1→0 + TgtFloor 클리어(PLC=Sim 책임). GT: Sim 타임라인 "분류 시작: Ready=0, TgtFloor 클리어". (기존 `S3`)
- **D8** ⚠ R_CellNo≠C_CellNo → 실적재 셀 기록. 현 `SimServer`는 R_CellNo=받은 C_CellNo 그대로 반환 → 불일치 주입 수단 없음. **"현 Sim 한계 — 기대동작 미정"으로 분류**(추측 단언 금지). (Q3)
- **D9** C_Seq 증가(매 건). GT: 연속 핸드셰이크 2건의 CSeq 단조 증가(소터별 _cSeq 보존).
- **D10** 단일 쓰기 큐 직렬화. GT: 동시 다발 쓰기에도 D 영역 경합 없이 순차 반영(절대규칙 #1) — 게이트웨이 큐 경유 입증. (기존 게이트웨이 동작 + F 항목과 통합)

### E. IF-10 / 적재
- **E1** 정상 → IF-11 트리거. GT: 3D 보고 → C_Flag=1 / sorter_command 생성. (기존 `VS6`·`S1`)
- **E2** 멱등(중복 pId 보고 무해). GT: 2차 보고 OK·기록 1건. (기존 `VS5`)
- **E3** 동시 같은 pId IF-10 → 정확히 1배정. GT: 8병렬 → piece 1건·cell_assignment 1건(부분 유니크). (기존 `CONCUR1` — E2E에서 실 Sim 맥락 재현)
- **E4** COMPLETED → 셀 수량 반영. GT: 핸드셰이크 완료 후 `LoadedQtyByCell` 증가(sorter_command COMPLETED JOIN piece.qty). (= A1/A2 GT)
- **E5** cell_assignment 해제 타이밍. GT: 핸드셰이크 콜백 `ReleaseCell` 후 배정 해제(released_at) 관찰. 현 동작 단언.
- **E6** ⚠ 콜백 throw 시 ReleaseCell 스킵 → 셀 누수(TODO 이연·M5). GT: 호스트종료/DI 오설정 한정 경로 — **재현 곤란 시 finding 등재**, 정상 경로에선 누수 0 단언. 추측 단언 금지(현 코드가 정상 경로에서 누수 없음만 입증).

### F. 동시성 (진성 경합 — barrier 동시관찰/실 동시 HTTP. 단일 idle 경로 함정 회피)
- **F1** N-AGV 서로 다른 셀 동시 → 각자 다른 셀 배정. GT: N개 cell_assignment 서로 다른 cellId·핸드셰이크 N건 모두 COMPLETED.
- **F2** N-AGV 같은 오더 셀 누적 + 과적재 경계. GT: 같은 배정 셀에 누적·Capacity 초과 적재 0(또는 soft-threshold 1건 바운드 — TODO §18 정보성 참조, 현 동작 단언).
- **F3** 비행 중 셀 채워짐(TOCTOU): IF-05 OK 받고 이동 중 셀이 만재 → IF-10 시점 재평가. GT: SelectCell 재선택 또는 빈셀없음 처리 — 현 동작 단언(IF-05 OK ⟹ 적재 가능 §88 불변식 범위 내).
- **F4** 동시 IF-05 같은 빈 셀 경합 → 정확히 1배정. GT: barrier 동시 IF-05 N건 → cell_assignment 부분 유니크로 1건. (기존 `EC5`·`CONCUR1` 패턴)
- **F5** 멀티소터 동시 핸드셰이크 교차 0. GT: 소터2대 동시 핸드셰이크 → 각 destId의 sorter_command가 자기 cell만·R_Seq 교차 없음. (다중 Sim 팩토리 필요)
- **F6** push 전이 동시 관찰 → 전이당 1건. GT: `FakeRcsServer.CountFor` 전이당 정확히 1(16스레드 동시 관찰). (기존 `VS9a`·`PUSH4` 패턴)
- **F7** OFFLINE 전이 동시 → 알람 1건. GT: alarm OFFLINE 전이당 1건(안정 카운트). (기존 `S7` 패턴)
- **F8** 한 소터 여러 AGV 순차(직렬화). GT: 분류·이동 직렬(Sim `_isSorting`/`_isMoving`) → 핸드셰이크 순차 COMPLETED·Ready 블립 없음.

### G. 장애 전이
- **G1** OFFLINE → push false. GT: Sim 읽기 실패/StopAsync → push ready=false 수신. (기존 `VS3` 패턴)
- **G2** 복구 → 자동 재평가. GT: Sim 재기동(`RestartSimAsync`) → online 복구 → push ready 재전이. (기존 `S7` Phase2)
- **G3** busy→ready 투입 가능 전이. GT: Ready 0→1 전이 → push ready=true 1건. (기존 `PUSH2_3`)
- **G4** 슈트 full→비움 재푸시. GT: OnReserved(만재)→OnCleared → push true→false→true 전이당 1건. (기존 `PUSH1`)
- **G5** PAUSED push 영향 없음·IF-05만. GT: 소터 PAUSED → push ready=true 유지(운영상태) BUT IF-05 NG. (기존 `VS5`·`EC3`)
- **G6** ⚠ RCS 다운 복구 시 소터 자동 재푸시 / 슈트 stale 이연(TODO §8·24 — 슈트 복구 재푸시 비대칭). GT: 소터는 관찰 타이머 재평가로 복구 재푸시 / 슈트는 다음 이벤트까지 stale = **현 동작 단언**(비대칭을 결함이 아닌 현 명세로 고정 + finding 표기). 추측 단언 금지.

### H. 경계
- **H1** qty 경계 −1/0/+1. GT: qty≤0 → 400 / qty≥1 → 정상. (기존 `MINOR1`)
- **H2** Capacity NULL/0/음수 → 무제한. GT: NULL·0·음수 모두 수량-full 미적용. (기존 `EC4` + 0/음수 추가)
- **H3** 다중 셀 보유 → 여유 셀 선택·과적재 0. GT: full셀+여유셀 보유 시 SelectCell이 여유셀. (기존 `EC8`)
- **H4** ⚠ TgtFloor 잔류 미해결(SPEC §7-B·TODO). GT: 이동만·투입 없이 이탈 시 TgtFloor≠0 잔류 → 현 동작(WCS 클리어 안 함·절대규칙 #3) 단언 + "해소책 미정" finding.
- **H5** ⚠ R_Flag 타임아웃 재시도 정책 미정(SPEC §7-B). GT: 현 동작 = TIMEOUT 1행·재시도 없음(기존 `S6` 단언) + "재시도 정책 미정" finding.
- **H6** 2층 고정 운영. GT: OperationalFloor=2 설정 경유(하드코딩 0)·정렬 항상 2층.

### I. 순서 / 멱등
- **I1** IF-09 선행(도착 전 정렬 안 함). GT: IF-09 없이 IF-10 → 정렬 미수행 상태에서도 핸드셰이크 동작 / 현 동작 단언.
- **I2** IF-10이 핸드셰이크 전(IF-09/정렬 미완) → 현 동작 단언(IF-10은 정렬 완료를 강제 대기하지 않음 — 현 코드 경로 확인 후 단언).
- **I3** 재시도 중복 수량 0(DISTINCT piece). GT: 재시도=새 sorter_command 행이어도 셀 수량은 piece별 1건만 합산(`SorterCellQty` DISTINCT). (기존 산출 로직 + E2E 재현)
- **I4** 중복 IF-05. GT: 같은 pId IF-05 2회 → 멱등/중복 piece 0 또는 현 동작 단언.

> **VS 개수 결정**: 위 A1~I4(약 60개 케이스)를 그룹 단위 테스트로 묶어 **실제 적용 가능한 것만** 구현한다. "기존 커버" 표기 항목은 다중 AGV E2E 맥락에서 재현 가치가 있을 때만 추가(중복 최소화). 최종 VS 수·매핑은 Generator가 §5 매핑 표로 확정하되, **각 매트릭스 항목이 ≥1 테스트에 대응**해야 한다(또는 "기존 테스트 X가 커버" 명시 + 그 근거).

---

## 5. Completion Conditions

1. **build 0 error / 0 warning** (`dotnet build`).
2. **`dotnet test` 전체 GREEN · exit 0** — 기존 회귀 0(현 93 테스트 메서드 전부 유지) + 신규 E2E 전부 통과.
3. **teardown exit 0** — 채널 경쟁(`testhost-teardown-channel-race`) 회귀 0. 실 Sim/실 호스트 다중 기동 후에도 hang/크래시 0(`--blame-hang`/`--diag` 또는 dumpasync로 입증).
4. **동시성/타이밍 표적 ≥5회 flaky 0** — F·D4·D5·G·push 전이 등 타이밍 의존 테스트를 ≥5회 반복(`--filter`)해 전부 GREEN(고정 sleep 의존이 아니라 `WaitUntil*` 폴링 동기화).
5. **매트릭스 A~I 각 케이스 대응** — §4 각 항목이 (a) 신규 테스트 또는 (b) 기존 테스트 명시 매핑 중 하나로 커버됨을 보이는 **매핑 표**가 sprint-log에 존재. "이름만 통과" 0.
6. **ground-truth 진정성** — 모든 핵심 단언이 실 Sim 핸드셰이크 / 실 EF DB(sorter_command·cell_assignment·piece·alarm·셀수량) / 가짜 RCS push payload 중 하나에 근거(인메모리 카운터 단독 0).
7. **드러난 결함 전부 문서화** — RED·미확정(⚠) 분류·finding이 sprint-log/feedback에 정직 기록(§6).
8. **무변경 가드 충족** — RegisterMap·판정 의미·DB 스키마/마이그레이션·DbSeeder 토폴로지·핸드셰이크 동작이 이 스프린트로 바뀌지 않음(production diff가 §3.2 예외 범위 내).

---

## 6. Evaluation Criteria (가중치)

| # | 기준 | 가중치 | 합격선 |
|---|---|---|---|
| ① | **매트릭스 커버리지** — A~I 각 케이스가 실제 실행되는 단언에 대응(매핑 표). "이름만 통과"·빈 테스트 0. | 0.25 | 전 항목 매핑·근거 |
| ② | **ground-truth 진정성** — 실 Sim 핸드셰이크·실 EF DB(셀수량/cell_assignment/sorter_command/alarm)·가짜 RCS push payload. 인메모리 카운터 단독 금지. | 0.25 | 핵심 단언 전부 실 상태원 |
| ③ | **동시성 진성 경합** — F 항목은 barrier 동시관찰/실 동시 HTTP. 단일 idle 경로(직렬로 도는데 "동시"라 주장)·중복억제만 보는 함정 회피. | 0.20 | F·D10 진성 경합 입증 |
| ④ | **flaky 0** — 타이밍·동시성 표적 ≥5회 반복 GREEN. 고정 sleep 의존 0. | 0.15 | ≥5회 0 실패 |
| ⑤ | **teardown exit 0** — 채널 경쟁 회귀 0(실 Sim/호스트 다중 기동 포함). | 0.10 | hang/크래시 0·exit0 |
| ⑥ | **결함 정직 보고** — RED·⚠ 미확정·finding 은폐 0. 추측 단언 0(현 동작 단언과 "올바른 명세 단언"을 구분). | 0.05 | 정직 기록 |

Evaluator는 **Fresh evidence 의무**: `dotnet build`/`dotnet test` 실제 출력·반복 실행 로그·DB 단언 코드를 직접 확인(인메모리 GREEN≠결함없음). 코드 직접 검사로 전이 원자성·핸들러 예외격리·동시 경합 진정성을 본다(메타교훈: GREEN/무변경가드만 보면 사각 발생).

---

## 7. 라이브 구동 (orchestrator step — Completion·APPROVED 후 별도)

자동 스위트가 §5 충족·Evaluator APPROVED 된 뒤, orchestrator가 직접 수행:
1. `dotnet run --project src/Wcs.Sim3ds` (다중 소터면 포트별 N개) 기동.
2. `dotnet run --project src/Wcs.Api` 기동 — appsettings `Sorters[]`(Sim 포트), `Wcs:RcsPush:BaseUrl`(가짜 또는 관찰용 수신), `Wcs:OperationalFloor=2` 구성. (선택) 가짜 RCS 수신기 별도 기동.
3. **이 스프린트가 만든 다중 AGV 드라이버(라이브 진입점, §3.3)**로 동시 N-AGV 부하 인가.
4. 로그(레지스터 변화·핸드셰이크·push)·DB(sorter_command/cell_assignment/piece/alarm/셀수량)·push 수신을 육안 관찰해 자동 스위트 결과와 정합 확인.
> 실 3DS 하드웨어 검증은 사용자가 이후 별도 진행(이 스프린트 범위 밖).

---

## 8. Parallel Modules / Evaluation Dimensions

- **Parallel Modules**: **단일 Generator 권장**(N/A). 동시성 E2E는 WebApplicationFactory·실 Sim·DB·드라이버 헬퍼를 **공유**하므로 worktree 병렬 분할 시 헬퍼 인터페이스 충돌·머지 비용이 크다. (단계별 테스트 파일은 한 Generator가 순차로 작성하되 파일은 분할 가능.) 단, 매트릭스가 과대해 일정 압박 시 "헬퍼/드라이버 확정 후 A~D / E~I 2-fan-out" 옵션을 Generator가 제안 가능(헬퍼 동결 전제).
- **Evaluation Dimensions**: **단일 Evaluator**(N/A) — 단일 도메인(백엔드 E2E). 단 Evaluator는 ②ground-truth ③동시성 진정성을 별도 렌즈로 점검.

---

## 9. 핵심 재사용 자산 (기존 인프라 — Generator 직접 read 후 활용)

| 자산 | 위치 | 재사용 포인트 |
|---|---|---|
| `SimWebApplicationFactory` | tests/ScenarioTests.cs | 실 Sim(동적 포트)+실 `SorterRegistryFactory`+config `Sorters[0]`+실 EF DB. **단일 소터 full-stack E2E의 기준 패턴**. `StartSimAsync`/`RestartSimAsync`/`CreateDbScope`/`IsSorterOnline`/`SorterSnapshot`. |
| `FakeRcsServer` | tests/RcsPushTests.cs | IF-08 push 수신 서버(동적 포트)·`CountFor`/`LastFor`/`All`·`StartRejecting`(미도달 주입). push GT 단언원. |
| `RcsPushWebApplicationFactory` | tests/RcsPushTests.cs | FakeMaster+push 활성+`SorterDestinationId`/`SorterChuteNo` 노출. 단 **단일 fake 번들**(실 핸드셰이크 아님) — push/status 산출 GT용. |
| `FakeModbusMasterForApi` | tests/ApiIntegrationTests.cs | 레지스터 직접 조작·`SetReady`/`SetCurFloor`/`SetTgtFloor`/`SetFailReads`(OFFLINE 주입). |
| `SimServer` 고장주입 | src/Wcs.Sim3ds/SimServer.cs | `InjectRSeqOverride`(D3)·`InjectRFlagDelayMs`(D4)·`InjectNoResponse`(D5 C_Flag/R_Flag 미응답)·`StopAsync`(OFFLINE D6/F7/G1). 타임라인 로그(D6·D7·이동·분류). |
| 셀/만재 시드 헬퍼 | tests/SorterCellFullnessTests.cs | `OccupyCells`/`FreeCellCount`/`SetAllCapacities`/`LoadCellQty`/`AddSorterOrderWithAssignedCell`/`MakeSorterFull`. 셀 수량 GT 구성(sorter_command COMPLETED JOIN piece.qty). |
| `WaitUntil*`/`WaitForSnapshot`/`WaitUntilExact` | 각 테스트 파일 | 고정 sleep 금지·폴링 동기화·전이당-1건 안정 카운트. flaky 0 토대. |
| `DbSeeder` | src/Wcs.Data/DbSeeder.cs | 단일 소터(chuteNo=30·3셀·Capacity=NULL)+슈트1~5+PAUSED슈트6+ORD-001~005(`TEST-BARCODE-1/2/3/AUTO/PAUSED`). **다중 소터·OVER/COMPLETED·비활성은 테스트 추가 시드 필요**(production 변경 금지). |
| `WcsTeardownGuard` / `TestAssemblyInit` | src/·tests/ | 종료 단계 미관찰 Task 예외 가드. teardown exit0 토대. |

⚠ **재사용 갭(Generator 인지 필수)**: ① 다중 소터(A6·F5)는 둘째 SORTER_3D destination+셀+order+Sim+config 배선이 기존에 없음 → 신규 테스트 팩토리/시드. ② full-stack 실 핸드셰이크 E2E는 `SimWebApplicationFactory` 계열(실 Sim)만 제공 — `RcsPush*`/`FakeModbus*` 팩토리는 fake 번들이라 핸드셰이크 GT 불가(역할 구분). 다중 AGV + 실 핸드셰이크 + push 수신을 **동시에** 요구하는 VS는 두 팩토리 능력을 합친 신규 배선이 필요할 수 있음(Generator 설계 — `SimWebApplicationFactory`에 `Wcs:RcsPush:BaseUrl` 주입 + 가짜 RCS 결선 등).

---

## 10. 미확정 사항 — 사용자 확인 질문 (계약 말미)

사용자가 이미 확정한 것(방식=둘 다·Sim3ds 기반·내일 착수)은 재질문하지 않는다. 아래는 매트릭스 적용 가부에 영향(전부 **비블로킹** — Generator가 기본 권장값으로 진행하되 불가가 드러나면 finding 등재):

- **Q1 (B6 비활성 소터)**: 비활성/미등록 소터 케이스를 (a) destination.IsActive=false 시드로 검증할지 (b) 범위 제외할지. 현 코드상 비활성 소터는 `SorterRegistryFactory`가 번들 미구성 → GetBundle=null → Offline 경로. **권장: 현 동작(번들 없음→OFFLINE/NG) 단언으로 포함.**
- **Q2 (B11 OVER/COMPLETED 오더)**: 오더 OVER(예약 초과)/COMPLETED 상태 전이를 테스트 시드로 만들지. `EfOrderRepository.QueryDestination`의 OVER/COMPLETED 판정 경로 확인 후 시드 가능하면 포함, 불가하면 "기대동작 미정"으로 분류. **권장: 코드 경로 확인 후 가능 범위만 포함.**
- **Q3 (D8 R_CellNo≠C_CellNo)**: 현 `SimServer`는 R_CellNo=받은 C_CellNo 그대로 반환 — 실적재 셀 불일치 주입 수단이 없음. (a) Sim에 주입 옵션 추가(=Sim 변경, 원칙 0 위반 소지) (b) "현 Sim 한계·기대동작 미정"으로 분류. **권장: (b) 분류**(Sim 변경은 별도 스프린트).
- **Q4 (다중 소터 범위)**: A6·F5를 위해 **둘째 소터를 풀 배선**(실 Sim 2대 동시)할지, 아니면 "다중 소터 라우팅은 단위/통합으로 충분"으로 보고 **단일 소터 + 다중 AGV에 집중**할지. 다중 실 Sim E2E는 인프라 비용이 큼. **권장: 둘째 소터 풀 배선 포함**(사용자 요청 "여러 AGV + 셀 이동 모든 경우"에 멀티소터 라우팅 포함) — 단 비용 과다 판단 시 Generator가 단일소터 집중 + A6/F5를 기존 멀티소터 단위 테스트 매핑으로 대체 제안 가능.

---

## Planner Self-Check
- [x] WHAT/WHERE만 규정 — 구현 방법(파일 분할·헬퍼 시그니처·동기화 디테일·다중 소터 배선 구체안)은 Generator에 위임.
- [x] 사용자 확정(둘 다·Sim3ds 기반·내일 착수) 재질문 없음. 라이브 구동은 APPROVED 후 orchestrator step으로 명시.
- [x] 매트릭스 A~I 전 항목을 VS로 정식화 + ground-truth 단언원·기존 커버 매핑·⚠미확정 분류 명시(추측 단언 금지 규칙 포함).
- [x] 필수 선행 read 수행: CLAUDE.md·SPEC.md(§1·2·4·6·7)·lessons.md·todo.md·전체 테스트 인프라(ApiIntegration·Scenario·SorterPushOperational·RcsPush·SorterCellFullness·TestAssemblyInit)·핵심 소스(RcsController·DestinationStatusService·DestinationStatusPusher·SorterGatewayRegistry·SimServer·DbSeeder·Program.cs).
- [x] production 변경 최소(원칙 0) + RED 결함 은폐 금지·정직 보고 의무 명시. 무변경 가드(RegisterMap·판정의미·스키마·DbSeeder·핸드셰이크) 명시.
- [x] Completion(build0/0·전체 GREEN·exit0·≥5회 flaky0·매핑표·결함문서화) + Evaluation(6기준 가중치·Fresh evidence) 명시.
- [x] Detected Project Type(Backend/API) 직접 확인. Parallel/Eval Dimensions = 단일(N/A) + 조건부 fan-out 제안.
- [x] 재사용 자산 표 + 재사용 갭(다중 소터·실핸드셰이크+push 동시 배선) 명시.
- [x] 미확정 Q1~Q4(비블로킹·기본 권장값 포함) 계약 말미 기록.
