# Sprint Feedback — S-B2B-2c (A4 라벨 인쇄 + 고급 포인터 다중선택 + 설정 페이지 부활)

**APPROVED** — Evaluator, 2026-07-08 (1 iteration to pass).

브랜치 `feat/b2b-2c-print-select-settings` (working tree, 미커밋). Evaluator 는 코드를 고치지 않음(read-only 검증).
Ground truth = git HEAD/status + 실제 코드 판독 + 실 stack 브라우저 클릭스루. Generator 요약은 신뢰하지 않고 전부 독립 재현.

핸드오프 확인: `tasks/sprint-log.md` L2853 에 `## IMPLEMENTATION COMPLETE — S-B2B-2c` 마커 존재 → 활성화 정당.

---

## FIX ITER 1 재검증 (code-review #1 print-scope + #2 modal a11y) — **APPROVED 유지** (2026-07-08)

Generator fix-only 1 iter(2 파일). Ground truth 로 재검증(주장 미신뢰). `## FIX ITER 1 — S-B2B-2c` 마커 sprint-log L2901.

- **fix 범위 확정**: `git diff --numstat develop` — tracked 변경분은 `index.css`(37→39 +2)만 갱신, 나머지 7 tracked 파일 numstat 불변 · `PrintLabelPreview.tsx`(untracked) in-place 수정 · **backend diff 여전히 EMPTY**. Minor 항목 미접촉(fix-only 준수).
- **정적 재실행**: tsc 0 · eslint 0/0 · vite build 0(선재 2경고만).
- **FIX #1 (핵심·앱 전역 인쇄 회귀 해소)** — `page.emulateMedia({media:'print'})` 실측:
  - 오버레이 **닫힘** 상태에서 `/logs`·`/data-generator`·`/monitor` 전부 `body.has-print-overlay` **클래스 부재** + `#root` **display:block(가시)** → 네이티브 Ctrl+P 가 더 이상 빈 시트로 인쇄 안 함(회귀 제거). (수정 전 무조건 `body>*{display:none}` 이 앱 전역을 숨겼음.)
  - 오버레이 **열림** + print emulation → 클래스 present · `#root` **display:none** · `.print-overlay` **display:block** · 툴바 chrome display:none · 라벨 11 → 인쇄 시 라벨 문서만 정확히 출력.
  - **닫기 후 클래스 제거(누수 0)** + `#root` 다시 display:block + 오버레이 unmount. CSSOM 규칙: `body.has-print-overlay > *:not(.print-overlay){display:none}` 로 게이트됨(무조건 `body>*` 억제 0).
- **FIX #2 (모달 a11y)** — 실측: overlay `role="dialog"` · `aria-modal="true"` · `aria-label="라벨 인쇄 미리보기"`. 열림 시 포커스가 모달 내부(툴바 `인쇄` 버튼)로 이동 · **Tab×6 / Shift+Tab×3 모두 포커스 오버레이 내부 유지(트랩)** · **Escape 닫기 → 포커스 트리거("인쇄 (11)") 복원** + 스크롤락 해제. onClose ref 안정화(effect deps=[open]).
- **무회귀 재확인**: 인쇄 DOM 2페이지(8+3)·grid 2×374.688px·라벨 374.7×255px==99.14×67.48mm·dual 6(SVG2)/single 5(SVG1)·CODE128 막대 29 그대로. 네트워크 external **0** · JsBarcode 로컬(`/node_modules/.vite/deps/jsbarcode.js`). 콘솔(내비 후 since-nav) error 0/warning 0.
- 산물 정리: 브라우저 close · :5205/:5190 kill(foreign 5173/5174 미접촉) · scratch DB 삭제 · git status 핸드오프 원복. 스크린샷 `09-fixiter-print-modal-a11y-reverified.png`.

→ **결론: 두 fix 모두 정상 반영 · 신규 결함 0 · 기존 W1~W7 무회귀. APPROVED 유지.**

---

## 검증 환경 (fresh)

- 백엔드: `dotnet run --project backend/src/Wcs.Api --no-build -c Debug`, **`ASPNETCORE_ENVIRONMENT=Production`**,
  **별도 scratch SQLite**(`scratchpad/wcs-eval-2c.db` — Generator 파일명과 다름 · DbInitializer 콜드스타트 자동 Migrate 로 스키마 생성).
  `Database__Provider=Sqlite` · `ConnectionStrings__WcsDb=Data Source=<scratch>` · `Database__SeedOnStartup=false`(로그로 "시드 게이트 off" 확인 · 자동 시드 0) ·
  **`Sorters__0__Transport=Tcp`**(현장 DB `Rcs3dsInterlockingWcs` 무접촉 · COM1/RTU 실 3DS PLC 무접촉 — lessons 2026-07-03/현장 PLC 준수). :5205 listen, `/health`=`{"status":"ok","db":true,"sorters":[]}`.
