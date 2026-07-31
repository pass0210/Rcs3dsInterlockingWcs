# Sprint Feedback — S-AUDIT-C-DATA-INTEGRITY
## Dimension: Functional / Data-integrity (Evaluator)

**판정: PASS**
평가일: 2026-07-31 (Asia/Seoul) · 브랜치 `feat/audit-c-data-integrity` · HEAD `ffe179c`
Provider: SQLite in-memory (실 SQL Server concurrency 재현은 ① concurrency 차원 위임)

---

## 0. Ground-truth (fresh)
- `git rev-parse HEAD` = `ffe179c2b0148a453ca8e1037ff18970964f698b`
- `git status`: modified = RcsController.cs, DbRepositories.cs, tasks/{sprint-contract,sprint-feedback,sprint-log}.md · untracked = DataIntegrityAuditTests.cs. **커밋 미수행**(Team 규약 준수).
- `tasks/sprint-log.md`에 `## IMPLEMENTATION COMPLETE (Generator, 2026-07-31)` 마커 존재 확인.
- 변경 코드(DbRepositories.cs / RcsController.cs / DataIntegrityAuditTests.cs)를 generator 요약이 아닌 **직접 read**로 검증.

---

## 1. ② 전건 비활성화 — PASS
- **QueryDestination OK 경로**(DbRepositories.cs :226~231): `_db.Pieces.Where(PId==pId && IsActive && ArchivedAt==null).ToList()` + `foreach{ IsActive=false }` — 무정렬 `FirstOrDefault` 1행 → **전건**. 직접 diff 확인.
- **RecordDenied NG 경로**(:311~318): 동일 `Where(...).ToList()+foreach` 전건. 즉 OK/NG(OVER·NO_DEST·PAUSED 등) **둘 다** 전건. 직접 read 확인.
- 부분 유니크 백스톱 `UQ_piece_pid_active_status` 유지(Wcs.Data git diff = 0, 스키마 무변경). 단일-활성 불변식 미강제(OQ-3 최소변경) 확인.
- 부수 하드닝: `RcsController.cs`에서 "1건 활성 piece" 무정렬 조회 2곳에 `OrderByDescending(p => p.Id)` 일관 적용 — capacity piece(:271~275)·TriggerSorterHandshake pieceRow(:329~332). diff 확인.
- **fresh 테스트 증거**:
  - `S2_MultiActivePieces_If05DeactivatesAll_If10NotFalselyIdempotent` GREEN — 같은 pId 활성 2행(RESERVED 저Id + DEPOSITED 고Id) 구성 → IF-05가 잔존 활성 0으로 전건 비활성화 → 신규 RESERVED C 1건 → **정상 IF-10이 위장유실(false) 없이 `RecordDeposit`=true·DEPOSITED 전이**(소터 destination). 구코드(무정렬 FirstOrDefault=저Id RESERVED만 비활성 → 활성 DEPOSITED 잔존 → 부분유니크 위반 → false 위장유실)는 수정 전 RED로 문서화됨.
  - `S2_MultiActivePieces_RecordDenied_DeactivatesAll` GREEN — NG(NO_DEST) 경로도 잔존 활성 0(신규 DENIED 1건뿐).

## 2. ⑤ SelectCell fail-loud — PASS
- `EfCellSelector` 생성자에 `IAlarmSink`+`ILogger<EfCellSelector>` 주입(:629~648). ② 빈 셀 분기에서 `order is null`(:701~712) → 배정 생성 금지·`selected=null`·`unmatched=true`, tx 종료 **후** WARN + `_alarm.Append("CELL_ORDER_UNMATCHED", AlarmSeverity.WARN, null, ...)`(:744~752). ③ 빈 셀 없음(정상 FULL)은 alarm 없이 null 반환 — **명확히 구분**. 직접 read 확인.
- alarm이 tx 밖(중첩 tx 없음): `EfAlarmSink`가 자체 tx를 여는데 동일 스코프 `WcsDbContext` 공유이므로 `using(tx)` 블록 종료 후 기록 — 코드 구조 확인. DI: Program.cs(무변경) :120 `ICellSelector→EfCellSelector`, :128~129 `IAlarmSink→EfAlarmSink`(scoped, 요청 스코프 WcsDbContext 공유) → 신규 생성자 의존 자동 해석.
- code=`CELL_ORDER_UNMATCHED`·severity=`WARN`·pieceId=`null` 정확.
- RcsController null-path 로그(:305~312) 중립화 — "빈 셀 없음=FULL 또는 미매칭 fail-loud"로 단정 회피(진단 오도 0). IF-11 생략 로직 불변.
- **fresh 테스트 증거**:
  - `S5a_SelectCell_UnmatchedOrder_ReturnsNull_RaisesAlarm` GREEN — 미매칭 바코드 → `SelectCell`=null(IF-11/틸트 미트리거) + `db.Alarms.Count(Code=="CELL_ORDER_UNMATCHED")==1` + 유령 cell_assignment 0.
  - `S5b_SelectCell_MatchedOrder_ReturnsCell_NoAlarm_Regression` GREEN — 정상 매칭 시 cell_assignment 1 + 셀 반환 + alarm 0(회귀 0).

