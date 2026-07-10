# Sprint Contract — S-CHUTESTATE-PUSH (신규 아웃바운드: WCS → 고객 `PUT /api/UpdateChuteState` · 호스트 미정 · DORMANT 선적용)

> Planner Subagent · 2026-07-09
> 발원: 고객이 `PUT /api/UpdateChuteState` API 스펙(`docs/UpdateChuteState_API_EN.md`)을 제공. WCS가 슈트/소터 상태(Pause / Manual-open)를
> 고객 시스템으로 **아웃바운드 푸시**한다. **HOST(BaseUrl)는 고객이 추후 제공** — 지금은 **완전히 구현하되 BaseUrl 미설정 시 DORMANT(no-op)**로 출하한다.
> 이는 기존 **RcsPush(IF-08 WCS→RCS 푸시)** 패턴의 구조적 형제(sibling)다.
>
> **소비/신설 대상(예상):**
> - (신설) `backend/src/Wcs.Api/Services/ChuteStatePushClient.cs` — 아웃바운드 PUT 클라이언트(RcsPushClient 미러).
> - (신설) `backend/src/Wcs.Api/Services/ChuteStatePusher.cs`(또는 동등 observer) — pause/resume 전이 관찰 → 클라이언트 호출.
> - (수정) `backend/src/Wcs.Api/Infrastructure/WcsOptions.cs` — `ChuteStatePushOptions` + `Wcs:ChuteStatePush` 섹션.
> - (수정) `backend/src/Wcs.Api/appsettings.json` — `Wcs:ChuteStatePush`(BaseUrl:null).
> - (수정) `backend/src/Wcs.Api/Program.cs` — named HttpClient + 클라이언트 + observer DI 배선(append).
> - (관찰 훅) `backend/src/Wcs.Api/Services/DestinationControlService.cs` — pause/resume 전이 이벤트/훅 관찰(코어 동작 무변경).
> - (신설) `backend/tests/Wcs.Tests/ChuteStatePushTests.cs` — 가짜 고객 서버 대상 검증(RcsPushTests 미러).
>
> **스택 PR 교훈(MEMORY):** 이 브랜치는 **develop(또는 최신 통합 브랜치)에서 분기**한다. 스택 브랜치로 병합 금지·병합 후 base 실재 검증.

---

## ✓ Confirmed decisions (LOCKED — 사수 게이트 응답 2026-07-09. 착수 전제·변경 금지)

> 아래 3건은 **사수가 확정한 계약**이다. 추론·기본값이 아니라 **LOCK**이다. Generator/Evaluator는 이 결정을 전제로 동작한다.

- **Q-a (trigger → next_state) — LOCKED.** PAUSE(`DestinationControlService` **PAUSED** 전이, O2) → **`next_state = 2`**(Pause chute). RESUME(**RESUMED** 전이, O3) → **`next_state = 3`**(Manual-open). **둘 다 이 API로 푸시**한다.

- **Q-b (chute_numbers) — LOCKED.** **WCS `Destination.ChuteNo`를 그대로(1:1) 사용**한다 — 목적지의 `ChuteNo`를 `chute_numbers` 값으로 전송(`chute_numbers:[dest.ChuteNo]`). **`ChuteNoMap` config는 도입하지 않는다**(사수가 직접 1:1 선택 — 매핑 테이블 불요, 더 단순). 기존 RcsPush도 `Destination.ChuteNo`를 키로 보내므로 일관. (향후 다른 external ID가 필요한 고객이 생기면 그때 별도 변경 — 지금은 1:1.)

- **Q-c (scope) — LOCKED.** 운영자 **O2/O3 PAUSED/RESUMED 전이만**, **CHUTE·SORTER_3D 목적지 둘 다**. **FULL(만재)·O6(cell-assign) 제외.** (FULL=capacity 자동 상태이지 pause 아님. O6=소터 셀 진단 PLC 쓰기이지 슈트 open/close 아님 — Manual-open=3은 RESUME에 대응.) RcsPush의 복합 `ready` 전이도 별개 채널(무접촉).

---

## Goal

고객 제공 `PUT /api/UpdateChuteState` 계약대로 **WCS→고객 아웃바운드 상태 푸시**를 완전 구현하되, **BaseUrl(호스트) 미설정 시 완전 비활성(no-op)**으로 출하한다("host 제외 미리 적용"). 활성화는 **고객이 호스트를 주면 `Wcs:ChuteStatePush:BaseUrl` 한 값만 설정**하면 되며 코드/재빌드/마이그레이션이 필요 없다. 기존 RcsPush(IF-08) 패턴의 구조적 형제로 만들어 폴링·재시도·Fail-Loud·dormant 규약을 재사용한다.

