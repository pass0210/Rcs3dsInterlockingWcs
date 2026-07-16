[Sprint Contract] — S-MULTISORTER-SHARED-BUS (Phase 1)

브랜치: feat/multisorter-shared-bus (base origin/develop = 8c25421)
작성: 2026-07-16 (Planner Subagent) · 원 트리거: 백로그 wcs-future-backlog "멀티소터 공유버스 리팩터"

────────────────────────────────────────────────────────────────────────
■ 배경 (실측 근거 — 실제 코드 file:line, 메모리 요약 아닌 파일 직독으로 확정)
────────────────────────────────────────────────────────────────────────

현상: 한 RTU(COM) 포트에 소터 1대만 가능. appsettings `Sorters[]`에 같은 `PortName`(예 COM1) +
다른 `UnitId`로 2대째를 추가하면 2대째가 OFFLINE이 된다.

근본원인(코드로 확정):
1. `backend/src/Wcs.Api/Program.cs` `SorterRegistryFactory.StartAsync`(L433–592)가 DB의 SORTER_3D
   destination마다(L467 foreach) **소터별로 독립 마스터를 새로 생성**한다:
   - `var master = ModbusMasterFactory.Create(transportOpt, logFac);`  (L488)
   - `var writeQueue = new PlcWriteQueue();`  (L491, 주석 "소터별 독립 PlcWriteQueue")
   - `var polling = new PlcPollingService(gwOpt, writeQueue, master, ...);`  (L494)
   - `var handshake = new HandshakeOrchestrator(polling, gwOpt, ...);`  (L501)
   즉 **소터당 마스터·큐·폴러·핸드셰이크가 전부 독립 인스턴스**다.
2. RTU 마스터는 자기 포트를 직접 Open한다: `ModbusRtuMaster.Connect()`(ModbusRtuMaster.cs:99–106)가
   `_client.Connect(_portName, _endianness)`를 호출. 같은 `PortName`으로 마스터 2개가 각각 Open →
   **한 물리 COM 포트를 두 번 Open**(2번째 `SerialPort.Open()`이 "액세스 거부"로 예외) → 그 폴러의
   `RunPollLoopAsync` catch(PlcGateway.cs:337–392)가 예외를 OFFLINE 전이로 처리 → **2대째 OFFLINE**.
   → 가설 확정: "포트 이중 Open"이 2대째 OFFLINE의 원인.

가설의 다른 절반도 확정(공유가 왜 가능한가):
3. unitId(슬레이브 주소)는 **이미 프레임마다 실려 나간다.** 요청마다 unitId 인자를 전달:
   - RTU: `_client.ReadHoldingRegistersAsync<short>(_unitId, ...)`(ModbusRtuMaster.cs:127),
     `WriteSingleRegisterAsync(_unitId, ...)`(L136), `WriteMultipleRegistersAsync<short>(_unitId, ...)`(L139).
   - TCP: 동형(ModbusTcpMaster.cs:57/66/69).
   → 마스터(=클라이언트+포트)를 **공유**하고, 요청마다 대상 unitId만 바꿔 실으면 슬레이브 구분이 된다.
     현재 유일한 장애물은 (a) `_unitId`가 **인스턴스 고정 필드**(마스터당 1 슬레이브), (b) 마스터가
     **자기 포트를 독점 Open**한다는 두 가지뿐이다. 프로토콜 차원의 다중슬레이브 지원은 이미 존재.

직렬화·큐·락 현황(절대규칙 #1 관련):
4. 쓰기 직렬화는 소터별 `PlcWriteQueue`(단일 컨슈머, PlcGateway.cs:47–60) + `PlcPollingService._clientLock`
   (SemaphoreSlim(1,1), PlcGateway.cs:147)로 이뤄진다. 폴 읽기(L255)·쓰기(L479)·RMW(L558–576)·재연결
   Disconnect(L388–390)가 전부 이 **인스턴스 하나의 _clientLock** 임계구역에서 직렬화된다. 지금은 마스터가
   소터당 1개라 "락 1개 = 포트 1개 = 슬레이브 1개"가 우연히 일치한다. 공유 버스가 되면 **한 물리 포트를
   두 폴러가 공유**하게 되어, 각자의 _clientLock으로는 프레임 인터리브(교차)를 막지 못한다 → **버스 단위
   락 1개**가 필요하다.
5. 폴 루프는 주기마다 1회 FC03: `await Task.Delay(_opt.PollIntervalMs, ct)` 후 `_clientLock` 잡고
   D0~D6 일괄 읽기(PlcGateway.cs:252–269). 슬레이브별 독립 폴러 N개가 각자 PollIntervalMs를 sleep하고
   공유 락을 다투면 타이밍이 어색해진다(설계 방향 4 참조).

Sim3ds(테스트 상대역) 현황 — 단일 유닛:
6. `SimServer`(Sim3ds/SimServer.cs)는 단일 `_unitId`(L121)·단일 섀도 `_hr`(L132)·단일 상태기계
   (`RunSimLoopAsync` L282~, 분류/이동 시퀀스)로 되어 있다. TCP 전송은 `AddUnit(unitId)`를 **1회만**
   호출(SimTransport.cs:54), `GetHoldingRegisters(unitId)`도 그 한 유닛만. → **한 TCP 엔드포인트에서 여러
   unitId 슬레이브를 흉내낼 수 없다.** 멀티소터-한버스 통합 검증의 전제가 부재.
7. 현재 "멀티소터" 통합 인프라(`E2EInfrastructure.cs`)조차 **소터마다 별도 SimServer를 서로 다른 동적 포트로
   기동**한다(L108–129: chuteNo마다 `GetFreePort()`+새 SimServer; L177–187: `Sorters:{i}:Port`가 슬롯별로
   다름). 즉 지금의 다중소터는 "포트당 1대(멀티 포트)"이지 "한 버스 공유(멀티 슬레이브)"가 아니다.
   → 공유 버스는 이 리팩터로 처음 도입되는 능력이며, 그것을 검증할 하네스(멀티유닛 Sim)도 신규다.

문서 정합:
8. `docs/SPEC.md` §7-A L110은 "소터별 독립 포트(토폴로지 확정): 소터마다 독립 버스/포트(포트당 소터 1대,
   다중 슬레이브 경합 없음)"를 **확정 토폴로지**로 명시한다. 본 리팩터는 이 문장을 **명시적으로 변경**한다
   (→ Open Questions OQ5: 문서 갱신은 Phase 2 산출물로 제안).

