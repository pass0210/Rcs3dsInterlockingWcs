# S-CELL-ACCUM — Sprint Contract

3D 소터 셀 누적(accumulation) 바인딩 수정. 현장 관찰(2026-07-09): 같은 barcode/order를 반복 투입해도
한 셀에 누적되지 않고 흩어진다(실측상 전부 cell 1로 몰림). 기대: 한 barcode/order의 다중 piece는
한 셀에 누적되고, 오더가 끝나면 그 셀이 다음 오더에 재사용된다. 사수 확인 — 목적지당 다중 piece가 정상.

> Planner Subagent · 2026-07-09 · **도메인 게이트 사수 확정 반영(재작성)**
> CORE dispatch/셀선택 로직(protected-ish, minimal-impact). PLC 쓰기 로직 불변(신규 PLC 쓰기 0).
> 코드 금지 — 여기서는 **무엇을 고칠지**만 정의한다.

---

## ✅ 확정 정책 (사수 2026-07-09 — 게이트 해소, LOCK)

SPEC/ERD가 침묵하던 릴리스/오버플로 정책을 사수가 확정했다. 아래는 재론 금지 계약 기준.

### Q-b (릴리스 시점) = **RELEASE ON ORDER COMPLETE**
- cell_assignment는 그 **오더가 전량 분류 완료(SortedQty == PlannedQty)** 될 때 release한다.
  오더의 piece들은 그때까지 **같은 셀에 누적**된다.
- **매 투입 무조건 ReleaseCell(`RcsController.cs:428`) 제거** → 오더 완료 시점에만 release.
- 완료 판정은 **기존 데이터로 산출 가능**: `OrderItem.SortedQty`(IF-10/12 확정 시 += qty)와 `PlannedQty`
  (Entities.cs:275-277) 존재, `OrderStatus.COMPLETED` enum 존재(Entities.cs:37). SortedQty 증가 지점에
  release 훅을 co-locate하고, 완료 시 `OrderStatus.COMPLETED` 전이도 함께(선택 — 아래 스코프 참조).

### Q-a (오버플로) = **ONE ORDER = ONE CELL, NO OVERFLOW**
- 한 오더는 **자기 배정 셀 하나에 국한**. 오더 piece가 그 셀 Capacity를 넘기면 두 번째 셀로 흘리지 않고
  **IF-05가 NG/FULL("더 안 받음")** 반환. (시드 PlannedQty=3=Capacity=3 정렬 — 정상 케이스는 정확히
  Capacity에서 완료.)
- **중대 함의(현 코드가 오버플로를 허용함 — 반드시 고칠 것)**: 현재
  `SorterCanAcceptBarcode = HasAssignedCellWithRoom OR HasFreeEnabledCell` 구조는 "오더의 배정 셀이 full이면
  free 셀로 폴백"이라 **두 번째 셀로 오버플로**한다. 이걸 막아야 한다 → 게이트를 **배정 유무 분기**로 바꾼다:
  - 오더가 이미 활성 배정 보유: OK ⟺ **그 배정 셀에 여유**(free 셀 폴백 금지 — 없으면 NG).
  - 오더가 활성 배정 없음(진짜 신규): OK ⟺ 빈 enabled 셀 존재.
  `SelectCell`도 동형으로: 배정 보유면 여유 시 그 셀 재사용·full이면 **null(NG)**(②로 안 감);
  배정 없으면 ② 빈 셀 신규 할당.

### Q-c (재사용) = **release된 셀은 다른 오더가 재사용**
- 오더 완료로 release된 셀은 활성 배정이 사라지므로 `SelectCell` ②가 **다른 신규 오더에 자동 재사용**.

---

## 진단 — 근본 원인 CONFIRMED (실코드 대조 완료)

**주경로**: IF-10 `DepositReport` → `TriggerSorterHandshake`(`RcsController.cs:298`) →
`SelectCell(chuteNo, barcode)`(`:307`) → 백그라운드 `ContinueWith` 콜백(`:342`~`:436`).

1. 콜백 `:428` `scopedCellSelector.ReleaseCell(selectedCell)`가 **무조건** 실행 — `if (t.IsCompletedSuccessfully)`
   블록 **밖**, try 본문 안이라 성공/실패/모든 종결 경로에서 매번 호출.
2. `ReleaseCell(cellNo)`(`DbRepositories.cs:671`)는 그 cellNo의 활성 배정 전부 `ReleasedAt = now`.
3. 결과: order→cell 바인딩(`cell_assignment`, ReleasedAt=NULL)이 **첫 투입 후 즉시 파기** → 2번째+ piece는
   `SelectCell` ①에서 못 찾고 ②(활성 배정 없는 최저 CellNo)로 폴백. **누적 파괴 — 가설 그대로 확정.**
