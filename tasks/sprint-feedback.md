# Sprint Feedback — S-HANDSHAKE-RESIDUE (핸드셰이크 R_Flag 잔류 대사 — off-by-one 연쇄 차단)

**APPROVED**

## Phase 3 Evaluate (Evaluator fresh evidence · branch `fix/handshake-rflag-residue` · working tree · 2026-07-07)

**최종 판정: APPROVED** — Verification Scenario S1~S7 + Completion Gate ①~⑧ + 동시성 blindspot 소스 점검 전부 PASS. 모든 증거는 Evaluator가 지금 직접 재실행한 raw tool output(빌드 요약·테스트 러너 요약 raw line·arming 비활성/복원 대조·git diff·grep)이다. Generator 요약은 신뢰하지 않고 전부 fresh 재현. 코드 수정·커밋·git 조작 없음.

- 핸드오프 마커 확인: `tasks/sprint-log.md:2090` `## IMPLEMENTATION COMPLETE (S-HANDSHAKE-RESIDUE)` 존재.
- 검증 방식: **Sim3ds TCP 더블 + xUnit 통합 테스트 전용**. COM1/RTU 미기동(현장 3DS는 이설로 물리 미연결 — 사용자 확인). appsettings Transport/COM/Sorters/Provider/ConnStr 무변경.
- 베이스라인: `dotnet build` 오류 0 / 경고 10(전부 NU1903 SQLitePCLRaw 2.1.10 advisory — base develop 선재 의존성 부채, 코드 경고 아님). 신규 CS 경고 0.

---

### ④ 전체 스위트 GREEN (S7) — PASS
`dotnet test backend/Wcs.sln --no-build --blame-hang-timeout 180s` 연속 실행(전부 Evaluator fresh):
```
RUN 1  통과!  실패: 0, 통과: 175, 건너뜀: 0, 전체: 175  (14s)
RUN 2  통과!  실패: 0, 통과: 175, 건너뜀: 0, 전체: 175  (17s)
RUN 3  통과!  실패: 0, 통과: 175, 건너뜀: 0, 전체: 175  (17s)
RUN 4  통과!  실패: 0, 통과: 175, 건너뜀: 0, 전체: 175  (19s)   ← arming 복원 후 재빌드 산출물
```
169 기존 + 6 신규(HandshakeResidueTests) = 175. 4회 연속 GREEN(hang/dump 0). 신규 테스트 단독 반복(`--filter ~HandshakeResidue`) NEW RUN 1~3 전부 `실패:0 통과:6` → 신규 테스트 총 관측 6회(3 단독 + 3 전체) 전원 GREEN, flake 0. 이전에 S7OfflineAlarmTests 포트경쟁 flake가 보고됐으나 4회 전체 run 전부 GREEN으로 미발현. 빌드 경고 = NU1903 10건(선재 의존성 advisory, 코드/CS 경고 0 — S-FOLDER-ORG 선례대로 스프린트 게이트와 무관·todo 부채).

### ① fix 입증(핵심) — PASS
Evaluator가 직접 `HandshakeOrchestrator.cs`의 arming 호출(`ArmRFlagZeroAsync`)을 임시 주석 처리(백업 해시 대조로 복원 보장) 후 재빌드하여 S1/S2 실행:
```
[arming 비활성] 실패!  실패: 2, 통과: 0
  S1  Actual: RSeqMismatch   [S1] Outcome=RSeqMismatch SentCSeq=1 RSeq=123
  S2  Actual: RSeqMismatch   [S2] #1 Outcome=RSeqMismatch CSeq=1 RSeq=123
```
잔류 R_Seq=123을 새 건(cSeq=1) 응답으로 오소비 → **레벨-읽기 결함 재현(RED)**. 이후 원본 복원(파일 SHA256 `8619AAFD…8972C1` byte-identical 대조 확인) + `--no-incremental` 재빌드 후 동일 S1/S2 → `통과! 실패:0 통과:2`. ⇒ "레벨→arming" 전환이 off-by-one 연쇄를 실제로 끊었음을 Evaluator가 직접 입증.
> 주의 기록: 파일 복원 직후 incremental `dotnet build`가 재컴파일을 스킵해 S1/S2가 stale(arming-비활성) 바이너리로 RED 재현 → `--no-incremental` 강제 재빌드로 GREEN 확정. 파일 복원 후 검증은 non-incremental 재빌드 필수(incremental build stale 함정).

