# Sprint Contract — S-HARDENING-1

> Planner Subagent · 2026-07-13
> "작고 실효 큰" 운영 견고화 묶음. 슈트 복구 하트비트 + 감사 묶음 C 핵심(ReleaseCell 소터 스코프 회귀 잠금 + 인덱스 2종) + 직전 스프린트(S-IF08-READY-PUSH) 동파일 Code Review Minor 2건.
> 원장: `tasks/todo.md`(2026-07-01 감사 묶음 C), `tasks/audit-20260701-full.md`(A-3·A-7 근거), `tasks/sprint-feedback.md`(S-IF08 Code Review Minor).

---

## 코드 선행 판독 결과 (Planner — 계약 전 실코드 확인, Generator/Evaluator 필독)

Planner가 스코프 후보를 실코드와 대조한 결과, **후보 중 1건은 코드 변경이 이미 완료**되어 있어 스코프를 정정한다. 이 판독이 계약의 전제다.

- **[정정] ReleaseCell destination 스코프(감사 C-④/A-7)의 코드 변경은 이미 존재한다.** 감사 시점(2026-07-01)의 `ICellSelector.ReleaseCell(int cellNo)`는 이후 **S-CELL-ACCUM Scope 5**에서 `ReleaseEmptyAssignment(int chuteNo, string barcode, int cellNo)`로 대체되었고, 구현(`DbRepositories.cs:661-698`)이 `a.Cell.DestinationId == dest.Id` 필터로 **이미 destination 스코프**다(주석이 명시적으로 "A-7 회귀 차단"을 인용). 오더 완료 시 해제 경로(`DbRepositories.cs:908`)도 `a.OrderId == orderId` 오더 스코프라 destination-safe. 호출부(`RcsController.cs:322`)도 `req.ChuteNo`를 전달한다.
  - **따라서 시그니처/스코프 코드 변경은 이 스프린트에서 재수행하지 않는다.**
  - **잔여 갭(진짜 미충족)**: 감사 A-7 권고의 "**멀티소터 회귀 테스트**"가 여전히 부재하다. 기존 멀티소터 테스트(`P2bMultiSorterTests`, E2E `A6`/`F5`)는 핸드셰이크·sorter_command 교차 0만 단언하고 `cell_assignment` **해제 격리는 검증하지 않는다**(A-7 검증노트 명시). → 스코프 항목 2는 "이미 올바른 스코프 동작을 잠그는 회귀 테스트 신설"로 축소·정정한다.
- **[유효] piece(PId,IsActive)·order_item(Barcode) 인덱스(감사 C-③/A-3)는 여전히 미충족.** piece에는 필터드 유니크 `UQ_piece_pid_active_status`(필터 `IsActive=1 AND Status IN(...)`)만 있어 핫패스 `PId==pId && IsActive`(Status 술어 없음) 조회가 이 인덱스를 못 쓴다(영구 테이블 풀스캔). order_item에는 복합 `UQ_order_item_order_barcode`(OrderId 선두)만 있어 IF-05의 순수 Barcode 조회에 못 쓴다. 둘 다 신규 인덱스 필요.
- **[유효] 슈트 복구 하트비트 부재는 실재.** `DestinationStatusPusher.RunSorterObserveLoopAsync`(`DestinationStatusPusher.cs:242-246`)가 `st.DestType != SORTER_3D`를 continue로 건너뛰어 슈트를 관찰하지 않는다. 슈트 push가 재시도 소진으로 실패하면(Acked≠Computed) 다음 슈트 이벤트까지 stale. 만재 슈트는 그 이벤트가 안 와 무기한 stale → RCS가 "받을 수 있음"으로 오인.
- **[유효] Code Review Minor 2건 실재.** `Program.cs:198-199` `IDestinationChangeNotifier` DI 등록은 소비처 0(이벤트 구독으로 대체됨 — 백엔드 전수 grep에서 인터페이스 정의·클래스·이 DI 등록 3곳뿐). `StartAsync`의 `_cts` 생성(`:161`)이 부트스트랩 발신 루프(`:153-158`) **뒤**라 부트스트랩 push가 `CancellationToken.None`로 나감.

---

## Goal

