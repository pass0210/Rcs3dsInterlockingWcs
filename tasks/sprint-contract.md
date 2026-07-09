# Sprint Contract — S-F3B-FOLLOWUP (B2C 운영 제어 현장 후속 2건: 셀입력 라벨 · Ready-아닐-때 수동쓰기 무시)

> Planner Subagent · 2026-07-09
> 발원: 2026-07-09 실 3DS 현장 테스트 중 사용자/사수 관찰 2건. **S-F3a(Ops 백엔드) + S-F3b(Ops 프론트) 후속.**
> 소비/수정 대상(고정): `frontend/src/pages/sections/OpsControls.tsx`, `frontend/src/pages/OpsPage.tsx`, `frontend/src/lib/ops.ts`,
> `backend/src/Wcs.Api/Controllers/OpsController.cs`, **(PROTECTED)** `backend/src/Wcs.PlcGateway/PlcGateway.cs`.
> 이번엔 **Full-stack**(FE cosmetic + FE 게이트 + OpsController 사전점검 + **PlcGateway 코어 최소 변경**). S-F3b(프론트 전용)와 달리 백엔드를 건드린다.
>
> **스택 PR 교훈(MEMORY):** 이 브랜치는 **develop에서 분기**한다. 스택 브랜치로 병합 금지·병합 후 develop 실재 검증.

---

## ⚠ Questions for user (착수 전 확인 — 전부 권장 기본값 있음. override 없으면 기본값으로 진행)

- **Q1 — ClearR(O5)를 Ready 게이트에 포함? → 권장 = NO(게이트 안 함).**
  근거(코드 확인): O5 `ClearR`은 **막힌 R_Flag 복구용 진단 도구**다. 소터가 stuck이면(R_Flag=1 미해소) 대개 `Ready==0`이다 — 이때 Ready 게이트를 걸면 **복구 자체가 불가**해진다. 또 `ClearR` 컨슈머 case에는 사전 가드가 없어(무조건 R 클리어) stale-스냅샷 결함도 없다. 따라서 **O5는 Ready 게이트·fresh-read 대상에서 제외**하고 기존 동작(항상 허용, danger 확인 다이얼로그) 유지. FE에서도 O5는 not-Ready여도 비활성화하지 않는다. (override: O5도 게이트하려면 명시 요청.)

- **Q2 — 운영자 즉시 피드백: OpsController 동기 사전점검(409) + 컨슈머 fresh-read 가드 병행 vs 컨슈머 가드만? → 권장 = 둘 다.**
  근거: 쓰기 큐는 비동기라 컨슈머-측 skip은 이미 200 "enqueued"로 응답한 뒤라 **사후 피드백이 불가**하다. 따라서 (a) OpsController O4/O6에서 enqueue 직전 `bundle.Latest`로 **동기 사전점검**(Ready==0 → 409 즉시 거부, C_Flag/TgtFloor advisory) = 흔한 경우 즉시 피드백, (b) 컨슈머-측 **fresh-read 가드** = rapid-double 경합까지 잡는 **최종 권위**. 사전점검만으로는 급속 2연타(둘 다 stale `Latest` 관찰)를 못 잡고, 컨슈머 가드만으로는 피드백이 없다 → 병행이 정답.

