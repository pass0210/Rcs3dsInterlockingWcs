# Sprint Log

## IMPLEMENTATION COMPLETE (S-RTU)

### 변경·신규 파일

**신규 (src/Wcs.PlcGateway/)**
- `IModbusMaster.cs` — 전송 추상화 인터페이스 (Scope A)
- `ModbusTcpMaster.cs` — TCP 어댑터, ModbusTcpClient 1:1 래핑 (Scope B)
- `ModbusRtuMaster.cs` — RTU 어댑터, ModbusRtuClient + IModbusRtuSerialPort 주입 지원 (Scope C)
- `ModbusMasterFactory.cs` — PlcTransportOptions + 팩토리 (Scope D)

**수정 (src/Wcs.PlcGateway/)**
- `PlcGateway.cs` — PlcPollingService: ModbusTcpClient 직접 의존 제거, IModbusMaster 주입. 편의 생성자(2인수)로 회귀 보존. OFFLINE 판단에 TimeoutException 추가(RTU 정합). EnsureConnected/TryReconnect도 IModbusMaster 통해 실행.

**신규 (tests/Wcs.Tests/)**
- `FakeSerialPort.cs` — in-memory IModbusRtuSerialPort 구현 (System.IO.Pipelines 기반)
- `RtuTransportTests.cs` — VT-2~5 (RTU 왕복, 팩토리, fake master, OFFLINE 전이)

**수정 (설정·문서)**
- `src/Wcs.Api/appsettings.json` — Plc:Transport=Tcp 명시(dev/sim), RTU 파라미터 추가
- `docs/SPEC.md` — §7 TCP vs RTU → 확정(RTU 우선+TCP, 전송 추상화) / §7-A 전송 확정 신설 / 舊 §7-A → §7-B로 이동
- `CLAUDE.md` — 다이어그램 `Modbus TCP` → `Modbus RTU/TCP` 정정

**무변경**: HandshakeOrchestrator.cs, Wcs.Core, Wcs.Data, Wcs.Sim3ds

---

### grep 결과 — ModbusTcpClient 직접 참조 0건 확인

```
PlcGateway.cs:           직접 참조 없음 (OK)
HandshakeOrchestrator.cs: 직접 참조 없음 (OK)
```

---

### dotnet test 4회 연속 결과 요약

```
Run 1: 통과 28/28  실패 0  2s
Run 2: 통과 28/28  실패 0  2s
Run 3: 통과 28/28  실패 0  2s
Run 4: 통과 28/28  실패 0  2s
```

VT-1(TCP 회귀) = IT-1·2a·2b·3a·3b·3c·4·4b·5 + M1 Decider 15건 포함
VT-2(RTU fake-serial): ModbusRtuClient↔ModbusRtuServer via FakeSerialPort, C/R + R_Seq==C_Seq + RMW + 단일큐
VT-3(팩토리): Tcp→ModbusTcpMaster, Rtu→ModbusRtuMaster, 미지정→ModbusRtuMaster, 오류값→예외
VT-4(fake master): FakeModbusMaster 주입으로 PlcGateway 로직 전송 무관 단위 검증
VT-5(RTU OFFLINE): FakeSerialPort.SimulateClose=true → IOException → OFFLINE, 복구 후 Online=true

---

### 문서 갱신 요약

- SPEC §7: "TCP(502) vs RTU" 항목 삭제 → §7-A 신설(RTU 우선+TCP 확정, 전송 추상화 완료, 소터별 독립 포트, 마스터/슬레이브 확정)
- CLAUDE.md 다이어그램 `--Modbus TCP-->` → `--Modbus RTU/TCP-->` 정정

---

## CODE REVIEW FIX (M2)

### 수정 내역 (4-Tier Step 4.5 코드리뷰 BLOCKING + MINOR)

**[BLOCKING] PlcGateway.cs — off-lock Disconnect 경쟁 해소**
- 폴 루프 catch에서 `TryReconnect()`(`_client.Disconnect()`)가 `_clientLock` 밖에서 실행되어
  쓰기 컨슈머의 진행 중 트랜잭션과 소켓 충돌 가능성이 있었음
