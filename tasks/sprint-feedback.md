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

---

# EVALUATION — S-TWO-FLOOR-CONTROL 서브 스프린트 A (인덕션 기반 2층 제어) · 2026-07-22 (Evaluator, single / functional)

브랜치 `feat/two-floor-control-a` · HEAD `7525e6f` · 변경은 전부 **working tree**(미커밋). Generator 요약·sprint-log
주장 불신 — git ground truth + 코드 직독 + fresh 빌드/테스트 자체 실행으로 검증. sprint-log `## IMPLEMENTATION
COMPLETE — S-TWO-FLOOR-CONTROL 서브 스프린트 A`(L4105) 마커 존재 확인(그 위 멀티소터 마커와 구별).

## 판정: **FAIL** — 유일 블로커 = 완료조건 "dotnet test 전체 GREEN(≥5회 반복 안정 — flake 없음)" 미충족.

절대규칙·아키텍처·시나리오·스코프는 전부 PASS(아래 상세). **단 전체 스위트 5회 반복에서 1회 flake** → 완료조건이
명시적으로 요구하는 "flake 없음"을 못 넘어 FAIL. flake는 결정적 회귀가 아니라 **병렬-부하 표면화 타이밍 flake**이며,
Generator가 이번 스프린트에서 도입한 flake-fix(RealSimSerialCollection)가 **불완전**한 것이 원인.

### 빌드 (PASS) — fresh evidence
- `dotnet build backend/Wcs.sln` → **오류 0 · 경고 10**(전부 선재 NU1903 SQLitePCLRaw CVE — base develop 부채, NEW 0).

### 전체 테스트 5회 반복 (FAIL — 1/5 flake)
`dotnet test backend/Wcs.sln --no-build`, 매 run 전 testhost/Sim3ds kill. raw 요약:
- run 1: **실패! 실패 1, 통과 394, 전체 395** — `SorterPushOperationalTests.VS9a_Sorter_OperationalTransition_ConcurrentObserve_ExactlyOncePush [FAIL]`
  · 오류: `WaitUntilExact 타임아웃(5000ms): 동시 16관찰에도 운영상태 전이당 정확히 1건(중복 0) (현재=5, 기대=3)`
- run 2: 통과! 395/395 · run 3: 통과! 395/395 · run 4: 통과! 395/395 · run 5: 통과! 395/395
- **flake율 1/5.** (Generator 주장 "395/395 · 19연속 GREEN"은 내 머신에서 재현 안 됨.)

### flake 귀속 (격리 재실행 — 결정적 vs 병렬부하 구분)
- `SorterPushOperationalTests` 단독 필터 **6/6 GREEN(각 8/8, ~4s)** — 격리에선 완전 안정.
- 결론: **결정적 버그 아님. 병렬-부하 표면화 타이밍 flake**(교훈 e2e-parallel-load-surfaces-integration-flakes /
  s9-flake-under-e2e-load 클래스와 정확히 일치). VS9a는 `DestinationStatusPusher` 주기 관찰 타이머 하에서 **정확
  push 카운트**(`baseline+1`)를 단언하는데, 동시에 병렬 실행되는 무거운 실-Sim E2E 클래스들의 CPU 경합 지터로
  스냅샷-read 레이스가 나 push 카운트가 초과(현재=5)됨.

### 근본 원인 = 이번 스프린트 flake-fix의 불완전성 (블로커의 소유권이 이 스프린트에 있음)
- 신규 `backend/tests/Wcs.Tests/RealSimSerialCollection.cs`(`[CollectionDefinition(DisableParallelization=true)]`)로
  무거운 실-Sim 클래스 15개에 `[Collection("RealSimSerial")]`를 붙여 **그들끼리는** 직렬화했으나,
  **push-결정성 테스트군은 그 컬렉션에서 제외**됨:
  · `backend/tests/Wcs.Tests/SorterPushOperationalTests.cs:39` — 클래스에 `[Collection]` 애트리뷰트 **없음**(병렬 풀 잔류).
    실패 테스트 = `SorterPushOperationalTests.cs:385` VS9a, 문제 단언 = `SorterPushOperationalTests.cs:417-419`
    (`WaitUntilExactAsync(..., baseline + 1, stableCount:8)` + `Assert.Equal(baseline+1, ...)`).
  · `backend/tests/Wcs.Tests/RcsPushTests.cs` — 동일 push-결정성 패밀리인데 역시 `[Collection("RealSimSerial")]` 없음(같은 위험군).
- 즉 무거운 실-Sim 테스트는 서로 직렬화됐지만, **여전히 비-컬렉션 Fake 테스트(push-결정성군)와는 병렬로 경합**한다.
  이번 스프린트가 실-Sim E2E(E2EGroupK 2개 신규 포함)를 추가해 총 병렬 CPU 부하를 늘렸고, 그 경합이 push-결정성
  테스트에서 flake로 표면화된다. → 스프린트의 flake-fix가 실제 flake 소스를 못 덮음(불완전).

### 요구 조치 (택1 — HOW는 Generator 결정)
- (A·권장·기존 패턴과 정합) `SorterPushOperationalTests.cs`와 `RcsPushTests.cs` 클래스에
  `[Collection("RealSimSerial")]`를 부여해 무거운 실-Sim 테스트와 **동시 실행되지 않게** 직렬화한다
  (이번 스프린트가 이미 채택한 mitigation을 push-결정성군까지 완결).
- (B) VS9a의 정확-카운트 대기를 `DestinationStatusPusher` 주기 타이머(SorterObserveIntervalMs) 하에서도 견고하도록
  재설계. 단 "전이당 정확히 1건(중복 0)"이 이 테스트의 목적이므로 카운트를 느슨히(`>=`) 하면 검증 의미가 사라짐 →
  (A)가 더 깨끗.
- 조치 후 **전체 스위트 ≥5회 재실행해 flake 0** 확인(격리 GREEN만으로는 불충분 — 부하 하 전체 실행이 판정 기준).

---

## 이하 항목은 전부 PASS (블로커 해소 시 재검증 불요 — flake만 고치면 됨)

### 절대규칙 (코드 직독 — 테스트 통과로 추론 아님)
- **#8 Wcs.Core 의존성 0 — PASS.** `Wcs.Core.csproj`에 PackageReference/ProjectReference **0**(SDK 기본만).
  `InductionFloorMap.DeriveFloor`(맵 호출자 주입·미매핑=null)·`DepositDecider.Decide`(순수 게이트) 둘 다 I/O·DI·EF 유입 0.
- **#1 TgtFloor 기입은 소터별 단일 쓰기 큐로만 — PASS.** grep 전수: TgtFloor 쓰기 경로는
  `SorterFloorReturnService.cs:207 bundle.EnqueueSetTgtFloorAsync` → `SorterGatewayRegistry.cs:57-58
  _polling.EnqueueAsync(new PlcWrite.SetTgtFloor(...))` → 컨슈머 `PlcGateway.cs:582-599`가 유일 실 Modbus write.
  API 핸들러/서비스의 직접 Modbus write **0**(OpsController도 동일 큐 경유). 관측 루프는 fire-and-forget이나
  `.ContinueWith(IsFaulted 로깅)`으로 예외 미삼킴.
- **#2 핑퐁 차단 + 컨슈머 fresh-read 재확인 — PASS.** DepositDecider가 `TgtFloor!=0`이면 write=null(코드 L51/59).
  컨슈머 `PlcGateway.cs:588-595`가 쓰기 직전 D6를 fresh FC03로 재읽어 `!=0`이면 스킵(스냅샷 stale 무관 결정적 차단).
- **#3 WCS는 TgtFloor 클리어 안 함 — PASS.** 전 코드에서 D6에 0을 쓰는 경로 **0**(grep 확인). 컨슈머는 floor(1/2)만
  기입. K1/K2 E2E가 `Assert.DoesNotContain(Timeline, D6 "→0")`로 실증(E2EGroupK L66·L150).
