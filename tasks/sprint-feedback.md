# Sprint Feedback — S-B2C-FACILITY (B2C 설비 관리 페이지 + 데이터 생성 슬림화)

**APPROVED (유지)** — Evaluator, 2026-07-14 (iter-1 FAIL → FIX ITER 2 PASS → Step 4.5 코드리뷰 FIX ITER 3 재검증 PASS).

---

## FIX ITER 3 재검증 (Step 4.5 코드리뷰 Important 2건 · PASS) — 2026-07-14

핸드오프 마커 = `tasks/sprint-log.md` L3580(말단) `## IMPLEMENTATION COMPLETE — FIX ITER 3`. HEAD=`888f062`(불변·미커밋). fix-only 3파일(B2cFacilityService·ChuteCapacityService + B2cChutePushTests)·무접촉 경계·마이그레이션 0·프론트 diff 0(iter-2 대비 변경 없음) 재확인.
재검증 환경: Wcs.Api :5215(Release·fresh scratch SQLite `eval-wcs3.db`) + Sim3ds :1512 + fake UpdateChuteState :5315(ack `{"flag":1}`). 증거 `screenshots/S-B2C-FACILITY_fix3_20260714-060107/`(fix3-eval-01 + chutestate_push.log + console.log).

### FIX 1 (Important — 작업자 귀속: 감사 detail op 병합) ✅
- iter-1에서 내가 U+FFFD 표시깨짐만 보고 넘긴 실제 결함: `Audit`가 detail이 `{`로 시작하면 op를 **드롭**(모든 mutation 성공 INFO가 `{`-prefixed JSON → 작업자 실종). `MergeOp(op, detail)` 신설로 JSON 객체는 선두 `"op"` 삽입·평문은 `{op,msg}` 래핑 — 코드 판독으로 빈객체/whitespace/공존 케이스 정합 확인(결과 JSON 유효성 보존).
- **라이브 실측**: B2C_DEST_CREATE INFO detail = `{"op":"mechanic","destType":"CHUTE","chuteNo":2,"workFullQty":100}`(iter-1엔 op 없었음) + 거부 WARN = `{"op":"mechanic","msg":"...이미 존재..."}`. 브라우저 경로(op=홍길동)로 B2C_DEST_DEACTIVATE/ACTIVATE INFO 모두 `"op"` 존재 + **Python 디코드 문자열 매칭으로 DB에 `홍길동` 정확 저장 확인** → iter-1 "한글 깨짐"이 콘솔 cp949 렌더 아티팩트였음을 확정(DB UTF-8 왕복 정상).
- destination_event(트랜잭션·enum 부재로 cells/assign/unassign 미기록)와 별개로 operation_log가 이들의 정본 작업자 감사임을 코드 주석에 명시 — 마이그레이션 0 하 합리적.

### FIX 2 (Important — 비활성화 레지스트리/IF-08 일관성) ✅
- iter-1 검증 사각: CHUTE 비활성화가 인메모리 IsActive·IF-08 push를 반영 안 해 RCS가 비활성 슈트를 "수용가능3"으로 오인할 수 있었음(IF-05는 DB 직독이라 오라우팅은 없었으나 push 일관성 갭). `ApplyActiveStateInMemory`(ApplyPauseStateInMemory 대칭) 신설 — 인메모리 IsActive + `RaiseChuteStateChanged` 락 밖 발화, SetActiveAsync 커밋 후 CHUTE만 호출(소터 no-op). S-IF08 단일-emit 경로(OnChuteStateChanged→Observe→Pump·per-dest Gate 락) 재사용 확인.
- **라이브 실측(UI+FakeChuteStateServer)**: 재기동 부트스트랩 `[2]→[3]` → UI 비활성화(op 홍길동) → push **`[2]→[2]`**(수용불가) + isActive=false → UI 재활성화 → push **`[2]→[3]`**(수용가능) + ready=true. **단일-emit 확인**: 비활성화가 정확히 `[2]` push 1건만 발생(스팸 0). 신규 회귀잠금 테스트 `Deactivate_PushesState2_Reactivate_PushesState3`.

