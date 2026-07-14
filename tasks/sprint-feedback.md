# Sprint Feedback — S-B2C-DATAGEN (B2C 테스트 데이터 생성·초기화 페이지)

**APPROVED** — Evaluator, 2026-07-13 (3 iterations: iter-1 FAIL 1건 → iter-2 fix PASS → iter-3 Step 4.5 코드리뷰 TOCTOU fix PASS).

브랜치 `feat/b2c-datagen`. HEAD=`28439170`(이전 스프린트 커밋). 스프린트 변경분 전부 **미커밋 working tree**(orchestrator 커밋 대기). 핸드오프 마커 = `tasks/sprint-log.md` L3451(말단) `## IMPLEMENTATION COMPLETE — S-B2C-DATAGEN FIX ITER 3`.

---

## Iteration 3 — Step 4.5 코드리뷰 BLOCKING(OQ3 in-flight 가드 TOCTOU) fix 검증 (PASS)

**결함**: in-flight COUNT 가드가 `BeginTransactionAsync` **밖**에 있어 "체크 → 아카이브 UPDATE" 사이에 IF-05 가 새 RESERVED piece 를 삽입하는 창(TOCTOU) — 비강제 reset 이 진행 중 piece 를 조용히 아카이브 가능(OQ3 위반).

**Fix 검증(변경 = 스프린트-신규 2파일만, mtime 으로 확정 — `B2cTestDataService.cs` 15:51 / `B2cTestDataServiceTests.cs` 15:53, 그 외 iter-2 와 동일·tracked diff 불변)**:
- 코드 판독: COUNT 가 트랜잭션 안·아카이브 UPDATE 직전으로 이동. 거부 시 `tx.Rollback + F`(메시지·counts·감사 WARN 기존과 동일 — Audit 는 논블로킹 채널 enqueue 라 tx 무관). 응답 형상 무변경. READ COMMITTED 협착은 단일 운영자 관리 시나리오 전제(리뷰어 확인)로 수용.
- 신규 회귀잠금 테스트 `Reset_InFlightGuard_CountExecutesInsideTransaction`: `DbCommandInterceptor` 로 거부 경로의 piece COUNT 가 `DbTransaction` 참여 상태로 실행됨을 구조 단언(`Assert.NotEmpty` 로 공허 통과 차단 + 거부 후 archived_at NULL 무접촉 재단언). 가드가 tx 밖으로 되돌아가면 즉시 RED — 잘 설계됨.
- **독립 full 재실행(`-c Release` — 사용자 Debug-bin 프로세스 잠금 회피, 검증 시점 사용자 프로세스 0 확인)**: **346/346 GREEN·0 실패·0 스킵·exit 0**(330+16 산술 일치, 20s, teardown 행 없음).
- **라이브 API 왕복(Release :5215·fresh SQLite·Sim3ds :1512)**: generate→IF-05(in-flight 1) → reset force=false → **F + `{"inFlight":1}` 형상·메시지 기존과 동일**·summary 무접촉(reserved 2/inFlight 1) → force=true → S(archivedPieces 1 등)·reserved 0/inFlight 0·오더 3R/배정 3 보존 → operation_log WARN refused→INFO force 전수.
- 브라우저 재검증 생략 판단 수용: 이번 사이클 frontend diff 0·응답 형상 무변경·UI force 경로는 iter-2 에서 클릭스루 완료 — 변경 표면(백엔드 가드)은 위 라이브 HTTP 왕복으로 재검증.
- 코드리뷰 Minor 4건(frontend max=200 미러·인프라 예외 감사·CANCELLED 비재개 문서화·summary N+1) 미수정은 coordinator 지시(다음 스프린트 이관)로 확인 — 비차단.

Ground truth(양 iteration 공통) = git status/diff + 변경 코드 직접 판독 + **독립 재실행 `dotnet test`(iter-1·iter-2 각각 345/345 GREEN)** + **양 provider 스크래치 DB `ef database update` 실적용·스키마 카탈로그 조회** + **라이브 API(:5215 SQLite + Sim3ds :1512) HTTP 왕복** + **Playwright 브라우저 클릭스루(:5190·콘솔 캡처)**. Generator 보고는 전부 독립 재현. Evaluator 코드 수정 0·커밋/푸시 0. 사용자 DB(`Rcs3dsInterlockingWcs`)·Azure·실 PLC/COM1 무접촉(SqlServer 검증은 throwaway `WcsB2cMigCheckEval_*` 생성→적용→DROP).

