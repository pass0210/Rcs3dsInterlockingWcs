# Sprint Contract — S-RTU (Modbus 전송 추상화 + RTU 어댑터)

## Goal
Modbus **TCP 전용**으로 하드코딩된 PLC 통신 계층을 **전송 추상화**로 교체해, 동일 애플리케이션 로직
(판정·C/R 핸드셰이크·단일 쓰기 큐·D4 RMW·OFFLINE)을 **RTU(시리얼)·TCP 양쪽**에서 설정만으로 선택 구동.
현장 1차 타깃 = **Modbus RTU(RS-485)**, TCP는 시뮬레이터·SAT·일부 장비 병행 유지.

통찰: Modbus는 RTU·TCP 애플리케이션 계층 동일(FC03/06/16, D0~D6). 판정엔진·핸드셰이크·단일 쓰기 큐·RMW·OFFLINE은
전송 무관 — 재사용. **전송 계층(클라이언트 생성·연결·read/write)만 인터페이스 뒤로 분리·교체.** `_clientLock` 단일
트랜잭션 직렬화는 RTU 단일 버스 제약(한 버스=한 번에 한 트랜잭션)과 정합 — 보존.

토폴로지(확정): **소터마다 독립 버스/포트**(포트당 소터 1대, 다중 슬레이브 경합 없음). WCS=마스터/3DS=슬레이브.
본 스프린트는 **단일 소터 추상화 + N대 확장 여지**까지. 다중 소터 라우팅(목적지→소터)은 M3/M4 — 안 함.

## 사전 검증 (Planner가 설치 패키지에서 직접 확인 — 추측 아님)
FluentModbus **5.3.2** (`…/fluentmodbus/5.3.2/lib/netstandard2.1/FluentModbus.xml`):
- `ModbusRtuClient` 제공(BaudRate·Parity·StopBits·Connect(port)·IsConnected). read/write는 공유 기반 `ModbusClient`에 정의 — `ReadHoldingRegistersAsync<T>(unitId,start,count,ct)`·`WriteSingleRegisterAsync(unitId,...)`·`WriteMultipleRegistersAsync<T>(unitId,...)` **TCP·RTU 동일 시그니처** → PlcGateway 호출부 1:1 대응.
- `ModbusRtuServer` 제공(슬레이브 시뮬레이션 가능).
- **핵심**: `IModbusRtuSerialPort` 공개 인터페이스 + `ModbusRtuClient.Initialize(IModbusRtuSerialPort,...)` / `ModbusRtuServer.Start(IModbusRtuSerialPort)` → **물리 COM·com0com 없이 in-memory fake serial 쌍**으로 실제 RTU 클라↔서버 in-process 왕복(CI 가능). **RTU 자동 테스트 리스크 해소.**

## Implementation Scope (WHAT)
**(A) 전송 추상화** `IModbusMaster`(가칭, src/Wcs.PlcGateway/ 신규): IsConnected/Connect()/Disconnect()/Dispose + ReadHoldingRegisters(FC03, D0~D6 일괄, unitIdentifier) + WriteSingle(FC06)/WriteMultiple(FC16, unitIdentifier). unitId·엔디안은 어댑터가 설정으로 관리(기본 UnitId=1·BigEndian = 현 TCP 동작 보존). **PlcPollingService·HandshakeOrchestrator는 `ModbusTcpClient` 직접 의존 제거, `IModbusMaster`에만 의존.** `_clientLock` 직렬화 유지(폴·쓰기·RMW·Disconnect/재연결 전부 임계구역 — IT-4b 불변).
**(B) TCP 어댑터**: 기존 ModbusTcpClient 1:1 래핑(IPEndPoint Host/Port·BigEndian·타임아웃·재연결 의미 보존). **M2 통합테스트 IT-1~5·3c·4b 단언·코드 변경 없이 GREEN(회귀 0 필수).**
**(C) RTU 어댑터**: ModbusRtuClient 래핑 — COM 포트·Baud·Parity·Stop·(Handshake)·Read/WriteTimeout·unitId 전부 설정값(하드코딩 금지). OFFLINE 전이가 RTU 예외(시리얼 타임아웃·IO)에서도 동작하도록 소켓 전용 분기 의존 제거.
**(D) 설정·팩토리**: appsettings `Plc:Transport` = `Tcp`|`Rtu`. **키 미지정 시 기본값 = `Rtu`(현장 우선 — 사용자 확정).** TCP=Host/Port, RTU=PortName/Baud/Parity/Stop/UnitId. `IModbusMaster` 생성 팩토리(설정→어댑터). 설정 스키마는 **소터별 독립 전송 N 확장 표현 가능**, 단 런타임은 단일 소터까지만 구현(경계 명시).
  - **회귀 보존(중요)**: 기본이 Rtu이므로 기존 M2 TCP 통합 테스트(IT-1~5·3c·4b)는 **명시적으로 `Transport=Tcp`(또는 TCP 어댑터 직접 주입)로 구성**해 그대로 GREEN 유지. 커밋되는 `appsettings.json`은 dev/시뮬레이터가 `dotnet run`으로 동작하도록 전송값을 **명시**(혼동 방지) — 현장 배포 설정에서 Rtu.