### 회귀·정적 (fresh)
- **전체 스위트 359/359 GREEN**(iter-2 358 + 신규 deactivate-push 1). ⚠ **flake 귀속 주의(fresh 증거)**: 내 live 스택(API+Sim3ds) 가동 중 1회차는 358/1-fail(부하 유도)·1회 42분류 환경 행 발생 → **live 스택 중단 후 격리 재실행 4회 연속 359/359 GREEN**(18~19s)로 확정. 단일 RED/행은 문서화된 환경 flake(lessons: e2e-parallel-load-surfaces-integration-flakes·testhost-teardown-channel-race)이며 FIX 3 변경(감사 문자열·인메모리 active·push)은 E2E 핸드셰이크/타이밍 경로와 무관 — 코드 귀속 아님. 생성자 `--blame-hang` 결정적 GREEN과 일치.
- 비-B2C 330 불변(B2C 29). `npx tsc --noEmit` 0 · `npm run lint` 0 · `npm run build` exit 0(프론트 무변경). 콘솔: fix3 상호작용창(:5215, 06:16~) error/warning/pageerror 0.

### 판정
Step 4.5 Important 2건 완전 해소(감사 작업자 귀속 전수·비활성화 IF-08 일관성)·회귀 0(격리 4회 GREEN)·단일-emit 보존·콘솔 0. 두 fix 모두 iter-1/iter-2에서 내가 미흡하게 다룬 지점(op 드롭 표시깨짐으로 오판·CHUTE 비활성 push 미검증)을 정확히 교정 — 코드리뷰 가치 실증. **APPROVED 유지.**

### Minor 처리 상태 (미착수 6건 — coordinator 이관·tasks/todo.md 등재 대상)
1. 비활성 CHUTE/소터 행 `정지` 배지 동반 노출(readiness paused 파생, status=NORMAL) — 이번에도 재현(비활성 시 "비활성 정지" 병기). cosmetic·혼동 소지. **미착수**.
2. facility API `operatorName` 서버측 공백 허용(/api/ops 400과 비대칭) — **미착수**.
3. `GetBatchesAsync` N+1 — **미착수**.
4. destination_event enum 확장 후속(enum 재사용 수용) — **미착수**.
5. (기타 Step 4.5 Minor — coordinator 이관분) — **미착수**.
> 6건 전부 비차단. fix-only 스코프 준수(Important만) — 정당.

---

## FIX ITER 2 재검증 (PASS) — 2026-07-14

핸드오프 마커 = `tasks/sprint-log.md` L3558(말단) `## IMPLEMENTATION COMPLETE — FIX ITER 2`. HEAD=`888f062`(불변·미커밋 working tree). fix-only 스코프(4파일 + 테스트 1)·무접촉 경계·마이그레이션 0 재확인(git status에 Migrations/PlcGateway/Core/Sim3ds/appsettings/vite.config diff 0).
재검증 환경: Wcs.Api :5215(Release·fresh scratch SQLite `eval-wcs2.db`) + Sim3ds :1512 + fake UpdateChuteState :5315(ack `{"flag":1}`). 증거 `screenshots/S-B2C-FACILITY_fix2_20260714-141500/`(fix2-eval-01~03.png + console.log).

## FIX ITER 2 재검증 (PASS) — 2026-07-14

핸드오프 마커 = `tasks/sprint-log.md` L3558(말단) `## IMPLEMENTATION COMPLETE — FIX ITER 2`. HEAD=`888f062`(불변·미커밋 working tree). fix-only 스코프(4파일 + 테스트 1)·무접촉 경계·마이그레이션 0 재확인(git status에 Migrations/PlcGateway/Core/Sim3ds/appsettings/vite.config diff 0).
재검증 환경: Wcs.Api :5215(Release·fresh scratch SQLite `eval-wcs2.db`) + Sim3ds :1512 + fake UpdateChuteState :5315(ack `{"flag":1}`). 증거 `screenshots/S-B2C-FACILITY_fix2_20260714-141500/`(fix2-eval-01~03.png + console.log).

