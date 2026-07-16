[Sprint Contract] — S-MULTISORTER-SHARED-BUS (Phase 2)

브랜치: feat/multisorter-shared-bus-p2 (base origin/develop = 36a47bc — Phase 1(PR #68) 병합 포함)
작성: 2026-07-16 (Planner Subagent) · 트리거: Phase 1 계약 로드맵 "Phase 2 — DI/레지스트리/설정/풀스택 결선"

────────────────────────────────────────────────────────────────────────
■ 배경 (실측 근거 — 실제 코드 file:line, 파일 직독으로 확정)
────────────────────────────────────────────────────────────────────────

Phase 1(PR #68, 커밋 70f98da)이 **통신 계층 메커니즘**을 이미 배달했다(코드 직독 확정):
- `backend/src/Wcs.PlcGateway/ModbusBus.cs` — 한 물리 버스 조정자. ctor(ISharedModbusConnection, PlcGatewayOptions, ILogger?).
  `AddSlave(byte unitId, ILogger?)`(L74)가 `BusSlaveMaster` 어댑터로 결선한 `PlcPollingService`를 생성·반환하고
  `ConfigureForBus(this, unitId, _busLock)`(L84)로 버스 공유 락에 붙인다. `StartAsync`(L98)가 **단일 폴 루프
  (주기당 1회 대기 후 멤버 순회, L110-124) + 단일 쓰기 컨슈머(L127-143)** 를 구동. `StopAsync`(L145)는
  `_writeCh.Writer.TryComplete()` 먼저(L150) → cancel → 폴/쓰기 태스크 await → 멤버 `StopAsync` → `_conn.Disconnect()`(L167).
  `BusKey`(L60)·`Slaves`(L63)·`Slave(unitId)`(L66) 노출.
- `backend/src/Wcs.PlcGateway/Modbus/SharedModbusConnection.cs` — `ISharedModbusConnection`(버스당 1 Open, unitId per-call).
  `SharedTcpModbusConnection`(ctor host,port,readTimeoutMs,log — BusKey=`$"{host}:{port}"` L65) /
  `SharedRtuModbusConnection`(ctor portName,baud,parity,stopBits,readTimeoutMs,writeTimeoutMs,endianness,log — BusKey=portName L143;
  주입 fakePort ctor L147). `BusSlaveMaster`(L206)가 고정 unitId를 실어 기존 `IModbusMaster`로 노출(시그니처 무변경).
  ⚠ **RTU 공유 연결에 DataBits 파라미터 없음, endianness 고정 BigEndian**(L128 기본) — 아래 OQ10.
- `backend/src/Wcs.PlcGateway/PlcGateway.cs` — `PlcPollingService`가 `PollCycleAsync`(L310)/`HandleWriteAsync`(L541)로
  추출됨. `ConfigureForBus`(L233): _clientLock을 버스 공유 락으로 대체(L238-240), `EnqueueAsync`(L221)는 버스 공유 큐로 라우팅.
  버스 멤버는 `StartAsync`(L245)가 즉시 return(자체 루프 없음). 예외 분류(L392-406): 버스 모드 TimeoutException=SOFT(그 슬레이브만
  OFFLINE, 공유 포트 미절단), 소켓/IO=하드. per-slave 상태(_failures/_prevRFlag/_prevSnap/_startupReconciled/_latest/_online).

Phase 1은 **게이트웨이 단위 테스트**(`backend/tests/Wcs.Tests/MultiSorterSameBusTests.cs`)에서만 공유 버스를 입증한다
(TCP 멀티유닛 Sim ↔ ModbusBus 직결). **DI/레지스트리/설정/풀스택은 아직 공유 버스를 쓰지 않는다** — 그것이 이 Phase 2다.

Phase 2가 바꿀 실제 코드(실측):

1. **`backend/src/Wcs.Api/Program.cs` `SorterRegistryFactory.StartAsync`(L433-592)** 가 DB SORTER_3D destination마다
   **독립 번들을 소터당 1세트씩** 만든다(L467 foreach):
   - `var master = ModbusMasterFactory.Create(transportOpt, logFac);`(L488) — 소터당 독립 마스터(자기 포트 Open).
   - `var writeQueue = new PlcWriteQueue();`(L491) — 소터당 독립 큐.
   - `var polling = new PlcPollingService(gwOpt, writeQueue, master, ...);`(L494) — 자체 폴 루프.
   - `var handshake = new HandshakeOrchestrator(polling, gwOpt, ...);`(L501).
   - `bundle = new SorterBundleHandle(dest.Id, dest.ChuteNo, polling, handshake, writeQueue);`(L506).
   - 이후 관측/OFFLINE 구독(L523-583) → `await bundle.StartPollingAsync(...);`(L585)로 소터별 폴러 기동.
   `StopAsync`(L594-614)는 `AllBundles`를 순회해 `bundle.StopPollingAsync()` 호출.
   → **같은 버스 키(같은 PortName / 같은 host:port)를 가진 두 소터를 넣어도 각자 마스터를 만들어 포트를 이중 Open**
     → Phase 1이 해소하려던 "2대째 OFFLINE"이 DI 경로에선 아직 발생한다. 이 루프를 **버스 키 그룹핑**으로 교체하는 것이 핵심.

2. **`SorterConfig`(Program.cs L653-698)** 는 그룹핑에 필요한 필드를 **이미 전부 갖고 있다**:
   `ChuteNo`(L657)·`Transport`(L661)·`Host`/`Port`(L664-665)·`PortName`(L668)·`BaudRate`/`Parity`/`StopBits`
   (L669-671)·`ReadTimeoutMs`/`WriteTimeoutMs`(L672-673)·**`UnitId`(byte, L674)**·`PollIntervalMs`(L677)·
   `OfflineAfterFailures`(L678)·`Timing` 오버라이드(L682). → **버스 키·unitId는 config에 이미 존재** → DB 스키마
   변경 불요(마이그레이션 0). ⚠ **`SorterConfig`에 DataBits 필드 없음**(OQ10). ⚠ `UnitId` 기본값=1 → 같은 버스에
   두 소터를 넣는데 둘 다 UnitId=1이면 충돌(OQ11).

3. **`SorterBundleHandle`(Infrastructure/SorterGatewayRegistry.cs L18-118)** 는 소터당 독립 인스턴스 전제로 설계됨.
   `StopPollingAsync`(L83-88)가 `_writeQueue?.Writer.TryComplete()`(L86) 후 `_polling.StopAsync()`(L87). 스냅샷
   `Latest`(L54)·enqueue 래퍼(L57-71)·핸드셰이크(L74)·구독 훅(L98-117) 전부 멤버 `PlcPollingService`/`HandshakeOrchestrator`
   에 위임 — **공유 버스 멤버가 되어도 per-slave 이벤트/스냅샷은 그대로 동작**(멤버 PlcPollingService가 자기 이벤트 발화).
   ⚠ **생명주기 위험(실측)**: 공유 버스에서 `bundle.StopPollingAsync()`를 멤버마다 부르면 (a) 공유 쓰기 큐에
   `TryComplete`를 걸어 형제 슬레이브의 쓰기를 조기 종료시키고, (b) `_polling.StopAsync()` → `_master.Disconnect()`
   (BusSlaveMaster.Disconnect → **공유 연결 Disconnect**, SharedModbusConnection.cs L223)로 형제까지 끊는다.
   → Phase 2는 **버스 단위 생명주기**(버스당 `ModbusBus.StartAsync`/`StopAsync` 1회)로 결선해야 하며, 버스 멤버 번들은
     공유 큐를 소유하지 않아야(writeQueue=null) 조기 teardown을 피한다.

4. **`MultiSorterGatewayRegistry`(SorterGatewayRegistry.cs L152-173)** 는 destId→bundle 불변 딕셔너리. `AllBundles`(L171)
   가 전 소터 번들을 노출. **함정6(Program.cs L211-216)**: `MonitorRelayService`는 `SorterRegistryFactory` 등록(L154)
   **이후**에 등록돼 StartAsync가 나중에 돌게 함 → 구독 시점(MonitorRelayService.StartAsync L69 `foreach AllBundles`)에
   AllBundles 유효. relay는 `bundle.SubscribeRegisterChange/Online/Offline`(L74-82)로 **per-slave 이벤트**를 구독 →
   공유 버스 멤버도 그대로 발화하므로 relay 결선 불변(회귀 대상). `/health`(L319-325)도 AllBundles per-slave Latest 순회.

5. **E2E 인프라 `E2EWebApplicationFactory`(backend/tests/Wcs.Tests/E2E/E2EInfrastructure.cs)** 는 **소터마다 별도 포트로
   SimServer 1대**를 띄운다: `StartSimsAsync`(L104-129)가 chuteNo마다 `GetFreePort()`+새 `SimServer` 1대(L111·L122·L127),
   `ConfigureWebHost`(L177-187)가 `Sorters:{i}:Port`를 슬롯별 **다른 포트**로 바인딩(그래서 지금은 전부 다른 버스 키
   = 멀티 포트). **`Sorters:{i}:UnitId`는 바인딩하지 않음** → 전부 기본 UnitId=1(포트가 달라 충돌 없음). → 멀티소터-한버스
   (한 host:port, 두 unitId)를 E2E로 세우려면 **한 멀티유닛 SimServer + 같은 Host:Port + 서로 다른 UnitId** 배선이 신규.
   Phase 1이 배달한 멀티유닛 Sim ctor `new SimServer(opt, IReadOnlyList<byte> unitIds, ...)`(SimServer.cs L115) + `.Unit(byte)`
   (L155)가 이 배선의 재료(이미 존재 — 소비만).

6. **config → ISharedModbusConnection 팩토리는 아직 없다**(grep 확정: `SharedTcpModbusConnection`/`SharedRtuModbusConnection`/
   `new ModbusBus` 참조는 ModbusBus.cs·SharedModbusConnection.cs·Phase 1 테스트뿐, Wcs.Api 0건). 기존
   `ModbusMasterFactory.Create`(ModbusMasterFactory.cs L73)는 **소터당 독립 마스터**를 만드는 구경로다. Phase 2는 버스 키 +
   시리얼/TCP 파라미터 → `ISharedModbusConnection` 1개를 만드는 **작은 진입점**이 필요(Phase 1 API 소비 — 아래 Scope에서 명시).

7. **`ModbusBus`는 버스당 단일 `PlcGatewayOptions`를 쓴다**(ModbusBus.cs L114 폴 대기 `_opt.PollIntervalMs`; `AddSlave`가
   멤버 `PlcPollingService`를 `_opt`로 생성 L83). 즉 **현재 시그니처로는 같은 버스 멤버가 서로 다른 per-sorter Timing/
   PollInterval 오버라이드를 가질 수 없다**(전 멤버가 버스 opt 공유). N=1 버스는 그 소터의 gwOpt로 버스를 만들면 되어 무해하나,
   N≥2 동일버스 멤버의 오버라이드 상이 시 처리 결정 필요(아래 OQ9 — 이 Phase의 고위험 포크).

문서 정합: `docs/SPEC.md` §7-A L110은 여전히 "소터별 독립 포트(토폴로지 확정): 포트당 소터 1대, 다중 슬레이브 경합 없음"
을 확정 토폴로지로 기술 → 이 Phase가 갱신(OQ5 게이트 = Phase 2 산출물).

────────────────────────────────────────────────────────────────────────
■ Goal
────────────────────────────────────────────────────────────────────────
Phase 1의 버스 메커니즘(ModbusBus + SharedModbusConnection + BusSlaveMaster)을 **DI/레지스트리/설정/풀스택 계층에 결선**해,
appsettings `Sorters[]`에 **같은 버스 키**(RTU=PortName / TCP=host:port)로 두 소터를 구성하면 그 둘이 **하나의 ModbusBus
(하나의 SharedModbusConnection, 포트/마스터 1개)** 위에서 unitId로 구분되어 **엔드투엔드(HTTP·핸드셰이크·SignalR)로 운영**되게
한다. 서로 다른 버스 키는 각자 독립 ModbusBus로 병렬 유지(멀티 포트 회귀 0). 버스 멤버 1개(N=1)는 현행 단일 소터 동작과 동치
(회귀 0, N=1 config 바이트 동일). 같은 버스 멤버의 시리얼 파라미터 불일치는 기동 거부(fail-loud, OQ4).

이 Phase는 **통신 계층 코드(Phase 1 산출물)를 재작성하지 않고 소비**한다 — 필요한 경우 Phase 1 API에 작은 가산(예: 버스 생성
진입점, per-slave opt 수용)만 허용하며 아래에서 명시한다.

절대규칙 #1(단일 큐 직렬화)은 **의미 불변, 입도만 버스 단위**: 쓰기는 여전히 버스당 단일 큐 컨슈머 하나로만 직렬화(ModbusBus가
소유). API/핸들러의 Modbus 직접 호출 금지 유지. D4 RMW 안전·TgtFloor fresh-read 가드는 멤버 PlcPollingService에 그대로 보존.

────────────────────────────────────────────────────────────────────────
■ Implementation Scope (Generator가 할 일 — 파일 지정)
────────────────────────────────────────────────────────────────────────
기법 상세(그룹핑 자료구조·팩토리 배치)는 Generator 재량. 아래 불변식·요구는 계약으로 강제한다.

A. Wcs.Api — 레지스트리 버스 키 그룹핑 (핵심)
   - 대상: `backend/src/Wcs.Api/Program.cs` `SorterRegistryFactory.StartAsync`(L433-592)·`StopAsync`(L594-614)·
     `SorterConfig`/바인딩(L653-698). 필요 시 DI 등록(L149-164) 소폭.
   - 요구 불변식:
     (A1) **버스 키 그룹핑**: DB SORTER_3D destination을 ChuteNo로 SorterConfig에 매칭한 뒤, **버스 키**(Transport=Rtu → PortName /
          Transport=Tcp → `Host:Port`)로 그룹핑한다. 그룹당 `ISharedModbusConnection` 1개 + `ModbusBus` 1개를 만들고, 그룹의 각
          멤버를 `bus.AddSlave(cfg.UnitId, ...)` 로 붙인다(그 반환 PlcPollingService를 HandshakeOrchestrator·SorterBundleHandle에
          그대로 감쌈). 그룹당 버스를 **1회** `bus.StartAsync()` 한다(멤버별 StartPollingAsync 아님).
     (A2) **서로 다른 버스 키 = 독립 병렬**: 다른 버스 키는 각자 독립 ModbusBus(독립 연결/포트) — 현행 멀티 포트 동작 보존.
     (A3) **N=1 하위호환(바이트 동일)**: 버스 멤버가 1개면 현행 단일 소터와 동작 동치. 기본 appsettings(ChuteNo=1·RTU·COM1·
          UnitId=1)는 **버스 1개·멤버 1개** = 현행과 동일하게 기동(config 무변경, N=1 바이트 동일).
     (A4) **fail-loud (OQ4)**: 같은 버스 키 멤버의 시리얼 파라미터(BaudRate/Parity/StopBits — SorterConfig에 존재하는 값) 불일치 시
          **그 버스 기동 거부**(명확한 InvalidOperationException + LogCritical). SORTER_3D인데 Sorters[] 항목 없음은 현행 fail-loud
          (L470-479) 보존. 같은 버스에 중복 UnitId(OQ11)도 fail-loud(ModbusBus.AddSlave가 이미 throw — 레지스트리가 명확 메시지로 표면화).
     (A5) **생명주기 = 버스 단위**: `StopAsync`는 **버스마다 `ModbusBus.StopAsync()`를 1회** 호출(내부에서 Writer.TryComplete +
          멤버 정지 + 공유 연결 Disconnect 결정 종료). **버스 멤버 번들에 대해 개별 `bundle.StopPollingAsync()`로 공유 큐/연결을
          조기 teardown하지 않는다**(형제 슬레이브 보호 — 배경 3의 위험). 이를 위해 버스 멤버 번들은 공유 큐를 소유하지 않도록
          구성(예: `SorterBundleHandle(..., writeQueue: null)` — TryComplete 미발생). 레지스트리는 destId→bundle 딕셔너리와 **버스
          목록**을 함께 보유.
     (A6) **관측/OFFLINE/relay 결선 보존**: per-bundle 구독(L523-583)·MonitorRelay 구독(함정6, L211-216)·`/health`(L319)은
          멤버 PlcPollingService per-slave 이벤트에 그대로 붙는다. AllBundles는 StartAsync 완료 후 채워짐(불변). 구독은 버스
          StartAsync **이전**에 붙여 첫 폴 포착(현행 L516 주석 취지 보존).
   - 허용된 작은 가산(호출): 버스 키 + 파라미터 → `ISharedModbusConnection` 생성 진입점. 배치 자유(Wcs.Api 내부 헬퍼 권장;
     Wcs.PlcGateway에 두려면 팩토리 1개 가산 허용 — 아래 B에서 조건 명시). 기존 `ModbusMasterFactory`·`PlcPollingHostedAdapter`는
     **삭제 금지**(M2 테스트 참조 — 미사용이 되어도 유지).

B. Wcs.PlcGateway — Phase 1 API 소비(원칙: 재작성 0). 작은 가산만 허용, 반드시 호출:
   - (B-opt) OQ9 확정에 따라 `ModbusBus.AddSlave`가 **per-slave PlcGatewayOptions**를 받도록 가산(멤버별 handshake Timing 오버라이드
     보존)하는 것을 **허용**하되, 이는 Phase 1 시그니처 확장이므로 (i) 기존 시그니처/오버로드 하위호환 유지, (ii) 커밋 메시지·PR에
     명시. 폴 대기 cadence(L114)는 본질적으로 버스 단위 1개이므로 그룹의 (일치하는) PollIntervalMs를 쓴다.
   - (B-fac) 버스 연결 팩토리를 Wcs.PlcGateway에 두는 경우: `PlcTransportOptions`(또는 동등) → `ISharedModbusConnection` 1개
     생성 정적 헬퍼 1개 가산 허용(BusKey 규칙은 SharedXxxConnection과 일치 — TCP `host:port`, RTU `portName`).
   - 그 외 ModbusBus/SharedModbusConnection/BusSlaveMaster/PlcPollingService 본문 **의미 변경 금지**.

C. appsettings — 스키마 가산(무파괴, N=1 바이트 동일)
   - 대상: `backend/src/Wcs.Api/appsettings*.json`의 `Sorters[]`. OQ3 확정대로 **평면 배열 유지 + 동일 버스 키 암묵 그룹핑**.
     스키마 파괴 금지. 현행 N=1 항목은 **바이트 동일**(변경 0). 필요하면 주석·둘째 소터 예시만 추가(선택). 실 현장 COM1/UnitId
     값은 건드리지 않는다.

D. Wcs.Tests — 풀스택 멀티소터-한버스 E2E (fan-in)
   - 대상: `backend/tests/Wcs.Tests/E2E/E2EInfrastructure.cs`(멀티유닛 Sim 모드 확장) + 신규 E2E 테스트 파일(예:
     `E2EGroupJ_SharedBusTests.cs`). 기존 E2E 테스트 무단 변경 금지(멀티 포트 회귀 대조 기준).
   - 요구:
     (D1) **멀티유닛 Sim 엔드포인트 모드**: E2EWebApplicationFactory가 **한 포트에 멀티유닛 SimServer 1대**(`new SimServer(opt,
          byte[] unitIds, ...)`)를 세우고, 그 포트를 공유하는 두 SORTER_3D(서로 다른 ChuteNo·서로 다른 UnitId)를 시드·config
          바인딩할 수 있어야 한다. `ConfigureWebHost`가 그 두 슬롯에 **같은 Host:Port + 서로 다른 `Sorters:{i}:UnitId`**를 바인딩
          (`Sorters:{i}:UnitId` 신규 바인딩 필요 — 배경 5). 전송=Tcp(테스트 vehicle — OQ2), 실 COM1/RTU 금지.
     (D2) **기존 멀티 포트 E2E 보존**: 현행 `StartSimsAsync`(소터당 별도 포트) 경로는 회귀 0(다른 버스 키 병렬 — A2 입증). 신규
          멀티유닛 모드는 **가산**(기본 경로 무변경).
     (D3) **신규 풀스택 시나리오**: 아래 Verification E2E (a)~(c). HTTP(IF-05/09/10 관련 경로)·핸드셰이크·SignalR relay가 한 공유
          버스의 두 소터 각각에 대해 동작함을 실 EF DB(named in-memory SQLite, 인스턴스별 Guid) + FakeChuteState 수신으로 입증.
     (D4) **결정성**(교훈): 고정 sleep 금지, 조건 폴링(WaitUntil), baseline(Online) 확립 후 관찰, 비동기 로그는 출현 대기 후 캡처.
          teardown 결정성(ModbusBus/SorterBundleHandle Writer.TryComplete 경로 보존). 빌드 전 고아 `Wcs.Sim3ds.exe` kill(파일잠금 방지).

E. 문서 — SPEC §7-A 갱신 (OQ5)
   - 대상: `docs/SPEC.md` §7-A L110. "포트당 소터 1대(다중 슬레이브 경합 없음)" → "**소터별 독립 버스가 기본이되, 같은 버스 키
     (RTU=PortName / TCP=host:port)로 여러 소터를 unitId로 구분해 한 물리 버스에 공유 가능**(Phase 1 메커니즘 + Phase 2 결선). 버스
     단위 단일 큐·버스 락으로 프레임 무교차, 슬레이브별 OFFLINE 독립. 같은 버스 멤버 시리얼 파라미터 불일치·중복 unitId는 fail-loud."
     로 갱신. CLAUDE.md 절대규칙 #1 입도 주석은 선택(코드 정합 우선).

범위 밖(무접촉 — 근거):
   - `backend/src/Wcs.Core`(순수 판정 — 절대규칙 #8). 변경 필요 시 근거 제시하고 사용자 보고.
   - `backend/src/Wcs.Data` 및 **마이그레이션**(이 Phase는 config/wiring — DB 스키마 아님. 목표 0파일. UnitId는 SorterConfig에
     이미 존재 → DB 컬럼 불요. 부득이 DB 컬럼이 필요하면 **OQ7로 승격**하고 사용자 보고).
   - 프론트엔드(0파일 — 브라우저 UI 변경 없음). 실 PLC/실 DB/사용자 로컬(절대 무접촉).
   - Phase 1 통신 계층 본문 재작성(소비만 — B의 명시된 작은 가산 예외).

────────────────────────────────────────────────────────────────────────
■ Constraints (절대규칙 보존 + Phase 2 특유)
────────────────────────────────────────────────────────────────────────
- 절대규칙 #1(단일 큐 직렬화): 의미 불변·입도만 버스 단위. 쓰기는 버스당 단일 큐 컨슈머(ModbusBus)로만. API/컨트롤러/서비스/
  핸드셰이크의 Modbus 직접 호출 0. D4 RMW read+write는 버스 락 단일 임계구역(멤버 ProcessWriteAsync — 의미 보존).
- 절대규칙 #2/#3(TgtFloor): SetTgtFloor fresh-read 가드·WCS 미클리어 — 멤버 PlcPollingService 로직 보존.
- 절대규칙 #4/#5(Ready 의미·OFFLINE은 WCS 판단): OFFLINE 판단 **슬레이브별 독립**. 한 슬레이브 실패가 형제 Online을
  뒤집지 않음(버스 모드 soft/hard 분류 보존). DI 결선 후에도 이 독립성이 유지돼야 함(코드 검사 + E2E).
- 절대규칙 #7(타이밍 외부화·하드코딩 0): PollIntervalMs·RFlag*·OfflineAfterFailures·타임아웃·재시도 전부 설정 주입. 신규 상수
  하드코딩 0. 버스 그룹핑/폴 cadence도 설정 기반. 테스트는 단축값을 config로 주입.
- 절대규칙 #8(판정=Wcs.Core 순수): 무접촉.
- 생명주기 안전(배경 3): 공유 버스 teardown은 **버스 단위 1회**. 형제 슬레이브를 조기 종료시키는 per-멤버 teardown 금지.
  Writer.TryComplete는 ModbusBus.StopAsync(L150)가 소유 — 버스 멤버 번들은 공유 큐 미소유(writeQueue=null).
- E2E DB regime(교훈 sqlserver-migration): 이 Phase는 DB 스키마 무변경이므로 SQL Server provider 검증 갱신은 불요이나, E2E
  팩토리의 provider 결선 규약(`UseSetting("Database:Provider","Sqlite")` L163 + named in-memory anchor)은 그대로 보존.
- 회귀 귀속(교훈 s9-flake / e2e-parallel-load / sim-timeline / testhost-teardown-channel-race): full-suite 1회 GREEN 불신.
  취약 클래스(S5RSeqMismatch·S9·IT3a·IT4b·teardown 채널 경쟁·E2E 그룹)는 **직렬/격리 재실행 ≥5회**로 flake 아님을 귀속.
  무거운 실 Sim E2E가 기본 병렬로 저빈도 flake를 발현시킬 수 있음 → 신규 E2E는 결정적 대기로. 빌드 전 고아 Sim kill.
- 워크트리 격리(교훈 agent-worktree-stale-base): 병렬로 돌릴 경우 수동 `git worktree add`로 base=현 브랜치 tip
  (feat/multisorter-shared-bus-p2) 명시. 커밋 직전 `git rev-parse --abbrev-ref HEAD` 확인, develop 직접 커밋 0.

────────────────────────────────────────────────────────────────────────
■ Detected Project Type: Full-stack
  (레포 신호: frontend/ SPA + backend/ ASP.NET Core + EF + SignalR = Full-stack.
   단, THIS Phase의 변경 표면은 백엔드 DI/설정/HTTP 결선 + 풀스택 E2E — 브라우저 UI 변경 0.)
────────────────────────────────────────────────────────────────────────

────────────────────────────────────────────────────────────────────────
■ Evaluation Criteria (가중치 — Evaluator 판정)
────────────────────────────────────────────────────────────────────────
1. (30%) 공유 버스 엔드투엔드: 같은 버스 키의 두 SORTER_3D가 **하나의 ModbusBus/SharedModbusConnection(포트/마스터 1개)** 위에서
   둘 다 Online·독립 폴링하고, HTTP·핸드셰이크·SignalR 경로가 두 소터 각각에 동작(풀스택 E2E 입증).
2. (20%) 멀티 포트 병렬 회귀 0: 서로 다른 버스 키(엔드포인트 2개)는 각자 독립 ModbusBus로 동시 Online·핸드셰이크 성공. 기존
   멀티 포트 E2E GREEN 유지.
3. (15%) fail-loud(OQ4): 같은 버스 키 멤버의 시리얼 파라미터 불일치 시 그 버스 기동 거부(명확 에러). 중복 UnitId·Sorters[] 항목
   누락도 fail-loud. (기동 거부 테스트로 입증.)
4. (15%) 하위호환 N=1: 버스 멤버 1개 = 현행 단일 소터 동작 동치. 기본 appsettings 바이트 동일. 기존 전 스위트(단일 소터 전제
   테스트) GREEN, 취약 클래스 ≥5회 귀속.
5. (10%) 생명주기/격리: 버스 단위 teardown(형제 조기 종료 0)·슬레이브별 OFFLINE 독립이 DI 결선 후에도 유지. 종료 시 행/크래시/
   고아 Sim 0. (코드 검사 + E2E.)
6. (5%) 절대규칙 #1: 공유 버스 쓰기가 버스당 단일 큐 컨슈머 경유, Modbus 직접 호출 0(코드 검사).
7. (5%) 스코프/문서: git diff가 Wcs.Api + appsettings + Wcs.Tests(+ 명시된 Wcs.PlcGateway 작은 가산) + docs/SPEC.md에만.
   Wcs.Core/Wcs.Data/마이그레이션/frontend 변경 0. SPEC §7-A 갱신. 하드코딩 타이밍 0.

────────────────────────────────────────────────────────────────────────
■ Completion Conditions (AND — 전부 충족해야 PASS · 수치/행위 기준)
────────────────────────────────────────────────────────────────────────
C1. **두 same-bus-key 소터가 하나의 ModbusBus/SharedModbusConnection 위에서 엔드투엔드로 동작**: 멀티유닛 TCP Sim(한 포트,
    unitId 2개) + 같은 Host:Port config 2항목 → 두 SORTER_3D 모두 Online, 각각 IF-05/IF-10 경로 + 핸드셰이크 Success
    (R_Seq==자기 C_Seq) + SignalR relay가 두 소터 워드 전이를 방출. 공유 연결(마스터/포트)이 **1개**임을 구성/관측으로 입증.
C2. **서로 다른 버스 키는 병렬**: 엔드포인트 2개(포트 2개, 각 단일 슬레이브) → 각자 독립 ModbusBus로 동시 Online·핸드셰이크
    성공. 기존 멀티 포트 E2E(E2EGroup* 다중 소터 케이스) GREEN.
C3. **fail-loud(OQ4)**: 같은 버스 키 멤버 2개의 BaudRate(또는 Parity/StopBits) 불일치 config → 기동이 명확한 예외로 거부
    (그 버스). 중복 UnitId(같은 버스) → 기동 거부. SORTER_3D인데 Sorters[] 항목 없음 → 기동 거부(현행 보존).
C4. **N=1 하위호환**: 기본 appsettings(N=1) 바이트 동일. 단일 소터 전제 기존 테스트(ScenarioTests·RcsPushTests·E2E 단일 소터
    등) 무변경 GREEN. 버스 멤버 1개 경로가 현행 폴/재연결/OFFLINE/arming/teardown 의미 보존.
C5. **생명주기/격리**: 호스트 종료 시 버스마다 ModbusBus.StopAsync 1회로 결정 종료(행/크래시 0, 고아 Sim 0). 공유 버스 한
    슬레이브 OFFLINE/타임아웃이 형제 Online·핸드셰이크에 영향 0(E2E로 입증). per-멤버 조기 teardown로 형제 쓰기/연결이
    끊기지 않음(코드 검사).
C6. **절대규칙 #1**: 공유 버스 모든 쓰기가 버스당 단일 큐 컨슈머 경유(코드 검사). D4 RMW·TgtFloor fresh-read 가드 멤버별 보존.
C7. **회귀 0**: `dotnet build backend/Wcs.sln` 클린(net10.0) + `dotnet test backend/Wcs.sln` GREEN. 취약 클래스(S5RSeqMismatch/
    S9/IT3a/IT4b/teardown/신규 E2E)는 직렬/격리 재실행 **≥5회**로 flake 아님 귀속(단일 GREEN 불신).
C8. **스코프**: git diff가 `Wcs.Api`(Program.cs + Infrastructure) + `appsettings*.json` + `Wcs.Tests`(E2E infra + 신규 테스트)
    + `docs/SPEC.md` (+ OQ9/팩토리 확정 시 명시된 `Wcs.PlcGateway` 작은 가산)에만 존재. **Wcs.Core/Wcs.Data/마이그레이션 파일 0 /
    frontend 0**. (부득이하면 근거 문서화 후 사용자 보고.)
C9. **SPEC §7-A 갱신**: L110 토폴로지 문장이 공유 버스 가능으로 갱신됨(코드-문서 정합).
C10. **하드코딩 0**: 신규 타이밍/그룹핑/포트/유닛/재시도 값이 전부 설정/옵션 주입(절대규칙 #7). 테스트는 config로 단축값 주입.

────────────────────────────────────────────────────────────────────────
■ Verification Scenarios (per-type, mandatory)
  Detected type = Full-stack → Web/UI(해당 없음·사유) + Backend/API(내부+HTTP 표면) + E2E 데이터플로.
────────────────────────────────────────────────────────────────────────

=== Web/UI ===
- 이 스프린트가 건드리는 각 화면의 기본 상태:
    N/A — 이 Phase는 브라우저/프론트 표면을 건드리지 않는다(frontend 0파일). DI/설정/HTTP 결선 + Modbus 버스 그룹핑 + E2E만.
- 대체 상태 / 빈·에러 상태 / 다크모드 / 핵심 상호작용:
    N/A(사유: UI 무접촉). 모니터링 SignalR(WcsMonitorHub) 표면은 **회귀 대상**일 뿐 변경 대상 아님 — 공유 버스 멤버의 per-slave
    워드 전이가 relay로 그대로 방출됨을 E2E(SignalR)로 확인(브라우저 검증 대체 정당 — 변경 표면에 UI 없음).

=== Backend/API ===
- 이 스프린트가 건드리는 HTTP 엔드포인트(method + path):
    신규 엔드포인트 0. 기존 표면(IF-05 POST, IF-09 도착 보고, IF-10 정렬, `/health` GET, `/hubs/monitor` SignalR)의 **계약·경로
    불변** — 변경은 그 아래 소터 라우팅이 "소터별 독립 버스"에서 "버스 키 그룹 공유 버스"로 바뀌는 것뿐. (회귀 대상: 기존 API 스위트
    GREEN.)
- (내부 표면) happy path:
    · SorterRegistryFactory.StartAsync: DB SORTER_3D N개 → 버스 키로 그룹핑 → 그룹당 ISharedModbusConnection 1개 + ModbusBus 1개
      → 멤버 AddSlave(cfg.UnitId) → bus.StartAsync 1회 → destId→bundle 딕셔너리 + 버스 목록. 두 same-key 소터 = 버스 1개(멤버 2).
    · `/health` GET: AllBundles per-slave Latest 순회 → 공유 버스의 두 소터 상태를 각각 보고(멤버별 스냅샷 독립).
    · SignalR relay: MonitorRelayService.StartAsync가 AllBundles(공유 버스 멤버 포함) 구독 → 두 소터 워드 전이 각각 방출.
- 에러/경계:
    · 같은 버스 키 멤버 시리얼 파라미터 불일치 → StartAsync가 LogCritical + InvalidOperationException(그 버스 기동 거부, fail-loud).
    · 같은 버스 중복 UnitId → 기동 거부(ModbusBus.AddSlave throw를 레지스트리가 명확 메시지로 표면화).
    · SORTER_3D인데 Sorters[] 항목 없음 → 기동 거부(현행 L470-479 보존).
    · DB 미가용 → 현행 LogCritical + throw 보존(L453-458).

=== 최소 1개 E2E 데이터플로 (2계층 이상 횡단 — 흐름 서술) ===
전부 **풀스택**(HTTP → SorterRegistryFactory/공유 버스 → TCP 멀티유닛 Sim3ds → 실 EF DB → SignalR/FakeChuteState) 횡단:
 (a) **한 공유 버스 두 소터 엔드투엔드(핵심)**:
     멀티유닛 Sim(한 포트, unitId=1·2) 기동 → 같은 Host:Port·다른 UnitId·다른 ChuteNo 두 SORTER_3D 시드/config → 호스트 기동 →
     두 소터 모두 Online(공유 연결 1개) → 각 소터로 라우팅되는 바코드로 IF-05/IF-10 흐름 + 핸드셰이크 → 두 소터 각각 Success
     (R_Seq==자기 C_Seq), DB(sorter_command/cell_assignment/piece) 각자 기록, SignalR relay가 두 소터 전이 방출. 공유 연결/포트
     1개임을 입증(2대째 OFFLINE 재현 안 됨 = 근본원인 DI 경로에서 해소).
 (b) **슬레이브 격리(한 소터 실패가 형제 무영향)**:
     공유 버스의 소터 B에 Sim 고장주입(InjectUnresponsive 또는 무응답) → B만 OFFLINE 전이(그 소터 /health·relay OFFLINE 방출),
     동시에 소터 A는 Online 유지 + IF-10 핸드셰이크 Success. A가 B 실패로 뒤집히지 않음(공유 연결 무-churn).
 (c) **fail-loud 기동 거부**:
     같은 버스 키 두 config의 BaudRate 불일치(또는 중복 UnitId)로 호스트 기동 시도 → 기동이 명확한 예외로 실패(그 버스 거부).
     (테스트는 기동 예외/로그를 단언 — 실 PLC/COM 무접촉, TCP config로 재현.)
 (d) **멀티 포트 병렬 회귀(다른 버스 키)**:
     기존 멀티 포트 모드(소터당 별도 포트) E2E가 그대로 GREEN — 서로 다른 버스 키 두 소터가 각자 독립 ModbusBus로 동시 Online·
     핸드셰이크 성공(A2 보존).
 (e) **전 스위트 회귀 0**:
     `dotnet test backend/Wcs.sln` GREEN. 취약 클래스(S5RSeqMismatch/S9/IT3a/IT4b/teardown/신규 E2E) 직렬·격리 ≥5회 귀속.
     종료 시 행/크래시/고아 Sim 0.

> Planner self-check — Detected project type: Full-stack. Required scenario slots: 3 (Web/UI [N/A·사유], Backend/API [엔드포인트 0 + 내부 표면 happy/error], E2E data-flow [a·b·c·d·e]). All slots filled: yes.

────────────────────────────────────────────────────────────────────────
■ Phased Plan (분할 여부)
────────────────────────────────────────────────────────────────────────
**단일 스프린트(분할 없음).** 근거: 변경 표면이 (1) Program.cs 레지스트리 그룹핑 + SorterConfig 바인딩, (2) 버스 연결 생성
진입점(작은 가산), (3) E2E 인프라 멀티유닛 모드 + 신규 풀스택 테스트, (4) SPEC §7-A 1문장으로 **경계가 명확하고 좁다**. 통신
계층(Phase 1)은 소비만 하므로 최고위험 부분이 이미 GREEN. 5-iteration 내 충분. (Phase 1에서 6갈래를 2 Phase로 자른 결과, 이
Phase는 그 후반 결선만 담당.)

────────────────────────────────────────────────────────────────────────
■ Parallel Modules (optional — Generator fan-out)
────────────────────────────────────────────────────────────────────────
**N/A (기본 1 Generator 순차 권장).** 명목상 2 seam이 있으나 강하게 결합·fan-in 성격이라 병렬 이득 불확실:
  - seam M1: Wcs.Api 레지스트리 그룹핑 + SorterConfig + 버스 연결 진입점.
  - seam M2: Wcs.Tests E2E 멀티유닛 인프라 + 신규 풀스택 테스트.
M2는 M1의 그룹핑이 실제 동작해야 성립(fan-in). 또한 무거운 실 Sim E2E를 동시에 두 Evaluator/Generator가 돌리면 고아 Sim/파일
잠금/parallel-load flake 위험(메모리 e2e-parallel-load-surfaces-integration-flakes). → **단일 순차(M1→M2→문서 E)** 권장. 굳이
병렬 시 수동 worktree(base=현 tip) 격리 + 커밋 전 브랜치 확인.

────────────────────────────────────────────────────────────────────────
■ Evaluation Dimensions (optional — Evaluator expert pool)
────────────────────────────────────────────────────────────────────────
2개 차원을 **단일 Evaluator가 둘 다** 수행(2명 동시 dotnet test 충돌 회피 — 고아 Sim/파일잠금/parallel-load flake). 단, 두 축을
1급으로 코드 직접검사:
  - 차원 1 (wiring-and-regression): 시나리오 (a)(c)(d)(e) + C1~C4·C7~C10. 버스 키 그룹핑 정합·fail-loud·N=1 바이트 동일·멀티
    포트 회귀·스코프/문서/하드코딩.
  - 차원 2 (lifecycle-and-isolation): 시나리오 (b) + C5·C6. 버스 단위 teardown(형제 조기 종료 0)·슬레이브별 OFFLINE 독립·버스
    단일 큐(절대규칙 #1). 근거(메모리 evaluator-concurrency-blindspot·testhost-teardown-channel-race): 전이 원자성·핸들러 예외
    격리·teardown 순서를 **코드 직접 검사**(단순 GREEN 신뢰 금지).

────────────────────────────────────────────────────────────────────────
■ Open Questions (사용자 게이트 — 기본값 제시, 고위험 포크 표시)
────────────────────────────────────────────────────────────────────────
OQ7 [Data 계층 — 기본값으로 진행 가능] UnitId를 DB가 실어야 하나, config 전용인가.
    **기본 제안: config 전용.** `SorterConfig.UnitId`(Program.cs L674)가 이미 존재하고 ChuteNo로 DB destination과 매칭되므로
    DB 컬럼/마이그레이션 불요(목표 0 마이그레이션 충족). DB가 unitId를 authoritative하게 실어야 한다면 그건 별도 마이그레이션
    스프린트(이 Phase의 0-마이그레이션 목표 위반) → 그 경우만 알려주세요. → **기본값(config 전용)으로 진행.**

OQ8 [저위험] 버스 키 도출·정규화. **기본 제안: RTU → `PortName`(대소문자 무시 비교), TCP → `Host:Port`(입력 그대로).**
    SharedXxxConnection.BusKey 규칙(TCP `host:port` L65 / RTU `portName` L143)과 일치. host 별칭(127.0.0.1 vs localhost)은
    정규화하지 않고 문자열 동일 그룹핑(현장은 IP 직기입). → **기본값으로 진행.**

OQ9 [★고위험 포크 — 사용자 결정 권장] 같은 버스 멤버의 per-sorter Timing/PollInterval 상이 처리.
    실측: `ModbusBus`는 버스당 단일 `PlcGatewayOptions`를 쓴다(폴 cadence L114; 멤버 PlcPollingService 생성 L83). 즉 현재
    시그니처로는 같은 버스 멤버가 서로 다른 오버라이드를 못 가진다. 두 갈래:
      (i) **폴 cadence(PollIntervalMs)는 본질적으로 버스 단위 1개** — 같은 버스 멤버가 서로 다른 PollIntervalMs를 선언하면
          **fail-loud(불일치 거부)** 또는 min 채택. **기본 제안: fail-loud**(OQ4와 같은 철학 — 한 물리 버스의 폴 주기는 하나).
      (ii) **per-member handshake Timing**(RFlagTimeoutMs/CFlagTimeoutMs/RFlagClearConfirmTimeoutMs/OfflineAfterFailures)은
          멤버별로 다를 수 있는 논리값 → **기본 제안: `ModbusBus.AddSlave`에 per-slave PlcGatewayOptions를 받는 오버로드를 가산**
          (Phase 1 시그니처 하위호환 확장, PR에 명시)해 멤버별 오버라이드 보존.
    대안(단순): 같은 버스 멤버는 **모든 Timing 동일 강제(fail-loud)** — AddSlave 가산 불요, 그러나 유연성↓.
    → **기본값: (i) PollIntervalMs 불일치 fail-loud + (ii) AddSlave per-slave opt 가산.** 이 가산을 원치 않으면(=같은 버스
      멤버 Timing 완전 동일 강제) 알려주세요. (설계 영향 중간 — Phase 1 API 표면 확장 여부.)

OQ10 [중간 — 확인 유용] OQ4 fail-loud 대상에 명시된 **DataBits**가 현재 설정 표면에 없음.
    실측: `SorterConfig`에 DataBits 필드 없음(L653-698), `SharedRtuModbusConnection`에도 DataBits 파라미터 없음(endianness
    고정 BigEndian, L128). **기본 제안: 불일치 검사는 존재하는 시리얼 파라미터(BaudRate/Parity/StopBits)만 대상**; DataBits/
    endianness는 현재 구성 불가 → 불일치 자체가 성립 안 함(검사 제외). DataBits를 현장에서 지정해야 한다면 `SorterConfig` +
    `SharedRtuModbusConnection`에 필드 가산(작은 Phase-1-인접 추가) — 필요 시만. → **기본값(BaudRate/Parity/StopBits 검사)으로 진행.**

OQ11 [저위험] 같은 버스 중복 UnitId 처리. **기본 제안: fail-loud**(ModbusBus.AddSlave가 이미 duplicate unitId throw — L78-79.
    레지스트리가 이를 잡아 "버스 키 X에 UnitId Y 중복" 명확 메시지 + LogCritical로 표면화). → **기본값으로 진행.**

────────────────────────────────────────────────────────────────────────
■ 게이트 확정 (사용자 2026-07-16)
────────────────────────────────────────────────────────────────────────
- OQ7 = **UnitId config 전용**(SorterConfig.UnitId 기존 사용, DB 컬럼/마이그레이션 0).
- OQ8 = **버스 키: RTU=PortName(대소문자 무시), TCP=Host:Port(입력 그대로)**. host 별칭 정규화 안 함.
- OQ9 = **폴주기만 버스 공유·핸드셰이크는 멤버별**: (i) 같은 버스 멤버의 PollIntervalMs 불일치 → **fail-loud**
  (물리 버스 폴 주기는 하나). (ii) per-member 핸드셰이크 Timing(RFlagTimeoutMs/CFlagTimeoutMs/
  RFlagClearConfirmTimeoutMs/OfflineAfterFailures)은 멤버별 상이 허용 → **`ModbusBus.AddSlave`에 per-slave
  PlcGatewayOptions 오버로드 가산**(Phase 1 시그니처 하위호환 확장, PR에 명시). 멤버별 오버라이드 보존.
- OQ10 = **시리얼 불일치 검사 = BaudRate/Parity/StopBits만**(DataBits/endianness는 설정 표면에 없어 검사 제외).
- OQ11 = **같은 버스 중복 UnitId → fail-loud**(AddSlave의 throw를 레지스트리가 잡아 "버스 X UnitId Y 중복" + LogCritical).
- (이월 결정) OQ3 평면 배열 암묵 그룹핑 / OQ4 시리얼 불일치 fail-loud / OQ5 SPEC §7-A 이 Phase에서 갱신 /
  OQ6 같은 슬레이브 동시 핸드셰이크 out-of-scope / TCP=테스트 vehicle·RTU=운영 목표.

■ 팀 구성 (orchestrator 결정)
────────────────────────────────────────────────────────────────────────
- Generator = **단일 순차(M1 DI 그룹핑 → M2 풀스택 E2E → 문서)**. 모듈 간 계약(버스 그룹핑↔E2E 하네스)
  합의를 단일 흐름이 자연 보장.
- Evaluator = **단일**(2명 동시 dotnet test 충돌 회피 — 고아 Sim/파일잠금/parallel-load flake). 두 Evaluation
  Dimension을 한 Evaluator가 코드 직접검사로 수행(동시성·수명·fail-loud 경로 포함).

(끝 — 이 계약은 Phase 2. 게이트 확정 완료 → Generator↔Evaluator 루프 진입.)
