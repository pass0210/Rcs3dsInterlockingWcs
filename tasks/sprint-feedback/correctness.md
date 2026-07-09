# CORRECTNESS-INVARIANT 평가 — S-CELL-ACCUM (3D 소터 셀 누적 바인딩 수정)

> CORRECTNESS-INVARIANT Evaluator · 2026-07-09 · dimension = 깊은 불변식(하드 게이트)만. functional은 별도.
> Ground truth = git diff(working tree vs develop, 미커밋) + 코드 직접 판독 + 독립 재실행. Generator 요약 불신.
> Handoff 마커: `tasks/sprint-log.md` L3077 `## IMPLEMENTATION COMPLETE — S-CELL-ACCUM`.
> 변경 파일(11): Wcs.Api {RcsController, DbRepositories, Repositories, DestinationStatusService, SorterCellQty}
>   + 테스트 4(E2EGroupAB, E2EGroupEF, E2ESeed, SorterCellFullnessTests) + tasks 2. **미커밋 working tree**.
> Sim/scratch-DB 전용 검증 — COM1/RTU/현장 DB 미접촉.

## CORRECTNESS: PASS

5개 불변식 게이트 전부 PASS. 근거는 아래(특히 게이트1 동형·게이트2 스코프 증명).

---

## FIX-ITER 재검증 (2026-07-09, code-review fixes 반영본) — CORRECTNESS: PASS

Generator가 code-review fix 5건 적용 후 **NEW diff 기준 fresh 재검증**(빌드·재실행). fix가 깨뜨릴 수 있는 지점 집중.

**#1 SortedQty ExecuteUpdate (원자 증가) — GATE 2 재확인 PASS.**
`EfSorterCommandJournal.Finalize`(DbRepositories.cs:874-909): `_db.OrderItems.Where(Id==itemId).ExecuteUpdate(SortedQty+=addQty, UpdatedAt=now)`.
- **명시 tx 내부 원자**: 메서드 전체가 `BeginTransaction`(:821)…`SaveChanges`(:910)…`Commit`(:911) 안. ExecuteUpdate는
  ambient 트랜잭션(현재 연결)에 참여하는 즉시 UPDATE — commit은 :911에서만, 예외 시 :915 Rollback이 UPDATE도 되돌림.
  **partial-commit 창 없음**. COMPLETED 전이·오더 COMPLETED·배정 release와 한 tx.
- **추적 우회 → fresh 재-read**: 증가 후 완료 판정은 `_db.OrderItems.Where(OrderId==orderId).Any(SortedQty<PlannedQty)`
  (스칼라 EXISTS SQL — SortedQty 비교가 **DB에서** 평가) + `Select(OrderId).First()`(스칼라). 엔티티 materialize 0 →
  **stale 추적본 영향 0**, 방금 ExecuteUpdate한 값(같은 tx·연결, read-your-writes)을 읽음.
- **멱등**: `wasAlreadyLoaded = piece.Status==LOADED`(전이 전 캡처) 가드 유지 → 재-Finalize/재시도 중복 가산 0.
- **완료 정확**: 전 항목 SortedQty>=PlannedQty에서만 fire(`!Any(<)`). 증거: E5(지속→완료 2단계)·E7(누적 3)·
  E8(no-overflow)·E9(재사용) 8/8 GREEN. ExecuteUpdate가 실 EF(SQLite) 경로에서 예외 없이 동작 확인.

**#2 LoadedQtyByCell minFrom 전역 하한 — GATE 2 스코프 재확인 PASS.**
SorterCellQty.cs:69-91: `minFrom = activeFrom.Values.Min()`(요청 셀들의 **가장 이른** 활성 배정 AssignedAt)을 DB
`CWrittenAt >= minFrom` 프리필터로 걸고, **셀별 정밀 하한 `>= from`은 in-memory 유지**(:88).
- **안전 superset**: minFrom ≤ 각 셀 from이므로 `CWrittenAt >= from ⟹ CWrittenAt >= minFrom`. 유효행 배제 0(정밀
  하한이 잡아야 할 행은 전부 프리필터 통과). 재사용 셀은 여전히 자기 from부터 0 카운트 — **오염 0**.
- **크로스-셀 오염 0**: fetch는 여러 셀 합쳐 오지만 in-memory `GroupBy(CellId)` 분리 후 각 셀 `activeFrom[key]`(자기 from)로
  정밀 필터 → 셀 간 혼입 없음.