4. **"전부 cell 1로 몰림" 기전**: release 후 cell 1(최저 CellNo)은 다시 활성 배정 0 → ②가 항상 cell 1 반환
   → 결정적 cell 1 funnel. 현장 실측과 정확히 일치.

**복합 결함 CONFIRMED(이 정책 하에 반드시 해소)**: ②와 `HasFreeEnabledCell`의 "빈 셀" 정의는 **활성
cell_assignment 부재만**으로 판정하고 물리 적재량(sorter_command COMPLETED)은 무시한다. release-on-complete로
셀이 재사용될 때, **이전 오더가 그 셀에 쌓은 COMPLETED 적재량(all-time 합)이 새 오더의 여유 계산을 오염**시켜
새 오더가 처음부터 full로 보이거나 반대로 초과 적재될 수 있다. → **적재량 산출을 현재 활성 배정 기간으로
스코프**해야 한다(아래 스코프 §2).

---

## 이 스프린트가 손대는 실코드 지점
- `backend/src/Wcs.Api/Controllers/RcsController.cs` — 콜백 무조건 ReleaseCell 제거(`:428`); OFFLINE/실패 경로
  배정 수명 정합(`:321`); SortedQty 증가·오더완료 release 훅 co-locate 지점.
- `backend/src/Wcs.Api/Repositories/DbRepositories.cs` — `EfCellSelector.SelectCell`(`:572`, 배정-분기·no-overflow
  재구성)/`ReleaseCell`(`:671`, 오더 스코프 release로 재정의).
- `backend/src/Wcs.Api/Services/DestinationStatusService.cs` — `SorterCanAcceptBarcode`(`:141`)를 배정-분기
  술어로 재구성(no-overflow), `HasAssignedCellWithRoom`/`HasFreeEnabledCell`/`ComputeSorterFull` 정합.
- `backend/src/Wcs.Api/Services/SorterCellQty.cs` — `LoadedQtyByCell`를 **현재 활성 배정 기간 스코프**로(§2).
- `backend/src/Wcs.Api/Repositories/Repositories.cs` — `ICellSelector` 시그니처(`:81`; ReleaseCell 오더/destId).

---

## [Sprint Contract]

- **Goal**: 3D 소터에서 한 오더의 다중 piece가 **자기 배정 셀 하나에 누적**(one-order-one-cell, no overflow)되고,
  **오더 완료(SortedQty==PlannedQty) 시에만 release**되어 그 셀이 다른 오더에 재사용되도록 order→cell 바인딩
  수명을 교정한다. 재사용된 셀의 적재량은 현재 배정 기간부터 0으로 카운트한다. IF-05
  `SorterCanAcceptBarcode` ↔ IF-10 `SelectCell` **동형(m4p4 "IF-05 OK ⟹ 적재 가능")을 보존**하고, 절대규칙
  #1(단일 쓰기 큐)·#3(TgtFloor 미클리어)·모든 PLC-쓰기 동작을 불변으로 유지(신규 PLC 쓰기 0).

