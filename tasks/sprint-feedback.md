# Sprint Feedback — S-MULTISORTER-SHARED-BUS (Phase 1)

Evaluator: 단일 (functional-and-regression + concurrency-and-frame-integrity 2축 동시).
평가일: 2026-07-16. 브랜치: feat/multisorter-shared-bus (HEAD 8c25421, 작업은 전부 워킹트리 미커밋).
방법: ground-truth git 확인 + 계약/코드 직독 + fresh Release 빌드/테스트(자체 실행) + develop stash 대조.

---

## 판정: APPROVED (10/10 조건 PASS, 2 차원 PASS) — 단, 아래 flake 관찰 1건 기록·추적 권고

---

## 스코프 (C9) — PASS
- 변경(tracked): `backend/src/Wcs.PlcGateway/PlcGateway.cs`, `backend/src/Wcs.Sim3ds/SimServer.cs`,
  `backend/src/Wcs.Sim3ds/SimTransport.cs` + `tasks/sprint-contract.md`·`tasks/sprint-log.md`(tasks/ 허용).
- 신규(untracked): `Wcs.PlcGateway/Modbus/SharedModbusConnection.cs`, `Wcs.PlcGateway/ModbusBus.cs`,
  `Wcs.Sim3ds/SimSlave.cs`, `Wcs.Tests/MultiSorterSameBusTests.cs`.
- **Wcs.Core·Wcs.Data·마이그레이션·frontend/·Wcs.Api(Program.cs SorterRegistryFactory/SorterConfig/appsettings)
  변경 0** (git diff --name-only 확인). IModbusMaster.cs·ModbusMasterFactory.cs·HandshakeOrchestrator.cs·
  Sim3ds Program.cs 무변경 — Generator가 B2 "가산적/시그니처 무파괴" 접근 채택. → 범위 위반 없음.
- 계약 sprint-contract.md diff(367/219)는 직전 S-UI-LAYOUT-FIX 계약을 본 Phase 1 계약으로 전체 교체한 것 —
  C1~C10/시나리오 개찬 없음(내가 읽은 계약과 일치). 조작 아님.

## 빌드 (C8 전반) — PASS
- `dotnet build backend/Wcs.sln -c Release`: **오류 0**, 경고 12개(전부 선재 NU1903 SQLite CVE). 신규 코드
  경고 0. develop 베이스라인 빌드도 동일 경고 12개 → 신규 경고 없음.

## 전체 테스트 (C8) — PASS (flake 관찰 기록)
- feature 전체 스위트: **366/366 통과, 실패 0, 18~20s, EXIT=0 — 6회 연속 클린**(정상 조용한 머신 조건).
- 통합/취약 클래스 필터(`PlcGatewayIntegration|ScenarioTests|HandshakeResidue|MultiSorterSameBus`, 27개):
  **5/5 반복 전부 27/27 GREEN(각 6s), 카운트 불변** — S5RSeqMismatch·S9·IT3a·IT4b·teardown 채널경쟁 회귀 0.
- 신규 MultiSorterSameBus 6개 격리: **6/6 GREEN(6s)**.
- develop 베이스라인(stash -u로 8c25421 워킹트리): **360/360 GREEN, 17~19s, EXIT=0 — 2/2 클린.**
  (feature 366 = develop 360 + 신규 6. 카운트 정합.)

### ⚠️ Flake 관찰 (blocking 아님 — 추적 권고)
- 초기 2회 실행에서 전체 스위트가 **테스트는 전부 통과(16s) 후 testhost가 teardown에서 HANG**
  (`--blame-hang` 3분 비활성 abort, EXIT=1, `[WcsTeardownGuard] ... SocketException` 로그, 카운트 359로 절단).