---

## Implementation Scope (Generator가 구현할 것 — WHAT)

### SC-1. 설정 섹션 신설 — `Wcs:ChuteStatePush`(RcsPush 미러, 전부 appsettings·하드코딩 0 절대규칙 #7)
- `WcsOptions`에 `ChuteStatePushOptions ChuteStatePush` 추가 + `Wcs:ChuteStatePush` 섹션. 필드:
  - `BaseUrl` (string?, **기본 null → 푸시 DISABLED**). 고객이 추후 제공하는 **유일한 활성화 값**.
  - `Path` (string, 기본 `"/api/UpdateChuteState"`). BaseUrl에 이어붙임(외부화 기본값 — todo의 RcsPushOptions.Path 문서화 권고 동일 적용).
  - `RetryCount`(기본 3), `RetryBaseDelayMs`(기본 1000), `RetryMaxDelayMs`(기본 4000), `HttpTimeoutMs`(기본 3000) — RcsPush와 동일 의미의 지수 백오프.
  - `IsEnabled => !string.IsNullOrWhiteSpace(BaseUrl)` (RcsPush 동형).
  - **`ChuteNoMap` 없음(Q-b LOCKED = 직접 1:1).** `chute_numbers`는 `Destination.ChuteNo`를 그대로 쓴다 — 매핑 config/딕셔너리/오프셋 도입 금지.
- appsettings.json에 `Wcs:ChuteStatePush` 블록 추가. **BaseUrl 은 커밋 시 null 유지**(고객이 추후 설정). `_comment_`로 "BaseUrl 설정 시 활성, 미설정 시 dormant — 고객이 호스트 제공 시 이 한 값만 설정하면 활성" 명기.
- IOptions 지연 소비(즉시 평가 키 아님) — 테스트가 `ConfigureAppConfiguration`으로 BaseUrl 주입 가능해야 함(2026-06-30 교훈: 즉시 평가 config 키만 UseSetting 필요, IOptions 키는 ConfigureAppConfiguration OK).

### SC-2. 아웃바운드 클라이언트 신설 — `IChuteStatePushClient` / `ChuteStatePushClient`(RcsPushClient 미러)
- **IHttpClientFactory 경유 named client**(직접 `new HttpClient()` 금지 — RcsPush 동일). 타임아웃 = `HttpTimeoutMs`(설정값).
- **HTTP 메서드 = PUT**(RcsPush는 POST — 여기선 계약이 PUT). `PutAsJsonAsync` 또는 동등.
- **요청 body 형상(계약 정확 준수):** `{ "chute_numbers": [<int>...], "next_states": [<int>...] }` — 두 배열 **동일 길이·인덱스 정렬**. **wire 는 snake_case** — STJ 기본 camelCase에 의존하지 말고 `[JsonPropertyName("chute_numbers")]`·`[JsonPropertyName("next_states")]` 명시(RcsPush의 camelCase 관례와 **다름** — 이 계약의 함정).
  - 한 전이당 길이-1 배열(단건)로 전송(트리거가 전이당 1건). 배치(다건)는 계약상 허용이나 이번 스코프는 단건 — 배열 구조는 계약대로 유지.
- **성공/실패 판정:**
  - 성공 = **2xx + `flag == 1`**(스펙: flag:1=처리 성공). 응답 `result[]`(status/msg/chute_id/last_changed, snake_case)는 파싱해 로깅에 활용(부수).
  - 실패 = 비2xx(400 missing-params 포함) **또는** body `{ "result": "Failed" }` **또는** flag != 1 → **재시도(설정 경유 지수 백오프)**. 소진 후 **false 반환 + 명시 ERROR 로깅(Fail-Loud, 예외 삼킴 금지)**. 절대 조용히 드롭 금지.
- **DORMANT no-op:** `IsEnabled==false`(BaseUrl null)면 **HTTP 시도 0**·즉시 false(미발신). RcsPushClient의 방어적 IsEnabled 체크 미러.
- operation_log 부수 기록(성공/실패 전수) — RcsPush의 `IF08_PUSH` 카테고리 미러(예: `CHUTESTATE_PUSH`), 실패는 WARN.

