# Sprint Contract — S-F3b (B2C "운영 제어" **프론트엔드 UI** · F3a Ops API 소비)

> Planner Subagent · 2026-07-09
> 설계 원본(고정): `docs/FRONTEND.md` §3(API 표면)·§3.3(clear/pause/resume 시맨틱)·§4·§4.5(인증=없음·사내망 신뢰 LOCK, 조작 확인 다이얼로그의 **작업자 이름 자유 입력** → `destination_event.operator_id`)·§5 페이지 ②(편집·제어 UI·확인 모달·규칙 경고)·§6 F3 Done. **모든 핵심 설계 결정은 그 문서에 LOCK — 재론 금지.**
> 소비 대상(고정): **develop에 병합된 F3a 백엔드.** `backend/src/Wcs.Api/Controllers/OpsController.cs`(O1~O6), `Infrastructure/WcsOptions.cs`(`Wcs:OpsLimits`), `Services/DestinationControlService.cs`(pause/resume outcome). 이 계약은 그 API를 **소비만** 한다.
>
> **분할 근거(F3a 계약에서 확정):** 원래 F3를 F3a(백엔드 Ops API·전이·Sim 전수검증 — 병합 완료) → **F3b(프론트 Ops/편집 UI)**로 분할했다. 위험한 PLC-write 백엔드는 F3a에서 Sim3ds로 격리·검증됐고, F3b는 **검증된 HTTP 엔드포인트를 호출하는 UI 레이어**다. **이 계약은 F3b(프론트)만 스코프한다. 백엔드는 한 줄도 바꾸지 않는다.**
>
> **스택 PR 교훈(MEMORY):** 이 브랜치는 **develop에서 분기**한다. PR #48은 별개의 오픈 백엔드 PR이며 F3b는 프론트-디스조인트(파일 충돌 0). 스택 브랜치로 병합 금지.

---

## ⚠ Questions for user (착수 전 확인 — 전부 권장 기본값 있음. override 없으면 기본값으로 진행)

설계 Q(§8)는 전건 LOCK이므로 재론하지 않는다. 아래는 **이 스프린트 경계에서만 발생하는 실제 갈림길** 3건이다.

- **Q1 — 워드 편집·제어의 집(home): 신규 "운영 제어" 페이지 vs 기존 `SortersPage`(F2 읽기 뷰) 확장?**
  **권장 = 신규 전용 페이지(신규 라우트, 예: `/ops`).** 근거(코드 확인):
  - b2c NAV_SET에 이미 **별도 항목** "운영 제어"(`Layout.tsx:40`, `enabled:false, phase:'F3'`)가 "3DS 워드"(`/sorters`)와 **분리**돼 있다 → 내비 구조가 별도 페이지를 전제.
  - `SortersPage`/`WordPanel`은 명시적으로 **"읽기 전용"(F2)** 배지를 달고 있다(`WordPanel.tsx:20`). 여기에 편집을 얹으면 F2 계약·회귀 위험을 건드린다.
  - **권장 구현:** 신규 `OpsPage`가 소터 선택 + 현재 상태 표시를 위해 **`WordPanel`을 그대로 재사용(import·무수정)**하고, 그 아래 편집·제어 컨트롤을 추가. `SortersPage`는 **무접촉**(F2 읽기 뷰 불변 = 회귀 0). 대안(SortersPage 확장)은 읽기/쓰기 관심사 혼합 + F2 회귀 리스크로 각하.
- **Q2 — 작업자 이름 캡처 UX: 확인 다이얼로그마다 입력 vs 세션 기억?**
  **권장 = 세션 기억(페이지 레벨 in-memory state) + 매 확인 다이얼로그에 필수 입력 필드로 프리필(수정 가능).** 근거:
  - §4.5는 **모든 Ops 호출에 `operatorName` 필수**(감사 귀속) — F3a가 공백/누락 시 400. 매 조작마다 재입력은 마찰.
  - 세션값은 **localStorage에 영속화하지 않는다**(브라우저 세션 간 잘못된 이름이 조용히 재사용되는 감사 위험 회피 — `uiMode`의 영속화 패턴을 여기엔 적용하지 않음). 페이지 진입 시 비어 있고, 첫 입력 후 세션 동안 프리필.
  - 다이얼로그에서 **공백이면 확인 버튼 비활성**(클라이언트 미러 = F3a 400 정합). 대안(매번 빈 필드)은 UX 저하로 비권장, 대안(영속화)은 감사 위험으로 각하.
