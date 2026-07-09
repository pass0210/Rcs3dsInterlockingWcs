# Sprint Feedback — S-F3b (B2C "운영 제어" 프론트엔드 · F3a Ops API 소비)

**APPROVED** — Evaluator, 2026-07-09 (1 iteration to pass).

브랜치 `feat/f3b-ops-ui`. 프론트엔드 전용. Evaluator 는 코드를 고치지 않음. Ground truth = git diff vs develop
+ 실제 코드 판독 + **격리 스택(내 전용 포트) 라이브 Playwright 클릭스루** + 독립 재실행. Generator 요약은 신뢰하지 않고 전부 독립 재현.

핸드오프 확인: `tasks/sprint-log.md` L3080 에 `## IMPLEMENTATION COMPLETE — S-F3b` 마커 존재(파일은 mixed-encoding 이라 rg -a 로 확인) → 활성화 정당.

Evaluation Dimensions(계약 선언 2차원): **functional-UX ∧ safety-surfacing — 둘 다 PASS**.

---

## 격리 검증 스택 (SAFETY S-1 — 유저 :5205 현장 테스트와 완전 분리)

유저는 :5205 에서 현장 DB(Rcs3dsInterlockingWcs) + 실 3DS PLC(COM1/RTU)로 자신의 필드 테스트 진행 중.
내 검증은 이와 충돌/오염 0 이도록 전용 포트·전용 provider 로 분리 기동:

- **Sim3ds TCP :1512**(NOT COM1/RTU) — 기동 로그 `Sim3ds 서버 기동 TCP 127.0.0.1:1512`.
- **Wcs.Api :5215**(NOT :5205) — `Urls` **config 키 직접 오버라이드**(base appsettings `"Urls":0.0.0.0:5205` 가 env ASPNETCORE_URLS 를 눌러 5205 바인드하는 함정 회피). 기동 로그 `Now listening on: http://127.0.0.1:5215`. ASPNETCORE_ENVIRONMENT=Production.
- **DB=스크래치 SQLite**(`Database:Provider=Sqlite` + scratchpad 전용 파일) — 기동 로그 `provider=Microsoft.EntityFrameworkCore.Sqlite, dataSource=...scratchpad/wcs-eval-scratch.db`. 현장 SqlServer `Rcs3dsInterlockingWcs` **미접근**(API 로그 전수 검사: "Rcs3dsInterlockingWcs" 매치 3건 전부 **repo 폴더명이 파일경로에 포함**된 것뿐 — SqlServer 연결 0, COM1/Rtu 0, :5205 바인드 0).
- **Transport=Tcp → Sim :1512**, Sorters:0:ChuteNo=30(시드 소터 매칭). 시드는 스크래치 SQLite 에만 주입(SeedOnStartup=true 는 provider=Sqlite override 하에서만 사용 — 현장 DB 오염 벡터 0, 2026-07-03 교훈 준수).
- **Vite dev :5194 --strictPort**(계약 예시 :5192 는 선재 foreign node 프로세스 점유 → false-PASS 회피 위해 이동, S-B2B-2c 교훈), proxy → 내 :5215. 왕복 확인: `GET :5194/api/monitor/sorters` → `[{destId:6,chuteNo:30,online:true,...}]`.
- **정리 완료**: 내 3 프로세스(5215/1512/5194) kill, 스크래치 DB 삭제, 포트 free, 고아 0. 유저 :5205 **무접촉**.

> vite.config.ts 는 proxy 타깃을 내 :5215 로 임시 편집 후 `git checkout` 으로 원복(추적파일·평가산물 유출 0). 최종 git status = Generator 산출물 그대로.

## [정적] 빌드/린트/테스트 — 독립 재실행 fresh evidence