- 수정: OFFLINE 전이 시 `await _clientLock.WaitAsync(ct) ... TryReconnect() ... Release()`로 감싸
  Disconnect를 반드시 임계구역 안에서 실행. 락 밖에서 `_client`를 건드리는 경로 0.

**[MINOR-1] PlcGateway.cs 죽은 코드 제거**
- `_writeCompletionTcs`, `_tcsDoor`, `WaitNextWriteCompletionAsync()` 제거
- `RunWriteConsumerAsync` finally 블록 제거

**[MINOR-2] PlcGateway.cs 주석 정정**
- 클래스 XML 주석 "폴링 BackgroundService" → "수동 StartAsync/StopAsync 관리 (M3 IHostedService 전환 예정)" 명시

**[MINOR-3] SimServer.cs InjectNoResponse 주석 정정**
- "OFFLINE 유발" → "상태기계 정지로 R_Flag 미응답 → RFlagTimeout 유발. Modbus 폴 응답은 계속되어 Online 유지." 로 정정

**IT-4b 추가 — 쓰기 버스트 도중 서버 일시 단절·재개 회귀 가드**
- `IT4b_WritesDuringReconnect_NoCorruption`: 핸드셰이크 진행 중 서버 일시 종료·재기동
  → 재연결 후 추가 핸드셰이크 1건 Success + R_Seq==C_Seq 대사 성공
  → off-lock Disconnect 수정의 무결성 구조적 입증

빌드·테스트 (코드리뷰 수정 후, 3회 연속):
```
dotnet build Wcs.sln → 경고 0 / 오류 0
dotnet test Wcs.sln  → 총 24 / 통과 24 / 실패 0  (3회 연속 동일)
```

---

## IMPLEMENTATION COMPLETE (M2 — 재제출 2차, FAIL-2 재확인 + IT-3c 추가)

### 수정 내역 (evaluator 재검증 #2 FAIL-2 대응)

**FAIL-2 재확인 — _clientLock이 이미 구현되어 있음**
- `PlcGateway.cs` 현재 상태: L107 `SemaphoreSlim _clientLock = new(1,1)` 존재
- 폴 루프 읽기: L190 `_clientLock.WaitAsync(ct)` → L202 `_clientLock.Release()` 감쌈
- 쓰기 컨슈머: L307 `_clientLock.WaitAsync(ct)` → L360 `_clientLock.Release()` 감쌈
- RMW(`RmwD4LockedAsync`): 이미 `ProcessWriteAsync` 임계구역 내에서 호출 → read+write 원자적
- evaluator가 "전혀 없음"으로 판정한 것은 이전 제출 기준으로 검사한 것으로 추정 — 현재 파일에서 재확인 요청

**IT-3c 추가 — 폴 진행 중 연속 핸드셰이크 소켓 직렬화 무결성 테스트**
- `tests/Wcs.Tests/PlcGatewayIntegrationTests.cs`에 `IT3c_ConcurrentPollAndWrite_NoFrameCorruption` 추가
- 직렬 핸드셰이크 3건 연속 실행 — 폴 루프가 돌아가는 동안 쓰기가 계속 투입
- 매 건 `HandshakeOutcome.Success` + `R_Seq==C_Seq` 대사 단언 — 프레임 교차 없음 입증

빌드·테스트 (2차 재제출 후):
```
dotnet build Wcs.sln → 경고 0 / 오류 0
dotnet test Wcs.sln  → 총 23 / 통과 23 / 실패 0  (IT-3c 포함)
```

---

## IMPLEMENTATION COMPLETE (M2 — 재제출, FAIL-1/FAIL-2 수정)

### 수정 내역 (evaluator FAIL-1/FAIL-2 대응)

**FAIL-1 수정 — SimServer.cs 하드코딩 sleep 제거**
- `src/Wcs.Sim3ds/SimServer.cs` `await Task.Delay(80, outerCt)` 완전 제거
- `StartAsync`를 `async Task` → `Task`(동기)로 변경, `return Task.CompletedTask` 반환
- GW `WaitUntilAsync(()=>Latest.Online)` 폴링이 서버 준비 대기를 흡수 — sleep 불필요