- **Implementation Scope** (Generator가 할 일):
  1. **바인딩 지속 + 오더완료 release**: 콜백 무조건 ReleaseCell(`:428`) 제거 → 배정이 오더 완료까지 지속.
     SortedQty 증가 경로(IF-10/12 확정)에서 `SortedQty >= PlannedQty` 도달 시 그 오더의 cell_assignment(들)을
     release(+ `OrderStatus.COMPLETED` 전이). `SelectCell` ①은 배정 셀 재사용(여유 시), ②는 배정 없는 진짜
     신규 오더에만 빈 셀 신규 할당.
  2. **적재량 산출 스코프(복합 결함 해소 — 채택안)**: `SorterCellQty.LoadedQtyByCell`를 **현재 활성
     cell_assignment 기간**으로 스코프한다 — COMPLETED `sorter_command` 중 그 셀의 활성 배정 `AssignedAt`
     **이후**(`SorterCommand.CWrittenAt`/`CreatedAt >= assignment.AssignedAt`)만 합산. 재사용된 셀은 새 배정의
     AssignedAt가 이전 오더의 COMPLETED 행을 배제 → **새 오더가 0부터 카운트**. all-time 합 오염 제거.
     provider-neutral(DateTime 비교, provider별 SQL 없음). 경계 등호는 `>=`로 포함(EC). 이 스코프 산출은
     IF-05·IF-10·SorterFull **한 곳(SorterCellQty)에서 공유**해 byte-consistent 유지.
     - (대안 교차검증: 현재 배정 셀 오더의 `SortedQty`도 동일 값이어야 함 — 불변식 테스트로 상호 검증 가능.)
  3. **No-overflow 게이트 재구성(Q-a) + 동형**: `SorterCanAcceptBarcode`와 `SelectCell`을 **배정 유무 분기**로
     통일 — 오더 배정 보유 시 free 셀 폴백 금지(그 셀 여유만 판정; full이면 NG/null). 배정 없으면 빈 셀 존재로
     판정. 두 술어가 같은 DB 상태에서 항상 같은 결론(OK⟺비-null, 같은 셀). 동형 불변식 명시 테스트 추가.
  4. **ReleaseCell 재정의 + destination 스코프**: cellNo-only 전 소터 해제(묶음 C④/audit A-7) 제거 —
     오더/destId 스코프로 release(멀티소터 교차-release 회귀 차단). 이 fix가 정확히 이 메서드를 재작성하므로
     함께 처리(한계비용 낮음). `ICellSelector` 시그니처 조정.
  5. **OFFLINE/실패 경로 배정 수명 정합**: 이번 SelectCell이 새로 만든 배정(②)과 재사용한 기존 배정(①)을
     구분해, 물리 적재가 없었던 piece 때문에 진행 중 오더의 누적 바인딩(①)을 파기하지 않도록 처리(orphan 배정
     잔존 0). 오더 미완료 중 실패는 배정 유지(다음 piece가 같은 셀 누적).
  6. **버그-단언 테스트 정합 개정(은폐 삭제 금지)**:
     - `E2EGroupEF...E5_CellAssignment_ReleasedAfterHandshakeCallback`(`:137`) — "매 투입 후 활성 배정 0 수렴"
       단언 폐기 → **오더 미완료 중 배정 지속 / 오더 완료 시 release**로 재작성(PlannedQty>1 시드로 지속 입증).
     - `E6_NormalPath_NoCellLeak...`(`:172`) — "활성 배정 0=누수 없음" 재정의: leak=완료된 오더의 배정 잔존;
       정상=미완료 오더 배정 지속. released 수 == 완료 오더 수, orphan 0.
     - `F1_DifferentOrders_EachOwnCell...`(`:211`) — 주석/전제("ReleaseCell→free-cell 재할당") 정정, 통과 재확인.
     - `E2EGroupAB_NormalAndGateTests`(`:80`,`:113`) 콜백 release 의존 주석/단언 점검.
  7. **누적 수용 테스트 신규 추가**(아래 Verification Scenarios). 배치: `SorterCellFullnessTests` 인접 또는 신규
     `SorterCellAccumulationTests` + E2E(EF군). in-memory SQLite 통합(기존 패턴).

- **Evaluation Criteria** (가중):
  - (30%) 누적 정확성: N piece 같은 오더 → 같은 CellNo 누적(오더 완료까지 배정 지속).
  - (20%) No-overflow(Q-a): 배정 셀 Capacity 초과 시 IF-05 NG, 두 번째 셀 유출 0.
  - (20%) 재사용 정확성(Q-b/Q-c): 오더 완료 시 release → 다른 오더가 그 셀 재사용, **적재량 0부터 카운트**
    (이전 오더 COMPLETED 미오염).
  - (15%) 크로스-엔드포인트 동형: `SorterCanAcceptBarcode` ⟺ `SelectCell`(같은 DB 상태·같은 셀·같은 NG),
    released-cell false-free/초과적재 0(m4p4).
  - (10%) 버그-단언 테스트(E5/E6/F1) 정합 개정 — 정책 근거 명시.
  - (5%) 절대규칙: #1 단일 쓰기 큐·신규 PLC 쓰기 0, #3 불변, OFFLINE/실패 배정 수명 정합, provider-neutral, 회귀 0.

- **Completion Conditions** (Evaluator 통과 최소 조건):
  - `dotnet test backend/Wcs.sln` 풀 스위트 GREEN — **베이스라인 fresh 실행으로 확정**(직전 알려진 ≈305) + 신규
    누적/no-overflow/재사용 테스트.
  - IF-05↔SelectCell 동형 불변식이 **명시 테스트로 존재**(같은 DB 상태로 두 술어 구동 + 배정-분기 + Capacity 경계).
  - **적재량 배정-기간 스코프**가 테스트로 검증(재사용 셀 0부터 카운트 — 이전 오더 미오염).
  - E5/E6/F1(+AB 주석) 정합 개정 반영, 각 개정이 정책 근거를 주석/이름으로 명시.
  - **PLC-쓰기 동작 변경 0**(`ExecuteHandshakeAsync`·번들 큐 경로 불변 — diff·테스트로 확인).
  - provider-neutral. **마이그레이션은 스키마 변경 시에만**(로직 변경이라 불필요 예상; 시그니처/스코프는 스키마
    무변. 스키마 변경 발생 시 SqlServer·Sqlite **양 provider** 마이그레이션).
  - **Sim 전용 검증** — COM1/RTU/현장 DB 절대 금지(실 3DS PLC가 COM1). TCP Sim3ds만.
  - Evaluator는 fresh 증거(직접 재실행)로 판정. 부하 flake 귀속 시 반복 실행(1회 GREEN 신뢰 금지).