---

## Iteration 2 — BLOCKING #1 fix 검증 (PASS)

**결함(iter-1)**: UI 강제-초기화(force) 재요청 경로 무동작(silent no-op) — `onConfirm` finally 의 무조건 `setPending(null)` 이 `run()` 내부 `requestForceReset` 가 연 강제 다이얼로그를 같은 틱에 덮어써 닫음(OQ3 force 경로 봉쇄·Fail Loud 위반).

**Fix(단일 파일 `frontend/src/pages/B2cDataGenPage.tsx` onConfirm)**: `justRan` 캡처 + 함수형 가드 `setPending(cur => cur === justRan ? null : cur)` — run() 이 후속 다이얼로그로 교체했으면 보존, 자기 자신일 때만 닫음. 코드 판독으로 React 배칭 하 정확성 확인(`justRan` 3회 등장). **이 파일 외 diff 0**(백엔드·타 프론트 파일 iter-1 과 동일 — `git diff --stat` 확인).

**라이브 재현(재평가 조건 전부 충족)**:
1. fresh 스크래치(SQLite live2.db·0 소터)에서 generate(FORCE2·3셀) → 실 IF-05(pId 22001·qty 2) 로 in-flight 1/예약 2 조성 → `초기화` → danger 다이얼로그(⚠ in-flight 1건 경고) → 확인 → **"강제 초기화" 다이얼로그 실제 노출**("진행 중 1건... 진행 중 피스까지 보관") → 강제 초기화 확인 → 요약 **진행중 1→0·예약 2→0**, 오더/배정 보존(3R·배정 3).
   DB 검증: piece pId=22001 archived(ArchivedAt NOT NULL)·활성 배정 3 유지. operation_log: `B2C_RESET WARN {refused,inFlight:1,force:false}` → `B2C_RESET INFO {archivedPieces:1,archivedEvents:2,resetItems:1,force:true}` — UI 가 실제로 거부→force 2왕복을 수행했음이 감사로그로 입증.
2. `dotnet test` full 재실행: **345/345 GREEN**(iter-2 fresh).
3. `npx tsc --noEmit` 0 · `npm run lint` 0. 콘솔(세션 전체): **error 0 / warning 0 / pageerror 0**.

증거: `screenshots/S-B2C-DATAGEN_eval/iter2-01-inflight-warning-dialog.png` → `iter2-02-force-dialog-APPEARS.png` → `iter2-03-force-reset-success.png`.

---

## Iteration 1 — PASS 항목 요약 (상세 증거는 iter-1 기록·전부 독립 재현)

- **[P1] 회귀 0**: `dotnet test` 345/345 GREEN(기존 330+신규 15 산술 일치). 빌드 경고=선재 NU1903 뿐.
- **[P2] 아카이브 회귀(HIGHEST-STAKES)**: 전 활성-읽기 경로 `ArchivedAt == null` 필터 코드 판독 확인 — `SorterCellQty.LoadedQtyByCell`(셀 currentQty·SorterFull·IF-05 CanAcceptBarcode·IF-10 SelectCell·COMPLETED sorter_command 이중카운트 차단), `DbRepositories`(IF-05 prevActive×2·IF-09·IF-10·HasDepositRecord), `RcsController`, `ChuteCapacityService`, `MonitoringQueries`. 오더 완료 판정은 `order_item.SortedQty` 집계(reset 0)·sorter_command JOIN 아님. 라이브 이중카운트 차단 실증: reset(force) 후 재 IF-05 → reservedSum 정확히 2(4 아님)·하드삭제 0(pId 2행=1 archived+1 active).
- **[P3] 마이그레이션**: SqlServer throwaway(6체인 적용·sys.columns 로 3테이블 `ArchivedAt` datetime2 nullable 확인·DROP·사후 0건) + SQLite 스크래치(PRAGMA 확인). `AddPieceArchivedAt` 가 `AddHotPathIndexes` 직후 체이닝·Down 대칭.
- **[P4] 백엔드/API 라이브**: generate S+counts·**멱등**(재실행 카운트 전부 0)·summary/detail N↔N 정확·reset 가드(F+inFlight)/force(S)·400 군(workDate/cellCount/orderPrefix 인젝션 "a; DROP TABLE" 정규식 차단)·200 F 군(미존재 소터)·operation_log STATE 전수(거부 포함).
- **[P5] IF-05 소비 E2E**: 생성 바코드로 `destination-query` → `{"result":"OK","chuteNo":1}` → 예약→reset→재예약 재테스트 성립.
- **[P6] 프론트**: build(tsc)+eslint 0. B2C 사이드바 "데이터 관리" nav 도달(발견가능성)·생성→토스트→요약 갱신→셀 상세→danger ConfirmDialog(범위·"되돌릴 수 없음"·in-flight ⚠)·콘솔 0. 다크모드 N/A(단일 라이트).
- **[P7] 무접촉**: `Wcs.PlcGateway`·`Wcs.Core`·`HandshakeOrchestrator`·`Wcs.Sim3ds`·`vite.config.ts` diff 0. 사용자 DB `__EFMigrationHistory` 무변경.