- `npm run typecheck`(tsc --noEmit): **0 오류**(무출력).
- `npm run lint`(eslint .): **0 오류/경고**(무출력).
- `npx vite build`(스크래치 outDir — 유저가 서빙 중인 wwwroot 무접촉): **✓ built in 7.08s**. 경고 2종(signalr `/*#__PURE__*/` annotation·chunk>500kB)은 **선재 라이브러리 수준**(이번 스프린트 신규 의존 0 → 도입분 아님, feedback-archive 기 확인).
- `dotnet test backend/Wcs.sln -c Release`: **통과! 실패 0, 통과 305, 건너뜀 0**(20s). 백엔드 무변경 baseline 유지. 경고 10건 전부 선재 NU1903(SQLitePCLRaw). (Release 로 빌드해 유저의 Debug bin 파일잠금 회피 — MSB3021 0.)

## [30%] 기능 완결(소터 제어) — 라이브 Playwright 네트워크 관찰

`window.fetch` 인터셉터로 **각 컨트롤의 실 요청 body 캡처**(operatorName 동반 실증):

| 컨트롤 | 요청 | status | requestBody(캡처) |
|---|---|---|---|
| Pause(O2) | POST /api/ops/destinations/6/pause | 200 | `{"operatorName":"평가담당자"}` |
| Resume(O3) | POST /api/ops/destinations/6/resume | 200 | `{"operatorName":"평가담당자"}` |
| SetTgtFloor(O4) | POST /api/ops/sorters/6/tgtfloor | 200 | `{"floor":5,"operatorName":"평가담당자"}` |
| SetTgtFloor(O4·핑퐁) | POST .../tgtfloor | 200 | `{"floor":7,"operatorName":"평가담당자"}` |
| Clear-R(O5) | POST /api/ops/sorters/6/clear-r | 200 | `{"operatorName":"평가담당자"}` |
| Cell-Assign(O6) | POST /api/ops/sorters/6/cell-assign | 200 | `{"cellNo":10,"seq":3,"operatorName":"평가담당자"}` |

- **내비·라우팅**: b2c "운영 제어" **점등·클릭 가능**(`/ops`, cursor=pointer, [active]) → 헤더 "운영 제어" + subtitle 반영(01·02 스크린샷).
- **확인 다이얼로그**: 각 조작이 `ui/dialog.tsx` `Dialog` 재사용 다이얼로그를 열고 — **포커스 트랩**(operator 필드 [active] = open 시 포커스 진입), **Esc 닫힘 + 트리거 포커스 복원**(dialogOpen=false, activeEl=BUTTON), busy 잠금.
- **작업자 이름 게이트**: 공백이면 확인 버튼 **[disabled]** + 인라인 "필수" 안내(03) = F3a 400 클라 미러. 첫 입력 후 **세션 기억 프리필**(resume·tgtfloor·clear-r·cell-assign 다이얼로그 전부 "평가담당자" 프리필, 05) — localStorage 영속화 없음(Q2 정합).
- **클라 범위 검증(선제 400 미러)**: floor=99 → 다이얼로그 미개봉·warning 토스트 "목표층은 20 이하…"(06)·**네트워크 요청 0**(인터셉터 empty). cellNo=99999 → 동일 클라 차단·cell-assign 호출 0(10).

## [25%] 안전·정직 표면화(하드) — 전부 PASS

- **pingPongGuard 정직 표면화**: TgtFloor=0 상태 floor=5 쓰기 → 응답 `pingPongGuard:false` → 성공 토스트, D6 라이브 5. 이어 D6=5(≠0) 상태 floor=7 쓰기 → 다이얼로그 **사전경고**("⚠ 현재 TgtFloor=5(진행 중) — 컨슈머 핑퐁 차단으로 스킵될 수 있습니다", 07) → 응답 `pingPongGuard:true,currentTgtFloor:5` → 코드가 **success 아닌 warning 토스트**로 분기(성공 위장 0). D6 는 5 유지(가드로 7 스킵 = 정합).
- **에러 표면화(삼키기 0)**: 백엔드 계약 curl 독립 확인 — blank operator→**400** `{error:operatorName…}`, floor=99→**400** `{error:floor는 20 이하…}`, 미등록 destId 999→**404**, 이미-정지 재pause→**AlreadyInState**. UI 코드는 400→warning·404 등→error·409→warning("재시도")·AlreadyInState→info("이미 …상태(변경 없음)") 로 **일률 토스트 표면화**(토스트 렌더 메커니즘은 06 에서 실렌더 확인).
- **파괴적 조작 강경고**: Clear-R 다이얼로그 danger 아이콘+빨강 "⚠ 진단 전용 — 핸드셰이크(C/R) 오염 가능"(09). Cell-Assign danger "⚠ 고위험 진단 — IF-10 핸드셰이크가 수행… 셀 회계·핸드셰이크 경합… 관리자 확인 후"(11).
- **`backend/**` diff 빈 상태**(아래 스코프), **외부 네트워크 요청 0**(아래 폐쇄망).