- **provider-neutral**: `CWrittenAt >= minFrom`(상수 비교)·`cellIds.Contains`(List<long> IN) 둘 다 SQLite·SQL Server 번역.
  증거: EC-11(결정적 t0 — A 옛 적재 2 배제·B 0→1→2)·E9(실 Sim 재사용 0부터) GREEN.

**#3 AssignedCell 미사용 필드 제거 — PASS.** 레코드 struct `(CellId, CellNo, Capacity)` 3필드로 축소(:36).
AssignmentId/AssignedAt 참조 grep 0(코드/주석 외 사용처 없음), 생성부 3인자(:116) 정합. 빌드 0 오류로 dangling 없음 확인.

**#4 AssignedCellsForBarcode .Distinct() 재추가 — PASS.** 투영 `(CellId,CellNo,Capacity)` 후 `.Distinct()`(:114) —
같은 셀 활성 배정 2건(② 레이스 보험)·barcode 다중 항목 시에도 셀당 1행. no-overflow/동형 산출 견고화.

**#5 IN-list materialize — PASS.** `cellIds = activeFrom.Keys.ToList()`(List<long>) → `cellIds.Contains(sc.CellId)`
캐논 IN 번역(구 KeyCollection.Contains보다 견고). 실 EF 실행 확인(EC-11/E9 GREEN, 번역 예외 0).

**GATE 1/3/4 재확인**: RcsController·Repositories 인터페이스·DestinationStatusService diff는 이전 iter와 **바이트 동일**
(SelectCell ①=AssignedCellsForBarcode+FirstAssignedCellWithRoom, CanAcceptBarcode 동형 불변). PlcGateway·Data·
Migrations 변경 **여전히 0**. #1 단일 쓰기 큐·#3 TgtFloor 불변. release-on-complete·ReleaseEmptyAssignment 스코프 불변.

**FIX-ITER 완료 조건**: 빌드 0 오류(신규 CS 경고 0). 타깃 fresh 8/8(EC10/11/12/13·E5/E7/E8/E9). **풀 스위트 312 GREEN**
(단일스레드 결정적, 1m2s, exit 0, 실패 0·skip 0). Sim/in-memory SQLite 전용(COM1/RTU/현장 DB 미접촉). 고아 프로세스 0.

---

---

### GATE 1 — IF-05 ↔ SelectCell 동형(m4p4) : PASS

**단일 진실 공유 확인.** 두 경로가 `SorterCellQty`의 동일 헬퍼를 호출한다:
- IF-10 `EfCellSelector.SelectCell` (DbRepositories.cs:597-616): ①`AssignedCellsForBarcode`.Count>0 →
  `FirstAssignedCellWithRoom(...)?.CellNo` 반환(여유 없으면 **null, ②폴백 없음**). ②빈 셀(`occupiedCellIds`/`freeCell`).
- IF-05 `DestinationStatusService.SorterCanAcceptBarcode` (DestinationStatusService.cs:154) → `SorterCellQty.CanAcceptBarcode`:
  ①`assigned.Count>0 → FirstAssignedCellWithRoom is not null`(**HasFreeEnabledCell 폴백 없음**). ②`HasFreeEnabledCell`.

**오버플로 벡터 제거 확인.** 구조 `HasAssignedCellWithRoom OR HasFreeEnabledCell`(OR 폴백)이 삭제됨
(DestinationStatusService.cs diff — 두 private 헬퍼 통째 제거). 배정 셀 full + 빈 셀 존재 시 **양쪽 모두 거부**:
`SelectCell`은 ①에서 null 반환 후 ②로 안 감(:600-602), `CanAccept`는 ①에서 false 반환 후 HasFree 안 봄(SorterCellQty.cs:139-143).
②의 freeCell 술어(dest+Enabled+미점유)는 `HasFreeEnabledCell`과 논리 동치.

**명시 동형 테스트가 divergence를 잡는다.** EC-13(스위프): 같은 DB 상태로 두 술어 구동,
`Assert.Equal(expectAccept, picked is not null)` + `Assert.Equal(expectCell, picked)` — OK⟺비-null AND 같은 셀.
EC-10: 정확히 오버플로 divergence 지점(배정 full + 빈셀≥1) → NG⟺null 단언. 구 OR 코드였다면 EC-10은 OK/비-null로 실패.
**증거**: EC10/EC11/EC12/EC13 4/4 GREEN(fresh, 3s).

### GATE 2 — LOADED-QTY 배정-기간 스코프(재사용 셀 오염 방지) : PASS