- **Q3 — Ready 게이트의 배치: PLC-코어 컨슈머 case에 넣나, OpsController 사전점검 + FE에만 두나? → 권장 = OpsController 사전점검 + FE만(컨슈머 case에는 넣지 않음).**
  **결정적 근거(코드 확인 — 공유 컨슈머 case):** `PlcWrite.SetTgtFloor`·`PlcWrite.CellAssign` 컨슈머 case는 **수동 경로와 자동/오케스트레이트 경로가 공유**한다.
  - `SetTgtFloor`: O4(수동) + **`RcsController.AlignSorterToOperationalFloor`(자동 IF-09)** — `DepositDecider.Decide`가 **`Ready==0`일 때 의도적으로** 운영층 복귀 TgtFloor를 기입한다(`DepositDecider.cs:56-57`). 컨슈머 case에 Ready 게이트를 넣으면 **이 자동 정렬이 깨진다.**
  - `CellAssign`: O6(수동) + **`HandshakeOrchestrator`(오케스트레이트 C 단계, `HandshakeOrchestrator.cs:129`)** — 정상 핸드셰이크가 `Ready==0`(분류/이동 중)에 C를 기입할 수 있다. 컨슈머에 Ready 게이트를 넣으면 **정상 핸드셰이크가 깨진다.**
  → 따라서 **Ready 게이트는 수동 경로에서만** 유효해야 한다. 수동 경로만이 OpsController를 지나므로, **Ready 게이트를 OpsController 사전점검 + FE에 두면** 자동/오케스트레이트 경로를 전혀 건드리지 않고 목적을 달성한다. PLC-코어 변경은 **origin-무관하게 안전한 fresh-read 가드 하나**로 최소화(protected-zone 원칙). (override: 굳이 컨슈머-측 manual-only Ready 게이트를 원하면 `PlcWrite` 레코드에 origin 태그(`bool Manual=false`, 기본값으로 기존 enqueuer 전부 무영향)를 추가하는 방식 — 위험·표면 확대이므로 비권장.)

> 위 3건에 override 없으면 **O5 게이트 제외 / 사전점검 409 + 컨슈머 fresh-read 병행 / Ready 게이트는 OpsController+FE에만**으로 확정 진행한다.

---

## Root Cause (#3 — 실제 코드 대조 확정)

**증상:** 사용자가 Cell-Assign을 급속 2회 클릭(cell1/seq1 → cell2/seq2)했더니 **둘 다 적용**되어 2번째가 1번째의 C 영역을 덮어썼다.

**기전(확정):**
1. 컨슈머 `ProcessWriteAsync`(`PlcGateway.cs:475`)는 단일 큐를 `_clientLock` 임계구역에서 **직렬** 처리한다.
2. `CellAssign` case(`:500`)의 가드 `var snapC = _latest; if (snapC.CFlag) return;`(`:502-507`)와 `SetTgtFloor` case(`:486`)의 가드 `var snapTgt = _latest; if (snapTgt.TgtFloor != 0) return;`(`:488-493`)는 **폴 스냅샷 캐시 `_latest`**(`:109`, `volatile`)를 읽는다.
3. `_latest`는 **폴 루프만** `PollIntervalMs`(기본 150ms)마다 갱신한다(`:272 _latest = snap;`). 쓰기 컨슈머는 쓰기 후 `_latest`를 갱신하지 않는다(`RmwD4LockedAsync`가 D4를 fresh FC03로 읽지만(`:549`) 그 값을 `_latest`에 되쓰지 않음).
4. 큐가 폴 갱신보다 빨리 비므로, write#1이 `C_Flag=1`을 세팅한 직후(같은 <150ms 창) write#2가 실행되면 `_latest.CFlag`는 **아직 0**(폴이 아직 새 C_Flag=1을 못 봄) → 가드 통과 → D0/D1을 cell2/seq2로 **덮어씀**.