## [정직 상태·UX] 라이브 반영 + empty/console

- **라이브 반영**: Pause → 헤더 `PAUSE:ON`+readiness "일시정지" 배지+컨트롤 "일시정지됨/재개" 전환(쿼리 무효화, 04). SetTgtFloor → **D6·D5 SignalR 델타 즉시 갱신**(D6=5·CurFloor=5, "변경 07-09 14:36:26"). 외부 curl-pause → **UI 3s 폴에서 자동 재개버튼으로 flip**(외부 변경도 반영).
- **empty 상태**: 코드상 소터 0 → "등록된 소터가 없어 운영 제어를 표시할 수 없습니다"(seeded 환경이라 실측 대신 코드 확인; select disabled·noSorters 가드).
- **콘솔 0 에러(BLOCKING 게이트)**: 운영 전 구간 authoritative 캡처(`all=true`) = **Total 3, Errors 0, Warnings 0**(monitor·ops 매 조작·/sorters 신규 nav 전부). pageerror 0, React dev-warning 0.
  - ⚠ 참고(비차단): browser_close 시 32-error 버스트 관측 → **전부 내 정리 순서 산물**. `git checkout vite.config.ts`(추적파일 원복)가 **Vite 설정 hot-reload → proxy 를 :5205 로 재지정** → 이후 /api·/hubs 가 500(negotiate/sorters/operation-log). 로그상 `[vite] server connection lost` 직후 단일 버스트(108571ms+)이며 앱은 `[hub] 연결 실패 — 2s 후 재시도`로 **graceful 처리**(크래시·pageerror·React경고 0). 즉 앱의 fail-loud 재시도 동작 확인일 뿐 스프린트 코드 결함 아님. 정리 순서만 조정하면(프로세스 kill 후 config 원복) 미발생.

## [폐쇄망] 외부 요청 0

- 전체 세션 네트워크 캡처(static 포함) 유일 origin = **`http://localhost:5194`**(내 Vite). 외부/CDN/비-동일출처 요청 **0**. `/api`·`/hubs` 는 동일출처 proxy 경유.

## [설계·인프라 정합]

- **신규 프리미티브 0**: `Dialog`(focus trap)·`toast`·`button/select/badge/card`·`WordPanel`·`useSorters`·`useMonitorState` 전부 재사용. `dialog.tsx` **무수정**(git status 미포함).
  - 계약 §4 는 `ConfirmDialog` 재사용을 명시했으나 Generator 는 `Dialog` 프리미티브를 직접 사용. 근거 정당(ConfirmDialog 엔 blank-operator 용 confirm-disabled 레버 부재 → 계약의 "공백이면 확인 비활성"을 정직 구현하려면 confirm disabled 제어 필요). **동일 focus-trap 인프라 재사용 + dialog.tsx 무수정 제약 준수** → 계약 정신(포커스 트랩·busy 잠금·danger) 충족, 위반 아님.