- **Parallel Modules**: N/A (single module — Wcs.Api 셀선택 로직 + 테스트, 파일 경계 분할 불가).

- **Evaluation Dimensions** (Evaluator expert pool — core dispatch 정확성이라 2차원):
  1. **functional** — 누적/no-overflow/오더완료-release/재사용(0부터 카운트)이 확정 정책대로 동작(시나리오 GREEN).
  2. **correctness-invariant** — IF-05↔SelectCell 동형, 배정-기간 스코프의 재사용 정확성(오염 0), no-overflow
     경계, 동시/순서 경합(같은 오더 동시 IF-10 soft-threshold — S-소터셀수량full 후속 인지), OFFLINE/실패 경로
     배정 수명(orphan·조기 release), 절대규칙 #1/#3·PLC-쓰기 불변. (APPROVED = 두 차원 모두 PASS.)

- **Detected Project Type**: Backend/API
  (신호: ASP.NET Core MVC Controllers[RcsController/OpsController], EF Core repositories, xUnit + Sim 통합.
   UI 없음. Wcs.Core 순수 판정 엔진. — 프로젝트 구조에서 확정.)

- **Verification Scenarios** (Backend/API — mandatory):

  - **Explicit list of endpoints touched by this sprint (method + path)**:
    1. `POST /api/v1/deposit-report` (IF-10) — 셀 선택·핸드셰이크·배정 수명·오더완료 release. **주 변경 지점.**
    2. `POST /api/v1/destination-query` (IF-05) — no-overflow 게이트 재구성(`SorterCanAcceptBarcode` 배정-분기).
       동형 검증 대상.
    (신규 엔드포인트 없음 — release-on-complete는 기존 IF-10/12 확정 경로 내부 훅. 운영자 비움 API 불요.)

  - **Happy path per endpoint (expected input → expected output shape)**:
    1. IF-10 누적: 같은 order/barcode(qty=1) 투입, Capacity=3·PlannedQty=3 시드. 1·2·3번째 piece 모두
       **동일 CellNo**에 COMPLETED, 3번째로 SortedQty==PlannedQty → 배정 release. 응답 `{result:"OK"}` 불변.
    2. IF-10 멀티오더: 서로 다른 barcode 2오더 → 각자 자기 셀에 누적, 교차오염 0, 둘 다 cell 1로 몰리지 않음.
    3. IF-05 동형: 위 상태에서 `SorterCanAcceptBarcode` OK ⟺ `SelectCell` 비-null, 같은 셀. 배정 보유 시 free
       셀 폴백 없음(no-overflow).
    4. 재사용: 오더 A 완료 → 그 셀 release → 다른 오더 B가 IF-05 OK·IF-10 → `SelectCell` ②가 그 셀 재사용,
       **B 적재량 0부터**(A의 COMPLETED 미합산 — AssignedAt 스코프).

  - **Relevant error cases per endpoint (Planner picks which apply)**:
    - IF-05/IF-10 — **No-overflow NG**: 오더 배정 셀이 full인데 오더 미완료(예: Capacity=2·오더 3개 필요) →
      3번째 piece IF-05 **NG/FULL**, 두 번째 셀 유출 0, `SelectCell`=null(IF-11 생략). 오더는 그 셀에 국한.
    - IF-10 — **OFFLINE(번들 null, `:318`)**: 재사용 배정(①)은 release 금지, 신규 배정(②)은 orphan 잔존 0,
      핸드셰이크 생략, `{result:"OK"}` 불변.
    - IF-10 — **핸드셰이크 실패**(RSeqMismatch/RFlagTimeout/CFlagTimeout): 오더 미완료 중 실패는 배정 유지
      (누적 바인딩 조기 파기 0). alarm 기록 경로 불변.
    - IF-05 — **소터 Paused**: 예외 없이 NG(FULL) 불변(확정4 — 이 fix 무관, 회귀 확인).
    - **500 금지** 경로 유지(미존재/비활성 chuteNo는 200+기록만) — 회귀 확인.

> Planner self-check — Detected project type: Backend/API. Required scenario slots: 3 (endpoints-touched, happy-path-per-endpoint, error-cases-per-endpoint). All slots filled: yes.
