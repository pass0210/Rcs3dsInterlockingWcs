# Sprint Feedback — S-S5-FLAKE (핸드셰이크 S5 테스트 결정적 안정화)

**APPROVED** — Evaluator, 2026-07-08 (1 iteration to pass).

브랜치 `fix/s5-handshake-flake` (working tree, 미커밋). Evaluator 는 코드를 고치지 않음 — 근본원인 대조를 위한
일시 revert 는 Copy-Item 백업+SHA256 대조로 복원(파괴적 `git checkout` 미사용, lessons 2026-07-06 준수).
Ground truth = git HEAD/status + 실제 코드/diff 판독 + 독립 테스트 재실행. Generator 요약은 신뢰하지 않고 전부 독립 재현.

핸드오프 확인: `tasks/sprint-log.md` L2917 에 `## IMPLEMENTATION COMPLETE — S-S5-FLAKE` 마커 존재 → 활성화 정당.

---

## 변경 표면 (ground truth · git diff vs develop)

- 변경 파일 4개: `backend/src/Wcs.Sim3ds/SimServer.cs`(Sim, +46/-1), `backend/tests/Wcs.Tests/HandshakeResidueTests.cs`(테스트, +65),
  `tasks/sprint-contract.md`(Planner 문서), `tasks/sprint-log.md`(로그). **프로덕션 코드 0줄.**
- **PROTECTED-ZONE 게이트 PASS**: `git diff develop --stat -- backend/src/Wcs.PlcGateway backend/src/Wcs.Core backend/src/Wcs.Api backend/src/Wcs.Data`
  → **빈 출력**. `HandshakeOrchestrator`/`PlcPollingService` 핸드셰이크 코어 미변경. 절대규칙 #1~#5 프로덕션 동작 불변.
  수정은 Sim 충실도(fidelity) + 테스트 전용. Q1 사전 보고 조건 미해당(프로덕션 미접촉).
- 필드/프로퍼티 배선 확인: `_stickyRResidue`(volatile bool) ↔ `InjectStickyRResidue { get; set; }` 정합, 핸들러 `if(!_stickyRResidue) return`
  게이트 → sticky 미사용 테스트/전송(TCP/RTU)에 영향 0.

## [40%] 결정성 증명 — 독립 재실행 fresh evidence

- **`dotnet test backend/Wcs.sln` 연속 10회 전부 GREEN** (각 회 `실패: 0, 통과: 289, 건너뜀: 0, 전체: 289`, 13~15s):
  ```
  RUN 1  통과! - 실패: 0, 통과: 289, 건너뜀: 0  (15s)
  RUN 2  통과! - 실패: 0, 통과: 289, 건너뜀: 0  (15s)
  RUN 3  통과! - 실패: 0, 통과: 289, 건너뜀: 0  (14s)
  RUN 4  통과! - 실패: 0, 통과: 289, 건너뜀: 0  (14s)
  RUN 5  통과! - 실패: 0, 통과: 289, 건너뜀: 0  (14s)
  RUN 6  통과! - 실패: 0, 통과: 289, 건너뜀: 0  (13s)
  RUN 7  통과! - 실패: 0, 통과: 289, 건너뜀: 0  (15s)
  RUN 8  통과! - 실패: 0, 통과: 289, 건너뜀: 0  (14s)
  RUN 9  통과! - 실패: 0, 통과: 289, 건너뜀: 0  (15s)
  RUN 10 통과! - 실패: 0, 통과: 289, 건너뜀: 0  (14s)
  ```
  (원문: scratchpad `eval_loop_results.txt`.) + 마지막 클린 full-rebuild(`--no-incremental`) 후 재실행 1회 = **289 GREEN** (총 11회 GREEN).
- **S5 단독 + S5b 포함 `HandshakeResidueTests`(7건) 격리 3회 반복 전부 GREEN** (2s/회).
- **인위적 병렬 부하:** 전체 스위트 자체가 무거운 실 Sim/webhost/E2E(ScenarioTests·ApiIntegrationTests)와 xUnit 기본 병렬로 동시 CPU 경합을
  일으키는 부하 조건 — 그 하에서 S5 가 10/10 GREEN. 과거 이 조건이 flake 발현 표면이었음(lessons e2e-parallel-load).

## [25%] 근본 원인 정합성 — Evaluator 직접 revert 대조 (fresh, 신뢰 아닌 실측)