### FIX 1 (BLOCKING B-1 해소) — 목적지 수정 UI 결선 ✅
- `b2cFacility.ts`: `updateDestination(destId, {floor?, workFullQty?, operatorName})` 클라 미러 추가(백엔드 POST /destinations/{id} 결선). status 제외(pause/resume 정본·주석).
- `B2cFacilityPage.tsx`: CHUTE 행 `수정`(Pencil) 버튼 + `EditDestinationDialog`. 프리필은 **React 렌더-중 파생상태 패턴**(`dest.id !== loadedId` 가드 — setState-during-render 안전·effect cascade 0·닫힘 시 리셋으로 재오픈 시 최신 재프리필). SORTER는 수정 필드 없어 버튼 미노출(코드 확인). 클라 검증(floor≥0·workFullQty≥1) 유지.
- **브라우저 실측(B-1 계획 그대로)**: 슈트 2 행에 수정 버튼 노출(fix2-eval 스냅샷) → 다이얼로그 프리필 workFullQty=100 → 50 입력(fix2-eval-01) → 제출 → 행 **"풀 50"** 반영(fix2-eval-02) + `GET /api/monitor/destinations` chuteNo=2 **workFullQty=50** + operation_log **`B2C_DEST_UPDATE` INFO** `{"status":"NORMAL","workFullUpdated":1}` 감사. 고아 엔드포인트 해소 — 정상 nav 경로로 도달·동작(Completion Condition 2 충족).

### FIX 2 (게이트 보완 — OQ-3 DENIED 예외) ✅ — 계약 게이트 근거 확인
- 계약 `tasks/sprint-contract.md` 게이트 확정 §에 **OQ-3 보완 = DENIED 예외**(L207-209) 기재됨: "거부(DENIED) 피스는 물리 라우팅 0이므로 재할당 가드의 '피스 이력'에 카운트하지 않는다 … 예약/적재(비-DENIED) 이력이 있으면 기존대로 차단." — iter-1 Minor #1(사용자 게이트 재확인 필요)이 이 게이트 라인으로 해소. 계약(설계 권위)이 이 예외를 담고 있어 수용. (게이트 발부 주체 = orchestrator/user 경유로 전제 — Evaluator는 계약을 권위로 검증. 만약 실제 사용자 발부가 아니라면 orchestrator가 reconcile 필요 — 단 변경은 iter-1 권고와 정합·정정 방향.)
- 코드: `OrderProgressAsync` + `GetOrdersAsync` 차단 술어에 `&& p.Status != PieceStatus.DENIED` 추가. **HasActivePiece = ANY 비-DENIED 활성 피스** — DENIED와 RESERVED가 공존하면 RESERVED가 여전히 차단 트리거(예외 누수 없음). reserved/sorted 합 가드는 별도 유지.
- 신규 테스트 `AssignOrder_DeniedOnlyOrder_Allowed_ReservedOrder_Blocked` — 양방향 단언(DENIED-only → canReassign true·배정 S / RESERVED → canReassign false·재배정 F+reserved≥1). DB 직접 조성으로 AUTO 비결정성 회피 — 잘 격리됨.
- **라이브 실측(예외 정밀 스코핑 확인)**: ① 슈트 2 비활성화 → 미할당 FX-01 IF-05 → NG(DENIED 피스 조성) → 슈트 2 재활성 → facility orders `hasActivePiece=false·canReassign=true`(DENIED 제외 실증) → **미할당 탭 배정 버튼 enabled**(fix2-eval-03) → CHUTE #2 배정 **S**(assigned 탭 "CHUTE #2·MANUAL"). ② **대조(누수 방지)**: 배정된 FX-01에 IF-05 재질의 → RESERVED 피스 생성 → unassign 시도 → **F**("예약 1")·canReassign=false로 전환. 비-DENIED 활성 피스는 그대로 차단됨을 라이브로 확인.

