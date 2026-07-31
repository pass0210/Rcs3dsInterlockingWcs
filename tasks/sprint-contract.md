[Sprint Contract]
Sprint: S-AUDIT-C-DATA-INTEGRITY (감사 2026-07-01 묶음 C — 데이터 정합, 운영 투입 전)
Base: 최신 develop = 11e76b8 (2601·사이클타임 포함). feature 브랜치에서 작업(develop 직접 커밋 0).

────────────────────────────────────────────────────────
## ★ 재triage 결과 (현재 코드 직접 확인 — 필수 의무 이행)
────────────────────────────────────────────────────────
원 5항목 중 2건(③④)은 한 달간 후속 스프린트(S-HARDENING-1)에서 이미 해소. 남은 3건(①②⑤)만 Implementation Scope.

| 원항목 | 판정 | 증거(file:line) |
|--------|------|------------------|
| ① 동시 IF-05 rowversion 충돌 500 | **유효** | DbRepositories.cs:199 `ReservedQty += qty`(추적 RMW)→:232 SaveChanges→:255-259 catch{Rollback;throw} 미가공 전파. OVER 검사(:96) tx 밖 stale-read. RcsController 호출부 try/catch·전역 핸들러 0 → SQL Server rowversion 패자=미처리 500 + DENIED 감사기록까지 롤백. SQLite=lost update. |
| ② piece 다중 활성 비활성화 FirstOrDefault 1행 | **유효** | DbRepositories.cs:203(OK)+:276(NG) 둘 다 정렬없는 `FirstOrDefault(PId==pId && IsActive && ArchivedAt==null)` 1행만. 잔존 활성 시 IF-10 부분유니크 위반→RecordDeposit false→RcsController:249-253 '멱등 OK' return→핸드셰이크 미도달=투입 유실+IF-11 미트리거. |
| ③ piece(PId,IsActive) 인덱스 | **해소** | WcsDbContext.cs:447-448 `IX_piece_pid_active` 비필터 복합. 마이그레이션 20260713012244_AddHotPathIndexes. → SCOPE OUT. |
| ③ order_item(Barcode) 인덱스 | **해소** | WcsDbContext.cs:385-386 `IX_order_item_barcode`. 동 마이그레이션. → SCOPE OUT. |
| ④ ReleaseCell destination 스코프 | **해소** | 전역 `ReleaseCell(cellNo)` 삭제. ICellSelector=`SelectCell`+`ReleaseEmptyAssignment(chuteNo,barcode,cellNo)`(destination 스코프)+Finalize 오더 스코프 release(:939-944). → SCOPE OUT. |
| ⑤ SelectCell 미매칭 시 배정없이 셀 반환 | **유효** | DbRepositories.cs:648-670: order==null이어도 배정행 없이 `freeCell.CellNo` 무조건 반환·WARN/alarm 0. REPORTED_DIRECT·바코드 오타 시 물리 틸트되는데 cell_assignment 부재→빈 셀→재배정→혼적. |

**결론: 남은 유효 = ①②⑤ (전부 코드 전용·스키마 변경 0·신규 마이그레이션 불필요). no-op 아님.**

────────────────────────────────────────────────────────
## Goal
────────────────────────────────────────────────────────
운영 투입 전 데이터 정합 결함 3건 제거:
(①) 다중 AGV 같은 barcode 동시 IF-05의 미처리 500·SQLite lost-update를 **DENIED 기록 계약 보존**하며 원자 갱신(또는 재시도)으로 해소.
(②) 같은 pId 잔존 다중 활성 piece가 정상 IF-10을 '멱등 OK'로 위장 유실+IF-11 미트리거하는 것을, IF-05 두 경로 이전 활성 piece **전건 비활성화**로 차단.
(⑤) SelectCell이 매칭 오더 없이 셀을 조용히 반환해 물리 적재가 DB상 빈 셀로 남는 혼적 벡터를 **fail-loud**로 전환.
절대규칙: #7(재시도 횟수 등 튜너블 appsettings·하드코딩 금지)·#8(Wcs.Core 순수 무변경). ③④ 해소로 #1·멀티소터 시그니처 파급 없음.