- 프론트: Vite dev **`:5190 --strictPort`**(내 인스턴스). ⚠ 포트 노트: 계약의 source-of-truth 는 5173 이나 **선재 stale foreign vite 가 5173/5174 점유**(Generator 사전 경고) → false-PASS 방지 위해 내 전용 포트를 명시 기동. proxy 대상(5205)은 vite.config 소유라 listen 포트와 무관. 이 스프린트 코드가 서빙됨을 proxy 왕복(batch 900)으로 확인.
- 시드: `test_data` 11행(batch 900, bizDay 2026-07-08) — **6 dual(barcode+barcode2) + 5 single(barcode2=NULL)**. `GET /api/test-data/detail` 11행 정확 반환(6 barcode2 보유).

## 정적 검사 (독립 재실행 · raw)

- `npm run typecheck`(tsc --noEmit) → **exit 0**.
- `npm run lint`(eslint .) → **exit 0, 0 warning**.
- `npm run build`(tsc+vite) → **exit 0**. 잔존 경고 2종(@microsoft/signalr `/*#__PURE__*/`, chunk>500kB=597.50kB)은 **선재 부채**(develop 번들도 이미 >500kB=517kB, F2 signalr — jsbarcode +80kB 이나 임계는 이미 초과) → 이번 스프린트 미도입.
- `dotnet build backend/Wcs.sln` → **경고 0 / 오류 0**(NuGetAudit=false 로 NU1903 선재부채 격리).
- 프론트 테스트 스크립트 없음(dev/build/typecheck/lint/preview only) → 재실행 대상 없음.

## Verification Scenarios (W1~W7 · 전부 fresh evidence + 스크린샷)

- **W1** default state: 배치 900 선택 → 상세 11행 로드, 인쇄/삭제 버튼 **disabled**(detailChecked=0), 체크박스/전체선택 정상. `01-*.png`.
- **W2** 설정 nav 점등: b2b nav `설정`→`/settings`(cursor-pointer·B2B-3 배지 없음). 설정 폼 기본값 = symbology **CODE128**, preset **a4-2x4**(대체 3종 disabled), 값표시 **on**. **자동생성 UI 0건**(autoGenTerms=[]). localStorage `wcs.print` 마운트 시 기록. `05-*.png`.
- **W3** 고급 선택(실 마우스 이벤트 — page.mouse steps):
  - drag 행2→5 → 연속 {BC-0002..0005}=4, 인쇄(4) enabled. `02-*.png`(하이라이트+배지 육안 확인).
  - Shift+click(anchor 행2 → 행8) → 연속 {BC-0002..0008}=7. `03-*.png`. (초기 실패는 대상 행이 뷰포트 밖 → 뷰포트 1400×1900 확대 후 재현 = 코드 결함 아님, 계측으로 shiftKey 전달 확인.)
  - Ctrl+click 누적: [BC-0001]→+0004→+0006(비연속)→0004 재클릭 toggle-off = {BC-0001,BC-0006}.
  - 병존: 헤더 전체선택 → 11 · 개별 체크박스 해제 11→10(동일 detailChecked 단일소스, 제스처와 충돌 0) · 체크박스 클릭이 pointer 제스처 유발 안 함(closest 가드 작동).
  - **필터-숨김 제외(B5)**: 바코드필터 'BC-0010' → 보이는 1행, 전체선택-보이는 토글이 **보이는 행만** 조작(숨은 9 checked 보존 → 필터 해제 시 복원). 보이는 행 기준 확정.