### 회귀·정적 (fresh)
- **전체 스위트 358/358 GREEN**(iter-1 357 + 신규 DENIED 테스트 1), 0 실패·0 스킵, 19s, exit 0(`dotnet test -c Release`). 42분 행 재현 없음.
- 분리 산술: `--filter ~Wcs.Tests.B2C` → **28 GREEN** ⇒ 비-B2C = 358−28 = **330 불변**(iter-1과 동일).
- `npx tsc --noEmit` 0 · `npm run lint` 0 · `npm run build` exit 0(5.23s). 청크 경고는 기존 informational.
- 콘솔: fix2 기능 세션(:5215, 05:36:10 이후) **error/warning/pageerror 0**. console.log의 :5216 잔재(build hash `index-Le7eLIxr.js` — 생성자 세션 탭)·05:26 재기동 창 CONNECTION_REFUSED는 내 세션·인프라 아티팩트로 앱 결함 아님.

### 판정
iter-1 BLOCKING B-1 완전 해소(수정 UI 결선·브라우저 왕복·감사) + FIX 2 게이트 보완이 계약 게이트 라인에 근거·예외 스코핑 라이브 검증·회귀 0. iter-1의 여타 GREEN 항목(혼합 토폴로지 E2E·OQ-1/2·AUTO/NO_DEST·감사)은 접촉 파일(facility service의 update/DENIED 술어 + b2cFacility.ts + page)이 그 표면을 건드리지 않고 358 전체 GREEN·수치 불변으로 회귀 0 확인 — 재실행 불요. Evaluation Criteria: 통합 품질 ★★★(수정 결선·IF-05 라우팅 불변)·파괴 작업 안전성 ★★★(OQ-3 예외가 비-DENIED 차단을 누수 없이 유지)·레이어별 품질 ★★(콘솔 0·파생상태 패턴 안전)·회귀 0 ★★ 전부 충족. → **APPROVED**.

### Minor 처리 상태 (최종 PASS — tasks/todo.md 등재 대상)
1. ~~DENIED 활성 피스가 OQ-3 영구 차단~~ → **FIX 2로 해소**(게이트 보완).
2. 비활성 소터 행 `정지` 배지+`재개` 버튼 노출(readiness paused 파생) — 미착수(fix-only 스코프). todo 등재.
3. facility API `operatorName` 서버측 공백 허용(/api/ops와 비대칭) — 미착수. todo 등재.
4. `GetBatchesAsync` N+1 — 미착수. todo 등재.
5. destination_event enum 확장 후속(enum 재사용 수용) — 미착수. todo 등재.

---

## 이하 iteration 1 기록 (FAIL — 참고 보존)

**FAIL (iteration 1)** — Evaluator, 2026-07-14. **BLOCKING 1건**(목적지 수정 UI 부재 — 고아 엔드포인트). 그 외 전 항목 GREEN — 아래 증거 전부 fresh(본 세션 실측).

브랜치 `feat/b2c-facility`. HEAD=`888f062`(직전 스프린트 커밋). 스프린트 변경분 전부 **미커밋 working tree**. 핸드오프 마커 = `tasks/sprint-log.md` L3477(말단) `## IMPLEMENTATION COMPLETE — S-B2C-FACILITY`.

평가 환경(평가자 포트·사용자/생성자 환경 무접촉): Wcs.Api :5215(Release·scratch SQLite·SeedOnStartup=false·SPA 정적 서빙) + Sim3ds TCP :1512 + 가짜 UpdateChuteState 수신 서버 :5315(**ack `{"flag":1}` — 계약 준수 필수**, 아래 참고①).
증거: `screenshots/S-B2C-FACILITY_20260714-044726/` (01~12.png + console.log + chutestate_push.log + chutestate_push.phase1-badack.log).

---

## BLOCKING — 수정 후 재핸드오프 요망

### B-1. 목적지 수정(floor·workFullQty) UI 부재 — 고아 엔드포인트 (Completion Condition 2 위반)

