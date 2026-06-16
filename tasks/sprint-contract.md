# Sprint Contract — S-M2 (PLC 게이트웨이 + 시뮬레이터 핸드셰이크)

## Goal
시뮬레이터(Sim3ds)를 상대로 WCS가 실제 Modbus TCP 왕복을 수행하는 라이브 루프를 세운다.
목표: "셀 지정(C) → (틸트→분류→복귀) → 적재 완료(R)" 1건이 실제 Modbus 프레임으로 왕복하고,
`R_Seq == C_Seq` 대사가 성공하며, 레지스터 변화가 타임라인 로그로 남는 것을 **통합 테스트**로
재현 가능하게 증명한다. 판정 로직(Wcs.Core.DepositDecider)은 M1에서 완성·검증되었고 이번에 불변.
본 스프린트는 그 결정을 실제 PLC 레지스터에 안전하게(단일 큐·RMW·핑퐁 차단) 반영하고,
PLC가 돌려준 결과를 폴링·대사·클리어하는 메커니즘만 책임진다.

## Implementation Scope (WHAT — HOW는 Generator 재량)
**(A) Sim3ds — 3DS PLC 시뮬레이터** (`src/Wcs.Sim3ds/`)
- FluentModbus ModbusTcpServer로 HR 7워드(D0~D6) 노출, :1502 수신(호스트/포트 설정값). FC03/06/16.
- SPEC §6 동작(정정본): C_Flag=1 감지→C 읽고 D0·D1·C_Flag=0 클리어→TiltDelay→분류 시작(Ready=0+TgtFloor=0 클리어)→SortDuration→D2=R_CellNo·D3=R_Seq(=받은 C_Seq)·R_Flag=1.
  복귀 이동 남으면(TgtFloor≠0 && TgtFloor≠CurFloor) Ready=0 유지하고 곧바로 이동, 그 외에만 Ready=1.
  분류 중 아닐 때 && TgtFloor≠0 && ≠CurFloor → 이동(Ready=0)→MoveDuration→CurFloor=TgtFloor(TgtFloor 유지)→Ready=1.
- **정정 §6 불변식(필수)**: 분류·이동 **직렬**(분류 중 이동 금지), 분류 종료 시 Ready=1 **블립 금지**.
- 설정값: TiltDelay·SortDuration·MoveDuration·초기 CurFloor.
- 고장 주입(테스트 제어 가능): (1) R_Seq 불일치 (2) R_Flag 지연 (3) 무응답(OFFLINE 유발).
- 레지스터 변화 + WCS 쓰기 수신을 타임스탬프 로그로. **in-process 기동·구성·고장주입·종료 가능한 타입**으로 분리(Program.cs entrypoint만으론 테스트 불가).