### SC-3. 트리거 관찰(observer) 신설 — pause/resume 전이 → 클라이언트 호출
- **관찰 대상:** 운영자 O2 PAUSE(→DestStatus.PAUSED)·O3 RESUME(→DestStatus.NORMAL) 전이. 발원은 `DestinationControlService.TransitionAsync`(CHUTE·SORTER 공통, 커밋 후).
- **매핑(LOCKED):** PAUSED 전이 → `next_state=2`, NORMAL(RESUMED) 전이 → `next_state=3`(Q-a). `chute_numbers` = **`dest.ChuteNo` 그대로(1:1, Q-b)** — 즉 PAUSED면 `{chute_numbers:[dest.ChuteNo], next_states:[2]}`, RESUMED면 `[..., next_states:[3]]`. 매핑 테이블 없음(unmapped 개념 없음 — 모든 목적지가 ChuteNo를 가짐). "무엇을 푸시하는가"의 게이트는 **전이 종류(PAUSED/RESUMED만)** 지 목적지 필터가 아니다(FULL/O6는 이 훅으로 안 들어옴).
- **관찰 방식(HOW는 Generator 결정, 단 코어 무변경 필수):**
  - **권장:** `DestinationControlService`에 **경량 전이 이벤트/notifier**(예: `event Action<long destId, DestStatus target, DestType> OnTransition`)를 추가해 실제 전이(Transitioned) 시 발화 → observer가 구독. CHUTE·SORTER를 **한 훅으로 균일** 처리하고, pause/resume 판정 로직·DB·인메모리 반영은 **한 줄도 바꾸지 않는다**(이벤트 발화만 append — 관찰 전용).
  - (대안) `ChuteCapacityService.OnChuteStateChanged`(CHUTE만) + 소터 상태 폴 관찰 — RcsPush 방식이나 capacity/ready와 pause를 혼동하기 쉬워 **비권장**(이 API는 복합 ready가 아니라 명시 PAUSED/RESUMED 전이).
- **관찰-전용 원칙(하드):** pause/resume 코어 동작(전이·감사·인메모리 반영·멱등)을 **바꾸지 않는다**. RcsPush가 게이트웨이를 안 건드리고 관찰만 하듯, 이 observer도 전이 로직을 변경하지 않는다.
- **AlreadyInState(멱등 no-op) 전이는 푸시하지 않는다**(실제 상태 변화가 아님 — 스퓨리어스 재푸시 방지). 실제 `Transitioned`만 푸시.
- **Fail-Loud + 비블로킹:** 푸시 실패가 pause/resume HTTP 응답(운영자 O2/O3)을 막지 않는다(fire-and-forget + 내부 재시도). 관찰 루프 예외는 삼키지 않되 코어를 죽이지 않음(RcsPush observe 루프 미러).

### SC-4. DI 배선 — `Program.cs`(append, 기존 배선 무접촉)
- named HttpClient(`ChuteStatePushClient.HttpClientName`, 타임아웃=설정) + `IChuteStatePushClient` 싱글톤 + observer(IHostedService 또는 이벤트 구독) 등록. RcsPush 등록 블록(Program.cs:175-197) 미러.
- BaseUrl null이면 observer가 기동 시 경고 로깅 후 비활성(RcsPush StartAsync의 "미설정 → 경고 후 no-op" 미러) — **크래시 0**.

### SC-5. 테스트 신설 — `ChuteStatePushTests.cs`(RcsPushTests + FakeRcsServer 미러)
- **가짜 고객 서버**(FakeRcsServer 미러 — Kestrel 동적 포트, `PUT /api/UpdateChuteState` 수신·기록, 거부 모드 토글) 구축. **인메모리 GREEN을 근거로 삼지 말고 "가짜 서버가 수신한 실제 JSON 본문"으로 입증**(RcsPushTests 메타교훈).
- 검증 시나리오는 아래 §Verification Scenarios(Backend/API) 참조.

---

## Absolute Rules Compliance (CLAUDE.md)