- **W4** 인쇄 프리뷰(11 선택 · DOM+스크린샷): **2 페이지 · 8+3 분할**(data-page 1/2). grid `374.688px 374.688px`(2열=99.14mm), columnGap 7.37px, 라벨 **374.7×255px == 99.14×67.48mm 정확 일치**. `@page { size:a4; margin:0px }` + `body>* display:none` + `body>.print-overlay display:block` + `.print-page break-after:page` 실재(CSSOM 덤프). **dual 6(SVG 2개·실 JsBarcode 막대 29/32 rect) + single 5(SVG 1개)**, aria-label `바코드 BC-…`/`바코드 LOT-…`, chuteNo **3자리 zero-pad(슈트 001…011)**, invalid 0. `04-*.png`(육안: CODE128 실 바코드+값텍스트).
- **W5** 설정 영속+반영: symbology CODE128→CODE39 시 미리보기 막대 [44,38]→[76,71] 변화 · 값표시 off 시 `<text>` [1,1]→[0,0]. **full reload 후 CODE39·off 유지**(localStorage `wcs.print`). 데이터 생성 복귀·재인쇄 → 프리뷰 헤더 "심볼로지 CODE39 · 값표시 OFF" + 라벨 막대 CODE39(46) + 값텍스트 0 → **설정→인쇄 단일소스 관통 확인**. `06-*.png`.
- **W6** 빈/오류: 0-선택 시 인쇄 버튼 disabled + force-click 해도 인쇄 뷰 안 뜸(가드 작동). 심볼로지 EAN13(비숫자 표본) → **크래시 0**, invalid 배지 2 + "인코딩 불가" 폴백, 페이지 정상 렌더. `07-*.png`. (0-선택 토스트 가드는 방어코드로 소스 확인 — 주 가드는 disabled 버튼.)
- **W7** 전체 흐름: b2b→데이터생성→배치선택→드래그선택→인쇄(dual 4·SVG 8)→설정 변경·저장→복귀·재인쇄 반영 = end-to-end 관통.

## BLOCKING 게이트

- **콘솔/pageerror**: 내 인스턴스(localhost:5190) — clean navigation 후 전체 흐름 구동 시 **error 0 / warning 0 / React dev 경고 0 / pageerror 0**(유일 출력 = benign React DevTools INFO). all=true 버퍼의 `localhost:5174` error 다수는 **선재 foreign 인스턴스**(내 탭은 5190 단독 · 5174 미방문 · Generator 사전 경고 stale) → 내 origin 아님, 게이트 무관.
- **폐쇄망(0 외부요청)**: performance resource 80건 **전부 same-origin localhost:5190**, **external 0**. JsBarcode = `localhost:5190/node_modules/.vite/deps/jsbarcode.js`(로컬 Vite dep, CDN 아님) · 폰트도 `@fontsource` 로컬. CSP `script-src 'self'` 호환.

## 무접촉 가드

- **백엔드 무접촉**: `git diff --numstat develop -- backend/` **빈 출력** · migration/ModelSnapshot 변경 0 · `dotnet build` 0/0.
- **B2C 회귀 0**: B2C 토글 → nav 원복(모니터링·3DS 워드 · 운영제어 F3 disabled), `/settings`·b2b 링크 부재, MonitorPage/StatusRail("등록된 소터 없음" = Tcp/무소터 정상 환경상태) 정상 렌더. `08-*.png`. Layout.tsx diff = b2b `설정` 항목 1줄만.
- **변경 표면**: 프론트 modified 8(App/Layout/index.css/main/DataGeneratorPage/DetailGrid/package·lock) + 신규 6(Barcode/PrintLabelPreview/PrintSettingsProvider/labelLayout/printSettings/SettingsPage + types/jsbarcode.d.ts). 백엔드·b2c 0.

## Completion Conditions (계약 §Completion 1~10)

1. build/lint/typecheck clean ✅ 2. dotnet build 0/0 ✅(백엔드 무접촉이라 test 회귀 구조적 0) 3. W1~W7 클릭스루+스크린샷 ✅ 4. A4 2×4/99.14×67.48·dual/single·chuteNo·8칸분할·0가드 ✅ 5. 드래그/Shift/Ctrl 3제스처+밖 mouseup+체크박스병존+필터제외 ✅ 6. 설정 점등·라우팅·reload 유지·인쇄 반영·자동생성 0 ✅ 7. 외부요청 0 ✅ 8. 콘솔 error/warning/React 0 ✅ 9. 백엔드 무접촉 diff 0 ✅ 10. B2C 무접촉 실측 ✅.

## Minor (비차단 — 다음 스프린트 Generator 참고, APPROVED 무관)

- 없음(신규 결함 0). 설계상 메모: 필터로 **숨겨진** 행이 이미 checked 이면 `printRows`(= 전체 detailRows 필터)에는 남아 인쇄 대상이 됨. 계약 B5 는 "새 선택 제스처의 대상에서 제외"를 요구하며 그건 충족(range=filtered, select-all=visibleIds). 기존 check 지속은 표준 동작 · 결함 아님.

## 검증 산물 정리

- 브라우저 close · 백엔드(:5205)/vite(:5190) kill(선재 5173/5174 foreign 미접촉) · scratch DB 삭제 · `.playwright-mcp`/`screenshots`/`wwwroot` 전부 gitignored 확인.
- 핸드오프 시점 `git status` 원복: modified 8(frontend)+contract/log, 신규 6(frontend)+types/ — 평가 산물 유출 0.
- 스크린샷: `screenshots/S-B2B-2c_20260708-215200/01~08-*.png`(덮어쓰기 없음).
