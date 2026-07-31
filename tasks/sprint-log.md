# Sprint Log — S-IF08-PUSH-LOG-THROTTLE

(Generator가 `## IMPLEMENTATION COMPLETE` + 변경 요약 + 테스트 결과 기록)

## IMPLEMENTATION COMPLETE (Generator, 2026-07-31)

### 설계 요약 (HOW)
- 억제 상태 위치 = **Pusher의 `RouteState`**(= route 1개). `RouteState`가 `IPushFailureLogThrottle`를 구현하고
  per-route `PushFailureLogThrottleState`(직전 로깅 실패 next_state + 요약 기준 시각)를 보유. 클라이언트는 호출 간
  상태가 없으므로 재시도-소진 실패 확정 시 `throttle.OnFailure(nextState)`로 emit 여부(Emit/Suppress/Summary)를
  위임받는다("클라이언트가 Pusher로부터 억제 힌트를 받음" — 계약 후보 채택).
- 억제 단위 = **게이트 인스턴스(route) × payload.next_state**(OQ-2). 판정·상태 갱신은 `RouteState.Gate` 락 안에서
  원자적(비원자 check-then-act 없음). OnFailure는 락 밖(발신 I/O 중)에서 호출되어 스스로 Gate를 잡음(데드락 없음 —
  PumpAsync 발신은 락 밖).
- **성공 리셋**은 클라이언트 성공 경로(:143-146)를 건드리지 않기 위해 **Pusher `PumpAsync` ③ `if(ok)` 블록**에서
  `rs.ResetFailureLogSuppression()`로 수행(이미 Gate 락 보유 — OnFailure의 check-and-set와 동일 임계구역).
- 요약(OQ-1) sink = operation_log `result:"SUMMARY"`(WARN) + Serilog `LogWarning`("아직 실패 중(요약)"). 트레이스
  8/10은 발화 안 함(FAIL 카운트와 구분 · 요약은 폭주 아닌 저빈도 생존 신호). "SUMMARY"에 "FAIL" 부분문자열 없음 →
  FAIL 카운트와 명확 분리.