---

## Minor (non-blocking — tasks/todo.md 등재)
1. **[S-B2C-DATAGEN] 프론트 테스트 러너 부재로 다이얼로그 체이닝(force 재요청) UI 회귀 테스트 미작성** — vitest/jest 미구성 상태라 이번 결함류(React 상태 배칭)가 어떤 자동테스트에도 안 잡힘. 테스트 프레임워크 도입 시 onConfirm→force 체이닝 케이스 최우선 등재(Generator 도 동일 의견·단일파일 제약으로 이연 — 타당).
2. **[S-B2C-DATAGEN] B2B 모드에서 `/b2c/test-data` 직접 URL 진입 시 배너 제목이 b2b 항목("데이터 생성")으로 표시** — 정상 nav 경로에선 미발생·cosmetic.

## 판정
Completion Conditions 5항 전부 충족(생성 데이터 IF-05 소비·초기화 후 재투입·확인 다이얼로그+감사로그+in-flight 가드(force 포함·iter-3 로 TOCTOU 협착)·브라우저 플로우 콘솔 0·회귀 0+양 provider 마이그레이션). 최종 스위트 = **346/346 GREEN**(-c Release·독립 재실행). Evaluation Criteria: 도메인 정합성 ★★★ 충족, 파괴 작업 안전성 ★★★ 충족(iter-2 force UI 경로 + iter-3 가드 원자성으로 완결), 패턴 일관성 ★★ 충족(B2B 패턴·기존 프리미티브 재사용·상수 외부화), 회귀 0+아키텍처 ★★ 충족. → **APPROVED**.

## Code Review Pass (Step 4.5 — 독립 리뷰 + fix iter 3, 2026-07-13)

**최종: Ready to merge = Yes** (초판 "With fixes" → Important #1 fix 후 Evaluator 재검증 APPROVED 유지).

- **[해소] Important #1 — OQ3 in-flight 가드 TOCTOU**: 가드 COUNT를 트랜잭션 안·아카이브 UPDATE 직전으로
  재배치(거부 시 Rollback+F, 감사 불변) + DbCommandInterceptor 회귀잠금 테스트(가드가 활성 tx 안에서 실행
  단언). Evaluator가 코드판독·346/346 Release 재실행·라이브 HTTP 거부/force 왕복으로 독립 검증.

### Minor (비블로킹 — 다음 sprint Generator 참고)
1. 인프라 예외 경로(DB throw·ArgumentException→400) operation_log 미기록 — 비즈니스 F/거부는 기록됨,
   Serilog 전역 커버. 감사 완결성 원하면 catch에서 WARN 1행.
2. 프론트 셀 수 상한 200 하드코딩 미러(B2cDataGenPage input max) — 백엔드 B2cConstants와 드리프트 가능,
   상수화/summary 응답에 상한 포함 후보(절대규칙 #7 정신).
3. Reset이 COMPLETED만 RUNNING 재개, CANCELLED 미재개 — 의도로 보이나 B2C-DATAGEN.md에 명시 권장.
4. GetSummaryAsync 소터당 N+1 쿼리 — 관리 화면·소수 소터라 실무 무해, 후속 최적화 후보.