`SorterCellQty.LoadedQtyByCell`(SorterCellQty.cs:54-87): (1) 셀별 활성 배정 `AssignedAt` 하한 `activeFrom = Max(AssignedAt)`,
(2) 그 셀들의 COMPLETED sorter_command 전량 fetch 후 **in-memory** `CWrittenAt >= from` 필터 + piece별 1건 dedup 합산.
- **UTC 일관성**: `AssignedAt = now`(DbRepositories.cs:639, now=DateTime.UtcNow:591)·`CWrittenAt = now`(DbRepositories.cs:800,
  now=UtcNow:793). 비교 피연산자 둘 다 **DB-read 값**(activeFrom는 SQL Max, CWrittenAt은 SQL SELECT) — 코드 UtcNow와
  DB값을 섞지 않음 → kind mismatch 없음. tick 기준 비교 일관.
- **경계 등호 `>=`가 정확**: 인과 순서상 신규 배정 생성(② AssignedAt) → 그 오더 command 기록(CWrittenAt)이므로
  현재 오더 command은 항상 `CWrittenAt >= AssignedAt`(포함해야 함). 이전 오더 command은 release→재배정 사이 실시간 경과로
  `CWrittenAt < 새 AssignedAt`(엄격히 작음) → 배제. off-by-one 없음. same-tick 충돌은 인과 지연으로 물리 불가.
- **단일 소스 공유**: IF-05(`CanAcceptBarcode`/`FirstAssignedCellWithRoom`), IF-10(`SelectCell`), SorterFull
  (`ComputeSorterFull` DestinationStatusService.cs:194) 모두 이 메서드 소비.
- **provider-neutral**: DateTime 비교가 `.ToList()` 이후 in-memory(SorterCellQty.cs:77). DB SQL은 status/dest/cell-IN 필터 +
  GroupBy/Max뿐. `activeFrom.Keys.Contains(sc.CellId)` IN-절이 실 EF provider(SQLite)에서 번역·실행 확인(EC11 GREEN,
  런타임 번역 예외 0). InExpression은 relational 공통 → SQL Server도 동일 번역.
- **증거**: EC-11(결정적 타임스탬프 t0 기준: A 옛 적재 qty=2 @t0+1 배제, B 재배정 @t0+20 → 0부터 1→2 카운트,
  Capacity=2 도달에서 NG). 구 all-time 합 코드였다면 첫 단언(B 여유=true)이 즉시 실패. E9(실 Sim 풀사이클: A 완료→
  셀 release→B 재사용, `SorterCanAcceptBarcode(B)=true` = A의 옛 2 미오염). 둘 다 GREEN.
- 교차검증: SortedQty(Finalize 가산) == 배정-기간 셀 적재량 동치 — E5/E7이 SortedQty·셀 적재 동시 단언.

### GATE 3 — RELEASE 타이밍(오더 완료 시에만) : PASS

- **매 투입 무조건 ReleaseCell 제거 확인**: RcsController.cs:428의 `scopedCellSelector.ReleaseCell(selectedCell)` 삭제
  (콜백 :376 `scopedCellSelector` 획득도 제거). diff로 확인.
- **완료 시에만 release**: `EfSorterCommandJournal.Finalize`(DbRepositories.cs:871-906) Success+`!wasAlreadyLoaded`+
  `OrderItemId` 있을 때만 `SortedQty += piece.Qty` → `!OrderItems.Any(SortedQty<PlannedQty)`면 COMPLETED 전이 +
  `CellAssignments.Where(OrderId==orderId && ReleasedAt==null)` release. **전부 단일 tx**(BeginTransaction:821 →
  SaveChanges:910 → Commit:911, 전이 원자성 확보).
- **바인딩 지속 확인**: E5(PlannedQty=2 — piece1 후 released 0·RUNNING·배정 지속, piece2 후 COMPLETED·release·두 piece 동일 셀),
  E7(PlannedQty=5 — 3 piece 동일 셀, 활성 배정 정확히 1). 조기 release 0.
- **완료 판정 정확**: 전 항목 SortedQty>=PlannedQty. `wasAlreadyLoaded` 가드로 재-Finalize 중복 가산 0.
  실패(MISMATCH/TIMEOUT/Offline)는 가산·release 없음 → 미완료 오더 배정 유지(E6: 미완료 3 piece → released 0·orphan 0).
- **order/destId 스코프(A-7 접힘)**: release가 `OrderId` 기준(orderId가 destination 함의) → CellNo-only 전 소터 해제 제거.
  `ReleaseEmptyAssignment`도 dest(chuteNo)+barcode 스코프(DbRepositories.cs:661-690). EC-12: 빈 orphan(O) 롤백·
  적재≥1(P) 유지. 교차 소터 해제 0.