- 백엔드 `POST /api/b2c/facility/destinations/{id}` (수정: status/floor/workFullQty)는 구현·문서화됨(`docs/B2C-FACILITY.md` L33 API 표에 게시).
- 그러나 **프론트 결선 0**: `frontend/src/lib/b2cFacility.ts`에 update 함수 없음(orders/create/setActive/configureCells/assign/unassign만), `B2cFacilityPage.tsx`에 수정 버튼/다이얼로그 없음(행 제어 = 정지/재개·비움·셀 설정·초기화·활성/비활성만). grep "수정|edit" → 0건.
- 계약 §11 Completion Condition 2: "설비 관리 페이지에서 **목적지 생성/수정/활성화** … 정상 nav 경로로 도달·동작". workFullQty(만재 임계)·floor는 생성 시 1회 입력 후 UI로 변경 불가 — 운영자가 만재 기준을 조정하려면 직접 API 호출뿐. workflow-agents §Evaluator: "API 엔드포인트는 있지만 호출할 UI가 없음 → FAIL".
- **수정 요구(최소 범위)**: ① `b2cFacility.updateDestination(destId, {floor?, workFullQty?, operatorName})` 클라 미러 추가 ② 목적지 행에 수정 진입(예: 아이콘 버튼) → 다이얼로그(CHUTE: floor·workFullQty / SORTER: 대상 필드 없으면 비노출 또는 CHUTE 전용 버튼) ③ 성공 시 목록 갱신 + 응답 message(재기동 반영 주석) toast 표출. status 필드는 /api/ops(pause/resume)가 정본이므로 다이얼로그에서 제외해도 무방(제외 시 그 근거 주석).
- **재검증 계획(iteration 2에서 평가자 수행)**: tsc/eslint/build 0 → 브라우저에서 chute 2 workFullQty 100→50 수정 → 행 "풀 50" 반영 + `GET /api/monitor/destinations` workFullQty=50 + operation_log `B2C_DEST_UPDATE` INFO 감사 + 콘솔 0. 여타 GREEN 항목은 파일 접촉 범위 밖이면 회귀 재실행 최소화(전체 스위트 1회 + 스모크).

---

## GREEN — 검증 완료 항목 (fresh evidence)

### 1. 회귀·정적 (§10.2 E2E-2)
- **전체 스위트 from scratch: 357/357 GREEN, 0 실패·0 스킵, 20s, exit 0** (`dotnet test backend/Wcs.sln -c Release`, 원문: "통과! - 실패: 0, 통과: 357, 건너뜀: 0, 전체: 357, 기간: 20 s"). 42분 행 재현 없음(1회 결정적).
- 분리 산술: `--filter FullyQualifiedName~Wcs.Tests.B2C` → **27 GREEN** ⇒ 비-B2C = 357−27 = **330 불변**.
- 프론트: `npx tsc --noEmit` 0 · `npm run lint` 0 · `npm run build` exit 0(6.23s — 산출물 wwwroot 재생성, 검증 UI = 최신 빌드). 청크 500kB 경고는 기존 informational.
- **마이그레이션 0**: git status에 Migrations 신규/변경 파일 없음(계약 준수). 무접촉 경계: PlcGateway/Wcs.Core/Sim3ds/vite.config/appsettings diff 0 확인.