- 명명된 근본원인(H1): Sim sticky 재천명이 Sim 루프(SimLoopMs=10ms)에만 일어나 WCS ClearR RMW(FC06)가 서버 버퍼에 R_Flag=0 을 쓴 뒤
  다음 Sim flush 까지의 창에서 버퍼가 R_Flag=0 을 노출 → GW 폴이 그 0 을 샘플링 → `ArmRFlagZeroAsync` 거짓 완료 → C 기입 → outcome 뒤집힘.
- **직접 대조(내 손으로 재현):** SimServer.cs 를 백업(SHA256 `28EF00…4E84`) 후 `RegistersChanged` 구독 2줄을 주석 처리(=수정 비활성, Sim 루프 백스톱만 잔존)
  → 테스트 프로젝트 `--no-incremental` 재빌드(stale-binary 함정 회피, lessons S-HANDSHAKE-RESIDUE) → **`S5b` 결정적 RED**:
  ```
  Assert.Equal() Failure: Expected: 0, Actual: 98
  [S5b] 100회 클리어 후 즉시 read-back에서 R_Flag=0 관측 = 98
  ```
  → 백업본 Copy-Item 복원(SHA256 재대조 = MATCH) → `--no-incremental` 재빌드 → **`S5b` GREEN**.
- 인과 확정: 동기 재천명(RegistersChanged 훅)을 제거하면 transient R_Flag=0 창(98/100)이 되살아나고, 되돌리면 사라진다.
  Generator 주장(revert 99/100 RED)과 동일 차수·결정적 RED 로 독립 확인. "부하 탓" 수준 아님 — 어느 버퍼값이 어느 순서로 노출되는지까지 특정됨.
- revert 후 git status/diff 확인: 변경 4파일 그대로, SimServer diff `+46/-1` 불변 → **Generator 산출물 무손실 복원 확인.**

## [20%] 마스킹 아님 — ANTI-MASKING 게이트 PASS

- **고정 sleep/Task.Delay 로 창 회피 0**: SimServer 수정은 이벤트 기반 동기 복원. `HandshakeResidueTests` 의 `Task.Delay(50)` 은
  **포트 바인딩 경쟁(SocketException) 재시도 백오프**뿐(6회, 기존 `StartRobustAsync` 패턴 동형) — 레이스 은폐 아님.
- **임의 재시도로 실패 은폐 0**: S5b 는 100회 반복 후 `Assert.Equal(0, observedZero)` 단일 결정적 단정. 실패를 삼키는 retry 없음.
- **어서션 약화/삭제 0**: 원 S5(`S5_ResidueClearNotReflected_TerminalTimeout_NoCWritten`) 어서션 **완전 불변**(diff 미포함) —
  `Assert.Equal(RFlagResidueTimeout, Outcome)`, `SentCSeq==0`, `DoesNotContain(HS_C_SENT)`, `Contains(HS_R_RESIDUE_TIMEOUT)` (L281-288) 그대로.
- **하드코딩 프로덕션 ms 0**: 새 프로덕션 타임값 도입 없음(프로덕션 미접촉). `readTimeoutMs:1000`/`iterations=100` 은 테스트 클라이언트 상한·반복수(스캐폴딩 상수)로 절대규칙 #7(운영 타이밍 appsettings) 대상 아님.
- **원 의도 보존**: ClearR 미반영 → 터미널 타임아웃 → C 미기입. Sim 충실도가 실 무ack PLC 의미(관측상 R_Flag 0 미하락)와 정합.

## [10%] 회귀 0 + 프로덕션 불변

- 288 baseline → **289**(신설 S5b +1). S1~S4·S6 포함 전건 GREEN, 10회 반복 중 새 flaky 0. 실패/스킵 은폐 0(건너뜀 매회 0).
- 프로덕션 핸드셰이크 코어 미변경(위 PROTECTED-ZONE 게이트).

## [5%] 위생

- **빌드 클린**: 0 오류. 경고 10개 전부 선재 NU1903(SQLitePCLRaw 2.1.10 GHSA-2m69) — 내 변경분 신규 경고 0(feedback-archive 기 확인 부채).
- **고아 프로세스 0**: 런 전/후 `Wcs.Sim3ds.exe`/`Wcs.Api.exe` 없음. MSB3021/파일잠금 0.

