# Sprint Feedback — S-B2C-UX (B2C 데이터 생성/설비 관리 UX 재구성)

**APPROVED (FIX ITER 2 재검증 · 유지)** — Evaluator, 2026-07-15. Step 4.5 코드리뷰 Important 1건(침묵 절단 / Fail-Loud) 완전 해소·회귀 0(360/360)·브라우저 실증·콘솔 0.

---

## FIX ITER 2 재검증 (Important — 침묵 절단 제거 / Fail-Loud · PASS) — 2026-07-15

핸드오프 마커 = `tasks/sprint-log.md` L3646(말단) `## IMPLEMENTATION COMPLETE — FIX ITER 2`. HEAD=`679aacc`(불변·미커밋). 브랜치 `feat/b2c-ux`. 검증 환경(격리): Wcs.Api :5215(Release·fresh scratch SQLite `wcs-fix2.db`) + Vite :5190. 증거 `screenshots/S-B2C-UX-FIX2_20260715-084000/`(01 datagen-1000-truncation-banner · 02 facility-unassign-204-dialog · 03 facility-after-unassign-all).

### 문제 (Step 4.5 Important)
`GET /api/b2c/facility/orders` take clamp 기본 200·최대 500 인데 프론트 훅이 take 미전달 → 한 배치 최대 1000 오더 시 **침묵 절단**: (a) 배치 디테일 그리드가 200건만 "전체"로 표시, (b) 미할당/할당 집계·**슈트 단위 해제**가 200 상한 → 언더카운트·부분 해제·무경고. (내 iter-1 브라우저 검증이 4-오더 소규모라 미포착 — 정당한 코드리뷰 캐치.)

