# Sprint Contract — 소터 셀 full 정정(셀 작업 투입 수량 기반) + 슈트 IF-05 dispatch 정정

> Branch: `feat/sorter-cell-qty-full` (develop @ PR #14 머지에서 분기)
> Planner 작성 · WHAT만 정의 (HOW는 Generator 결정) · 사용자 확인 대기 (Phase 1 게이트)
> 선행: m4p4(PR #14) — 소터 full="빈 셀 없음" + IF-05 piece-aware 오더 재사용 예외 + 푸시 ready 결선 (머지됨)

---

## ⚠ 사용자 확인 필요 (계약 확정 전 결정 요청)

아래 4개는 설계 분기점이다. **권고안을 기본값으로 계약 본문에 반영**했으나, 사용자가 다르게 결정하면 계약을 갱신한다.
사용자 확정(정밀화 2026-06-25)은 (A)소터 셀 full=수량 기반 (B)슈트 IF-05 OK 두 축까지만 — 아래 4개의 "어떻게 결합/산출하는가"는 미확정으로 남아 있다.

### Q1. 소터 목적지-단위 푸시 `ready` 정의 — 셀 수량 full을 단일 bool로 어떻게 접는가?  ✅ 확정 = 예(push ready도 셀 작업수량 반영)
푸시 ready는 **chuteNo당 단일 bool**(목적지 단위)인데, 새 수량-full은 **셀 단위**다. m4p4까지 푸시 ready의 full은 "빈 셀 0개"였다.

**사용자 확정**: 푸시 목적지-단위 소터 `ready`도 **셀 작업수량을 반영**한다(m4p4 "빈 셀 0개"만으로는 부족).
- **`SorterFull`(목적지 단위) = 빈 enabled 셀 없음 AND 모든 (활성) 배정 셀이 작업수량 도달** — 즉 소터가 새 오더도 기존 오더도 **아무것도 못 받는** 상태. 이때만 push ready=false.
- **빈 enabled 셀이 1개라도 있거나, 활성 배정 셀 중 작업수량 미달이 하나라도 있으면 `SorterFull`=false** — 그 채널(빈 셀=새 오더 / 여유 셀=기존 오더)로 수용 가능하므로 ready 유지(true, 정렬·online 등 다른 조건 충족 시).
- `ComputeSorter`가 이 합성 `SorterFull`을 산출(셀 현재수량은 Q2 산출원을 재사용). **IF-05 piece-aware는 같은 셀 수량 산출을 재사용**하되 "그 piece 오더의 배정 셀"만 별도 체크(목적지 전체가 아니라 그 오더 셀 여유 유무).
- 푸시 변화원은 **기존 150ms 소터 관찰 타이머**(`RunSorterObserveLoopAsync`)가 이 합성 ready 전이를 포착 — 별도 변화원 추가 불요. 단, 셀 적재(sorter_command 기록)·셀 작업수량 도달은 폴 스냅샷 변화가 아니라 **DB 상태 변화**이므로, 관찰 타이머가 매 주기 `Compute`를 호출해 DB를 재조회하면 자연히 포착된다(m4p4 빈셀 조회와 동형 — Compute 안에서 DB 읽음).

### Q2. 셀 "현재 투입 수량" 산출원 — `sorter_command` vs `cell_assignment→order→piece`?
셀 현재 투입 수량 = 그 셀에 deposited된 piece.qty 합. 두 산출 경로:
- **(a) `sorter_command` 경유**: `SorterCommand(CellId, PieceId, Status)` JOIN `Piece.Qty`. 한 piece가 한 셀로 간 사실의 1:1 기록. 셀 현재수량 = `SUM(piece.qty) WHERE sorter_command.cell_id=셀 AND status IN (SENT, COMPLETED)`(또는 piece.status LOADED). **장점**: piece↔cell 직접 연결·qty 보유. **주의**: 재시도=새 행(중복 합산 위험 — piece별 1건만 카운트 필요), 실패(MISMATCH/TIMEOUT)는 적재 안 됐으니 제외.
- **(b) `cell_assignment→order→piece` 경유**: 셀의 활성 배정 오더의 piece들 qty 합. **단점**: cell_assignment는 IF-11 콜백에서 핸드셰이크마다 즉시 released(현 `ReleaseCell` 호출) — released되면 그 셀의 누적 piece를 못 찾음. 오더↔셀이 1:N일 수도(같은 오더가 시간차로 다른 셀). **부정합 위험 큼**.

→ **권고**: **(a) sorter_command 경유** — piece↔cell 직접 연결이 의미상 정확하고 qty를 들고 있다. 셀 현재수량 = `SUM(p.Qty)` over `sorter_command sc JOIN piece p ON sc.piece_id=p.id WHERE sc.cell_id=<셀> AND sc.status=COMPLETED`(성공 적재만). piece별 중복 합산은 "piece당 최신 COMPLETED 1건"으로 방지(같은 piece 재시도 행 중복 카운트 금지). 정확한 SQL·중복 제거·status 필터 경계는 Generator가 ERD·핸드셰이크 코드 보고 확정.
**Q2 결정 요청**: 셀 현재수량을 **sorter_command(COMPLETED) JOIN piece.qty 합**으로 산출하는 데 동의하는가? (cell_assignment 경유를 원하면 — 단 위 released 부정합 해소 방안 필요 — 지정.)

### Q3. `cell.Capacity` NULL의 의미 — 무제한 vs full?
`cell.Capacity`(=셀 작업 투입 수량)가 시드에서 **NULL**(현재 미사용·DbSeeder.cs:117). 마이그레이션 없이 재활용이므로 기존 셀은 NULL일 수 있다.
- **NULL=무제한**: 작업수량 임계치 없음 → 그 셀은 수량으로 full 안 됨(m4p4 "빈 셀 유무"로만 판정 — 회귀 0). 현장이 셀 작업수량을 설정하기 전까지 안전한 기본값.
- **NULL=즉시 full(0 취급)**: 셀에 아무것도 못 받음 → 모든 셀 full → 소터 전체 마비. **위험·부적절**.

→ **권고**: **NULL=무제한**(수량 full 미적용). 셀 현재수량 ≥ Capacity 판정은 `Capacity`가 양수일 때만. NULL/0/음수면 수량-full 판정을 건너뛴다(m4p4 빈셀 모델로만 동작 — 기존 테스트·시드 회귀 0). 이로써 **시드 변경·마이그레이션 없이** 도입 가능하고, 현장이 셀 Capacity를 채우면 그때부터 수량-full 활성.
**Q3 결정 요청**: `cell.Capacity` NULL(또는 ≤0) = **무제한(수량 full 미적용)**에 동의하는가? (테스트는 Capacity를 명시 양수로 세팅해 수량-full 경로를 검증.)

### Q4. 슈트 IF-05 OK 정정이 기존 capacity·푸시 로직과 충돌하지 않는가?
슈트 full/pause → IF-05 **NG→OK**(보냄) 정정. 현재 슈트 차단은 **두 곳**:
1. `EfOrderRepository.QueryDestination`의 **order-level PAUSED 조기 차단**(DbRepositories.cs:69 — `order.Destination?.Status==PAUSED` → NG). 이 검사는 **모든 dest 타입**(슈트+소터)에 적용된다.
2. `RcsController` **availability 콜백**의 `r.Full`/`r.Paused` → `DestinationBlock.Full/Paused` 매핑(RcsController.cs:65-72). 슈트·소터 공통(소터엔 piece-aware 예외).

슈트를 OK로 만들려면 **두 차단 모두**에서 슈트를 통과시키되 **소터는 NG 유지**해야 한다. 또한:
- **푸시(IF-08)는 슈트 readiness를 계속 전달**(사용자 확정2: "슈트 readiness는 푸시로 RCS에 계속 전달"). 즉 IF-05 dispatch와 푸시 ready는 **분리된 소비자** — IF-05 OK여도 푸시 ready=false(슈트 만재/정지)는 그대로 보낸다. 충돌 없음(서로 다른 채널: IF-05는 "보낼지", 푸시는 "받을 수 있는지 상태 통지").
- **ChuteCapacityService 모델 무변경**(GetHold·OnReserved/OnDeposited/OnCleared·집계). IF-05 OK 시 슈트 예약 차감(`capacity.OnReserved`, RcsController.cs:78) — full인데 OK로 보내면 예약이 work_full_qty를 **초과**할 수 있다. → 슈트는 곧 비워지니 초과 예약을 허용한다(사용자 모델: "슈트는 곧 비워지니 보내고 대기"). 단 음수·언더플로 없음(기존 `Math.Max(0, ...)` 가드 유지).

→ **권고**: 슈트 IF-05는 full/paused여도 **항상 OK**(목적지 정상 매칭·활성인 한). 차단점 1·2를 **dest 타입으로 분기** — 슈트는 full/paused 통과(OK), 소터는 m4p4대로 NG(piece-aware 예외 포함). 푸시·capacity 집계는 무변경(IF-05 OK 시 예약 차감은 유지 — 초과 허용). order-level PAUSED 차단(차단점 1)은 **소터에만 적용**되도록 좁히고, 슈트 PAUSED는 통과.
**대안**: 슈트 PAUSED는 여전히 NG로 두고 **FULL만 OK**로 — 사용자 확정은 "슈트 full/pause → OK" 둘 다이므로 대안 채택 안 함(확정과 충돌).
**Q4 결정 요청**: 슈트는 full·paused **둘 다 IF-05 OK**(목적지 활성·매칭 시), 차단점 1·2를 dest 타입 분기로 슈트만 통과시키고 소터 NG 유지, 푸시·capacity 무변경에 동의하는가?

---

## Goal
사용자 정밀화(2026-06-25) 두 축을 구현한다.

**(A) 소터 셀 full = 셀 현재 투입 수량 ≥ 셀 작업 투입 수량** (수량 기반 단일 임계치).
m4p4의 "빈 셀 없음=full"을 수량 기반으로 정밀화한다. 셀 작업 투입 수량 = `cell.Capacity` 재활용(현재 미사용). 셀 현재 투입 수량 = 그 셀에 적재(deposited/loaded)된 piece.qty 합(산출원 = Q2). **결합 모델**: 새 오더(셀 미보유)는 빈 셀 필요(m4p4 free-cell 슬롯 유지), 기존 오더(셀 보유)는 그 셀이 작업수량 미달일 때만 OK. m4p4 IF-05 "오더 셀 보유하면 무조건 OK" 예외에 **"그 셀 현재수량 < 작업수량" 체크를 추가**(셀이 꽉 차면 그 오더 piece도 NG/FULL). 셀은 **기본 투입 수량·마감 reset 없음**(단순 임계치 — 슈트만 3-수량). 만재 센서는 이연(이번 스코프 아님).

**(B) 슈트 IF-05 dispatch full/pause → OK** (소터는 NG 유지).
현재 develop은 슈트도 full/pause → NG. 사용자 모델: 슈트는 곧 비워지니 보내고 대기(OK). 소터 full/pause는 곧 안 풀리니 NG. **슈트만 OK로 정정, 소터 NG 유지**. 슈트 readiness는 푸시(IF-08)로 RCS에 계속 전달(IF-05 dispatch와 분리된 채널).

## Implementation Scope (Generator가 할 일)
1. **셀 현재 투입 수량 산출(읽기 전용)**: Q2 권고(sorter_command COMPLETED JOIN piece.qty 합) 경로로 `셀별 현재 투입 수량`을 산출하는 읽기 전용 조회를 추가. piece별 중복 합산 금지(재시도 행). 정확한 SQL·status 경계·중복 제거는 Generator가 ERD·핸드셰이크(`EfSorterCommandJournal`·`TriggerSorterHandshake`) 코드 보고 결정.
2. **셀 작업 투입 수량 = `cell.Capacity` 재활용**: Q3 권고(NULL/≤0 = 무제한 = 수량-full 미적용). 양수일 때만 `현재 ≥ Capacity` → 그 셀 full.
3. **IF-05 piece-aware 수량 full 결선**: `RcsController.DestinationQuery` availability/예외 경로에서, 소터 + 그 piece 오더가 활성 cell_assignment 보유(m4p4 `SorterHasActiveAssignmentForBarcode`) 시 — **추가로 그 배정 셀의 현재수량 < 작업수량인지** 확인. 셀이 꽉 찼으면(현재 ≥ Capacity) 그 piece도 NG(FULL). 빈 셀로 새로 받을 수 있으면(빈 셀 ≥1) OK. m4p4 "오더 셀 보유=무조건 OK"를 "오더 셀 보유 AND 그 셀 여유"로 좁힌다.
4. **소터 목적지-단위 full/ready 수량 반영(Q1=예)**: `DestinationStatusService.ComputeSorter`의 `SorterFull`을 수량 기반으로 **정밀화**한다 — `SorterFull = 빈 enabled 셀 없음 AND 모든 활성 배정 셀이 작업수량 도달`(현재수량 ≥ `cell.Capacity` 양수). 빈 셀 ≥1 또는 작업수량 미달 배정 셀 ≥1이면 `SorterFull`=false. 셀 현재수량은 1번의 산출원(sorter_command JOIN piece.qty)을 **재사용**(IF-05 piece-aware와 동일 산출 공유 — 중복 구현 0). 푸시 ready 합성 식(`ready = !SorterFull && !paused && decision.Ready`)은 형태 유지하되 `SorterFull` 의미가 수량 반영으로 확장. 단일 원자 쿼리로 평가(빈셀 카운트 + 배정셀별 작업수량 도달 여부를 한 시점 스냅샷으로 — check-then-act 분리 금지).
5. **(B) 슈트 IF-05 full/pause → OK**: Q4 결정에 따라 차단점 1(order-level PAUSED, DbRepositories.cs:69)과 차단점 2(availability 콜백 Full/Paused 매핑, RcsController.cs:65-72)를 **dest 타입으로 분기** — 슈트는 full/paused 통과(OK), 소터는 NG 유지(piece-aware 예외 포함). 슈트 OK 시 예약 차감(`OnReserved`)은 유지(초과 허용).
6. **DB 접근 경계**: 셀 수량 조회는 m4p4와 동일 패턴 — `DestinationStatusService`/`RcsController`가 `IServiceScopeFactory` 또는 요청 스코프 `WcsDbContext`로 읽기. 싱글톤 captive dependency 금지. `DepositDecider`(순수) 무변경.
7. **테스트 추가/수정**: 아래 Verification Scenarios를 `SorterCellFullnessTests`(소터 수량 full) + `ApiIntegrationTests`(슈트 IF-05 OK 회귀 반전)에 실 DB·실 sorter_command/cell_assignment·가짜 RCS 수신으로 구현. **기존 슈트 NG 테스트 3건의 assertion을 OK로 반전**(아래 회귀 항목).

## 무변경 / 회귀 0 (절대 건드리지 않음)
- **Modbus·C/R 핸드셰이크·Sim3ds·`DepositDecider`(순수)** 본문 무변경.
- **인바운드 IF-09/10·푸시(Phase 2) 메커니즘·슈트 ChuteCapacity 모델(GetHold·집계·OnCleared)** 회귀 0. (B)는 IF-05 dispatch의 슈트 full/paused NG→OK **정정만**.
- **DB 스키마 무변경**: `cell.Capacity`(기존 nullable 컬럼) 재활용 — 신규 컬럼/테이블/인덱스/마이그레이션 **불요**(Q3 NULL=무제한이면 시드 변경도 불요). 스키마 변경이 실제 필요하다고 판명되면 protected zone → 사용자 확인.
- **m4p4 자산 재사용**: free-cell 슬롯 로직(`EfCellSelector` ②분기)·`SorterHasActiveAssignmentForBarcode`·`IServiceScopeFactory` 패턴·`SorterCellFullnessTests`/`RcsPushWebApplicationFactory`/`FakeRcsServer`/`OccupyCells`·`ReleaseOneCell`·`FreeCellCount` 헬퍼.
- **teardown exit 0** 유지(`DisposeAsync`·`StopAsync` 경쟁 경로 무변경).
- **절대규칙 준수**: PLC 쓰기 단일 큐(#1), TgtFloor 조건(#2·#3), Ready 의미(#4), FULL/PAUSED는 WCS 판단(#5), 설정값(#7 — 하드코딩 금지), Wcs.Core 순수(#8).

## ⚠ 회귀 — 의도적 동작 변경 (assertion 반전 필요)
(B) 슈트 IF-05 OK 정정으로 **아래 기존 테스트의 기대값이 NG→OK로 바뀐다**. Generator가 반전한다(삭제 금지 — 새 의도로 갱신):
- `ApiIntegrationTests.If05_Chute_Paused_Ng` → 슈트 PAUSED → 이제 **OK**(슈트 readiness는 푸시로 전달). 테스트명/assertion 갱신.
- `ApiIntegrationTests.If05_Chute_Full_ThenCleared_Normal` → 슈트 FULL → 이제 **OK**(비움 전후 둘 다 OK). 수량-full 부분은 무의미해지므로 슈트 OK 단언으로 갱신.
- `ApiIntegrationTests.VS2_If05_PausedOrder_NgPaused` → PAUSED 슈트 오더 → 이제 **OK**. (소터 PAUSED 오더는 NG 유지 — 별도 보강.)
- **소터 PAUSED/FULL NG는 회귀 0**: `SorterCellFullnessTests.EC1/EC2`(소터 FULL/PAUSED → NG) 유지. (B)는 슈트만 정정, 소터 불변.

## 동시성 / 일관성 (메타교훈 — 인메모리 GREEN ≠ 결함 없음)
- 셀 현재수량 조회(읽기)와 IF-10 배정/적재(sorter_command 기록·`ReleaseCell`)는 서로 다른 스코프·시점. 셀 수량 판정(IF-05 piece-aware AND 목적지-단위 `SorterFull` 둘 다)은 **단일 원자 쿼리**(SUM + 비교를 한 쿼리/한 시점 스냅샷)로 — "셀 꽉 찼는데 OK" 또는 "셀 여유 있는데 NG", "소터 전부 꽉 찼는데 push ready=true"가 한 순간도 새지 않게. check-then-act 분리 금지(m4p4 EC-5 원자성 교훈 연장).
- 조회와 적재 사이 race가 있어도 **다음 관찰/다음 IF-05에서 재평가**(eventually consistent) — 영구 오류 0. 단 단일 응답 내부 불변식(셀full ⟹ 그 piece NG / `SorterFull` ⟹ push ready=false)은 항상 성립.
- 푸시 전이 멱등(전이당 1회·중복 0·누락 0)은 기존 Pusher per-dest 락·in-flight로 보장. **Q1=예로 push ready 산출이 수량 반영으로 확장되지만(`Compute` 내부 `SorterFull` 의미만 변경), 전이 추적·락·in-flight·관찰 타이머(150ms) 메커니즘은 무변경** — `Compute`가 DB(sorter_command·cell·cell_assignment)를 매 관찰 주기 재조회해 합성 ready 전이를 포착(m4p4 빈셀 조회와 동형). 멱등 기계는 손대지 않는다.
- (B) 슈트 IF-05 OK 시 `OnReserved` 초과 예약 — 음수/언더플로 없음(`Math.Max(0,...)` 가드 유지), 단조 증가는 비움(OnCleared)에서 리셋.

## Evaluation Criteria (Evaluator 판정 기준 + 가중치)
- **(30%) 소터 셀 수량 full 산출 정확성**: 셀 현재수량(deposited piece.qty 합, Q2 경로·중복 0) ≥ `cell.Capacity`(양수) → 그 셀 full. NULL/≤0=무제한(미적용·Q3). 단일 원자 쿼리. 셀별 정확 합산(piece 재시도 중복 합산 0).
- **(20%) IF-05 결합 모델 정확성**: 새 오더(빈 셀 ≥1)=OK / 기존 오더 셀 여유(현재<작업)=OK / 기존 오더 셀 full(현재≥작업) AND 빈 셀 0 = NG(FULL). m4p4 "오더 셀 보유=무조건 OK"가 "셀 여유 확인"으로 정확히 좁혀짐. piece_event reason(내부) FULL/NORMAL 정합.
- **(15%) 소터 목적지-단위 push ready 수량 반영(Q1=예)**: `SorterFull = 빈 셀 없음 AND 전 활성 배정 셀 작업수량 도달`일 때만 push ready=false. 빈 셀 ≥1 또는 미달 배정 셀 ≥1이면 ready 유지(true, 정렬 등 충족 시). 셀 적재로 마지막 여유가 사라지는 순간 ready=false 전이 1건(관찰 타이머 포착), 여유 생기면 ready=true 재푸시 1건. IF-05 piece-aware 산출과 동일 셀수량 소스 재사용.
- **(15%) (B) 슈트 IF-05 OK 정정 + 소터 NG 유지**: 슈트 full/paused → IF-05 OK(차단점 1·2 모두 슈트 통과). 소터 full/paused → IF-05 NG(불변). 슈트 readiness 푸시는 계속 전달(IF-05 OK와 무관하게 ready=false 푸시).
- **(10%) 회귀 0 + 의도적 반전**: 기존 VS-PUSH-1~8·`SorterCellFullnessTests`(소터)·`DepositDeciderTests`·`ScenarioTests` GREEN. 슈트 NG 3건은 OK로 반전(삭제 아님). 무변경 항목 디프 0(스키마·Modbus·핸드셰이크·DepositDecider·ChuteCapacity 모델).
- **(10%) 동시성·경계·DI 정합**: 셀 수량 평가 원자성(셀full⟹piece NG, `SorterFull`⟹push ready=false 불변식). 싱글톤 captive 주입 0(IServiceScopeFactory). DepositDecider 순수·Wcs.Core 의존성 0 불변. `Compute`/IF-05 예외가 관찰 루프·핸들러를 죽이지 않음. teardown exit 0.

## Completion Conditions (Evaluator PASS 최소 조건)
- `dotnet build` 경고 0, `dotnet test` 전체 GREEN (신규/반전 테스트 포함).
- 아래 Verification Scenarios 전부 자동화 테스트로 통과 — **실 sorter_command/cell_assignment/cell.Capacity DB 상태**와 **가짜 RCS 수신 본문**을 ground-truth로 단언(인메모리 카운터 GREEN만으로 PASS 금지 — 메타교훈).
- 무변경 영역(Modbus·Sim3ds·`DepositDecider`·핸드셰이크·스키마·ChuteCapacity 집계 모델) 디프 0.
- Evaluator가 동일 테스트를 독립 재실행해 Generator 주장 검증(fresh evidence — HTTP 응답 본문·piece_event.reason·DB 셀수량 raw 단언).

## Parallel Modules
N/A (single module — 셀 수량 산출 + IF-05 결선 + 슈트 dispatch 정정이 `DestinationStatusService`/`RcsController`/`DbRepositories` 좁은 표면에 응집. 경계 분할 불가, 1/1/1 유지).

## Evaluation Dimensions
functional + concurrency (동시성이 핵심 위험 — 셀 수량 조회와 IF-10 적재/cell_assignment 변화 race). 단일 Evaluator가 두 차원 모두 판정(표면적이 좁아 expert pool 분리 불요) — 단 functional 판정 시 동시성 항목(원자 쿼리·셀full⟹piece NG 불변식·전이 멱등·예외 격리)을 **명시 체크 항목**으로 포함.

## Detected Project Type: Backend/API

## Verification Scenarios (Backend/API — mandatory)

### 엔드포인트(메서드 + 경로) — 이 스프린트가 건드리는 표면
- `POST /api/v1/destination-query` (IF-05) — 소터 셀 수량 full piece-aware 판정(신규) + 슈트 full/paused → OK(정정).
- 아웃바운드 푸시 `POST {RcsBase}/api/v1/destination-status` (IF-08) — 소터 목적지-단위 ready가 **셀 작업수량 반영**(Q1=예: `SorterFull`=빈셀0 AND 전 배정셀 작업수량도달일 때만 ready=false) + 슈트 readiness 계속 전달(불변).
- (간접) `POST /api/v1/deposit-report` (IF-10) — sorter_command 적재가 셀 현재수량을 누적시키는 입력원(동작 무변경, 셀 수량 트리거로만 관여).

### Happy path (입력 → 기대 출력)
- **HP-1 (소터 셀 여유 → OK)**: 그 piece 오더가 활성 cell_assignment 보유 + 그 셀 현재수량 < `cell.Capacity`(예: Capacity=10, 현재=3) → IF-05 `{result:"OK", chuteNo:30}`. piece_event reason(내부)=NORMAL.
- **HP-2 (소터 새 오더 빈 셀 → OK)**: 셀 미보유 새 오더 + 빈 enabled 셀 ≥1 → IF-05 `{result:"OK", chuteNo:30}`. (m4p4 free-cell 슬롯 유지 — 회귀 가드.)
- **HP-3 (슈트 FULL → OK 정정)**: 슈트 `ChuteCapacityService` Full 주입 → IF-05 `{result:"OK", chuteNo:N}`(NG 아님). 같은 piece에 대해 푸시 ready=false는 별도 전달됨(IF-05 OK ≠ 푸시 ready).
- **HP-4 (슈트 PAUSED → OK 정정)**: 슈트 destination.Status=PAUSED → IF-05 `{result:"OK", chuteNo:N}`(NG 아님). order-level PAUSED 차단(차단점 1)이 슈트엔 미적용 확인.
- **HP-5 (소터 push ready — 배정 셀 일부 여유 → ready 유지·Q1=예)**: 빈 enabled 셀 0개지만 활성 배정 셀 중 **하나라도 작업수량 미달**(예: 셀A 현재=Capacity, 셀B 현재<Capacity) + 정렬·online → 가짜 RCS 수신 `ready=true` 유지(`SorterFull`=false — 그 여유 셀로 기존 오더 수용 가능). `Compute(sorter).Full=false` 동반 단언. **실 sorter_command/cell.Capacity DB 상태로 ground-truth 구성.**

### 오류/차단 케이스 (Planner가 적용 대상만 선별 — 패딩 금지)
- **EC-1 (소터 셀 full → NG)**: 그 piece 오더가 활성 cell_assignment 보유하지만 그 셀 현재수량 ≥ `cell.Capacity`(예: Capacity=5, 현재=5) AND 빈 셀 0 → IF-05 `{result:"NG", chuteNo:null}`. piece_event reason(내부)=FULL. **실 sorter_command 행으로 현재수량을 ground-truth 구성**(인메모리 카운터 아님).
- **EC-2 (소터 빈 셀 0 + 오더 미보유 → NG)**: 새 오더 + 모든 셀 점유(빈 셀 0) → IF-05 NG(FULL). (m4p4 EC-1 유지 — 회귀 가드.)
- **EC-3 (소터 PAUSED/FULL NG 유지)**: 소터 destination PAUSED → IF-05 NG(불변). 소터 빈셀0+오더없음 → NG(불변). (B) 정정이 소터를 깨지 않음 확인.
- **EC-4 (cell.Capacity NULL=무제한)**: 그 셀 `Capacity=NULL` + 현재수량이 아무리 많아도(예: 현재=100) → 수량-full 미적용 → 빈 셀 유무로만 판정(빈 셀 있으면 OK). Q3 권고 검증 — 시드 기본값(NULL)이 소터를 막지 않음(회귀 0).
- **EC-5 (동시성 원자성)**: sorter_command 적재(셀 수량 증가)와 cell_assignment 배정/해제를 동시 다수 스레드가 churn하는 동안 IF-05/Compute 호출 — "셀 현재 ≥ Capacity인데 그 piece OK" 또는 "셀 여유 있는데 NG" 모순 응답 0건(단일 원자 쿼리). 최종 상태로 수렴(누락 0).
- **EC-6 (셀 경계값)**: 현재수량 == Capacity-1 → OK(미달), == Capacity → NG/full(도달), == Capacity+1(초과 적재된 경우) → NG/full. 경계 등호(`≥`) 정확성.
- **EC-7 (소터 push ready=false 전이 — 마지막 여유 소진·Q1=예)**: 빈 셀 0 + 마지막 미달 배정 셀이 작업수량에 도달(sorter_command 적재로 현재 → Capacity)하는 순간 → `SorterFull`=true → 관찰 타이머(150ms)가 `ready=true→false` 전이 감지 → 가짜 RCS가 `ready=false` **정확히 1건** 수신(중복 0·무변화 폴 폭주 0). 이어서 그 셀 작업수량 미달로 되돌리거나 빈 셀 1개 생성 → `ready=true` 재푸시 1건(전이당 1회). m4p4 EC-3/HP-3(빈셀 전이)와 동형이되 **수량 도달이 트리거**.

> Planner self-check — Detected project type: Backend/API. Required scenario slots: 3 (endpoints-touched, happy-path-per-endpoint, error-cases-per-endpoint). All slots filled: yes. Scenario count this sprint: HP-1~5 + EC-1~7 = 12 (소터 수량 full·결합 모델·push ready 수량 반영·슈트 OK 정정·동시성·경계).

---

## 사용자 확정 (2026-06-25 — 진행 승인, Phase 2 진입)

1. **Q1 = 예 (push ready도 셀 작업수량 반영)** ← Planner 권고(아니오)와 다름. 목적지-단위 소터 push `ready`를 셀 작업수량으로 정밀화:
   - **`SorterFull`(목적지 단위) = 빈 enabled 셀 없음 AND 모든 활성 배정 셀이 작업수량 도달**(현재수량 ≥ `cell.Capacity` 양수). 이때만 push `ready=false`(새 오더도 기존 오더도 못 받음).
   - **빈 enabled 셀 ≥1 OR 작업수량 미달 배정 셀 ≥1 → `SorterFull`=false** → ready 유지(정렬·online 등 충족 시 true). 그 채널(빈 셀=새 오더 / 여유 셀=기존 오더)로 수용 가능하므로.
   - `ComputeSorter`가 이 합성 `SorterFull`을 산출. **IF-05 piece-aware는 같은 셀 수량 산출을 재사용**하되 "그 piece 오더의 배정 셀"만 별도 체크(목적지 전체 아님). 푸시 변화원 = 기존 150ms 소터 관찰 타이머(`RunSorterObserveLoopAsync`)가 `Compute` 재호출로 합성 ready 전이 포착 — 별도 변화원·멱등 기계 무변경.

2. **Q2 = sorter_command(COMPLETED) JOIN piece.qty 합**: 셀 현재 투입 수량 = `SUM(piece.qty)` over `sorter_command(status=COMPLETED) JOIN piece` per cell. cell_assignment는 핸드셰이크마다 즉시 released라 **비사용**. piece별 중복 합산 금지(재시도 행 — piece당 1건). 정확한 SQL·status 경계·중복 제거는 Generator가 핸드셰이크 코드(`EfSorterCommandJournal`·`TriggerSorterHandshake`) 보고 확정.

3. **Q3 = cell.Capacity NULL/≤0 = 무제한**(그 셀 수량-full 미적용). 양수일 때만 `현재 ≥ Capacity` → 그 셀 full. **시드·마이그레이션 무변경**(기존 nullable 컬럼 재활용). 테스트는 Capacity를 명시 양수로 세팅해 수량-full 경로 검증.

4. **Q4 = 슈트 IF-05 full·paused 둘 다 OK**(보냄). 차단점 2곳 — ① `QueryDestination` order-level PAUSED(`DbRepositories.cs:69`) ② availability 콜백 Full/Paused 매핑(`RcsController.cs:65-72`) — 에 **dest 타입 분기**: 슈트는 full/paused 통과(OK), 소터는 NG 유지(piece-aware 예외 포함). **ChuteCapacity 집계 무변경**(IF-05 OK 시 OnReserved 초과 예약 허용). 슈트 readiness는 푸시(IF-08)로 계속 전달(IF-05 dispatch와 분리 채널 — IF-05 OK여도 ready=false 푸시).
