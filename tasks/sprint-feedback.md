# Sprint Feedback — S-B2C-GRID-UX (랜딩 정합 + 공용 그리드 상호작용)

**APPROVED (FIX ITER 2 재검증 · 유지)** — Evaluator, 2026-07-15. Step 4.5 코드리뷰 3건(Important 1 + fold 2) 완전 해소·회귀 0·브라우저 실증·콘솔 0. 초회 APPROVED 아래 보존.

---

## FIX ITER 2 재검증 (Step 4.5 코드리뷰 3건 · PASS) — 2026-07-15

핸드오프 마커 = `tasks/sprint-log.md` L3705 말단 `## IMPLEMENTATION COMPLETE — FIX ITER 2 (S-B2C-GRID-UX · Step 4.5 코드리뷰 3건)`. HEAD=`9c6f63a`(불변·미커밋). 변경 파일 = `useRowSelection.ts`(FIX1·2·3) + `B2cFacilityPage.tsx`(FIX2 소터행 결선). 검증 환경(격리): Sim3ds :1512 · Wcs.Api :5215(Release·fresh scratch SQLite `wcs-eval2.db`·seed·Sorters[0].ChuteNo=30) · Vite :5190. 증거 `screenshots/S-B2C-GRID-UX_20260715-121800/07-fixiter2-g3-drag-highlight-menu.png` + console-fixiter2.log.

### 정적/회귀/무접촉 (PASS)
- tsc 0 · eslint 0 · vite build 0(스크래치 outDir·wwwroot 무접촉). `git diff backend/` 빈 출력 · 신규 마이그레이션 0.
- `dotnet test -c Release`: **run1 = 실패 2/통과 358**(`S5RSeqMismatchTests.S5_RSeqMismatch_AlarmAndSorterCommandMismatch` 외 1) → **clean 재run = 360/360 GREEN(실패 0)**. 귀속: (1) 백엔드 byte-identical(diff 0)이라 프론트 변경이 백엔드 Modbus 시나리오 테스트를 논리적으로 깨뜨릴 수 없음, (2) S5 는 타이밍-취약 핸드셰이크 시나리오(lessons: S9 flake·testhost-teardown·e2e-parallel-load), run1 시 잔여 testhost 프로세스 2개 존재. → **비결정 flake, 회귀 아님**.

### FIX 1 (Important) — 전역 stuck user-select 방지 (브라우저 실증 · PASS)
- 코드: `beginDragVisual`/`endDragVisual` 를 `dragVisualRef` 로 멱등화 + `onMove` 에 `e.buttons===0 → onUp()` 조기 종료.
- 실증(합성 PointerEvent, G3): (a) 정상 드래그 중 `body.userSelect='none'` → **창 밖 릴리스 누락(pointerup 미발화) 후 buttons=0 재진입 이동 → '' 복원**(before '' / during 'none' / after ''). (b) 회귀 근원 경로: stuck('none') 상태에서 **복구 없이 새 드래그 → clean pointerup → '' 복원**(멱등 begin 이 오염된 'none' 을 prevUserSelect 로 재저장하지 않음 — verdict "RESTORED, no corruption"). "리로드까지 영구 잠김" 제거 확정.

### FIX 2 (fold) — 소터 펼침 행 드래그-후 오발화 방지 (브라우저 실증 · PASS)
- 코드: 선택-`draggedRef` 재사용(stale 회귀) 폐기 → 자기 이동거리 기반 `expandableRowProps`(pointerdown 좌표 기록 → click 이동 ≥4px 면 억제) 신설, `B2cFacilityPage` 소터 헤더행에 결선.
- 실증(assign-panel 소터 헤더행 정확 타겟팅): (A) **타행(chute) 드래그 직후 소터행 정당 클릭 → 펼침(3셀)** — 생성자가 자가-발견한 stale-flag 오억제 회귀 수정 확인. (B) **소터행에서 드래그 → 토글 안 됨(3셀 유지)**. (C) 드래그 직후 소터행 정당 클릭 → 접힘(0셀). 정당 클릭은 항상 토글·실제 드래그만 억제.

