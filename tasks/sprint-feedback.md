# Sprint Feedback — S-B2C-BARCODE-MULTI-FIX (Fix1 + Fix2)

Evaluator: 단일 (functional only — 계약 선언). 평가일: 2026-07-27.
브랜치: feat/b2c-barcode-multi-fix. Ground-truth HEAD: d9b6acc (작업은 전부 워킹트리 미커밋 + 신규 3파일).
방법: ground-truth git 확인 + 계약/코드 직독 + dotnet test 자체 재실행(466) + 프론트 tsc/lint/build 자체 실행 +
      격리 라이브 스택(백엔드 :5299 Sqlite scratch · Vite :5290) Playwright(MCP 헤드리스) 실측 +
      실 HTTP IF-05 왕복 응답 본문 캡처. 현장 실서비스(:5205/:5173/:1502/COM1/현장 SqlServer) 무접촉.

---

## 판정: APPROVED (7/7 Completion Conditions PASS, functional 차원 PASS)

---

## 조건 1 — dotnet test 전량 GREEN (독립 재실행) — PASS
- `dotnet test backend/Wcs.sln` **자체 재실행**: `실패 0 · 통과 466 · 건너뜀 0`(1m23s, exit 0).
- 신규 필터(`~BarcodeDestinationSelectorTests|~B2cBarcodeMultiFixTests`) 자체 재실행: `14/14 GREEN`(2s).
- baseline 산술: `총 466 − 신규 14 = 452(기존)` — 일치. Generator 보고와 독립 확인 일치.
- 빌드 경고 = 선재 NU1903(SQLitePCLRaw advisory) 5건뿐 · 신규 경고 0.
- 빌드 함정 대응: 사전 dotnet/MSBuild/testhost/vstest 전수 kill + MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0.

## 조건 2 — 프론트 tsc/lint/build exit 0 (wwwroot 무접촉) — PASS
- `npm run typecheck`(tsc --noEmit) exit 0.
- `npm run lint`(eslint) exit 0.
- `npx vite build --outDir <scratch> --emptyOutDir` exit 0("✓ built in 8.10s"). 스크래치 outDir → **wwwroot 무접촉**.
- 신규 에러/경고 0 (유일 경고 = 선재 signalr PURE-annotation + >500kB chunk).