**(B) PlcGateway — 폴링 + 단일 쓰기 큐 + RMW** (`src/Wcs.PlcGateway/`)
- 폴링 BackgroundService: PollIntervalMs 주기 D0~D6 일괄 FC03 → PlcSnapshot.FromRegisters로 캐시 갱신→Latest 게시. 연속 실패 OfflineAfterFailures회/소켓 예외→Online=false(OFFLINE), 복구 시 true.
- **단일 쓰기 큐 컨슈머(절대규칙 #1)**: PlcWriteQueue Channel 단일 리더 한 곳에서만 모든 Modbus 쓰기. PlcWrite 처리: SetTgtFloor(쓰기 직전 TgtFloor==0 재확인, ≠0이면 스킵=핑퐁 차단)/CellAssign(C_Flag==0 확인→C_CellNo·C_Seq→D4 RMW C_Flag set)/ClearR(R_CellNo·R_Seq=0+D4 RMW R_Flag clear).
- **RMW 헬퍼**: D4 비트 set/clear = ReadD4→해당 비트만 수정(상대 비트 보존)→WriteD4. D4 쓰기는 단일 컨슈머에서만.
- 시간값(WriteTimeoutMs 등) 전부 appsettings. R_Flag 상승(0→1) 통지 표면(이벤트/Channel).

**(C) C/R 핸드셰이크 오케스트레이터** (Wcs.PlcGateway 신규)
- SPEC §4: C(C_Flag==0→C_CellNo·C_Seq→C_Flag=1, 쓰기는 모두 큐 경유 — 직접 Modbus 호출 금지) → R 폴링(RFlagPollMs, 타임아웃 RFlagTimeoutMs) → R_Flag=1 시 R 읽기→R_Seq==보낸 C_Seq 대사(불일치=알람) → R 클리어(ClearR 큐 투입). C_Seq 매 건 증가, 한 건씩 직렬. 통합 테스트가 1건 시작·결과(성공/불일치/타임아웃) 관찰 가능한 진입점.

**(D) 설정·배선**: appsettings Plc/Timing 키 사용, 신규 시간값은 키 추가(하드코딩 금지). Sim3ds 설정값도.
**(E) 테스트 배선**: tests/Wcs.Tests에 Wcs.PlcGateway·Sim3ds 기동 참조 추가. 신규 통합 테스트 파일. DepositDeciderTests.cs 무변경(M1 GREEN 유지).

## Out of Scope
- **M3 HTTP API**(IF-05/08/10·DTO·예약·멱등) 구현/수정 안 함(Wcs.Api 코드 무변경, appsettings 키 추가만 허용). **IF-08 판정을 라이브 루프에 결선(스냅샷→Decide→큐)도 M3** — 본 스프린트는 메커니즘만.
- **M4 영속화**(Wcs.Data/EF Core/ERD/마이그레이션) 없음 — 결과는 로그·메모리만.
- **M5 운영**(Windows Service/Serilog/재시작 동기화) 없음 — ILogger/콘솔로 충분.
- **판정 로직 무변경**: Wcs.Core 동작/시그니처 불변, 기존 15 테스트 GREEN 유지.
- 다중 AGV 경합·전체 S1~S9 자동화는 M4. 본 스프린트는 정상 왕복 1건 + 핵심 메커니즘 시나리오만.

## Detected Project Type: Backend/API
M2엔 HTTP 엔드포인트 없음(M3). 검증 표면 = **라이브 Modbus 왕복 통합 테스트**(Sim3ds TcpServer ↔ PlcGateway). Backend/API 슬롯을 "엔드포인트" 대신 "통합 시나리오"로 채움(TASKS.md M2 Done=통합 테스트에 부합).

## Evaluation Criteria
1. **내부 계약 설계(★★★)**: IPlcGateway 표면·PlcWrite·핸드셰이크 결과 타입(성공/불일치/타임아웃/OFFLINE 구분)의 일관성. 절대규칙 #1(모든 쓰기 단일 큐)이 구조적으로 강제되는가.
2. **아키텍처(★★★)**: 폴링·쓰기·핸드셰이크 책임 분리, 단일 쓰기 컨슈머 패턴 정확, RMW 한 곳 집중. 불필요한 추상화/중복 회피.
3. **장인성(★★)**: 예외 안 삼킴(통신 실패→OFFLINE 명시 전이), 레지스터 타임라인 로그, 시간값 전부 설정(하드코딩 0), 테스트 결정성(고정 sleep 대신 폴링/대기 — flaky 회피).
4. **기능(★★)**: M2 Done 충족 — 정상 왕복 1건 성공, R_Seq==C_Seq 검증, 레지스터 타임라인 로그, Seq 대사로 유실·중복 검출.

## Completion Conditions (전부 필수)
- `dotnet build Wcs.sln` 성공(net10.0). `dotnet test Wcs.sln` 전부 GREEN — M1 회귀 0 + M2 통합 테스트 GREEN. (검증 환경 PowerShell 권한 거부 → Bash로 `cd "<절대경로>" && dotnet ...`)
- 아래 Verification Scenarios가 전부 **자동화 통합 테스트**로 GREEN(수동 1회성 아님).
- 절대규칙 위반 0: 모든 Modbus 쓰기 단일 큐 경유(테스트 입증), D4 RMW(상대 비트 보존), TgtFloor≠0일 때 SetTgtFloor 쓰기 0, WCS가 TgtFloor 클리어 안 함.
- 하드코딩 시간값 0. Wcs.Core·Wcs.Api·Wcs.Data 동작/시그니처 무변경(설정 키 추가 제외).
- 레지스터 타임라인 로그 실제 출력(C 쓰기→PLC 클리어→분류→R 세팅→WCS 클리어 타임스탬프).

## Verification Scenarios (통합 테스트 — Sim3ds ↔ PlcGateway 실제 Modbus 왕복)
- **IT-1 정상 왕복**: 핸드셰이크 1건(CellNo·증가 C_Seq) → 타임라인 ①WCS C기입+C_Flag=1 ②Sim C읽고 클리어→TiltDelay ③분류 Ready=0+TgtFloor 클리어 ④Sim R기입+R_Flag=1(복귀 없으면 Ready=1) ⑤WCS R_Flag 감지→대사 성공→R 클리어. 단언: 결과=성공, 대사 일치, 종료 시 C_Flag·R_Flag=0, 타임라인 로그 존재.
- **IT-2 R_Seq 대사**: 일치=성공 / R_Seq 불일치 주입→결과=알람(불일치)·사유 반환·알람 로그.
- **IT-3 단일 쓰기 큐 직렬화**: 다수 쓰기 투입→단일 컨슈머 순차 통과(동시 쓰기 0), D4 비트 set/clear 후 상대 플래그 비트 보존(RMW), SetTgtFloor는 TgtFloor==0일 때만 기입.
- **IT-4 OFFLINE 전이**: 무응답/끊김 주입→연속 실패 후 Latest.Online==false, 재개 시 true. 예외 안 삼킴.
- **IT-5 R_Flag 타임아웃**: R_Flag 지연 주입(또는 짧은 RFlagTimeoutMs)→타임아웃 종료, 방침 P1까지만 단언.

## 미확정 사항 처리 방침 (SPEC §7-A — 추측 금지)
- **(P1) R_Flag 타임아웃 초과**: RFlagTimeoutMs 초과 시 RFLAG_TIMEOUT 알람 + PLC 상태 재확인(Ready·Online) + 타임아웃 결과 반환까지만. **재시도 vs 포기는 미구현**(사용자 확정 대기).
- **(P2) C_Flag=1 대기 타임아웃**: 상한을 설정값(신규 CFlagTimeoutMs)으로, 초과 시 알람+상태 재확인. 후속(재시도/리셋)은 3DS 협의 대기.
- **(P3) TgtFloor 잔류 해소**: 본 스프린트 Out of Scope(M4 S4에서 정의). WCS는 TgtFloor 클리어 금지 유지. 인지만 기록.
- **(P4) 레지스터 주소/오프셋**: RegisterMap 상수(0~6) 그대로. 현장 확정 시 RegisterMap만 수정.

> Planner self-check — Detected project type: Backend/API. Required scenario slots: 3 (integration-scenarios list / expected register timeline per scenario / fault-injection scenarios). All slots filled: yes.