────────────────────────────────────────────────────────────────────────
■ Goal
────────────────────────────────────────────────────────────────────────
하나의 물리 버스(같은 PortName / 같은 TCP 엔드포인트)에 여러 3D 소터(슬레이브)를 **unitId로 구분해 공유
운영**할 수 있게 하는 통신 계층 메커니즘을 도입한다. 이 스프린트(**Phase 1**)의 목표는 **버스 공유 메커니즘
자체**(공유 마스터 + 버스 락 + 버스 단위 폴 사이클(슬레이브별 독립 상태) + 버스 단위 공유 쓰기 큐(슬레이브별
타겟팅))와 **Sim3ds 멀티유닛 하네스**를 완성하고, **PlcGateway ↔ TCP Sim3ds 통합 테스트**로 다음을
입증하는 것이다: 한 버스의 두 슬레이브가 각각 ONLINE·독립 폴링, 두 슬레이브에 대한 동시 핸드셰이크가 프레임을
교차시키지 않음(버스 락), 한 슬레이브의 타임아웃이 다른 슬레이브를 OFFLINE으로 오판하지 않음.

이 스프린트는 **DI/레지스트리 결선·appsettings 그룹핑 스키마·풀스택 E2E는 다루지 않는다**(→ Phase 2).
경계 근거: DI/DB/HTTP를 건드리지 않고 Wcs.PlcGateway + Wcs.Sim3ds + Wcs.Tests 안에서 버스 메커니즘을
독립 검증할 수 있고, 이 세 모듈이 절대규칙 #1(단일 큐)·프레임 무결성이라는 최고위험 부분이기 때문이다.

절대규칙 #1은 **의미 불변, 입도만 변경**: 쓰기는 여전히 단일 큐 컨슈머 하나로만 직렬화된다. 다만 큐의 입도가
"소터당 1큐"에서 "**버스당 1큐(멤버 슬레이브 공유)**"로 바뀐다. 핸들러의 Modbus 직접 호출 금지·D4 RMW
안전은 그대로 보존한다.

────────────────────────────────────────────────────────────────────────
■ Implementation Scope (Generator가 할 일 — 파일 지정)
────────────────────────────────────────────────────────────────────────
기법 상세(클래스 배치·인터페이스 형태)는 Generator 재량. 아래 **불변식·요구**는 계약으로 강제한다.

A. Wcs.Sim3ds — 멀티유닛 슬레이브 (모듈 A)
   - 대상: `backend/src/Wcs.Sim3ds/SimServer.cs`, `SimTransport.cs`, (필요시) `Sim3dsConfig.cs`, `Program.cs`.
   - 요구: **한 TCP 엔드포인트(그리고 RTU 한 포트)에서 여러 unitId 슬레이브를 응답**할 수 있어야 한다.
     각 unitId는 독립 레지스터 뱅크(섀도 _hr)와 독립 상태기계(C_Flag 감지→분류→R 에코, 이동, 잔류 프리셋,
     고장주입 InjectRSeqOverride/InjectRFlagDelayMs/InjectNoResponse/InjectStickyRResidue)를 갖는다.
     고장주입은 **슬레이브별로 독립 지정 가능**해야 한다(scenario (c) 전제 — B 타임아웃/무응답을 A와 분리).
   - 전송: TCP는 `ModbusTcpServer.AddUnit(unitId)`를 유닛 수만큼 호출(FluentModbus 다중 유닛 지원).
     `GetHoldingRegisters(unitId)`/RegistersChanged는 unitId 스코프로 유닛별 처리.
   - **하위호환(회귀 0)**: 단일 유닛 구성은 현행과 바이트/동작 동일 — 기존 단일 SimServer 사용처(모든 통합
     테스트, `dotnet run --project Wcs.Sim3ds`)가 무변경으로 통과. `Sim3dsConfig` 기본값(Transport=Tcp,
     단일 유닛)은 불변(Sim3dsRtuTests A1~A10 GREEN 유지).