RCS 실투입을 앞두고, 코드 표면을 최소로 건드리면서 세 개의 운영 리스크를 닫는다.
1. **관측된 침묵 실패 복구** — RCS 장애 복구 후 만재 슈트의 수용상태가 RCS에 영구히 stale로 남는(=RCS가 만재 슈트를 "열림"으로 오인) 구멍을 닫는다.
2. **멀티소터 2대째 투입 전 안전 잠금** — 이미 올바른 셀 해제 destination 스코프 동작을, 두 소터가 같은 CellNo를 공유할 때 교차 해제되지 않음을 입증하는 회귀 테스트로 잠근다.
3. **운영 규모 성능 보증** — 영구 보존 테이블(piece)·매 호출 스캔(order_item)의 핫패스 조회가 인덱스를 타도록 하여 수개월 운영 후 API 3s 예산 열화를 예방한다.

부수로, 직전 스프린트(S-IF08-READY-PUSH)가 동일 파일에 남긴 비블로킹 Minor 2건을 자연 동반 처리한다.

---

## Implementation Scope (Generator가 수행할 것 — WHAT)

### 항목 1 — 슈트 복구 하트비트 (`Wcs.Api/Services/DestinationStatusPusher.cs`)
- 소터 전용인 관찰 루프(`RunSorterObserveLoopAsync`)를 확장하여, **매 관찰 주기에 `Acked != Computed`(=성공 발신 못 한 미동기 상태)인 슈트(CHUTE) destination을 재평가·재발신 시도**하도록 한다. 이로써 슈트 push가 재시도 소진으로 실패해도 후속 슈트 이벤트 없이 주기적으로 자동 복구(재푸시)된다.
- **S-IF08 계약 성질을 반드시 보존한다**(회귀 0):
  - 전이당 정확히 1회 발신(중복 0·누락 0) — `PumpAsync`의 per-dest `Gate` 락 + `PushInFlight` 클레임 + `Acked==Computed` 값기반 억제 경로를 우회하지 않는다.
  - 같은 chuteNo에 대해 모순(한쪽 3·다른 쪽 2) 발신 불가 — 단일 술어 `ComputeAccept` 단일 소스 유지.
  - **무변화(Acked==Computed) 슈트는 폴마다 재발신 0**(폭주 금지). 즉 하트비트는 "미동기 슈트만" 재구동하고, 이미 동기된 슈트는 건드리지 않는다.
  - DORMANT(BaseUrl 미설정) 시 관찰 루프·발신 전면 비활성 불변(HTTP 0·크래시 0·구독 0).