## Completion Conditions (계약 §Completion 1~8) — 전부 충족

1. 캡처된 재현 문서화(sprint-log 실제 outcome=Success/RSeqMismatch·HS_C_SENT·sawZero 32/32) ✅
2. 근본원인 1개(H1) 명명 + revert 대조로 뒷받침(Evaluator 직접 98/100 RED) ✅
3. `dotnet test` 연속 10회 전부 GREEN(독립 재실행 fresh) ✅
4. S5 단독 GREEN + 병렬 부하 하 GREEN ✅
5. 회귀 0(289 전건·S1~S6 무 flaky) ✅
6. 새 하드코딩 타임값 0·고정 sleep/임의 재시도/어서션 약화 0 ✅
7. 고아 Sim3ds.exe 0·빌드 클린 ✅
8. 프로덕션 핸드셰이크 코어 미접촉(Q1 보고 조건 미해당) ✅

## Repeat detection

- 과거 s9-flake/sim-timeline/e2e-parallel-load/teardown-race 교훈과 동류의 "1회 GREEN 불신·비동기 append≠스냅샷 전이" 원칙을 준수해 검증(10회 반복 + 직접 revert 대조).
  이번 수정은 그 교훈들이 지적한 창을 **결정적으로 제거**(스케줄링 무관) → 반복 이슈 아님, 신규 lessons 승격 불요.

## Minor (비차단)

- 없음(신규 결함 0).

## 검증 산물 정리

- SimServer.cs 백업/복원 SHA256 대조 완료(원본 무손실). scratchpad `eval_loop_results.txt`·`SimServer.cs.bak` 만 잔존(gitignored 밖 scratch).
- git status = Generator 4파일 그대로(평가 산물 유출 0). 고아 프로세스 0.

→ **결론: 8개 Completion Condition + 5개 게이트(결정성·근본원인·마스킹아님·회귀0/프로덕션불변·위생) 전부 PASS. APPROVED.**

---

## Step 4.5 Code Review (독립 코드리뷰 — orchestrator 기록)
- **판정: Ready to merge = Yes.** Critical 0 · Important 0 · Minor 3.
- 리뷰어가 FluentModbus 5.3.2 IL 디컴파일로 동시성 검증: `Lock→_hrLock` 단일 순서·`GetHoldingRegisters` lock-free → **데드락 불가**; 재기입이 clear와 동일 `ModbusServer.Lock` 구간에서 원자 실행 → **일시적 R_Flag=0 창 실제 차단**; sticky off 시 lock 전 early-return → **블라스트 반경 0**; 프로덕션 무접촉; S5b 정당한 revert-contrast 가드(masking 아님), 원 S5 assertion 무변경.
- Minor 3건(핸들러 `-=` 미대칭·핸들러 try/catch 부재(Fail Loud)·docstring "전혀" 과장) + 업그레이드 체크리스트 = 전부 방어적·현재 미발현 → tasks/todo.md 이연. fix-only 반복 불요(BLOCKING 0). 커밋 진행.

---

## AGGREGATION (orchestrator) — 2-Evaluator 풀 집계
- **FUNCTIONAL: PASS** (tasks/sprint-feedback/functional.md) — 302/302 GREEN, O1~O6 계약대로, A-8 해소, pause→PAUSED/resume→NORMAL, 안전3종 Sim 반영·단일 큐 경유, destination_event operator_id, PlcGateway 코어·frontend 무접촉.
- **SAFETY: PASS** (tasks/sprint-feedback/safety.md) — 7 안전게이트 전부 통과: 단일 큐 enqueue(직접 Modbus 0)·SAFE-3만·TgtFloor 게이트/무클리어 보존·PlcGateway diff EMPTY(S1~S6 무변경)·Sim TCP 전용·마이그레이션 0·감사 append-only(operator_id).
- **집계 결과: APPROVED** (AND — 두 차원 모두 PASS).
- 비차단 노트: (1) teardown ObjectDisposedException(OperationLogService.FlushBatchAsync) = 기존 observability 스트림·F3a 무관 → S-OBSERVABILITY 백로그. (2) sprint-log.md stray null byte → 텍스트 정규화 권고(추적파일).

**APPROVED — S-F3a (functional ∧ safety)**

---