B. Wcs.PlcGateway — 버스 공유 메커니즘 (모듈 B)
   - 대상: `backend/src/Wcs.PlcGateway/PlcGateway.cs`, `HandshakeOrchestrator.cs`,
     `Modbus/IModbusMaster.cs`, `ModbusRtuMaster.cs`, `ModbusTcpMaster.cs`, `ModbusMasterFactory.cs`.
   - 요구 불변식:
     (B1) **버스당 마스터 1개**: 같은 버스(같은 PortName / 같은 host:port)의 슬레이브들이 클라이언트+포트를
          **1개만 Open**해 공유. 서로 다른 버스는 현재처럼 병렬(각자 독립 마스터·포트).
     (B2) **unitId per-call 라우팅**: 공유 마스터가 요청마다 대상 unitId를 실어 슬레이브를 구분. (설계 자유:
          IModbusMaster 메서드에 unitId 인자 추가 vs 공유 클라이언트를 감싼 per-slave 어댑터. **권장**:
          기존 IModbusMaster 시그니처를 깨면 ~20개 파일(테스트 fake 다수 — RcsPushTests·ChuteStatePushTests·
          SorterPushOperationalTests·SorterCellFullnessTests·ScenarioTests 등)이 연쇄 수정된다. 회귀
          최소화를 위해 **가산적/호환 접근**(per-slave 어댑터가 공유 클라이언트에 unitId 주입)을 우선 검토.)
     (B3) **버스 락 1개로 트랜잭션 직렬화**: 폴 read(FC03)·write(FC06/16)·D4 RMW(read+write)·재연결
          Disconnect가 **버스 단위 단일 임계구역**에서 직렬화되어 프레임이 절대 교차하지 않는다. (현
          `_clientLock` 인스턴스별 락을 버스 단위 공유 락으로 승격.)
     (B4) **버스 단위 폴 사이클(주기당 1회)**: 한 폴 주기에 멤버 슬레이브 각각을 **1회씩** 트랜잭션(폴 read)
          하고 **주기당 대기는 1회**. 즉 한 주기 ≈ N×트랜잭션 + PollIntervalMs 대기1회이지, N×PollIntervalMs가
          아니다. 슬레이브별 sleep 금지. (설계 자유: 버스 스코프 폴 루프가 멤버를 순회.)
     (B5) **슬레이브별 독립 상태**: 최신 스냅샷(Latest)·Online 플래그·연속 실패 카운트·OFFLINE 전이 이벤트
          (전이당 1회)·R_Flag 에지/arming 상태·C_Seq 카운터를 **슬레이브별로 독립** 보유. 한 슬레이브의
          read 타임아웃/예외가 다른 슬레이브의 Online을 건드리지 않는다(절대규칙 #5 슬레이브별 유지).
     (B6) **버스 단위 공유 쓰기 큐(절대규칙 #1 입도 변경)**: 버스당 단일 큐 + 단일 컨슈머. 각 `PlcWrite`는
          대상 슬레이브(unitId)를 실어 큐에 들어가고, 컨슈머가 버스 락 안에서 해당 슬레이브로 라우팅해
          처리. 핸들러/서비스의 Modbus 직접 호출 금지 유지. D4 RMW의 read+write는 대상 슬레이브에 대해
          같은 버스-락 임계구역에서 원자 수행(TgtFloor==0 fresh-read 가드·C_Flag fresh-read 가드 등
          기존 안전장치 슬레이브별 보존 — PlcGateway.cs:486–541 로직 의미 불변).
   - **하위호환(회귀 0)**: **단일 슬레이브 버스 == 현행 동작**. 기동/재연결/OFFLINE 요약 로깅·arming·기동
     reconcile·teardown(Writer.TryComplete로 쓰기 컨슈머 결정 종료, PlcGateway.cs:214/SorterBundleHandle.cs:86)
     등 하드닝된 단일 슬레이브 경로가 그대로 GREEN. **경고**: 하드닝이 두꺼운 `PlcPollingService`를 in-place로
     크게 재작성하면 회귀 위험이 크다 — 단일 슬레이브 경로 의미를 보존하는 방향(기존 타입 확장 또는 버스
     스코프 조정자 신설, Generator 판단)으로.

C. Wcs.Tests — 멀티소터-한버스 통합 테스트 (fan-in)
   - 대상: `backend/tests/Wcs.Tests/` 신규 파일(예: `MultiSorterSameBusTests.cs`). 기존 테스트 무단
     변경 금지(회귀 대조 기준). 필요한 신규 테스트 헬퍼만 추가.
   - **전송은 반드시 TCP Sim3ds**(멀티유닛). 실 하드웨어 COM1/RTU 절대 금지(COM1 = 실 3DS PLC).
     결정적 설계 준수: 고정 sleep 금지, 조건 폴링(WaitUntil), baseline(Online) 확립 후 관찰(교훈 2026-07-06).
   - 시나리오는 아래 Verification Scenarios 참조.

범위 밖(무접촉 — 근거):
   - `backend/src/Wcs.Core` (순수 판정 — 절대규칙 #8. 변경 필요 시 근거 제시하고 사용자에 보고).
   - `backend/src/Wcs.Data` 및 마이그레이션(통신 계층 리팩터이지 데이터 계층 아님 — 목표 0파일).
   - `backend/src/Wcs.Api/Program.cs` `SorterRegistryFactory`·`SorterConfig`, `Infrastructure/SorterGatewayRegistry.cs`
     (DI 결선·그룹핑 → **Phase 2**). appsettings `Sorters[]` 스키마(→ Phase 2).
   - 프론트엔드(0파일). 실 PLC/실 DB/사용자 로컬(절대 무접촉).

────────────────────────────────────────────────────────────────────────
■ Constraints (절대규칙 보존 명문화)
────────────────────────────────────────────────────────────────────────
- 절대규칙 #1(단일 큐 직렬화): **의미 불변, 입도만 버스 단위로.** 버스당 단일 큐+단일 컨슈머 유지. API/서비스/
  핸들러·핸드셰이크·폴 루프의 Modbus **직접 호출 금지**(전부 큐 경유). D4는 RMW(read-modify-write)로,
  read+write를 **버스 락 단일 임계구역**에서 원자 수행(비트 경합·프레임 교차 0).
- 절대규칙 #2/#3(TgtFloor): SetTgtFloor는 TgtFloor==0 fresh-read 가드 후에만 기입, WCS는 클리어 안 함 —
  슬레이브별로 기존 로직(PlcGateway.cs:486–504) 의미 보존.
- 절대규칙 #4(Ready 의미)·#5(FULL/PAUSED/OFFLINE은 WCS 판단, OFFLINE=폴 타임아웃/소켓 끊김): OFFLINE
  판단은 **슬레이브별 독립**. 한 슬레이브 타임아웃이 다른 슬레이브를 OFFLINE으로 만들지 않는다. 예외를
  삼키지 말 것(폴 실패 → 그 슬레이브만 OFFLINE 전이로 명시 처리).
- 절대규칙 #7(타이밍 외부화·하드코딩 금지): PollIntervalMs·RFlag*·OfflineAfterFailures·타임아웃 등 모든
  시간값은 설정/옵션 주입. 버스 폴 주기·슬레이브 수 기반 튜닝 여지를 옵션으로(신규 상수 하드코딩 0). 테스트는
  단축값을 옵션으로 주입.
- 절대규칙 #8(판정은 Wcs.Core 순수함수): 판정 로직 무접촉. 본 스프린트는 I/O·전송 계층만.
- 전송 계층 의미: RTU는 "물리 포트 1개 공유"가 핵심(현장 목표). TCP는 "같은 host:port에 여러 unitId"
  (한 ModbusTcpServer가 여러 AddUnit) 모델을 **테스트 vehicle**로 사용. 테스트는 TCP Sim3ds로만.
- teardown 결정성(교훈: testhost-teardown-channel-race): 버스 쓰기 큐도 종료 시 `Writer.TryComplete()`로
  컨슈머를 결정적으로 종료. 멀티유닛 Sim의 accept 루프/RTU read 루프 종료도 기존 `WcsTeardownGuard`·
  DisposeAsync 패턴 보존(고아 Wcs.Sim3ds.exe 파일잠금 → MSB3021 재빌드 실패 방지: 빌드 전 고아 프로세스
  kill).
- 회귀 귀속(교훈: s9-flake / e2e-parallel-load / sim-timeline race): full-suite 1회 GREEN을 신뢰하지 말 것.
  알려진 취약 클래스(S5RSeqMismatch, S9, IT3a, IT4b, teardown 채널 경쟁)는 **직렬/격리 재실행 ≥5회**로
  회귀 여부를 귀속. 비동기 로그 append는 스냅샷 전이와 다름 — 로그 출현 대기 후 캡처.
- 워크트리 격리(교훈 2026-06-16 / 메모리 agent-worktree-stale-base): 병렬 모듈을 worktree로 돌릴 경우
  `isolation:worktree` 자동 생성이 **구커밋 베이스**를 만들 수 있으므로, 수동 `git worktree add`로 base=현
  브랜치 tip(feat/multisorter-shared-bus) 명시. 커밋 직전 항상 `git rev-parse --abbrev-ref HEAD` 확인,
  develop 직접 커밋 0.

────────────────────────────────────────────────────────────────────────
■ Detected Project Type: Full-stack
  (레포 신호: frontend/ SPA + backend/ ASP.NET Core + EF + SignalR = Full-stack.
   단, THIS 스프린트(Phase 1)의 변경 표면은 서버측 Modbus/게이트웨이/Sim/테스트뿐 — HTTP/브라우저 표면 0.)
────────────────────────────────────────────────────────────────────────

────────────────────────────────────────────────────────────────────────
■ Evaluation Criteria (가중치 — Evaluator 판정)
────────────────────────────────────────────────────────────────────────
1. (30%) 공유 버스 정합성: 한 TCP 엔드포인트의 두 unitId 슬레이브가 **마스터/포트 1개만 Open**한 채 둘 다
   ONLINE·독립 폴링. (버스당 마스터 1 — B1 입증.)
2. (25%) 프레임 무결성/동시성: 두 슬레이브에 대한 동시 핸드셰이크가 각자 R_Seq==자기 C_Seq로 성공, 프레임
   교차/R_Seq 교차 0. 반복(≥20회) 무결. (버스 락 — B3 입증.)
3. (15%) 슬레이브별 OFFLINE 독립: 슬레이브 B 무응답/타임아웃 시 B만 그 상태로 전이, A는 ONLINE 유지·핸드셰이크
   성공. (B5 입증.)
4. (15%) 회귀 0: 기존 전 스위트 GREEN(단일 슬레이브=현행 동작), 취약 클래스 직렬/격리 ≥5회 GREEN으로 귀속.
5. (10%) 절대규칙 #1 보존: 모든 쓰기가 버스당 단일 큐 컨슈머 경유, Modbus 직접 호출 0, D4 RMW가 버스 락
   임계구역 안. (코드 검사 + 인터리브 쓰기 테스트로 비트 보존 입증.)
6. (5%) 범위/안전: Wcs.Core·Data·마이그레이션·프론트·DI(SorterRegistryFactory)·appsettings 무접촉(git diff로
   스코프 확인), 하드코딩 타이밍 0, 실 PLC/DB 무접촉.

────────────────────────────────────────────────────────────────────────
■ Completion Conditions (AND — 전부 충족해야 PASS · 수치/행위 기준)
────────────────────────────────────────────────────────────────────────
C1. 멀티유닛 TCP Sim3ds가 한 엔드포인트에서 ≥2개 unitId를 응답(각 유닛 독립 레지스터/상태기계). 단일 유닛
    구성은 현행과 동작 동일(기존 Sim 사용 테스트 무변경 GREEN).
C2. PlcGateway가 한 TCP 엔드포인트의 두 슬레이브를 **마스터/포트 1개**로 공유 폴링하여 둘 다 Online=true를
    관측(테스트에서 두 슬레이브 스냅샷 각각 갱신 확인). 마스터 인스턴스가 1개임을 구성/테스트로 입증.
C3. 버스 폴 사이클이 주기당 1회 대기: N=2 슬레이브가 **1 PollIntervalMs 안에 둘 다 갱신**(≈ N×트랜잭션 +
    대기1회). 슬레이브별 sleep으로 인한 N×PollIntervalMs 지연이 아님을 행위로 입증.
C4. 두 슬레이브 동시 핸드셰이크: 각각 Success + R_Seq==자기 C_Seq, 교차 0. **반복 ≥20회** 전부 성공(프레임
    인터리브 시 RSeqMismatch가 관측될 것 — 없음으로 버스 락 입증).
C5. 슬레이브 격리: 슬레이브 B에 InjectNoResponse/RFlag 지연 주입 시 B는 RFlagTimeout/OFFLINE(자기 상태),
    A는 Online 유지 + 핸드셰이크 Success. A의 Online이 B의 실패로 뒤집히지 않음.
C6. 절대규칙 #1: 두 슬레이브에 CellAssign/ClearR을 인터리브 투입해도 D4 RMW가 다른 비트(Ready 등)를 보존,
    C 영역이 교차 오염 0. 모든 쓰기 경로가 버스당 단일 큐 컨슈머 경유임을 코드 검사로 확인.
C7. 서로 다른 버스(엔드포인트 2개) 병렬 유지: 두 버스 각각 독립 마스터로 동시 ONLINE·핸드셰이크 성공(멀티
    포트 병렬 회귀 0).
C8. 회귀 0: `dotnet build backend/Wcs.sln` 클린(net10.0) + `dotnet test backend/Wcs.sln` GREEN. 취약 클래스
    (S5RSeqMismatch/S9/IT3a/IT4b/teardown)는 직렬/격리 재실행 ≥5회로 flake 아님을 귀속(단일 GREEN 불신).
C9. 스코프: git diff가 Wcs.PlcGateway + Wcs.Sim3ds + Wcs.Tests 안에만 존재. Wcs.Core/Wcs.Data/마이그레이션/
    frontend/Program.cs SorterRegistryFactory/appsettings 변경 0(불가피하면 근거 문서화 후 사용자 보고).
C10. 하드코딩 0: 신규 타이밍/포트/유닛 값이 전부 옵션/설정 주입(절대규칙 #7). teardown 결정성(Writer.TryComplete)
    유지 — 테스트 종료 시 행/크래시 0, 고아 Sim 프로세스 0.

────────────────────────────────────────────────────────────────────────
■ Verification Scenarios (per-type, mandatory)
  Detected type = Full-stack → Web/UI(해당 없음·사유) + Backend/API(내부 표면) + E2E 데이터플로.
────────────────────────────────────────────────────────────────────────

=== Web/UI ===
- 이 스프린트가 건드리는 각 화면의 기본 상태:
    N/A — Phase 1은 브라우저/프론트 표면을 건드리지 않는다. Modbus 마스터·폴러·큐·Sim3ds 서버측 로직과
    통합 테스트만 변경. /health JSON·모니터링 SignalR·SPA 무변경(회귀 대상일 뿐 변경 대상 아님).
- 스프린트가 도입하는 대체 상태 / 빈·에러 상태 / 다크모드 / 핵심 상호작용 흐름:
    N/A — 위와 동일(사유: UI 무접촉). Evaluator는 이 슬롯에 대해 브라우저 검증 대신 서버측 통합 테스트로
    대체함이 정당(변경 표면에 UI 없음).

=== Backend/API ===
- 이 스프린트가 건드리는 HTTP 엔드포인트(method + path):
    없음(0). Phase 1은 HTTP 표면을 변경하지 않는다. 변경되는 "API 표면"은 내부 게이트웨이/버스 계약과 Modbus
    와이어 동작이다(아래 대체 슬롯에서 검증). (참고: 기존 IF-05/09/10·/health·/hubs/monitor는 회귀 대상 —
    dotnet test 전 스위트 GREEN으로 불변 입증.)
- (대체) 내부 서비스/와이어 표면의 happy path (입력 → 기대 산출/동작):
    · 버스 폴 read: 한 엔드포인트·마스터 1개로 unitId=1,2 각각 FC03(D0~D6) → 두 슬레이브 스냅샷 각각 정상
      갱신, 둘 다 Online=true.
    · 쓰기 큐(버스 단위): PlcWrite(대상 unitId 포함) enqueue → 단일 컨슈머가 버스 락 안에서 그 슬레이브에
      FC06/16 + D4 RMW → 대상 슬레이브만 변경, 다른 슬레이브 무영향.
    · 핸드셰이크(슬레이브별): unitId=1과 unitId=2에 대해 각각 C 기입→R 에코→R_Seq==C_Seq→ClearR 성공.
- (대체) 관련 에러/경계 케이스:
    · 슬레이브 B FC03 타임아웃/무응답 → **B만** 연속 실패 누적 후 OFFLINE 전이(전이당 1회 이벤트), A는 영향 0.
    · 동시 핸드셰이크 프레임 교차 시도(버스 락 없으면) → R_Seq 교차/RSeqMismatch로 표출. 락 有 → 0건.
    · 같은 PortName에 두 슬레이브지만 마스터를 공유하지 못하는 (구)경로 = 포트 이중 Open 예외 → 재현 후 공유
      경로에서 소멸함을 입증(근본원인 해소 확인).
    · (범위 명시) **같은 슬레이브에 대한 동시 핸드셰이크**는 여전히 out-of-scope(기존 갭 —
      single-sorter-concurrent-handshake-gap): 버스 락은 슬레이브 간 프레임 교차만 막고, 한 슬레이브에 대한
      동시 IF-10 안전을 만들지 않는다(순차 dispatch 전제 유지). 시나리오 (b)는 **서로 다른 두 슬레이브**의 동시
      핸드셰이크다.

=== 최소 1개 E2E 데이터플로 (2계층 이상 횡단 — 흐름 서술) ===
아래는 전부 **TCP Sim3ds(멀티유닛) ↔ PlcGateway 버스** 통합 레벨(HTTP/DB 미경유 — Phase 1 경계):
 (a) 한 버스 두 슬레이브 ONLINE·독립 폴링:
     멀티유닛 Sim(한 엔드포인트, unitId=1,2) 기동 → 공유 버스 폴러 기동(마스터 1개) → 두 슬레이브 스냅샷이
     각각 Online·Ready 관측. 마스터/포트가 1개임을 입증(2대째 OFFLINE 재현 안 됨 = 근본원인 해소).
 (b) 동시 핸드셰이크 프레임 무교차:
     unitId=1과 unitId=2에 대한 핸드셰이크를 **동시 트리거** → 둘 다 Success, 각 R_Seq==자기 C_Seq, R_CellNo가
     각자 값. **≥20회 반복** 전부 성공(버스 락으로 프레임/R_Seq 교차 0). 락 제거 대조(음성 테스트, 선택)로
     교차가 실제로 발생함을 보이면 가점.
 (c) 슬레이브 격리(한 타임아웃이 다른 소터 OFFLINE 아님):
     슬레이브 B에 InjectNoResponse=true(또는 RFlag 지연) → B 핸드셰이크 RFlagTimeout / B OFFLINE 전이,
     동시에 A는 Online 유지 + 핸드셰이크 Success. A.Online이 B 실패로 false 되지 않음.
 (d) 서로 다른 버스 병렬(멀티 포트 회귀 0):
     엔드포인트 2개(버스 2개, 각 단일 슬레이브) 기동 → 각자 독립 마스터로 동시 ONLINE·핸드셰이크 성공(현행
     멀티포트 동작 보존).
 (e) 전 스위트 회귀 0:
     `dotnet test backend/Wcs.sln` GREEN. 취약 클래스(S5RSeqMismatch/S9/IT3a/IT4b/teardown 채널 경쟁)는
     **직렬 또는 격리 재실행 ≥5회**로 flake 아님을 귀속(단일 GREEN 신뢰 금지). 종료 시 행/크래시/고아 Sim 0.

> Planner self-check — Detected project type: Full-stack. Required scenario slots: 3 (Web/UI [N/A·사유], Backend/API [엔드포인트 없음+내부 와이어 표면 happy/error], E2E data-flow [a·b·c·d·e]). All slots filled: yes.

────────────────────────────────────────────────────────────────────────
■ Phased Plan (스프린트 분할 — 근거 포함)
────────────────────────────────────────────────────────────────────────
판단: **단일 스프린트로는 5-iteration 안에 위험하다 → 2 Phase로 분할.** 근거:
 - 변경 표면이 (1) 멀티유닛 Sim, (2) IModbusMaster/마스터 계층(공유 클라이언트+per-call unitId), (3) 하드닝된
   PlcPollingService의 폴 루프/락/쓰기 큐를 버스 단위로 승격, (4) SorterRegistryFactory의 PortName 그룹핑 +
   SorterConfig 스키마, (5) SorterBundleHandle을 공유 버스 위 슬레이브로 재배선, (6) 풀스택 E2E까지 6갈래.
   특히 (3)은 arming·기동 reconcile·OFFLINE 요약·teardown 채널 경쟁 등 다수 하드닝을 품고 있어 회귀 위험이
   높다. 6갈래를 한 스프린트에 묶으면 실패 3회/이터 5회 한도 초과 가능성이 크다.
 - 깨끗한 seam: (1)+(2)+(3) = 통신 계층 메커니즘(Wcs.PlcGateway+Wcs.Sim3ds+Tests)만으로 DI/DB/HTTP 없이
   독립 검증 가능. (4)+(5)+(6) = DI/레지스트리/설정/풀스택 결선. 이 경계로 자른다.

**Phase 1 (이 계약)** — 통신 계층 공유 버스 메커니즘 + Sim3ds 멀티유닛 + PlcGateway↔TCP-Sim3ds 통합 테스트.
  산출: 공유 마스터(버스당 1 Open) + per-call unitId + 버스 락 + 버스 폴 사이클(슬레이브별 독립 상태) + 버스
  단위 공유 쓰기 큐 + 멀티유닛 Sim + 시나리오 (a)~(e). DI/설정/DB/프론트 무접촉.

**Phase 2 (로드맵 — 별도 스프린트)** — DI/레지스트리/설정/풀스택 결선:
  - `Program.cs SorterRegistryFactory.StartAsync`: DB SORTER_3D를 **버스 키(RTU=PortName / TCP=host:port)로
    그룹핑** → 그룹당 버스 1개를 멤버 SorterBundleHandle이 공유(현재 소터당 마스터/큐 생성 L488–506 교체).
  - `SorterConfig`/appsettings `Sorters[]` 그룹핑 스키마(권장: 평면 배열 유지 + 동일 PortName 암묵 그룹핑,
    N=1 현행 동일 — OQ3). 같은 버스 멤버의 시리얼 파라미터 불일치 시 fail-loud(OQ4).
  - `SorterBundleHandle`: 공유 버스 위 자기 슬레이브(unitId)로 read/enqueue/handshake 라우팅.
  - 풀스택 E2E: `E2EWebApplicationFactory`를 확장해 **한 Sim 엔드포인트에 여러 unitId**(현재는 소터당 별도
    포트, L108–129) → 멀티소터-한버스 E2E. 멀티포트 병렬 E2E 회귀. 전 스위트 0회귀(SQL Server provider 검증은
    통신 계층 무관이나 E2E 팩토리 규약 유지 — 교훈 sqlserver-migration).
  - 문서: `docs/SPEC.md` §7-A L110 토폴로지 갱신(포트당 1대 → 공유 버스 가능), 필요시 CLAUDE.md 절대규칙 #1
    입도 주석.

────────────────────────────────────────────────────────────────────────
■ Parallel Modules (optional — Generator fan-out)
────────────────────────────────────────────────────────────────────────
2개 모듈이 서로 다른 프로젝트로 **공유 파일 쓰기 없음** → 병렬 가능(선택):
  - 모듈 A: `backend/src/Wcs.Sim3ds/*` — 멀티유닛 슬레이브.
  - 모듈 B: `backend/src/Wcs.PlcGateway/*` — 버스 공유 메커니즘(마스터/락/폴/큐).
Fan-in: `backend/tests/Wcs.Tests/MultiSorterSameBusTests.cs`(단일 작성자) — A·B 완료 후 통합 테스트 작성.
주의: A·B는 멀티유닛 와이어 계약(한 엔드포인트 다중 unitId 응답 형식)에 합의해야 fan-in 테스트가 성립 →
  fan-out 전에 그 계약(레지스터 뱅크 unitId 스코프)을 짧게 고정. 병렬 이득이 불확실하면 **기본 1 Generator
  순차**(A→B→C)도 정당(단일 개발자 흐름이 계약 합의를 자연히 보장). 워크트리 격리 시 base=현 브랜치 tip 수동
  지정(메모리 agent-worktree-stale-base).

────────────────────────────────────────────────────────────────────────
■ Evaluation Dimensions (optional — Evaluator expert pool)
────────────────────────────────────────────────────────────────────────
2개 차원 병렬 검증(APPROVED = 둘 다 PASS):
  - 차원 1 (functional-and-regression): 시나리오 (a)(d)(e) + C1~C3·C7~C10. 기능 정합 + 전 스위트 0회귀
    (취약 클래스 ≥5회 귀속) + 스코프/하드코딩/teardown.
  - 차원 2 (concurrency-and-frame-integrity): 시나리오 (b)(c) + C4~C6. 버스 락에 의한 프레임/R_Seq 무교차
    (≥20회 반복), 슬레이브별 OFFLINE 독립, 절대규칙 #1(단일 큐·D4 RMW 원자성) 코드+행위 검사. (근거: 메모리
    single-sorter-concurrent-handshake-gap·evaluator-concurrency-blindspot — 전이 원자성/핸들러 예외격리/
    switch default를 코드 직접 검사로 확인.)

────────────────────────────────────────────────────────────────────────
■ Open Questions (사용자 게이트 필요 — 기본값 제시, 고위험 포크 표시)
────────────────────────────────────────────────────────────────────────
OQ1 [고위험 포크] 스프린트 분할 yes/no. **기본 제안: YES(2 Phase).** 이 계약은 Phase 1(통신 계층 메커니즘
    + Sim 멀티유닛 + 통합 테스트)만. Phase 2(DI/설정/풀스택 E2E)는 후속. 단일 스프린트 강행 시 6갈래 동시로
    5-iteration 초과 위험. → 승인 요청.

OQ2 [고위험 포크] TCP 공유 버스가 "테스트 vehicle"인가 "운영 경로"인가. 현장 목표는 **RTU 물리 포트 1개 공유**.
    테스트는 TCP Sim3ds(한 host:port에 다중 unitId). 다만 **serial-to-TCP 게이트웨이**가 여러 RTU 슬레이브를
    한 TCP 엔드포인트로 노출하는 실 배치가 있다면 TCP 공유도 운영 경로가 된다. **기본 가정: TCP=테스트 vehicle,
    RTU=운영 목표.** 실제 TCP 다중슬레이브 배치 계획이 있으면 알려주세요(설계엔 영향 적으나 검증 범위/문서에 반영).

OQ3 [Phase 2 관련·조기 확인 유용] appsettings `Sorters[]` 그룹핑 스키마. **기본 제안: 평면 배열 유지 +
    동일 PortName(RTU)/host:port(TCP) 암묵 그룹핑**(스키마 무파괴, N=1 현행 바이트 동일). 대안: 명시 `Buses[]`
    중첩. Phase 1은 스키마 무접촉이므로 지금 확정 불요이나, Phase 1 버스 추상화 형태에 영향 → 방향만 확인.

OQ4 [Phase 2 관련] 같은 버스(PortName) 멤버의 시리얼 파라미터(BaudRate/Parity/StopBits) 불일치 처리.
    **기본 제안: fail-loud(기동 거부)** — 물리적으로 한 버스는 파라미터가 하나여야 하므로. 확인.

OQ5 [문서 정합] `docs/SPEC.md` §7-A L110 "포트당 소터 1대(다중 슬레이브 경합 없음)"는 확정 토폴로지로 기술됨.
    본 리팩터가 이를 변경. **기본 제안: Phase 2에서 SPEC §7-A 갱신**(포트당 1대 → 공유 버스 가능, unitId 구분).
    Phase 1에서 문서를 건드리지 않는 것에 동의하는지(문서-코드 잠시 발산 허용) 확인.

OQ6 [범위 확인] 같은 슬레이브에 대한 동시 핸드셰이크는 **out-of-scope**(기존 갭). 버스 락은 슬레이브 간 프레임
    교차만 차단하고, 한 슬레이브에 대한 동시 IF-10 안전(순차 dispatch 전제)을 신설하지 않는다. 동의 확인.

────────────────────────────────────────────────────────────────────────
■ 게이트 확정 (사용자 2026-07-16)
────────────────────────────────────────────────────────────────────────
- OQ1 = **YES(2-Phase)**. 이 계약 = Phase 1(통신 계층 메커니즘 + Sim 멀티유닛 + 게이트웨이↔TCP-Sim 통합
  테스트)만. Phase 2(DI 그룹핑·appsettings 스키마·풀스택 E2E·SPEC §7-A 갱신)는 후속 스프린트.
- OQ2 = **TCP=테스트 vehicle, RTU=운영 목표**. TCP 다중슬레이브(한 host:port 여러 unitId)는 검증 수단.
  실 배치는 RTU 물리 포트 1개 공유. 설계는 두 전송 모두 지원하되 통합 테스트는 TCP Sim3ds로만.
- OQ3 = **평면 Sorters[] 유지 + 동일 PortName(RTU)/host:port(TCP) 암묵 그룹핑**(스키마 무파괴). Phase 2 확정.
- OQ4 = **fail-loud**: 같은 버스 멤버의 시리얼 파라미터(baud/parity/stopbits) 불일치 시 기동 거부.
- OQ5 = **SPEC §7-A 갱신은 Phase 2**. Phase 1은 문서-코드 잠시 발산 허용(코드 무접촉).
- OQ6 = **같은 슬레이브 동시 핸드셰이크 out-of-scope 유지**(버스 락은 슬레이브 간 프레임 교차만 차단).

■ 팀 구성 (orchestrator 결정)
- Generator = **단일 순차(A→B→C)**. A(Sim 멀티유닛)·B(게이트웨이 버스)가 멀티유닛 와이어 계약에 합의해야
  fan-in 통합테스트(C)가 성립 → 단일 개발자 흐름이 합의를 자연 보장. 워크트리 fan-in 리스크 회피.
- Evaluator = **단일**(2명 동시 dotnet test 충돌 회피 — 고아 Sim/파일잠금/parallel-load flake). 단, 계약의
  Evaluation Dimensions 2축(functional-and-regression / concurrency-and-frame-integrity)을 **한 Evaluator가
  둘 다** 수행하되 동시성·프레임무결성을 1급 축으로 **코드 직접검사**(evaluator-concurrency-blindspot).

(끝 — 이 계약은 Phase 1. Generator↔Evaluator 루프 진입.)