**동일 클래스:** `SetTgtFloor`의 `snapTgt=_latest` 가드도 같은 stale-스냅샷 결함(audit 묶음 D 계열·field #31 "레벨-읽기 stale 스냅샷" 패턴 — `tasks/audit-20260701-full.md:191`, `tasks/lessons.md` 2026-07-06 A-1 참조).

**핵심 수정(권위):** 가드가 `_latest`가 아니라 **`_clientLock` 안에서 현재 레지스터를 fresh FC03로 직접 읽어** 판정하면, write#2의 fresh-read가 write#1이 방금 세팅한 `C_Flag=1`을 관측 → 정확히 거부한다. read+write가 같은 임계구역 안이라 폴·다른 쓰기와 프레임 교차 없음(결정적).

---

## ★ SAFETY BOUNDARY (이 계약의 최우선 절 — 위반 = 즉시 FAIL)

### S-1. `Wcs.PlcGateway` 코어 = PROTECTED ZONE — 최소 변경 + 명시 call-out
- 변경은 **컨슈머 가드의 판정 원천을 `_latest` → `_clientLock` 내부 fresh FC03로 교체**하는 것에 국한한다. 큐 구조(절대규칙 #1), `_clientLock` 임계구역, RMW 원자성, 이벤트/관측 훅, OFFLINE 전이 로직은 **무변경**. PR 설명에 protected-zone 변경임을 명시.
- **`HandshakeOrchestrator`는 한 줄도 바꾸지 않는다** — R_Flag arming·off-by-one 연쇄 fix는 별개 현장 작업(#31/묶음 D-①). 이 계약은 그것과 분리.

### S-2. 검증·실행은 **Sim3ds TCP + 스크래치 DB 전용 — 실 3DS PLC(COM1/RTU)·현장 DB에 절대 접근 금지 (하드 제약)**
- **함정(코드/MEMORY 확인):** `launchSettings.json` 부재로 `dotnet run --project backend/src/Wcs.Api` 기본 환경 = **Production** = base `appsettings.json` = **`Sorters[0].Transport="Rtu"`, `PortName="COM1"`, `Database.Provider="SqlServer"` + 현장 연결문자열**. `appsettings.Development.json`은 Transport/DB provider를 오버라이드하지 않는다 → Development로 띄워도 **여전히 COM1 + 현장 DB로 붙는다.**
- **안전 검증 = 명시 오버라이드 필수:** ① Sim 먼저(`dotnet run --project backend/src/Wcs.Sim3ds`, TCP :1502). ② Wcs.Api를 `Sorters__0__Transport=Tcp`·`Sorters__0__Host=127.0.0.1`·`Sorters__0__Port=1502` + **스크래치 DB**(SQLite 파일 또는 일회용 SQL Server DB, **현장 이름/연결문자열 절대 금지**)로 오버라이드 기동. 자동 테스트는 `SimWebApplicationFactory`(동적 포트 Sim + in-memory SQLite) 재사용.
- **실 PLC에 워드 1개라도 쓰거나(COM1/RTU) 현장 SQL Server DB에 시드·쓰기하면 즉시 FAIL.** 예외 없음.
- **현장 DB 오염 가드(MEMORY 2026-07-03 사고):** Provider/ConnectionStrings를 스크래치로 오버라이드하지 않은 채 `SeedOnStartup=true` 금지.

### S-3. 절대규칙 보존 (위반 = 즉시 FAIL)
- **#1** PLC 쓰기는 단일 큐만 — 컨트롤러/사전점검은 **읽기(`bundle.Latest` 또는 신규 동기 read)만** 할 수 있고 Modbus 쓰기는 큐 경유. 사전점검이 fresh read를 하더라도 그것은 **컨슈머 큐 밖의 쓰기가 아니라 읽기**여야 한다(권장: 사전점검은 `bundle.Latest`만 사용 — 신규 동기 Modbus 읽기 표면을 만들지 않음). **#2** TgtFloor==0 게이트 보존(fresh-read는 이 게이트를 더 정확하게 만들 뿐, floor>=1만 수락 유지). **#3** WCS는 TgtFloor를 클리어하지 않음. **#7** 하드코딩 타이밍 금지. **#8** Core 순수 함수 무변경(`DepositDecider` 무접촉).

---

## Item #2 — 셀 수동지정 입력칸 라벨 (FRONTEND · cosmetic/easy)

**문제:** `OpsControls.tsx` O6 Cell-Assign 행의 두 입력(cellNo `~:326-336`, seq `~:337-347`)은 `placeholder`+`aria-label`만 있고 **가시 라벨이 없다** → 값을 타이핑하면 placeholder가 사라져 어느 칸이 무엇인지 구분 불가. O4 SetTgtFloor 입력(`~:286-296`)도 동일.

**Scope:**
- O6의 cellNo 입력 앞에 가시 라벨 "셀 번호", seq 입력 앞에 "순번"(또는 "명령 순번" — 다이얼로그/desc 문구와 일관되게) 배치.
- O4 SetTgtFloor 입력 앞에 가시 라벨 "층"(또는 "목표층").
- 기존 `INPUT_CLS`·계기판 톤·`ControlRow` 레이아웃 재사용. 가시 라벨 추가 시 기존 `aria-label`은 유지하거나 `<label htmlFor>`/래핑으로 접근성 유지(중복 라벨링 회피). 값 있을 때도 라벨이 남아 구분되게.
- 작고 안전한 순수 FE 변경 — 로직/네트워크 무변경.

---

## Item #3 — Ready 아닐 때 수동 쓰기 무시 + rapid-double 덮어쓰기 방지 (BACKEND PLC CORE + OpsController + FE · HIGHEST RISK)

### 3-A. 컨슈머 fresh-read 가드 (PLC 코어 — 최소·권위·origin-무관)  `PlcGateway.cs`
- `ProcessWriteAsync`의 `SetTgtFloor`·`CellAssign` 가드가 **`_latest` 대신 `_clientLock` 내부 fresh FC03**로 현재 레지스터를 읽어 판정하도록 교체.
  - `CellAssign`: 쓰기 전 **D4(Flags) fresh read → C_Flag==1이면 skip**(현재 `snapC=_latest` 대체). D4 하나만 읽으면 됨(FC03 1워드). 통과 시 기존대로 D0/D1 FC16 → `RmwD4LockedAsync`로 C_Flag set.
  - `SetTgtFloor`: 쓰기 전 **D6(TgtFloor) fresh read → !=0이면 skip**(현재 `snapTgt=_latest` 대체, 절대규칙 #2 핑퐁 차단을 정확화).
  - 최소 범위 유지: 필요한 레지스터만(D4·D6) 읽거나 D0~D6 블록 1회 읽기 중 Generator가 최소·명확한 쪽 선택. read는 반드시 이미 보유 중인 `_clientLock` 임계구역 안(추가 lock 없음).
- **origin-무관 안전성(회귀 0 근거):** fresh-read는 "언제 쓰기를 허용하는가"를 바꾸지 않는다 — 오직 `C_Flag==0`/`TgtFloor==0` 판정을 **실제 PLC 값 기준으로 정확화**할 뿐. 정상 오케스트레이트 핸드셰이크는 C 기입 시 C_Flag=0(PLC가 직전 건 클리어)이라 fresh-read=통과=무회귀. 자동 IF-09 정렬도 TgtFloor==0에서 기입=무회귀. rapid-double·경합만 새로 거부됨.
- **Ready는 이 가드에 넣지 않는다**(Q3 — 공유 컨슈머 case가 자동/오케스트레이트 경로와 겸용, 그들은 `Ready==0`에 정당히 쓴다).
- 기존 skip 로그(`_log.LogWarning("[쓰기 큐] CellAssign 스킵 …")`)와 `EmitWrite` 관측 훅은 유지(감사·관측 보존).

### 3-B. Ready 게이트 (수동 경로 전용 — OpsController 사전점검, 409)  `OpsController.cs`
- O4 `SetTgtFloor`·O6 `CellAssign`에서 enqueue **직전** `bundle.Latest`로 동기 사전점검:
  - `!Latest.Ready`(BUSY: 분류/이동 중) → **409**(예: `{ error: "소터가 Ready 상태가 아닙니다(분류/이동 중) — 수동 쓰기가 거부되었습니다." }`) + 감사 1행(WARN). enqueue 안 함.
  - `!Latest.Online`(OFFLINE)도 동일 거부(선택: 409 또는 명확 메시지) — Ready와 함께 판정.
- **O5 ClearR은 사전점검 대상 아님**(Q1 — 복구 도구, 항상 허용).
- 사전점검은 **읽기만**(절대규칙 #1). 신규 동기 Modbus 읽기 표면을 만들지 말고 `bundle.Latest`(폴 스냅샷) 사용 — Ready는 초 단위로 바뀌는 레벨이라 ~150ms stale이 허용 범위(C_Flag처럼 서브-폴 창에서 뒤집히지 않음). rapid-double은 3-A 컨슈머 가드가 최종 차단.

### 3-C. 운영자 피드백 정직화 (O6 advisory) `OpsController.cs` + `ops.ts`
- O6 응답에 **`cFlagGuard`** advisory 필드 추가(O4의 `pingPongGuard` 미러 — `bundle.Latest.CFlag`). `true`면 "이미 C_Flag=1 → 컨슈머가 이 쓰기를 스킵할 수 있음". (S-F3a 코드리뷰 이연 `todo.md:41` 흡수.)
- `ops.ts`의 `EnqueueData`(또는 O6 전용 형상)에 `cFlagGuard?: boolean` 반영. O6 성공 토스트가 `cFlagGuard`면 O4처럼 "큐 수락됨 — 진행 중이라 스킵될 수 있음" 경고로 정직 표면화(거짓 성공 금지).
- 409(Ready 아님) 응답은 `ops.ts` `postOps`의 기존 `!ok` 경로로 자동 환원 → 호출부가 명시 메시지 토스트.

### 3-D. FE Ready 게이트 + 정직 표면화  `OpsControls.tsx` (+ `OpsPage.tsx` 이미 wordState 전달)
- `wordState.word.ready`(`SorterWordState.word.ready`, SignalR 실시간)로 **O4/O6를 not-Ready일 때 차단**: 버튼 비활성 또는 확인 다이얼로그에서 강경고 + 확인 차단(사수 판단 — disable 우선, 상태 근거 문구 표시). `!online`도 동일 취급.
- **O5 Clear-R는 not-Ready여도 활성 유지**(복구 도구 — Q1). 기존 danger 경고만.
- 409(Ready 아님) 응답을 성공으로 위장하지 않고 명시 토스트(fail-loud). O6 `cFlagGuard` advisory도 정직 표면화.
- readiness 근거는 이미 `OpsPage`가 `ReadinessStrip`(online/ready/busy)로 노출 — 게이트 사유를 사용자가 볼 수 있음.

### 3-E. #1(SetTgtFloor "안 바뀜")은 버그 아님 — 설명·표면화만
- `TgtFloor≠0`이면 컨슈머가 스킵하는 것은 **설계된 핑퐁 게이트(절대규칙 #2)**다. 이미 O4 응답 `pingPongGuard` + 다이얼로그 경고 문구(`OpsControls.tsx:132-139`)로 정직 표면화됨. **스코프 밖(해결·설명 완료).** (선택·minor: FE가 게이트 사유를 더 또렷이 보여줄 수 있으나 이번 스프린트 필수 아님 — defer.)

---

## Absolute Rules Compliance

- **#1 (단일 쓰기 큐):** 쓰기는 여전히 컨슈머 큐만. 사전점검·fresh-read는 **읽기**(컨슈머는 `_clientLock` 내부 FC03 읽기, 컨트롤러는 `bundle.Latest`). 컨트롤러의 직접 Modbus 쓰기 0.
- **#2 (TgtFloor==0 게이트):** fresh-read가 `TgtFloor==0` 판정을 실제 값 기준으로 **정확화**(우회 아님). floor>=1만 수락 유지.
- **#3 (WCS 비클리어):** 변경 없음 — floor=0 수동 리셋 미노출 유지.
- **#4 (Ready 의미):** Ready==0 = 분류 중 또는 이동 중(BUSY). 수동 O4/O6는 이때 거부(사전점검+FE). 자동/오케스트레이트는 정당히 진행(공유 컨슈머 case 무변경).
- **#6 (필드명):** Ops body/응답 필드 = `operatorName`·`floor`·`cellNo`·`seq`·`pingPongGuard`·신규 `cFlagGuard`. RCS 계약 필드 무관.
- **#7 (하드코딩 타이밍 금지):** 신규 타이밍 상수 도입 0. 폴 주기·타임아웃 기존 appsettings 유지.
- **#8 (Core 순수 함수):** `Wcs.Core`(`DepositDecider`·`RegisterMap`·모델) **무변경**. 판정 로직은 컨슈머 가드(I/O)와 분리 유지.

---

## Evaluation Criteria (Evaluator 판정 기준 + 가중치)

- **[30%] 안전·PROTECTED ZONE(하드):** (i) 검증 전 과정 **Sim3ds TCP + 스크래치 DB**·실 COM1/RTU·현장 DB 접근 0, (ii) `PlcGateway` 변경이 **fresh-read 가드로 최소화**·큐/락/RMW/이벤트 구조 무변경·`HandshakeOrchestrator` 무접촉, (iii) `Wcs.Core` 무변경. **하나라도 위반 시 전체 FAIL.**
- **[30%] 기능 완결(#3):** rapid-double CellAssign → 2번째 거부(C 영역 미덮어씀), Ready==0 수동 O4/O6 → 거부(409 + FE 차단), O5는 Ready 무관 허용, O6 `cFlagGuard`/409 정직 표면화. **fresh 증거**(백엔드 신규 테스트 + Playwright/live 관찰).
- **[20%] 회귀 0(하드 관심):** 정상 오케스트레이트 핸드셰이크(S1~S6·핸드셰이크군) 무손상, 자동 IF-09 정렬(`Ready==0` 기입 포함) 무손상, `dotnet test` 312 baseline + 신규 전건 GREEN, FE `tsc`/`eslint`/`build` 클린, b2b·F1·F2 무손상.
- **[15%] #2 라벨 + 정직 UX:** O4/O6 가시 라벨(값 있을 때도 구분), 접근성 유지(중복 라벨링 회피), 파괴적 조작 경고 유지, 콘솔 0 에러.
- **[5%] 결정성·인프라 정합:** fresh-read 가드 테스트가 **비-flaky**(반복 GREEN — 큐 직렬+임계구역 read+write로 결정적), 하드코딩 타이밍 0, 기존 프리미티브/톤 재사용.

---

## Completion Conditions (Evaluator PASS 최소 조건 — 전부 충족)

1. **전체 스위트 GREEN:** `dotnet test backend/Wcs.sln` = **312 baseline + 신규 테스트** 전건 GREEN. 착수 시 clean run으로 baseline 카운트 확인(단일 run 신뢰 금지 — 실-Sim I/O 테스트는 부하 flake 이력 있음, ≥5회 반복 또는 관련군 반복으로 결정성 확인).
2. **신규 백엔드 테스트(스펙 입증):**
   - rapid double CellAssign(같은 소터, 큐/컨슈머 경유) → **2번째 거부, C 영역(D0/D1)이 첫 값 유지**. Sim 레지스터 + skip 로그로 이중 입증. **결정적**(fresh-read가 write#1의 C_Flag=1을 관측).
   - `Ready==0` 상태에서 수동 O4/O6 POST → **409 거부**(enqueue 안 됨·Sim 미변경). Sim을 Ready=0(분류/이동 또는 frozen 상태)로 몰아 검증.
   - **정상 오케스트레이트 핸드셰이크 무회귀**: S1~S6(또는 대표 핸드셰이크 시나리오) 여전히 성공 — fresh-read/사전점검이 orchestrated C→R→ClearR을 깨지 않음.
   - (선택) 자동 IF-09 정렬이 `Ready==0`에서 여전히 TgtFloor 기입(공유 컨슈머 case 무회귀) 확인 — 기존 S3류로 커버되면 재사용.
3. **fresh-read 가드 결정성:** rapid-double 테스트가 sleep/타이밍 의존 없이(큐 직렬 + `_clientLock` read+write 원자성) 반복 GREEN.
4. **하드코딩 타이밍 0:** 신규 상수 없음. **마이그레이션 0**(스키마 무변경 — 신규 필드 `cFlagGuard`는 응답 DTO 전용, DB 무관).
5. **PLC-코어 최소·protected call-out:** `git diff -- backend/src/Wcs.PlcGateway/PlcGateway.cs`가 fresh-read 가드 교체에 국한. `HandshakeOrchestrator.cs`·`Wcs.Core` diff 0.
6. **프론트 클린:** `frontend/`에서 `npm run typecheck`·`npm run lint`·`npm run build` 통과(경고/에러 0). O4/O6 가시 라벨 렌더·not-Ready 차단·409/`cFlagGuard` 정직 표면화 Playwright/live 관찰.
7. **SAFETY:** 검증 전 과정 Sim3ds TCP + 스크래치/시드 DB. 실 COM1/RTU·현장 DB 미접근(기동 설정·로그로 증거).
8. **회귀 0:** b2b·모드 토글·F1(모니터링)·F2(`SortersPage`/`WordPanel` 읽기) 무손상. `SortersPage` 무접촉.

---

## Scope OUT (이 계약에 흡수 금지)

- **`HandshakeOrchestrator` R_Flag arming/off-by-one 연쇄 fix** → 별개 현장 작업(#31·묶음 D-①). 무접촉.
- **컨슈머-측 manual-only Ready 게이트 / `PlcWrite` origin 태그** → Q3 override 시에만(비권장·표면 확대).
- **슈트(O1 clear)·CHUTE pause/resume** → S-F3b Q3 후속 이관(읽기 열거 엔드포인트 부재) 그대로.
- **임의 레지스터/D4 비트 편집 UI, 인증 UI** → 설계 LOCK.
- **S-F3a/S-F3b 코드리뷰 이연 minor**(DRY·a11y·async 등, `todo.md`) 중 본 항목과 무관한 것 → 이번 스코프 밖(단 O6 `cFlagGuard`·라벨은 흡수).
- **신규 프론트 라이브러리·CDN 추가** → 폐쇄망 금지.

---

## Multi-Instance / Project Type / Verification

- **Parallel Modules:** N/A (단일 응집 변경 — PlcGateway 가드 ↔ OpsController 사전점검 ↔ ops.ts ↔ OpsControls가 한 흐름으로 결선. 병렬 분할 이득 없음). 순차 단일 Generator. 단, #2(FE 라벨)는 #3와 독립·저위험이라 같은 Generator가 함께 처리.
- **Evaluation Dimensions:** **functional + safety (2 Evaluators)** — S-F3a와 동형. #3가 **PLC 쓰기 경로(안전 필수·PROTECTED)**를 건드리므로 safety 차원을 functional과 **병렬 전문 검토**로 격리(둘 다 PASS해야 APPROVED). safety = [30% PROTECTED ZONE] + [20% 회귀 0]의 하드 관심(공유 컨슈머 case 무회귀·Sim 전용·`HandshakeOrchestrator` 무접촉·`Wcs.Core` 무변경)을 독립 관점으로 검증.

- **Detected Project Type:** **Full-stack** — 이번 변경 표면 = **Backend(PlcGateway 코어 + OpsController) + Frontend(OpsControls·ops.ts).** Web/UI **및** Backend/API 슬롯 **둘 다** 채운다.

- **Verification Scenarios (Backend/API):**
  - **핵심 동작:** (a) rapid double CellAssign → 2번째 거부·C 영역 첫 값 유지(Sim 레지스터 + 컨슈머 skip 로그). (b) `Ready==0` 수동 O4/O6 → 409·enqueue 0·Sim 미변경. (c) 정상 오케스트레이트 핸드셰이크(S1~S6) 무회귀. (d) O6 `cFlagGuard` advisory·409 body 형상.
  - **엔드포인트/컨슈머:** `POST /api/ops/sorters/{destId}/tgtfloor`·`/cell-assign`(사전점검 409·advisory), `PlcGateway.ProcessWriteAsync`(fresh-read 가드), `EnqueueClearRAsync`(무변경 확인).
  - **상태:** Ready=1(정상 통과)·Ready=0(거부)·OFFLINE·C_Flag=1(스킵)·TgtFloor≠0(핑퐁 스킵)·rapid-double 경합.
  - **관찰(하드):** `SimWebApplicationFactory`(동적 포트 TCP Sim + in-memory SQLite)로 실행, Sim 레지스터 스냅샷 + `operation_log`(PLC_WRITE/STATE) + HTTP 상태코드로 입증. **실 COM1/RTU·현장 DB 미접근**(기동 설정 증거). 데이터 정합 없음(마이그레이션 0).
  - **회귀 관찰:** 핸드셰이크군·자동 IF-09 정렬(`Ready==0` 기입)·기존 O4/O6 성공 경로 무손상. 312 baseline 유지.

- **Verification Scenarios (Web/UI):**
  - **핵심 플로우:** `/ops` 진입 → 소터 선택 → (i) O4/O6 입력칸 **가시 라벨 확인**(값 타이핑 후에도 "셀 번호"/"순번"/"층" 구분 유지). (ii) 소터 **not-Ready(Busy)일 때 O4/O6 차단**(버튼 비활성/확인 차단)·O5는 활성 유지. (iii) Ready일 때 O4/O6 정상 → 확인 다이얼로그 + 작업자 이름 → Ops 호출. (iv) 409(Ready 아님)·O6 `cFlagGuard` 경고가 **명시 토스트로 정직 표면화**(성공 위장 0).
  - **컴포넌트/페이지 (touched):** `OpsControls.tsx`(라벨·Ready 게이트·`cFlagGuard` 표면화), `ops.ts`(`cFlagGuard` 형상). **재사용(무수정):** `WordPanel`·`ui/{dialog,button,select,badge,card}`·`lib/{toast,queries,signalr,useMonitorHub}`, `OpsPage.tsx`(이미 wordState 전달 — 최소/무변경).
  - **상태:** Ready/Busy(readiness 스트립 근거)·OFFLINE·로딩·empty(소터 0)·400/404/409·`pingPongGuard`(O4)·`cFlagGuard`(O6)·busy(다이얼로그 잠금).
  - **상호작용:** 클릭(버튼·토글), 타이핑(floor/cellNo/seq/작업자), 셀렉트(소터), 키보드(Esc/Tab 트랩). **접근성:** 가시 라벨 ↔ 입력 연결(`htmlFor`/래핑), 확인 다이얼로그 포커스 트랩 유지.
  - **관찰(하드):** Playwright 네트워크 캡처로 O4/O6가 **Ready일 때만 `/api/ops/*` 요청**을 보냄·not-Ready면 요청 0(FE 차단)·409면 토스트 노출을 확인. **콘솔 에러 0. 외부(비-동일출처) 요청 0**(폐쇄망).
  - **회귀 관찰:** b2b 모드 토글·b2b 내비·F1·"3DS 워드"(F2 읽기) 정상.

> Planner self-check — Detected project type: Full-stack (touched surface: Backend PlcGateway+OpsController AND Frontend OpsControls+ops.ts). Required scenario slots: 6 (user flows, components/pages, states, interactions, console/network observation, regression) filled for Web/UI; Backend/API scenario slot filled (behaviors, endpoints/consumer, states, hard observation via Sim+scratch DB, regression). All slots filled: yes (both Web/UI and Backend/API populated — no N/A).