## FIX ITER 1 재검증 집계 (orchestrator) — 코드리뷰 I-1/I-2
- 코드리뷰(Step 4.5): critical=0 major=2(I-1 O4/O6 무음 short 오버플로 PLC 기입, I-2 AlreadyInState 인메모리 재조정 스킵) minor=7(todo 이연).
- Generator fix: I-1 = O4/O6 상한을 설정 `Wcs:OpsLimits`{20/1000/30000} + 하드 short.MaxValue 이중 상한으로 검증(초과 400·enqueue 0), I-2 = AlreadyInState 경로도 CHUTE 인메모리 재동기.
- **FUNCTIONAL 재검증 PASS**: full-suite 305 GREEN(302+3 신규), O4/O6 초과(floor 21·70000, cellNo 1001·70000, seq 30001)→400·enqueue 0·D6 불변, I-2 divergence self-heal 실증. PlcGateway 코어 diff empty·마이그레이션 0·frontend 무접촉.
- **SAFETY 재검증 PASS**: 7 안전게이트 유지, 상한이 config 기반(리터럴 아님)+하드 short 상한, 초과 enqueue 0, PlcGateway diff EMPTY, safe-3만, Sim 전용, 감사 append-only.
- **집계 결과: APPROVED (functional ∧ safety, 305 GREEN)**. 커밋 진행.

**APPROVED — S-F3a (fix iter 1 재검증 포함, 305 GREEN)**

---

## AGGREGATION (orchestrator) — 2-Evaluator 풀 (기능+정합성)
- **FUNCTIONAL: PASS** (functional.md) — full-suite 312 GREEN(305+7), 수용행위 (a)누적 (b)Capacity+1→NG 유출0 (c)완료→release→재사용 0부터 전부 GREEN·코드판독 일치, E5/E6 강화(은폐 0), PLC/frontend 무접촉·마이그레이션 0·Sim 전용.
- **CORRECTNESS: PASS** (correctness.md) — 5 불변식 게이트: ①IF-05↔SelectCell 동형(오버플로 벡터 삭제·EC-13 sweep) ②loaded-qty 배정기간 스코프(UTC 일관·provider-neutral·EC-11) ③release=오더완료(원자·A-7 흡수) ④PlcGateway diff empty·#1/#3 불변·마이그레이션0 ⑤테스트 진정(신규 EC-10~13·E7~9 구코드서 RED).
- **집계 결과: APPROVED (functional ∧ correctness, 312 GREEN)**.
- 비차단 minor 3건 todo: (1) E2E AB A2 주석↔단언 불일치, (2) 동시 동일오더 IF-10→②중복배정 read-then-create race(직렬 dispatch 안전·후속), (3) SelectCell② 비-RUNNING 오더 무배정 셀 반환(선재).

**APPROVED — S-CELL-ACCUM (functional ∧ correctness, 312 GREEN)**

---

## FIX ITER 1 재검증 집계 (orchestrator) — 코드리뷰 #1/#2 + cleanups
- 코드리뷰(Step 4.5): critical=0 major=2(#2 LoadedQtyByCell hot-path 이력전량 로드 성능회귀, #1 SortedQty RMW 동시충돌 Finalize 전체롤백) minor=5(#6 등 todo 이연).
- Generator fix: #2 = SQL 전역 하한 minFrom(bounded fetch)+셀별 정밀 하한 in-memory 유지, #1 = SortedQty ExecuteUpdate 원자 증가+재-read 완료판정(wasAlreadyLoaded 멱등 유지). cleanups #3/#4/#5/#7.
- **FUNCTIONAL 재검증 PASS**: 312 GREEN(단일스레드 결정적·회귀0), 수용행위 (a)/(b)/(c) 전부 유지(EC-11·E9), SortedQty 완료 정확(E5 2piece→완료). PlcGateway/frontend 무접촉·마이그레이션 0·9파일.
- **CORRECTNESS 재검증 PASS**: 5게이트 유지 — #1 ExecuteUpdate 명시 tx 원자·read-your-writes(stale 0)·멱등, #2 minFrom safe superset·셀별 정밀 하한 유지(재사용 0부터), cleanups 정상, PlcGateway diff empty.
- **집계 결과: APPROVED (functional ∧ correctness, 312 GREEN)**. 커밋 진행.

**APPROVED — S-CELL-ACCUM (fix iter 1 재검증 포함, 312 GREEN)**
