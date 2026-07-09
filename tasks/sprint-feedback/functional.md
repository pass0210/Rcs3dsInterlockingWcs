# S-CELL-ACCUM — FUNCTIONAL Evaluation

## FUNCTIONAL: PASS (fix-iter 재검증 — code-review #1/#2/#3/#4/#5/#7 반영 후 최신 verdict)

> FUNCTIONAL Evaluator · 2026-07-09 · branch `fix/cell-accumulation-binding` (base develop `c813c3a`)
> 판정은 전부 fresh 재실행 증거. Generator 요약을 신뢰하지 않고 git ground-truth + 직접 테스트 재실행으로 확인.
> correctness-invariant 차원(동형·오염 0·경합·절대규칙)은 별도 evaluator(correctness.md) 소관 — 여기서 중복 안 함.

---

## ★ FIX-ITER 재검증 (code-review 픽스 적용 후 — 최신, delta-focused + no-regression)

### 적용된 픽스 (git ground-truth diff로 확인 — Generator 주장 아님)
- **#2 (LoadedQtyByCell bounded fetch)**: DB 쿼리에 전역 하한 `minFrom = activeFrom.Values.Min()` 추가
  (`sc.CWrittenAt >= minFrom` + `cellIds.Contains`) 로 이력 전량 fetch 방지. 셀별 정밀 하한(`x.CWrittenAt >= from`,
  piece 중복 제거)은 in-memory 유지. **의미 불변**: 모든 셀의 `from >= minFrom`이므로 DB 사전필터가 어떤 셀에도
  필요한 행을 누락시키지 않음 → 배정-기간 스코프 결과 동일. EC-11 GREEN로 실증.
- **#1 (Finalize SortedQty ExecuteUpdate)**: RMW+RowVersion 충돌이 Finalize 전체를 롤백시키던 증폭 제거 →
  `_db.OrderItems.Where(Id==itemId).ExecuteUpdate(SortedQty + addQty, UpdatedAt=now)` 원자 증가(명시 tx 참여).
  ExecuteUpdate가 추적 우회이므로 완료 판정용 **재-read**(`!OrderItems.Where(OrderId==orderId).Any(SortedQty<PlannedQty)`).
  `wasAlreadyLoaded` 멱등 가드 보존 → 재-Finalize 중복 가산 0. 완료는 정확히 SortedQty==PlannedQty(>=)에서 전이.
- **#3/#4/#5/#7 cleanup**: 변경 표면 여전히 9파일(Wcs.Api 5 production + tests 4). PlcGateway/Core diff 빈 출력 유지.

### 재실행 결과 — FULL GREEN (single-threaded)
- 첫 시도(기본 parallel)는 **행(hang)** — testhost CPU 8s간 +0.1s(idle). 코디네이터가 경고한 parallel
  teardown-socket flake 재발로 귀속(fix-induced deadlock 아님). kill 후 오펀 정리.
- **single-threaded 재실행**(scratchpad `serial.runsettings`: ParallelizeTestCollections=false·MaxParallelThreads=1;
  repo 무수정): trx Counters `total=312 passed=312 failed=0 error=0 timeout=0 aborted=0 notExecuted=0`.
  콘솔 `통과!  실패: 0, 통과: 312, 건너뜀: 0, 전체: 312, 기간: 1 m 7 s`. → **312 GREEN·회귀 0·skip 0**.
- single-threaded 클린 통과 = 행의 원인이 병렬 teardown 경합이지 이번 픽스가 아님을 확정.