- **Q3 — ⚠ 스코프 경계: 슈트 비움(O1 clear) + 슈트 pause/resume을 F3b에 포함?**
  **권장 = NO(이번 스프린트 제외·후속 이관). F3b는 소터 대상 제어만 스코프한다.**
  **결정적 근거(코드 확인 — 읽기 표면 갭):** UI가 목적지를 고르려면 목록이 필요한데, 읽기 API `GET /api/monitor/sorters`(`MonitoringQueries.GetSorters`)는 **`_registry.AllBundles`의 SORTER_3D만** 열거한다. **CHUTE 목적지를 열거하는 엔드포인트가 존재하지 않는다**(`FRONTEND.md §3.1`이 `GET /api/monitor/destinations`를 계획했으나 F1 미구현 — `MonitoringController`는 E1~E7 + operation-log뿐). 즉:
  - **O2/O3 pause/resume(소터)·O4/O5/O6 워드 쓰기**는 소터 목록으로 완전 구동 가능 → **F3b 포함.**
  - **O1 clear(CHUTE 전용)·CHUTE pause/resume**는 슈트 목록 없이는 picker를 만들 수 없다. 슈트 목록을 만들려면 **백엔드 읽기 엔드포인트 신설이 필요**한데, 이 스프린트는 **프론트 전용(백엔드 git diff 빈 상태가 완료 조건)** → 불가.
  - **권장:** O1(A-8 슈트 FULL 복구 트리거)과 CHUTE pause/resume은 **`GET /api/monitor/destinations` 읽기 엔드포인트를 함께 추가하는 후속 스프린트(full-stack)**로 이관. A-8 백엔드 결선은 F3a에서 이미 닫혔고(호출자 존재), 남은 것은 UI 트리거뿐이다.
  - **대안(비권장):** 슈트 destId 수동 텍스트 입력으로 O1 노출 → 오입력 시 엉뚱한 슈트 clear 위험·UX 저하. 각하.
  - **override 시(사용자가 O1을 이번에 원하면):** 이 스프린트는 **full-stack로 승격**되어 백엔드에 슈트 열거 엔드포인트 추가가 포함돼야 하며, "백엔드 무변경" 완료 조건이 완화된다 — 착수 전 사용자 사인오프 필요.

> 위 3건에 override가 없으면 **신규 `/ops` 페이지 / 세션 기억 프리필 / 소터 제어만(슈트 clear 후속 이관)**으로 확정 진행한다.

---

## ★ SAFETY BOUNDARY (이 계약의 최우선 절 — 위반 = 즉시 FAIL)

이 UI는 버튼 클릭이 **실 PLC 동작을 유발**한다(백엔드 큐 경유). UI 자체는 HTTP만 호출하지만, **검증 환경이 실 하드웨어에 닿으면 물리 소터가 움직인다.**

### S-1. 검증·실행은 **Sim 전용 — 실 3DS PLC(COM1/RTU)에 절대 접근 금지 (하드 제약)**
- **함정(코드 확인):** `launchSettings.json` 부재로 `dotnet run --project backend/src/Wcs.Api`의 기본 환경 = **Production** = base `appsettings.json` = **`Sorters[0].Transport="Rtu"`, `PortName="COM1"`** + **`Database.Provider="SqlServer"`, 현장 연결문자열 `Rcs3dsInterlockingWcs`**. `appsettings.Development.json`은 **Transport·DB provider를 오버라이드하지 않는다**(Serilog·SeedOnStartup만) → Development로 띄워도 **여전히 COM1 + 현장 DB로 붙는다.**
- **따라서 안전한 검증 기동 = 명시 오버라이드 필수:**
  1. Sim 먼저: `dotnet run --project backend/src/Wcs.Sim3ds`(FluentModbus TCP, 기본 `127.0.0.1:1502`).
  2. Wcs.Api를 **Sim TCP + 스크래치 DB로 오버라이드** 기동(환경변수 이중밑줄 config 또는 전용 오버라이드 파일): `Sorters__0__Transport=Tcp`, `Sorters__0__Host=127.0.0.1`, `Sorters__0__Port=1502`, **DB는 스크래치**(SQLite 파일 또는 **일회용** SQL Server DB — **현장 DB 이름/연결문자열 절대 금지**), `Database__MigrateOnStartup=true` + `Database__SeedOnStartup=true`(소터/목적지가 있어야 UI가 열거·조작 가능).