### ② S3 기동 reconcile / S5 확인 타임아웃 / S6 무응답 회귀 — PASS
전부 자동 테스트 GREEN + 명확 outcome 단언(소스+테스트 재확인):
- **S3** `S3_StartupReconcile_ResidueClearedByPollLoop`: Sim R 프리셋(20/123/RFlag=1) 기동 → 폴 루프 첫 유효 폴이 ClearR 큐 투입 → `!RFlag` 도달. 단언: `OnWrite("CLEAR_R")` 발화 + `OnRegisterChange(R_CellNo 20→0·R_Seq 123→0·R_Flag 1→0)` + 이후 핸드셰이크 `Success` + `HS_R_RESIDUE` 미발화(reconcile가 선처리).
- **S5** `S5_ResidueClearNotReflected_TerminalTimeout_NoCWritten`: sticky 잔류(WCS ClearR를 Sim이 재천명 — PLC 무ack 모사) → `HandshakeOutcome.RFlagResidueTimeout`, `SentCSeq=0`, `ReceivedRSeq=123`, `HS_C_SENT` 부재(C 미기입), `HS_R_RESIDUE_TIMEOUT` 발화. terminal·비-silent·테스트 단정 가능(§2C 충족).
- **S6** `S6_RealNoResponse_RFlagTimeout_Preserved`: `InjectNoResponse` → 기존 `RFlagTimeout` 경로 보존, `HS_R_RESIDUE` 미발화, `HS_C_SENT` 존재(C 정상 기입). arming 도입이 무응답 회귀 경로 훼손 안 함.

### ③ 무잔류 회귀(S4) — PASS
`S4_CleanPath_TwoConsecutive_NoResidueReconcile`: 깨끗한 상태(R_Flag=0) 연속 2건 → 2건 모두 `Success` + `HS_R_RESIDUE`/`HS_R_ARMED` **미발화**(추가 지연 0·깨끗한 경로 기존 타이밍 보존). 소스에서도 `ArmRFlagZeroAsync`가 `if (!snap.RFlag) return null`로 즉시 진행(함정 1 회피) 확인.

### ⑤ 절대규칙 준수 — PASS
- **#1 (단일 큐)**: 잔류 대사 ClearR = 오케스트레이터 `_gw.EnqueueAsync(new PlcWrite.ClearR())` (HandshakeOrchestrator.cs:170). 기동 reconcile ClearR = 폴 루프 `_writeQueue.Writer.TryWrite(new PlcWrite.ClearR())` (PlcGateway.cs:300). 둘 다 단일 쓰기 큐 → 단일 컨슈머(`case PlcWrite.ClearR:` WriteMultipleRegisters + RmwD4Locked, :460-470)에서만 실 Modbus 쓰기. **오케스트레이터 직접 Modbus 호출 grep 0**(`WriteSingleRegister|WriteMultipleRegister|_master.Write|WriteAsync` → No matches).
- **#7 (타이밍=설정)**: 확인 타임아웃 = `_opt.RFlagClearConfirmTimeoutMs`(appsettings `Timing:RFlagClearConfirmTimeoutMs=2000` + Program.cs TimingOptions/SorterTimingOverride 배선). Wcs.PlcGateway 신규 로직 하드코딩 ms **grep 0**(`Task.Delay([0-9]|AddMilliseconds([0-9]` → No matches; 대기는 `_opt.RFlagPollMs`/`_opt.RFlagClearConfirmTimeoutMs` 바인딩).
- **#8 (Core 무접촉)**: `git diff --stat -- backend/src/Wcs.Core` **빈 출력**.
- **#3 (TgtFloor)**: 오케스트레이터 TgtFloor 쓰기 0(R 클리어만). appsettings diff에 TgtFloor 무접촉.

### ⑥ 관측성 — PASS
- 잔류 대사: S1 테스트가 `HS_R_RESIDUE` detail에 `"rCellNo":20`·`"rSeq":123` 포함을 단언(GREEN). `HS_R_ARMED`·`HS_C_SENT` 발화 확인.
- 기동 reconcile: S3가 `OnWrite("CLEAR_R")` + `OnRegisterChange` 잔류값 전이(20→0·123→0·1→0)를 단언(GREEN).
- operation_log 결선: `Program.cs:408` `bundle.SubscribeHandshakeStage((action,detail) => opLog.Log(HANDSHAKE, action, …))` — 기존 구독이 신규 action(HS_R_RESIDUE/HS_R_ARMED/HS_R_RESIDUE_TIMEOUT)을 자동으로 operation_log **HANDSHAKE** 카테고리에 기록(HS_R_RESIDUE_TIMEOUT은 "TIMEOUT" 포함 → ERROR 레벨). Wcs.Api 무변경.

