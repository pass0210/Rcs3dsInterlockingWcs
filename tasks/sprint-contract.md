# Sprint Contract — S-IF08-READY-PUSH

> Planner Subagent · 2026-07-13
> 계약 정본: `docs/wcs_rcs_interface_kr.html` §IF-08 (PR #55~58 병합됨). 이 스프린트는 계약을
> 재정의하지 않는다 — **코드를 그 계약에 맞추는** 것이다. 배경(RCS 수신 API는 `UpdateChuteState`
> 하나뿐 · destination-status 와이어 폐지 · ready 의미는 `next_state` 3/2로 운반)은 2026-07-11 확정된
> 사실이며 재논의 대상이 아니다.

---

## Goal

WCS의 아웃바운드 "목적지 수용 상태 알림"을 확정 계약(단일 와이어 `UpdateChuteState`)에 정합시킨다.

- 폐지된 `destination-status` 와이어(`POST {RCS}/api/v1/destination-status` + `{chuteNo, ready, timeStamp}`)로
  ready 전이를 발신하던 경로를, 확정 와이어 `PUT {RCS}/api/UpdateChuteState` +
  `{chute_numbers:[n], next_states:[2|3]}`로 재배선한다. `3`=받을 수 있음 / `2`=받을 수 없음.
- 현재 **두 개**로 갈라진 발신 소스(`DestinationStatusPusher`의 ready 전이 발신 +
  `ChuteStatePusher`의 운영자 pause/resume 발신)를 목적지당 **단일 발신 소스**로 통합해, 같은
  `chuteNo`에 이중/모순 발신(한쪽 `3`·다른 쪽 `2`)이 물리적으로 불가능하게 만든다.
- 발신 상태 합성을 계약대로 바꾼다: 소터의 수용상태 = **운영 ready ∧ 비정지**, 슈트 = 비만재 ∧ 비정지.
  **셀 만재는 발신에 반영하지 않는다**(IF-05 dispatch 차단만 유지).
- 폐지 와이어의 코드·설정 잔재를 **전부 제거**한다(0 잔재).

이 스프린트는 순수 `Wcs.Api` 서비스 계층 재배선이다. PLC 레지스터·핸드셰이크·`Wcs.PlcGateway`·
`Wcs.Core` 판정 로직은 무접촉이다.

---

## Implementation Scope (Generator가 구현할 것 — WHAT)

> 기술 상세(어떻게 통합할지, 어느 클래스로 흡수할지)는 Generator 재량. 아래는 계약이 요구하는 WHAT만.

### SC-1. ready 전이 발신 재배선 (폐지 와이어 → 확정 와이어)
현재 `DestinationStatusPusher`가 폐지된 `IRcsPushClient`(destination-status 와이어)로 ready 불리언을
발신한다. 이를 확정 `UpdateChuteState` 와이어로 발신하도록 재배선한다. 발신 값은 ready 불리언이 아니라
`next_state`(수용가능 `3` / 불가 `2`)로 접어 보낸다.

### SC-2. 발신 상태 합성 변경 (계약 매핑 정합)
목적지당 "발신할 수용상태"를 계약대로 합성한다:
- **소터:** `next_state=3` ⇔ 온라인 ∧ 운영층 정렬 ∧ 비분류(Ready=1) ∧ **비정지**. 하나라도 아니면 `2`
  (분류중·이동중·미정렬·오프라인·운영자정지 → `2`). 즉 발신 수용상태 = **운영 ready ∧ !paused**
  (현재 `DestinationStatusService.Compute().Ready`(운영 ready)는 paused를 제외하고 있으므로, 발신
  합성에서 paused를 다시 접어 넣어야 한다).
- **슈트:** `next_state=3` ⇔ 비만재 ∧ 비정지. 아니면 `2`.
- **셀 만재(SorterFull)는 발신 합성에서 제외.** 만재여도 운영상태 OK ∧ 비정지면 `3`을 유지한다
  (만재는 IF-05 dispatch에서만 차단 — 2단계 게이트 분리 현행 유지).

### SC-3. 두 발신 소스를 단일 소스로 통합
`DestinationStatusPusher`(ready 전이)와 `ChuteStatePusher`(운영자 pause/resume 전이)를 목적지당 단일
발신 소스로 통합한다. 통합 방식(둘 중 하나로 흡수 / 새 단일 서비스)은 Generator 재량이되:
- 같은 `chuteNo`에 대해 이중·모순 발신이 발생하지 않아야 한다.
- 전이 감지·전이당 정확히 1회·복구 재푸시의 동시성 멱등(중복 0·누락 0)이 보존되어야 한다.
- 관심사 분리(전이 감지 → 1건 전송+재시도)가 유지되어야 한다.

### SC-4. 폐지 와이어 완전 제거
- `RcsPushClient` + `IRcsPushClient` + `DestinationStatusPushPayload` 제거.
- `WcsOptions` 내 `RcsPushOptions` 제거, `appsettings.json`의 `Wcs:RcsPush` 섹션 제거.
- `Program.cs`의 해당 DI 배선(named HttpClient `RcsPush` · `IRcsPushClient` · `DestinationStatusPusher`
  중 폐지 대상) 정리.
- 프로덕션 코드·설정에 `destination-status`·`RcsPush*`·`IRcsPushClient`·`DestinationStatusPushPayload`
  심볼/문자열이 0건 남게 한다.

### SC-5. 관찰 주기 설정 이전
폐지되는 `RcsPushOptions`의 `SorterObserveIntervalMs`(소터 스냅샷 관찰 주기 — 분류 사이클 ready 전이
감지에 필수)를 존치되는 push 설정 섹션으로 이전한다. 운영자 pause/resume 이벤트만으로는 소터 분류
사이클(3↔2) 전이를 감지할 수 없으므로 스냅샷 관찰은 반드시 유지된다. 관찰 주기는 설정값(하드코딩 금지).

### SC-6. 트리거 정합
- 수용상태가 **실제로 전이할 때만** 발신(운영자 pause/resume 포함 — 값이 같으면 미발신). 고정 주기 아님.
- 기동 시 전 활성 목적지(슈트+소터)의 현재 수용상태를 목적지당 1회 부트스트랩 발신.

### SC-7. 재시도 유지
존치되는 push 클라이언트(`ChuteStatePushClient`)의 지수 백오프 재시도(설정값, 기본 3회 1s/2s/4s)를
유지한다. 재시도·백오프·타임아웃·관찰 주기 전부 설정값(하드코딩 0 — 절대규칙 #7).

### SC-8. 테스트 스위트 재작성·정합 (0 회귀 필수)
- 폐지 와이어를 검증하던 테스트(`RcsPushTests` 및 `DestinationStatusPusher` 경로)를 존치 와이어(가짜
  `UpdateChuteState` 수신 서버 — `FakeChuteStateServer` 재사용) 기준으로 재작성한다.
- `ChuteStatePushTests`를 확장해 합성(ready∧!paused)·단일소스·부트스트랩·소터 분류 사이클 전이를 포함한다.
- **공유 표면 정합(스코프 포함 — 이걸 놓치면 스위트 컴파일/실행 붕괴):**
  - `RcsPushTests.cs`의 `RcsPushWebApplicationFactory`는 `SorterCellFullnessTests`·`Field20CellsGateTests`·
    `SorterPushOperationalTests`가 **공유**하는 픽스처다. 폐지 심볼 제거 후에도 이 다운스트림 스위트가
    컴파일·GREEN 유지되도록 픽스처를 이전/치환한다.
  - `backend/tests/Wcs.Tests/E2E/E2EInfrastructure.cs`가 `Wcs:RcsPush:*` 키·`DestinationStatusPusher`를
    배선한다. 존치 와이어 기준으로 갱신한다.

---

## Absolute Rules Compliance (CLAUDE.md)

- **#1 (PLC 단일 큐):** 무접촉 — 아웃바운드 HTTP 재배선이지 PLC 쓰기가 아니다. Modbus/게이트웨이/쓰기큐 0 변경.
- **#7 (하드코딩 금지):** BaseUrl·Path·재시도·백오프·타임아웃·관찰 주기 전부 appsettings. URL/타이밍 리터럴 0.
- **#6 (필드명):** 이 와이어 필드는 **RCS 계약**(`chute_numbers`/`next_states`, snake_case) — WCS 내부
  camelCase(`chuteNo` 등)와 다르다. 계약 필드명 정확 준수(camelCase 직렬화 의존 금지).
- **예외 삼킴 금지:** 푸시 최종 실패는 명시 로깅 + false 반환(Fail-Loud). 연결 실패를 성공 위장 금지.
- **#2/#3/#4/#5/#8:** 전부 무관(PLC/TgtFloor/Ready/판정엔진 무접촉). `Wcs.Core`·`Wcs.PlcGateway`·
  `HandshakeOrchestrator` diff 0.

---

## Evaluation Criteria (Backend/API — 가중치)

1. **API 계약 정합성 (★★★)** — 발신 와이어가 계약 정본(§IF-08)과 바이트 수준 일치: `PUT`, snake_case
   `chute_numbers`/`next_states`, 인덱스 정렬 단건 배열, 값 ∈ {2,3}, 상태 매핑(3/2)이 계약대로. 소터
   합성 = 운영 ready ∧ !paused, 슈트 = !full ∧ !paused, 셀 만재 미반영.
2. **아키텍처 (★★★)** — 목적지당 단일 발신 소스(이중/모순 발신 구조적 불가). 전이당 1회·복구 재푸시의
   동시성 멱등(중복 0·누락 0) 보존. 관심사 분리(전이 감지 vs 1건 전송) 유지.
3. **Craft (★★)** — 하드코딩 0(전 타이밍 설정값). 폐지 와이어 잔재 0(grep-clean). DORMANT(BaseUrl
   미설정) 시 크래시 0·발신 0. 예외 삼킴 0(Fail-Loud). teardown 경쟁 방어 유지.
4. **Functionality (★★)** — 계약의 트리거·합성·부트스트랩·재시도가 실제 동작으로 재현. IF-05 dispatch
   (셀 만재 차단 포함) 회귀 0.

---

## Completion Conditions (Evaluator PASS 최소 조건 — 전부 충족)

1. 아래 §Verification Scenarios **전부**가 자동 xUnit(가짜 `UpdateChuteState` 수신 서버 + Sim3ds(TCP) +
   SQLite)로 재현되어 PASS. 인메모리 단언만으로 PASS 금지 — "가짜 RCS 서버가 실제 수신한 JSON 본문"으로 입증.
2. `dotnet test backend/Wcs.sln` 전체 스위트가 **독립 실행**에서 GREEN(0 회귀). Evaluator가 처음부터 재실행.
   단일 run 신뢰 금지(실-Sim I/O 테스트 부하 flake 이력 — 관련군 반복 또는 ≥5회로 결정성 확인).
3. 폐지 와이어 grep-clean: 프로덕션 코드·`appsettings.json`에 `destination-status`·`RcsPush*`·
   `IRcsPushClient`·`DestinationStatusPushPayload` 0건(grep 증거).
4. `dotnet build backend/Wcs.sln` 경고/에러 0(프로젝트 설정 기준). 정적 검사 결과를 sprint-feedback.md에 기록.
5. 스키마 무변경 → **마이그레이션 0**(신규 마이그레이션 파일 생성 시 계약 위반).
6. 실 PLC/COM1·현장 DB 미접촉(검증은 Sim3ds TCP + in-memory SQLite로만).

---

## Scope OUT (이 계약에 흡수 금지)

- **PLC 레지스터/핸드셰이크/`Wcs.PlcGateway` 변경** — 무접촉(절대규칙 #1 보존).
- **IF-05/09/10 인바운드 계약·판정 로직 변경** — 무접촉(회귀만 방지). `Wcs.Core` diff 0.
- **셀 만재를 발신에 반영** — 계약상 만재는 IF-05 dispatch만 차단(발신 미반영). 현행 유지.
- **배치(다건) 발신·전이 코얼레싱** — 계약 배열 구조는 유지하되 전이당 단건. 배치는 후속.
- **프론트/모니터링 UI** — 요청 없음. 이 스프린트는 백엔드 서비스 계층 전용.
- **신규 인바운드 WCS 엔드포인트** — 이 와이어는 WCS가 **호출하는** 아웃바운드.

---

## Parallel Modules

N/A (단일 모듈). 두 pusher·존치 클라이언트·옵션·DI·테스트가 상호 의존하고 파일을 공유하므로
경계-청정 분할 불가. 기본 1 Generator.

## Evaluation Dimensions

functional only. 신규 보안/성능 민감 표면 없음(내부 아웃바운드 HTTP 재배선). 동시성 정합(단일소스·
이중발신 금지)은 별도 전문 dimension이 아니라 기능 정합의 일부로 검증. 기본 1 Evaluator.

---

## Detected Project Type: Backend/API

> 리포 구조 신호: `backend/src/Wcs.Api`에 ASP.NET Core Controller + 서버 진입점(`Program.cs`)이 있고,
> `frontend/`에 React SPA(브라우저 진입점)도 함께 존재한다 — 리포 전체로는 Full-stack 신호다. 그러나
> **이 스프린트의 변경 표면은 `Wcs.Api` 백엔드 서비스 계층(HostedService·아웃바운드 HTTP 클라이언트·옵션·
> DI) + xUnit 테스트로 한정**되며 프론트엔드 파일·브라우저 진입점·HTTP 엔드포인트 라우팅을 일절 건드리지
> 않는다. 발신 채널 상대역은 협력사 RCS(외부 시스템)이지 이 리포의 프론트엔드가 아니다. 따라서 검증 타입은
> **Backend/API**로 확정한다(자동 xUnit 대 가짜 RCS 수신 서버 — 이 리포의 동종 아웃바운드 push 스프린트
> RcsPush Phase 2·ChuteStatePush·SorterPushOperational이 확립한 패턴). 브라우저 검증은 이 스프린트에
> 검증할 프론트엔드 델타가 없어 N/A.

---

## Verification Scenarios (Backend/API — 필수)

### 이 스프린트가 건드리는 엔드포인트 / 와이어 (method + path)

- **아웃바운드(WCS = 클라이언트 · 재배선 대상):** `PUT {RCS base}/api/UpdateChuteState`
  — body `{chute_numbers:[n], next_states:[2|3]}` (snake_case, 전이당 단건). 이 스프린트가 재배선하는
  유일한 아웃바운드 채널. (폐지 제거 대상: `POST {RCS}/api/v1/destination-status`.)
- **인바운드(무변경 · 회귀 방지):** `POST /api/v1/destination-query`(IF-05) — 발신과 상태 서비스를 공유
  (`DestinationStatusService`)하므로 회귀 없음을 확인. `POST /api/ops/destinations/{id}/pause|resume`
  (운영자 전이 발원지) — 전이가 통합 발신으로 이어짐을 확인.
- **검증 대역:** 가짜 RCS 수신 서버(in-process Kestrel 동적 포트, `PUT /api/UpdateChuteState` 수신·기록·
  거부토글) — 기존 `FakeChuteStateServer` 재사용.

### Happy path (입력 → 기대 출력 형태)

- **VS-1 소터 분류 사이클(전이당 1건·순서):** 소터 수용상태 `3`→`2`→`3` 전이 시 가짜 서버가
  `{[chuteNo],[2]}` → `{[chuteNo],[3]}`를 그 순서로 각 1건 수신. 값이 안 바뀌는 폴에서는 **미발신**
  (폴마다 폭주 0).
- **VS-2 기동 부트스트랩:** 기동 시 전 활성 목적지(슈트+소터)의 현재 수용상태가 목적지당 정확히 1회 발신됨.
- **VS-3 운영자 pause 합성(핵심):** 소터가 운영상태 OK(온라인·정렬·Ready=1)여도 운영자 PAUSED면 발신값
  `2`(합성 `ready ∧ !paused` 검증), RESUME 시 `3`. 폐지 전 두 소스로 갈렸던 값이 단일 소스로 정합됨.
- **VS-4 슈트 만재/정지:** 슈트 만재 또는 정지 전이 → `2`, 해소 → `3`.
- **VS-5 소터 셀 만재 무영향(핵심):** 소터 셀이 만재(SorterFull)여도 운영상태 OK ∧ 비정지면 발신값 `3`
  유지(만재로 `2` 발신 없음). 동시에 IF-05 dispatch는 그 piece를 여전히 차단(회귀 0) — 2단계 게이트 분리.
- **VS-9 와이어 형태 정합:** 메서드 `PUT`, 키가 정확히 `chute_numbers`·`next_states`(camelCase 아님),
  두 배열 동일 길이·인덱스 정렬·길이 1, 값 ∈ {2,3}. 성공 응답 판정(2xx ∧ `flag==1`)이 기존과 동형.

### 관련 오류·경계·회귀 케이스 (골라 채움 — 패딩 아님)

- **VS-6 DORMANT(BaseUrl 미설정):** RCS base URL 미설정 시 발신 0·크래시 0, 전이 여러 번 발생시켜도 수신 0,
  인바운드 IF-05 정상(200). (현재 테스트 배포 상태 = DORMANT.)
- **VS-7 단일 소스·이중/모순 발신 금지(핵심):** 같은 `chuteNo`에 운영자 pause 전이와 ready 전이가 겹쳐
  발생해도 중복·모순 발신 없음. 최종 발신값이 최종 합성 상태와 일치(중복 0·누락 0 멱등).
- **VS-8 폐지 와이어 완전 제거:** 프로덕션 코드·`appsettings.json`에 `destination-status`·`RcsPush*`·
  `IRcsPushClient`·`DestinationStatusPushPayload` 심볼/설정 0건(grep 증거).
- **VS-11 재시도·복구(RCS 미도달):** RCS가 비2xx/실패 응답 → 지수 백오프 재시도(설정 3회) 후 명시 실패
  (Fail-Loud, 조용한 드롭 0) → 복구 후 최신 수용상태가 RCS에 도달.
- **VS-10 전체 스위트 GREEN(0 회귀):** 공유 픽스처(`RcsPushWebApplicationFactory` 소비 스위트) + E2E
  하네스 정합 후, `dotnet test backend/Wcs.sln` 전체가 독립 실행에서 GREEN.

> Planner self-check — Detected project type: Backend/API. Required scenario slots: 3 (endpoints touched [method+path], happy path per endpoint, relevant error/edge/regression cases per endpoint). All slots filled: yes — endpoints(아웃바운드 UpdateChuteState 재배선 + 무변경 인바운드 IF-05/pause·resume + 가짜 RCS 대역) · happy(VS-1 분류사이클 / VS-2 부트스트랩 / VS-3 pause 합성 / VS-4 슈트 full·pause / VS-5 셀만재 무영향 / VS-9 와이어 형태) · error·regression(VS-6 DORMANT / VS-7 단일소스 이중발신 금지 / VS-8 폐지와이어 제거 / VS-11 재시도·복구 / VS-10 전체 GREEN). Web/UI 슬롯은 이 스프린트에 프론트 표면이 없어 정당하게 부재. 배경 계약(destination-status 폐지·next_state 3/2 운반)은 2026-07-11 확정 — open question 없음.
