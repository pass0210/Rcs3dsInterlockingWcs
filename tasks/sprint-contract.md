[Sprint Contract] — S-SIM3DS-RTU

Branch: feat/sim3ds-rtu (PR #33 위 스택 — 병합은 사용자가). 이전 계약(S-FIELD-20CELLS)은 커밋 완료됨, 본 파일 overwrite.
작성: Planner Subagent · 2026-07-07 · 사용자 요청(2026-07-07)

────────────────────────────────────────────────────────────────────────
## Goal (WHAT — 무엇을 만들 것인가)

목요일(7/9) 현장 방문 **전에** WCS의 RTU 전송 계층(`ModbusRtuMaster` — 지금까지 실 PLC로만
검증됨)을 실 PLC 없이 사전 리허설할 수 있게 한다. 이를 위해 3DS PLC 시뮬레이터(Sim3ds)가
현행 TCP뿐 아니라 **Modbus RTU 슬레이브**로도 동작하게 만든다.

리허설 구성:
```
WCS(Transport=Rtu, COM-A) ──[가상/실 시리얼 페어]── Sim3ds(Transport=Rtu, COM-B)
     Modbus 마스터                                        Modbus 슬레이브
```
이로써 프레이밍·UnitId·타임아웃·폴링 스냅샷·C/R 핸드셰이크 전 구간을 실 PLC 없이 왕복 검증한다.

**핵심 불변식**: 전송 계층만 교체한다. 시뮬레이터의 의미(레지스터 맵 D0~D6, C_Flag 자체 클리어,
SortDuration 후 R 에코, ClearR까지 R 유지, 잔류 프리셋, 고장 주입)는 RTU에서 **완전 동일**해야 하며,
기존 TCP 경로와 그 위의 모든 테스트는 **0 회귀**여야 한다.

이 스프린트의 변경 표면은 **Sim3ds + 테스트 + 문서**뿐이다. WCS 프로덕션 코드(Wcs.Api /
Wcs.PlcGateway / Wcs.Core / frontend)와 appsettings.json은 **0 변경**(WCS 측 Transport=Tcp|Rtu
선택은 S-RTU에서 이미 구현 완료 — 무변경 가드).

────────────────────────────────────────────────────────────────────────
## Implementation Scope (Generator가 해야 할 것)

1. **Sim3ds 전송 선택**: 설정으로 `Transport = Tcp(기본, 현행 보존) | Rtu` 선택.
   - 미지정/기본 = **Tcp** → `dotnet run --project backend/src/Wcs.Sim3ds`가 지금과 **바이트 동일**하게
     `127.0.0.1:1502` TCP로 기동(현행 보존 — 회귀 방지). (참고: WCS 측 기본은 Rtu이나, Sim3ds의
     기존 관측 동작은 TCP였으므로 Sim3ds 기본은 Tcp로 두어 현행 보존을 우선한다.)
   - 알 수 없는 Transport 값 → fail-loud(명확한 예외, 절대규칙 #8·Core 원칙 "Fail Loud").

2. **RTU 옵션(전부 설정값 — 절대규칙 #7, 하드코딩 금지)**: PortName·BaudRate·Parity·StopBits·UnitId
   (+ Read/WriteTimeoutMs). 기본값은 WCS appsettings `Sorters[0]` placeholder와 **정합**
   (BaudRate=9600·Parity=Even·StopBits=One·UnitId=1·Timeout=1000). 단 **PortName은 안전한 기본값
   없음** — RTU 모드에서 명시 지정을 요구한다(우발적 COM1 점유 방지. WCS와 Sim은 시리얼 페어의
   반대쪽 포트를 쓰므로 같은 포트를 공유할 수 없다).

3. **SimServer 내부 전송 추상화**: 현재 `SimServer._server`는 `ModbusTcpServer`로 하드코딩되어 있고
   `FlushToServerLocked`/`PullFromServerLocked`가 `_server.GetHoldingRegisters(UnitId)`에 결합돼 있다.
   전송을 TCP/RTU 중 선택 가능하게 추상화하되, 시뮬레이션 루프·프리셋·고장주입·테스트 훅이 깨지지
   않게 한다. **무변경 가드(공개 표면 시그니처 유지)**: `Options` record 필드, `StartAsync`/`StopAsync`/
   `DisposeAsync`, `ReadSnapshot`, `SetRResidue`, `InjectRSeqOverride`/`InjectRFlagDelayMs`/
   `InjectNoResponse`/`InjectStickyRResidue`. 엔디안 처리(현 `BinaryPrimitives.ReverseEndianness`
   가정)는 RTU 서버 버퍼에서도 동일 의미가 되도록 확인·보존한다(RtuTransportTests의
   `InitServerRegisters`가 동일 ReverseEndianness 패턴을 쓰므로 정합).

4. **RTU 모드 의미 동일성**: 위 3의 추상화 후 RTU에서 C_Flag 감지→즉시 C·C_Flag 클리어 → TiltDelay →
   분류 시작(Ready=0 + TgtFloor=0 클리어, Ready 블립 금지) → SortDuration 후 R 세팅(R_Seq==C_Seq) →
   ClearR까지 R 유지 → 복귀 이동 규칙까지 SPEC §6과 **동일**. 잔류 프리셋·4종 고장 주입도 RTU에서 동작.

5. **관측성**: 콘솔/로그에 어느 전송·어느 엔드포인트로 리스닝 중인지 명시
   (예: `Sim3ds 서버 기동 TCP 127.0.0.1:1502` / `Sim3ds 서버 기동 RTU COM6 9600/Even/One unit=1`).
   기존 타임라인 로그 형식 보존.

6. **문서**: 시리얼 페어 준비법 + WCS↔Sim RTU 리허설 절차(설정 예시 포함) — docs/ 아래.
   - 가상 페어: com0com 설치법(관리자 드라이버 설치 — 사용자 작업, scope 밖임을 명시) **또는**
     USB-시리얼 어댑터 2개 크로스 결선(TX↔RX, RX↔TX, GND↔GND) 대안.
   - 리허설 절차: Sim3ds를 COM-B RTU로 기동 → WCS appsettings `Sorters[0]`를 Transport=Rtu·COM-A로
     설정 → 폴 Online·핸드셰이크 1건 왕복 확인. Sim3ds RTU 설정 예시 + WCS 설정 예시 양쪽 수록.
   - **주의**: appsettings.json은 이 스프린트에서 변경 대상 아님. 문서에는 "현장 리허설 시 이렇게
     설정" 예시(diff/스니펫)만 싣고 실제 파일은 건드리지 않는다.

7. **테스트** (아래 Completion Conditions·Verification Scenarios와 정합):
   - (a) **단위**: Sim3ds 전송 선택 + RTU 옵션 파싱 + 잘못된 Transport fail-loud.
   - (b) **CI 통합(권장·환경 무관, 결정적)**: `FakeSerialPort`가 `IModbusRtuSerialPort`를 구현하므로,
     **실 `SimServer`(RTU 모드)**를 fake-serial 페어에 결선해 WCS `ModbusRtuMaster`와 in-process
     왕복(폴 Online 스냅샷 + C/R 핸드셰이크 1건 + ClearR + 의미 검증 1개 이상: R_Seq==C_Seq / Ready
     블립 없음 / 잔류 프리셋 중 택). 물리 COM 불요 → CI에서 항상 실행. (기존 VT-2는 hand-rolled
     `ModbusRtuServer`를 썼을 뿐 실 SimServer 상태기계를 안 태웠음 — 본 (b)가 진짜 신규 가치.)
     이를 위해 SimServer RTU 모드가 **테스트용 주입 `IModbusRtuSerialPort` seam**을 받도록 설계할 것을
     권장(ModbusRtuMaster의 fake-port 생성자 패턴과 동형 — Consistency Over Preference).
   - (c) **실선 통합(환경 게이트, 필수)**: 환경변수 `WCS_RTU_TEST_PORTS=COMx,COMy`(client,server) 지정
     시에만 실 OS 시리얼 스택으로 (b)와 동일 시나리오 스모크. **미지정 시 스킵이 GREEN**
     — 반드시 **기존 프로젝트 스킵 패턴(`LiveMultiAgvRunner`)을 따른다**: `Environment.
     GetEnvironmentVariable` + early-return(+스킵 사유 콘솔/출력). ⚠️ 새 `SkippableFact` 의존성 도입 금지
     (xUnit 2.9.3 동적 Skip 미지원 — 프로젝트는 이미 early-return 방식으로 결정함. 새 패턴 도입 전 확인 원칙).
   - (d) **회귀 0**: 기존 전체 스위트(특히 TCP 경로: PlcGatewayIntegrationTests·HandshakeResidueTests·
     ScenarioTests·E2E) 무변경 GREEN.

────────────────────────────────────────────────────────────────────────
## Non-change Guards (변경 금지 — git diff 0 라인)

- **Wcs.Api / Wcs.PlcGateway / Wcs.Core / frontend**: 프로덕션 코드 0 변경. (WCS Transport 선택은 이미 구현됨.)
- **appsettings.json**: 0 변경. Sim3ds 자체 설정만 신설/사용.
- **FluentModbus 5.3.2 고정**: 버전 변경 금지.
- **SimServer 공개 표면**: Scope 3에 열거한 시그니처 유지(테스트·프리셋·고장주입 호환).
- 허용 변경: `backend/src/Wcs.Sim3ds/**`, `backend/tests/Wcs.Tests/**`(신규 테스트 + 필요한 테스트
  인프라), `docs/**`(리허설 문서), 그리고 Sim3ds 프로젝트 **자체** 설정 파일(신설 시).

────────────────────────────────────────────────────────────────────────
## Cautions (계약 명기 — Generator 필독)

- **ModbusRtuServer 실 API를 코딩 전 확인**(추측 금지, 5.3.2 고정). 테스트에서 확인된 실서명:
  `new ModbusRtuServer(unitId, isAsynchronous: true)`, `GetHoldingRegisters(unitId)`,
  `Start(IModbusRtuSerialPort)`. **물리 COM 기동은 `Start(string portName)` 오버로드**로 추정되나
  5.3.2에서 실제 시그니처(및 SerialPort 파라미터 지정 방식 — BaudRate/Parity/StopBits를 서버가
  어떻게 받는지)를 반드시 확인 후 구현. TCP 서버는 `AddUnit(id)`+`Start(IPEndPoint)`인데 RTU 서버는
  ctor에서 단일 unitId를 받는 등 **API가 발산**하므로 추상화 시 이 차이를 흡수해야 한다.
- **SimServer 레지스터 버퍼 결합**: `GetHoldingRegisters(UnitId)` 접근이 TCP 인스턴스에 묶여 있으니
  추상화 후에도 Flush/Pull·프리셋·`SetRResidue`·`ReadSnapshot`이 그대로 동작하는지 검증.
- **COM 포트 부재 환경**: 이 머신 현재 COM 포트 0개·com0com 미설치 → (c) 실선 테스트는 CI/일반 러너에서
  항상 실행 불가. **전체 스위트가 스킵 포함 GREEN**이어야 하고, **스킵 사유가 출력**돼야 한다. Evaluator는
  포트 페어가 없으면 (c)를 "환경 부재로 Skip됨이 정상 동작"으로 검증한다(스킵 로직 자체가 검증 대상).
  단 (b) fake-serial 통합은 환경 무관 실행되므로 RTU 의미 검증의 실질 게이트다.
- **com0com 설치는 scope 밖**(관리자 권한 드라이버 설치 = 사용자 결정/작업). 문서로만 안내.
- **기준 테스트 카운트 = 178**(PR #31+#32 병합 반영) — 기동 시 `dotnet test`로 실제 재확인. 완료 후 =
  178 + 신규 단위/CI 통합 테스트, (c) 실선 테스트는 환경 부재 시 스킵으로 보고.

────────────────────────────────────────────────────────────────────────
## Evaluation Criteria (Evaluator 판정 기준 — Library/CLI 4기준, ★★★ 최우선)

1. **API Ergonomics / 전송 선택 명료성** (★★★): Transport 선택·RTU 옵션이 직관적이고 하드코딩 0.
   콘솔이 "지금 어느 전송·포트로 리스닝 중"인지 즉시 알려주는가. 잘못된 값은 명확한 예외인가.
2. **Architecture Originality / 추상화 품질** (★★★): 전송 교체가 SimServer 상태기계·프리셋·고장주입·
   테스트 훅을 오염시키지 않는 깔끔한 seam인가(AI slop·과설계 아님). TCP/RTU 분기가 응집적인가.
3. **Craft** (★★): 엔디안·타임아웃·UnitId 경계, 예외 삼킴 없음(RTU 예외 = 명시 처리), 리소스 정리
   (포트/서버 dispose), 스킵 사유 로깅. 문서의 정확성(설정 예시가 실제로 동작).
4. **Reliability** (★★): 회귀 0(기존 178 GREEN), RTU 의미가 TCP와 동일함을 (b)로 실증,
   COM 부재 환경에서 스위트 안정 GREEN(스킵 포함).

가중: 회귀 0과 "전송만 교체·의미 동일"이 통과의 하드 게이트. 문서 없이 코드만 = 미완.

────────────────────────────────────────────────────────────────────────
## Completion Conditions (Evaluator 통과 최소 조건 — 전부 AND)

- [ ] Sim3ds가 설정으로 Transport=Tcp(기본)|Rtu 선택 가능. 기본 실행이 현행 TCP :1502와 동일.
- [ ] RTU 옵션 전부 설정값(하드코딩 0). 잘못된 Transport = fail-loud.
- [ ] (a) 전송 선택/옵션 파싱 단위 테스트 GREEN.
- [ ] (b) 실 SimServer(RTU) ↔ WCS ModbusRtuMaster fake-serial 왕복 통합 테스트 GREEN(환경 무관):
      폴 Online + C/R 핸드셰이크 1건 + ClearR + 의미 검증(R_Seq==C_Seq 등) 1개 이상.
- [ ] (c) 실선(WCS_RTU_TEST_PORTS) 테스트: 환경 부재 시 스킵 사유 출력 + 스위트 GREEN. (환경 있으면 왕복 GREEN.)
- [ ] (d) 기존 전체 스위트(178 기준) 회귀 0 — TCP 경로·E2E 포함 독립 재실행으로 확인.
- [ ] 콘솔/로그가 전송·엔드포인트를 명시.
- [ ] 문서: 시리얼 페어 준비법(com0com/어댑터 직결) + WCS↔Sim RTU 리허설 절차 + 설정 예시(양쪽).
- [ ] Non-change Guards 준수: git diff가 Wcs.Api/PlcGateway/Core/frontend/appsettings.json 0 라인.
- [ ] dotnet build 경고 0(신규분), 예외 삼킴 0.

────────────────────────────────────────────────────────────────────────
## Parallel Modules
N/A (단일 모듈 — Wcs.Sim3ds + 그 테스트/문서. 경계 깨끗한 병렬 분할 없음). → Generator 1인.

## Evaluation Dimensions
functional only (표면이 시뮬레이터/CLI·프론트/보안/성능 민감 표면 없음). → Evaluator 1인, 1/1/1 기본 유지.

────────────────────────────────────────────────────────────────────────
## Detected Project Type: Full-stack

(레포 신호에 근거 — frontend/ 브라우저 진입점 + Wcs.Api 서버 라우트가 같은 레포에 공존.
사용자 표현이 아니라 구조로 판별.)

> **투명성 노트(S-HANDSHAKE-RESIDUE 계약 선례 준용)**: 레포 전체는 Full-stack이나, **이 스프린트가
> 변경하는 표면은 백엔드/시뮬레이터 단독**이다 — 독립 콘솔 실행체(`Wcs.Sim3ds`, OutputType=Exe) +
> 그것의 타입 공개 API(`SimServer`/`Options`, xUnit이 소비) + docs. **프론트엔드 0·HTTP 엔드포인트 0·
> DB 0·appsettings.json 0 변경.** 따라서 아래 Full-stack 슬롯 중 순수 프론트 Web/UI 슬롯은
> 근거를 달아 N/A로 두고, 시뮬레이터/백엔드 표면의 시나리오와 전송 경계를 넘는 데이터 플로우
> 시나리오를 실효 검증으로 채운다.

## Verification Scenarios (Full-stack 슬롯 — 필수)

### Applicable Web/UI scenarios (프론트 표면)
- **N/A** — 이 스프린트는 프론트엔드 파일을 전혀 건드리지 않는다. Sim3ds는 헤드리스 콘솔 시뮬레이터로
  브라우저 표면이 없다. (프론트 회귀는 Non-change Guard의 git diff 0로 보장.)

### Applicable Backend/API scenarios (시뮬레이터/백엔드 표면 — 이 스프린트가 건드리는 "서버측" 표면)
"엔드포인트" 대신 **콘솔/설정 진입점 + Sim3ds 타입 공개 표면 + WCS↔Sim Modbus 왕복**을 시나리오로 열거:
- **S1 — TCP 회귀(기본 경로 불변)**: Transport 미지정/Tcp → Sim3ds가 여전히 TCP :1502 바인딩,
  현행과 동일 동작. 증거: PlcGatewayIntegrationTests·HandshakeResidueTests·ScenarioTests·E2E
  독립 재실행 GREEN + 콘솔 "…기동 TCP 127.0.0.1:1502".
- **S2 — RTU 선택 + 옵션 파싱(해피)**: Transport=Rtu + RTU 옵션(PortName/BaudRate/Parity/StopBits/
  UnitId) 설정/CLI 파싱, 하드코딩 0. 증거: 단위 테스트가 파싱 결과 단언 + 콘솔
  "…기동 RTU COMx 9600/Even/One unit=1".
- **S3 — RTU 의미 동일성(해피, 핵심)**: 실 SimServer(RTU) ↔ WCS ModbusRtuMaster fake-serial 왕복 —
  폴 Online 스냅샷 → CellAssign → C_Flag=1 → Sim R 에코(R_Seq==C_Seq, Ready 블립 없음) →
  ClearR → R_Flag=0. 증거: (b) 통합 테스트 GREEN(환경 무관) + 타임라인 로그.
- **S4 — 에러/스킵 경계**: 잘못된 Transport 값 → fail-loud 예외(단위 테스트). 그리고 COM 페어
  부재(WCS_RTU_TEST_PORTS 미설정) → (c) 실선 테스트가 **스킵(사유 출력)**, 전체 스위트 GREEN 유지.
  이 스킵 동작 자체가 검증 대상.

### End-to-end 데이터 플로우(2+ 계층/프로세스 교차)
- WCS 마스터 프로세스(`ModbusRtuMaster` → `PlcPollingService` + `HandshakeOrchestrator`) ↔
  **시리얼 경계** ↔ Sim3ds 슬레이브(`ModbusRtuServer` + SimServer 상태기계)의 전 구간 C/R 핸드셰이크
  왕복. **CI(환경 무관)**: fake-serial 페어로 실 SimServer를 태워 결정적 검증(S3). **실선(환경 게이트)**:
  WCS_RTU_TEST_PORTS 지정 시 실 OS 시리얼 스택으로 동일 왕복(프레이밍·타임아웃 실측 커버리지 추가);
  미지정 시 스킵. — "레지스터가 시리얼을 건너 WCS↔Sim을 왕복한다"는 흐름을 기술(도구가 아니라 흐름).

> Planner self-check — Detected project type: Full-stack. Required scenario slots: 3 (Applicable Web/UI scenarios [N/A+사유], Applicable Backend/API scenarios [S1~S4], End-to-end 데이터 플로우 [WCS↔serial↔Sim]). All slots filled: yes.

────────────────────────────────────────────────────────────────────────
## Questions (사용자 확인 필요 — novel 결정, 선택지+권장안)

**Q1. Sim3ds 설정 제공 방식** *(진짜 결정 — 현재 `Program.cs`가 Options를 하드코딩. "appsettings/환경변수
Sim3ds:*로 제공"이라는 주석은 stale이고 Sim3ds는 자체 config 파일·바인딩이 없다. RTU 도입은 이 갭을
닫는 일이기도 함.)*
- (A) **CLI 인자만** — 예: `dotnet run --project ...Wcs.Sim3ds -- --transport rtu --port COM6 --baud 9600`.
  가장 가볍고 리허설에 빠름. 새 파일 없음. 단 절대규칙 #7("전부 설정값") 철학과 약함.
- (B) **appsettings.Sim3ds.json(자체 파일) + 환경변수** — 절대규칙 #7·기존 API의 Sim3ds 블록 형태와 정합.
  약간 무겁고 리허설 때 파일 편집 필요.
- (C) **둘 다** — appsettings(기본값, Transport=Tcp로 현행 보존) + CLI/환경변수 오버라이드로 리허설
  시 `--transport rtu --port COMx` 한 줄 전환. ← **권장**. 근거: (i) 절대규칙 #7을 지키면서 stale
  주석의 약속을 실제로 구현, (ii) 기본 Transport=Tcp라 현행 `dotnet run` 동작 바이트 동일 보존,
  (iii) 목요일 리허설에서 파일 편집 없이 CLI로 즉시 RTU 전환.

**Q2. 리허설 문서 위치**
- (A) `docs/SPEC.md` §6/§7-A에 추가 — 스펙과 한 곳.
- (B) **`docs/RTU-REHEARSAL.md` 신규(SPEC §6/§7-A에서 링크)** ← **권장**. 근거: SPEC.md는 "응축 스펙"
  성격이고, 단계별 운영자 리허설 절차(com0com 설치·결선·기동·확인)는 절차 문서라 별도 파일이 스펙
  가독성을 해치지 않는다. SPEC에는 1줄 포인터만 추가.

**Q3(확인만 — 권장 채택 예정)**: 실선 테스트 환경 게이트 변수명 = `WCS_RTU_TEST_PORTS=COMx,COMy`
(client,server 순), 스킵은 기존 `LiveMultiAgvRunner` early-return 패턴 준수(새 의존성 없음). RTU
기본 파라미터는 WCS `Sorters[0]` placeholder와 정합(9600/Even/One/UnitId=1), PortName은 기본값 없이
명시 요구. 이견 없으면 이대로 진행.

────────────────────────────────────────────────────────────────────────
## 참고 — 이 스프린트에서 활용/보존할 기존 자산
- `backend/tests/Wcs.Tests/FakeSerialPort.cs` — `IModbusRtuSerialPort` in-memory 페어(이미 존재).
  (b) 통합 테스트가 실 SimServer(RTU)를 여기에 결선. `FakeSerialPortPair.Create()` 재사용.
- `backend/tests/Wcs.Tests/RtuTransportTests.cs` VT-2 — WCS 마스터↔hand-rolled RtuServer 왕복 선례
  (본 스프린트 (b)는 실 SimServer 상태기계로 격상).
- `backend/tests/Wcs.Tests/PlcGatewayIntegrationTests.cs` — SimServer(TCP)↔GW 통합 패턴·teardown
  (Writer.TryComplete → StopAsync → DisposeAsync) 준용.
- `backend/tests/Wcs.Tests/E2E/LiveMultiAgvRunner.cs` — 환경변수 게이트 + early-return 스킵 선례(준수).
- `backend/src/Wcs.PlcGateway/Modbus/ModbusRtuMaster.cs` — fake-port 주입 생성자 패턴(SimServer RTU
  seam 설계의 동형 참조).

── ★ 사용자 확정 (2026-07-07, Phase 1→2 게이트 통과) ─────────────────────────
Q1 설정 방식: **C안 — appsettings.Sim3ds.json 기본값(Transport=Tcp 현행 보존) + CLI/환경변수 오버라이드**
   (목요일 현장: `dotnet run -- --transport rtu --port COMx` 한 줄 전환)
Q2 문서: **B안 — docs/RTU-REHEARSAL.md 신설**, SPEC §6에서 링크
Q3 확인: 환경 게이트 변수 WCS_RTU_TEST_PORTS=COMx,COMy / RTU 기본값은 WCS Sorters[0]
   placeholder 정합(9600/Even/One/UnitId=1), 기본 PortName 없음(미지정 시 fail-loud)
브랜치: feat/sim3ds-rtu — PR #33 위 스택. ⚠ 병합 순서 #33→본 PR (본 PR은 draft로 생성 예정)
