# Sprint Feedback — S-TRACE-READY-PUSH-AND-DEFAULT

(Evaluator가 PASS/FAIL·APPROVED 기록)

---

## APPROVED (Evaluator, 2026-07-30)

브랜치 feat/trace-ready-push-default · HEAD=8c5d8c4(develop 기준, 스프린트 변경 전부 uncommitted — 정상).
Ground truth 확인: 변경 파일 = 코드 6 + 신규 테스트 1(TraceReadyPushTests) + 계약/로그/피드백 3. no-touch 존
(Wcs.Core·Wcs.PlcGateway·Wcs.Sim3ds·HandshakeOrchestrator) `git diff --stat` 공란 = zero-diff 확인.

전 조건(C1~C5) 및 전 시나리오(U1~U5·B1~B4·E1~E2) fresh evidence로 통과. APPROVED.

### 1. 빌드·정적·테스트 (자체 재실행)
- **빌드**: `dotnet build backend/Wcs.sln` → 오류 0 / 경고 10(전부 선재 NU1903 SQLitePCLRaw). 신규 경고 0. SDK 10.0.301(net10.0 정합).
- **프론트**: `eslint .` exit 0 · `tsc --noEmit` exit 0 · `vite build` exit 0(선재 chunk>500kB 경고만, wwwroot gitignored).
- **전체 테스트 `dotnet test backend/Wcs.sln`**: **총 504**(= baseline 493 + 신규 11 — 생성자 주장과 산술 일치).
  - run#1: 502통과/2실패, run#2: 503통과/1실패 — 실패 세트가 매 run 다름(간헐).
  - **실패는 전부 RTU fake-serial 부하 flake — 회귀 아님**(§근거 아래). 트레이스 관련 테스트(N1·N2·N3·TraceReadyPush)는 두 run 전부 GREEN.
- **신규 11 결정성**: TraceReadyPushTests(10) + N3(1) 격리 **3/3 반복 전부 GREEN**(각 ~3s).