- **실 PLC로 워드 1개라도 쓰거나(COM1/RTU 기동) 현장 SQL Server DB에 시드·쓰기하면 즉시 FAIL.** 라이브 관찰이든 Playwright든 예외 없음.
- **현장 DB 오염 가드(MEMORY 교훈, 2026-07-03 사고):** 환경(Development)만으로 시드가 발동하지 않게 돼 있으나, base가 현장 연결문자열이므로 **Provider/ConnectionStrings를 스크래치로 오버라이드하지 않은 채 SeedOnStartup=true 금지**.

### S-2. UI는 절대규칙을 **우회하지 않는다** (백엔드가 강제, UI는 정직 반영)
- UI는 **오직 `/api/ops/*` HTTP만 호출**한다 — Modbus·PLC 직접 접근 0(프론트엔 그 경로 자체가 없음, 절대규칙 #1은 백엔드가 이미 강제).
- **핑퐁 가드·경계 오류를 숨기지 않는다(정직·Fail Loud):** O4 응답의 `pingPongGuard:true`(현재 TgtFloor≠0 → 컨슈머가 스킵 가능)를 성공으로 위장하지 않고 **경고로 표면화**. 400(공백 operator·범위 초과)·404·409(Conflict)·`AlreadyInState`를 **사용자에게 명시 메시지**로 노출(토스트/인라인) — 조용히 삼키기 금지.
- 클라이언트 검증은 F3a 경계를 **미러**하되(선제 UX) 대체하지 않는다: 서버가 최종 권위.

### S-3. 백엔드·인접 스코프 무변경 (프론트 디스조인트)
- `backend/**` **무변경**(git diff 빈 상태 = 완료 조건). Ops API·전이·감사·마이그레이션은 F3a에서 완결 — 재구현·수정 금지.
- b2b 내비·모드 토글·B2B 페이지·모니터링(F1)·`SortersPage`/`WordPanel`(F2 읽기) **무접촉**(회귀 0). `useMonitorHub`·`signalr.ts`·`queries.ts`·`api.ts`(읽기 클라이언트) 기존 동작 보존.

---

## Goal

b2c 내비의 비활성 **"운영 제어"** 항목(`Layout.tsx:40`)을 활성화하고, F3a `/api/ops/*`를 소비하는 **운영 제어 페이지**를 신설한다. 소터를 선택해 **(a) Pause/Resume 토글(O2/O3)**, **(b) 안전 워드 쓰기 3종 — SetTgtFloor(O4)·Clear-R(O5)·Cell-Assign(O6)**을 **확인 다이얼로그 + 필수 작업자 이름 입력 + 규칙 위반 경고**로 수행한다. 현재 상태(D0~D6 실시간·readiness)를 편집 전에 보여주고, 조작 결과(성공/핑퐁/400/404/409/AlreadyInState)를 **정직히 표면화**하며, 조작 후 라이브 상태가 반영된다. 기존 UI 무회귀, 백엔드 무변경, 사내망 폐쇄(외부 요청 0). (슈트 clear = Q3에 따라 후속 이관.)

---

## Implementation Scope (Generator가 할 일 — F3b 프론트 한정)

### 1. 내비 활성화 + 라우트 (`Layout.tsx`, `App.tsx`)
- `Layout.tsx` b2c NAV_SET의 "운영 제어" 항목: `enabled:true`, `phase:null`, `to:'/ops'`(신규), `subtitle` 채움(예: "Pause/Resume · 워드 편집(안전 3종)"). **b2b NAV_SET·모드 토글·기존 b2c 두 항목 무접촉.**
- `App.tsx`: `<Route path="/ops" element={<OpsPage />} />` 추가(기존 라우트·`ModeHome` 무변경).

### 2. 신규 `OpsPage` (권장 `frontend/src/pages/OpsPage.tsx`) — 소터 대상 제어
- **소터 선택:** `useSorters()`(기존, `queries.ts`) 재사용 — 첫 소터 기본 선택(`SortersPage` 패턴 준용). 소터 없음/로딩 empty 상태 처리.
- **현재 상태(편집 전 근거):** `WordPanel`(F2, import·무수정) 재사용으로 D0~D6 실시간(`useMonitorState`) 표시 + readiness 스트립(online/ready/full/paused = `api.sorters()`의 `SorterStatus`). SignalR 연결 배지 재사용 가능.
- **제어 컨트롤(신규 섹션, 권장 `frontend/src/pages/sections/OpsControls.tsx`):**
  - **Pause/Resume 토글(O2/O3):** 현재 `paused` 반영. 확인 다이얼로그 → 성공 시 `outcome`(Transitioned/AlreadyInState) 반영, 409 Conflict는 "동시 전이 충돌 — 재시도" 경고.
  - **SetTgtFloor(O4):** 숫자 입력(클라 검증 `1 ≤ floor ≤ OpsLimits.MaxTgtFloor(=20)`). **현재 TgtFloor 표시 + ≠0이면 "진행 중 — 핑퐁 차단될 수 있음" 사전 경고.** 확인 다이얼로그. 200 응답의 `pingPongGuard:true`면 "큐 수락됨(진행 중이라 컨슈머가 스킵할 수 있음)" 경고 토스트, false면 성공 토스트.
  - **Clear-R(O5):** 진단용 — danger 확인("핸드셰이크 상태 오염 가능").
  - **Cell-Assign(O6):** 고위험 진단 — cellNo(`1..1000`)·seq(`1..30000`) 입력 + **강한 danger 경고**("정상 셀 지정은 IF-10 핸드셰이크가 수행 — 수동 지정은 셀 회계와 경합 가능").
- **작업자 이름(Q2):** 페이지 레벨 in-memory state. **매 확인 다이얼로그에 필수 필드로 프리필**(수정 가능), 공백이면 확인 버튼 비활성. → 모든 Ops 호출 body의 `operatorName`.
- **조작 후 반영:** 성공 시 관련 쿼리 무효화(`['sorters']`, 필요 시 `['cells']`) + 워드는 SignalR 델타로 자동 갱신. (F3a `OnWrite`→`PLC_WRITE` oplog가 `useMonitorHub`의 무효화 맵을 이미 태운다 — 결선 확인.)

### 3. 신규 Ops HTTP 클라이언트 (권장 `frontend/src/lib/ops.ts`)
- **별도 파일**(BASE=`/api/ops`) — 기존 `lib/api.ts`(읽기·`/api/monitor`)와 시맨틱이 다르다. `lib/testData.ts`의 실패-표면화 패턴을 참고하되, **Ops 응답 형상에 맞춤**: 성공은 200 + `{status:"enqueued"/"paused"/...}`(엔드포인트별 상이), 실패는 400/404/409 + `{error:"..."}`.
- **Fail-loud 계약:** !ok면 응답 body의 `error` 문자열을 추출해 던지거나 Outcome으로 반환(호출부가 토스트). 네트워크 예외도 메시지로 환원. O4는 `pingPongGuard`·`currentTgtFloor`·`floor`를, pause/resume은 `outcome`을 호출부에 전달(정직 표면화용).
- **JSON POST** 헤더 = `{'Content-Type':'application/json', Accept:'application/json'}`. TanStack Query `useMutation` 또는 명시 async 중 기존 코드 컨벤션에 맞는 쪽(현 코드베이스는 `useMutation` 미사용 — 단순 async + 성공 시 `queryClient.invalidateQueries`도 정합).

### 4. UI 인프라 재사용 (신규 프리미티브 금지)
- **확인 모달:** `components/ui/dialog.tsx`의 `ConfirmDialog`(포커스 트랩·busy 잠금·danger 스타일) 그대로. 작업자 이름 필드는 다이얼로그 `description` 슬롯 안에 배치(초기 포커스·Tab 트랩과 정합 확인).
- **버튼/셀렉트/배지/카드:** `components/ui/{button,select,badge,card}.tsx`. **토스트:** `lib/toast.ts` `useToast`(success/error/warning/info) — 이미 `ToastProvider` 배선됨(`main.tsx`). 스타일·톤은 기존 계기판 체계(`lib/status.ts`) 일관.

### 스코프 OUT (F3b에 흡수 금지)
- **백엔드 전부**(OpsController·전이·감사·마이그레이션) → F3a 완결. `backend/**` 무변경.
- **슈트 clear(O1) + CHUTE pause/resume** → **Q3에 따라 후속 이관**(슈트 열거 읽기 엔드포인트 + UI, full-stack). override 시에만 포함.
- **인증/로그인 UI** → §4.5 LOCK(미도입). 작업자 이름은 인증이 아닌 자유 입력 감사 흔적.
- **임의 레지스터/D4 비트 편집 UI** → Q2(설계) LOCK(안전 3종만).
- **F2 읽기 뷰(`SortersPage`/`WordPanel`) 개조** → 재사용(import)만, 무수정.
- 신규 프론트 라이브러리 추가(외부 CDN·의존 확장) 금지 — 폐쇄망.

---

## Absolute Rules Compliance (UI 관점)

- **#1 (단일 쓰기 큐):** UI는 `/api/ops/*` HTTP만 호출 — Modbus 경로 자체가 프론트에 없음. 백엔드가 큐 경유를 강제(F3a 검증 완료).
- **#2 (TgtFloor 게이트):** O4 UI는 컨슈머 `TgtFloor==0` 가드를 우회하지 않는다. 현재 TgtFloor≠0을 사전 경고하고, 응답 `pingPongGuard`를 **정직 표면화**(거짓 성공 금지).
- **#3 (WCS 비클리어):** UI는 `floor>=1`만 전송(F3a도 `floor<1` 400). `floor=0` 수동 리셋 노출 없음.
- **#6 (필드명):** Ops body 필드명은 F3a DTO 그대로 — `operatorName`·`floor`·`cellNo`·`seq`. RCS 계약 필드(`pId/agvNo/...`)와 무관(이 페이지는 RCS API 미사용).
- **#7 (하드코딩 금지):** OpsLimits(20/1000/30000)는 **하드코딩 리터럴로 UI에 박지 않기**를 권장 — 이상적으론 서버 노출값 소비이나, F3a가 한계값 조회 엔드포인트를 제공하지 않으므로 **기본은 F3a 문서값과 동기된 프론트 상수 1곳(주석에 근거 명시)**로 두고, 서버 400이 최종 권위. 프론트 폴 주기 등은 기존 상수 정책(`queries.ts` 주석) 준용.
- **#8 (순수 판정):** 신규 순수 판정 없음. UI는 상태 표시·클라 검증(범위 미러)만.

---

## Evaluation Criteria (Evaluator 판정 기준 + 가중치)

- **[25%] 안전·정직 표면화(하드):** (i) 검증이 **Sim3ds TCP + 스크래치 DB 전용**·실 COM1/RTU·현장 DB 접근 0(S-1), (ii) `pingPongGuard`/400/404/409/`AlreadyInState`가 **사용자에게 명시 노출**(삼키기 0), (iii) `backend/**` diff 빈 상태, (iv) 신규 외부 네트워크 요청 0(폐쇄망). **하나라도 위반 시 전체 FAIL.**
- **[30%] 기능 완결(소터 제어):** 내비 "운영 제어" 활성·라우팅, Pause/Resume·SetTgtFloor·Clear-R·Cell-Assign 각각 **올바른 `/api/ops/*` 호출 + `operatorName` 동반**, 확인 다이얼로그·포커스 트랩·busy 잠금, 클라 범위 검증(1..20 / 1..1000 / 1..30000), 조작 후 라이브 상태 반영. **Playwright fresh 증거**(클릭→네트워크 요청 관찰).
- **[20%] 회귀 0:** b2b 내비·모드 토글·B2B 페이지·모니터링(F1)·`SortersPage`(F2 읽기) 무손상. 프론트 `tsc --noEmit`·`eslint`·`vite build` 클린. 백엔드 `dotnet test` 전건 GREEN(백엔드 무변경이므로 baseline 유지 — 착수 시 clean run 확인).
- **[15%] 정직한 상태·UX:** 로딩/empty(소터 0)·에러 상태 표면화, 파괴적 조작(Clear-R·Cell-Assign) 강경고 문구, 콘솔 0 에러.
- **[10%] 설계·인프라 정합:** `ui/dialog.tsx`·`toast`·`button/select/badge/card` 재사용(신규 프리미티브 0), `WordPanel` 무수정 재사용, `lib/ops.ts` 분리, 코드 스타일(한국어 주석·file-scoped 컨벤션) 일관.

---

## Completion Conditions (Evaluator PASS 최소 조건 — 전부 충족)

1. **프론트 빌드 클린:** `frontend/`에서 `npm run typecheck`(`tsc --noEmit`)·`npm run lint`(eslint)·`npm run build` 전부 통과(경고/에러 0).
2. **백엔드 회귀 0:** `dotnet test backend/Wcs.sln` 전건 GREEN(백엔드 무변경 — baseline 유지; 착수 clean run으로 카운트 확인, 단일 run 신뢰 금지).
3. **내비·라우팅:** b2c "운영 제어"가 활성·점등되고 신규 라우트로 이동. b2b 내비·모드 토글 무손상.
4. **컨트롤 → 정확한 Ops 호출(Playwright + 네트워크 관찰):** Pause/Resume→`POST /api/ops/destinations/{destId}/{pause|resume}`, SetTgtFloor→`.../sorters/{destId}/tgtfloor`, Clear-R→`.../clear-r`, Cell-Assign→`.../cell-assign` — 전부 `operatorName` 포함 body. 확인 다이얼로그·포커스 트랩 동작.
5. **정직한 결과 표면화:** 400(공백 operator·범위 초과 — 클라 선제 차단 포함)·404·409 Conflict·`AlreadyInState`·O4 `pingPongGuard`가 사용자에게 명시 메시지로 노출(성공 위장 0).
6. **라이브 반영:** 조작 성공 후 readiness/워드 상태가 갱신(쿼리 무효화 + SignalR 델타).
7. **SAFETY:** 검증 전 과정 Sim3ds TCP + 스크래치/시드 DB. 실 COM1/RTU·현장 DB 미접근(기동 설정·로그로 증거). 신규 외부 네트워크 요청 0(Playwright 네트워크 캡처).
8. **스코프 청정:** `backend/**` diff 없음. `SortersPage`/`WordPanel`·b2b·F1 무변경. 신규 파일 = `OpsPage`·`OpsControls`·`lib/ops.ts`(+`Layout.tsx`·`App.tsx` 최소 수정).

---

## Remaining phases (이 계약 이후)

- **(후속) 슈트 목적지 제어 — full-stack 스프린트:** `GET /api/monitor/destinations`(슈트/소터 상태 열거, `FRONTEND.md §3.1` 미구현분) 읽기 엔드포인트 신설 + UI에서 O1 clear(A-8 슈트 FULL 복구 트리거)·CHUTE pause/resume 노출. Q3에서 F3b가 소터로 한정했으므로 이 갭을 명시 이관.
- **(선택) `OPS` operation_log 카테고리 + 필터 UI:** 운영자 발원 조작 단일 필터 요구가 확인되면(F3a는 경량 STATE 재사용). 마이그레이션(SqlServer+Sqlite 2종) 동반 — 백엔드 스프린트.

---

- **Parallel Modules:** N/A (single cohesive module). 신규 `OpsPage`·`OpsControls`·`lib/ops.ts` + `Layout.tsx`/`App.tsx` 최소 수정이 서로 결선돼 경계-클린 병렬 분할 이득 없음. 순차 단일 Generator.
- **Evaluation Dimensions:** functional-UX + safety-surfacing(2차원). safety-surfacing = 위 [25%](정직 표면화·Sim 전용·백엔드 무변경·폐쇄망)을 functional과 **병렬 전문 검토**로 격리(둘 다 PASS해야 APPROVED). PLC-affecting UI라 정직/안전 차원을 독립 관점으로 둔다.

- **Detected Project Type:** **Full-stack** — 단, **이 스프린트의 변경 표면은 FRONTEND 전용**(백엔드 F3a는 develop 병합·검증 완료). 따라서 Web/UI Verification Scenario 슬롯을 채우고, **Backend/API = N/A(백엔드 무변경 — `git diff origin/develop -- backend/`가 빈 상태임을 완료 증거로 제출)**.

- **Verification Scenarios (Web/UI — 이 스프린트 변경 표면 기준):**

  - **핵심 사용자 플로우:**
    1. 앱 진입(b2c) → 좌측 내비 "운영 제어" **점등·클릭 가능** → 클릭 시 `/ops`로 라우팅·헤더 타이틀 반영.
    2. 소터 선택(기본 첫 소터) → `WordPanel`에 D0~D6 실시간 + readiness(online/ready/full/paused) 표시.
    3. **Pause:** Pause 클릭 → 확인 다이얼로그(대상·현재 상태 + 필수 작업자 이름) → 확인 → `POST /api/ops/destinations/{destId}/pause` → 성공 토스트 + `paused` 배지 반영. **Resume**으로 복원.
    4. **SetTgtFloor:** floor 입력(범위 내) → (현재 TgtFloor≠0이면 사전 경고) 확인 다이얼로그 → `POST .../tgtfloor` → `pingPongGuard`에 따른 정직 메시지, 성공 시 D6 라이브 반영(Sim).
    5. **Clear-R / Cell-Assign:** danger 확인 다이얼로그(강경고) → 해당 Ops 호출 → 결과 표면화.

  - **컴포넌트/페이지 (touched):** `Layout.tsx`(내비 활성), `App.tsx`(라우트), 신규 `OpsPage`·`OpsControls`, 신규 `lib/ops.ts`. **재사용(무수정):** `WordPanel`·`ui/dialog.tsx`(ConfirmDialog)·`ui/{button,select,badge,card}`·`lib/{toast,queries,api,signalr,useMonitorHub}`.

  - **검증할 상태:** 로딩(소터 목록 대기)·empty(소터 0 — "등록된 소터 없음")·성공·에러(400 공백 operator·클라 범위 초과 선제 차단·404 미등록 destId·409 Conflict·`AlreadyInState` 멱등)·busy(다이얼로그 처리 중 버튼 잠금)·O4 `pingPongGuard` 경고.

  - **상호작용:** 클릭(내비·버튼·토글), 타이핑(floor/cellNo/seq/작업자 이름), 셀렉트(소터), 키보드(다이얼로그 Esc 취소·Tab 포커스 트랩·확인). **접근성:** 확인 다이얼로그 포커스 트랩·복원(`ui/dialog.tsx` 계약 유지).

  - **관찰(하드):** Playwright 네트워크 캡처로 **각 컨트롤이 올바른 `/api/ops/*` 요청 + `operatorName` body**를 보냄을 확인. **콘솔 에러 0.** **외부(비-동일출처) 요청 0**(폐쇄망 — `/api`·`/hubs`만). 조작 후 라이브 상태 갱신 관찰.

  - **회귀 관찰:** b2b 모드 토글·b2b 내비 5항목·모니터링(F1)·"3DS 워드"(F2 읽기 뷰) 정상 — 무손상.

  - **Backend/API 시나리오:** **N/A** — F3b는 `backend/**`를 전혀 건드리지 않는다(Ops API는 F3a에서 완결·Sim 검증됨). 증거 = 백엔드 diff 빈 상태 + 기존 `dotnet test` 전건 GREEN(baseline).

> Planner self-check — Detected project type: Full-stack (touched surface: Frontend only). Required scenario slots: 6 (user flows, components/pages, states, interactions, console/network observation, regression). All slots filled: yes (Backend/API slot = N/A with reason: git-diff backend empty).