- 설계상 미완료 영구 오더(예 PlannedQty>Capacity)는 셀 무기한 점유 — 이는 계약 Q-b(one-order-one-cell) 명시 선택,
  누수 아님.

### GATE 4 — PLC-쓰기 불변 / #1·#3 : PASS

- `git diff --stat` — Wcs.PlcGateway·Wcs.Data·Migrations.{Sqlite,SqlServer} **변경 0**. Wcs.Api 5파일 + 테스트뿐.
- 신규 PLC 쓰기 0. 핸드셰이크(`ExecuteHandshakeAsync`/번들 큐/`journal.Finalize` 호출부) 미변경. #1 단일 쓰기 큐 불변.
- #3 never-clear-TgtFloor: RcsController diff는 OFFLINE 분기(ReleaseEmptyAssignment)·:428 제거뿐 — TgtFloor 로직 미접촉.
- **마이그레이션 0**: 스키마 무변(기존 컬럼 SortedQty/Status/ClosedAt/AssignedAt/CWrittenAt 재사용). 시그니처 rename
  (`ReleaseCell`→`ReleaseEmptyAssignment`)은 스키마 무관.

### GATE 5 — 테스트 은폐/약화 없음 : PASS

- **E5** 개정: 구 "매 투입 후 활성 배정 0 수렴" 폐기 → PlannedQty=2 지속→완료 2단계(released 0→release, 동일 셀). **강화**.
- **E6** 개정: leak 재정의(완료 오더 배정 잔존) — 미완료 3 piece → 활성 1·released 0·단일 셀·RUNNING. 약화 아님.
- **F1**: 주석/전제 정정(배정 지속→① 재사용), body는 서로 다른 오더 각자 셀 시드 유지. AB: 주석 정정.
- **신규 진정성**: EC-10(구 OR 코드면 실패)·EC-11(구 all-time 합이면 실패)·EC-12·EC-13·E7·E8·E9 — 전부 신 정책 동작 반영,
  구 버그 코드에서 RED. 은폐 삭제 0.
- 관찰(비차단): E2EGroupAB A2 주석은 "핵심 단언=같은 셀 누적"이라 하나 실제 단언은 destination 총 적재량(=7)뿐(CellNo 미단언).
  회귀 은폐 아님(같은-셀은 E7/EC가 커버) — 문서 부정확. todo 등재 권고.

---

## 완료 조건 대조 (전부 충족)
- `dotnet test backend/Wcs.sln` **312 GREEN**(실패 0·skip 0) — **fresh 단일스레드 재실행**(1m9s, 결정적).
  · 최초 기본 병렬 실행은 298 통과·0 실패 후 `테스트 실행이 중단`(WcsTeardownGuard SocketException 스팸) — 기존
    testhost-teardown-channel-race / e2e-parallel-load flake(런 abort, **테스트 실패 아님**). 단일스레드 재실행 시 312 클린 종료로
    귀속 확정. baseline 305 + 신규 7 = 312 일치.
  · 포커스 재실행 fresh: EC10-13 4/4, E5/E6/E7/E8/E9/F1 6/6.
- IF-05↔SelectCell 동형 명시 테스트 존재(EC-13 스위프 + EC-10 오버플로 지점) ✓
- 적재량 배정-기간 스코프 테스트(EC-11 결정적 + E9 실 Sim) ✓
- E5/E6/F1(+AB) 정합 개정·정책 근거 명시 ✓
- PLC-쓰기 변경 0(diff·구조 확인) ✓ / provider-neutral ✓ / 마이그레이션 0 ✓ / Sim 전용 ✓
- 빌드 0 오류(신규 CS 경고 0; NU1903 SQLitePCLRaw 10건은 선재).

## Minor(비차단 — todo 권고)
1. E2EGroupAB A2 주석("핵심 단언=같은 셀")↔단언(총 적재량) 불일치 — 문서 정정 또는 CellNo 단언 추가.
2. 같은 오더 동시 IF-10(한 소터)이 ② 중복 배정 생성 가능(SelectCell tx가 read-then-create race). 물리 직렬 dispatch
   전제 + 계약상 후속(S-소터셀수량full) 인지 사항. 실운영 무해하나 향후 배정 생성 유니크 가드 검토.
3. SelectCell ②에서 RUNNING 오더 없을 때 배정 미생성인 채 freeCell.CellNo 반환(DbRepositories.cs:633-647) — 이번 스프린트
   미변경(선재). 비-RUNNING 오더 dispatch 시 누적 미추적 가능. 선재 동작이라 회귀 아님.