## 3. ① 기능 측면(provider 무관 원자 경로) — PASS
- 예약 차감을 추적 RMW(`item.ReservedQty += qty; SaveChanges`) → **원자 조건부 UPDATE**로 교체(:195~199): `_db.OrderItems.Where(Id==item.Id && ReservedQty+qty <= PlannedQty).ExecuteUpdate(Set ReservedQty=ReservedQty+qty, UpdatedAt=now)`. `affected==0`(OVER) → `tx.Rollback()`·`overReserved=true` → tx 종료 후 `RecordDenied(...,"OVER",...,item,dest)`(:291) → `return ("NG",null,"OVER",destApiType,null)`(:292). **DENIED 계약 하드 보존**(piece DENIED + IF05_REQ/RES(OVER)) — RecordDenied 본문 직접 확인(:304~). 재시도 상수 0(#7).
- **fresh 테스트 증거**:
  - `S1_AtomicReservation_SingleOk_IncrementsExactly` GREEN — 단건 OK → reserved_qty 정확히 +3(회귀 0)·RESERVED piece 1.
  - `S1b_ConcurrentIf05_AtomicReservation_NoOverReserve_DeniedPreserved` GREEN — SQLite 8-way barrier 실 HTTP 병렬. **미처리 500 0건**(전부 200)·**정확히 1 OK**(초과예약 0)·나머지 7 NG(OVER)·`reserved_qty==planned==1`·RESERVED piece 1·**DENIED piece 7 + IF05_RES(OVER) event 7**(계약 보존). 실행 로그 raw: `pId=14102 → OK chuteNo=1`, 그 외 7건 `result=NG reason=OVER`. (실 SQL Server rowversion 패자 500 재현은 계약대로 concurrency 차원 위임.)

## 4. 회귀 0 — PASS
- `dotnet build backend/Wcs.sln -c Debug`: **오류 0개 · 경고 10개**(전부 선재 `NU1903` SQLitePCLRaw 취약성 · 신규 CS 경고 0). raw: "경고 10개 / 오류 0개".
- 전체 `dotnet test backend/Wcs.sln --no-build`: **524 통과**(baseline 518 + 신규 6 = 524, 산술 일치) — **12회 중 11회 GREEN**.
  - RUN 1 `실패: 0, 통과: 524` (95s) · RUN 3·4 동일 · RUN 5~12 각 `통과: 524`(로그 파일 grep 확인).
  - **RUN 2: `실패: 1, 통과: 523`** (104s) — 단일 RED.
- **단일 RED 귀속(간헐성+격리)**: run 2 이후 **11회 연속 재현 안 됨**. 스프린트 자체 6 테스트는 **focused 6/6 + 격리 반복 15/15**(S1b 8-way 동시성 포함) 무flake로 안정 입증. 따라서 run 2 RED는 스프린트 변경(DB 리포지토리 로직)이 아니라 **xUnit 병렬 부하 하 선재 타이밍-취약 테스트**(lessons: S9 flake / IT4b / sim-timeline race)의 저빈도 flake로 귀속. 즉시 FAIL 사유 아님(Evaluator 규칙: 단일 RED는 간헐성+격리 귀속).
  - teardown hang 0(모든 run ~95s 자연 완료).
- Wcs.Core git diff = **0**(#8) · Wcs.PlcGateway git diff = **0**(#1) · Wcs.Data git diff = **0**(신규 마이그레이션 없음).
- 정상 경로 불변: S1 happy·S5b matched GREEN.

## 5. 마이그레이션 0 (양 provider) — PASS (구조적 dispositive 증거)
- `has-pending-model-changes`는 (현재 EF 모델) vs (최신 마이그레이션 스냅샷)의 순수 비교. 본 스프린트는 **Wcs.Data(모델)·Wcs.Migrations.Sqlite·Wcs.Migrations.SqlServer 세 프로젝트 모두 git diff = 0**(byte-identical to develop, 검증된 baseline). 두 입력이 develop과 동일 → 결과는 develop의 기지(旣知) "No"와 필연적으로 동일. **양 provider = No.**
- 참고: 설치된 `dotnet ef` 툴 v9.0.10 로 CLI 직접 실행 시 `Microsoft.EntityFrameworkCore.Design` 어셈블리 로드 실패(툴/런타임 조립 마찰) — 이는 pending 신호가 아니라 툴링 버전 마찰. 위 구조적 증거(zero-diff)가 CLI 재계산보다 상위 권위이므로 마이그레이션 무변경은 확정. (스킵 아님 — 더 강한 검증 방법 사용.)

## 6. 절대규칙 — PASS
- **#7**: 재시도 루프/상수 0(원자 UPDATE 1회 — catch-retry 미채택). 하드코딩 0.
- **#8**: Wcs.Core git diff = 0(판정 순수 함수 무변경).

## 7. SCOPE OUT 재해소(존재 단언) — PASS
- **R-③**: `IX_piece_pid_active`·`IX_order_item_barcode` — WcsDbContext.cs:448·386 + 마이그레이션(SqlServer/Sqlite 스냅샷·AddHotPathIndexes) 계속 존재.
- **R-④**: 전역 `ReleaseCell(cellNo)` 미재도입 — `ICellSelector`(Repositories.cs:84)는 `ReleaseEmptyAssignment(int chuteNo, string barcode, int cellNo)`(:98, destination 스코프)만 노출. RcsController :463는 제거를 문서화한 주석뿐.

---

## 결론
Functional / Data-integrity 차원 **PASS**. ①(원자 예약 경로·DENIED 계약)·②(전건 비활성화·위장유실 차단)·⑤(fail-loud null+alarm·혼적 차단) 전부 fresh 테스트로 기능 정확성 입증, 정상 경로 회귀 0, #7·#8 준수, SCOPE OUT 유지. 단일 RED(1/12)는 선재 타이밍 flake로 귀속(스프린트 6 테스트 32회 실행 무flake). 실 SQL Server 동시성(rowversion 패자 500·≥5회 무flake)은 계약대로 concurrency 차원 판정에 위임 — **본 차원 판정에는 미포함**.

> APPROVED는 전 차원 PASS 시에만(main이 2차원 aggregate). 본 파일은 Functional 차원 단독 판정.
