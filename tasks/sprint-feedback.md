# Sprint Feedback — S-SIM3DS-RTU

**APPROVED** — Evaluator, 2026-07-07 (1 iteration). 전 시나리오(S1~S4 + E2E 왕복) + Completion Conditions 전항 PASS, fresh evidence.

브랜치 feat/sim3ds-rtu. 검증자는 코드 수정/커밋/브랜치 전환 없이 독립 재실행·코드 판독만 수행. 핸드오프 마커 확인: `tasks/sprint-log.md:2199` `## IMPLEMENTATION COMPLETE (Generator, 2026-07-07)`.

---

## 환경 / 기준

- SDK: `dotnet --version` = **10.0.301**(net10.0 정합, 설치 9.0.308·10.0.301). TargetFramework 변경 불요.
- 기동 시 포트 1502/5080 free, Sim3ds/Api/testhost 오펀 0.
- **CLAUDE.md 준수**: COM1 실 PLC 가능성 → Sim은 TCP로만 기동, RTU 실선(--port COMx) 미기동. RTU 의미 검증은 fake-serial(b)로만.

## 빌드

```
dotnet build backend/Wcs.sln → 빌드했습니다. 경고 0개 / 오류 0개 (전 프로젝트, 경과 3.01s)
Wcs.Sim3ds → 경고 0 / 오류 0
```
- csproj diff: FluentModbus **5.3.2 고정 유지**(변경 0). 추가분은 Microsoft.Extensions.Configuration(.Json/.EnvironmentVariables/.Binder) 10.0.0 + appsettings.Sim3ds.json Content 복사 — 전부 additive.

## 전체 테스트 (≥2회, fresh)

```
RUN #1: 통과!  - 실패: 0, 통과: 189, 건너뜀: 0, 전체: 189, 기간: 13s
RUN #2: 통과!  - 실패: 0, 통과: 189, 건너뜀: 0, 전체: 189, 기간: 15s
```
- 189 = 기준 178 + 신규 11. 회귀 0. **건너뜀 0** — C1 실선 게이트는 early-return 패턴(xUnit 2.9.3 동적 Skip 미사용·프로젝트 convention)이라 "통과"로 계수됨. SkippableFact 신규 의존성 도입 0(계약 준수).
- 신규 타이밍 취약(Sim3dsRtuTests 11건) 표적 **5회 반복 → RUN1~5 전부 11/11, flake 0**.

---

## 시나리오별 PASS/FAIL (raw evidence)

### S1 — TCP 회귀(기본 경로 불변) : **PASS**
- 실 프로세스 스모크: `dotnet run --project backend/src/Wcs.Sim3ds`(인자 없음) →
  콘솔 `[HH:mm:ss.fff] Sim3ds 서버 기동 TCP 127.0.0.1:1502` + `Sim3ds 기동 완료 (transport=Tcp)`.
  `Get-NetTCPConnection -LocalPort 1502` = **Listen**(바인딩 확인). 종료 후 1502 free.
- 단위 A1: 기본 = Tcp/127.0.0.1/1502 + 타이밍 기본값(Tilt200/Sort500/Move300/CurFloor1/SimLoop20) 보존 단언 GREEN.
- 기존 TCP 경로 스위트(PlcGatewayIntegrationTests·HandshakeResidueTests·ScenarioTests·E2E) 189 GREEN에 포함 — byte-identical 동작.

### S2 — RTU 선택 + 옵션 파싱(해피) : **PASS**
- 단위 A2: `--transport rtu --port COM7 --baud 19200 --parity None --stopbits Two --unit 2` → Transport=rtu·PortName=COM7(--port가 RTU에서 PortName으로 라우팅)·BaudRate19200·ParsedParity=None·ParsedStopBits=Two·UnitId2 단언 GREEN.
- 단위 A3: TCP 모드 `--port 1600` → Port=1600·PortName=null(TCP 라우팅) GREEN.
- 콘솔 RTU 엔드포인트 형식 `RTU COMx 9600/Even/One unit=1` — `RtuSimTransport.Endpoint` 코드 확정 + RTU-REHEARSAL.md §2에 문서화. (실 COM 기동은 COM1 PLC 리스크로 미실행 — 계약 Caution 준수.)
- 하드코딩 0: 전 옵션이 Options record + Sim3dsConfig 해석기 경유. ParsedParity/ParsedStopBits는 `Enum.Parse`(잘못된 값 fail-loud).

### S3 — RTU 의미 동일성(핵심) : **PASS**
- 통합 B1 GREEN: **실 SimServer(RTU, fake-serial)** ↔ WCS `ModbusRtuMaster` 왕복 —
  폴 Online + Ready=1 → `hs.ExecuteAsync(cellNo:5)` → **HandshakeOutcome.Success, SentCSeq==ReceivedRSeq, ReceivedRCellNo=5** → ClearR 후 R_Flag=0 → Ready=1 복귀.