### 수정 검증 (코드 판독 + 실증) ✅
- **백엔드**(`B2cFacilityController.GetOrders`): clamp 상한·기본값 모두 `B2cConstants.GenerateCountMax`(1000)로 상향. 스케일-세이프(무한 조회 금지·cap 유지). 서비스/스키마/**마이그레이션 0**. 신규 테스트 `GetOrders_LargeBatch_NotClampedAtLegacyCap`(250 오더·take 미지정 GET → 250 전량, 200 clamp 회귀 잠금).
- **프론트**(`b2cFacility.ts`): `ORDERS_FETCH_MAX=1000` export(백엔드 미러) + `orders(...,take,...)` 인자. `useFacilityOrders`·`useFacilityBatchOrders` 모두 take=1000 명시. `orders()` 시그니처에 take 를 signal 앞에 삽입 — 유일 소비처 2 훅만(다른 `.orders` 는 별개 `api.orders`)·tsc 0 로 스테일 콜러 0 확인.
- **Fail-Loud 배너**(반환수 >= 상한): DataGen 디테일 + Facility 미할당/할당 각각 warn 배너·근거 문구.

### 브라우저 실증 (핵심 — >200 시나리오)
- **데이터 생성 디테일**: BIG-A(250) 선택 → 디테일 **250행 전량**·헤더 "250건"·**배너 없음**(250<1000, false-hint 0) — 과거 200 clamp 제거 실증(evaluate: detailRows=250). MAX-A(1000) 선택 → **1000행**·헤더 "상위 1,000건 표시"·**절단 warn 배너 present**(Fail-Loud at cap).
- **슈트 단위 해제 전량 커버**: 슈트 50 에 **204 오더**(>200) 배정(API 벌크) → 좌 그리드 현재배정 **"204건"** → 체크 → `해제` → ConfirmDialog **"해제될 오더 204건"**(200 clamp 였다면 200) → 확인 → 순차 unassign → **assigned=0**(API 폴 확인)·현재배정 "—". 200 초과분(4건) 누락 0.
- **미할당 배너**: 미할당 1046(1000 MAX+46 BIG) → 우 패널 **"조회 상한 1,000건 도달" 배너 present**(Fail-Loud)·할당 배너는 204<1000 이라 미표시(false 0).
- **콘솔 BLOCKING**: :5190 origin error/warning/pageerror **0**(전 세션).

### 회귀 · 정적 (fresh · 격리)
- **전체 스위트 360/360 GREEN**(359 + large-batch 1·실패0·건너뜀0·**18s**·exit0). raw: `통과! - 실패: 0, 통과: 360, 건너뜀: 0, 전체: 360`. Release 빌드 0 error(경고 10 = 기존 NU1903).
- `tsc --noEmit` 0 · `eslint .` 0 · `vite build`(스크래치 outDir·wwwroot 무접촉) 0. 무접촉: 수정 표면 4파일(controller·b2cFacility.ts·2 페이지)+테스트/문서만. PlcGateway/Core/실 PLC/사용자 DB diff 0. 라이브 assign 204 + unassign 204 정상 — 기존 배정/해제 경로 회귀 0.
- 나머지 5 Minor 는 백로그 잔류(무접촉) — 계약 위반 아님.

### 판정
Step 4.5 Important(침묵 절단) 완전 해소 — 백엔드 cap 상향·프론트 take 명시·Fail-Loud 배너 3처. >200(250·204) 및 cap(1000) 실증으로 배치 디테일·슈트 단위 해제 전량 커버 확인. 회귀 0(360 격리)·콘솔 0·마이그레이션 0. **APPROVED 유지.**

---

## (이하) S-B2C-UX 최초 APPROVED 기록 — 2026-07-14

**APPROVED** — Evaluator, 2026-07-14. 게이트 확정(OQ-1~OQ-5 + 해제) 전부 반영·회귀 0·브라우저 4-플로우 + E2E-1 실증·콘솔 0.

핸드오프 마커 = `tasks/sprint-log.md` L3602(말단) `## IMPLEMENTATION COMPLETE — S-B2C-UX`. HEAD=`679aacc`(불변·미커밋). 브랜치 `feat/b2c-ux`.
검증 환경(평가자 격리): Wcs.Api :5215(Release·fresh scratch SQLite `wcs-eval.db`·`MigrateOnStartup` 스키마 생성·`SeedOnStartup=false` 0 목적지) + Vite :5190(`VITE_API_TARGET=http://localhost:5215`). Sim3ds :1512 **불요** — 소터 IF-05 가용성은 `SorterCanAcceptBarcode`(셀 술어)만 게이트하고 Online 을 요구하지 않아(RcsController IF-05 lambda·DestinationStatusService.ComputeSorter bundle-null=Paused:false) UI-생성 offline 소터도 셀 배정만 있으면 OK — 테스트 `Mixed_Topology_IF05` 와 동형. 증거: `screenshots/S-B2C-UX_20260714-164600/`(01~07 png + console.log).

---

## 1. 백엔드 — 배치-스코프 reset (OQ-1) ✅
- `B2cResetRequest`: `sorterChuteNo`(int) → **`batchId`(long)** + `force` + **`operatorName?`**(OQ-3). 소터 스코프 폐지.
- `ResetAsync`: 스코프 술어 = `p.OrderItem != null && orderIds.Contains(p.OrderItem.OrderId)`(배치 오더). in-flight COUNT 재판정 **트랜잭션 안**(TOCTOU 협착 유지)·아카이브 소프트삭제(`ExecuteUpdate ArchivedAt`·하드삭제 0)·order_item reset·**COMPLETED→RUNNING 재개, CANCELLED 비재개**(명시 주석) 전부 보존. 감사 `B2C_RESET` detail 에 `op`·`batchId` 병기. `Op()` 공백→"(unspecified)".
- **고아 엔드포인트 0**: 라우트 `POST /api/b2c/test-data/reset` 재사용(본문만 변경). 소터-스코프 잔존 코드/호출부 0(컨트롤러·서비스·프론트 grep 확인).
- **마이그레이션 0**: `git status backend/src/Wcs.Data/` diff 0(ArchivedAt 기존재). 신규 마이그레이션 파일 0.
- reset 테스트 배치 스코프로 갱신: `B2cTestDataServiceTests`(6 — SeedSorter가 (SorterId,BatchId) 반환·piece 를 OrderItemId 로 배치 귀속·미존재 배치 F·TOCTOU) + `B2cApiTests`(Reset_UnknownBatch_200F) + `B2cFacilityApiTests`(E2E batchId+operatorName). 의도된 계약 변경 — 회귀 아님.

## 2. 회귀 · 정적 (fresh · 격리) ✅
- **전체 스위트 359/359 GREEN**(실패 0·건너뜀 0·**18s**). raw: `통과!  - 실패: 0, 통과: 359, 건너뜀: 0, 전체: 359 - Wcs.Tests.dll` (DOTNET_TEST_EXIT=0).
- ⚠ **flake 귀속(fresh 증거)**: live 스택(:5215)+브라우저+빌드 **동시 부하 중** 1회차 dotnet test 가 ~17분 미완(CPU 기아·행 아님·testhost 생존 확인) → **live 스택·Vite·browser 전부 중단 후 격리 재실행 = 18s 359/359 GREEN**·exit 0 로 확정. 문서화된 환경 flake(lessons: e2e-parallel-load-surfaces-integration-flakes·testhost-teardown-channel-race)이며 S-B2C-FACILITY Evaluator 도 동일 패턴 기록(피드백 L22). reset 변경은 E2E 핸드셰이크/타이밍 경로 무관 — 코드 귀속 아님.
- 프론트: `npx tsc --noEmit` exit 0(무출력) · `npx eslint .` exit 0(무출력) · `npx vite build`(스크래치 outDir — wwwroot 무접촉) exit 0. Release 빌드 0 error(경고 10 = 기존 NU1903 SQLitePCLRaw 취약성 advisory·본 스프린트 무관).
- 무접촉: diff 는 B2C API(2)/테스트(3)/프론트(6)/docs(2)/tasks(2)만. Wcs.PlcGateway·Wcs.Core·실 PLC/COM1/Azure/사용자 DB diff 0. 상수 외부화 유지(GENERATE_COUNT_MAX 미러·근거 주석).

## 3. 브라우저 검증 (Playwright MCP · :5190 · 콘솔 BLOCKING 0) ✅
- **U1 NAV**: b2c 사이드바 상단 2개 = 데이터 생성 → 설비 관리, 이어 모니터링·3DS 워드·운영 제어. nav 클릭 도달(직접 URL 아님). `homePathFor('b2c')`=`/monitor`(하드코딩·NAV순서 비의존 — 재배열로 불변, 계약 §C "바뀌면 조정" 조건 미발생). [01-nav-order.png]
- **U2/U3/U8 데이터 생성 마스터-디테일**: 생성 폼(EVAL-A·4건) → 그리드 행 체크박스 + 상단 초기화(0=비활성) + 하단 디테일 빈안내. 행 클릭 → `배치 상세 — EVAL-A #1`(orders?batchId 왕복·EVA-01~04). **체크박스≠행선택 분리(OQ-5)** — 행 클릭이 체크 미발생. [02-datagen-master-detail.png]
- **U5/U6 설비 2패널(OQ-4a/4b)**: 좌=슈트 201(리프 체크박스) + 소터 231(펼침→셀1·셀2 체크박스), 우=미할당 오더, **초기화 버튼 부재(이관 확인 OQ-1/2)**. 3 대상 + 3 오더 체크 → "선택 대상 3·오더 3 → 3건 배정" → `배정` → **min(N,M)=3 인덱스 페어링**: EVA-01→CHUTE#201, EVA-02→SORTER#231 셀1, EVA-03→셀2. 좌 현재배정 갱신·우에서 제거. [03,04]
- **U4 다건 초기화**: 배치 체크 → 작업자 공백 초기화 시도 → **경고·다이얼로그 미개시(OQ-3 게이트)**. 작업자 입력 후 초기화 → danger ConfirmDialog(대상 배치 목록·soft-delete 범위·작업자 평가자·비가역) → 확인 → in-flight 3건 거부 토스트("0건 초기화, 1건 진행 중") + **강제 초기화 다이얼로그 체이닝**(silent-close 없음) → 강제 확인 → 성공. [05,06]
- **U7 다건 해제**: 좌 슈트+소터 셀2 대상 3개 체크 → `해제` → ConfirmDialog(대상 3·해제 오더 3·작업자·OQ-3 안내) → 확인 → "해제 1건 완료, 진행 중 스킵 2건" — **OQ-3 가드 실증**: 재-IF-05로 reserved=1 된 EVA-01/02 스킵, 미시작 EVA-03만 해제·미할당 복귀. [07-facility-unassign-oq3guard.png]
- **콘솔 BLOCKING**: :5190 origin error/warning/pageerror **0**(전 세션·React DevTools info 1건만). console.log 첨부.

## 4. E2E-1 교차 레이어 (BE+FE+DB · MANDATORY) ✅
① 배치 생성(4 오더) ② 2패널 혼합 배정(슈트+소터 셀) ③ **IF-05 왕복**: EVA-01→OK chuteNo 201·EVA-02→OK 231·EVA-03→OK 231(reserved 1 each) ④ 데이터 생성 디테일이 예약/할당(셀번호 포함) 반영 ⑤ 배치 초기화 refuse→force → **piece 아카이브(reserved→0)·오더/셀 배정 보존·배치 존치(orderTotal 4·하드삭제 0)** ⑥ **재 IF-05(동일 pId)** → 재예약 OK·reserved **정확히 1**(아카이브 dedup 제외·이중카운트 0 — HIGHEST-STAKES 불변량 실증).

## 5. 문서 · Minor ✅
- `docs/B2C-DATAGEN.md`(reset 배치 스코프·마스터-디테일·마이그레이션 0)·`docs/B2C-FACILITY.md`(reset 제거·2패널·min(N,M)·Minor #1/#7) 개정 정합.
- **Minor #1/#7 흡수**: `DestStatusBadges` — 활성 슈트 "정상"만·`!isActive`면 "비활성"만(정지/만재/정상 억제) 코드+관측 확인. 파괴 액션(초기화·해제·비활성·clear·pause) 전부 ConfirmDialog+작업자 귀속.
- defer Minor(#2 서버측 공백 거부·N+1·enum·abort)는 tasks/todo.md 잔류 — 계약 위반 아님.

## 판정
게이트 확정 OQ-1~5 + 해제 전부 정합. 통합 품질(reset batchId 계약 BE↔FE 미러·고아 0)·파괴 안전성(배치 reset·다중 해제 OQ-3 가드·아카이브 불변량·감사·ConfirmDialog)·레이어 품질(마스터-디테일·2패널·콘솔 0)·회귀 0(359/359 격리)·마이그레이션 0·무접촉 경계 — 완료 조건 9/9 충족. **APPROVED.**

### 비차단 관찰(후속 참고 · FAIL 아님)
- NAV 최상단이 데이터 생성이나 `homePathFor('b2c')`는 여전히 `/monitor`(모드 진입 착지 ≠ nav 최상단). 계약이 "순서만 변경·item/경로/enabled 불변"을 요구했고 homePathFor 는 하드코딩이라 미변경이 계약 부합 — 필요 시 후속에서 착지 페이지 정합 검토 가능.

## Code Review Pass (Step 4.5 — 독립 리뷰 + fix iter 2, 2026-07-15)

**최종: Ready to merge = Yes** (초판 "With fixes" → Important 1건 fix 후 Evaluator 재검증 APPROVED 유지).

강점(리뷰): 배치스코프 reset이 in-tx TOCTOU in-flight 재확인·아카이브·수량·COMPLETED재개·CANCELLED
비재개·archived-exclusion 전부 보존 / 소터스코프 orphan-free(테스트 이관) / min(N,M) 안정정렬 페어링 /
force 체이닝 / vite 기본 :5205 유지 / 파괴액션 confirm+operator / 비활성 배지 혼동 해소.

- **[해소] Important — orders take=200 조용한 잘림(Fail-Loud 위반)**: 백엔드 clamp를 GenerateCountMax(1000)로
  상향, 프론트가 명시 take 전달 + 캡 도달 시 truncation 배너(상세/미할당/배정그룹핑). 250-오더 전량·1000
  배너·204 배정 슈트 전량 해제 실측.

### Minor (비블로킹 — 다음 sprint / todo 등재)
1. b2c 랜딩(homePathFor)이 /monitor인데 NAV 최상단은 데이터 생성 — 랜딩 일관성(의도면 유지). **판단: /monitor 유지**(모니터링=운영 기본 뷰, nav 순서≠랜딩) — 사용자 이견 시 변경.
2. 대량 배정 부분실패 시 식별 피드백 부재(집계만) — identity-level 피드백 후보.
3. 레거시 null cellNo 소터 배정은 좌측 셀 행이 없어 UI 해제 불가(엣지/레거시).
4. QUERIED(예약 전) 피스 거부가 "진행중 스킵" 아닌 "실패"로 분류(표시상).
5. pre-existing dead code(useB2cSummary/Detail·b2cTestData.summary/detail·백엔드 summary/detail 미사용) — 별도 정리 스프린트.