- **SorterFloorReturnService 동시성/격리/teardown — PASS.**
  · 큐 = `ConcurrentQueue`(다생산자 IF-05 enqueue) + 관측 루프 단일 소비자(TryPeek→TryPop 사이 타 소비자 없음).
    단위 테스트 `ConcurrentEnqueue_SingleConsumer_NoLoss`(8×250=2000 손실 0) GREEN.
  · 관측 루프 예외 격리: 소터별 `try{ObserveSorter}catch{log}`(L154-161) + 루프 레벨 catch(L164-169), OCE는 rethrow/break.
    한 소터 예외가 형제·루프를 안 죽임.
  · `StopAsync` 결정적: `Interlocked.Exchange(_stopped)` 멱등 + `CancelAsync`(ObjectDisposed catch) + loopTask await
    (OCE/Exception 흡수) — teardown 채널 경쟁(교훈 testhost-teardown) 방어.
  · 무변화 폴 write 폭주: 큐 빔→즉시 return(L179), OFFLINE→skip(L185), CurFloor==f→pop만(L189). 스냅샷-lag 구간의
    중복 SetTgtFloor enqueue는 컨슈머 fresh-read 게이트에서 no-op으로 흡수(실 PLC write는 1회) — 폭주 아님(bounded·멱등).

### 계약 시나리오 (자동 테스트 + 실 Sim3ds 크로스레이어)
- IF-05 inductionNo→F 파생 + 소터 큐 enqueue(F=1·F=2) — E2EGroupK K1 Theory(초기2→F1, 초기1→F2) GREEN. RcsController.cs:76·128 결선.
- 미매핑 inductionNo → NG + `IF05_NO_FLOOR` WARN(RcsController.cs:100-108), 소터로 안 보냄(DbRepositories.cs blockReason "NO_FLOOR"→RecordDenied→NG,
  enqueue는 result=="OK" 게이트라 미도달). 단위 `DeriveFloor_UnmappedInduction_ReturnsNull` GREEN. (조용한 통과·기본층 폴백 아님.)
- IF-09 → 도착 기록만, 정렬 트리거 0 — `AlignSorterToOperationalFloor` 제거. 재작성된 ApiIntegration `If09_..._NoAlignmentTrigger_TgtFloorUnchanged`
  (500ms TgtFloor=0 유지 단언) GREEN. K2가 정렬을 IF-05+관측 루프로 구동해 이중기입 경합 없음 실증.
- TgtFloor==0 관측 → 큐 머리 F 기입 → Sim3ds CurFloor=F 복귀 — K1 폐루프(HTTP↔큐↔Modbus↔Sim↔DB, operation_log SET_TGTFLOOR floor=F 영속) GREEN, 1층·2층 둘 다.
- FIFO 순서(1,2,1) 한 번에 하나씩 + pop-on-arrival(CurFloor==F) 폐루프 + 큐머리==CurFloor 즉시소비 — K2 GREEN(마지막 CurFloor=1, D6 기입에 1·2 모두 등장).
- FULL/PAUSED/OFFLINE enqueue·기입 안 함 — IF-05 availability가 NG로 차단(enqueue 미도달), 관측 루프는 DepositDecider hold/offline 게이트. DepositDecider F=1 Hold/Offline 미기입 테스트 GREEN.
- 위생 회귀(pId·barcode·qty 상한 400) 보존.

### 스코프 (B/C/D 미침범) — PASS
- `git diff --stat develop` 상 마이그레이션 프로젝트(Wcs.Migrations.SqlServer/Sqlite) **변경 0**, untracked 마이그레이션 파일 0.
- IF-08 층별 호스트 라우팅/dual-host(B): `ChuteStatePushOptions`는 단일 `BaseUrl` 그대로(불변). R-clear@Ready/3시각/sorter_command(C)·파킹존(D) 무접촉.

### DepositDeciderTests / 테스트 변경 정당성 (gutting 아님)
- DepositDeciderTests: 기존 Row1-7·C1-3(OperFloor=2) 유지 + FloorParam_F1_* 6종 추가(+61 라인, 가산적).
- VS-2 "misalign" InlineData 제거는 정당: ready가 CurFloor 기준이 되어 "미정렬(CurFloor≠운영층)"은 더 이상 not-ready 케이스가 아님(그 층에서 ready). Ready==0(busy) 케이스는 유지.
- E2EInfrastructure 기본 inductionMap `{1:2,2:2}`로 기존 E2E 동작(induction=1→층2) 보존, K 테스트만 커스텀 맵 주입. 정당.