### FIX 3 (fold) — "전체 해제" 완결성 (브라우저 실증 · PASS)
- 코드: `uncheckAll` = `setChecked(new Set())`(소유 Set 전량 클리어). `checkAll` 은 렌더+자격 유지(불변).
- 실증(G2): ①전체 선택(8=활성슈트5+enabled셀3, chute:5 제외) → **소터 접기(셀 DOM 0·`선택 대상` 카운트 8 유지=접힌 셀 Set 잔존 재현)** → **전체 해제 → 선택 대상 0** → 재펼침 시 3셀 전부 uncheck. 접힌 소터 셀까지 완전 클리어 확정.

### 회귀 (핵심 UX 무손상 · PASS)
- 드래그 범위 하이라이트(G3 3행 7·8·9 연속) → 우클릭 ③ 선택된 행 체크 = **하이라이트와 정확 일치**(match). 메뉴 4항목·자격 존중(전체선택(9)·chute:5 제외)·Escape 닫힘 정상.
- 콘솔: 내 origin(:5190) error/warning **0**(현재 네비 0/0, 전세션 덤프 180 error/warning 전부 :5191 foreign-origin — 불산입).

### 미접촉 (지시대로)
- 나머지 2 Minor(ContextMenu Tab 처리·컨테이너 tabIndex/role)는 조정자 백로그 지시대로 미접촉 — 확인. 비차단.

---

## 초회 APPROVED (S-B2C-GRID-UX 전체) — Evaluator, 2026-07-15. 순수 프론트 스프린트. 정적/회귀 0, 백엔드 diff 0·마이그레이션 0, 브라우저 클릭스루로 R1~R4 및 V1~V9 전 시나리오 실증, 내 origin 콘솔 0.

