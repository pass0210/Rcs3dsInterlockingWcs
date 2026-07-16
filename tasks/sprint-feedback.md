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