### 2. 혼합 토폴로지 E2E-1 (MANDATORY — 전 단계 실측)
1. **발견가능성(U6)**: B2C nav에 `설비 관리` 노출·클릭 도달(01.png). 기존 항목 "데이터 생성" 개명 확인.
2. **빈 상태(U4)**: 목적지 0·미할당 오더 0 안내 + 소터 재기동 주의문(02.png).
3. **UI 생성**: 소터 #1(SORTER_3D) + **슈트 2~9 전부 UI 다이얼로그로 생성**(8회 반복 — 03.png) + 셀 5×4=20 벌크(다이얼로그 라벨 "20셀 설정"·멱등 안내문, 04.png) → 행 "20/20 셀".
4. **런타임 슈트 등록**: 신설 슈트 각각 재기동 없이 IF-08 부트스트랩 push 발신(수신 로그 — phase1 로그에 chute 2~9 전수 `next_states:[3]`).
5. **재기동 → 소터 폴링**: /health `"sorters":[{"chuteNo":1,"online":true,…}]` + destinations online:true + UI 배지 online(05.png). 기동 부트스트랩 push = **목적지당 정확히 1회**(chute 2~9 [3]·소터 1 [2], 9줄 — chutestate_push.log 04:56:53).
6. **슬림 생성(2a·U1)**: 5-필드 폼 → FAC-EVAL 배치, **미할당 오더 5건**(EV-01~05) — 결과 view "5(미할당 5)"(06.png). OQ-4(계획수량=생성 개수·planned_qty=1) API 재확인. **멱등**: 동일 파라미터 재실행 → `ordersCreated 0·orderItemsCreated 0`.
7. **혼합 할당(UI)**: EV-01→소터#1 셀1 / EV-02·03→슈트#2. 할당됨 탭 "SORTER_3D #1 · 셀 1"/"CHUTE #2"(07.png).
8. **IF-05 혼합 라우팅**: EV-01→`{"result":"OK","chuteNo":1}` · EV-02→`{"result":"OK","chuteNo":2}` (POST /api/v1/destination-query 실왕복).
9. **슈트 pause→push→분리→resume→clear**: UI 정지(확인 다이얼로그: 대상·타입·작업자 — 08.png) → push `{"chute_numbers":[2],"next_states":[2]}` 1건 실수신(05:03:01) → **같은 슈트 오더 EV-03 IF-05 여전히 OK**(dispatch/readiness 분리 실증) → UI 재개 → push `[2]→[3]` 실수신(05:03:55) → UI 비움 → `lastClearedAt` 스탬프(destinations API) + OPS_CLEAR 감사. 상태 배지 정지↔정상 전환(09.png).

### 3. 2a 슬림 회귀 (미할당 IF-05 거동 — QueryDestination 코드 앵커 일치)
- **AUTO 배정**: 미할당 EV-04 IF-05 → `{"result":"OK","chuteNo":3}` (최저 빈 NORMAL 활성 슈트 — 슈트 2는 RUNNING 점유라 제외, DbRepositories.cs L97-110 앵커와 일치).
- **NO_DEST**: 빈 슈트 전무 상태(슈트 4~9 비활성화) → 미할당 EV-05 IF-05 → `{"result":"NG","chuteNo":null}` (piece_event에 NO_DEST — 응답 reason 필드 없음 = 기존 계약). 미존재 바코드 → NG. 상태 원복 완료(4~9 재활성).

### 4. 가드 (OQ-1/2/3 — ★★★ 파괴 작업 안전성)
- **OQ-2 refuse→force 체이닝(UI)**: in-flight(EV-01 RESERVED) 소터#1 비활성화 → 백엔드 거부 → **강제 비활성화 다이얼로그 자동 체이닝**("진행 중 작업 포함 강제" 문구, 10.png) → 강제 성공(isActive=false) → **비활성 목적지 IF-05 NG 실증**(EV-01 재질의 → NG) → UI 재활성화 성공. 거부·강제 전수 WARN 감사.
- **OQ-3 시작 오더 차단**: IF-05로 예약(1/0)된 EV-01~03 — UI 할당됨 탭 **재배정/해제 버튼 disabled**(11.png) + API 직접 호출도 F(`"진행 중 오더는 재배정할 수 없습니다(예약 1·분류 0·활성 피스 있음)"` / unassign 동형) — UI 우회 불가(가드 tx 내).
- **OQ-1 셀 벌크**: 5×4 재실행 → `created 0·updated 0·total 20`(멱등). 2×2 축소 → **기존 20셀 보존**(`total 20`·삭제 0 — DTO 문서와 일치).
- **reset 이관 + refuse 체인(UI)**: 설비 페이지 소터 행 초기화 → 범위·비가역 고지 다이얼로그 → in-flight 거부 → **강제 초기화 체이닝 확인 후 취소**(12.png — force 실행 경로는 B2cFacilityApiTests reset E2E가 잠금: 예약→거부→force→재예약·하드삭제 0).
- **에러 케이스**: 중복 chuteNo → 200 F("이미 존재(CHUTE)") · CHUTE+cellNo → F · 미존재 오더/목적지 → F · **점유 셀 배정 → F**("다른 오더가 점유 중" — 부분 유니크 준수) · workDate 400 · barcodePrefix 인젝션 400 · plannedQty 1001 → 400 · chuteNo=0 → 400(Fail 형상 — allowlist 동작) · rows=0 → 400 · **ops blank operator → 400**.