#### RTU 실패 flake 귀속(회귀 아님 — 근거)
실패 테스트 = `RtuTransportTests.VT2_...`(WaitUntil 2000ms: R_Flag=0 after ClearR) · `Sim3dsRtuTests.B1_...`(WaitUntil 3000ms: GW Online). 판정:
1. RTU/Sim/PlcGateway 코드 **zero-diff** — 본 스프린트는 Wcs.Api 트레이스 로깅 + 프론트만 변경. RTU fake-serial 핸드셰이크 경로 무접촉.
2. **격리 3/3 GREEN**(17 RTU 테스트 전부 sub-500ms) — 부하 없으면 여유롭게 통과.
3. **간헐성**: full run 간 실패 세트가 2건→1건으로 달라짐(B1은 run#2에서 회복). 결정적 회귀라면 매 run 동일 실패.
4. 두 RTU 클래스 모두 `[Collection]` 부재 → xUnit 기본 병렬로 무거운 실-Sim E2E와 동시 실행 → 타이트한 2s/3s WaitUntil이 CPU 경합에 걸림(구조적 부하 flake). 실패 모드=타임아웃(부하)이지 단언 불일치(로직) 아님.
5. 추가 트레이스 훅은 논블로킹 Channel.TryWrite(Wcs.Api) — fake-serial Online 타임아웃을 유발할 수 없음.
   ⇒ lessons e2e-parallel-load-surfaces-integration-flakes / s9-flake 재적용. **pre-existing 부하 flake로 귀속 — APPROVED 무영향.**

### 2. 4-이벤트 정확 발화 (C1·B2·E1·E2 — 격리 라이브 스택)
격리 스택(실 Sim TCP 에페메랄 + fake RCS(FakeChuteStateServer) + scratch TraceLog dir + in-memory SQLite — 실경로 D:\/현장 5205/COM1/운영DB 무접촉)으로 소터 Ready 1→0·0→1을 실제로 태워 **전용 파일 raw 인용**(evaluator throwaway 하니스, chuteNo=30 소터):
```
[7]  {"eventNo":7,"event":"READY_1TO0","at":"...16.643...","chuteNo":30,"destId":6,"floor":2,"pId":null,"cSeq":null,"cellNo":null,"trigger":"READY_EDGE","detail":"{\"reg\":\"Ready\",\"old\":1,\"new\":0,\"curFloor\":2}"}
[8]  {"eventNo":8,"event":"CHUTESTATE_PUSH_BUSY","at":"...16.659...","chuteNo":30,"destId":null,"trigger":"IF08_PUSH","detail":"{\"next_state\":2,\"result\":\"OK\",\"attempts\":1,\"host\":\"http://127.0.0.1:55117\"}"}
[9]  {"eventNo":9,"event":"READY_0TO1","at":"...16.734...","chuteNo":30,"destId":6,"floor":2,"detail":"{\"reg\":\"Ready\",\"old\":0,\"new\":1,\"curFloor\":2}"}
[10] {"eventNo":10,"event":"CHUTESTATE_PUSH_READY","at":"...16.755...","chuteNo":30,"trigger":"IF08_PUSH","detail":"{\"next_state\":3,\"result\":\"OK\",\"attempts\":1,\"host\":\"http://127.0.0.1:55117\"}"}
```
- Ready 1→0 → `[7]{old:1,new:0}` · 0→1 → `[9]{old:0,new:1}`; IF-08 PUT next_state 2 → `[8]{next_state:2}` · 3 → `[10]{next_state:3}` — **정확**.
- **같은 chuteNo=30**로 7→8(Δt 16ms)·9→10(Δt 21ms) 시각차 산출 가능(비인과 상관 — 계약 조사 C 정합).
- 7·9 소터 scope: pId/cSeq/cellNo=null, chuteNo/destId 세팅, floor=curFloor. 8·10: chuteNo=payload[0], destId=null(best-effort), detail={next_state,result,attempts,host}.
- N3 라이브 E2E(실 Sim SetReady + 실 PushAsync PUT→fake RCS)도 동일 4-이벤트를 REST(GET /api/monitor/trace)로 관통 확인 + 파일 raw `[7]/[8]/[9]/[10]` 태그 단언 GREEN.

### 3. additive / 회귀 0 (C3)
- **전용 파일**: 모든 줄이 `[N] {json}` 형식(비-태그 줄 0). 
- **전역 격리**: 리포 내 `logs/wcs-*.log` 전수 grep `^\[(7|8|9|10)\] {"eventNo"` = **0건** — 트레이스는 전용 파일에만, 전역 Serilog 무유입.
- **operation_log additive**: N3가 `OperationLogs(API·CHUTESTATE_PUSH)` 존재 단언 GREEN, N1이 HANDSHAKE 존재 단언 GREEN — 트레이스가 대체 아님. REG_CHANGE(Ready) 발화 경로(PlcGateway) zero-diff로 보존.
- **DORMANT**: baseUrl null → PUT 0 → 이벤트 8/10 미발화(TraceReadyPushTests.Push_Dormant 결정적 + 라이브 백엔드 FloorHosts {} + BaseUrl null에서 이벤트 8/10 자연 무발화).
- **디렉터리/빈 결과**: GET /trace 빈 결과 → HTTP 200 `[]`(500 없음). eventNo=99 / pId=99999 각각 200·`[]` 확인.
- 기존 6 이벤트·GET /trace camelCase 형상 불변(B1: 필드 eventNo/event/at/pId/cSeq/chuteNo/destId/cellNo/floor/inductionNo/trigger/detail).

### 4. 프론트 (Playwright 헤드리스 — :5290 dev, proxy→:5215)
- **U1**: fresh localStorage(기본 b2c)에서 `/` → **/trace 랜딩**, h1="추적 로그". B2B 토글 클릭 → `/data-generator`(b2b nav 세트, 추적 로그 항목 없음 — 정상). 
- **U2·C4**: /trace에 1~6 + 신규 7·8·9·10 전부 렌더. 필터 드롭다운 11항목(전체+1~10, 라벨 7="Ready 1→0"·8="슈트상태 push(busy)"·9="Ready 0→1"·10="슈트상태 push(ready)"). 신규 이벤트 pId/cSeq/cellNo="—", chuteNo/floor/detail 채움. 배지 색조 구분(스크린샷 확인). 기존 1~6 무변경. → screenshots/S-TRACE-READY-PUSH-AND-DEFAULT_20260730-140000/01-trace-landing.png
- **U3**: 드롭다운 event 7 선택 → 정확히 1행(Ready 1→0)만.
- **U4**: 무매칭 필터(pId=99999) → 0행 + "표시할 추적 로그가 없습니다".
- **U5**: N/A(다크모드 없음 — 계약 명시).
- **콘솔(BLOCKING)**: 내 세션(:5290) pageerror **0** · React dev-warning **0**(React DevTools INFO 라인만). 콘솔 파일의 192 [ERROR]/48 hub-negotiate-500은 **전부 foreign :5173 세션**(공유 프로필 잔재 — 내 포트 5290 참조 0건, 5173 참조 384건). lessons foreign-buffer 재적용 — 앱 결함 아님.

### 5. 절대규칙 코드 게이트 (C5)
- **#1/#7**: 신규 코드(ChuteStatePushClient·TraceWiring) grep — EnqueueSet*/WriteRegister/Modbus/write-queue/리터럴 경로(D:\)/리터럴 호스트(http://)·COM/1502/5205 **0건**. host=baseUrl 파라미터, TraceLog dir=옵션값, next_state/result=런타임 데이터.
- **#8**: ChuteStatePushClient diff = 부수 훅만(const 2·optional 필드+생성자 param·sentAt anchor·EmitPushTrace 2 call·신규 메서드) — 성공/재시도/백오프/URL 판정로직 **zero-diff**. Wcs.Core/PlcGateway/HandshakeOrchestrator/Sim3ds zero-diff. 로깅은 Wcs.Api 계층.
- **논블로킹·fail-safe**: trace.Log=Channel.TryWrite, Ready 훅·EmitPushTrace 전체 try/catch 예외 격리.
- **DI 결선 확인**: Program.cs `AddSingleton<ITraceLogger>`(147) 등록 → `ChuteStatePushClient`(216) optional param 주입 → 실 호스트에서 이벤트 8/10 실제 발화(테스트 한정 아님).

### 6. N1 완화 정당성(주 관심사) 검토
N1 변경 = `SequenceEqual({1..6})` → `IsSupersetOf({1..6})` + `Assert.All(1..6, Contains)`. **회귀 은폐 아님**: additive 이벤트 7~10이 같은 분류 흐름에 정당하게 공존(Ready 토글 + 수용상태 push)하므로 exact-set은 필연 실패. 이벤트 1~6의 발화·pId 전파·cSeq 조인·cellNo/chuteNo 상관·operation_log additive 단언은 전부 **불변 유지**. 계약 정합.

### Minor(비차단 — todo 등록 불요, 관측)
- 없음(계약 스코프 정확 일치·재량 결정 모두 계약 명시 허용 범위).

## Step 4.5 코드리뷰 결과 (2026-07-30) — Ready to merge: Yes (Critical 0 · Important 0 · Minor 3)
BLOCKING/Critical 0 → 병합 무차단. 강점(리뷰어 코드수준): push 사이드훅이 상호배타 2지점(성공 line146 / 재시도소진 line192)에서만 발화 → 논리적 push당 0/1건, operation_log CHUTESTATE_PUSH 카디널리티와 정확 일치. 취소는 EmitPushTrace 이전 throw로 유령이벤트 0. 예외 이중격리(void try/catch + TryWrite). Floor 캡처 타이밍 정확(_latest=snap이 EmitRegisterChanges보다 먼저·PlcGateway:437<452). 판정로직 zero-diff(#8), #1/#7 grep-clean. N1 완화 = 정당한 additive(1~6 subset 보장 유지·회귀탐지력 손실 0).
### Minor (다음 sprint — 비차단, todo 등재)
- [CR-MINOR-1] TraceLogService.cs BuildReadyEdgeRecord docstring "순수·부수효과 0" 부정확 — 내부 DateTimeOffset.Now 읽어 시간 의존(에지→null 매핑만 결정적). 주석 완화 또는 clock 주입.
- [CR-MINOR-2] 프론트 "10개 이벤트" 카피가 Layout.tsx:46·TraceLogPage.tsx:113 문자열 하드코딩(2곳) — 이벤트 추가 시 수동 동기 누락 위험. EVENT_FILTER_OPTIONS.length 파생 권장.
- [CR-MINOR-3](무액션·설계) 이벤트 8/10이 소터뿐 아니라 CHUTE push까지 포함(ChuteStatePushClient=모든 IF-08 chokepoint·OQ2 확정). DestId=null·chuteNo only — 뷰어에서 소터 외 chuteNo의 8/10 혼재 유의(운영자). 결함 아님.