- **타이밍 하드코딩 금지(절대규칙 #7)**: 새 주기 상수를 도입하지 말 것. 기존 관찰 루프 cadence(`ChuteStatePush.SorterObserveIntervalMs`, appsettings)를 재사용한다. 슈트 Compute는 인메모리 GetHold 기반이라 비용 ~0.

### 항목 2 — 멀티소터 셀 해제 격리 회귀 테스트 (테스트 전용, `backend/tests/Wcs.Tests`)
- **코드 변경 없음.** `ReleaseEmptyAssignment`는 이미 destination 스코프다(위 선행 판독 참조). 이 항목은 **그 올바른 동작을 잠그는 회귀 테스트 신설**이다.
- 시나리오: 소터 A·소터 B가 **같은 CellNo**(예: 둘 다 CellNo=1)에 각각 활성 `cell_assignment`를 보유한 상태에서, 소터 A의 그 셀에 대해 `ReleaseEmptyAssignment`(빈 orphan 롤백 경로)를 호출했을 때 → **소터 B의 동일 CellNo 활성 배정은 생존**함을 단언한다(교차 해제 0). 기존 EC-12(단일 소터 O/P)는 이 교차 격리를 커버하지 않으므로 별도 케이스가 필요.
- 기존 스코프 동작을 검증하는 테스트이므로 GREEN이 정상(현 코드가 이미 올바름 — 이 테스트가 RED면 회귀가 있는 것).

### 항목 3 — 핫패스 인덱스 2종 + 양 provider 마이그레이션 (`Wcs.Data/WcsDbContext.cs` + `Wcs.Migrations.SqlServer` + `Wcs.Migrations.Sqlite`)
- **piece**: 핫패스 조회 `PId==pId && IsActive`(Status 술어 없음)가 실제로 탈 수 있는 인덱스를 추가한다(현 필터드 유니크는 Status 필터 때문에 이 조회에 못 씀). 인덱스 정확한 형태(예: 비필터 `(PId, IsActive)` 또는 `(PId)`)는 Generator 재량 — 조건은 "이 조회가 seek로 처리되게" + 기존 `UQ_piece_pid_active_status`(멱등 백스톱)와 공존·비파괴.
- **order_item**: IF-05의 순수 `Barcode` 조회가 탈 수 있는 인덱스를 추가한다(현 복합 `(OrderId, Barcode)`는 OrderId 선두라 못 씀). 기존 유니크 제약과 공존·비파괴.
- **마이그레이션은 provider별 2종 모두 생성**: `Wcs.Migrations.SqlServer`와 `Wcs.Migrations.Sqlite` 양쪽에 신규 마이그레이션을 추가하고 각 `WcsDbContextModelSnapshot.cs`를 갱신한다(최신 마이그레이션 `AddB2BArchivedAt` 뒤에 체이닝). 두 provider 스냅샷/마이그레이션이 동일 스키마 델타를 표현해야 한다.

### 항목 4 — 라이드얼롱 (항목 1과 동일 파일 — 자연 동반)
- `Program.cs:198-199`의 사장 `IDestinationChangeNotifier` DI 등록을 제거한다(소비처 0 확인됨 — 제거해도 이벤트 구독 경로 무영향).
- `DestinationStatusPusher.StartAsync`에서 `_cts` 생성(`:161`)을 **부트스트랩 발신 루프(`:153-158`) 앞으로 재배치**하여 부트스트랩 push도 `_cts.Token`으로 취소 가능하게 한다.
- 두 변경 모두 항목 1과 같은 파일군이라 동반 처리. 스코프 비대화 없음.

---

## Constraints (무접촉·금지 — 위반 시 harness violation)

- **무접촉 프로젝트**: `Wcs.PlcGateway`·`Wcs.Core`·핸드셰이크(`HandshakeOrchestrator`)는 건드리지 않는다(감사 묶음 D는 이 스프린트 스코프 아님 — 섞지 말 것). Modbus 레지스터 맵 불변.
- **DB 안전(마이그레이션 교훈 — `tasks/lessons.md` 2026-06-30 준수)**: SQLite만으로 검증 금지. SqlServer 마이그레이션은 **로컬 SQL Server의 일회용 스크래치 DB**(예: `WcsMigCheck_임시`, 검증 후 DROP)에 `ef database update`로 적용·검증한다. 기존 `localhost/Rcs3dsInterlockingWcs`(사용자 데이터) 및 Azure/현장 DB는 절대 건드리지 않는다. 실 PLC/COM1 무접촉.
- **검증 포트(고정 :5205/:1502는 사용자 소유 — 사용 금지)**: 평가자 API :5215 / Sim :1512, 생성자 API :5216 / Sim :1513. 자동 테스트는 기존대로 동적/loopback 포트 사용(고정 포트 미사용).
- **하드코딩 금지(절대규칙 #7)**: 모든 시간값·상한은 appsettings에서. 항목 1은 신규 타이밍 상수 도입 금지(기존 `SorterObserveIntervalMs` 재사용).
- **회귀 0**: 기존 테스트 전량 GREEN 유지. 스키마 변경(항목 3)이 기존 마이그레이션/스냅샷을 깨지 않아야 한다.
- **스코프 통제**: 위 4개 항목 밖 파일 변경 금지. 특히 항목 2는 테스트 파일만, `ReleaseEmptyAssignment` 본문/시그니처 무변경.

---

## Evaluation Criteria (Evaluator 판정 기준 — Backend/API 4기준)

1. **API/컴포넌트 설계 정합성 (★★★)** — 하트비트가 S-IF08 단일 발신 소스·전이당 1회 계약을 우회하지 않고 확장했는가(모순 발신 불가·폭주 0 구조 보존). 인덱스가 스키마 계약(기존 유니크 공존)을 깨지 않는가.
2. **아키텍처 원본성 (★★★)** — 관찰 루프 확장이 별도 병렬 경로가 아니라 기존 `Observe→PumpAsync` 단일 경로로 수렴하는가(이중 소스 재도입 금지). 마이그레이션이 양 provider 대칭인가.
3. **Craft (★★)** — 하드코딩 0(신규 타이밍 상수 없음), DORMANT/teardown 방어 보존, 마이그레이션 스크래치 DB 검증 절차 준수, 예외 삼킴 0.
4. **Functionality (★★)** — 아래 Verification Scenario를 실제 재현으로 입증(코드 리뷰 대체 금지). 회귀 0. 인덱스가 실제로 생성됨을 스키마로 확인.

**독립 재실행 의무**: Evaluator는 Generator 보고를 신뢰하지 않고 `dotnet test backend/Wcs.sln`을 처음부터 재실행한다. 실-Kestrel/폴링 I/O flake 이력(S9·testhost teardown) 대비, 하트비트/push 관련 군은 **≥5회 반복 GREEN**으로 결정성을 확인한다(단일 run 신뢰 금지).

---

## Completion Conditions (Evaluator 통과 최소 조건)

1. `dotnet build backend/Wcs.sln` 오류 0. 신규 경고 0(선재 NU1903 advisory는 제외 — 기존 부채).
2. `dotnet test backend/Wcs.sln` 전량 GREEN·skip 0·회귀 0. 하트비트/push 군 ≥5회 반복 GREEN(flake 0).
3. **항목 1**: 아래 VS-1(하트비트 복구) 자동 테스트가 존재하고 GREEN. S-IF08 계약 성질(전이당 1회·무모순·무변화 폭주 0·DORMANT 불변) 보존을 기존 push 스위트 회귀 0으로 입증.
4. **항목 2**: 아래 VS-2(멀티소터 해제 격리) 회귀 테스트가 존재하고 GREEN. `ReleaseEmptyAssignment` 시그니처/본문 무변경(git diff로 확인).
5. **항목 3**: 신규 마이그레이션이 `Wcs.Migrations.SqlServer`·`Wcs.Migrations.Sqlite` **양쪽**에 존재하고 각 스냅샷 갱신됨. VS-3(양 provider 적용 + 스키마 인덱스 확인) 입증. SqlServer는 스크래치 DB에 `ef database update`로 적용·인덱스 확인 후 DROP(사용자 DB 무접촉).
6. **항목 4**: `IDestinationChangeNotifier` DI 등록 제거 후 빌드/기동 정상(소비처 0이므로 무영향). `_cts` 재배치 후 부트스트랩 push가 취소 가능·기존 teardown 테스트 GREEN.
7. `Wcs.PlcGateway`·`Wcs.Core`·핸드셰이크 diff 0(git status로 확인).

---

## Parallel Modules

N/A (single module). 세 항목이 파일 경계상 분리 가능하나 규모가 작고, 항목 2·항목 1의 테스트가 동일 테스트 프로젝트(`Wcs.Tests`)에 기록되며 항목 3 마이그레이션은 순차 스크래치-DB 검증이 필요해 fan-out 조정 비용이 이득을 상회한다. "When unsure, start with Generate-Verify."

## Evaluation Dimensions

functional only. 성능(인덱스)·동시성(하트비트)·스키마(마이그레이션) 각도가 있으나 전부 자동 테스트 + 마이그레이션 적용 확인 + push 복구 재현이라는 단일 functional 검증 차원으로 수렴한다(별도 보안/성능 전문 풀 불요).

---

## Detected Project Type: Backend/API

**판정 근거(리포 구조 직접 판독)**: 리포에는 브라우저 프런트엔드(React — `frontend/`)와 서버측 컨트롤러가 공존하여 리포 전체로는 Full-stack이다. 그러나 **이 스프린트의 변경 표면은 100% 서버측**이다 — IHostedService(`DestinationStatusPusher`), EF Core 스키마/마이그레이션, DI 등록(`Program.cs`), 리포지토리 회귀 테스트. 브라우저 대면 표면을 전혀 건드리지 않는다. Sprint Contract Template의 "시나리오는 이 스프린트 변경 표면의 실제 면적에서 도출" 규칙에 따라, 프런트 시나리오를 채우는 것은 패딩(역방향 harness 위반)이므로 **Backend/API 슬롯을 정직한 집합으로 채운다**. (프런트 무접촉은 VS-4로 명시 검증.)

---

## Verification Scenarios (Backend/API — 전부 실제 재현으로 입증, 코드 리뷰 대체 금지)

### 슬롯 1 — 이 스프린트가 거치는(behavior-driven) 엔드포인트/와이어 (method + path)
- **아웃바운드 push 와이어**: `PUT {RCS_base}/api/UpdateChuteState` (ChuteStatePush 발신 — 하트비트가 재구동하는 대상). 가짜 RCS 수신 서버(FakeChuteStateServer, 동적 loopback 포트)가 실제 수신 본문으로 입증.
- **인바운드 IF-05**: `POST /api/v1/destination-query` — 슈트 예약으로 capacity 전이 유발(하트비트 트리거원) + order_item(Barcode) 인덱스 조회 경로.
- **인바운드 IF-10**: `POST /api/v1/deposit-report` — 슈트 OnDeposited로 capacity 전이 유발 + 소터 셀 배정/해제 경로(항목 2) + piece(PId,IsActive) 인덱스 조회 경로.
- **인바운드 IF-09**: `POST /api/v1/arrival-report` — piece(PId,IsActive) 인덱스 조회 경로.
- (이 스프린트는 위 엔드포인트를 **신설·수정하지 않는다** — 동작을 구동/검증하기 위해 거치기만 한다. 계약·응답 형상 불변.)

### 슬롯 2 — Happy path (입력 → 출력/관측 형상)
- **VS-1 (항목 1 — 하트비트 복구, 실효 입증)**: (a) 만재 슈트가 accept=false(next_state=2)로 전이 → RCS 다운(수신 실패)이라 push 재시도 소진(Acked≠Computed 잔존). (b) RCS 복구(수신 재개). (c) **후속 슈트 이벤트 없이** 관찰 주기 경과만으로 → 가짜 RCS가 그 슈트의 최신 상태(2)를 재수신함을 단언. 배경(스코프 근거)의 "만재 슈트 2 재도달"을 정확히 재현: 만재 슈트 2건이 복구 후 재도달하는지 확인.
- **VS-2 (항목 2 — 멀티소터 해제 격리)**: 소터 A·B가 같은 CellNo에 각각 활성 배정 보유 → A의 그 셀 `ReleaseEmptyAssignment`(빈 orphan) 호출 → A 배정은 해제(또는 조건상 유지), **B의 동일 CellNo 배정은 생존**(교차 해제 0)을 DB 카운트로 단언.
- **VS-3 (항목 3 — 양 provider 마이그레이션 + 스키마)**: (a) SQLite: 신규 마이그레이션 적용된 스키마에 piece·order_item 신규 인덱스 실재(테스트/스키마 조회로 확인). (b) SqlServer: 스크래치 DB(`WcsMigCheck_임시`)에 `ef database update` 적용 → `sys.indexes`(또는 동등) 조회로 두 인덱스 실재 확인 → DROP. 양쪽 스냅샷이 동일 델타 표현.
- **VS-4 (무접촉·회귀 0)**: `Wcs.PlcGateway`/`Wcs.Core`/핸드셰이크/프런트엔드 diff 0(git status). 전체 스위트 GREEN + push/E2E 군 ≥5회 반복 GREEN.

### 슬롯 3 — 관련 에러/경계 케이스 (이 스프린트에 해당하는 것만, 패딩 없음)
- **VS-5 (하트비트 계약 보존 — 폭주/모순 금지)**: 이미 동기된(Acked==Computed) 슈트는 관찰 주기가 반복돼도 재발신 0(가짜 RCS 수신 카운트 stable). 같은 chuteNo 모순(3↔2 동시) 발신 0. per-dest 전이당 1회 불변.
- **VS-6 (DORMANT 불변)**: BaseUrl 미설정 시 하트비트 포함 발신 0·크래시 0·인바운드(IF-05/09/10) 정상.
- **VS-7 (라이드얼롱 무해)**: `IDestinationChangeNotifier` DI 등록 제거 후 앱 부팅·기존 push 경로(슈트 capacity 콜백·소터 관찰·운영자 전이) 정상. `_cts` 재배치 후 teardown 경쟁 테스트 GREEN(부트스트랩 push 취소 가능).

> Planner self-check — Detected project type: Backend/API. Required scenario slots: 3 (endpoints-touched, happy-path-per-endpoint[VS-1·VS-2·VS-3·VS-4], error/boundary-cases[VS-5·VS-6·VS-7]). All slots filled: yes.
