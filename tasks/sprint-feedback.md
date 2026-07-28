# Sprint Feedback — S-IF10-CWRITE-SETTLE-DELAY

평가자(Evaluator) 독립 검증 — 2026-07-28. 브랜치 feat/if10-cwrite-settle-delay, HEAD 2121be9(uncommitted working tree).
모든 근거는 Evaluator 자체 재실행 fresh output(Generator 보고 수치 불신·독립 재현).

## 결과: APPROVED (전 7 Completion Condition + 전 Verification Scenario PASS, 1 iteration)

---

### Ground truth
- `git rev-parse HEAD` = 2121be95… · branch = feat/if10-cwrite-settle-delay · 변경 파일 = 계약 Implementation
  Scope와 정확히 일치(PlcGateway 2 + Wcs.Api 4 + docs 1 + 테스트 3[E2E infra + 신규 2]).
- sprint-log.md `## IMPLEMENTATION COMPLETE` 마커 존재(L5) + Generator 재량 결정 5건 기록(L46~58) — #7 충족.

### Completion #1 — 전량 GREEN + baseline 대조 + 신규 경고 0  ✅
- `dotnet build backend/Wcs.sln -c Debug`: **오류 0 / 경고 10개 — 전부 선재 NU1903**(SQLitePCLRaw 취약성).
  CS 경고 0·신규 경고 0.
- `dotnet test backend/Wcs.sln --no-build` **자체 재실행**: `실패 0 / 통과 477 / 건너뜀 0 / 전체 477`(1m28s).
- baseline 대조: `477 − 11(신규) = 466(기존)` → 기존 회귀 0.
- 타이밍 flake 귀속: `--filter FullyQualifiedName~SettleDelay` **11/11 GREEN × 4회 연속 반복**(6~7s 각, flake 0).

### Completion #2 — 안착 지연 정확성(fresh 로그)  ✅
detailed 로거로 캡처한 실측 발화 tick/경과(자체 재실행):
- **[S1]** SettleDelayMs=400 → C 기입까지 경과 **406ms**(하한 340ms=400−60 이후) — C가 지연 하한 전 미발생.
- **[S1b]** SettleDelayMs=0 → C 기입 **0ms**(추가 지연 0·HS_SETTLE_WAIT 스테이지 부재·경로 무변경).
- **[S5]** anchor(now−900ms) 경과>지연(400) → C 기입 **0ms**(remaining clamp 0·HS_SETTLE_WAIT detail `"remainingMs":0`).
- **[S4]** 스테이지 tick 순서 residue(…765) ≤ clearArm(…765) ≤ armed(…781) ≤ **settle(…781) ≤ cSent(…031)**
  → 안착 지연이 arming "후"·C "전"(D1). settle→cSent 간격 ≈250ms = SettleDelayMs.

### Completion #3 — 비블로킹(자동화 실증, 코드리뷰 대체 아님)  ✅
- **[API SettleDelayApiTests]** SettleDelayMs=1000 하니스: IF-10 HTTP 왕복 **200ms**(<500 단언) — 응답이
  SettleDelayMs와 독립(즉시 200 ack). sorter_command COMPLETED는 **1336ms**(≥ 1000−100)에 등장 → C는 백그라운드
  안착 지연 뒤로 밀림. 응답 직후 sorter_command Count=0 단언 통과. 폐기된 "C 완료 대기 후 응답" 부활 0.
- 코드 확인: RcsController diff = anchor 캡처(진입 즉시 UTC) + 파라미터 전달만. `_ = bundle.ExecuteHandshakeAsync(…)
  .ContinueWith(…)` fire-and-forget 구조·`return Ok(new DepositReportResponse("OK"))` 즉시 응답 로직 **무변경**.

### Completion #4 — 종결 안전(OFFLINE·종료 중 지연)  ✅
- **[S2]** 지연 중 실 Sim 중단 → OFFLINE 전이 → Outcome=**Offline**, `HS_C_SENT` 부재 + `CELL_ASSIGN` 쓰기 부재.
- **[S3]** 지연 중 취소(ct.Cancel) → **OperationCanceledException 전파**, `HS_C_SENT`/`CELL_ASSIGN` 부재.
- 코드: 안착 지연이 `Interlocked.Increment(ref _cSeq)` **이전**에 위치 → 지연 중 종결이 cSeq 미소비·더티 진행 0.

### Completion #5 — 하드코딩 스캔 + 출하값/주석  ✅
- 안착 지연 경로 리터럴 지연 상수 0 — 대기량은 전량 `_opt.SettleDelayMs`(config), 잔여=config 유도,
  deadline=Environment.TickCount64 단조 시계. 유일 리터럴 `50`은 `RFlagPollMs<=0` 시 폴 스텝 granularity 폴백
  (오설정 방어 가드)로 안착 지연량과 무관 — 절대규칙 #7 위반 아님.
- appsettings.json `Timing:SettleDelayMs = 0`(출하 비활성) + `_comment_SettleDelayMs`에 "★현장 실측 후 조정 …
  양수=활성, 0=비활성/현행" 존재.