### ⑦ 무변경 가드 — PASS
`git status --porcelain` = 핸드오프 동일(9 M + 1 ??): Program.cs·appsettings.json·HandshakeOrchestrator.cs·PlcGateway.cs·SimServer.cs·docs/SPEC.md·tasks/{lessons,sprint-contract,sprint-log}.md(하네스 산출물) + `?? backend/tests/Wcs.Tests/HandshakeResidueTests.cs`.
- `git diff --stat -- frontend` **빈 출력** / `-- backend/src/Wcs.Core` **빈 출력** / `-- Wcs.Data + Migrations.Sqlite/SqlServer` **빈 출력**.
- appsettings diff = `RFlagClearConfirmTimeoutMs` 키 + 주석 추가만. Transport/COM/Sorters/Provider/ConnectionStrings/TgtFloor **무변경**(grep 확인).

### ⑧ 관측 무영향(fail-safe) — PASS
`EmitStage`(HandshakeOrchestrator:87-91)·`EmitWrite`(PlcGateway:508-512)·`EmitRegisterChanges`(:376-381) 전부 try/catch로 핸들러 예외 격리. 기동 reconcile 로깅도 try/catch 보호(:291-297) + 폴 루프 외곽 try/catch 내부. 타이밍 회귀 0(④ 4회 + ③ 깨끗한 경로 지연 0).

### 동시성 blindspot 소스 점검(feedback-archive 교훈 — GREEN만으론 불충분) — PASS
- **arming 대기 루프 취소**: `while(true) { await Task.Delay(_opt.RFlagPollMs, ct); … }` — ct 취소 시 OCE 전파(기존 WaitCFlagZero/WaitRFlagAndProcess와 동형). deadline 검사로 무한 대기 불가. 일관·안전.
- **OnStage 신규 action 예외 격리**: 신규 4개 action 전부 EmitStage 경유(try/catch). 확인.
- **startup reconcile 정확히 1회**: `startupReconciled`는 `RunPollLoopAsync` 지역변수 — 첫 Online 폴에서 RFlag 판정 전 true 세팅(:288) → poll-loop 1회 기동 = 게이트웨이 1회 기동당 1회. 재기동 시 새 루프에서 리셋. 확인.
- **reconcile ClearR ↔ 이른 첫 핸드셰이크 경쟁**: 두 ClearR 모두 R 영역 0 기입(멱등) + 단일 큐 단일 컨슈머 직렬화 + arming이 잔류 재확인·재처리 → 최악 중복 ClearR(무해). per-소터 순차 dispatch 전제 위에서 안전(F1b는 Scope OUT·악화 없음). S1(런타임 잔류→arming)·S3(기동→reconcile) 두 타이밍 모두 GREEN으로 커버.

### 프로세스/포트 정리 — PASS
Wcs.Sim3ds/Wcs.Api/testhost/vstest.console 오펀 0 · 포트 1502·5080 free(연결 0). 임시 백업은 scratchpad 한정(리포 산출물 0). 워킹트리 핸드오프 동일.

---

## 판정 근거 요약
①~⑧ 전부 PASS · S1~S7 전부 PASS · 동시성 blindspot 소스 점검 4항 PASS. 반복 FAIL·repeat-issue 없음(신규 결함 0 → lessons.md 승격 대상 없음; Generator가 이미 A-1 교훈 1행 등재). docs/SPEC.md §4-A 동기화 완료. → **APPROVED**.

## Code Review Minor (4-Tier Step 4.5 — S-HANDSHAKE-RESIDUE, 병합 비차단·다음 스프린트 Generator 참조)

1. **HS_R_RESIDUE가 operation_log에서 INFO로 분류** — Program.cs 레벨 분류기가 키워드(MISMATCH/TIMEOUT/OFFLINE) 기반이라 잔류 감지 이벤트가 INFO로 기록됨(ILogger는 WARN). 분류기에 RESIDUE 키워드 WARN 승격 권고(현장 추적성).
2. **기동 reconcile가 spurious RFlagRaised 에지 미억제** — 첫 폴 잔류가 에지 채널에 발화. 현재 소비자 0이라 무해(pre-existing), 향후 에지 소비자 추가 시 잠복 함정 — 억제 또는 주석 명시.
3. **InjectStickyRResidue 비-volatile** — 형제 `_noResponse`는 volatile. lock 경유 읽기라 실무 무해(테스트 더블 일관성 흠).
4. **TimingOptions 레코드 중복 필드** — 실 바인딩은 PlcGatewayOptions 경유, TimingOptions 사본 미사용(기존 dual-record 패턴 답습). 통합 여지.

리뷰어 백로그 권고: 대기 루프 deadline의 벽시계(DateTimeOffset.Now)→Stopwatch(monotonic) 일괄 전환 검토(코드베이스 전반 관행, 이 스프린트 밖). RFlagRaised 채널 소비자 부재 — 데드코드 정리 vs 소비 설계 확정 필요.