- 귀속: 그 2회는 내가 **동시에 foreground `tasklist` 폴 루프(2s 간격)+Monitor(1s) 부하**를 준 실행(run#1)과
  그 직후 abort/덤프수집 잔여 프로세스가 경쟁하던 실행(run#2)이었다. **내 간섭을 제거한 뒤 6회 연속 클린**으로
  재현되지 않음. 증상(E2E 병렬부하 하 teardown SocketException)이 문서화된 **선재 flake 클래스**와 일치
  (메모리 e2e-parallel-load-surfaces-integration-flakes / testhost-teardown-channel-race / s9-flake).
- develop도 조용한 조건 2/2 클린 — 부하 하 develop 재현 실험은 미수행(선재 여부 100% 단정 불가). 그러나
  **feature 코드에 귀속되는 결정적 회귀는 아님**(격리·필터·정상조건 전체 GREEN, 버스 teardown은 Writer.TryComplete
  보유). → C8/C10 기능 기준 충족. **후속: 이 저빈도 E2E teardown flake는 백로그 추적 유지 권고.**

## 조건별 판정

- **C1 (멀티유닛 Sim ≥2 unitId·독립 뱅크/상태기계; 단일유닛 현행 동일) — PASS.**
  SimSlave가 `GetHoldingRegisters(UnitId)` 자기 뱅크만·`RegistersChanged`를 `e.UnitIdentifier`로 필터·
  독립 상태기계(_isSorting/_isMoving)·per-slave volatile 고장주입. 단일유닛 ctor는 `logPrefix=""`로 위임(현행
  타임라인 문자열 보존). 시나리오(a) 두 슬레이브 Online + 뱅크 독립(Unit(1) Ready=0 → Unit(2) 무영향) 검증.
  기존 Sim3dsRtu A1~A10·ScenarioTests 전체 GREEN.

- **C2 (마스터/포트 1개 공유, 둘 다 Online, 마스터 1 입증) — PASS.**
  버스당 `SharedTcpModbusConnection` 1개 + `BusSlaveMaster` 어댑터가 동일 `_conn` 래핑. 시나리오(a)는
  SharedTcpModbusConnection 1개 생성 + `Bus.Slaves.Count==2` + 둘 다 Online. (OS 소켓수 직접 단언은 없으나
  버스당 연결 1개가 구조적으로 보장 — 어댑터 Dispose=no-op로 형제 연결 보호.)

- **C3 (버스 폴 주기당 대기 1회, N=2가 1 PollIntervalMs 내 갱신, N×아님) — PASS (코드검사).**
  `ModbusBus.RunBusPollLoopAsync`: 주기당 `Task.Delay(PollIntervalMs)` **1회** 후 멤버 순회, `PollCycleAsync`에
  Delay 없음 → 슬레이브별 sleep 0. 구조적으로 N×PollIntervalMs 불가능. **GAP: 전용 타이밍 행위 단언은 테스트에
  없음**(구조 보장으로 충족; 후속에 타이밍 단언 추가 권고).

- **C4 (동시 핸드셰이크 2슬레이브 ≥20회, R_Seq==자기 C_Seq, 교차 0) — PASS.**
  시나리오(b) 20회 WhenAll 동시 트리거, 매회 `SentCSeq==ReceivedRSeq`(슬레이브별)·R_CellNo 각자값 단언.
  전체·격리 실행 전부 GREEN. 버스 락(공유 `_busLock`)이 프레임 교차 차단.

- **C5 (슬레이브 격리: B 실패 시 B만 상태전이, A Online+Success) — PASS.**
  (c1) InjectNoResponse→B RFlagTimeout·A Online+Success·B도 폴응답 유지. (c2) InjectUnresponsive
  (서버 ServerDeviceFailure=soft)→B만 OFFLINE·A Online+Success·B 복구. 코드: per-slave 상태 필드,
  `PollCycleAsync` catch에서 `if(!_isBusMember||isHardEx)`로 **soft 실패는 공유연결 미절단**(형제 보호).

- **C6 (절대규칙 #1: 인터리브 쓰기 D4 RMW 비트보존·C 교차오염 0·단일 큐 경유) — PASS.**
  (e) 두 슬레이브 인터리브 CellAssign → 각자 C값(11/101, 22/202)·C_Flag=1·**Ready 비트 보존** 단언.
  코드: 버스 단일 쓰기 채널(SingleReader) + 컨슈머가 unitId 라우팅 → `ProcessWriteAsync`/`RmwD4LockedAsync`가
  공유 버스락 단일 임계구역에서 read+write 원자수행. grep: Modbus 마스터 호출부는 PlcGateway(큐/락 내부)·
  SharedModbusConnection·마스터 구현·테스트뿐 — 핸들러/서비스/핸드셰이크 직접호출 0.

- **C7 (서로 다른 버스 병렬) — PASS.** (d) 포트 2개·독립 SharedTcpModbusConnection/ModbusBus 2개, 둘 다
  Online+Success, portA≠portB. 멀티포트 회귀 0.

- **C8 (빌드 클린 + 전체 GREEN, 취약 ≥5회 귀속) — PASS.** 위 참조(366/366 6회 클린, 필터 5/5×27, develop 2/2).
  ⚠️ 초기 2회 teardown hang은 평가환경 간섭+선재 flake로 귀속(위 flake 관찰).

- **C9 (스코프) — PASS.** 위 스코프 절 참조.

- **C10 (하드코딩 0, teardown 결정성, 고아 0) — PASS.** 신규 타이밍/포트/유닛 전부 옵션·인자 주입
  (PlcGatewayOptions·SimOpt·unitIds). `ModbusBus.StopAsync`가 `_writeCh.Writer.TryComplete()` 선호출 →
  parked ReadAllAsync 결정 종료. 실행 후 testhost/Sim/Api 고아 **0** 확인. (flake성 teardown hang은 위 관찰.)

## 차원 판정
- **차원 1 (functional-and-regression) — PASS.** 시나리오(a)(d)(e)+C1~C3·C7~C10 충족, 전체 0회귀(정상조건 6/6).
- **차원 2 (concurrency-and-frame-integrity) — PASS (코드 직접검사).**
  · 버스 락 = **동일 공유 `_busLock` 인스턴스**가 poll-read(`PollCycleAsync` L315)·write(`ProcessWriteAsync`
    L551)·D4 RMW(`RmwD4LockedAsync`, ProcessWrite 임계구역 내)·재연결(`TryReconnect` L437)에 모두 적용
    (`ConfigureForBus`가 자기 락 폐기→공유 락 대체). **락 밖 프레임 연산 없음.**
  · per-slave OFFLINE 독립: 상태 전부 인스턴스 필드(_failures/_online/_latest/_prevRFlag/_prevSnap/
    _startupReconciled), 공유 실패 카운터 없음; soft 실패는 공유연결 미절단; catch는 슬레이브별 scope.
  · D4 RMW 원자성/비트보존: read+write 동일 임계구역(테스트 e로 Ready 보존·C 교차오염 0 입증).
  · 절대규칙 #1: 모든 쓰기 버스 단일 큐 컨슈머 경유(EnqueueAsync 버스 라우팅), 직접 Modbus 우회 0.
  · 같은-슬레이브 동시 핸드셰이크 out-of-scope(OQ6) 준수; 시나리오(b)는 서로 다른 두 슬레이브.
  (참고 switch default: 최초 리뷰 시 default 없음 관찰 → I1에서 log-only default 추가로 해소, 아래 M2 참조.)

---

## I1 RE-VERIFY (2026-07-16, Evaluator — I1 델타만 재검증, C1~C10 스윕은 유지)
Generator가 I1(공유 버스 슬레이브 read-timeout 격리) 수정 적용. HEAD 8c25421 위 미커밋. 스코프 불변
(PlcGateway.cs+SimServer.cs+SimTransport.cs+신규 4파일, tasks/ 외 코드 변경 없음). 세 주장 모두 실제 코드로 확인:

- **M1 (예외 분류·재연결 게이트) — CONFIRM 정확.** `PollCycleAsync` catch(PlcGateway.cs L392~448):
  `isConnLevel = Socket/IOException(+inner)`; `isTimeout = ex is TimeoutException`;
  `isHardEx = isConnLevel || (!_isBusMember && isTimeout)`; 재연결 게이트 `if (!_isBusMember || isHardEx)`.
  · **버스 모드 + TimeoutException**: isHardEx = false||(false&&true)=false → 게이트 false → **공유 연결 Disconnect
    안 함**(anti-churn). B는 `_failures >= OfflineAfterFailures`(3회 누적)로 OFFLINE 전이(soft 경로). ✓
  · **버스 모드 + Socket/IOException**: isConnLevel=true → isHardEx=true → 게이트 true → 공유 연결 drop+reopen(정당). ✓
  · **단독 모드 + TimeoutException**: isHardEx = false||(true&&true)=true → 즉시 OFFLINE + 재연결. **현행 보존(불변).** ✓
  (게이트 `!_isBusMember||isHardEx`는 버스 모드에서 `isConnLevel`로 환원 — 논리 반전/누락 없음.)

- **M2 (ProcessWriteAsync default) — CONFIRM 무-throw.** L623~627 default는 `_log.LogError(...)` + `break`만.
  throw 없음 → HandleWriteAsync가 잡아 잘못 OFFLINE 시키는 경로 없음(주석에 사유 명시). ✓

- **신규 테스트 C3_SlaveReadTimeout_OnlyBOffline_SharedConnNotChurned_AOnlineAndRecovers — CONFIRM 강함.**
  (a) `TimeoutInjectingConnection` 데코레이터가 B(unit2) read/write에 **실 `TimeoutException` throw**(soft Modbus 예외 아님, L400-401). ✓
  (b) B OFFLINE 단언(L346-347). ✓  (c) A Online 유지 + 핸드셰이크 Success·`SentCSeq==ReceivedRSeq`(L350-354). ✓
  (d) **anti-churn 핵심 단언**: baseline `DisconnectCalls==0` → 타임아웃 주입 후 ~13폴 경과(PollForDuration 400ms)
      → `Assert.Equal(discBaseline, conn.DisconnectCalls)`(L359) = 계측된 부작용에 대한 hard equality(버그 시 매 폴
      Disconnect로 급증했을 값이 0 유지). 비-tautological. ✓
  (e) 타임아웃 해제 후 B가 **재연결 없이**(Disconnect 여전히 0) Online 복구(L362-365). ✓

### 재실행 raw (클린 머신·간섭 없음)
- 빌드 Release: 오류 0, 경고 10(전부 선재 NU1903). 
- 전체 스위트: **367/367 GREEN, 19s, EXIT=0, 클린 종료(hang 없음)** — 367 = 366 + 신규 C3.
- 취약필터(PlcGatewayIntegration|ScenarioTests|HandshakeResidue|MultiSorterSameBus, **28개** = 27+C3):
  **13회 중 12회 28/28 GREEN(각 7s)**. 1회(최초 빌드 직후 첫 실행)만 27/28(1 실패) — 이후 12회 재현 안 됨.
- 신규 C3 격리 **12/12 GREEN(각 ~800ms)** → 그 1 실패는 **C3 아님**. 실패 테스트명 재현 실패로 미포착이나,
  최초-실행/JIT 콜드스타트에서만 1회 발현·이후 12회 클린 → **문서화된 선재 저빈도 flake 클래스(S9/IT4b/teardown,
  s9-flake·e2e-parallel-load)에 귀속**. I1 델타(코드/신규 C3)에 귀속되는 결정적 회귀 아님.
- 단독 `PlcGatewayIntegration` 격리 **10/10 GREEN(2s)** — 단독 경로 불변 입증.
- 실행 후 testhost/Sim/Api 고아 **0**. 스코프 불변(코드 = PlcGateway+Sim3ds+Tests만).

**I1 재검증 결론: 수정 로직 정확(anti-churn·단독 불변), 신규 C3 anti-churn 단언 강함, 전체 스위트 GREEN.
→ 스프린트 APPROVED 유지.** (관찰: C3 외 필터 1/13 first-run 실패는 선재 flake로 귀속; C3의 타이밍은
12/12 격리로 안정 확인. 저빈도 E2E flake 백로그 추적 권고는 이전대로 유효.)

---

APPROVED

## Code Review Pass (Step 4.5 — 독립 리뷰, 2026-07-16)

**최종: Ready to merge = Yes (Phase 1). Critical 0 · Important 1(I1, 교정+재검증 완료) · Minor 6.**

강점(적대적 검사): 버스당 단일 `_busLock`이 poll-read·write·D4 RMW·재연결 전부에 적용(락 밖 프레임 연산 0),
가산적 seam(IModbusMaster/Factory/HandshakeOrchestrator 무파괴), D4 RMW 원자성 구조 보장, per-slave 상태
인스턴스 필드 격리, teardown 순서 정합(TryComplete→Cancel→join→Disconnect), 단일슬레이브 하위호환(추출·비재작성).

- **[교정완료·재검증] I1** — 버스 모드에서 `TimeoutException`을 hard로 분류해 죽은 슬레이브 1개가 매 주기 공유
  연결을 drop+reopen하며 형제 스톨. 실 RTU 죽은 슬레이브 신호=read 타임아웃인데 TCP 테스트는 soft 예외라 못 잡음.
  fix: `isConnLevel`(Socket/IO)만 공유연결 drop, 버스 timeout=soft(슬레이브만 OFFLINE), standalone 불변.
  신규 C3 테스트가 DisconnectCalls==0 hard-equality로 anti-churn 단언. 367/367 GREEN.
- **[교정완료] M2** — ProcessWriteAsync switch에 log-only `default:`(throw 아님 — throw면 HandleWriteAsync가 잘못 OFFLINE).

### Minor (비블로킹 — 백로그/Phase 2)
- **M3**: `EnqueueReconcileClearR` 버스 경로가 teardown 레이스 시 ChannelClosedException 미관측 가능(`_ = WriteAsync`).
  → ModbusBus에 `TryEnqueue` 노출 권고(저확률).
- **M4**: 버스 폴 루프가 멤버 `PollCycleAsync` 비-OCE 예외 미가드 — 한 멤버 예외가 전 슬레이브 폴 중단 가능.
  → 멤버별 `catch(Exception){log;continue;}` 하드닝 권고.
- **M5**(Phase 2): `Connect()`가 버스 락 안에서 동기 블로킹 — 행 TCP connect(OS ~21s, connect-timeout 없음)가
  버스 전체 스톨. 현장 RTU 결선 시 connect-timeout 필요.
- **M6**: C3(주기당 1회 대기)는 구조 보장이나 전용 타이밍 단언 테스트 부재 → 후속 추가 권고.
- **M7**(Phase 2): SharedModbusConnection의 readTimeout/baud/parity가 C# 기본인자(既 마스터와 동일 패턴,
  테스트서 명시 오버라이드) — Phase 2 DI 결선 시 PlcGatewayOptions/config에서 주입 필요.
- **[백로그 추적]** 저빈도 E2E testhost teardown/parallel-load flake(S9/IT4b/teardown) — I1과 무관·결정적 회귀 아님.
  13회 중 1회 first-run/JIT 재현(격리 12/12 GREEN). develop 부하하 재현실험 미수행이라 선재 100% 단정 불가 → 추적.

---

# EVALUATION — S-MULTISORTER-SHARED-BUS (Phase 2) · 2026-07-16 (Evaluator, single)

브랜치 `feat/multisorter-shared-bus-p2` · HEAD 36a47bc(= PR#68 Phase 1 병합) · Phase 2 변경은 전부 **working tree**(미커밋).
빌드/테스트는 ground truth(코드 직독 + fresh 빌드·테스트)로 검증 — Generator 요약 불신.

## 판정: **FAIL** (블로킹 = C4 / ★N=1 설계 검증 = REGRESSION-UNVERIFIED)

전체 완료조건 중 C1·C2·C3(부분)·C5·C6·C7·C8·C9·C10 PASS, **C4 FAIL**. 두 Evaluation Dimension(배선/회귀, 수명/격리)
코드검사는 통과했으나, ★CRITICAL DESIGN SCRUTINY(N=1-through-bus soft-timeout)를 **latent regression(미검증)**으로 판정.

### 빌드·테스트 (fresh evidence)
- `dotnet build backend/Wcs.sln -c Release` → **오류 0**. 경고 10개 전부 **선재 NU1903**(SQLitePCLRaw 2.1.10 취약성 advisory,
  Phase 2 무관). NEW 경고 0.
- `dotnet test backend/Wcs.sln -c Release` → **372/372 통과, 0 실패, 0 스킵**(367 + 신규 E2EGroupJ 5). raw 원문 보존.
- 격리·직렬 ≥5회 재실행(flake 귀속): E2EGroupJ **5/5×5회**, MultiSorterSameBusTests **7/7×5회**,
  PlcGatewayIntegrationTests **10/10×5회**, ScenarioTests **4/4×5회**, MonitorHubTests **5/5×5회**. **flake 0**.
- 종료 후 testhost/Wcs.Sim3ds/Wcs.Api/vstest 고아 **0**(잔존 dotnet은 MSBuild/Roslyn 빌드서버 노드 — 보존 대상).

### 완료조건별 (raw 근거)
- **C1 (두 same-bus 소터 엔드투엔드·연결 1개) PASS** — E2EGroupJ `A_SharedBus_TwoSorters_EndToEnd_OneConnection`:
  `Assert.Single(factory.Buses)`(물리 버스 1개=SharedModbusConnection 1개) + `bus.MemberCount==2` + UnitA·UnitB 포함 +
  `registry.AllBundles.Count==2`. 두 소터 Online, IF-05 OK×2, SorterCommands COMPLETED≥2, cell.DestinationId destA≠destB(교차 0),
  RCellNo==CellNo(R_Seq==C_Seq), SignalR relay가 chute30·31 델타 방출. 단언 실질적(비-tautological).
- **C2 (다른 버스 키 병렬) PASS** — E2EGroupJ `D_MultiPort_DifferentBusKeys_ParallelIndependent`:
  `factory.Buses.Count==2`·각 `MemberCount==1`·포트 상이·둘 다 Online·COMPLETED≥2. 멀티 포트 회귀 0.
- **C3 (fail-loud) 부분 PASS** — 시리얼 파라미터 불일치(`C1_...SerialParamMismatch_FailsLoud`: "시리얼 파라미터"+"fail-loud"
  단언)·중복 UnitId(`C2_...DuplicateUnitId_FailsLoud`: "UnitId"+"중복" 단언) 기동 예외로 거부 확인. SORTER_3D-without-Sorters[]
  fail-loud 경로 보존(Program.cs L494-504). **미충족: OQ9-i PollIntervalMs 불일치 fail-loud는 구현(Program.cs
  ValidateBusGroupConsistency L717-724)됐으나 테스트 부재** — E2E 인프라에 induceSerialMismatch/induceDuplicateUnitId만 있고
  inducePollIntervalMismatch 없음. Minor 검증 갭(계약 Verification이 각 fail-loud 경로 테스트 확인을 요구).
- **★C4 (N=1 하위호환) FAIL** — appsettings 바이트 동일(diff 0, 기본=N=1·RTU·COM1·UnitId=1) PASS, 단일소터 전제 기존
  테스트 GREEN PASS. **그러나 C4 마지막 절 "버스 멤버 1개 경로가 현행 폴/재연결/OFFLINE/arming/teardown 의미 보존"
  미충족.** 아래 설계 검증 참조.
- **C5 (수명/격리) PASS** — 버스 단위 teardown: StopAsync가 `_buses` 순회 `bus.StopAsync()` 1회(멤버별 아님).
  ModbusBus.StopAsync 순서 `Writer.TryComplete → cancel → poll/write await → member.StopAsync → _conn.Disconnect`(정합).
  버스 멤버 번들 `writeQueue: null`(Program.cs L620) → StopPollingAsync의 TryComplete no-op. **per-멤버 drop 경로 봉인**:
  StartPollingAsync/StopPollingAsync는 dead(PlcPollingHostedAdapter 미등록·registry가 bus.Start/StopAsync만 호출).
  BusSlaveMaster.Dispose no-op(공유 연결 미절단). 슬레이브별 OFFLINE 독립: E2EGroupJ `B`(N=2, InjectUnresponsive→B만
  OFFLINE, A Online·핸드셰이크 Success, B 복구) GREEN.
- **C6 (절대규칙 #1) PASS** — 멤버 EnqueueAsync→`_bus.EnqueueAsync(unitId,...)`→버스 `_writeCh`(SingleReader) 단일 컨슈머.
  D4 RMW·TgtFloor/CellAssign fresh-read 가드는 멤버 ProcessWriteAsync에 그대로(_clientLock=버스 공유 락 임계구역). Modbus 직접 호출 0.
- **C7 (회귀 0) PASS** — 위 빌드·테스트·≥5회 근거.
- **C8 (스코프) PASS** — working diff = Wcs.Api/Program.cs, Wcs.PlcGateway/ModbusBus.cs(가산 오버로드), 신규
  SharedModbusConnectionFactory.cs, Wcs.Tests(E2EInfrastructure.cs + 신규 E2EGroupJ), docs/SPEC.md + tasks/. **Wcs.Core/Wcs.Data/
  마이그레이션/frontend/appsettings 변경 0**(appsettings 바이트 동일 확인).
- **C9 (SPEC §7-A) PASS** — L110 토폴로지 문장이 공유 버스 가능 + fail-loud 목록 + N=1 동치로 갱신됨(diff 확인).
- **C10 (하드코딩 0) PASS** — 버스 키/정합검사/폴 cadence 전부 SorterConfig·Timing·PlcGatewayOptions 주입. 신규 매직 타이밍 상수 0.

### Dimension 1 (wiring-and-regression) — PASS
버스 키 그룹핑(BusKeyOf: RTU="RTU|"+PortName대문자 / TCP="TCP|"+Host:Port, 전송을 키에 포함해 교차전송 충돌 방지) 정합.
그룹당 SharedModbusConnectionFactory.Create→ISharedModbusConnection 1개 + ModbusBus 1개 + 멤버 AddSlave(per-member opt) +
bus.StartAsync 1회. 예외 시 이미 만든 버스 DisposeAsync로 포트 누수 0. 멀티 포트 회귀·fail-loud(2/3)·N=1 바이트 동일 입증.

### Dimension 2 (lifecycle-and-isolation) — PASS
공유 연결 1회 Open(BusSlaveMaster.Connect→_conn.Connect 멱등, 첫 멤버 EnsureConnected가 Open)·1회 Dispose(ModbusBus.DisposeAsync→
_conn.Dispose). teardown 순서 정합·mid-transaction disconnect 불가(poll/write 태스크 join 후 disconnect). AddSlave 오버로드는
가산적: 기존 `AddSlave(unitId,log)`→`AddSlave(unitId,_opt,log)` 위임(동작 무변경). 폴 cadence·_busLock은 버스 단위 유지, memberOpt는
멤버 PlcPollingService의 핸드셰이크 Timing/OfflineAfterFailures/WriteTimeoutMs만 오버라이드. 단일슬레이브 락/상태 의미 불변.

## ★ CRITICAL DESIGN SCRUTINY — N=1-through-bus soft-timeout = **REGRESSION (UNVERIFIED)** → C4 FAIL

**사실:**
1. 운영 기본 appsettings = **N=1·Transport=Rtu·COM1·UnitId=1**(바이트 동일) — 즉 현 현장 배포가 단일 RTU 소터.
2. Phase 2는 **모든** 소터(N=1 포함)를 ModbusBus 멤버(`_isBusMember=true`)로 라우팅. 운영에 standalone 경로 없음.
3. PlcGateway.cs PollCycleAsync 분류(L392-407): `isHardEx = isConnLevel || (!_isBusMember && isTimeout)`,
   재연결 게이트 `if (!_isBusMember || isHardEx)`. 버스 멤버에선 사실상 `isConnLevel`만 hard:
   - SocketException/IOException(내부 포함) → HARD → 재연결(N=1 버스에서도 **보존**).
   - **TimeoutException → SOFT → 재연결 안 함**(standalone은 HARD였음 — **변경**).
   - 기타 비-conn 예외(예: ModbusException CRC/protocol) → SOFT → 재연결 안 함(standalone은 `!_isBusMember`=true라 HARD였음 — **변경**).
4. 기존 재연결/OFFLINE 테스트(IT4/IT4b)는 Sim 서버 종료(=소켓 refused/reset=SocketException)를 **standalone**
   `new PlcPollingService(...)` 경로로만 주입. bare-TimeoutException 경로·버스 경로 **미커버**.
5. 유일한 버스 read-timeout 테스트 MultiSorterSameBus `C3`는 **N=2**(건강한 형제 A가 공유 소켓을 살아있게 유지) —
   재연결 없는 복구를 입증하나 형제 有 전제. **N=1(형제 無) 버스의 timeout 복구를 검증하는 테스트는 0.**

**판정 근거:**
- C4 마지막 절이 요구하는 "버스 멤버 1개 경로가 현행 **재연결** 의미 보존"이 객관적으로 **미충족**(timeout·비-conn 예외에서
  HARD→SOFT로 변경). 기존 단일소터 GREEN 테스트는 standalone 경로를 검증할 뿐, 운영이 실제로 타는 N=1 버스 경로를 검증하지 않음.
- 실 conn 사멸(소켓 close/reset·IO·시리얼 포트 장치 제거→IOException/SocketException)은 N=1 버스에서도 HARD로 재연결 **보존**되므로
  가장 흔한 "reopen 필요" 실패는 처리됨. 그러나 **reopen으로만 복구되는데 TimeoutException/비-conn 예외로 표면화되는** 조건
  (예: RTU 시리얼 프레이밍 desync — 부분프레임 timeout 또는 CRC ModbusException; standalone은 Disconnect로 버퍼 클리어해 재동기)은
  N=1 버스에서 재연결하지 않아 **영구 OFFLINE 잔류 위험**.
- "intended+safe"로 인증 불가: 안전성은 (a) FluentModbus 5.3.2가 실 연결사멸을 항상 Socket/IO로 던지고 bare TimeoutException은
  "전송 생존·응답부재"에만 쓴다는 **미검증 가정**과 (b) RTU desync 자연복구 가정에 의존. **입증 책임(테스트) 미충족** + 대상이
  **1차 운영 구성(N=1 RTU)**이라 가설적 엣지 아님.

**권고(택1로 해소):**
- (A·선호) **solo 멤버 버스**(멤버 1개)는 TimeoutException/비-conn 예외를 HARD로 취급(형제 없음 → soft 근거 부재) — standalone
  재연결 의미 복원. ModbusBus가 멤버에 solo 여부 전달 or 레지스터리가 N=1 그룹은 standalone(비-버스) 경로로 결선.
- (B) N=1-on-bus read-timeout(및 비-conn 예외) 복구 동치를 입증하는 테스트 추가(재연결 없이 복구됨 or conn사멸은 여전히 재연결).

## Minor (비블로킹)
- **OQ9-i 검증 갭**: PollIntervalMs 불일치 fail-loud 구현됐으나 테스트 부재(위 C3).
- Phase 1 코드리뷰 M4(버스 폴 루프 멤버 비-OCE 예외 미가드)·M5(락 안 동기 Connect 블로킹)는 Phase 2에서 미해소 — 후속 유지.

**요약: 배선·격리·회귀·fail-loud(2/3)·스코프·문서·규칙#1 모두 견고(372/372, 격리≥5회 flake 0). 그러나 Phase 2가 N=1
단일소터(운영 기본)를 버스 멤버로 편입시켜 read-timeout/비-conn 예외의 재연결 의미를 바꿨고 그 경로에 테스트가 0 →
C4 미충족·latent regression. → FAIL.**

---

# C4 RE-VERIFY — S-MULTISORTER-SHARED-BUS (Phase 2) · 2026-07-16 (Evaluator, focused)

Generator가 C4/N=1 fix 적용. 이전 판정에서 C4 외 전부 PASS였으므로 **C4 델타만** 실 코드 대조 재검증(전 스윕 재수행 아님).

## 판정: **APPROVED** — C4 이제 충족, 회귀 0.

### 1. 예외 분류 4 regime (PlcGateway.cs L406-464, 코드 직독·불리언 환원)
`ownsPortExclusively = !_isBusMember || _soloBusReconnect` · `isTimeout = ex is TimeoutException` ·
`isHardEx = isConnLevel || (isTimeout && ownsPortExclusively)` · 재연결 게이트 `if (ownsPortExclusively || isHardEx)`.
- **standalone**(`!_isBusMember`→owns=T): timeout→isHard=T→재연결. **pre-Phase-2 불변**. ✓
- **solo bus**(1멤버·`_soloBusReconnect`=T→owns=T): timeout→isHard=T→재연결. **C4 fix 복원**(=standalone). ✓
- **multi-member**(≥2·`_isBusMember && !_soloBusReconnect`→owns=F): timeout→isHard=F→SOFT, 게이트 `F||F`→**공유연결 미절단**.
  **Phase 1 I1 불변**(형제 보호). ✓
- **any + Socket/IOException**(isConnLevel=T→isHard=T): 재연결. 전 모드 보존. ✓
불리언이 정확히 이 4 regime로 환원됨 — 반전 없음(C4·형제보호 어느 쪽도 재파손 안 됨).

### 2. solo 플래그 설정 (ModbusBus.cs L114-130)
`StartAsync`: `bool solo = _members.Count == 1; foreach(m) m.SetSoloBusReconnect(solo);` — **AddSlave 완료 후 · 폴 루프
기동 직전**에 멤버 수 확정. 이후 AddSlave는 `_started` 가드로 금지→런타임 플립 불가. **2멤버 버스는 solo=false**(둘 다 false). ✓

### 3. 신규 solo 복구 테스트 E2EGroupJ.E_SoloBus_ReadTimeout_HardReconnect_Recovers
- **실 레지스트리 경로**: 단일 소터(시드 chuteNo=30, sharedBusUnits/extras 없음)→`CreateClient()`로 SorterRegistryFactory가
  1-멤버(solo) 버스 생성. `Assert.Single(Buses)` + `MemberCount==1` 단언.
- **DI seam 주입**: `injectTimeoutConnection`→테스트가 `ISharedModbusConnectionFactory`(TimeoutInjectingConnectionFactory)를
  DI 등록→레지스트리가 seam으로 resolve→실 `SharedModbusConnectionFactory.Create(opt)` 출력을 timeout 데코레이터로 감쌈.
  **실 production 경로 그대로**(seam 미등록 시 DefaultSharedModbusConnectionFactory=정적 위임, 동작 동일).
- **비-tautological·RED-without-fix**: `SetTimeoutUnit(UnitA)`→`WaitUntil(DisconnectCalls > baseDisc)`+`Assert.True(>base)`.
  fix 없으면 solo timeout=SOFT→재연결 미발생→Disconnect 불변→WaitUntil 타임아웃·단언 실패. fix 有→HARD→reopen(Disconnect 증가).
  `SetTimeoutUnit(-1)`→Online 복구 단언. **결정적**(고정 sleep 0, 조건 폴링).
- **seam 기본 경로 무변경**: production은 `_sp.GetService<ISharedModbusConnectionFactory>() ?? new Default...()`—미등록→정적
  Create 위임=이전 직접 생성과 바이트 동일. production 동작 변경 0.

### 4. OQ9-i PollIntervalMs fail-loud (신규 C3_SharedBus_PollIntervalMismatch_FailsLoud)
`inducePollIntervalMismatch`→둘째 멤버 `Sorters:1:PollIntervalMs=77`→ValidateBusGroupConsistency 거부. 예외에 "PollIntervalMs"+
"fail-loud" 단언·PASS. 이전 검증 갭 해소. 시리얼 불일치(C1)·중복 UnitId(C2) fail-loud 여전히 테스트·PASS.

### 5. 두 regime 동시 pin
MultiSorter C3(N=2 soft·`DisconnectCalls==0` 무-churn) + E2EGroupJ E(solo HARD·Disconnect 증가) — 대칭 단언으로 soft/hard
양 regime 고정. 둘 다 GREEN.

### 빌드·테스트 (fresh)
- `dotnet build -c Release` → 오류 0, 경고 10(선재 NU1903). NEW 경고 0.
- `dotnet test backend/Wcs.sln -c Release` → **374/374 통과**(372 + 신규 2: E_SoloBus, C3_PollIntervalMismatch).
- 격리·직렬 ≥5회 count-invariant: E2EGroupJ **7/7×5**, MultiSorterSameBus **7/7×5**, PlcGatewayIntegration **10/10×5**,
  ScenarioTests **4/4×5**, MonitorHubTests **5/5×5**. flake 0.
- 스코프 불변: Wcs.Api(Program.cs) + Wcs.PlcGateway(ModbusBus.cs·PlcGateway.cs·SharedModbusConnectionFactory.cs) +
  Wcs.Tests(E2EInfrastructure.cs·E2EGroupJ) + docs/SPEC.md. **Core/Data/마이그레이션/frontend/appsettings 변경 0**(appsettings 바이트 동일).
- 종료 후 testhost/Sim/Api 고아 0.

**C4 이제 충족(1-멤버 버스 = standalone 재연결 의미 복원, 실 레지스트리 경로 테스트로 입증)·형제 보호(I1) 불변·회귀 0.
→ 전 완료조건 PASS.**

APPROVED

---

# CR-I1 RE-VERIFY — S-MULTISORTER-SHARED-BUS (Phase 2) · 2026-07-16 (Evaluator, focused delta)

Generator가 코드리뷰 CR-I1(a·b·c) + M2/M3/M4 적용. 이미 APPROVED된 스프린트의 이 델타만 실 코드 대조 재검증(전 스윕 아님).

## 판정: **여전히 APPROVED** — 검증 fail-loud 확장 정확·M2 teardown 안전·회귀 0.

### 1. CR-I1(a) ReadTimeoutMs/WriteTimeoutMs 정합 검사 (Program.cs L744-754)
`ValidateBusGroupConsistency`에 `if (cfg.ReadTimeoutMs != first.ReadTimeoutMs || cfg.WriteTimeoutMs != first.WriteTimeoutMs)`
추가 → LogCritical + throw("연결 타임아웃(ReadTimeoutMs/WriteTimeoutMs) 불일치…fail-loud"). serial/PollInterval과 동일 패턴.
호출 위치 L516(그룹 루프 build 단계) — **bus.StartAsync(L654) 이전**에 발화(다른 검사와 동일 타이밍). 근거: 공유 클라이언트의
Read/Write 타임아웃은 버스 단위 1개(그룹 대표값이 실효) → 멤버별 상이 시 조용히 대표가 이기는 대신 fail-loud로 표면화.

### 2. CR-I1(c) 신규 C4_SharedBus_ConnTimeoutMismatch_FailsLoud (E2EGroupJ)
`induceTimeoutMismatch`→인프라가 **둘째 멤버만** `Sorters:1:ReadTimeoutMs=2000`(멤버0=기본 1000, WriteTimeoutMs 전 슬롯 500 고정)
→ ReadTimeoutMs만 불일치. `Record.Exception(CreateClient)`가 non-null·"ReadTimeoutMs"+"fail-loud" 단언. **비-tautological**:
CR-I1 검사 없으면 대표가 조용히 이겨 기동 성공→ex null→Assert.NotNull 실패(RED-without-fix). C1(serial)·C2(dupUnitId)·C3(PollInterval)
fail-loud 여전히 PASS(E2EGroupJ 8/8).

### 3. CR-I1(b)+M3 주석 (SorterGatewayRegistry.cs L82-87, Program.cs L706-714)
- 검사 헬퍼 주석: 버스 단위(검사 대상)=serial/PollInterval/Read/WriteTimeout·rep-sourced / per-member(검사 안 함)=
  RFlagTimeoutMs/CFlagTimeoutMs/RFlagClearConfirmTimeoutMs/OfflineAfterFailures — 코드 실제와 일치.
- StopPollingAsync 주석: writeQueue=null은 **TryComplete 벡터만** 무력화하나 `_polling.StopAsync()→_master.Disconnect()`
  (BusSlaveMaster→공유 연결 Disconnect)가 형제를 끊으므로 버스 멤버는 절대 개별 teardown 금지 — 코드 실제(registry가
  bus.StopAsync만 호출)와 일치. **주석만 변경·동작 0**.

### 4. M2 (teardown-critical — 정밀 검사) — 안전
- `_buses = buses`가 **StartAsync 루프 이전**(Program.cs L651, 루프 L654)에 게시됨. 확인.
- (i) **미기동 버스 StopAsync 안전·멱등**: `Interlocked.Exchange(_stopped)` 가드(1회만). `_cts is not null`(미기동=null→cancel 스킵),
  `_pollTask/_writeTask` null→`.Where(t=>t is not null)`이 걸러 null await 없음(NRE 0). `_writeCh.Writer.TryComplete()`·`_conn.Disconnect()`
  try/catch·멱등. throw 0.
- (ii) **부분 기동 시 결정 종료**: StartAsync 루프는 construction try/catch 밖 → bus[1].StartAsync가 던지면 전파되나 `_buses`가
  이미 게시돼 host StopAsync가 bus[0](기동됨)을 `TryComplete→cancel→join→member.StopAsync→_conn.Disconnect` 순서로 결정 종료
  (폴 태스크·열린 포트 누수 0). 이전(루프 뒤 게시)이었다면 `_buses`=null→`if(_buses is null) return`으로 bus[0] 누수 — 이 reorder가 그 창을 봉인.
- (iii) **이중 dispose 없음**: StopAsync 멱등(_stopped), DisposeAsync 내부 StopAsync 재진입은 조기 return. 정상 종료는 bus.StopAsync만
  (DisposeAsync 아님), construction 실패만 DisposeAsync. member.StopAsync/DisposeAsync도 각자 멱등. Disconnect/Dispose는 try/catch·멱등.
- reorder가 teardown 레이스/StopAsync-before-StartAsync 해저드 도입 안 함. **실측: 전 스위트·격리 반복 모두 hang/orphan 0**.

### 5. M4 (SharedModbusConnectionFactory.Create) — 확인
Transport switch가 `(opt.Transport ?? "").Trim().ToUpperInvariant()`로 정규화 — BusKeyOf(`(cfg.Transport ?? "").Trim().ToUpperInvariant()`)와
일치(" Tcp " 같은 값도 그룹핑-일관 경로). 알 수 없는 값은 여전히 fail-loud.

### 빌드·테스트 (fresh)
- `dotnet build -c Release` → 오류 0, 경고 10(선재 NU1903). NEW 경고 0.
- `dotnet test backend/Wcs.sln -c Release` → **375/375 통과**(374 + 신규 1: C4_ConnTimeoutMismatch), 22s, clean exit(hang 0).
- 격리·직렬 ≥5회 count-invariant, clean exit: E2EGroupJ **8/8×5**, MultiSorterSameBus **7/7×5**, PlcGatewayIntegration **10/10×5**,
  ScenarioTests **4/4×5**, MonitorHubTests **5/5×5**. flake 0·teardown hang 0·고아(testhost/Sim/Api) 0.
- 스코프 불변: Wcs.Api(Program.cs·SorterGatewayRegistry.cs) + Wcs.PlcGateway(ModbusBus.cs·PlcGateway.cs·SharedModbusConnectionFactory.cs)
  + Wcs.Tests(E2EInfrastructure.cs·E2EGroupJ) + docs/SPEC.md. **Core/Data/마이그레이션/frontend/appsettings/Sim3ds 변경 0**.

**CR-I1(a/b/c)·M2·M3·M4 모두 코드 실제와 일치·회귀 0·teardown 안전. 스프린트 APPROVED 유지.**

## Code Review Pass (Step 4.5 — 독립 리뷰, 2026-07-16)

**최종: Ready to merge = Yes. Critical 0 · Important 1(CR-I1, 교정+재검증) · Minor 4(M2/M3/M4 교정, M5 백로그).**

강점(적대적 검사): C4 진리표 정합(3모드×3예외, multi가 non-conn 예외도 soft로 공유연결 보존), solo 플래그 메모리
안전(Task.Run happens-before·write-once-before-start), fail-loud가 버스 기동 전 발화(자원 누수 0·생성자 Open 안 함),
DI seam 운영 무해(default 위임 verbatim), 버스 레벨 teardown, M7 사실상 해소(SorterConfig에서 시리얼 주입),
BusKeyOf가 transport 접두(RTU|/TCP|)로 충돌 방지.

- **[교정완료·재검증] CR-I1** — 버스 공유 Read/WriteTimeoutMs가 rep에서 오나 정합 검사 없고 주석은 "WriteTimeoutMs
  멤버별"이라 거짓. fix: ValidateBusGroupConsistency에 Read/WriteTimeoutMs 추가(불일치 fail-loud), 주석 정정
  (버스 레벨 = 시리얼·PollInterval·Read/WriteTimeout / per-member = RFlag*·CFlag*·RFlagClearConfirm*·OfflineAfterFailures),
  신규 C4_SharedBus_ConnTimeoutMismatch_FailsLoud(RED-without-fix). 375/375 GREEN.
- **[교정완료] M2** — 부분 기동 누수: `_buses` 게시를 StartAsync 루프 이전으로 → bus[1] 실패 시 bus[0] 결정 teardown.
- **[교정완료] M3** — writeQueue=null이 큐완료 벡터만 무력화하고 Disconnect 반벡터는 형제 절단함을 주석 정정
  (버스 멤버는 bundle.StopPollingAsync teardown 금지·버스 레벨만). 무동작.
- **[교정완료] M4** — SharedModbusConnectionFactory.Create Transport에 .Trim() 추가(BusKeyOf 정합).

### Minor (백로그)
- **M5**(Phase 3/RTU bring-up): RTU `Connect()`가 버스 락 안에서 동기 블로킹·connect-timeout 없음 → 행 포트
  Open이 버스 전체 스톨. TCP=테스트라 미노출이나 RTU 현장 결선 시 connect-timeout 필요. config 배선이 RTU
  생성자에 닿았으니 추적.
- **[이월]** PlcPollingHostedAdapter dead code 정리 / testhost teardown 저빈도 flake 추적.
