# Sprint Feedback — S-CHUTESTATE-PUSH (신규 아웃바운드: WCS → 고객 `PUT /api/UpdateChuteState` · DORMANT 선적용)

**APPROVED** — Evaluator, 2026-07-09 (1 iteration to pass).

브랜치 `feat/chutestate-push`(HEAD == develop, 전 작업 미커밋 working tree — orchestrator 가 승인 후 커밋). Backend/API 단일 차원(functional only, 계약 선언). Evaluator 는 코드를 고치지 않음. Ground truth = git diff/status + 코드 판독 + **독립 재실행 dotnet test**(full 1× + 신규군 5× 반복) + **가짜 고객 서버가 수신한 실제 JSON 본문**. Generator 요약(327 GREEN 등)은 신뢰하지 않고 전부 독립 재현.

핸드오프 확인: `tasks/sprint-log.md` L3213 에 `## IMPLEMENTATION COMPLETE — S-CHUTESTATE-PUSH` 마커 존재(파일 mixed-encoding — `grep -a` 로 확인) → 활성화 정당.

---

## [정적] 빌드/테스트 — 독립 재실행 fresh evidence

- `dotnet test backend/Wcs.sln`(full): **`통과!  - 실패: 0, 통과: 327, 건너뜀: 0, 전체: 327` (20s, exit 0)**. baseline 318 + 신규 ChuteStatePush 9 = 327. 회귀 0·skip 0.
- **결정성(계약 Completion #1 — 실-Kestrel/폴링 I/O flake 이력 대비 ≥반복 필수)**: 신규군 `--filter FullyQualifiedName~ChuteStatePush` **5회 연속 = 9/9 GREEN**(각 4~5s). flake 0.
- **빌드 경고**: 신규 코드 경고 0. 유일 경고 = 선재 NU1903(`SQLitePCLRaw.lib.e_sqlite3` 취약성 advisory) — 이번 스프린트 도입분 아님(feedback-archive 기 확인). 오류 0.

## [30%] DORMANT 정확성 (하드 — "host 제외 미리 적용"의 핵심) — PASS

- **appsettings 커밋값 = `"BaseUrl": null`** (git diff 확인). `_comment_ChuteStatePush` 에 "BaseUrl 한 값만 설정하면 활성, 미설정 시 DORMANT" 명기.
- **CS_PUSH_6c(client)**: `BuildClient(baseUrl:null)` → `IsEnabled==false`·`PushAsync`→false·`srv.All` **Empty(HTTP 시도 0)**.
- **CS_PUSH_6(observer/e2e)**: `ChuteStatePushWebApplicationFactory(chuteBaseUrl:null)` 기동 **크래시 0**(`CreateClient()` 성공) → pause/resume/소터 pause 3회 발생시켜도 가짜 서버 **수신 0** + 인바운드 IF-05(`POST /api/v1/destination-query`) **200**(회귀 0). `ChuteStatePusher.StartAsync` 가 `!IsEnabled` 시 경고 후 구독 안 함(코드 확인).

## [30%] 계약 정합 (고객 API) — 가짜 서버 수신 본문으로 입증 — PASS

- **PUT 메서드(POST 아님)**: `FakeChuteStateServer` 가 `app.Map`(전 메서드 매칭)으로 실 메서드 기록 → CS_PUSH_7/CS_PUSH_1 이 `last.Method == "PUT"` positive 단언. 클라이언트는 `PutAsJsonAsync`.
- **snake_case `{chute_numbers, next_states}` via [JsonPropertyName]**: `ChuteStatePushPayload` record 가 `[property: JsonPropertyName("chute_numbers")]`·`[property: JsonPropertyName("next_states")]` 명시(STJ 기본 camelCase 미의존). CS_PUSH_7 이 수신 RawBody 파싱 → 키 정확히 2개 `chute_numbers`·`next_states` 포함 + `chuteNumbers`·`nextStates`(camelCase) **DoesNotContain**(계약 함정 방어).
- **인덱스 정렬·동일 길이**: 전이당 길이-1 단건 배열. CS_PUSH_7 이 `ChuteNumbers.Length == NextStates.Length` 단언.
- **성공 = 2xx && flag==1**: `IsSuccessStatusCode && IsSuccessBody`(flag==1). CS_PUSH_4: 200 `{flag:1}` → true·재시도 0(`srv.All.Count==1`).
- **실패 = 비2xx / `{result:"Failed"}` / flag≠1 → 재시도+백오프+Fail-Loud**: CS_PUSH_5 — (a) 503 → 재시도 소진(총 3회 = 1+RetryCount2 전부 서버 도달, 조용한 드롭 0)→false, (b) `{result:"Failed"}`(400)→false, (c) 200 `{flag:0}`→false, (d) 복구→true. `ComputeBackoffDelay` 지수 백오프(설정값 경유)·소진 시 `LogError`(Fail-Loud)+operation_log WARN·false 반환. 매 시도 `LogWarning`(조용한 실패 0).

## [20%] 관찰-전용 · 회귀 0 (하드) — PASS

- **`git diff backend/src/Wcs.Api/Services/DestinationControlService.cs` = 순수 additive**: (1) `DestinationTransition` record struct 신설, (2) 인터페이스+구현에 `event Action<DestinationTransition>? OnTransition`, (3) 스코프 안에서 `chuteNo = dest.ChuteNo` 캡처, (4) **Transitioned 반환 직전**(AlreadyInState/NotFound 는 이미 조기 반환) `OnTransition?.Invoke(...)` 를 try/catch 로 감싼 발화. pause/resume 코어(전이·감사·인메모리·멱등) 로직 **무변경**(제거 라인 0). 이벤트는 DB 커밋(스코프 종료)·"전이 완료" 로그 **이후** 발화 → destination_event 정합 불변.
- **구독자 예외 격리**: `OnTransition` 발화가 try/catch 로 감싸져 구독자 예외가 코어 반환(운영자 O2/O3 응답)을 죽이지 않음. Pusher 도 fire-and-forget(`_ = PushSafeAsync`)로 비블로킹.
- **무접촉 확인**(git status): `Wcs.Core`·`Wcs.PlcGateway`·`HandshakeOrchestrator`·기존 RcsPush(`RcsPushClient`·`DestinationStatusPusher`) diff **0**(수정 목록에 부재).
- **스코프 게이트 진성(FULL/O6 무발신)**: CS_PUSH_3 — (1) FULL(capacity `OnReserved`)→`OnChuteStateChanged`만 발화(OnTransition 아님)→발신 0, (2) O6 `cell-assign`(소터 PLC 쓰기 경로, DestControlService 무접촉)→발신 0, (3) AlreadyInState(멱등 재-pause)→`DestControlOutcome.AlreadyInState`·발신 0. 3종 후 `srv.All` Empty → 이어 실제 PAUSE(chuteNo 2)→정확히 1건(pusher 생존·게이트 진성). FULL·O6 는 절대규칙 #1(PLC 큐) 무접촉으로도 스코프 밖.

## [15%] Craft · 아키텍처 (RcsPush 형제성) — PASS

- **IHttpClientFactory 경유 named client**(`CreateClient(HttpClientName)`, 직접 `new HttpClient()` 0). 타임아웃=`HttpTimeoutMs` 설정값(Program.cs `AddHttpClient` 배선).
- **관심사 분리**: `ChuteStatePushClient`=1건 전송+재시도+Fail-Loud+operation_log / `ChuteStatePusher`=전이 감지+`ChuteNo` 직송 매핑. RcsPush(`RcsPushClient`/`DestinationStatusPusher`) 구조 미러.
- **매핑 LOCKED(Q-a/Q-b)**: `DestStatus.PAUSED→2`·`DestStatus.NORMAL→3`, `chute_numbers=[t.ChuteNo]` 직송(이벤트가 실어 옴 — DB 조회/매핑 테이블 0). CS_PUSH_1(CHUTE)·CS_PUSH_1b(SORTER) `next_states:[2]`, CS_PUSH_2 `[3]` 로 실증. CHUTE·SORTER_3D 균일 훅(DestType 필터 없음).
- **config 외부화(#7)**: `ChuteStatePushOptions`(BaseUrl·Path·RetryCount·RetryBaseDelayMs·RetryMaxDelayMs·HttpTimeoutMs·`IsEnabled`) — RcsPush 동형. **`ChuteNoMap` 필드 부재**(Q-b LOCKED = 1:1 직송). URL/타이밍 하드코딩 리터럴 0.
- **DI 배선 append-only**(Program.cs): RcsPush 블록 이후에만 추가(기존 배선 무접촉).

## [5%] 결정성 · 인프라 정합 — PASS

- 가짜 서버 대상 테스트 5× 반복 GREEN(비-flaky). FakeChuteStateServer(Kestrel 동적 포트)·`WaitUntilExactAsync`(stable-count)·in-memory SQLite = RcsPushTests 프리미티브 재사용. **실 고객 호스트·실 3DS PLC(COM1/RTU)·현장 DB 미접근**(in-process Kestrel + Sqlite Mode=Memory).

## 마이그레이션 0 (계약 §Completion 3·4 하드)

- `git status --porcelain | grep migrat` = **0건**. Glob `**/Migrations/*.cs` = 선재 파일만(0630/0708 — 이번 스프린트 신규 0). 스키마 무변경(매핑 config/DB 테이블 미신설).

## Completion Conditions (계약 §Completion 1~6) — 전부 충족

1. 전체 스위트 GREEN(327, 실패 0·skip 0) + 신규군 5× 결정성 ✅
2. 신규 테스트 7시나리오 9건(CS-PUSH-1/1b/2/3/4/5/6/6c/7) 가짜 서버 수신 본문 기반 ✅
3. 하드코딩 0(전부 appsettings)·마이그레이션 0·ChuteNoMap 미신설 ✅
4. 관찰-전용(DestControlService additive·코어 무변경·Core/PlcGateway/Handshake/RcsPush diff 0) ✅
5. 활성화 경로 문서화(BaseUrl 단일값·appsettings 주석·커밋 시 null 유지) ✅
6. Sim/offline-safe(in-process Kestrel + in-memory SQLite, 실 호스트/PLC/현장DB 미접근) ✅

## Wire contract 정합 (docs/UpdateChuteState_API_EN.md) — 전부 충족

PUT ✅ / body snake_case `{chute_numbers:int[], next_states:int[]}` via [JsonPropertyName] ✅ / index-aligned 동일 길이 ✅ / success 2xx && flag==1 ✅ / failure 비2xx·`{result:"Failed"}`·flag≠1 → retry+backoff+fail-loud(무 silent) ✅ / 캡처 body 검사(CS_PUSH_7) ✅.

## LOCKED semantics 정합 — 전부 충족

PAUSED→[2] ✅ / RESUMED→[3] ✅ / chute_numbers=[dest.ChuteNo] DIRECT(매핑 0) ✅ / scope O2/O3 only, FULL·O6 무발신(CS_PUSH_3 증명) ✅ / DORMANT BaseUrl null 시 HTTP 0(CS_PUSH_6/6c 증명) ✅.

## Repeat detection

- 스택 PR 금지(develop 분기·HEAD==develop 확인)·Sim/현장 DB 무접촉·가짜 서버 수신 본문 입증(인메모리 GREEN 불신)·≥반복 결정성 = 기존 교훈(MEMORY) 준수. 반복 결함 0 → 신규 lessons 승격 불요.

## Minor (비차단 — 다음 스프린트 Generator 참고)

- `ChuteStatePushClient.DetailJson` 이 payload 를 수동 문자열 보간으로 조립(operation_log detail) — 소규모라 무해하나 STJ 직렬화로 통일 여지(cosmetic·비차단).

→ **결론: functional 단일 차원 PASS, 계약 Completion 1~6 + wire/LOCKED 전부 충족. APPROVED.**

**APPROVED — S-CHUTESTATE-PUSH**

## Step 4.5 Code Review (orchestrator 기록)
- **판정: With fixes → 이연 처리로 merge-ready.** Critical 0 · Important 2 · Minor 4.
- 강점: scoped DbContext 미포획(ChuteNo 값캡처)·fire-and-forget async Task+전구간 try/catch(unobserved 0)·코어 전이 이중보호·IsSuccessBody가 RcsPush보다 견고(flag==1 AND result!=Failed)·snake_case JsonPropertyName 캡처검증·HttpClient timeout vs shutdown cancel 구분·이벤트 구독 leak-free.
- **#1 재동기화 부재 = 의도적 best-effort로 확정(이연)**: 이 API는 Pause(2)/Open(3)만·normal 상태 부재라 startup bootstrap 시맨틱 미정. dormant(BaseUrl null)라 현재 영향 0. **활성화(고객사 host 제공) 시 재동기화 필요 여부를 고객사와 협의해 별도 결정**. → todo.
- #2 4xx 재시도(RcsPush 동일패턴·정상 body라 자가발생 불가)·minor 4건(#3 주석·#4 _cts 종료 race 로그·#5 backoff/url DRY·#6 DisposeAsync await) → todo 이연. fix 반복 불요(BLOCKING 0).

**APPROVED — S-CHUTESTATE-PUSH (Evaluator ∧ Step 4.5, 327 GREEN, dormant)**