────────────────────────────────────────────────────────
## Implementation Scope (Generator가 할 일)
────────────────────────────────────────────────────────
### 항목 ① — 동시 IF-05 예약 차감 원자화 (DbRepositories.cs EfOrderRepository.QueryDestination)
- 예약 차감을 RowVersion RMW→**원자 조건부 갱신**(의미: `reserved_qty += qty WHERE reserved_qty+qty <= planned_qty`, 영향행 0=OVER). SQL Server 미처리 500·SQLite lost-update 동시 해소. 선례: Finalize의 `ExecuteUpdate` 원자 증가(:916-920) 패턴 재사용(명시 tx 내).
- **DENIED 기록 계약 보존(하드 요구)**: 원자 갱신 OVER 실패(또는 재시도 소진) 시 반드시 `RecordDenied(...,"OVER",...)`로 piece(DENIED)+piece_event 남김. 어느 동시 요청도 감사기록 없이 500 소실 금지.
- tx 밖 pre-OVER(:96)↔tx 안 차감 TOCTOU 창 닫음(원자 갱신 최종 권위). 정상 단건·비경합 응답 형상·부수효과 전부 불변(회귀 0).

### 항목 ② — 이전 활성 piece 전건 비활성화 (동 DbRepositories.cs)
- QueryDestination OK(:203)·RecordDenied NG(:276) 경로의 이전 활성 piece 비활성화를 `FirstOrDefault` 1행→**전건**(활성·미아카이브 전체). 부분 유니크 백스톱(UQ_piece_pid_active_status·MAJOR-1 멱등)은 **유지**.
- (부수 하드닝) 활성 piece "1건" 읽는 잔여 무정렬 조회(RcsController:321 pieceRow, :270 IF-10 capacity)에 결정적 정렬(`OrderByDescending(Id)`) 일관 적용.

### 항목 ⑤ — SelectCell 미매칭 fail-loud (DbRepositories.cs EfCellSelector.SelectCell + RcsController.cs TriggerSorterHandshake)
- SelectCell ② 빈 셀 분기에서 매칭 오더 없으면 조용히 셀 반환 금지 → fail-loud(OQ-2 최종 확정): 권고=`null` 반환(핸드셰이크/틸트 미트리거)+WARN+alarm 1건(`CELL_ORDER_UNMATCHED`). 대안=orphan 표식 행 남겨 점유 추적(틸트 허용).
- 호출부 RcsController:298-304는 이미 `cellNo==null`이면 IF-11 생략 — 미매칭 null도 합류하되 로그/alarm이 "미매칭 fail-loud"임을 구분(진단 오도 금지).

### 스키마
- **신규 마이그레이션 없음**(①②⑤ 코드 전용). WcsDbContext·Entities 무변경. 스키마 변경 필요 판단 시 즉시 Evaluator에 스코프 확장 요청(임의 마이그레이션 금지).

────────────────────────────────────────────────────────
## SCOPE OUT (해소 확인 — 착수 금지)
────────────────────────────────────────────────────────
- ③ piece(PId,IsActive)/order_item(Barcode) 인덱스: 해소(WcsDbContext:447-448·385-386, 마이그레이션 20260713012244).
- ④ ReleaseCell destination 스코프: 해소(전역 ReleaseCell 삭제·ReleaseEmptyAssignment+Finalize 오더 스코프). 멀티소터 2대째 안전.
- (참고) A-2/A-9 오더 완료 수명주기·sorted_qty 가산 이미 구현(Finalize :916-945). 묶음 C 원항목 아님.

────────────────────────────────────────────────────────
## Parallel Modules: N/A (①②⑤ 전부 DbRepositories.cs 공유 파일 편집 — 단일 Generator)
────────────────────────────────────────────────────────

## Evaluation Dimensions (Evaluator expert pool — APPROVED는 전 차원 PASS)
1. **Functional / Data-integrity** — ②전건 비활성화·⑤fail-loud 기능 정확성 + 정상 경로 회귀 0(TDD SQLite in-memory).
2. **Concurrency & Provider-fidelity** — ①동시 IF-05는 **실 SQL Server provider**에서만 재현(SQLite rowversion 미증가 — lessons 실 prod provider 검증). 실 SQL Server 병렬 동시 요청 실증 + 단독 다회 실행(flake 배제). fake+구조단언 불충분.

────────────────────────────────────────────────────────
## Detected Project Type: Backend/API
(ASP.NET Core MVC Controllers·EF Core 리포지토리·xUnit. 프론트 표면 무변경 — 순수 백엔드.)
────────────────────────────────────────────────────────