### 5. 목적지 열거 API (A2)
- `GET /api/monitor/destinations` → CHUTE+SORTER_3D 전 9건, 형상 = id/chuteNo/destType/floor/status/isActive + online/ready/full/paused + CHUTE workFullQty/lastClearedAt + 소터 cellTotal/cellEnabled(타입별 null 상보). 빈 DB에서 `[]`(빈 배열) 확인. 부수효과 0(AsNoTracking — 코드 확인).

### 6. 감사 전수 (operation_log STATE)
`B2C_DEST_CREATE` INFO×9(소터1+슈트2~9)+WARN×1(중복 거부) · `B2C_DEST_DEACTIVATE` INFO×6+**WARN×2(거부·강제)** · `B2C_DEST_ACTIVATE` INFO×7 · `B2C_GENERATE` INFO×2 · `B2C_ORDER_ASSIGN` INFO×3+WARN×5(거부·미존재) · `B2C_ORDER_UNASSIGN` WARN×1 · `B2C_SORTER_CELLS` INFO×3 · `B2C_RESET` WARN×1(거부) · OPS_PAUSE/RESUME/CLEAR + PAUSED/NORMAL 전이. 실패/거부 전수 기록 확인. 한글 detail 무결(U+FFFD 0건 — 표시 깨짐은 평가자 콘솔 cp949 파이프라인 아티팩트로 판명).

### 7. 콘솔 (BLOCKING 규칙)
- 기능 세션(재기동 이후 04:57~종료) **error/warning/pageerror 0**. React dev-mode 경고 0.
- 유일한 에러군 = 평가자의 계획된 백엔드 재기동 창(04:56:26~31, ERR_CONNECTION_REFUSED + SignalR negotiate — 인프라, 앱 결함 아님, 재접속 자동 회복). console.log의 04:41~04:42 :5216행은 생성자 세션 잔재(브라우저 프로필 공유 — all:true 캡처).

---

## 판단 요청 회신 (Generator → Evaluator)