- `lib/ops.ts` 분리(BASE `/api/ops`), OPS_LIMITS 프론트 상수 1곳(WcsOptions.cs 미러·근거 주석, 서버 400 최종권위 = 절대규칙 #7 정합). 한국어 주석·file-scoped 컨벤션 일관. 디자인 품질 양호(운영 콘솔 톤·danger 색·경고 문구 명료).

## [회귀 0]

- **b2b 모드 토글·내비**: B2B 탭 → /data-generator, 내비 5항목(데이터 생성·로그 조회·결과 비교·박스 조회·설정) 정상. B2C 복귀 정상.
- **F2 읽기 뷰(/sorters)**: "읽기 전용" 배지 유지, 편집 컨트롤(일시정지/R클리어/셀지정/SetTgtFloor/작업자이름) **0건 누출**(12) — 무개조 재사용 확인.
- **F1 모니터링**: 렌더·데이터 정상(01).

## 스코프 청정(계약 §Completion 8)

- `git diff develop --stat -- backend/` = **빈 출력**. `git status --porcelain` = App.tsx·Layout.tsx(수정) + lib/ops.ts·pages/OpsPage.tsx·pages/sections/OpsControls.tsx(신규) + sprint-contract/sprint-log(문서) 만. 신규 프리미티브·라이브러리 0.
- **슈트 clear(O1)/CHUTE pause·resume = DEFERRED 정합**: OpsPage/OpsControls/ops.ts 에 O1·슈트 picker 미노출, 깨진 스텁 0(계약 Q3 근거 = 슈트 열거 읽기 엔드포인트 부재).

## Completion Conditions (계약 §Completion 1~8) — 전부 충족

1. 프론트 빌드 클린(typecheck 0·lint 0·build 성공, 선재 라이브러리 경고만) ✅
2. 백엔드 회귀 0(dotnet test 305 GREEN) ✅
3. 내비 "운영 제어" 점등·/ops 라우팅·b2b/토글 무손상 ✅
4. 컨트롤→정확한 Ops 호출 + operatorName body(6요청 캡처)·확인 다이얼로그·포커스 트랩 ✅
5. 정직 표면화(client 400·pingPongGuard·404·409·AlreadyInState 노출, 성공위장 0) ✅
6. 라이브 반영(pause 배지·D6/D5 SignalR·외부변경 flip) ✅
7. SAFETY(Sim TCP :1512+스크래치 SQLite, COM1/RTU·현장 DB·:5205 미접근, 기동로그 증거) ✅
8. 스코프 청정(backend diff empty·F2/b2b/F1 무변경·신규 3파일+2최소수정) ✅

## Repeat detection

- foreign-vite false-PASS 회피(--strictPort 포트 이동, S-B2B-2c), 현장 DB 오염 3중 차단(Provider=Sqlite override·스크래치 파일명·Transport=Tcp, 2026-07-03), 콘솔 authoritative 캡처 = 기존 교훈 준수. 반복 결함 0 → 신규 lessons 승격 불요.

## Minor (비차단 — 다음 스프린트 Generator 참고)

- readiness 스트립 "Busy"(api.sorters() ready:false) vs WordPanel Ready 비트=1 이 동시에 표시될 수 있음 — REST 스냅샷 vs SignalR 라이브 이중 소스(기존 SortersPage 동형)라 회귀 아님·기능 정확하나, 두 신호가 순간 불일치로 읽힐 수 있음. 표기 톤 통일 여지(cosmetic).
- 정리 순서 권고: 라이브 dev 서버 종료 **전** vite.config 원복 금지(hot-reload proxy 재지정 → 세션 말미 500 버스트). 프로세스 kill 후 원복.

→ **결론: functional-UX ∧ safety-surfacing 두 차원 모두 PASS, Completion 1~8 전부 충족. APPROVED.**

**APPROVED — S-F3b**

---

## Step 4.5 Code Review (orchestrator 기록)
- **판정: Ready to merge = Yes.** Critical 0 · Important 0 · Minor 5.
- 안전-critical 2요건 충족: (1) ops.ts 경로/메서드/body필드/OPS_LIMITS가 OpsController.cs·WcsOptions.cs와 **정확 일치**(UI가 400날 요청 미발생), (2) fail-loud 정직 — pingPongGuard=warning(가짜성공 아님)·성공문구 "큐 수락됨"(enqueue 정직)·409/404 정직 토스트. 확인모달 게이트(blank→disabled)·focus-trap 정상. WordPanel 무개조·백엔드 diff empty.
- Minor 5건(bound 리터럴 OPS_LIMITS 유도·ConnBadge 추출·pingPong 사전힌트 advisory 주석·aria-describedby/invalid·Dialog 초기포커스 문서화) todo 이연. fix 반복 불요(BLOCKING 0).

**APPROVED — S-F3b (Evaluator ∧ Step 4.5 code review, 305 GREEN·백엔드 무접촉)**