## 조건 3 — Playwright 브라우저 실측(N행 표시 + 설비 배정 UI 무손상) — PASS
격리 스택: Wcs.Api :5299(`--urls`로 appsettings :5205 오버라이드) · Sqlite scratch(`eval-wcs.db`, MigrateOnStartup) ·
Sorters 0-DB → 폴링 미개통 + dead-TCP override(:59991) → 실 PLC/COM1 무접촉 · Vite :5290(strictPort, VITE_API_TARGET=:5299).
- (a) **배치 상세 per-item N행**: EVAL-MULTI(1 오더 EVAL-ORD-M : 3 바코드) 선택 → 하단 그리드 **정확히 3행**
  (evaluate `detailRowCount===3`) · 바코드 EVAL-BC-1/2/3 · 항목별 계획 5/7/3(집계 아님) · 헤더 "3건".
  대조: EVAL-SINGLE(N=1) 선택 → **1행**(order-level 목적지 CHUTE #502·배정 표시). 스샷 01·02.
- (b) **설비 관리 배정 UI 오더 단위·무손상**: `/b2c/facility` 미할당 오더 패널에 3 바코드 오더 EVAL-ORD-M 이
  **오더 1행**으로만 표시(per-item 아님) — GetOrdersAsync/B2cOrderDto 오더단위 유지 실증. 스샷 03.
  · 배정 클릭스루: 작업자 입력 → 슈트 501 + NOASSIGN-ORD 체크 → 배정 → 501 "2건 NOASSIGN-ORD, DUP-ORD-B",
    미할당 목록에서 제거(스샷 04).
  · 해제 클릭스루: 501 체크 → 해제 → confirm("해제될 오더 2건") → **NOASSIGN-ORD만 미할당 복귀**,
    DUP-ORD-B(IF-05 예약=시작)는 **OQ-3로 스킵 유지** → 501 "1건 DUP-ORD-B"(스샷 05). 배정/해제 정상 동작.

## 조건 4 — cross-layer E2E(중복 바코드 → 배정 오더 목적지 OK+chuteNo, 응답 본문 증거) — PASS
조성: 실 엑셀 업로드 API(`POST /api/b2c/test-data/upload`)로 교차-배치 중복 바코드 생성 + 설비 배정 API 로 한쪽만 배정.
실 HTTP IF-05(`POST /api/v1/destination-query`) 응답 본문(fresh):
- `DUPBC01`(배정 오더 DUP-ORD-B→슈트501, orderId=3 + **미배정** DUP-ORD-A, orderId=2[더 작음]) →
  `{"result":"OK","chuteNo":501}` — **배정 오더 목적지 선택**(구 무정렬 .FirstOrDefault()면 작은 orderId 미배정을 골라 NG 위험). ★ THE FIX.
- `SINGLEBC01`(단건 1:1, 배정 슈트502) → `{"result":"OK","chuteNo":502}` — 회귀 0.
- `NOASSIGNBC01`(미배정만 + 두 슈트 이미 RUNNING 점유) → `{"result":"NG","chuteNo":null}` — AUTO 폴백→NO_DEST 불변.
- `DUPBC01` 재호출(다른 pId) → `{"result":"OK","chuteNo":501}` — 결정성 확인(동일 목적지).
- 예약 증거: 배치 상세/설비 UI 에서 DUP-ORD-B·SINGLE-ORD reserved=1(선택 오더에만 반영) 실측.

## 조건 5 — 마이그레이션 diff 0 · 무접촉 경계 diff 0 — PASS
- 마이그레이션(Sqlite/SqlServer): `git status` 0건. PlcGateway: 0건. Wcs.Data 스키마: 0건.
- Wcs.Core: **신규 파일 BarcodeDestinationSelector.cs 만**(Fix 2 순수 규칙·계약 명시 in-scope) — 기존 판정 로직(DepositDecider/RegisterMap/모델) 수정 0.
- B2cFacilityService.cs: 삭제(`-`)행 0 → GetOrdersAsync 순수 무변경(GetBatchItemsAsync 가산만). B2cOrderDto: 삭제 0(B2cBatchItemDto 가산만).
- frontend b2cFacility.ts: `useFacilityOrders`(설비 배정용) 무변경 — `useFacilityBatchOrders`→`useFacilityBatchItems` 교체만.
- 변경 파일 = 계약 명시 파일 한정. (DEPLOY-ONPREM.md 503줄은 **이 스프린트 이전 브랜치 커밋**[2af3c98/d9b6acc]의 docs-only — 워킹트리 미변경·스프린트 스코프 외.)

## 조건 6 — 콘솔 BLOCKING 0 — PASS
- 내 세션(all:false, :5290): **총 3 메시지 · Errors 0 · Warnings 0**. React dev-warning/pageerror 0.
- `all:true` 캡처의 177 에러줄은 **전부 foreign-buffer(:5175)** — 내 격리 포트 :5290 참조 0건(grep 확인) + 타임스탬프가
  내 navigate(≈05:50) 이전(05:45~46). 공유 브라우저 프로필 타 세션 잔재(lessons foreign-buffer 재확인) — 앱 결함 아님.

## 조건 7 — sprint-log.md 마커 + 조사 결론 + 재량 근거 — PASS
- `## IMPLEMENTATION COMPLETE — S-B2C-BARCODE-MULTI-FIX` 마커 실재(파일 최말단·정본).
- 경로 일관성 조사 결론 기록: "바코드→목적지" 모호성은 IF-05 QueryDestination 고유. IF-09/IF-10 은 PId 조회(바코드 아님),
  SelectCell/ReleaseEmptyAssignment 은 destination(chuteNo) 스코프 → 교차-목적지 모호성 구조적 부재. SPEC §7-B 반영 확인.
- 재량 결정 근거 기록: (1) 엔드포인트 신규(monitor items·orders 재사용 시 계약 위반·N+1) (2) tiebreak = 최신 DestAssignedAt → 최소 OrderId → 최소 OrderItemId(PK 전순서·완전 결정성).

---

## Craft/성능 관찰 (비블로킹)
- `GetBatchItemsAsync`: batchId 단일 쿼리 + AssignedCellNo 상관 서브쿼리(projection 내) — EF Core 가 단일 SQL 로 번역(N+1 아님). GetOrdersAsync 와 동형. OK.
- `QueryDestination` 후보 `.ToList()` 후 LINQ-to-objects 선택 — 단건 1:1 은 1행 materialize(무시가능), 중복도 소수. Barcode 인덱스(S-HARDENING-1 `(Barcode)`) 존재. 성능 우려 없음.
- 선택 `Select(...)!` null-forgiving 은 상위 `candidateItems.Count==0` 조기반환으로 후보≥1 보장 → 안전.
- 절단 힌트: `items.length >= 1000` 이면 표면화(Fail-Loud). 항목 단위 절단이라 초과 시 한 오더 바코드가 경계에서 갈릴 수 있으나 힌트로 방어 — orders 와 동일 정책, 비블로킹.

## 스크린샷/증거
`screenshots/S-B2C-BARCODE-MULTI-FIX_20260727-145000/`: 01-datagen-batchdetail-3rows.png · 02-datagen-single-1row.png ·
03-facility-orderlevel.png · 04-facility-after-assign.png · 05-facility-after-unassign.png · console-all.log.

## 반복 이슈 검사
feedback-archive.md 대조 — 이 스프린트의 결함/이슈(비결정적 FirstOrDefault·per-item 표시)와 동일 반복 항목 없음. lessons 승격 대상 없음.

---

## APPROVED
전 7개 Completion Condition + functional 차원 PASS. 코드 수정 없이 피드백만 수행. commit 전 4-Tier 코드리뷰는 orchestrator 몫.

## Step 4.5 코드리뷰 Minor (S-B2C-BARCODE-MULTI-FIX · Critical 0 · Important 1(스코프-문서·분리처리) · Ready=With fixes) — 다음 스프린트 참고
- [Minor] B2cFacilityController batch-items `take` 상한이 GenerateCountMax(=오더 수 1000)를 **항목(order_item) 수**에 적용 → 400오더×3바코드=1200항목이면 1000에서 절단(Fail-Loud 힌트 `items>=ORDERS_FETCH_MAX`로 표면화·침묵 아님). 항목 전용 상수 또는 "오더-수 상한을 항목-수에 적용" 한계 주석 권장.
- [Minor] BarcodeDestinationSelector 배정 승자가 downstream 가용성 미고려 — 같은 바코드 다중 배정(서로 다른 목적지) 최악 케이스에서 최신 DestAssignedAt 승자 목적지가 FULL/PAUSED면 다른 가용 배정후보로 폴백 안 함(NG). 계약 인정 최악케이스·선택/가용성 분리 설계. SPEC §7-B에 "승자-blocked 시 무폴백" 한 줄 명시 권장.
- [Minor·기존] DbRepositories.cs:75 `if (order.Status == COMPLETED)` 도달 불가(후보 쿼리가 이미 COMPLETED/CANCELLED 제외) — 이 스프린트가 만든 게 아니라 FirstOrDefault 시절부터 존재. 회귀 아님. 정리는 별건.