### 변경 파일
- `backend/src/Wcs.Api/Infrastructure/WcsOptions.cs` (M) — `ChuteStatePushOptions`에 설정 2개 추가(절대규칙 #7):
  `SuppressRepeatedFailureLog`(bool, 기본 true), `FailureLogSummaryIntervalMs`(int, 기본 300000=5분).
- `backend/src/Wcs.Api/Services/PushFailureLogThrottle.cs` (신규) — `PushFailureLogAction`(enum: Emit/Suppress/Summary),
  `IPushFailureLogThrottle`(OnFailure), `PushFailureLogThrottleState`(순수 결정기 `Decide(nextState, suppressEnabled,
  summaryIntervalMs, now)` + `Reset()` — clock 주입으로 요약 주기 결정적 테스트).
- `backend/src/Wcs.Api/Services/ChuteStatePushClient.cs` (M) — `PushAsync`에 `IPushFailureLogThrottle? throttle` 오버로드
  추가(기존 3-arg는 throttle:null 위임 — 하위호환). **FAIL 경로만** 게이트 위임: Emit→세 sink(LogError+oplog WARN
  FAIL+트레이스 8/10 FAIL, 문자열·인자 byte-identical) / Summary→요약 1건 / Suppress→0건. **성공 경로(:143-146)·재시도·
  백오프·DORMANT 가드·성공 판정(IsSuccessBody)·per-attempt LogWarning·반환값 전부 무변경**(diff로 실증).
- `backend/src/Wcs.Api/Services/DestinationStatusPusher.cs` (M) — `RouteState : IPushFailureLogThrottle`(+Options·throttle
  상태·OnFailure·Reset, 전부 additive). `Observe`의 GetOrAdd가 `Options=_opt` 주입. `PumpAsync` 발신 호출이 `rs`를
  throttle로 전달(같은 delivery — 4-arg 오버로드). ③ `if(ok)` 블록에 `ResetFailureLogSuppression()` 추가. **Acked/
  Computed/PushInFlight·전이당 1회·라우팅·부트스트랩·하트비트·콜드스타트 배리어 로직 byte-identical**.
- `backend/src/Wcs.Api/appsettings.json` (M) — `Wcs:ChuteStatePush`에 `SuppressRepeatedFailureLog:true` +
  `FailureLogSummaryIntervalMs:300000` + `_comment_LogThrottle`.
- `backend/tests/Wcs.Tests/PushLogThrottleTests.cs` (신규, 10 테스트) — 3계층:
  · `PushFailureLogThrottleStateTests`(6) — Decide 순수 시맨틱(첫=Emit/반복=Suppress/next_state 전이=Emit/리셋
    재무장/요약 주기당 1건/억제 off·요약 off) 결정적 clock으로.
  · `PushLogThrottleClientGateTests`(2) — 클라이언트가 게이트대로 세 sink 함께 emit/억제/요약 + delivery(HTTP 시도)
    불변(가짜 RCS 수신 시도 카운트), throttle=null 현행 동작 보존.
  · `PushLogThrottleEndToEndTests`(2, `[Collection("RealSimSerial")]`) — VS-E1: 실 Pusher+실 Client+다운 가짜 RCS+
    실 operation_log(EF)+capturing 트레이스 병치(oplog FAIL==1 / trace FAIL==1 / 재발신 시도≥9 / 복구 OK 1건+freeze);
    VS-B4: 복구 리셋 후 재실패=새 FAIL 1건.

### 억제 상태 위치 / 설정 키
- 상태: `DestinationStatusPusher.RouteState._failureLog`(PushFailureLogThrottleState), Gate 락으로 보호(per-route 원자).
- 설정: `Wcs:ChuteStatePush:SuppressRepeatedFailureLog`, `Wcs:ChuteStatePush:FailureLogSummaryIntervalMs`(하드코딩 0).

### 계약 매핑
- C1(FAIL 1건·폭주 0) = VSE1 (a)(b) + 순수 first/repeat. C2(delivery 매 주기) = VSE1 (c) 재발신 시도≥9 + 클라이언트 게이트
  delivery 3/6/9. C3(복구 1건+리셋) = VSE1 (d) + VSB4. C4(next_state 전이 새 1건) = 순수 `NextStateTransition_ReEmits`
  (실 pusher는 Computed≠Acked 디덥으로 복구가 next_state를 뒤집어 "연속 다운 중 2↔3 둘 다 실패"가 기계적 도달 불가 —
  이 사실을 VSB4 주석에 명기, 순수 Decide로 (route,next_state) 재무장을 정밀 실증). C5(설정화·락) = appsettings + Gate 락.
  C6(동작 diff 0) = 위 diff 실증. C7(양 provider 전체 GREEN·회귀 0). C8(요약) = 순수 `Summary_FiresOncePerInterval` +
  게이트 Summary sink 1건.

### 테스트 결과
- 전체 스위트: **514 GREEN / 0 FAIL**(baseline 504 + 신규 10, 산술 일치·회귀 0) — 2회 반복 동일(1m31s·1m32s).
- 신규 10 테스트: 격리 GREEN. E2E(RealSimSerial) 5회 반복 flake 0.
- 후보 push 테스트(RcsPush·ChuteStatePush·ChuteRecoveryPushHeartbeat·TraceReadyPush·SorterPushOperational·
  TwoFloorHostRouting·TwoFloorWriteGateI2) 46 GREEN — **기존 테스트 무수정**(전부 delivery/attempt 카운트만 단언,
  로그-행 수 단언 없음 → 억제(로깅만)에 불변. 클라이언트-직접 테스트는 throttle 미주입=현행 Emit). E2EGroupL 로그-행
  단언 없음.
- 빌드: Wcs.Api·Wcs.Tests 0 오류(선재 NU1903·CS8604만). 프론트 무변경 — typecheck/lint/build exit 0(선재 chunk 경고).
- Wcs.Core zero-diff(#8) 확인(git status empty).

### ⚠ 스코프 밖 foreign 변경(오케스트레이터 주의 — 이 스프린트가 만든 것 아님)
- 세션 시작 시 clean이던 워크트리에 **다른 스프린트의 미커밋 작업**이 존재: `Wcs.Data/WcsDbContext.cs`(Piece 멱등
  unique index에 `ArchivedAt IS NULL` 추가) + 마이그레이션 `20260730075818/24_FixPieceIdempotencyIndexExcludeArchived`
  (SqlServer·Sqlite) + 두 ModelSnapshot. **본 스프린트(log-throttle)는 EF/엔티티/마이그레이션 무접촉** — 이 변경들은
  로그 억제와 무관한 foreign 잔재다. 커밋 시 log-throttle 파일만 분리 권고(공유 워크트리 교훈 재적용).
- 그 외 `tasks/RESUME.md`, `tasks/sprint-contract.S-SORT-CYCLE-TIME-METRIC.bak`도 foreign(무관).

### 본 스프린트가 커밋 대상으로 만든 파일
- backend/src/Wcs.Api/Infrastructure/WcsOptions.cs
- backend/src/Wcs.Api/Services/PushFailureLogThrottle.cs (신규)
- backend/src/Wcs.Api/Services/ChuteStatePushClient.cs
- backend/src/Wcs.Api/Services/DestinationStatusPusher.cs
- backend/src/Wcs.Api/appsettings.json
- backend/tests/Wcs.Tests/PushLogThrottleTests.cs (신규)
- (+ tasks/ 프로세스 파일)

## FIX ITER (M1/M2/M4) — Generator, 2026-07-31 (코드리뷰 Step 4.5 Minor 견고화)

사용자 결정: Critical/Major 0, Minor 3건만 머지 전 견고화. **이 3건만 fix**(억제 시맨틱·delivery·성공로깅·재시도 전부 불변). M3(단조시계)·M5(센티넬)는 미착수(Minor 등재만).

- **M1 (fail-safe wrap)** `ChuteStatePushClient.cs` — `throttle?.OnFailure(firstNextState)` 호출을 `try/catch`로 격리, 예외 시 기본 `Emit` 폴백(FAIL 신호 유실보다 폭주가 안전·Fail-Loud). 인접 `EmitPushTrace`의 catch 패턴과 동형 — 로그 게이트 예외가 PumpAsync로 전파되지 않음.
- **M2 (Reset 자체 락)** `DestinationStatusPusher.cs` — `ResetFailureLogSuppression`가 스스로 `lock(Gate)` 하도록(호출자 관례 미의존·by-construction 안전). PumpAsync ③가 이미 Gate 보유 중 호출해도 동일 스레드 Monitor 재진입으로 안전. OnFailure의 check-and-set와 동일 임계구역 유지.
- **M4 (switch default → Emit)** `ChuteStatePushClient.cs` — 결합돼 있던 `case Suppress: default: break;`를 분리: `case Suppress:`는 그대로 break(무로그), `default:`는 `goto case Emit`(향후 enum 확장 시 무음 억제 방지·Fail-Loud). Emit/Summary 현행 유지.

불변 유지: 억제 시맨틱(첫 1/반복 0/복구 리셋/next_state 전이/요약)·delivery·성공 로깅(:143-146)·재시도/백오프/DORMANT·Acked/Computed/PushInFlight·전이당 1회·#7 설정화·#8 Wcs.Core zero-diff 전부 무변경. 억제 상태는 여전히 Gate 락 내 원자.

테스트: 빌드 0 오류. 전체 스위트 **514 GREEN / 0 FAIL**(baseline 504 + 신규 10, 회귀 0). foreign 미커밋 변경(WcsDbContext + FixPieceIdempotencyIndex 마이그레이션 2건) 무접촉 유지.
