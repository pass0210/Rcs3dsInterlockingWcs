# Sprint Contract — 소터 셀 만재 판정 (m4p4)

> Branch: `feat/sorter-cell-fullness` (develop @ PR #13 머지에서 분기)
> Planner 작성 · WHAT만 정의 (HOW는 Generator 결정) · 사용자 확인 대기 (Phase 1 게이트)

---

## ⚠ 사용자 확인 필요 (계약 확정 전 결정 요청)

아래 4개는 설계 분기점이다. **권고안을 기본값으로 계약 본문에 반영**했으나, 사용자가 다르게 결정하면 계약을 갱신한다.

### Q1. IF-05 "piece→오더→셀 해소" vs 푸시 "목적지→빈셀" 산출 — 통합 or 분리?
두 소비자가 묻는 질문이 **다르다**:
- **IF-05 (배정 시점)**: "이 piece의 오더가 셀을 확보할 수 있는가?" = ① 그 오더의 활성 cell_assignment 재사용 가능 **OR** ② 빈 셀(enabled·미점유) ≥ 1. → barcode/오더 의존. `EfCellSelector.SelectCell`의 ①②분기와 동형(단 SelectCell은 부수효과로 배정까지 함 — IF-05 필터는 **읽기만** 해야 함).
- **푸시 `ready` (목적지 상태)**: "이 소터가 새 오더를 받을 수 있는가?" = 빈 셀 ≥ 1. → barcode 무관, 목적지 단위.

**차이가 드러나는 경우**: 오더 A가 마지막 빈 셀을 점유 중 → 빈 셀 0개. 이때 (a) 오더 A의 다음 piece IF-05 = **OK**(활성 assignment 재사용) (b) 소터 푸시 ready = **false**(새 오더 불가) (c) 오더 B의 piece IF-05 = **NG**(재사용 불가 + 빈 셀 0).

→ **권고**: `DestinationReadiness.Full`은 **목적지 빈셀 산출**(빈 셀 0 = Full)로 정의하고 푸시 ready·IF-05 NG 공통 소비. **단 IF-05는 `Full=true`여도 "그 오더의 활성 assignment가 있으면 OK"인 예외**가 필요(위 (a)). 이를 위해 IF-05 availability 콜백 경로에서 **barcode 기반 "오더 활성 assignment 보유" 여부**를 추가 조회해, `Full && !오더활성assignment보유`일 때만 NG. (즉 산출의 "빈셀=0" 부분은 공유, "오더 재사용 예외"는 IF-05 전용 추가 판정.)
**대안**: 단순화 — IF-05도 빈셀 0이면 무조건 NG(오더 재사용 예외 미적용). 스펙 §4 "오더 활성 cell_assignment 있으면 재사용"과 충돌 가능 → 확인 필요.
**Q1 결정 요청**: 위 권고(오더 재사용 예외 적용) vs 단순화(빈셀 0이면 일괄 NG) 중 무엇인가?

### Q2. 소터 `full` 변화의 푸시 트리거 — 어떤 변화 감지원?
현재 `DestinationStatusPusher`의 소터 변화원은 **폴링 스냅샷 관찰 타이머**(`RunSorterObserveLoopAsync`, 기본 150ms)뿐. 이 타이머는 매 주기 **모든 소터에 대해 `Compute` 호출**한다. `ComputeSorter`가 빈셀(cell_assignment)을 DB 조회하도록 바꾸면 — cell_assignment 변화(IF-10 `SelectCell` 배정 / 백그라운드 `ReleaseCell` 해제)도 **이 타이머가 자연히 포착**한다(별도 변화원 불요).
- **비용**: 소터 1대당 매 150ms DB 쿼리 1회(빈셀 카운트). 멀티소터·짧은 주기면 부하.
- **대안**: IF-10 셀 배정/해제 시점에 명시적 `IDestinationChangeNotifier.NotifyCellChanged(destId)` 콜백 추가(슈트 `OnChuteStateChanged`와 동형) → 타이머 DB 조회 없이 이벤트 기반.

→ **권고**: **타이머 경로 재사용(별도 콜백 없음)** — 가장 작은 변경, 변화원 추가 0. `ComputeSorter`에 빈셀 DB 조회가 들어가므로 관찰 주기 부하를 수용. (RcsPushTests의 소터 관찰 주기는 30ms로 동작 중 — 테스트 부하 OK 확인됨.)
**Q2 결정 요청**: 타이머 재사용(권고) vs 명시적 cell-change 콜백 추가 중 무엇인가? (부하 우려 시 후자.)

### Q3. `DestinationStatusService`의 DB 접근 경계
현재 `DestinationStatusService`는 **싱글톤**(`Program.cs:101`)이고 의존(ChuteCapacityService·SorterRegistry·WcsOptions)이 전부 싱글톤. 그러나 cell/cell_assignment 조회는 **scoped `WcsDbContext`** 필요 — 싱글톤이 scoped DbContext를 직접 주입하면 captive dependency(안티패턴).
→ **권고**: `ChuteCapacityService`·`DestinationStatusPusher`와 **동일 패턴** — 생성자에 `IServiceScopeFactory`(싱글톤) 주입, `ComputeSorter` 내부에서 `CreateScope()`로 `WcsDbContext` 취득해 빈셀 카운트 조회. 순수 `DepositDecider`는 무변경(스냅샷·hold 입력만).
**Q3 결정 요청**: `IServiceScopeFactory` 주입(권고) 동의? (다른 경계 선호 시 지정.)

### Q4. 마이그레이션 요부
cell·cell_assignment 테이블·`(cell_id) WHERE released_at IS NULL` 부분 유니크 인덱스는 **이미 존재**(ERD·`WcsDbContext` 확인). 빈셀 판정은 **조회만** — 신규 컬럼/테이블/인덱스 불요.
→ **권고**: **마이그레이션 없음**. (protected zone 미해당.)
**Q4 결정 요청**: 마이그레이션 불필요에 동의? (스키마 변경이 실제로 필요하다고 보면 protected zone → 별도 확인.)

---

## Goal
RCS↔WCS 재설계 Phase 1·2에서 의도적으로 이연한 **3D 소터의 `full`/`paused` 산출**을 구현하고, 두 소비자에 반영한다: **(1) IF-05 NG 상류 필터**(만재·정지 소터엔 배정 안 함) **(2) IF-08 푸시 `ready`**(full/paused 전이 시 ready=false 푸시·해소 시 true 재푸시). `DestinationStatusService.ComputeSorter`가 현재 하드코딩한 `Full:false, Paused:false`를 실제 산출로 대체한다.

## Implementation Scope (Generator가 할 일)
1. **`ComputeSorter` paused 산출**: 소터 destination이 비활성(`IsActive==false`) 또는 `Status==PAUSED`면 `Paused:true, Ready:false`. (슈트 `ComputeChute`와 동형. 현재 소터 경로는 이 검사를 안 함 — destination 행 조회 필요.)
2. **`ComputeSorter` full 산출**: 그 소터 소속 **enabled 셀 중 미점유(활성 cell_assignment 없는) 셀이 0개**면 `Full:true, Ready:false`. 빈셀 판정 = `cell.Enabled && DestinationId==소터 && id NOT IN (released_at IS NULL인 cell_assignment의 cell_id)`. `EfCellSelector` ②분기의 `occupiedCellIds`/`freeCell` 쿼리 로직 재활용(읽기 전용 — 배정 부수효과 없이).
3. **`ComputeSorter` ready 합성**: 현 `ready = decision.Ready`(online && CurFloor==운영층 && Ready==1)를 → `ready = !full && !paused && decision.Ready`로 변경. `DenyReason` 우선순위 명시(권고: Offline > Paused > Full > decision.Reason).
4. **DB 접근 경계(Q3)**: `DestinationStatusService` 싱글톤에 `IServiceScopeFactory` 주입, `ComputeSorter`에서 scope 생성해 destination·cell·cell_assignment 조회. `DepositDecider`(순수) 무변경.
5. **IF-05 NG 필터 결선**: `RcsController.DestinationQuery`의 `availability` 콜백은 이미 `r.Full`/`r.Paused`를 `DestinationBlock`으로 매핑(`RcsController.cs:57-64`) — 소터도 이제 Full/Paused를 반환하므로 **기본은 코드 변경 없이 자동 NG**. (Q1 권고 적용 시: "오더 활성 assignment 보유" 예외를 IF-05 availability 경로에 추가 판정 — barcode 기반.)
6. **푸시 ready 결선(Q2)**: 권고(타이머 재사용)면 `DestinationStatusPusher` 코드 변경 0 — `Compute`가 full/paused를 ready에 접으므로 기존 소터 관찰 타이머가 전이 자동 포착. (명시적 콜백 선택 시: IF-10 SelectCell/ReleaseCell 경로에 `NotifyCellChanged` 추가 + Pusher 구독.)
7. **테스트 추가**: 아래 Verification Scenarios를 `RcsPushTests`(푸시) + `ApiIntegrationTests`(IF-05 NG)에 실 DB·실 cell_assignment·가짜 RCS 수신 서버로 구현.

## 무변경 / 회귀 0 (절대 건드리지 않음)
- **Modbus·C/R 핸드셰이크·Sim3ds·`DepositDecider`(순수)** 본문 무변경.
- **인바운드 IF-05/09/10·푸시 Phase 2 기존 동작** 회귀 0 (기존 `RcsPushTests` VS-PUSH-1~8 + `ApiIntegrationTests` 전부 GREEN 유지).
- **DB 스키마** 변경 없음(Q4 — cell/cell_assignment 기존 테이블·부분유니크 재사용). 변경 필요 판명 시 protected zone → 사용자 확인.
- **teardown exit 0** 유지 (`DisposeAsync`·`StopAsync` 경쟁 경로 무변경).
- **절대규칙 준수**: PLC 쓰기 단일 큐(#1), TgtFloor 조건(#2·#3), Ready 의미(#4), FULL/PAUSED는 WCS 판단(#5), 설정값(#7 — 하드코딩 금지), Wcs.Core 순수(#8).

## 동시성 / 일관성 (메타교훈 — 인메모리 GREEN ≠ 결함 없음)
- cell 조회(읽기)와 cell_assignment 변화(IF-10 백그라운드 콜백의 `SelectCell` 배정 / `ReleaseCell` 해제)는 **서로 다른 스코프·시점**. `ComputeSorter`의 빈셀 카운트는 호출 시점 스냅샷 — 조회와 배정 사이 race가 있어도 **다음 관찰/다음 IF-05에서 재평가**되므로 영구 오류 없음(eventually consistent). 단 **"빈셀 0인데 ready=true"가 한 순간이라도 새지 않도록** 단일 쿼리(SQL `NOT IN`/`LEFT JOIN ... IS NULL`)로 원자 평가 — check-then-act 분리 금지.
- 푸시 전이 멱등(전이당 1회·중복 0·누락 0)은 기존 `DestinationStatusPusher` per-dest 락·in-flight로 보장 — 이 스프린트는 `Compute`의 ready 산출만 바꾸므로 멱등 메커니즘 무변경.
- 백그라운드 `ReleaseCell`(IF-11 콜백)이 cell_assignment를 해제 → full→!full 전이 → 푸시 ready=true 재푸시가 **타이머 주기 내** 발생해야 함(VS로 검증).

## Evaluation Criteria (Evaluator 판정 기준 + 가중치)
- **(40%) full/paused 산출 정확성**: 빈셀 0 → Full=true·ready=false / 빈셀 ≥1 → Full=false / 비활성·PAUSED → Paused=true·ready=false. 단일 원자 쿼리(check-then-act 분리 없음). 오더 활성 assignment 재사용 예외(Q1 결정대로) 정확.
- **(25%) 두 소비자 결선**: IF-05 소터 full/paused → NG(chuteNo=null). 푸시 ready가 full/paused 전이를 반영(false 푸시·!full 시 true 재푸시), 전이당 1회.
- **(15%) 회귀 0**: 기존 VS-PUSH-1~8·`ApiIntegrationTests`·`DepositDeciderTests`·`ScenarioTests` 전부 GREEN. 무변경 항목 디프 0.
- **(10%) DI·경계 정합**: 싱글톤이 scoped DbContext를 captive 주입하지 않음(`IServiceScopeFactory` 경유). `DepositDecider` 순수 유지. Wcs.Core 의존성 0 불변.
- **(10%) 동시성·예외 격리**: 빈셀 평가 원자성. `Compute` 예외가 관찰 루프·IF-05 핸들러를 죽이지 않음(기존 try-catch 흡수 패턴 유지). teardown exit 0.

## Completion Conditions (Evaluator PASS 최소 조건)
- `dotnet build` 경고 0, `dotnet test` 전체 GREEN (신규 VS 포함).
- 아래 Verification Scenarios 전부 자동화 테스트로 통과 — **가짜 RCS 수신 본문·실 cell_assignment DB 상태**를 ground-truth로 단언(인메모리 카운터 GREEN만으로 PASS 금지 — 메타교훈).
- 무변경 영역(Modbus·Sim3ds·`DepositDecider`·핸드셰이크·스키마) 디프 0.
- Evaluator가 동일 테스트를 독립 재실행해 Generator 주장 검증(fresh evidence).

## Parallel Modules
N/A (single module — `DestinationStatusService` 단일 산출 지점 + 그 테스트. 경계 분할 불가, 1/1/1 유지).

## Evaluation Dimensions
functional + concurrency (동시성이 핵심 위험 — cell 조회와 IF-10 배정/해제 race). 단일 Evaluator가 두 차원 모두 판정(표면적이 좁아 expert pool 분리 불요) — 단 functional 판정 시 동시성 항목(원자 쿼리·전이 멱등·예외 격리)을 **명시 체크 항목**으로 포함.

## Detected Project Type: Backend/API

## Verification Scenarios (Backend/API — mandatory)

### 엔드포인트(메서드 + 경로) — 이 스프린트가 건드리는 표면
- `POST /api/v1/destination-query` (IF-05) — 소터 목적지 full/paused → NG 필터 (신규 동작: 소터 full/paused가 이제 NG)
- 아웃바운드 푸시 `POST {RcsBase}/api/v1/destination-status` (IF-08) — 소터 full/paused 전이 → ready 푸시 (신규 변화원: cell_assignment 변화)
- (간접) `POST /api/v1/deposit-report` (IF-10) — 셀 배정/해제가 full 전이를 유발하는 입력원 (동작 무변경, full 트리거로만 관여)

### Happy path (입력 → 기대 출력)
- **HP-1 (IF-05 빈셀 있음)**: 소터 enabled 셀 3개 중 ≥1 미점유 + online·CurFloor=운영층·Ready=1 → IF-05 `{result:"OK", chuteNo:30}`. 푸시 ready=true.
- **HP-2 (IF-05 오더 재사용)**: 빈셀 0이지만 그 piece의 오더가 활성 cell_assignment 보유 → IF-05 `{result:"OK", chuteNo:30}` (Q1 권고 적용 시). piece_event 내부 reason=NORMAL/BUSY.
- **HP-3 (푸시 full→!full 재푸시)**: 빈셀 0(full → ready=false 푸시됨) → IF-11 백그라운드 `ReleaseCell`로 셀 1개 해제 → 빈셀 1 → 관찰 타이머가 full→!full 전이 감지 → 가짜 RCS가 ready=true 1건 수신(전이당 1회).

### 오류/차단 케이스 (Planner가 적용 대상만 선별 — 패딩 금지)
- **EC-1 (소터 FULL → NG)**: 소터 enabled 셀 전부 활성 cell_assignment 점유(빈셀 0) + 오더 재사용 불가(다른 오더 piece) → IF-05 `{result:"NG", chuteNo:null}`. piece_event reason(내부)=FULL. (도메인 거부 — 와이어는 200+NG, 검증 실패 400 아님.)
- **EC-2 (소터 PAUSED / 비활성 → NG)**: 소터 destination.Status==PAUSED → IF-05 `{result:"NG", chuteNo:null}`, reason(내부)=PAUSED. IsActive==false도 동일 NG — 두 케이스 각각 단언.
- **EC-3 (푸시 !full→full 전이 ready=false)**: 빈셀 ≥1(ready=true 푸시됨) → 마지막 빈 셀 점유(cell_assignment 활성 삽입)로 빈셀 0 전이 → 가짜 RCS가 ready=false 1건 수신(전이당 정확히 1건·중복 0·무변화 폴 폭주 0).
- **EC-4 (paused 전이 푸시)**: 소터 NORMAL(ready=true) → Status PAUSED 전이 → ready=false 푸시 1건. full과 독립적으로 paused 단독 전이 검증.
- **EC-5 (동시성 원자성)**: cell_assignment를 동시 다수 스레드가 배정/해제하는 동안 IF-05/Compute 호출 — "빈셀 0인데 ready=true" 또는 "빈셀 ≥1인데 ready=false" 같은 모순 응답이 단 한 건도 없음(원자 쿼리 검증). 전이 푸시는 최종 상태로 수렴(누락 0).
- **EC-6 (회귀 — 기존 소터 정렬 ready)**: 빈셀 충분 + 미정렬(CurFloor≠운영층) → ready=false (full/paused 아님, decision.Reason). 기존 VS-PUSH-2/3 동작 유지(full 도입이 정렬 ready를 깨지 않음).

> Planner self-check — Detected project type: Backend/API. Required scenario slots: 3 (endpoints-touched, happy-path-per-endpoint, error-cases-per-endpoint). All slots filled: yes.

---

## 사용자 확정 (2026-06-24 — 진행 승인)
1. **Q1 = 오더 재사용 예외(OK)**: 공통 산출 `SorterFull = (그 소터 enabled 셀 중 미점유 셀 0개)`. 
   - **푸시 ready(목적지 단위)**: `SorterFull`이면 ready=false(새 오더 수용 불가). `ComputeSorter`: `!SorterFull && !paused && online && CurFloor==운영층 && Ready==1`.
   - **IF-05(piece 단위)**: piece의 오더가 **활성 cell_assignment 보유**(EfCellSelector 오더 재사용 경로) 시 `SorterFull`이어도 **OK**(자기 셀에 누적). 보유 없고 `SorterFull`이면 **NG**(FULL). 보유 없고 빈셀≥1이면 OK. 즉 IF-05는 SorterFull 위에 "오더 활성 셀 예외"를 더해 piece-aware 판정.
2. **Q2 = 기존 소터 관찰 타이머(150ms) 재사용**: `ComputeSorter`가 빈셀을 DB 조회하면 cell_assignment 변화가 매 주기 Compute에 자동 반영 → full↔!full 전이가 기존 푸시 변화원(소터 스냅샷 관찰)에 포착됨. **별도 cell-change 변화원 불요.**
3. **Q3 = IServiceScopeFactory 주입**: `DestinationStatusService`(싱글톤)는 scoped `WcsDbContext`를 직접 못 받으므로(captive dependency) `IServiceScopeFactory`로 셀 조회 스코프 생성(ChuteCapacityService/푸시 콜백 패턴 동형). I/O 경계 명확화·DepositDecider 순수성 불변.
4. **Q4 = 마이그레이션 없음**: cell·cell_assignment·`(cell_id) WHERE released_at IS NULL` 부분유니크 모두 기존. **DB 스키마 무변경**(protected zone 미접촉). 읽기 전용 조회만 추가.