**(E) Sim3ds**(P-RTU-3 확정 — 테스트 전용 RTU 서버): 기존 TCP 경로·시그니처 **불변**(IT 회귀). RTU 검증은 **테스트 인프라(ModbusRtuServer + in-memory fake serial)**로만 — SimServer 본체는 TCP 유지.
**(F) 문서**: SPEC §7/§7-A "TCP vs RTU" → 확정(RTU 우선+TCP, 전송 추상화, 소터별 독립 포트, 마스터/슬레이브) 갱신 + CLAUDE.md 다이어그램 `Modbus TCP`→`Modbus RTU/TCP` 정정. **코드와 같은 커밋.**

## Out of Scope
Wcs.Core 판정 로직 변경 / M3 API·DTO / M4 영속화 / M5 운영 / 다중 소터 라우팅·N대 동시 런타임(스키마만 N, 구현 1대) / 물리 시리얼 하드웨어 의존 테스트(in-memory fake serial로 대체).

## Detected Project Type: Backend/API
HTTP 엔드포인트는 M3. 검증 표면 = 단위/통합 테스트(추상화 위 PlcGateway 로직 + TCP 회귀 + RTU fake-serial 라이브 왕복).

## Evaluation Criteria
1. **추상화 경계(★★★)**: `IModbusMaster`가 M2 사용 표면을 정확·최소 포착. PlcGateway·HandshakeOrchestrator에 구상 타입(`ModbusTcpClient`/`ModbusRtuClient`) 직접 참조 0(grep, 어댑터·팩토리에만). 절대규칙 #1·`_clientLock` 직렬화 구조 보존.
2. **회귀 안전(★★★)**: M2 IT-1~5·3c·4b **변경 없이 GREEN**. TCP 어댑터가 기존 동작 보존.
3. **RTU 정합(★★)**: FluentModbus 5.3.2 실제 API로 동작(추측 API 0). 시리얼 파라미터·UnitId 전부 설정. RTU 예외에서 OFFLINE 전이. fake-serial RTU 왕복으로 C/R + R_Seq 대사 입증.
4. **장인성·설정(★★)**: 하드코딩 0, 예외 안 삼킴, 스키마 N 확장형, 테스트 결정성(고정 sleep 금지), 문서 동기화 완료.

## Completion Conditions (전부 필수)
- `dotnet build Wcs.sln` 성공 / `dotnet test Wcs.sln` 전부 GREEN. (막히면 Bash로 `cd "<절대경로>" && dotnet ...` — S-M1 교훈. Wcs.sln 클래식 — S-M0 교훈.)
- **M2 회귀 0(명시)**: M1 Decider 15 + M2 통합 8(IT-1·2a·2b·3a·3b·3c·4·4b·5)이 단언·코드 변경 없이 GREEN, split 수 감소 없음.
- 동시성/직렬화 변경 포함 → **`dotnet test` 4회 연속 GREEN**(결정성) + **독립 코드리뷰**(M2 off-lock 같은 구조적 동시성 결함은 기능 테스트가 못 잡음 — 메타 교훈).
- `ModbusTcpClient` 직접 참조가 PlcPollingService·HandshakeOrchestrator에 0건(grep). 모든 트랜잭션 `_clientLock` 통과(M2 검증법 재적용).
- 하드코딩 시간/시리얼 값 0. Wcs.Core·Wcs.Api(appsettings 제외)·Wcs.Data 무변경.
- SPEC §7/§7-A 확정 갱신 + CLAUDE.md 다이어그램 정정이 코드와 같은 커밋에.

## Verification Scenarios (자동화)
- **VT-1 TCP 회귀(필수)**: IT-1~5·3c·4b 전부 GREEN — 추상화 도입 후에도 C/R·R_Seq==C_Seq·OFFLINE·재연결 무손상 불변.
- **VT-2 RTU 라이브 왕복**: in-memory fake `IModbusRtuSerialPort` 쌍으로 실제 ModbusRtuClient(WCS) ↔ 실제 ModbusRtuServer(슬레이브). C/R 1건 성공 + R_Seq==C_Seq + RMW 비트 보존 + 단일 큐 직렬화.
- **VT-3 전송 선택**: `Plc:Transport=Tcp/Rtu` → 해당 어댑터 생성 + RTU 시리얼 파라미터 전달을 팩토리 단위 테스트로 단언.
- **VT-4 추상화 단위 테스트**: 인메모리 fake `IModbusMaster`로 PlcGateway 로직(스냅샷·큐·RMW·OFFLINE) 전송 무관 검증.
- **VT-5 RTU OFFLINE 전이**: RTU fake serial 단절/무응답 → 연속 실패 후 Online=false, 재개 시 true. 예외 안 삼킴.

## 미확정·리스크
- (RTU 테스트 — 해소) in-memory fake serial로 CI 자동화 가능(확인 완료). 방식은 P-RTU-1 확정.
- (엔디안/UnitId) 현장 VEICHI 실측 전 — 설정값 노출, 기본=현 TCP(BigEndian·UnitId 1) 회귀 보존. 변경 시 설정만.
- (RTU 타임아웃·프레임 침묵) FluentModbus 기본/설정 위임, 현장 실측 전 appsettings 기본.
- (P1/P2/P3) M2 방침 그대로 무변경.

> Planner self-check — Detected project type: Backend/API. Required scenario slots: 3 (transport-selection config / TCP live-roundtrip regression / RTU adapter live-roundtrip via fake serial). All slots filled: yes. FluentModbus 5.3.2 RTU API pre-verified (ModbusRtuClient·ModbusRtuServer·IModbusRtuSerialPort). RTU test risk resolved (in-memory fake serial).