- **#1 (PLC 단일 큐):** **무관·무접촉.** 이 스프린트는 **아웃바운드 HTTP**이지 PLC 쓰기가 아니다. Modbus/게이트웨이/쓰기 큐 0 변경. (O6 매핑을 스코프에서 제외한 이유이기도 — PLC 쓰기 경로를 건드리지 않음.)
- **#7 (하드코딩 금지):** BaseUrl·Path·재시도·백오프·타임아웃·chute_number 매핑 **전부 appsettings**. URL/타이밍/매핑 리터럴 0(Path 기본 리터럴은 외부화된 기본값 — override 가능·규칙 위반 아님).
- **#6 (필드명):** 이 API 필드는 **고객 계약**(`chute_numbers`/`next_states`/`flag`/`result`/`chute_id`/`last_changed`, snake_case) — WCS 내부 RCS 계약 필드(`chuteNo` 등)와 무관. 계약 필드명 정확 준수.
- **예외 삼킴 금지:** 푸시 최종 실패는 명시 ERROR 로깅 + false 반환(Fail-Loud). 연결 실패를 성공으로 위장 금지.
- **#2/#3/#4/#5/#8:** 전부 **무관**(PLC/TgtFloor/Ready/판정엔진 무접촉). `Wcs.Core`·`Wcs.PlcGateway`·`HandshakeOrchestrator` diff 0.

---

## Evaluation Criteria (Evaluator 판정 기준 + 가중치)

- **[30%] DORMANT 정확성(하드 — "host 제외 미리 적용"의 핵심):** BaseUrl 미설정 시 (i) 기동 크래시 0, (ii) **HTTP 시도 0**(가짜 서버 수신 0), (iii) 인바운드/pause·resume/기존 RcsPush 정상(회귀 0). 가짜 서버 대상 fresh 증거로 입증.
- **[30%] 계약 정합(고객 API):** 요청 body `{chute_numbers, next_states}` **snake_case·인덱스 정렬·동일 길이**, PUT 메서드, PAUSE→2·RESUME→3 매핑, 200+flag==1 성공/400·`{result:"Failed"}`·flag≠1 실패 처리, 재시도(설정 경유 지수 백오프)·Fail-Loud. **가짜 서버가 수신한 실제 JSON 본문**으로 입증(인메모리 GREEN 불가).
- **[20%] 관찰-전용·회귀 0(하드 관심):** pause/resume 코어(전이·감사·인메모리·멱등) 동작 무변경(관찰 훅만 append), `Wcs.Core`·`Wcs.PlcGateway`·`HandshakeOrchestrator`·기존 RcsPush 파이프 무접촉, 전체 스위트 baseline + 신규 전건 GREEN.
- **[15%] Craft·아키텍처(RcsPush 형제성):** IHttpClientFactory 경유·관심사 분리(클라이언트=1건 전송+재시도 / observer=전이 감지+`ChuteNo` 직송), config 외부화, operation_log 부수 기록, 전이 종류 게이트(PAUSED/RESUMED만·FULL/O6 제외), dormant no-op 명료.
- **[5%] 결정성·인프라 정합:** 가짜 서버 대상 테스트 비-flaky(반복 GREEN), 하드코딩 타이밍/URL 0, RcsPushTests 프리미티브(FakeServer·WaitUntilExact·stable-count) 재사용.

---

## Completion Conditions (Evaluator PASS 최소 조건 — 전부 충족)

1. **전체 스위트 GREEN:** `dotnet test backend/Wcs.sln` = **착수 시 clean run으로 확정한 baseline(직전 기록 ~312건 부근) + 신규 ChuteStatePush 테스트** 전건 GREEN. 단일 run 신뢰 금지(실-Sim I/O 테스트 부하 flake 이력 — 관련군 반복 또는 ≥5회로 결정성 확인).
2. **신규 테스트(스펙 입증 — 가짜 고객 서버 수신 본문 기반):**
   - **CS-PUSH-1** PAUSE 전이 → 가짜 서버가 `{"chute_numbers":[dest.ChuteNo],"next_states":[2]}`(snake_case·정렬·ChuteNo 직송) 정확히 1건 수신. CHUTE·SORTER_3D 목적지 둘 다 대상임을 커버.
   - **CS-PUSH-2** RESUME 전이 → `{"chute_numbers":[dest.ChuteNo],"next_states":[3]}` 정확히 1건 수신.
   - **CS-PUSH-3** scope 게이트: FULL(만재/capacity) 상태 변화·O6 CellAssign은 **이 API 발신 0건**(PAUSED/RESUMED 전이만 푸시 — Q-c LOCKED). AlreadyInState 멱등 pause도 재푸시 0건(실제 전이만).
   - **CS-PUSH-4** 성공 응답 처리: 200 `{flag:1, result:[...]}` → 성공(재시도 없음).
   - **CS-PUSH-5** 실패 처리: 비2xx / `{result:"Failed"}` / flag≠1 → 재시도(설정 백오프) 후 소진 → **false + ERROR 로깅**, 조용한 드롭 0. 복구 후 다음 전이 정상 도달.
   - **CS-PUSH-6 (DORMANT·핵심)** BaseUrl null → 기동 크래시 0 · pause/resume 발생시켜도 **가짜 서버 수신 0건**(HTTP 시도 0) · 인바운드/기존 RcsPush 정상.
   - **CS-PUSH-7 (payload 정합)** body 키가 정확히 `chute_numbers`·`next_states`(camelCase 아님), 두 배열 동일 길이·인덱스 정렬. PUT 메서드.
