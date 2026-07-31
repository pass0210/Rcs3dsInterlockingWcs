# Sprint Feedback — S-IF08-PUSH-LOG-THROTTLE

(Evaluator가 PASS/FAIL·APPROVED 기록)

## 평가 (Evaluator, 2026-07-31) — APPROVED

Ground-truth: HEAD `00cece141`, branch `feat/if08-push-log-throttle`. sprint-log.md `## IMPLEMENTATION COMPLETE` 마커 확인. 본 스프린트 변경 6파일(WcsOptions.cs·PushFailureLogThrottle.cs[신규]·ChuteStatePushClient.cs·DestinationStatusPusher.cs·appsettings.json·PushLogThrottleTests.cs[신규]). foreign 미커밋(WcsDbContext + FixPieceIdempotency 마이그레이션·RESUME.md·.bak)은 본 스프린트 산출 아님 — 회귀·#8 판정에서 제외.

### 독립 검증 실행(fresh evidence)
- 빌드: `dotnet build backend/Wcs.sln` → 오류 0개, 경고 10개 전부 선재 NU1903(SQLitePCLRaw). 신규 경고 0.
- 신규 10 테스트 격리: `--filter ~PushLogThrottle|~PushFailureLogThrottle` → 통과 10/0.
- 전체 스위트: `dotnet test backend/Wcs.sln --no-build` → **514 GREEN / 0 FAIL**(baseline 504 + 신규 10, 산술 일치, 1m32s). 회귀 0.
- E2E(RealSimSerial) 억제 테스트 격리 3회 반복: 각 2/2 GREEN(+격리1+전체1=총5회) flake 0.
- **Evaluator 자체 실측(throwaway probe, 측정 후 삭제)**: 실 `DestinationStatusPusher` 관찰 루프 + 실 `ChuteStatePushClient` + 실 `RouteState` 억제 게이트 + 다운 `FakeChuteStateServer` + 실 operation_log(EF SQLite) + capturing 트레이스 + **closed-generic `ILogger<ChuteStatePushClient>` 교체**(Program 은 `UseSerilog` writeToProviders:false 라 MEL provider 우회 → 실 클라이언트 로거를 직접 교체해야 sink(c) 측정 가능)를 한 스택에 결선. 같은 실패 전이를 ≥7 주기(delivery 22~25 시도) 돌린 뒤 **분리 단언**(3회 반복 동일):
  - (a) operation_log CHUTESTATE_PUSH WARN "FAIL" == **1**
  - (b) 트레이스 이벤트 8 result:"FAIL" == **1**
  - (c) Serilog `LogError` == **1** ← 억제됨(22~25 재발신에도 추가 0) · 실 스택 직접 측정
  - (d) fake RCS PUT 실패 시도 == **22~25**(≥9 · 재발신 살아있음)
  - 복구 → OK 로그 1건 + 재발신 freeze(총 31 안정) / 재실패(새 전이) → 새 FAIL 1건(총2)·새 LogError(총2) = 리셋 재무장.