## Verification Scenarios (Backend/API)
### [Slot 1] 건드리는 엔드포인트
- POST /api/v1/destination-query (IF-05) — ①예약차감 원자화·②전건 비활성화.
- POST /api/v1/deposit-report (IF-10) — ②멱등 오판 방지 하류·⑤SelectCell fail-loud.
(IF-09 arrival-report 무변경.)
### [Slot 2] Happy path
- IF-05 단건 매칭: 200 {result:"OK",chuteNo}+piece RESERVED 1+IF05_REQ/RES+(소터)floor enqueue. 원자화 후 reserved_qty 정확히 +qty.
- IF-10 정상 투입: 활성 RESERVED→200+piece DEPOSITED+(소터)SelectCell 매칭→IF-11 트리거.
### [Slot 3] 에러/경계 (적용 케이스만)
- **[S-①a] 동시 IF-05(실 SQL Server)**: planned_qty 2건 동시수용 불가 SKU 2요청 병렬 → 미처리 500 **0건**(양 200), 한쪽 OK·다른쪽 NG(OVER) 또는 reserved_qty ≤ planned_qty(초과예약 0). **NG도 piece(DENIED)+IF05_REQ/RES(OVER) 존재**(DENIED 계약 보존). 실 SQL Server+단독 ≥5회 무flake.
- **[S-①b] SQLite lost-update 부재**: 원자 갱신으로 reserved_qty 이중 가산 없음(provider 무관 최종 권위).
- **[S-②] 다중 활성 전건 비활성화**: 같은 pId 활성 2행 구성→신규 IF-05가 전건 비활성화(잔존 0)→정상 IF-10이 위장유실 없이 DEPOSITED+(소터)IF-11 트리거. (수정 전=1행만→부분유니크 위반→false→유실 RED 정상.)
- **[S-⑤a] SelectCell 미매칭 fail-loud**: IF-05 선행 없이(또는 오타) IF-10 빈셀 분기 RUNNING 오더 매칭 실패→셀 조용히 반환 안함(null)+WARN+alarm 1건, IF-11 미트리거, 유령 점유 0. (수정 전=CellNo 반환+무로그 RED 정상.)
- **[S-⑤b] SelectCell 정상 매칭 회귀 0**: 매칭 오더 존재 시 기존대로 cell_assignment 생성+셀 반환(불변).
### [Regression-guard — SCOPE OUT 재해소 확인(존재 단언만)]
- **[R-③]** IX_piece_pid_active·IX_order_item_barcode 모델/마이그레이션 계속 존재.
- **[R-④]** ICellSelector에 전역 ReleaseCell(cellNo) 미재도입(멀티소터 교차 해제 0).

## Completion Conditions
- ①②⑤ 각 RED-우선 테스트 수정 전 RED→수정 후 GREEN.
- [S-①a]는 **실 SQL Server provider** 실증(SQLite 대체 불가)+단독 ≥5회 무flake.
- 전체 `dotnet test backend/Wcs.sln` GREEN(동시성 변경이므로 ≥4회 연속)+teardown hang 0+신규 경고 0.
- 정상 경로 회귀 0. 신규 마이그레이션 없음(양 provider has-pending-model-changes "No").

## Evaluation Criteria (가중치)
- 정확성(①②⑤ 결함 제거+DENIED/멱등/혼적 계약 보존) 45% · 동시성·provider 충실도(①실 SQL Server·flake 배제) 25% · 회귀 0(정상 경로·SCOPE OUT 유지) 20% · 절대규칙(#7·#8·마이그레이션 무단생성 금지)+진단 정직성 10%.

────────────────────────────────────────────────────────
## Open Questions (★ 사용자 게이트 확정 2026-07-31)
────────────────────────────────────────────────────────
- **OQ-1 (①) ✅ 조건부 원자 UPDATE**: `reserved_qty += qty WHERE reserved_qty+qty <= planned_qty`, 영향행 0=OVER. 양 provider 동시 해소·RMW 충돌 원천 제거·Finalize ExecuteUpdate 패턴 재사용. (catch-retry 미채택.)
- **OQ-2 (⑤) ✅ 틸트 차단(null+alarm)**: 미매칭 시 셀 반환 거부(null)+WARN+alarm(`CELL_ORDER_UNMATCHED`) → IF-11/물리 틸트 미트리거. 혼적 원천 차단. (orphan 추적 미채택 — 이미 IF-10 보고된 물리 상품은 틸트 명령 없이 대기.)
- **OQ-3 (②) ✅ 전건 비활성화만**: 이전 활성 piece 전건 비활성화 + 기존 status-필터 유니크 백스톱(MAJOR-1 멱등) 유지. 단일-활성 DB 불변식 강제는 미포함(최소 변경).

> Planner self-check — Detected project type: Backend/API. Required scenario slots: 9 (endpoints-touched, happy-path-per-endpoint, error-cases[S-①a·S-①b·S-②·S-⑤a·S-⑤b], regression-guard[R-③·R-④]). All slots filled: yes.