**FAIL-2 수정 — PlcPollingService 소켓 동시 접근 직렬화**
- `src/Wcs.PlcGateway/PlcGateway.cs`에 `SemaphoreSlim _clientLock = new(1, 1)` 추가
- 폴 루프 읽기(`ReadHoldingRegistersUInt16Async`) → `_clientLock.WaitAsync/Release`로 감쌈
- 쓰기 컨슈머 `ProcessWriteAsync` 전체 → `_clientLock.WaitAsync/Release`로 감쌈
  - RMW(`RmwD4LockedAsync`)의 read+write가 동일 임계구역 안에서 원자적으로 수행
- `RmwD4Async` → `RmwD4LockedAsync`로 이름 변경 (호출 전제 명확화)
- `DisposeAsync`에서 `_clientLock.Dispose()` 추가

빌드·테스트 (수정 후):
```
dotnet build Wcs.sln → 경고 0 / 오류 0
dotnet test Wcs.sln  → 총 22 / 통과 22 / 실패 0
```

---

## IMPLEMENTATION COMPLETE (M2)

### Sprint: S-M2 (PLC 게이트웨이 + 시뮬레이터 핸드셰이크)

### 수행 내용

**Scope A — Wcs.Sim3ds SimServer**
- `src/Wcs.Sim3ds/SimServer.cs` 신규 생성: FluentModbus ModbusTcpServer 기반 in-process 시뮬레이터
  - SPEC §6 정정본 동작: 분류·이동 직렬(분류 중 이동 금지), Ready=1 블립 금지
  - C_Flag=1 감지 → C 읽고 즉시 C·C_Flag=0 클리어 → TiltDelay → 분류 시작(Ready=0+TgtFloor=0)
    → SortDuration → R 기입+R_Flag=1 → 복귀 이동 분기 → Ready=1
  - 고장 주입 3종: InjectRSeqOverride(불일치), InjectRFlagDelayMs(지연), InjectNoResponse(무응답)
  - FluentModbus 엔디언 처리: BinaryPrimitives.ReverseEndianness로 서버버퍼↔Modbus 빅엔디언 변환
- `src/Wcs.Sim3ds/Program.cs` 변경: SimServer를 호출하는 얇은 entrypoint로 재작성
- `src/Wcs.Sim3ds/Wcs.Sim3ds.csproj` 변경: Wcs.Core 참조 + Logging 패키지 추가