- B2 GREEN: 잔류 프리셋 R_CellNo=20/R_Seq=123 이 RTU에서 동일 관측(RFlag=1·RSeq=123·RCellNo=20).
- **코드 판독으로 "실 SimServer 경유" 확인**: B1/B2는 프로덕션 `new SimServer(opt, fakePort:...)`를 사용. fake는 시리얼 바이트 파이프(FakeSerialPort)뿐이고, 그 위에서 **실 FluentModbus ModbusRtuServer + 실 RunSimLoopAsync/RunSortSequenceAsync 상태기계**가 D0~D6를 구동. hand-rolled mock 아님(기존 VT-2 hand-rolled 서버 대비 진짜 격상).

### S4 — 에러/스킵 경계 : **PASS**
- 실 프로세스 fail-loud (조용한 기본값 진행 0):
  - `--transport rtu`(PortName 없음) → stderr `[Sim3ds] 기동 실패(transport=rtu): ... PortName이 지정되지 않았습니다 ...`, **EXIT=1** + `Sim3ds 서버 종료`(실패 시에도 DisposeAsync 정리).
  - `--tranport`(오타) → stderr `[Sim3ds] 설정 오류: 알 수 없는 스위치 '--tranport'. 지원: --transport --host --port ...`(지원 스위치 전체 목록), **EXIT=1**.
- 단위 A6(알 수 없는 스위치)·A7(알 수 없는 Transport "Serial" → StartAsync throw)·A8(RTU PortName 미지정 → StartAsync throw) 전부 GREEN.
- **C1 실선 게이트 스킵 로직 검증**(WCS_RTU_TEST_PORTS 미설정·이 머신 COM 0개): verbose 실행 캡처 →
  `통과 Wcs.Tests.Sim3dsRtuTests.C1_LiveSerial_Roundtrip_WhenPortsProvided [9 ms]` +
  `[C1] WCS_RTU_TEST_PORTS 미설정 — 실선 시리얼 테스트 건너뜀(... com0com 또는 USB 어댑터 크로스 결선). 형식: WCS_RTU_TEST_PORTS=COMclient,COMserver`.
  early-return(사유 출력) + 전체 GREEN 유지 = 스킵 동작 자체 검증됨(LiveMultiAgvRunner 패턴 준수, 새 의존성 0).

### E2E 왕복 (WCS↔serial↔Sim) : **PASS**
- CI(환경 무관) 왕복 = B1(위). WCS 마스터 → FakeSerialPort 파이프 → FluentModbus RTU 서버 → SimServer 상태기계 전 구간 C/R 핸드셰이크. 실선(환경 게이트) = C1, 환경 부재 시 정상 스킵.

---

## 무변경 가드 (fresh git diff HEAD)

- `git diff --stat HEAD -- Wcs.Api Wcs.PlcGateway Wcs.Core Wcs.Data frontend` = **빈 출력(0줄)**.
- `git diff --stat HEAD -- Wcs.Api/appsettings.json appsettings.Development.json` = **빈 출력(0줄)**.
- `git status --porcelain | Select-String -NotMatch "Wcs.Sim3ds|Wcs.Tests|docs/|tasks/"` = **빈 출력** — 변경이 전부 허용 스코프(Wcs.Sim3ds·신규 테스트·docs·tasks) 내.
- 변경 파일: (수정) Program.cs·SimServer.cs·Wcs.Sim3ds.csproj·docs/SPEC.md(+1줄 §6 링크) / (신규) Sim3dsConfig.cs·SimTransport.cs·appsettings.Sim3ds.json·Sim3dsRtuTests.cs·docs/RTU-REHEARSAL.md.
- SimServer 공개 표면: Options 기존 필드·StartAsync/StopAsync/DisposeAsync·ReadSnapshot·SetRResidue·Inject*(4종) **시그니처 유지**. 추가분(Transport/PortName/Baud/Parity/StopBits/Timeout/UnitId + ParsedParity/StopBits + fake-serial ctor)은 전부 **additive**.

## 문서 (docs/RTU-REHEARSAL.md)

- 실행 가능한 절차 확인: §1 시리얼 페어 준비 2방법(A com0com `setupc.exe install PortName=COM5 PortName=COM6` + scope 밖 관리자 설치 명시 / B USB 어댑터 크로스 결선 TX↔RX·RS-485 A/B 결선) · §2 Sim3ds RTU 기동 CLI 예시 + 우선순위 표 + 콘솔 출력 예시 · §3 WCS Sorters[0] RTU 설정 예시(JSON + 환경변수 오버라이드) + "appsettings.json 미변경" 명시 · §4 체크리스트 8항 · §5 CI fake-serial/실선 게이트 명령 · §6 트러블슈팅 표. SPEC §6 라인95에 `[docs/RTU-REHEARSAL.md](RTU-REHEARSAL.md)` 링크 존재.