### Completion Conditions (전부 AND)
- **C1** PASS — RCS 다운 ≥7 주기 재발신에도 oplog FAIL 정확히 1·트레이스 8/10 FAIL 정확히 1(probe (a)(b) + VSE1 + 순수 first/repeat). 폭주 부재 stableCount:6 확정.
- **C2** PASS — delivery 시도 22~25(≥9), 로그만 억제·push 안 죽음(probe (d) + VSE1 재발신≥9).
- **C3** PASS — 복구 성공 로그 정확히 1건 + freeze, 직후 재실패 = 새 FAIL 1건(probe + VS-B4).
- **C4** PASS — next_state 전이(2↔3) 새 FAIL 1건. 순수 `NextStateTransition_ReEmits_SameRoute`(2→3→2 각 재emit) + VS-B4 통합(nextState 2 FAIL==1·nextState 3 FAIL==1, probe 확인). "연속 다운 중 2↔3 동시 실패는 Computed≠Acked 디덥으로 기계적 도달 불가"라는 생성자 논거는 (route,next_state) 키잉을 순수+통합 이중 실증으로 대체 — 방어적 타당(과소/과다 로깅 양방향 순수 커버).
- **C5** PASS — 상수 2개 appsettings `Wcs:ChuteStatePush`(SuppressRepeatedFailureLog:true·FailureLogSummaryIntervalMs:300000), 코드 하드코딩 0(설정 default init-only = 확립된 패턴). Wcs.Core git status **empty(zero-diff, #8)**. 억제 상태 갱신: `OnFailure`=`lock(Gate)→Decide`, `ResetFailureLogSuppression`=③ `if(ok)` 블록 내(`lock(rs.Gate)` 보유) — per-route 락 내 원자 check-and-set, 코드 직독 확인. 발신은 락 밖 → OnFailure 의 lock(Gate) 재진입/데드락 없음.
- **C6** PASS — diff 실증: Emit case 3-sink 발화가 구 FAIL 경로와 **byte-identical**(LogError 문자열·인자·oplog WARN FAIL·EmitPushTrace FAIL 동일). 성공 블록(:152-161)·재시도 루프·백오프·DORMANT 가드·IsSuccessBody·per-attempt LogWarning 무접촉. Pusher diff = additive(throttle 필드+OnFailure+Reset+GetOrAdd Options+rs 전달+if(ok) Reset)만, Acked/Computed/PushInFlight/라우팅/부트스트랩/하트비트 로직 불변.
- **C7** PASS — 전체 514 GREEN·회귀 0. **기존 테스트 0건 수정**(git status: 신규 PushLogThrottleTests.cs만 untracked, 갱신 대상으로 지목된 후보 push 테스트 전부 무수정 — delivery/attempt 단언이라 로그 억제에 불변). 계약 #10 예상보다 강한 결과(테스트 편집 회귀 리스크 0). SqlServer provider: 본 스프린트는 EF/엔티티/마이그레이션 무접촉(스키마 영향 0, operation_log 는 기존 테이블·기존 IOperationLogger 경로) → provider 패리티 위험 없음(sqlserver-migration 교훈은 스키마 변경 스프린트 대상, 본 스프린트 해당 없음).
- **C8** PASS — 저빈도 요약: 순수 `Summary_FiresOncePerInterval_NotEveryFailure`(결정적 clock: 0 Emit → 500/999 Suppress → 1000 Summary → 1500 Suppress → 2000 Summary) + 클라이언트-게이트 Summary → oplog result:"SUMMARY" 1건 + Serilog WARN 1건("아직 실패 중(요약)") + FAIL/트레이스/Error 추가 0. 완전 무음 아님(Fail-Loud).

### Verification Scenarios
- **VS-B1~B6** PASS — B1(첫 실패 각 1)·B2(반복 억제+delivery 지속)·B3(복구 1+freeze)·B4(리셋 후 재실패 새 1)·B5(next_state 전이 새 1)·B6(동작 불변 diff 0). probe + VSE1/VS-B4 테스트 + 순수 6 + 클라이언트-게이트 2.
- **VS-E1** PASS — 실 스택 병치 분리 단언(생성자 VSE1 테스트 + Evaluator probe 가 sink(c) LogError 직접 측정으로 보강). GREEN 하나로 합치지 않음.
- **VS-U1/U2(간접)** PASS — 프론트 git status **empty(diff 0)**. /trace·모니터링 뷰어는 데이터(파일/DB tail)가 줄어든 것뿐, 컴포넌트·API 클라이언트 무변경. UI default/alternate/empty/dark-mode 슬롯 N/A(신규 UI 없음) 정당.

### Static checks
- C# 컴파일러: 오류 0. 프론트: diff 0(정적검사 대상 변경 없음). 린터: 백엔드 별도 린터 미구성(컴파일러 경고 = 선재 NU1903만).

### 스레드안전 코드 직독(C5 흡수 · GREEN 무의미 영역)
- `RouteState : IPushFailureLogThrottle`. `OnFailure(nextState)` → `lock(Gate){ _failureLog.Decide(nextState, Options.SuppressRepeatedFailureLog, Options.FailureLogSummaryIntervalMs, DateTimeOffset.UtcNow) }` — 비원자 check-then-act 없음. `_failureLog`(PushFailureLogThrottleState)는 락 없는 순수 상태기, 소유자 Gate 락으로 직렬화. Reset 은 PumpAsync ③ `if(ok)` 블록(동일 Gate 락) 내 — OnFailure 의 check-and-set 와 원자 직렬. 발신(락 밖)·PushInFlight 가드(동일 route 동시 발신 차단)로 route 간/내 경합 없음.

전 항목 PASS.

## FIX ITER 재검증 (Evaluator, 2026-07-31) — 코드리뷰 Minor M1/M2/M4 견고화

코드리뷰 Step 4.5 Minor 3건 하드닝. diff 정확성 + 회귀 0 + 불변식 유지 독립 재검증:
- **M1** (`ChuteStatePushClient.cs`) PASS — `throttle?.OnFailure(firstNextState)` 를 try/catch 로 격리, 예외 시 `logAction = Emit` 폴백. 예외를 삼키지 않고 **loud 경로(Emit=3 sink 로깅)로 전환** → FAIL 신호 유실 방지(Fail-Loud). OnFailure 는 순수 lock+Decide 라 실 RouteState 에선 throw 없음 → 비예외 경로는 구 동작과 동일(LogError 발화 불변). 실 스택 probe 재측정으로 확인(아래).
- **M2** (`DestinationStatusPusher.cs`) PASS — `ResetFailureLogSuppression` 가 자체 `lock(Gate)` 획득(by-construction 안전, 호출자 관례 미의존). PumpAsync ③ 가 이미 `lock(rs.Gate)` 보유 중 호출해도 **Monitor 재진입**(동일 스레드)으로 안전 — 재귀 카운트 증가/감소, 외곽 락 유지, 조기 해제·데드락 없음. OnFailure 의 check-and-set 와 동일 임계구역·원자.
- **M4** (`ChuteStatePushClient.cs`) PASS — switch 에서 `case Suppress: break;`(무로그) 와 `default: goto case Emit;`(미지 판정=Fail-Loud Emit) 분리. **현행 3 enum 값(Emit/Summary/Suppress) 동작 완전 불변**(전부 명시 case) — default 는 향후 enum 확장/무효 캐스트에서만 도달, 무음 억제 방지. 억제 시맨틱 회귀 0.

### 독립 재검증 실행(fresh evidence)
- 빌드: 오류 0(선재 NU1903만). Wcs.Core git status empty(#8 유지), 프론트 diff 0.
- 전체 스위트: 후속 3회 중 **514 GREEN 2회** + 1회 513/1-FAIL(host-startup 플레이크 "Hosting failed to start", 병렬 부하 표면화 — e2e-parallel-load/testhost-teardown 교훈). 귀속: (i) 스프린트 표면(throttle+push 45 테스트) **격리 3회 연속 GREEN**(45/45), (ii) throttle E2E 이전 5회 flake 0, (iii) 실패 모드=호스트 기동(인프라)이지 억제 로직 단언 아님 → **회귀 아님**. 단일 RED 로 FAIL 금지 원칙 적용(간헐성+격리 귀속).
- **Evaluator probe 재실행(하드닝 코드, 측정 후 삭제)**: 실 Pusher+실 RouteState 게이트+다운 fake RCS+실 EF oplog+capturing 트레이스+closed-generic ILogger 교체 → (a)oplog FAIL==1·(b)트레이스 FAIL==1·**(c)Serilog LogError==1(23 재발신 시도에도 억제)**·(d)delivery 23 병치 단언. 복구 freeze(총31) → 재실패 새 FAIL(총2)·새 LogError(총2)=리셋 재무장. **M1/M2/M4 후에도 억제 시맨틱 완전 보존**(첫1/반복0/복구1/리셋 재무장).

억제 시맨틱·delivery·성공로깅(:152-161)·재시도/백오프/DORMANT/Acked·Computed·PushInFlight·전이당 1회 발신 무변경(diff = M1/M2/M4 하드닝 지점 + 기존 additive만). #7/#8 유지. foreign 미커밋(WcsDbContext + FixPieceIdempotency 마이그레이션)은 본 스프린트 아님(제외).

FIX ITER 전 항목 PASS. Minor 없음.

**APPROVED (FIX ITER M1/M2/M4 반영)**

## Minor (코드리뷰 잔여 — 다음 스프린트 Generator 참고)
- **M3**: 요약 간격이 wall-clock(`DateTimeOffset.UtcNow`)로 측정됨(`PushFailureLogThrottleState.Decide`) — NTP/수동 시계 스텝 시 요약이 과소/과다 발화 가능. 5분 생존 하트비트라 영향 benign. `Environment.TickCount64`/`Stopwatch.GetTimestamp()` 단조시계로 교체 권고.
- **M5**: `ChuteStatePushClient` FAIL 경로에서 빈 `payload.NextStates` 시 `int.MinValue`를 억제 키로 사용(`firstNextState` 폴백). Pusher는 항상 길이-1이라 도달 불가·EmitPushTrace가 빈 payload 가드 → 버그 아님. cosmetic(명시적 no-suppress 처리 권고).