핸드오프 마커 = `tasks/sprint-log.md` 말단 `## IMPLEMENTATION COMPLETE — S-B2C-GRID-UX`. HEAD=`9c6f63a`(PR #64 병합 위 미커밋 작업트리). 브랜치 `feat/b2c-grid-ux`.
검증 환경(격리): Sim3ds :1512(TCP) · Wcs.Api :5215(Release·fresh scratch SQLite `wcs-eval.db`·SeedOnStartup·Sorters[0].ChuteNo=30 소터=Sim :1512) · Vite :5190(VITE_API_TARGET=:5215). 포트: 5205/1502 미사용. 증거 `screenshots/S-B2C-GRID-UX_20260715-121800/`.

---

## 정적 / 회귀 / 무접촉 게이트 (fresh·격리 · PASS)

- **tsc --noEmit = 0** (frontend, fresh).
- **eslint . = 0** (warning 포함 0).
- **vite build = 0** — 스크래치 outDir(`scratchpad/vite-out`)로 산출, `backend/src/Wcs.Api/wwwroot` **무접촉**(`git status wwwroot` 빈 출력 재확인). 1851 modules, exit 0.
- **dotnet test -c Release = 360/360 GREEN** (실패 0·건너뜀 0·21s). 경고=선재 NU1903(SQLitePCLRaw) 뿐 — 회귀 0.
- **무접촉 경계**: `git diff --stat backend/` 빈 출력 · `git status --short backend/` 빈 출력 → 백엔드/서비스/스키마 diff 0, **신규 마이그레이션 0**(순수 프론트 계약 준수). Wcs.PlcGateway/Wcs.Core 무접촉.

## 재사용 / 아키텍처 (코드 판독 · PASS · 사용자 R3 핵심)

- 드래그/메뉴 로직이 **단일 훅 `frontend/src/lib/useRowSelection.ts` + 단일 프리미티브 `frontend/src/components/ui/context-menu.tsx`** 로 통일. 5개 그리드 전부 소비만(중복 로직 0).
- 각 그리드가 **자기 체크 Set 계속 소유**(브리지 패턴): G1 `setChecked`(number) · G2 `setCheckedTargets`(string) · G3 `setCheckedOrders`(number) · b2b `setSummaryChecked`(string)·`setDetailChecked`(number). 체크 모델 재작성 0(최소 침습).
- 기존 S-B2B-2c 페인트-선택(드래그/Shift/Ctrl·`onSelectExact`/`selectDetailExact`) 통합 모델로 **완전 제거**(잔존 참조 0 — grep 확인, tsc 0).
- DOM 기반(`data-rsid`/`data-rseligible`) 설계 → 지연 로딩 셀(G2 소터) 자동 포함·OQ-1 로드행 한정 구조 충족. 이벤트 정리: window 리스너 useEffect cleanup + MutationObserver 콜백 ref 대칭 해제(누수 0). react-refresh 규칙 청정(훅 파일=훅만, 컴포넌트 파일=컴포넌트만).

## 브라우저 클릭스루 — 전 시나리오 (fresh evidence · PASS)

- **V1 랜딩**: `/`→(b2c)`/b2c/test-data`(데이터 생성·G1 3배치 렌더) · B2B 탭→`/data-generator` · b2b 모드 `/`→`/data-generator`(ModeHome). `homePathFor` 단일 소스 3경로 정합. `[01-v1-b2c-landing-datagen.png]`
- **V4 컨텍스트 메뉴 4항목 — 5개 그리드 전부**: aria-label 별 확인 — b2b `배치 요약 메뉴`·`상세 바코드 메뉴` / G1 `생성 결과 배치 메뉴` / G2 `배정 대상 메뉴` / G3 `미할당 오더 메뉴`. 첫 활성항목 자동 포커스([active]), 하이라이트 0이면 ③④ disabled. `[02·06]`
- **V3 드래그 범위 하이라이트**: 실 PointerEvent(down→move>임계→move) — b2b Detail 4행(idx1..4=id 2,1,8,6)·G1 2행·G3 3행 연속 하이라이트. **체크와 시각/상태 분리**(하이라이트 시 checked=[]). 드래그 중 `document.body.style.userSelect='none'`, pointerup 후 `''` 복원(OQ-2). `[03-b2b-detail-drag-highlight-4rows.png]`
- **V5 메뉴 액션 + 자격**: ③선택행 체크(b2b Detail·G3) = 하이라이트 집합과 **정확히 일치**. ①전체 선택(G1·b2b Summary) = 전 행 체크 + 헤더 체크박스 on. **G2 자격 존중**: 비활성 chute:5(deactivate로 준비, `data-rseligible=0`·개별 체크박스 disabled)는 `전체 선택 (5)`에서 **제외**(chute 1,2,3,4,7만 체크, chute:5 미체크). `[04-g2-selectall-skips-inactive-chute.png]`
- **V8 공존**: G1 행 클릭→디테일 로드(EVAL-A 바코드) 정상 · **드래그 후 click 억제**(드래그로 하이라이트해도 디테일 미로드) · G2 소터 행 클릭→셀 3개 펼침(cell:6:1~3, 지연 렌더가 선택에 자동 참여) · 기존 액션 버튼 카운트 반영(G1 `초기화 (3)`·b2b `수신 초기화 (2)`·G3 `배정 (3)`).
- **V9 교차레이어**: G3 드래그+③로 오더 3건 체크 → 배정 → `/api/b2c/facility/orders/assign` 왕복 → **미할당 19→16·배정 4→7**(API 재확인). refetch 후 사라진 오더의 하이라이트·체크 **prune**(OQ-4). (참고: 배정은 기존 `작업자 이름` 필수 게이트 — 공백 시 정상 차단, 회귀 아님.) `[05-v9-facility-after-assign.png]`
- **V6 빈 그리드**: 배치 0(bizDay 2020-01-01) b2b Summary 우클릭 → 메뉴 4항목(③④ disabled)·`전체 선택` no-op(행 0·크래시 0).
- **OQ-3**: 그리드 본문 밖 우클릭 = `contextmenu` defaultPrevented=false·커스텀 메뉴 미개방(네이티브 유지). 본문 안은 메뉴 개방(대체). → 본문 한정 확정.
- **OQ-4**: 필터 변경(b2b Detail 바코드 필터 입력) → 하이라이트 전체 리셋([]), 체크는 유지. refetch(V9) → 사라진 id prune. → id-키 정책 확정.
- **a11y**: 메뉴 role=menu/menuitem, 첫 항목 포커스, disabled 항목 포커스/실행 제외, **Escape로 닫힘**(실증). R4 충족.
- **V7 다크모드 = N/A**(단일 라이트 테마) — 라이트에서 하이라이트(teal 좌측 바)/체크(파랑) 대비 양호(스크린샷 확인).

## 콘솔 (BLOCKING · PASS)

- **내 origin(:5190) = error/warning 0**. 현재 네비게이션 쿼리 0/0, 전 세션 덤프(`console-all.log`) 168 error/warning **전부 `:5191`(생성자 포트) 참조 = foreign-origin 잔류**(영속 브라우저 프로필). 계약상 타 포트 잔류는 불산입.
- 생성자가 보고한 hook-order `TypeError`(`updateCallback`→`useRowSelection.ts:262`)는 스택이 `localhost:5191`(생성자 HMR live-edit) — **fresh load 아님**. 나는 동일 컴포넌트(B2cDataGenPage/useRowSelection)를 :5190 fresh 로드로 드래그·클릭·메뉴·전체선택까지 집중 조작했고 error 0. HMR Fast-Refresh 아티팩트로 귀속(운영/신규마운트 무영향).

---

## Minor (비차단 — todo 등록 권고, 후속)

- (없음 — 계약 요구/게이트 전부 충족. 후속 강화 아이디어: G2/G3 useRowSelection 에 resetKey 미전달 — 현재 route unmount + refetch prune 으로 충분하나, 스코프 필터가 추가되면 명시 resetKey 검토.)

## Code Review Pass (Step 4.5 — 독립 리뷰 + fix iter 2, 2026-07-15)

**최종: Ready to merge = Yes** (초판 "With fixes" → Important 1 + Minor 2 fix 후 Evaluator 재검증 APPROVED 유지).

강점(리뷰): 단일 useRowSelection 훅 + 단일 ContextMenu 프리미티브가 5그리드에 중복 0으로 결선(DOM-driven
data-rsid/rseligible로 lazy 소터 셀도 자동 포섭), 리스너/MutationObserver 누수 0·attr 미관찰로 피드백루프
차단·1000행 O(n) prune, 클릭↔드래그 억제·좌우버튼 분리 견고, ContextMenu portal/clamp/dismiss/키보드 완비,
랜딩 단일소스·리다이렉트 루프 없음, b2b paint-select 잔재 0.

- **[해소] Important — 창밖 드래그 해제 시 userSelect 전역 고착**: onMove e.buttons===0 미스드릴리스 감지 +
  beginDragVisual 멱등(dragVisualRef) — 손상값 재저장 방지. OQ-2 자기 관심사.
- **[해소] Minor — 소터 부모행 드래그 종료 오펼침**: movement-based 가드(expandableRowProps). 첫 시도의
  stale-flag 회귀(진짜 클릭 오억제)를 브라우저 테스트로 잡아 이동거리 기반으로 재설계.
- **[해소] Minor — "전체 해제" 접힌 셀 잔존**: uncheckAll이 Set 전체 클리어(checkAll은 렌더+eligible 유지).

### Minor (비블로킹 — 다음 sprint / todo 등재)
1. ContextMenu Tab 미처리(Escape/Arrow/Home/End만) — Tab 시 메뉴 뒤로 포커스 이동. 표준 메뉴 close/trap 폴리시.
2. 그리드 컨테이너 tabIndex/role 부재 — 키보드 메뉴열기(Shift+F10)가 자식 체크박스 포커스 시에만 동작. R4 최소요건은 충족.