## 동시성/seam 사각 코드 판독

- **레지스터 버퍼 참조 경로**: FlushToServerLocked/PullFromServerLocked → `_transport!.Server.GetHoldingRegisters(_unitId)`. `Server`는 ModbusServer 기반(TCP/RTU 공통 API). 엔디안 `BinaryPrimitives.ReverseEndianness` 패턴 불변. TCP는 ctor `AddUnit(unitId)`·RTU는 ctor `ModbusRtuServer(unitId,...)`가 Start 이전 버퍼 확보 → B2(프리셋 Flush가 Start 전 수행)가 GREEN으로 실증.
- **경합/예외 미도입**: `_transport`는 StartAsync에서 sim 루프(`Task.Run`) 생성 **전** 1회 대입(Task.Run 메모리 배리어) → 루프 스레드가 null 관측 불가. Flush/Pull은 전부 `_hrLock` 보유 하에 호출. 잘못된 Transport/PortName은 Factory.Create가 `_transport` 대입 전 throw → StartAsync 전파(fail-loud), StopAsync/DisposeAsync는 `_transport?.` null-safe(A7/A8 정리 시 NRE 0).
- **상태기계·프리셋·Inject 훅 무오염**: RunSimLoop/Sort/Move·SetRResidue·Inject*(RSeqOverride/RFlagDelay/NoResponse/StickyRResidue)은 전송 무관 `_hr` 섀도만 조작 — seam 교체가 이 로직에 손대지 않음(diff 확인).
- **리소스 정리**: TCP/RTU 둘 다 Dispose=server.Dispose(). fake-serial RTU는 주입 포트를 externally-owned로 두어 Dispose 안 함(테스트가 소유·해제) → 이중 dispose 0.

## Minor (비차단 — 다음 sprint Generator 참고, APPROVED 불변)

- **(정보성) 물리 COM 시리얼 파라미터 적용 경로는 이 환경에서 미실증**: RtuSimTransport 물리 ctor가 BaudRate/Parity/StopBits/ReadTimeout/WriteTimeout를 `ModbusRtuServer` 프로퍼티로 설정하나, fake-serial 왕복(b)은 시리얼 협상을 하지 않아 이 파라미터 적용은 안 탄다. baud/parity/stopbits 정합의 실측 커버리지는 C1 실선 게이트 + 목요일 현장 리허설(RTU-REHEARSAL §4 체크리스트 6항)의 몫 — 계약이 의도한 (b)/(c) 분업 경계이며 갭 아님. 현장 리허설 시 파라미터 오정합→OFFLINE/타임아웃을 일부러 유발해 확인 권장(문서에 이미 기재).

## 정리

- 기동 프로세스(Sim3ds TCP 스모크) 종료, 자식 프로세스 kill, **포트 1502/5080 free 확인**, Sim3ds/Api/testhost 오펀 0.

## Code Review Minor (4-Tier Step 4.5 — S-SIM3DS-RTU, 병합 비차단·다음 스프린트 Generator 참조)

- **[Important·비차단] 물리 RTU 경로(Start(portName)+시리얼 파라미터 적용) 자동 테스트 미검증** — SimTransport.cs:82-101. B1/B2는 fake-serial 주입 ctor라 baud/parity/stopbits 미설정, C1 실선은 COM 부재로 스킵. 코드 결함 아님(하드웨어 부재 커버리지 공백, 계약이 스킵을 정상 설계). → 목요일 리허설 체크리스트 6번(파라미터 오정합→OFFLINE) 또는 com0com 페어로 WCS_RTU_TEST_PORTS 스모크 1회로 사전 확인 권장.
1. **RTU-REHEARSAL.md:8 SPEC 참조 라벨 stale** — "§7-A 전송 방식 확정"이나 실제 SPEC 변경은 §6 bullet 1줄. §7-A 절 없음. "§6"으로 정정.
2. **(byte)opt.UnitId 무음 절단** — SimServer.cs:118,134. UnitId>255면 조용히 wrap. 다른 입력은 fail-loud인데 여기만 예외. RTU 유효 1~247 범위 검증 고려.
3. **SetRResidue/Flush/Pull StartAsync 이전 호출 시 NRE** — SimServer.cs:210-220,431,443. _transport가 StartAsync 생성. 테스트 전용·주석 명시라 무해하나 명확한 예외 메시지 권장.
4. **ISimTransport.Server가 ModbusServer 전체 노출** — SimTransport.cs:25. GetHoldingRegisters만 쓰므로 더 좁은 seam 가능. internal이라 표면 봉인됨(실용적 선택).
5. **fire-and-forget 시퀀스 태스크 vs Dispose 경합** — SimServer.cs:275,283(선재·범위 밖). TCP 시절부터 동일 패턴, catch로 흡수. 전송 교체가 새로 만든 경합 아님.