3. **하드코딩 0 · 마이그레이션 0:** URL/타이밍 리터럴 없음(전부 appsettings). `chute_numbers`는 `Destination.ChuteNo` 직송(1:1) — 매핑 config·DB 테이블 **모두 미신설**. 스키마 무변경 → **마이그레이션 0**(신규 마이그레이션 파일이 생기면 계약 위반).
4. **관찰-전용 증거:** `git diff -- backend/src/Wcs.Api/Services/DestinationControlService.cs`가 **전이 이벤트 발화 append에 국한**(pause/resume 판정·DB·인메모리·멱등 로직 무변경). `Wcs.Core`·`Wcs.PlcGateway`·`HandshakeOrchestrator`·기존 RcsPush(`RcsPushClient`·`DestinationStatusPusher`) diff 0.
5. **활성화 경로 문서화:** 고객이 호스트 제공 시 설정할 **단 하나의 값 = `Wcs:ChuteStatePush:BaseUrl`**(그 외 추가 설정 불요 — chute_numbers는 ChuteNo 직송)임을 appsettings 주석/PR 설명에 명기. BaseUrl은 커밋 시 **null 유지**(고객 미제공).
6. **Sim/offline-safe:** 검증은 가짜 고객 서버(in-process Kestrel 동적 포트) + in-memory SQLite(기존 테스트 팩토리) — **실 고객 호스트·실 3DS PLC(COM1/RTU)·현장 DB 미접근**. 푸시 비활성/실패가 WCS 핵심 흐름(인바운드·pause/resume·PLC)을 막지 않음.

---

## Scope OUT (이 계약에 흡수 금지)