### Completion #6 — git diff 스코프 한정(무접촉 경계)  ✅
- `git status` 경계 검사: Sim3ds / migration / frontend / .tsx / WcsDbContext = **NONE**(무접촉). untracked 마이그레이션
  파일 0. IF-10 응답 로직 diff 0(anchor plumbing만).

### Completion #7 — sprint-log 마커 + 재량 결정  ✅
- `## IMPLEMENTATION COMPLETE` + anchor 캡처 방식(컨트롤러 진입 UtcNow)·키명(SettleDelayMs)·HS_SETTLE_WAIT 스테이지
  유(有)·무조건-지연 단순화 미채택(anchor 잔여 계산 채택)·cSeq 증가 이전 삽입 근거 기록.

---

### Verification Scenarios
- Web/UI(5 슬롯): 전부 N/A — frontend 무접촉(git diff 경계 clean, .tsx 0)로 정당.
- Backend/API: endpoints touched=POST /api/v1/deposit-report 응답 형상/타이밍 불변(API 테스트 실증) · happy(settle>0
  즉시200+백그라운드 C / settle=0 현행동일 / 슈트 미트리거[기존 GREEN] / 중복 멱등[기존 GREEN]) · error(S2 OFFLINE·
  S3 종료·FULL/번들없음 미진입[기존]·입력검증 400[기존, anchor 캡처는 검증 이전이나 무해·400 반환 불변]) — 실증.
- cross-layer: N/A — HTTP 응답 vs Modbus C 쓰기 독립성을 Backend/API(API 테스트)에서 실증.

### Signature 역호환
- ExecuteAsync에 `DateTime? depositedAtUtc = null` **선택적** 3번째 파라미터(ct 뒤) 추가 → 기존 ~20 호출부
  무수정. 전체 솔루션 --no-build 재빌드 오류 0 + 477 GREEN이 back-compat 실증. SorterGatewayRegistry는
  `_handshake.ExecuteAsync(cellNo, ct, depositedAtUtc)` 위치 정합 위임.

### 검증 인프라 격리(현장 오염 0)
- HandshakeSettleDelayTests: 실 SimServer TCP + GetFreePort 에페메랄 포트 + PlcPollingService 직접 번들.
  실 PLC·COM1/RTU·5205·운영 DB 무접촉. SettleDelayApiTests: WebApplicationFactory(격리 Sim + scratch DB).

### Minor(비블로킹 — todo 후보)
- SettleDelayAsync의 `50` 폴 스텝 폴백은 안착 지연량과 무관하나, 다른 대기 루프가 `_opt.RFlagPollMs`를 직접
  쓰는 패턴과 미세 비일관. 필요 시 후속에서 공통 폴백 헬퍼로 정리 가능(현 스프린트 비차단).

## Step 4.5 코드리뷰 결과 (2026-07-28) — Ready to merge: Yes (Critical 0 · Important 0 · Minor 1)
BLOCKING/Critical 0 → 병합 무차단. 강점 확인(리뷰어): 벽시계 anchor(경과)+단조시계(TickCount64) 대기 혼용이
버그 아니라 NTP 노출 최소화 정설계(양방향 clamp·역행→과대대기 안전·전방 스텝만 미세 under-wait, 스텝크기로 bound)·
remaining `[0,settleMs]` clamp로 오버플로 0·조기중단(OFFLINE/취소) 양경로 C 미기입(지연이 cSeq 증가 이전→시퀀스
미소비·off-by-one 0)·규칙#1/#8 보존(지연은 순수 대기·큐/Modbus 무접촉)·비블로킹 불변(anchor는 컨트롤러 진입
UtcNow 1회·fire-and-forget/200 즉시 응답 무변경)·역호환(전 호출부 (cellNo)/(cellNo,ct)만 → optional 충돌 0)·
테스트 결정성(조건 폴링·post-hoc tick 단언·HS_SETTLE_WAIT 대기 후 OFFLINE/취소 주입·E2E default off byte-identical).
### Minor (= 위 Evaluator Minor와 동일, todo 등재 완료 — 다음 sprint Generator)
- [CR-MINOR-1] HandshakeOrchestrator.cs:218 `50` 폴스텝 폴백이 형제 4개 루프(`_opt.RFlagPollMs` 직접)와 비일관.
  단, 운영 poll은 `_opt.RFlagPollMs`(appsettings=100)라 이 리터럴은 configured run에서 도달 불가 → 규칙#7 위반 아님.
  정리 = 삼항 제거 또는 공유 폴백 헬퍼로 5개 루프 정책 통일(비차단).
### 관측(무액션 — 기록만)
- HandshakeOrchestrator.cs:142 안착 지연이 cSeq 증가 전 in-flight 창을 소폭 넓힘 → 기존 단일소터 동시 IF-10
  직렬화 갭([[single-sorter-concurrent-handshake-gap]], SPEC §6 순차 dispatch 전제)을 미세 확대. 본 스프린트 회귀
  아님·전제 그대로·스코프 밖(기록만).
- SettleDelayAsync 스테이지 비대칭(settle<=0 무발화 vs settle>0&remaining==0 발화 remainingMs:0)은 "기능 OFF"vs
  "ON·대기 불요" 구분 의도(S1b/S5가 고정). 오설계 아님(기록만).