- **destination_event 기존 enum 재사용**(CREATE→FULL_QTY_CHANGED·활성전이→CLOSED, detail JSON 구분): **수용**. 마이그레이션 0 제약 하 합리적 절충 — operation_log STATE가 완전한 정본이고 detail JSON(`{"action":"CREATE",…}`/`{"isActive":…,"force":…}`)으로 구분 가능함을 실데이터로 확인. 단 이벤트 통계·조회 시 의미 혼선 소지 → **후속 스프린트에서 enum 확장(+양 provider 마이그레이션) 권고**(Minor #5, 최종 PASS 시 todo 등재).

## Minor (비차단 — 최종 PASS 시 todo.md 등재 예정)

1. **DENIED 활성 피스가 OQ-3 가드를 영구 차단** — 실측: NO_DEST 거부(pId 9007)만 받은 EV-05(예약 0·분류 0)가 이후 수동 배정 불가("활성 피스 있음" F, UI 배정 disabled). `RecordDenied`가 piece를 IsActive=true로 영속(DbRepositories.cs L270) + `OrderProgressAsync`의 HasActivePiece가 status 무관 카운트. **게이트 문언("피스 이력 0")과는 합치**하므로 계약 위반 아님 — 그러나 "RCS 선질의 NG → 운영자 수동 배정" 복구 시나리오가 dead-end(소터 reset도 destination=null 피스는 못 푼다). 사용자 게이트 재확인 필요: DENIED 제외 여부. UI도 disabled 사유 무표시(툴팁 권고).
2. 비활성 소터 행에 `정지` 배지+`재개` 버튼 노출(readiness paused=true 파생, status=NORMAL) — 운영자 혼동 소지(재개 눌러도 활성화 안 됨).
3. facility API `operatorName` 공백 허용("(unspecified)" 대체) — UI만 필수 게이트. /api/ops는 400 강제와 비대칭. 서버측 Required 보강 권고(감사 귀속 방어심층).
4. `GetBatchesAsync` 배치당 3쿼리 N+1(take 상한 있음·관리 화면 — 저위험, 기존 GetSummaryAsync #4와 동류).
5. destination_event enum 확장 후속(위 판단 요청 회신 참조).

## 참고 (환경·재검증 노트)

① **가짜 UpdateChuteState 수신 서버는 반드시 `{"flag":1}` ack** — `ChuteStatePushClient.IsSuccessBody` 계약(2xx+flag==1). 평가자 초기 `{"result":"OK"}` ack 시 전 슈트가 재시도+미동기-하트비트 재발신 루프(539줄/8분, phase1-badack.log) — **구현 결함 아님**(S-HARDENING-1 자율 복구가 설계대로 동작함을 역증명). ack 교정 후 "전이당 정확히 1회" 확인.
② 소터 push-ready는 세션 내내 false(Sim3ds 초기 Ready 워드 0) — IF-05 dispatch는 무관(분리 채널·설계 일치).
③ 평가자 실행 잔재: `screenshots/S-B2C-FACILITY_20260714-044726/`(증거·보존). Evaluator 코드 수정 0·커밋/푸시 0. 사용자 DB·Azure·실 PLC/COM1 무접촉.

## Code Review Pass (Step 4.5 — 독립 리뷰 + fix iter 3, 2026-07-14)

**최종: Ready to merge = Yes** (초판 "With fixes" → Important 2건 fix 후 Evaluator 재검증 APPROVED 유지).

강점(리뷰): 런타임 등록 순서(DB commit→capacity→pusher)·멱등성·push 레지스트리 ConcurrentDictionary 동시성·
가드 트랜잭션 in-tx(TOCTOU 규율)·DENIED 일관성(가드=프로젝션)·null-safe 소터·DI(concrete singleton) 전부 견실.

- **[해소] Important #1 — 감사 작업자 귀속**: Audit가 `{`-prefixed JSON detail에서 op 드롭 → MergeOp로 전
  detail(성공·거부) 병합. 라이브 확인(op DB 정확 저장). iter-1 "한글 깨짐"은 콘솔 cp949 아티팩트로 확정.
- **[해소] Important #2 — 비활성화 IF-08 일관성**: ApplyActiveStateInMemory 신설(인메모리 IsActive+OnChuteStateChanged,
  단일-emit 보존) → 비활성 push[2] 1건·재활성 push[3]. 회귀잠금 테스트 추가.

### Minor (비블로킹 — 다음 sprint Generator 참고 / todo 등재)
1. 재배정이 ConfirmDialog 미게이트(AssignOrderDialog 폼 — unassign은 게이트됨). §U3 확인셋 이탈.
2. 변경 다이얼로그(create/edit/cell/assign) 공백 operatorName 허용(requireOperator 미적용) — OpsController는 거부, 불일치.
3. GetBatchesAsync N+1(배치당 3 CountAsync) — GroupBy 1쿼리로 축약 후보. (GetSummaryAsync N+1도 병존.)
4. destination_event enum에 CELL_CONFIG/ASSIGN/UNASSIGN 부재 → operation_log가 해당 감사 정본(후속 enum 확장 시 destination_event 병기).
5. 소터 신설 재기동 경고 문구가 "appsettings 없이 재기동 시 부팅 실패(fail-loud)" 결과 미고지.
6. useDestinations abort signal 부재 / facility Audit의 sorterChuteNo 컬럼명 오칭(CHUTE에도 사용).
7. 비활성 소터 UI: 정지 배지+재개 버튼 병기 혼동(Evaluator Minor).