- **기존 RcsPush(IF-08 WCS→RCS `ready` 푸시) 변경** — 별개 채널. 무접촉(관찰 훅 공유 시에도 RcsPush 파이프 로직 무변경).
- **FULL(만재)·capacity·복합 ready 를 UpdateChuteState로 푸시** — Q-c 기본 제외(override 시에만).
- **O6 CellAssign(수동 셀지정)·PLC 쓰기 경로 연동** — 슈트 open/close 상태가 아님. 무접촉(절대규칙 #1 보존).
- **인바운드 엔드포인트 신설** — 이 API는 WCS가 **호출하는** 아웃바운드. WCS에 새 컨트롤러/라우트 추가 없음.
- **chute_number 매핑(config·DB·오프셋 일체)** — Q-b LOCKED = `Destination.ChuteNo` 직접 1:1. 매핑 계층 도입 금지(향후 다른 external ID 고객 발생 시 별도 스프린트).
- **배치(다건) 최적화·전이 코얼레싱** — 계약 배열 구조는 유지하되 이번엔 전이당 단건. 배치는 후속.
- **프론트/모니터링 UI(설정·상태 표면)** — 요청 없음(dormant 백엔드 전용). UI 추가 없음.

---

## Multi-Instance / Project Type / Verification

- **Parallel Modules:** N/A (단일 응집 변경 — Options ↔ Client ↔ Observer ↔ DI ↔ Tests가 한 흐름으로 결선. 병렬 분할 이득 없음). 순차 단일 Generator.
- **Evaluation Dimensions:** **functional only** — PLC/안전 경로(절대규칙 #1) 무접촉인 순수 아웃바운드 HTTP라 별도 safety 차원 불요. 단 Evaluator는 "관찰-전용·회귀 0"을 functional 기준 [20%] 하드 관심으로 포함 검증(코어 diff 대조).

- **Detected Project Type:** **Backend/API** — 변경 표면 = 서버측 아웃바운드 HTTP 클라이언트 + observer + DI 배선 + config(모두 `backend/src/Wcs.Api`). 브라우저 진입점/클라이언트 렌더 트리 **무접촉**(프론트 UI 없음). 서버 라우트/서비스 계층만. Web/UI 슬롯 없음.

- **Verification Scenarios (Backend/API):**

  - **이 스프린트가 건드리는 엔드포인트/표면(method + path):**
    - **아웃바운드(신규 소비 — WCS가 호출):** `PUT {Wcs:ChuteStatePush:BaseUrl}{Path=/api/UpdateChuteState}` (고객 API). **신규 인바운드 WCS 엔드포인트 없음.**
    - **트리거(기존·무변경, 푸시 발원):** `POST /api/ops/destinations/{destId}/pause`(→ next_state 2), `POST /api/ops/destinations/{destId}/resume`(→ next_state 3). 이들은 관찰 대상일 뿐 시그니처/동작 무변경.
    - **검증 대역:** 가짜 고객 서버(in-process Kestrel, `PUT /api/UpdateChuteState` 수신·기록·거부토글) — FakeRcsServer 미러.

  - **Happy path (입력 → 기대 출력 형상):**
    - 목적지 **PAUSE**(O2 또는 DestControlService.PauseAsync) → 가짜 서버가 **`{"chute_numbers":[dest.ChuteNo],"next_states":[2]}`** (snake_case·동일 길이·인덱스 정렬·ChuteNo 직송, PUT) 정확히 1건 수신 → WCS가 200 `{flag:1,result:[...]}`를 성공으로 처리(재시도 0). CHUTE·SORTER_3D 둘 다 대상.
    - 목적지 **RESUME** → **`{"chute_numbers":[dest.ChuteNo],"next_states":[3]}`** 1건 수신 → 200 성공.
    - AlreadyInState(멱등 재-pause) → 추가 수신 0(실제 전이만 푸시).

  - **관련 에러/경계 케이스(해당하는 것만 — 패딩 금지):**
    - **DORMANT (BaseUrl null):** pause/resume 발생 → 가짜 서버 **수신 0**(HTTP 시도 0)·기동 크래시 0·인바운드 정상. (이 API의 400 missing-params는 WCS가 항상 두 배열을 함께 보내므로 정상 경로엔 미발생 — 방어적 처리만 유지.)
    - **처리 실패(고객 500/비2xx 또는 `{result:"Failed"}` 또는 flag≠1):** 재시도(설정 지수 백오프) → 소진 시 **false + ERROR 로깅**(Fail-Loud), 조용한 드롭 0. 이후 복구되면 다음 전이 정상 도달.
    - **scope 게이트:** FULL(만재)·O6 CellAssign 상태 변화 → **무발신**(수신 0) — PAUSED/RESUMED 전이만 푸시(Q-c LOCKED).
    - **취소/종료:** 호스트 종료 시 재시도 루프 취소 전파(미발신 유지) — RcsPushClient 취소 처리 미러.

  - **관찰(하드):** `WebApplicationFactory<Program>` + 가짜 고객 서버(동적 포트) + in-memory SQLite 로 실행. **가짜 서버가 수신한 실제 JSON 본문**(키/값/배열 정렬)·HTTP 메서드(PUT)·재시도 횟수·operation_log(`CHUTESTATE_PUSH` 성공/실패)로 입증. 인메모리 상태 단언만으로 PASS 금지. **실 고객 호스트·실 PLC·현장 DB 미접근**(기동 설정 증거).

  - **회귀 관찰:** 기존 RcsPush(IF-08 VS-PUSH 스위트)·pause/resume(O2/O3 Ops)·인바운드 IF-05/09/10·b2b·모니터링(F1/F2) 무손상. baseline 카운트 유지. `DestinationControlService` 코어 동작(전이·감사·멱등) 무변경 확인.

> Planner self-check — Detected project type: Backend/API. Required scenario slots: 4 (endpoints touched [method+path], happy path per endpoint, relevant error cases per endpoint, hard observation+regression). All slots filled: yes (Backend/API populated with outbound target + trigger endpoints + fake-server happy [PAUSE→2 / RESUME→3, ChuteNo 1:1] / dormant / failure / scope-gate [FULL·O6 무발신] cases + fresh-body observation and regression; Web/UI slots correctly absent — no browser surface in this sprint). Customer-contract gate CLOSED — Q-a/Q-b/Q-c LOCKED by 사수 2026-07-09 (no open questions remain).
