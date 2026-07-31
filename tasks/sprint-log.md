# Sprint Log — S-AUDIT-C-DATA-INTEGRITY

(Generator가 `## IMPLEMENTATION COMPLETE` + 변경 요약 + 테스트 결과 기록)

## IMPLEMENTATION COMPLETE (Generator, 2026-07-31)

신규 마이그레이션 0·스키마/Entities 무변경·Wcs.Core zero-diff(#8)·PlcGateway 무접촉(#1). 커밋/푸시 미수행.

### 변경 파일·항목별 요약

**backend/src/Wcs.Api/Repositories/DbRepositories.cs** (판정 로직 코어)
- **① 원자 예약 차감** — `EfOrderRepository.QueryDestination` tx 블록(:178~294):
  - 추적 RMW(`item.ReservedQty += qty; SaveChanges`)를 **원자 조건부 UPDATE**로 교체
    (`_db.OrderItems.Where(i => i.Id==item.Id && i.ReservedQty+qty <= i.PlannedQty).ExecuteUpdate(SetProperty ReservedQty=ReservedQty+qty, UpdatedAt=now)` — :195~199).
  - `affected==0`(OVER) → tx 롤백 → `overReserved=true` 플래그 → tx 밖에서 `RecordDenied(...,"OVER",...)`
    호출(:289~293) 후 `("NG",null,"OVER",destApiType,null)` 반환. **DENIED 감사기록 계약 하드 보존**
    (piece(DENIED)+IF05_REQ/RES(OVER)). tx-밖 pre-OVER(:96)↔tx-안 차감 TOCTOU 창 폐쇄(원자 WHERE=최종 권위).
  - 원자 UPDATE를 tx 최초 write로 배치 → SQLite shared→write 승격 데드락 회피. 재시도 상수 0(#7).
- **② 전건 비활성화** — 두 경로 모두 `FirstOrDefault` 1행 → `Where(...).ToList()`+`foreach` 전건:
  - `QueryDestination` OK 경로 prevActives(:226~231), `RecordDenied` NG 경로 prevActives(:311~316).
  - 부분 유니크 백스톱(UQ_piece_pid_active_status) 유지·단일-활성 불변식 강제 미포함(계약 OQ-3).
- **⑤ SelectCell fail-loud** — `EfCellSelector`:
  - 생성자에 `IAlarmSink`+`ILogger<EfCellSelector>` 주입(:637 — DI 기등록·동일 스코프 WcsDbContext).
  - `SelectCell` ② 빈 셀 분기에서 매칭 RUNNING 오더 없음(`order is null`, :707~712) → 배정 생성 금지·
    셀 반환 거부(`null`)·`unmatched=true`. tx 종료 **후** WARN+`_alarm.Append("CELL_ORDER_UNMATCHED",
    WARN, null, ...)`(:737~743) — EfAlarmSink 자체 tx이므로 중첩 방지 위해 tx 밖 기록. ③ 빈 셀 없음(정상 FULL)은
    alarm 없이 null(구분).

**backend/src/Wcs.Api/Controllers/RcsController.cs**
- **② 부수 하드닝** — 활성 piece "1건" 읽는 무정렬 조회에 `OrderByDescending(p => p.Id)` 일관 적용:
  IF-10 capacity piece(:271~275)·TriggerSorterHandshake pieceRow(:329~332).
- **⑤ null-path 로그 중립화** — `cellNo==null` 경로(:305~312) 로그를 "빈 셀 없음=FULL 또는 미매칭 fail-loud"로
  중립 표기(진단 오도 방지). 미매칭 구체 사유·alarm은 SelectCell가 기록. IF-11 생략 로직 불변.

**backend/tests/Wcs.Tests/DataIntegrityAuditTests.cs** (신규 6 테스트)
- 직접 SQLite 하네스(`SeededDb`: named in-memory shared cache + `DbSeeder.Seed`) + 실 리포지토리.
- S1(①happy)·S1b(①동시)·S2×2(②)·S5a(⑤a)·S5b(⑤b 회귀).

### 원자 UPDATE 방식
EF Core `ExecuteUpdate`(추적 우회 DB-side `UPDATE`) — 기존 `Finalize`의 SortedQty 원자 증가(:988) 선례 재사용.
명시 tx(`BeginTransaction`) 참여. `WHERE reserved_qty+qty <= planned_qty`가 최종 권위 → 영향행 0=OVER.
SQL Server rowversion 패자=미처리 500 + SQLite lost-update 동시 해소(양 provider 무관 정확). 추적 `item`은
불변(ExecuteUpdate가 미갱신)이라 후속 SaveChanges에 미포함 → 이중 write 0.

### alarm 메커니즘
기존 `IAlarmSink`/`EfAlarmSink`(alarm 테이블 행 삽입, code·severity·pieceId·message) 재사용.
`EfCellSelector`에 `IAlarmSink` 생성자 주입(Program.cs DI 무변경 — `AddScoped<IAlarmSink>` 기등록·자동 해석).
alarm은 SelectCell 트랜잭션 종료 **후** 기록(EfAlarmSink가 자체 tx를 열므로 동일 WcsDbContext 중첩 tx 방지).
code=`CELL_ORDER_UNMATCHED`·severity=WARN·pieceId=null.

### 테스트 결과 (RED→GREEN 증거)
- **Baseline**: full 518 GREEN·0 fail(1m36s).
- **RED (수정 전 — 스캐폴딩 생성자만 넣어 컴파일)**: 신규 6 중 4 RED / 2 회귀 GREEN.
  - S2_If05DeactivatesAll: `Assert.Single Failure: collection contained 2 items`(단일 비활성화 후 잔존 2).
  - S2_RecordDenied: `Assert.Single Failure: contained 2 items`.
  - S1b_ConcurrentIf05: `Assert.Equal Expected 1 Actual 8`(SQLite lost-update — 동시 IF-05 전원 OK).
  - S5a_SelectCell_Unmatched: `Assert.Null Failure: Actual 1`(빈 셀 cellNo 조용히 반환).
  - GREEN: S1_happy(회귀 baseline)·S5b_matched(회귀 guard).
- **GREEN (수정 후)**: 신규 6/6 GREEN ×3 반복(동시 S1b flake 0).
- **Full 회귀**: full **524 GREEN**(=518 baseline + 6 신규 산술 일치)·0 fail ×**4 연속**(동시성 변경 게이트)·
  teardown hang 0(각 ~1m30s 자연 완료)·flake 0.
- **경고**: 빌드 경고 13 전부 선재 NU1903(SQLitePCLRaw 패키지 취약성)·신규 CS 경고 0.
- **마이그레이션**: 양 provider `has-pending-model-changes` = **No**(SQLite·SqlServer 모두 "No changes to the model").

### 실 SQL Server 동시성 실증 위임 (계약 §Evaluation Dimensions 2)
SQLite는 rowversion 미증가(lessons sqlserver-migration-prod-provider)라 **S-①a(실 SQL Server 동시 IF-05
rowversion 패자=미처리 500)** 시나리오를 재현 못 한다. 본 스프린트는 provider 무관 원자 갱신 경로(reserved_qty
이중 가산 부재·초과예약 0·미처리 500 0·DENIED 계약 보존)를 SQLite 실 HTTP 병렬(S1b, 8-way barrier)로 입증했다.
**실 SQL Server 병렬 동시 요청 실증(≥5회 무flake)은 Evaluator concurrency 차원이 수행**한다.