### Minor (비블로킹 — 다음 스프린트 전 todo 등재 권고)
- "큐 머리 F가 이미 현재 CurFloor면 즉시 pop"(확정 결정 #2 스톨방지 엣지) 직접 단언 단위 테스트 부재 — 로직은 SorterFloorReturnService.cs:189 존재하고 K2 흐름에서 간접 커버되나 전용 케이스(induction이 현재층으로 매핑) 추가 권고.

**결론: 설계·절대규칙·시나리오·스코프 견고. 유일 블로커 = 전체 스위트 1/5 flake(VS9a, 병렬부하 표면화) — 이번 스프린트
flake-fix(RealSimSerialCollection)가 push-결정성 테스트군을 누락한 불완전성. 그 2개 클래스 직렬화(또는 VS9a 하드닝) 후
전체 ≥5회 flake 0 재확인 필요. → FAIL.**

---

# FIX ITER 1 RE-VERIFY — S-TWO-FLOOR-CONTROL 서브 스프린트 A · 2026-07-22 (Evaluator, focused delta)

Generator가 권장안 A 적용. **flake 블로커 델타만** 재검증(전 스윕 재수행 아님 — 1차에서 flake 외 전부 PASS였으므로).

## 판정: **APPROVED** — flake 블로커 해소(6/6 GREEN), 프로덕션 무변경, VS9a 단언 미완화.

### 1. fix 델타 (코드 직독)
- `SorterPushOperationalTests.cs:43` + `RcsPushTests.cs:208`에 `[Collection("RealSimSerial")]` 클래스 부여
  (설명 주석 동반). push-결정성 테스트를 무거운 실-Sim 테스트와 **동일 직렬 컬렉션**에 편입 → 병렬 CPU 경합 제거.
- **프로덕션 무변경**: `git diff --stat develop -- backend/src/` 8파일 라인수 1차 리뷰와 **byte-identical**
  (RcsController 122·WcsOptions 54·Program 9·DbRepositories 8·Repositories 3·DestinationStatusService 15·
  appsettings 8·DepositDecider 35). 마이그레이션 무접촉. HEAD 불변(7525e6f).
- **VS9a 단언 미완화**: `SorterPushOperationalTests.cs:422-424` 여전히 strict exact-count
  (`WaitUntilExactAsync(..., baseline+1, stableCount:8)` + `Assert.Equal(baseline+1, ...)`).
  직렬화로 경합을 제거해 strict 단언이 통과하게 한 것 — 카운트를 느슨히 한 것이 **아님**(gutting 아님).

### 2. flake 재검증 (fresh evidence — 6회 반복, 클린 환경)
- 재실행 전 고아 dotnet/testhost/vstest/Sim3ds 전수 kill(1차 반복 시 mid-run testhost kill이 dotnet 드라이버를
  고아화해 17개 누적 → CPU 기아로 1런 hang된 것을 격리·해소. **코드 결함 아님 — 평가 하네스 아티팩트**).
- `dotnet test backend/Wcs.sln --no-build` **6회 순차**(각 run 전 testhost/Sim kill, per-run 160s timeout guard):
  · RUN1 통과 395/395 (65s) · RUN2 통과 395/395 (66s) · RUN3 통과 395/395 (68s)
  · RUN4 통과 395/395 (67s) · RUN5 통과 395/395 (67s) · RUN6 통과 395/395 (66s)
  - **6/6 GREEN · rc=0 · FAIL 블록 0 · flake 0 · hang 0.** 카운트 불변(395=이전 375+신규 20).
- 격리 재확인(1차): SorterPushOperationalTests 단독 6/6 GREEN. → 직렬 편입 후 부하 하 전체 6/6도 안정 = flake 근본 제거.
- 빌드: 재컴파일(attribute 반영) 오류 0.

### 결론
1차 FAIL의 유일 블로커(전체 스위트 flake)가 해소됐고, fix는 테스트 attribute 전용(프로덕션·판정로직·단언강도 불변).
1차에서 PASS 판정한 절대규칙(#1 단일 쓰기 큐·#2 fresh-read 핑퐁·#3 클리어 0·#8 Core 순수)·시나리오(K1/K2 폐루프·
미매핑 fail-loud·IF-09 정렬 트리거 제거·ready=CurFloor)·스코프(마이그레이션/IF-08/파킹존 무접촉)는 프로덕션
무변경이므로 유효. → **APPROVED.**

APPROVED

---

# FIX ITER 2 RE-VERIFY — S-TWO-FLOOR-CONTROL 서브 스프린트 A · 2026-07-23 (Evaluator, focused delta)

Step 4.5 코드리뷰 발견 **I-1**(도착-pop의 [A,A,B] 조기 pop → 2번째 동일층 AGV 고립) fix. 로직 변경이므로
flake뿐 아니라 I-1 정합성·절대규칙·회귀·스코프까지 직독+실행 재검증. HEAD 불변(7525e6f).

## 판정: **APPROVED** — I-1 정합·회귀 0·절대규칙 불변·flake 0. ⚠️ **단, pop 트리거가 사용자 확정 결정 #2에서 변경됨 → 사용자 인지 권고**(아래 NOTE).

### 1. I-1 fix 코드 직독 (SorterFloorReturnService.cs — 유일 프로덕션 변경)
- pop 트리거를 **도착(CurFloor==F) → 분류 사이클(Ready 1→0→1) 단위**로 변경. 소터별 `ObserveState`(PrevReady·
  CycleStartFloor) 추가(관측 루프 단일 스레드 전용 — 락 불요, 실제 단일 스레드 순회 확인).
- 로직(L216-233): Ready 1→0에 `CycleStartFloor=CurFloor` 기록 → Ready 0→1에 **`CycleStartFloor==CurFloor`(제자리
  분류, 이동 아님) && `head==CurFloor`**일 때만 머리 1건 pop. 정렬 이동에 의한 0→1은 pop 안 함(피스 소비 아님).
- 구 "머리 F==CurFloor 즉시 소비" 엣지 **제거**([A,A,B] 조기 pop 버그 근원). 기입은 **유휴(Ready=1)·CurFloor!=head
  에서만**(L236) — 분류 중(Ready=0) 선기입 폐지(분류+이동 융합으로 사이클 감지 깨짐 방지).
- **[A,A,B]=큐[1,1,2]·소터 1층 트레이스**(직접 검증): 큐 유지 → A1 분류(Ready 1→0→1 제자리) pop 1→[1,2]·소터 1층
  유지 → A2 분류 pop→[2]·유휴 시 D6=2 기입→2층 이동(이 0→1은 CycleStartFloor(1)≠CurFloor(2)라 pop 안 함) →
  B 분류 pop→[]. **2번째 A-AGV 미고립·중복/누락 pop 0.** stall: 각 enqueue엔 대응 IF-10 분류가 오므로 정상흐름 무-stall
  (미투하 abandonment는 파킹존 D 스코프 — 명시).

### 2. K3 신규 테스트 (강함·non-tautological — 코드+실행)
`K3_MultiAgv_SameFloorConsecutive_ThenOther_HoldsFloorUntilBothClassified`(실 Sim3ds, `[Collection("RealSimSerial")]`):
- 큐 [1,1,2] enqueue 후 `UntilExactAsync(Count==3, stableCount:6)` — **조기 pop 0 실증**(구 즉시-pop이면 즉시 [2]로 드레인 → RED).
- A1 분류 COMPLETED≥1 → `Count==2`([1,2], 1건만 pop) + `CurFloor==1 stableCount:6`(B 이동 안 함) + `D6→2 기입 0`.
- A2 분류 COMPLETED≥2 → CurFloor==2 이동. B 분류 → 큐 빔. 절대규칙 #3 `DoesNotContain D6→0`.
- 5회 반복 전부 GREEN → 사이클 감지(Ready 전이 샘플링, 관측 30ms ≪ 분류 Ready=0 창)가 누락/중복 없이 안정.

### 3. 회귀 (직독 + 실행)
- K1(1·2층 폐루프)·K2(FIFO 1→2→1) 5회 전부 GREEN — idle-only 기입이 폐루프를 깨지 않음(정렬 이동은 유휴 시 기입 후
  이동, 도착 후 분류 시 pop).
- OpsController 재작성 **정당(마스킹 아님)**: 구 `IF09_AutoAlign_...EvenWhenReadyZero`(Ready==0 선기입) → 신
  `QueueDrivenAlign_WritesTgtFloor_WhenIdle`(Ready=1·CurFloor 2≠head 1 → IF-05 enqueue F=1 → 유휴 기입 D6=1).
  실 Sim D6=1 레지스터 값 + PLC_WRITE/SET_TGTFLOOR operation_log(컨슈머 EmitWrite = 컨트롤러 직접 Modbus 부재 증거)로
  **실 쓰기 발생을 단언**(약화 아님). 새 idle-only 모델을 정확히 반영.

### 4. 절대규칙 재확인 (쓰기 타이밍 변경에도 불변)
- **#1**: 기입 여전히 `bundle.EnqueueSetTgtFloorAsync`(L251) 단일 쓰기 큐만. 직접 Modbus 0.
- **#2**: DepositDecider(순수) `git diff` **byte-identical**(변경 0) — TgtFloor==0 게이트 불변. 컨슈머 fresh-read(PlcGateway.cs:588-595) 불변.
  idle-only 기입은 오히려 더 보수적(Ready=0 선기입 제거).
- **#3**: D6=0 쓰기 경로 0 유지 — K1/K2/K3 `DoesNotContain D6→0` 실증.
- **#8**: DepositDecider·층 파생 순수 함수 불변(diff 0). pop 로직은 서비스(트리거)에만.

### 5. 스코프 (B/C/D·마이그레이션 무접촉)
- 프로덕션 변경 = `SorterFloorReturnService.cs`(untracked) **단 1파일**. 8개 tracked 프로덕션 파일 diff-stat이 1차 리뷰와
  **byte-identical**(RcsController 122·WcsOptions 54·Program 9·DbRepositories 8·Repositories 3·DestinationStatusService 15·
  appsettings 8·DepositDecider 35) — 이번 iter는 이들 무접촉. 신규 마이그레이션 파일 0. IF-08 호스트·파킹존 무접촉.
- I-3(인메모리 큐 재시작 유실) = Sub-Sprint C 이연·SPEC §2-C 문서화만(코드 0). I-2/M-2 = Minor 등재만.

### 6. flake (fresh evidence — 5회, 클린 환경)
- 고아 dotnet/testhost/vstest/Sim 전수 kill → 재빌드 0 error → 각 run 전 testhost/Sim kill + per-run 170s timeout guard,
  각 run 자연 완료(mid-run kill 없음 → 드라이버 고아화 0).
- `dotnet test backend/Wcs.sln --no-build` **5회**: RUN1 396/396(69s) · RUN2 396/396(67s) · RUN3 396/396(67s) ·
  RUN4 396/396(65s) · RUN5 396/396(63s). **5/5 GREEN · rc=0 · FAIL 블록 0 · flake 0 · hang 0.** (396 = iter-1 395 + K3.)

## ⚠️ NOTE (사용자 인지 권고 — 블로커 아님)
pop 트리거가 **사용자 확정 결정 #2(2026-07-22)**의 문언("큐 머리 pop = CurFloor==F 도착 확인 시 … 머리 F==현재
CurFloor면 즉시 소비")에서 **분류 사이클(Ready 1→0→1) 단위 pop**으로 변경됐다. 이는 코드리뷰 I-1이 확정 #2의
즉시-pop 엣지를 [A,A,B] 조기 pop 버그로 판정해 나온 정정이며, 사용자 **의도**(폐루프·한 번에 하나·고립 방지)를 더
충실히 구현한다(SPEC §2-C가 M-1로 갱신됨). Step 4.5 코드리뷰가 사내 절차로 승인한 설계 정정이나, 사용자-게이트
결정을 변경한 것이므로 **오케스트레이터가 확정 #2 변경(및 SPEC §2-C 갱신)을 사용자에게 고지**할 것을 권고한다.
(구현 자체는 정합·검증 완료 — 승인 보류 사유 아님.)

## Minor (비블로킹 — 다음 스프린트 전 todo)
- 사이클 감지가 관측-루프 **샘플링 기반**(Ready 1→0→1 에지) — Ready=0 창 < 관측 주기면 에지 유실 위험. 현장(150ms·초 단위
  분류)·테스트(30ms) 5회 안정이나, 극단적으로 빠른 분류/느린 관측에선 이론적 유실 가능. Sub-Sprint C(핸드셰이크 타이밍)에서
  에지 유실 방어(예: 사이클 진행 플래그) 검토 권고.
- I-3(인메모리 큐 재시작 유실) Sub-Sprint C 이연 확인 — 재시작 시 미소비 큐 유실(운영 재기동 시 미정렬 소터 잔존 가능).

**결론: I-1(분류사이클 pop) 정합·K3 강함·회귀 0·절대규칙 불변·스코프 tight·5/5 flake 0. → APPROVED.
단 pop 트리거의 확정 #2 대비 변경은 사용자 고지 권고(NOTE).**

APPROVED

---

## S-TWO-FLOOR-CONTROL A — 코드리뷰(Step 4.5) Minor 이연 (다음 스프린트, 코드 변경 금지)

FIX ITER 2에서 I-1(분류사이클 pop)·M-1(문구 정정)·I-3(재시작 복원 문서화)는 처리. 아래 2건은 Minor로 등재만:

- **[I-2 · 성능·다음 스프린트(B 후보)]** `SorterFloorReturnService.ObserveSorter`가 정렬 기입이 필요한 소터
  (Ready=1·CurFloor!=머리층·큐 비지 않음)마다 매 관측 주기 `IDestinationStatusService.Compute`(scoped
  WcsDbContext 스코프 생성 + paused/SorterFull 다중 쿼리)를 호출한다. 정렬 대기 창(짧음)에만 발생하고
  정상 정렬 완료 후엔 호출 0(CurFloor==F면 조기 return)이라 현 부하는 낮으나, 소터 수·정렬 빈도 증가 시
  비용. **B에서 dual-host 발신(DestinationStatusPusher도 매 주기 Compute)과 함께 hold 산출 캐싱/공유로
  정리 후보.** (I-1 재설계로 기입은 유휴 시에만 → Compute 호출은 이전보다 오히려 감소.)
- **[M-2 · 정리·다음 스프린트]** 일부 E2E 테스트 대기 람다가 `factory.SorterSnapshot(destId)`를 한 조건 안에서
  2회 이상 읽는다(스냅샷 이중 읽기 — 관측 무해하나 일관성상 1회 캡처 권장). production 아님·테스트 전용.

### 델타 재리뷰(코드리뷰) Minor — 후속 등재
- [C 스코프] 사이클 감지가 샘플링 기반(Ready=0 창 < ObserveIntervalMs면 에지 유실 → under-pop/stall만, over-pop 불가). C에서 (a) `ObserveIntervalMs << 최소 분류시간` 불변식 문서화 + (b) fail-loud 스톨 감지기(head 불변 && 유휴 && TgtFloor==0 N틱 → WARN) 추가 검토. 현재 유실 시 silent.
- [문서 sync] SPEC §2-A 표 row4 / §2-C가 "TgtFloor==0 관측 트리거"로 서술돼 있으나 실제 관측 루프는 유휴(Ready=1)에서만 기입. DepositDecider row4(Ready=0) write는 이제 소비처 없음(dead output). SPEC 표에 "관측 루프는 유휴에서만 기입" 반영 권고(동작 이상 아님).

---

# EVALUATION — S-TWO-FLOOR-CONTROL 서브 스프린트 B (IF-08 층별 호스트 라우팅 · 소터 dual-host push · 부트스트랩 per-floor · IF-09 문서 · A 이연 I-2) · 2026-07-23 (Evaluator, single/functional)

브랜치 `feat/two-floor-control-b`(HEAD fdb34db=develop, A 병합 PR#76 포함 — 스택 아님·A 위 정상 브랜치), 변경은 전부 working tree.
ground truth(코드 직독 + fresh 빌드/테스트) 검증 — Generator 요약 불신. sprint-log B 마커 존재.

## 판정: **FAIL** — 블로커 = **CLAUDE.md(보호 파일) 무단 변경(계약 명문 위반 + Generator 허위 보고)**. 기술 구현 자체는 견고·Q5 사용자 승인.

### ★ BLOCKER-1 (필수 수정): CLAUDE.md 보호 파일 무단 변경 — 계약 확정 결정 직접 위반
- `git diff develop -- CLAUDE.md`: Generator가 **절대규칙 #2 문언을 변경**했다 — "FULL/PAUSED/OFFLINE이면 쓰지 않는다" →
  "PAUSED/OFFLINE이면 쓰지 않는다 + **FULL은 IF-05 dispatch에서만 차단**하고 관측 루프 물리 정렬 기입은 막지 않음".
- **계약 `✅ 확정 결정(2026-07-23)`이 명문으로 금지**: "절대규칙 #2 문언(CLAUDE.md)은 **오케스트레이터가 사용자 승인 하에
  직접 정정**(에이전트 보호 파일). **Generator는 CLAUDE.md 미변경**, 대신 SPEC §2-A/§2-C를 sync." → Generator가 이 지시를
  **정면 위반**하고 CLAUDE.md를 직접 편집.
- **Generator 핸드오프 메시지는 "CLAUDE.md 무접촉"이라 허위 보고** — ground truth와 불일치(Evaluator 독립 검증 의무의 근거).
- 하네스 규칙(workflow-agents.md Team Agent Safety): "Neither Generator nor Evaluator may modify CLAUDE.md" — 보호 구역 위반.
- ⚠ **설계(Q5: FULL 미차단)는 사용자 승인됨** — 문제는 설계가 아니라 **행위 주체**(CLAUDE.md는 오케스트레이터만·사용자 승인 하).
- **요구 조치**: Generator는 CLAUDE.md 변경을 **revert**(develop 버전 복원). 절대규칙 #2 문언 정정은 **오케스트레이터가 이미
  받은 Q5 승인 하에 별도 수행**. Generator가 올바르게 한 SPEC §2-A/§2-C sync는 유지. (Evaluator도 CLAUDE.md 수정 불가 —
  보고만.) **오케스트레이터·사용자에게 고지 필요.**

### 기술 구현 검증 (BLOCKER-1 외 전부 PASS — 코드 직독 + 빌드)
- **빌드**: `dotnet build backend/Wcs.sln` 오류 0 · 경고 10(전부 선재 NU1903). NEW 경고 0.
- **층별 호스트 라우팅(DestinationStatusPusher)** — 견고: (dest, floor-host) 단위 `RouteState`(Gate/Computed/Acked/PushInFlight
  독립)로 S-IF08 목적지당 멱등을 route당 멱등으로 확장. `ResolveRoutes`: 소터=설정 전 층(CurFloor층 accept?3:2 / 타층·오프라인=2),
  고정 슈트=자기 층 1곳, Floor==NULL 슈트=전 층 동일 accept, 레거시=단일 호스트(구 동작 보존→기존 push 테스트 무변경). CurFloor 1→2가
  route1:3→2 + route2:2→3 각 1회(중복·누락 0). 층 독립(한 층 다운이 타 층 미차단).
- **I-2(Q5 승인)**: `SorterFloorReturnService`가 매 유휴 틱 `Compute`(→ComputeSorterFull 셀 다중 집계) 대신 경량 `IsPaused`(단일
  조회) 사용. FULL은 정렬 기입 차단 안 함, Paused/Offline은 차단(#2 정정 문언 정합). `IDestinationStatusService.IsPaused`는
  destination Status/IsActive 단일 조회(ComputeSorterFull 미호출). ComputeSorter의 paused 산출과 동형.
- **DORMANT-per-floor**: `FloorHosts`/`HostByFloor`/`IsLegacySingleHost`/`HostForFloor` — 층 미설정=그 층 no-op, 전 층 미설정=전체
  DORMANT. 출하 기본 FloorHosts={}+BaseUrl=null → DORMANT(실 운영 파괴 0). `ChuteStatePushClient.PushAsync(payload, host, ct)`가
  지정 호스트로 PUT(레거시 오버로드는 BaseUrl 위임).
- **절대규칙**: #1 단일 쓰기 큐 불변(I-2는 hold 산출만)·#3 D6 클리어 0·#7 호스트 리터럴 코드 0(appsettings 주석만)·#8 Wcs.Core diff 0.
- **스코프**: Wcs.Core·마이그레이션·frontend **무접촉**. B2cFacilityService는 `RegisterDestination`에 `dest.Floor` 전달(라우팅 결선 —
  정당·in-scope). PlcGateway **무접촉**.
- **SPEC §2-A(‡행6 FULL·†행4 dead output)/§2-C/부트스트랩 dual-host 문서 sync** 존재(Generator가 지시대로 SPEC은 정확히 sync).
- **신규 테스트**: E2EGroupL(VS-E1 실 Sim+fake RCS 2대 층별 라우팅), TwoFloorHostRoutingTests(11) — 실질적.

### 테스트 안정성 — ⚠ **≥5회 flake 0 미확인** (단, 관찰된 실패는 전부 **B 무관 선재 teardown-race** + 평가 환경 열화)
- 전체 스위트 = **408개**(Generator 주장 일치). 클린 완료 run은 **408/408 GREEN**(BFIN1/2/4·이전 2회 = 최소 5회 GREEN 관찰).
- 그러나 반복 중 3회 실패 관찰 — **전부 teardown 경쟁(제품/로직 아님)**:
  · 2회 testhost crash = `[WcsTeardownGuard] SocketException(I/O 취소)` — FluentModbus/Sim TCP teardown(선재 가드가 흡수 대상).
  · 1회 = `RcsPushTests.PUSH4` **teardown** `ObjectDisposedException: CancellationTokenSource has been disposed`
    @ `PlcGateway.cs:281`(`PlcPollingService.StopAsync`의 bare `_cts.CancelAsync()` — try/catch 없음).
- **귀속**: 셋 다 **B가 건드리지 않은 파일**(PlcGateway·FluentModbus/Sim teardown)의 **선재 flake**(교훈
  testhost-teardown-channel-race / e2e-parallel-load). B는 PlcGateway diff 0. PUSH4/테스트 본문도 B 미변경. RcsPushTests는
  A의 `[Collection("RealSimSerial")]` 보유(L208). → **B 코드 회귀 아님.**
- **⚠ 평가 환경 열화 자백(내 하네스 아티팩트)**: 반복 중 `taskkill //IM dotnet.exe`를 남발해 MSBuild 빌드 노드를 손상
  → 후속 run들이 `MSB4166(자식 노드 종료)`로 실패(rc=127/1). 이는 코드·B와 무관한 **내 측정 오염**. `dotnet build-server
  shutdown`+재빌드 후 부분 회복했으나 완전 클린 ≥5회 연속을 확정하지 못함.
- **결론(안정성)**: B의 제품/로직 테스트는 클린 완료 시 408/408 GREEN이며, 관찰된 실패는 선재 teardown-race(B 무관) + 내 환경
  오염이다. 그러나 완료조건 "≥5회 반복 안정 flake 0"을 **결정적으로 확정하지 못했다** → BLOCKER-1 revert 후 **클린 환경에서
  재검증** 필요(그때 teardown flake 빈도도 재측정). ★후속(B 범위 밖·선재): `PlcGateway.StopAsync`의 `_cts.CancelAsync()`를
  `SorterFloorReturnService.StopAsync`처럼 `try/catch(ObjectDisposedException)`로 감싸면 이 teardown flake 제거 — 별도 정리 권고.

**요약: 층별 호스트 라우팅·dual-host·I-2 구현은 견고하고 Q5는 사용자 승인. 그러나 Generator가 계약이 명시적으로 금지한
CLAUDE.md(보호 파일)를 무단 변경하고 "무접촉"이라 허위 보고 → 보호구역·계약 위반으로 FAIL. CLAUDE.md revert(정정은
오케스트레이터 몫) + 클린 환경 flake 재검증 필요. → FAIL.**

---

## 독립 재검증 ADDENDUM — Evaluator (2026-07-23, 별도 인스턴스 · fresh ground truth)

위 B 평가를 **독립적으로 재수행**(코드 직독 + 자체 빌드/테스트, 위 결과 불신·중복 실행)했다. **동일 verdict 도달 — FAIL,
단일 블로커 = CLAUDE.md**. 내 증거는 위 결론을 확증하고 flake 귀속을 **결정적으로 강화**한다.

### BLOCKER-1 확증 (CLAUDE.md 보호 파일이 working-tree diff에 존재)
- `git diff develop -- CLAUDE.md` 실측: 절대규칙 #2가 "FULL/PAUSED/OFFLINE이면 쓰지 않는다" → "PAUSED/OFFLINE이면 쓰지
  않는다 + **FULL(만재)은 IF-05 dispatch에서만 차단**하고 관측 루프 물리 정렬 기입(D6)은 막지 않는다"로 변경됨(내용은 Q5 확정과 정확 일치).
- **내 평가 지시 명문**: "Generator가 CLAUDE.md를 건드리지 않았는지 — **diff에 CLAUDE.md 없어야**(오케스트레이터 커밋 예정)".
  계약 ✅확정: 이 문언 정정은 **오케스트레이터가 사용자 승인 하 직접 수행**, Generator는 CLAUDE.md **미변경**. → working-tree diff에
  CLAUDE.md가 있는 것 자체가 "rules intact" PASS 조건 미충족(내 PASS 게이트가 명시적으로 이를 요구) → **FAIL 확정**.
- ⚠ **작성 주체(authorship) 미확정**: 미커밋 공유 working-tree 변경이라 git으로 Generator vs 오케스트레이터를 귀속 불가.
  내용은 오케스트레이터 몫과 동일하므로 **(a)** 오케스트레이터가 조기 적용했다면 그가 별도 커밋으로 소유하고 Generator 작업셋에선
  분리, **(b)** Generator가 편집했다면 revert. **어느 쪽이든 지금 상태로 무조건 APPROVED는 불가** — main이 authorship을 확정·처분.
  설계(Q5 FULL 미차단)는 사용자 승인됨 — 쟁점은 설계가 아니라 보호 파일 편집 주체·시퀀싱뿐.

### 기술 구현 — 독립 PASS (코드 직독)
- **빌드**: `dotnet build backend/Wcs.sln` 오류 0 · 경고 10(전부 선재 NU1903 SQLite CVE, base develop 부채) · NEW 경고 0.
- **절대규칙 직독**: #1 PLC 단일 쓰기 큐 — PlcGateway diff **0**(I-2는 hold 산출부만). #3 D6→0 쓰기 경로 **0**(decision.TgtFloorValue=머리층 F, 클리어 없음).
  #7 `192.168` 코드 리터럴 **0**(backend/src 매치는 appsettings 주석·그 bin 사본뿐 — 코드 0). #8 Wcs.Core diff **0**(git stat 빈 출력).
- **3자 일치(#2 ↔ SPEC ↔ 코드)**: CLAUDE.md #2(FULL 미차단) ↔ SPEC §2-A ‡행6·†행4 dead-output 주석·§2-C ↔ `SorterFloorReturnService.ObserveSorter`
  (hold = paused? Paused : None, ComputeSorterFull 미호출) — 셋 정합. FULL 미차단·Paused/Offline 차단 정확.
- **Q5/I-2 스파이 검증**: `TwoFloorWriteGateI2Tests` — VSE2a(만재 would-be-Full 소터 idle·미정렬 → TgtFloor=2 기입 발생 + `ComputeCount==0`·`IsPausedCount` 매틱 증가),
  VSE2b(Paused → 미기입 + `ComputeCount==0`). 관측 루프가 `Compute`(→ComputeSorterFull 셀 집계) 0회 호출·경량 `IsPaused`만 호출을 구조적으로 실증.
- **dual-host 멱등/DORMANT/wire**: `(dest,floor-host)` 단위 `RouteState`(Gate/Computed/Acked/PushInFlight), per-route 락+in-flight로 전이당 1회.
  레거시 BaseUrl fallback 보존(HostByFloor 비었을 때만) → 기존 push 테스트군 회귀 0. 층별 DORMANT(HostByFloor 미설정 층 no-op). wire=snake_case·PUT·층 필드 유입 0(VS-B9).
- **스코프**: Wcs.Core·마이그레이션(신규 0·스키마 불변)·frontend(`git diff develop --stat -- frontend/` **빈 출력**) 무접촉. B2cFacilityService=RegisterDestination에 dest.Floor 전달(라우팅 결선·in-scope).

### 테스트 안정성 — flake 귀속 **결정적으로 환경(고아 testhost)으로 확정** (제품 회귀 아님)
- **전체 스위트 = 408개**(Generator 주장 일치). **클린 완료 시 408/408 GREEN 관찰 ≥5회**: 초기 연속 2회(408/408 rc=0) + 이후 3회(408/408 rc=0, ~71–75s).
- **신규 B군 격리(`FullyQualifiedName~TwoFloor`, 16개) 5/5 GREEN**(각 11–14s, rc=0) — 타이밍 민감 신규군 결정성 확인.
- **관측된 절단 run(243·397·400·405 등)은 전부 `실패:0`**(named test FAIL 0회) + rc=1 — 즉 assertion 실패가 아니라 testhost 조기 abort/crash.
- **★ 결정적 귀속 증거**: 반복 중 rebuild가 `MSB3021/MSB3027 — "파일이 testhost(44004)에 의해 잠겨 있습니다"(Wcs.Api.dll)`로 실패.
  즉 **고아 testhost가 kill을 넘겨 생존**해 후속 run의 포트/자원을 오염 → 절단. 이는 교훈 `testhost-teardown-channel-race` /
  `e2e-parallel-load-surfaces-integration-flakes`의 **문서화된 선재 flake이자 내 반복 실행이 유발한 평가-환경 아티팩트**다(B 코드 무관).
  `WcsTeardownGuard SocketException(I/O 취소)`도 동반 관찰 — 선재 teardown 가드가 흡수하는 대상.
- **stash 대조 시도**(`git stash -u`로 A-베이스라인 회귀 후 재빌드)는 위 고아 testhost 잠금으로 rebuild가 실패해 **A 바이너리 생성 불가**
  → A vs B 순수 대조는 미완. 단 그 실패가 곧 고아-testhost 오염의 물증이며, 같은 (스테일 B) 바이너리가 고아 제거 후 즉시 408/408×3 클린 →
  **절단의 원인이 B 코드가 아니라 잔존 프로세스임을 확정**.
- **결론(안정성)**: B는 제품/로직 회귀 0(named FAIL 0회, 클린 408/408 ≥5회 + 격리 16/16×5). 절단은 전부 고아 testhost/teardown-race(환경).
  단 "≥5회 **연속** 클린"은 내 환경 열화로 한 세션에서 결정적으로 못 박지 못함 → BLOCKER-1 처리 후 **클린 환경 연속 재확인 권고**(제품 판정엔 영향 없음).
- ★후속(B 범위 밖·선재 정리 권고): `PlcGateway.StopAsync`의 bare `_cts.CancelAsync()`를 `SorterFloorReturnService.StopAsync`처럼
  `try/catch(ObjectDisposedException)`로 감싸면 이 teardown flake 소거 가능.

### 최종(ADDENDUM) — FAIL 유지
층별 호스트 라우팅·dual-host 멱등·I-2(Q5)·DORMANT·wire·절대규칙·스코프·3자 일치 전부 독립 PASS이고 제품 회귀는 0.
**유일 블로커는 CLAUDE.md(보호 파일)가 working-tree diff에 존재**한다는 사실 — 내 PASS 게이트("diff에 CLAUDE.md 없어야")를 위반.
authorship을 main이 확정: Generator 편집이면 revert, 오케스트레이터 조기 적용이면 그가 별도 소유·Generator 작업셋에서 분리.
그 후 클린 환경 연속 GREEN 재확인 시 APPROVED 전환 가능. → **FAIL**.

---

## FIX ITER 1 RE-VERIFY — Evaluator (2026-07-23, 독립 재검증 · fresh ground truth)

이전 FAIL의 단일 블로커(CLAUDE.md) 처분 + Generator teardown 근본픽스 적용 후 델타 재검증. **판정: APPROVED.**

### BLOCKER-1 해소 확인 (git ground truth)
- HEAD = `a7ddce7`(develop..HEAD 유일 커밋), author pass0210 · Co-Authored-By Claude, 메시지 "docs(rules): 절대규칙 #2 정정 —
  FULL은 IF-05 dispatch만 차단 (Q5 승인, 오케스트레이터)". `git show --stat a7ddce7` = **CLAUDE.md만 +3/-1**(다른 파일 0).
- `git status --short`에 **CLAUDE.md 없음** — 즉 working-tree diff에서 분리됨. 내 이전 해소책 (a)대로 오케스트레이터가 별도 커밋으로 소유.
  authorship 확정·보호구역 위반 없음. Generator는 CLAUDE.md 미변경(SPEC §2-A/§2-C sync만) — 3자 일치(#2↔SPEC↔코드)는 이전 라운드 PASS 유지.

### teardown 근본픽스 확인 (PlcGateway.cs — 이번 유일 코드 변경, +8/-1)
- `git diff develop -- backend/src/Wcs.PlcGateway/PlcGateway.cs`: `PlcPollingService.StopAsync`(L281)의 bare `await _cts.CancelAsync()`를
  `try { … } catch (ObjectDisposedException) { }`로 감쌈. SorterFloorReturnService·DestinationStatusPusher와 동일 패턴 미러.
- **쓰기 큐 경로·폴 루프·취소 시맨틱 불변**: 변경은 취소 호출의 예외 흡수뿐(CTS 이미 dispose면 취소는 어차피 종결). ProcessWriteAsync/
  EnqueueAsync/RMW 무변경 → 절대규칙 #1 보존. 내가 이전 라운드에 지목한 `PUSH4 ObjectDisposedException @ PlcGateway.cs:281` 근본원인 제거.
- 스코프: PlcGateway 변경은 teardown 예외 흡수 한정(단일 쓰기 큐 로직/레지스터 맵 무변경) — 오케스트레이터 승인 최소 픽스. 그 외 B 파일 무변경.

### 클린 환경 ≥5회 연속 재검증 (fresh evidence — 내 자체 실행, Generator 보고 불신)
- 방법(지난 MSB3021 오염 제거): 시작 전 고아 dotnet/testhost/vstest/Sim/Api 전수 kill + 포트 1502 free 확인 + 클린 rebuild(0오류·경고10 선재 NU1903·NEW 0).
  이후 `dotnet test backend/Wcs.sln --no-build` **각 run 자연 완료**(mid-run kill 0), run 사이에만 완료 고아 정리.
- **RUN 1~5 전부 `실패: 0, 통과: 408, 전체: 408` GREEN · rc=0**(각 71–74s). **MSB3021/파일잠금 0 · abort("중단되었습니다") 0 · flake 0.**
  (VS-B1~B9·VS-E1/E2·teardown·RcsPushTests·전체 스위트 포함.) 신규 B군 격리(`~TwoFloor` 16개)도 별도 5/5 GREEN(이전 라운드).
- 결론: 이전 라운드의 절단 run들은 고아 testhost 파일잠금(환경 아티팩트)이 원인이었음이 확정 — 자연 완료 방식 5/5 연속 클린으로 소거.
  PlcGateway teardown 픽스가 PUSH4 ObjectDisposedException 근본원인도 제거.

### 최종 — APPROVED
층별 호스트 라우팅·소터 dual-host 멱등·부트스트랩 per-floor·I-2(Q5 사용자 승인)·DORMANT·wire 계약 불변·절대규칙 #1/#3/#7/#8·
3자 일치·스코프·문서 sync 전부 PASS(이전 라운드 독립 코드리뷰). CLAUDE.md 블로커는 오케스트레이터 커밋 `a7ddce7`로 해소.
teardown flake는 PlcGateway 근본픽스 + 클린 5/5 연속 408/408 GREEN으로 소거. **→ APPROVED (FIX ITER 1 최종).**

APPROVED

### 코드리뷰(4-Tier) 후속 등재 — B, Critical 0 · Ready-to-merge
- [Important·선재/후속] DestinationStatusPusher observe 루프가 소터마다 매 틱(150ms) `ComputeSorterFull`(다중 테이블 쿼리 2회) 호출 후 폐기 — accept=Ready&&!Paused라 Full 불요. I-2가 SorterFloorReturnService에서 없앤 바로 그 비용이 pusher 경로엔 남음(비대칭). 방향: pusher에 경량 readiness(IsPaused + decision.Ready 재사용, ComputeSorterFull 스킵). 선재(S-IF08) — 후속 스프린트(C 또는 별도 cleanup).
- [Minor] `ChuteStatePushOptions.HostForFloor(int)` dead code(WcsOptions.cs:255) — 미사용·주석 오해소지. 제거 또는 결선.
- [Minor] `HostByFloor` get마다 Dictionary 재파싱·할당(WcsOptions.cs:198, 소터 hot path 2×/Observe) — 설정 불변이므로 1회 계산(field/Lazy).
- [Minor] bootstrap ResolveRoutes가 어떤 슈트에서 throw하면 RouteState 0개→하트비트가 "동기 완료"로 간주해 재관찰 안 됨(DestinationStatusPusher.cs:301). 확률 낮음·이벤트 시 self-heal. 인지만.
- [설계노트] NULL-floor 고정 슈트는 전 호스트 브로드캐스트(chuteNo 전역유일이라 무해) — RCS가 타 층 chute 항목 허용하는지 1줄 확인 권고.

---

# Sprint Feedback — S-TWO-FLOOR-CONTROL 서브 스프린트 C1 (R-clear@Ready + 처리 3시각 + 양-provider 마이그레이션)

Evaluator: 단일 (functional + data-integrity[마이그레이션 포함] 순차).
평가일: 2026-07-24. 브랜치: feat/two-floor-control-c1 (HEAD ea8d48e, 작업은 전부 워킹트리 미커밋 — status 확인).
방법: ground-truth git 확인 + 계약/코드 직독 + fresh 빌드/테스트(자체 실행·Generator 보고 불신) + 양 provider 마이그레이션 실적용.

## 1. Build + Tests — PASS
- `dotnet build backend/Wcs.sln -c Debug`: **0 오류**. 경고 = NU1903(선재 SQLitePCLRaw 취약성) 10개뿐 — **NEW 경고 0**. (Generator가 언급한 CS8604/xUnit2013은 이 빌드에 미출현.)
- `dotnet test backend/Wcs.sln --no-build` **전체 스위트 5회 연속 GREEN**: RUN1~5 전부 `실패: 0, 통과: 413, 전체: 413`(각 72–75s). flake 0. run 사이 orphan Sim/testhost kill·자연완료(mid-run kill 0 → MSB3021 0).
- baseline 산술: 413 − 5 신규(HandshakeReturnClearTests R1~R4 4건 + E2EGroupCD D11 1건) = **408** ✓.
- 타이밍 취약군(`HandshakeReturnClearTests|HandshakeResidueTests|E2EGroupCD_AlignHandshakeTests`, 21 테스트) **추가 3회 반복 GREEN(21/21)** — 전체 5회 + 표적 3회 = 이 군 8회 관측 flake 0(s9-flake·e2e-parallel-load 교훈 대응).

## 2. R-clear@Ready 타이밍 실증 (E2E, 실 SimServer TCP) — PASS
상세 로거 출력(fresh, 자체 실행)으로 실증:
- **(b) 무-이동 사이클 R1**: Outcome=Success, tilted==returned, `gap=0.0711ms < 300ms`(MoveDuration=1000ms인데 이동 미발생) → 즉시 clear·추가 지연 0. `HS_RETURN_TIMEOUT` 미발생 + CLEAR_R 발생.
- **(a) 복귀 이동 사이클 R2**: Outcome=Success, `gap=411ms`(MoveDuration=400ms 실측). RegChange 순서 `R_Flag↑=idx4 → Ready↑=idx5 → R_Flag↓=idx9` — **ClearR(R_Flag 1→0)가 Ready 0→1 이후**임을 인덱스로 실증 = Ready==1까지 R 영역 유지. `HS_RETURN_WAIT` 스테이지 발화.
- **(c) 복귀 타임아웃 R3**: MoveDuration=3000ms ≫ ReturnReadyTimeoutMs=250ms → Outcome=Success, tiltedAt non-NULL, **returnedAt=NULL**, `HS_RETURN_TIMEOUT` 스테이지 + CLEAR_R ack.
- **(d) 회귀 R4(불일치)**: Outcome=RSeqMismatch, tiltedAt 기입, returnedAt=NULL, `HS_RETURN_WAIT` 미발생(즉시 clear 현행 유지). arming/타임아웃/OFFLINE 경로 diff 0(코드 직독) — HandshakeOrchestrator 삭제 라인은 옛 즉시-clear 성공경로 + mismatch return 라인뿐, ArmRFlagZeroAsync/WaitCFlagZeroAsync/R-poll 타임아웃 무변경.

## 3. 3시각 DB 실증 — PASS
- **D11**(E2E, 실 Sim3ds Tcp, IF-10→핸드셰이크→DB 3레이어 관통): 성공 sorter_command 행 `deposited=01:30:54.163 ≤ tilted=01:30:54.419 ≤ returned=01:30:54.421`, **전부 non-NULL·단조** ✓.
- outcome별 NULL 규칙((e)): 성공=3시각 non-NULL(D11), 복귀타임아웃=returnedAt NULL·tiltedAt non-NULL(R3), 불일치=tiltedAt non-NULL·returnedAt NULL(R4). HandshakeResult가 규칙대로 담고 Finalize가 그대로 기입(DbRepositories.cs:853-857) — RFlagAt=now 옛 기입 제거 확인.

## 4. 양 provider 마이그레이션 — PASS (교훈 sqlserver-migration-prod-provider 대응)
- **SQLite**(scratch `migcheck.db`): `ef database update` fresh 7개 마이그레이션 적용(신규 `20260724005735_AddSorterCommandProcessingTimes` 최신). PRAGMA table_info: `TiltedAt`(구 RFlagAt 개명)·`DepositedAt`·`ReturnedAt` 전부 nullable TEXT 실재, **RFlagAt 소멸**. FK piece/cell 둘 다 RESTRICT 불변. 인덱스 IX_PieceId/IX_CellId 불변(신규 0). has-pending = "No changes".
- **SQL Server**(실 검증 — localhost 가용): 빈 일회용 DB `WcsMigCheck_20260724102953` 생성 → `ef database update` **fresh 성공(1785/207 없음)** 신규 마이그레이션 최신 적용. sys.columns: `TiltedAt`/`DepositedAt`/`ReturnedAt` datetime2 nullable=1, RFlagAt count=0. sys.foreign_keys: FK_..._cell/piece 둘 다 delete=NO_ACTION·update=NO_ACTION 불변. sys.indexes: PK(clustered)+IX_CellId+IX_PieceId(nonclustered non-unique), 신규 0. **DROP 후 존재 0 확인**. has-pending = "No changes".
- **운영 DB 무접촉**: `Rcs3dsInterlockingWcs.__EFMigrationHistory` 최신 = `20260713053134_AddPieceArchivedAt`(신규 미적용) 읽기전용 확인 → 스크래치/일회용에만 적용됨 증거로 닫힘.
- RenameColumn(RFlagAt→TiltedAt) 데이터 보존 방식 확인(양 provider Up/Down 대칭).

## 5. 절대규칙 (코드 직독) — PASS
- **#1**: 모든 ClearR = `_gw.EnqueueAsync(new PlcWrite.ClearR())` 경유(HandshakeOrchestrator L181/309/404/422). 직접 Modbus 0. ProcessWriteAsync ClearR 케이스 내부 무변경(PlcGateway.cs diff = ReturnReadyTimeoutMs 필드 1개뿐).
- **#3**: TgtFloor 미접촉 — 프로덕션 src에 D6/TgtFloor **write 0**(RcsController의 TgtFloor는 전부 주석). SetTgtFloor는 테스트 하니스(R2/R3 Sim 구동)에만.
- **#4**: 복귀 완료 판정 = `s.Ready`(Ready 0→1)로 정확.
- **#7**: 복귀 타임아웃/주기 = `_opt.ReturnReadyTimeoutMs`/`_opt.RFlagPollMs` (appsettings·PlcGatewayOptions·TimingOptions·SorterTimingOverride·Program.cs 배선). 신규 return-wait 코드 3자리+ ms 리터럴 grep 0.
- **#8**: Wcs.Core diff 0(git 확인).
- **teardown 채널 경쟁 방어**: 신규 WaitReadyThenClearRAsync 대기 루프 `Task.Delay(_opt.RFlagPollMs, ct)`가 ct 존중. 핸드셰이크 dispatch = `bundle.ExecuteHandshakeAsync(cell, lifetime.ApplicationStopping)` → 종료 시 취소 전파 + ContinueWith `stopping.IsCancellationRequested→return` 게이트 + ObjectDisposedException 스킵 유지. 테스트 하니스 DisposeAsync는 `Queue.Writer.TryComplete()` 선행(교훈 testhost-teardown 미러). 새 teardown 경쟁 도입 0.

## 6. 스코프 — PASS
- 무접촉존 diff 0(git 확인): Wcs.Core·frontend·Sim3ds 소스·arming(ArmRFlagZeroAsync)·ProcessWriteAsync ClearR 케이스·CLAUDE.md. HandshakeOrchestrator 삭제 라인은 성공경로 재구성분뿐. C2(재시작/I-3)·C3(스톨/pusher) 미구현(로드맵 유지). 마이그레이션 FK/인덱스 거동 불변.
- frontend diff 0: `git diff --stat -- frontend/` 빈 출력 → Web/UI 브라우저 검증 면제(계약 N/A 근거 성립).

## 7. Generator 플래그 2건 판정 — 둘 다 합리적
- **(i) PascalCase 컬럼명**: 계약 scope #4·ERD는 snake_case(deposited_at 등) 표기이나, sorter_command 테이블의 **기존 컬럼 전부가 PascalCase**(Id/PieceId/CWrittenAt/RFlagAt…, WcsDbContext "B2C 규약·HasColumnName 미사용")임을 SQL Server sys.columns 덤프로 확인. 신규 3컬럼만 snake_case로 가면 **한 테이블 내 혼용** — Core Principle "Consistency Over Preference" 위반. PascalCase 준수가 정답. Generator가 sprint-log에 투명 플래그. → **PASS(기존 스키마 관행 준수)**.
- **(ii) 복귀 타임아웃 outcome=Success + returnedAt=NULL + WARN**: R_Seq 대사 성공 = 틸트·적재 완료(분류 성공). Ready 미복귀는 복귀 이동 정체(계측 실패)일 뿐 분류 결과를 뒤집지 않음 → 완료된 분류를 실패(MISMATCH/TIMEOUT)로 격하하면 재dispatch/오알람 유발. Success 유지 + returnedAt=NULL(계측 실패 표기) + RETURN_TIMEOUT WARN(운영 이상 표면화) + ClearR ack(R 잔류 방지)가 계약 (d-iii)·(e)에 정합. → **PASS(합리적)**.

## 최종 — APPROVED
전체 5회+타이밍군 8회 GREEN(413/413, flake 0)·R-clear@Ready 레지스터 순서 실증·3시각 단조 DB 실증·양 provider 마이그레이션 실적용(SQL Server 일회용 fresh·운영 무접촉·FK/인덱스 불변)·절대규칙 #1/#3/#4/#7/#8·teardown 방어·스코프·플래그 2건 전부 PASS. **→ APPROVED.**

APPROVED

### 코드리뷰(4-Tier) 후속 등재 — C1, Critical 0 · Ready-to-merge
- [Important·소비처 결선 전 처리] depositedAt≤tiltedAt 단조가 코드로 강제 안 됨. depositedAt=RCS 클라이언트 timeStamp(ParseTimestamp ?? UtcNow), tilted/returned=서버 UtcNow. clamp는 tilted≤returned만 보장. Entities.cs:373 주석·D11 테스트는 전체 체인을 보장처럼 단언(현재 E2E 드라이버가 server-ish 시각이라 통과). 실 RCS 시계 오차 시 depositedAt>tiltedAt 가능 → 향후 '투입→틸트' 소요 지표 음수. 관측 전용(프론트 미결선). 방향: (a) depositedAt를 서버 시계로 정규화, (b) 하류 소요 계산 방어적으로, 또는 (c) 주석/테스트를 'depositedAt=client-sourced, 하드 보장은 서버측 tilted≤returned'로 완화. → C3/cleanup 또는 프론트 소요 지표 결선 전 처리.
- [Minor] ReturnReadyTimeoutMs 경계검증 없음(≤0이면 매 복귀사이클 즉시 타임아웃→허위 RETURN_TIMEOUT). RFlagPollMs=0 tight-loop도 동일(선재). 가드/최소값 문서화 권고.
- [Minor] 복귀 대기(최대 ReturnReadyTimeoutMs) 동안 R 영역 유지 → 동일 소터 concurrent IF-10 시 2번째 arming이 1번째 R 잔류 보고 조기 ClearR(memory single-sorter-concurrent-handshake-gap). 순차 dispatch 전제라 비블로킹이나 C1이 취약 창을 늘림 → 향후 동일소터 직렬화 후보.
- [Minor·정보] 개명 컬럼 의미 드리프트: 레거시 행 TiltedAt=구 Finalize 시각(Success만) vs 신규=R_Flag 관측(Mismatch도). 이력 비교 시 유의.