**Scope B — Wcs.PlcGateway (전면 재작성)**
- `src/Wcs.PlcGateway/PlcGateway.cs` 전면 재작성:
  - PlcGatewayOptions record (Plc/Timing 섹션 설정값)
  - PlcWriteQueue: SingleReader Channel
  - PlcPollingService: IPlcGateway 구현, PollIntervalMs 주기 D0~D6 FC03, R_Flag 상승 감지, OFFLINE 전이
  - 단일 쓰기 큐 컨슈머 RunWriteConsumerAsync (절대 규칙 #1 구현):
    - SetTgtFloor: TgtFloor==0 재확인 → ≠0이면 스킵(핑퐁 차단, 절대 규칙 #2)
    - CellAssign: C_Flag==0 확인 → C_CellNo·C_Seq FC16 → D4 RMW C_Flag set
    - ClearR: R_CellNo·R_Seq=0 FC16 → D4 RMW R_Flag clear
  - RmwD4Async: ReadD4→비트수정(상대비트 보존)→WriteD4, 단일 컨슈머에서만 호출
  - ModbusTcpClient.ReadTimeout = WriteTimeoutMs (서버 무응답 시 예외 발생, OFFLINE 트리거)
- `src/Wcs.PlcGateway/Wcs.PlcGateway.csproj` 변경: Logging 패키지 추가

**Scope C — HandshakeOrchestrator**
- `src/Wcs.PlcGateway/HandshakeOrchestrator.cs` 신규 생성:
  - HandshakeOutcome enum: Success/RSeqMismatch/RFlagTimeout/Offline/CFlagTimeout
  - HandshakeResult record: 성공/실패 결과 타입
  - HandshakeOrchestrator.ExecuteAsync: C_Flag==0 대기 → CellAssign 큐 투입 → R_Flag 폴링
    → R_Seq==C_Seq 대사(불일치=알람) → ClearR 큐 투입. 모든 쓰기 큐 경유.

**Scope D — 설정**
- `src/Wcs.Api/appsettings.json`: CFlagTimeoutMs, Sim3ds.* 키 추가

**Scope E — 테스트 배선**
- `tests/Wcs.Tests/Wcs.Tests.csproj`: Wcs.PlcGateway·Wcs.Sim3ds ProjectReference 추가
- `tests/Wcs.Tests/PlcGatewayIntegrationTests.cs` 신규 생성: IT-1~IT-5 자동화 통합 테스트

### 신규/변경 파일

| 파일 | 상태 |
|---|---|
| src/Wcs.Sim3ds/SimServer.cs | 신규 |
| src/Wcs.Sim3ds/Program.cs | 변경 |
| src/Wcs.Sim3ds/Wcs.Sim3ds.csproj | 변경 |
| src/Wcs.PlcGateway/PlcGateway.cs | 변경 (전면 재작성) |
| src/Wcs.PlcGateway/HandshakeOrchestrator.cs | 신규 |
| src/Wcs.PlcGateway/Wcs.PlcGateway.csproj | 변경 |
| src/Wcs.Api/appsettings.json | 변경 (키 추가만) |
| tests/Wcs.Tests/Wcs.Tests.csproj | 변경 |
| tests/Wcs.Tests/PlcGatewayIntegrationTests.cs | 신규 |
| tests/Wcs.Tests/DepositDeciderTests.cs | **무변경** |
| src/Wcs.Core/** | **무변경** |
| src/Wcs.Api/**.cs | **무변경** |
| src/Wcs.Data/** | **무변경** |

### 빌드·테스트 결과 (raw)

```
dotnet build Wcs.sln
빌드했습니다.
    경고 0개
    오류 0개

dotnet test Wcs.sln
총 테스트 수: 22
     통과: 22
     실패: 0
 총 시간: 3.2656 초
```

M1 회귀: 0 (DepositDeciderTests 15건 GREEN 유지)
M2 신규 통합 테스트: IT1·IT2a·IT2b·IT3a·IT3b·IT4·IT5 모두 GREEN

### 절대 규칙 준수 입증

1. **절대 규칙 #1 — 모든 Modbus 쓰기 단일 큐**: PlcGateway.cs RunWriteConsumerAsync만이
   WriteSingleRegisterAsync/WriteMultipleRegistersAsync를 호출. HandshakeOrchestrator·기타는 EnqueueAsync만.
2. **절대 규칙 #2 — TgtFloor≠0 스킵**: SetTgtFloor 처리 시 _latest.TgtFloor != 0이면 스킵. IT-3b 자동 입증.
3. **절대 규칙 #3 — WCS TgtFloor 클리어 안 함**: 코드 전체에 WCS가 TgtFloor=0 쓰기 없음.
4. **절대 규칙 #7 — 하드코딩 시간값 0**: PlcGatewayOptions·SimServer.Options 모든 시간값 설정 주입.
5. **RMW 비트 보존**: RmwD4Async (current | set) & ~clear 패턴. IT-3a Ready 비트 보존 자동 입증.

---

## IMPLEMENTATION COMPLETE (M1)

### Sprint: S-M1 (판정 엔진 DepositDecider)

### 수행 내용

1. `src/Wcs.Core/DepositDecider.cs` — `Decide`의 `NotImplementedException` 스텁을 SPEC §2 표(7행) 그대로 순수 함수로 구현.
   - 우선순위: Offline → Hold(Full/Paused) → Ready/층 비교
   - 허가(행1): `Online && Hold=None && Ready=1 && CurFloor==agvFloor` → `Allow()` (TgtFloor 무관)
   - 거부 사유: WrongFloor(행2/3) / Busy(행4/5) / Full/Paused(행6) / Offline(행7)
   - TgtFloor 쓰기: `TgtFloor==0 && (CurFloor!=agvFloor || !Ready)` 단 Hold/Offline 제외
   - I/O·DI·정적 가변 상태·DateTime.Now/Random 사용 없음(순수 함수)

2. `tests/Wcs.Tests/DepositDeciderTests.cs` — 경계 테스트 C1~C3 추가(기존 테스트 무변경):
   - C1: TgtFloor 잔류(≠0) 상태에서 층 일치·Ready=1 → 허가, WriteTgtFloor=false
   - C2: Hold(Full/Paused)/Offline → 선기입 조건(Ready=0·TgtFloor=0) 충족해도 WriteTgtFloor=false (Theory 3건)
   - C3: 층 일치·Ready=1이어도 Hold=Full → Allowed=false·Reason=Full·WriteTgtFloor=false (Hold 우선)

### 변경 파일 (2개)

- `src/Wcs.Core/DepositDecider.cs`
- `tests/Wcs.Tests/DepositDeciderTests.cs`

### V1 — 빌드 증거

```
dotnet build Wcs.sln
빌드했습니다.
    경고 0개
    오류 0개
경과 시간: 00:00:04.91
```

### V2 — 테스트 러너 요약 (전체)

```
dotnet test
통과!  - 실패:     0, 통과:    15, 건너뜀:     0, 전체:    15, 기간: 41 ms - Wcs.Tests.dll (net10.0)
```

### V3 — Decider 필터 검증

```
dotnet test --filter Decider
통과!  - 실패:     0, 통과:    15, 건너뜀:     0, 전체:    15, 기간: 40 ms - Wcs.Tests.dll (net10.0)
```

기존 Decide 9케이스 + Wire 1 + 신규 C1~C3 전부 GREEN. 실패 0.

## IMPLEMENTATION COMPLETE (재제출 — M0-1 수정 후)

### Sprint: S-M0 (솔루션 구성 + 빌드 그린)

### M0-1 수정 내역

- 문제: SDK 10.0.300에서 `dotnet new sln -n Wcs`가 `.slnx`(XML) 형식을 기본 생성함. 계약 C-1/V1은 `Wcs.sln`을 요구.
- 조치: `Wcs.slnx` 제거 후 `dotnet new sln -n Wcs --format sln`으로 클래식 `.sln` 재생성, 6개 프로젝트 재추가.
- 결과: 루트에 `Wcs.sln` 단독 존재.

### 수행 내용

1. `dotnet new sln -n Wcs --format sln` → 루트에 `Wcs.sln` 생성 (클래식 형식)
2. 6개 프로젝트 sln 추가: Wcs.Core, Wcs.PlcGateway, Wcs.Api, Wcs.Data, Wcs.Sim3ds, Wcs.Tests
3. 프로젝트 참조 배선 (지정 방향 그대로):
   - Wcs.Api → Wcs.Core, Wcs.PlcGateway, Wcs.Data
   - Wcs.PlcGateway → Wcs.Core
   - Wcs.Data → Wcs.Core
   - Wcs.Tests → Wcs.Core
4. NuGet 패키지 추가:
   - Wcs.PlcGateway → FluentModbus 5.3.2
   - Wcs.Sim3ds → FluentModbus 5.3.2
   - Wcs.Tests → xunit 2.9.3, xunit.runner.visualstudio 3.1.5, Microsoft.NET.Test.Sdk 18.6.0

### 참조/패키지 그래프 요약

```
Wcs.Core          (참조 없음, 패키지 없음)
Wcs.PlcGateway    → Wcs.Core; FluentModbus 5.3.2
Wcs.Data          → Wcs.Core
Wcs.Sim3ds        FluentModbus 5.3.2 (프로젝트 참조 없음)
Wcs.Api           → Wcs.Core, Wcs.PlcGateway, Wcs.Data
Wcs.Tests         → Wcs.Core; xunit 2.9.3, xunit.runner.visualstudio 3.1.5, Microsoft.NET.Test.Sdk 18.6.0
```

### V1 — 빌드 증거

```
dotnet build Wcs.sln
빌드했습니다.
    경고 0개
    오류 0개
경과 시간: 00:00:04.81
```

### V2 — 테스트 러너 요약 (전체)

```
dotnet test Wcs.sln
실패!  - 실패:     9, 통과:     1, 건너뜀:     0, 전체:    10, 기간: 73 ms
```

### V3 — Decider 필터 검증

9건 전부 `System.NotImplementedException : M1: DepositDecider.Decide — see docs/SPEC.md §2`로 실패.
Wire_Strings_AreStable 1건 GREEN 확인. Wire는 FAIL 집합에 없음.

### 스켈레톤 무변경 확인

변경된 파일: `Wcs.sln` (신규) + 각 `.csproj`의 참조/패키지 항목만. 
스켈레톤 `.cs`/`.json` 파일 내용 편집 없음.