### 수용 행위 STILL HOLD (trx testName별 outcome 직접 확인 — 전부 Passed)
- (a) 같은 오더→같은 셀: `E5_..._PersistsUntilOrderComplete_ThenReleased`·`E6_IncompleteOrder_AssignmentPersists_NoPrematureRelease`·`E7_SameOrder_NPieces_AccumulateSameCell_UntilComplete`
- (b) Capacity+1→IF-05 NG·유출 0: `E8_AssignedCellCapacityExceeded_If05Ng_NoOverflowToSecondCell`·`EC10_AssignedCellFull_FreeCellsExist_NoOverflow_Isomorphic`
- (c) 완료→release→재사용·0부터: `E9_OrderComplete_CellReused_ByOtherOrder_LoadedFromZero`·`EC11_ReusedCell_LoadedScopedToCurrentAssignment_NotAllTime`
- 동형/orphan: `EC13_If05_SelectCell_Isomorphism_Sweep`·`EC12_ReleaseEmptyAssignment_RollsBackEmptyOrphan_KeepsLoaded`·`F1_DifferentOrders_EachOwnCell_AllCompleted_DistinctCells`
- **EC-11 (배정-기간 스코프)**: bounded-fetch(#2) 후에도 Passed — 재사용 셀 A의 옛 적재(2) 미오염·B 0부터(t0/t0+20 명시 타임스탬프).
- **SortedQty 완료 회계(#1)**: E5(PlannedQty=2, 2 piece→정확히 완료·release)·E9(완료→재사용) Passed — ExecuteUpdate 후 완료가 정확히 ==PlannedQty에서 전이·중복 가산 0.

### 위생 (fix-iter 재확인)
- 빌드 0 오류. `: warning CS` grep 0건 → **신규 CS 경고 0**. 경고 10 전부 선재 NU1903(SQLitePCLRaw).
- PlcGateway/Core diff 빈 출력, frontend 무접촉, 마이그레이션 0(스키마 무변·provider-neutral).
- 재실행 후 `Wcs.Sim3ds.exe`/`Wcs.Api.exe`/testhost **오펀 0**(정리 확인). Sim TCP + in-memory SQLite 전용 — COM1/RTU/현장 DB 미접촉.

**FIX-ITER 결론: FUNCTIONAL PASS** — 312 GREEN, 픽스 의미 보존(EC-11·SortedQty 회계 GREEN), 회귀 0, 무접촉 게이트 불변.

---

## (초기 검증 원문 — S-CELL-ACCUM 최초 handoff 기준, 보존)

### 1. 핸드오프 마커 — OK
`tasks/sprint-log.md:3078` `## IMPLEMENTATION COMPLETE — S-CELL-ACCUM` 존재 확인.

### 2. 전체 테스트 — FULL GREEN (fresh 재실행)
`dotnet test backend/Wcs.sln` 직접 재실행. trx Counters(fresh):
```
total="312" executed="312" passed="312" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" notExecuted="0" warning="0"
콘솔: 통과!  - 실패: 0, 통과: 312, 건너뜀: 0, 전체: 312, 기간: 20 s - Wcs.Tests.dll (net10.0)
```
- Generator 주장(305 baseline + 7 신규 = 312) **정확 일치**. 회귀 0 · skip 0 · error 0.
- 빌드 0 오류. 경고 10건 **전부 선재 NU1903**(SQLitePCLRaw known vuln) — 신규 CS 경고 0(`: warning CS` grep 0건).
- 참고: 첫 시도가 느렸던 원인은 IF-08 teardown 로그 홍수(`DestinationStatusPusher.Observe`→`ComputeSorter:259` ObjectDisposed)가
  `tee` 파이프에 동기 기록돼 생긴 I/O 병목뿐 — 이 스프린트 diff 무관·선재 teardown 패턴. 파일 직접 redirect 재실행 시 20s 정상 종료.

### 3. ACCUMULATION 수용 행위 — 확인(테스트 실행 GREEN + 코드 판독 일치)
| 행위 | 테스트(전부 Passed) | 명시 Capacity 시드 |
|---|---|---|
| (a) N piece 같은 오더 → **같은 셀 누적**(cell 1 흩어짐 아님) | `E7_SameOrder_NPieces_AccumulateSameCell_UntilComplete`(실 Sim 3 piece·활성 배정 1·SortedQty=3·동일 CellNo Single) · `E5_...PersistsUntilOrderComplete`(2 piece 동일 셀) · `E6_IncompleteOrder_AssignmentPersists`(3 piece Single(cells)) | 경계는 E8이 커버 |
| (a)+(b) piece 1·2 같은 셀 → **(Capacity+1)th → IF-05 NG**, 두 번째 셀 유출 0 | `E8_AssignedCellCapacityExceeded_If05Ng_NoOverflowToSecondCell`(`SetAllCapacities(2)`; 3rd `If05Result=="NG"`·`ChuteNo==null`; 그 오더 적재 셀 Single·활성 배정 1; **freeBefore≥1 단언 후에도 유출 0**) | **`SetAllCapacities(destId,2)`** |
| (b) 빈 셀이 남아 있어도 오버플로 0(동형) | `EC10_AssignedCellFull_FreeCellsExist_NoOverflow_Isomorphic`(`SetAllCapacities(2)`·`FreeCellCount≥1` 단언·`SorterCanAcceptBarcode==false`·`SelectCell==null`·IF-05 200+NG; 대조: 배정 없는 새 오더는 빈 셀로 OK) | **capacity=2** |
| (c) 오더 완료(SortedQty==PlannedQty) → 셀 release → 다른 오더 재사용·**적재 0부터** | `E9_OrderComplete_CellReused_ByOtherOrder_LoadedFromZero`(실 Sim; A 완료→활성 배정 0→B가 A의 옛 셀 재사용·`SorterCanAcceptBarcode(B)=true`, A 옛 COMPLETED 2 미오염) · `EC11_ReusedCell_LoadedScopedToCurrentAssignment_NotAllTime`(명시 타임스탬프 t0/t0+20으로 배정-기간 스코프 결정적 입증) | E9/EC11 **`SetAllCapacities(2)`** |

- 수용 행위 판정: (a) 같은 셀 누적 · (b) Capacity+1 → NG·두 번째 셀 유출 0(빈 셀 존재해도) · (c) 완료→release→재사용·0부터 — 전부 실행 GREEN + 코드 판독 일치.
- Capacity>0 명시 시드 요건: E8/E9/EC10/EC11/EC12/EC13 전부 `SetAllCapacities(…, N>0)` 명시. (E7만 기본 시드 `Capacity=null`(무제한)이나, "같은-셀 누적 바인딩" 검증엔 무해 — 경계는 E8이 명시 capacity=2로 커버하므로 갭 아님.)

### 4. 버그-단언 테스트(E5/E6/F1/AB) 정합 개정 — 은폐 삭제/약화 0 (diff 직접 검사)
- **E5**: `..._ReleasedAfterHandshakeCallback` → `..._PersistsUntilOrderComplete_ThenReleased` **개명·재작성**. 구 단언("매 투입 후 활성 배정 0 수렴") 폐기 → PlannedQty=2로 (미완료: 활성 1·released 0·RUNNING) → (완료: 활성 0·released≥1·COMPLETED)·두 piece 동일 셀. **강화**(삭제/약화 아님).
- **E6**: `..._NoCellLeak_FindingForCallbackThrow` → `..._IncompleteOrder_AssignmentPersists_NoPrematureRelease`. leak 재정의(=완료 오더 배정 잔존). 미완료 오더 3 piece → 활성 배정 정확히 1·released 0·orphan 0·Single(cells)·SortedQty=3. **강화**.
- **F1**: 본문 유지·통과, 주석/전제("콜백 ReleaseCell→빈 셀 재할당" → "배정 지속→① 재사용 동일 셀") 정정.
- **AB(A1/A2)**: 주석 정정(배정 지속→동일 셀 누적). 단언은 `sorter_command.cell_id` ground-truth 유지.
- 각 개정에 정책 근거를 주석/이름(`[S-CELL-ACCUM 정합 개정]`)으로 명시 — 근거 명시 요건 충족.

### 5. 무접촉 / 안전 게이트 — 확인
- **PLC-쓰기 동작 변경 0**: `git diff develop -- backend/src/Wcs.PlcGateway/ backend/src/Wcs.Core/` **빈 출력**. RcsController diff는 콜백 무조건 `ReleaseCell` 제거 + OFFLINE 경로 `ReleaseEmptyAssignment` 치환뿐 — `ExecuteHandshakeAsync`/번들 큐/SetTgtFloor 무접촉. 절대규칙 #1(단일 쓰기 큐)·#3(TgtFloor 미클리어) 불변.
- **frontend 무접촉**: `git diff --stat develop -- frontend/` 빈 출력.
- **마이그레이션 0건**: `git status` Migrations/ModelSnapshot 파일 0(기존 컬럼 SortedQty/Status/ClosedAt/AssignedAt/CWrittenAt 재사용, 스키마 무변). provider-neutral(LoadedQtyByCell DateTime 비교 in-memory — provider별 SQL 0).
- **변경 표면**: 정확히 9파일(Wcs.Api 5 production + tests 4) — 계약이 명시한 손대는 지점과 일치.
- **Sim 전용·현장 무접촉**: E2E/Push 팩토리 `Database:Provider=Sqlite`(in-memory named anchor) + `Sorters:*:Transport=Tcp`(`E2EInfrastructure.cs:163,181,215`). COM1/RTU/SqlServer 현장 DB **미접촉**.
- **오펀 0**: 실행 후 `Wcs.Sim3ds.exe`/`Wcs.Api.exe` 0(E2E in-process Sim). 잔존 `testhost.exe`(선재 teardown-race 산물)만 있어 정리 완료.

### 6. Stale-resend guard
평가는 워크트리 아님·live 작업트리(`fix/cell-accumulation-binding`, HEAD `c813c3a`)의 미커밋 변경분에 직접 수행. 평가 전후 `git status --porcelain` 동일(9파일 M) — stale base/누락 없음.

---

## 결론: FUNCTIONAL PASS
312/312 GREEN(0 fail·0 skip·0 error), 누적/no-overflow/오더완료-release/재사용(0부터)이 확정 정책대로 동작, 버그-단언 테스트(E5/E6/F1/AB) 정합 개정(은폐 삭제 0), PLC-쓰기·frontend·스키마 무접촉, Sim TCP+SQLite 전용. 기능 차원 통과.
(APPROVED = functional AND correctness-invariant — correctness 차원은 correctness.md 별도 evaluator.)
