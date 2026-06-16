# Sprint Contract — M3 (API 3종: IF-05 / IF-08 / IF-10) + S-RTU MINOR 4건 정리

## Goal
501 스텁(`Program.cs`)을 **실제 HTTP 엔드포인트 3종**으로 교체. RCS(클라)→WCS(서버) REST/JSON — IF-05 목적지 조회 / IF-08 투입 가부 / IF-10 투입 보고.
M1·M2·S-RTU 완성 자산(판정 `DepositDecider`·게이트웨이 `IPlcGateway` 스냅샷·단일 쓰기 큐·`HandshakeOrchestrator`·전송 추상화)을 **API 계층에 DI 결선**해 라이브 IF-08 판정 + IF-10→IF-11 트리거까지 통합.
M3 본질: 완성된 하부 위에 **(1) HTTP 표면, (2) DB 없이(메모리 우선) 오더·목적지·예약·셀 기준정보를 인터페이스 뒤에 두어 M4에서 DB로 교체**. 판정 로직·전송 추상화는 **동작 변경 0**(READY는 API 계층 주입, 전송은 설정/DI). 부수: S-RTU 코드리뷰 MINOR 4건 정리.

## Implementation Scope (WHAT)
**(A) DTO 정정 `Dtos.cs`** (원본 HTML·SPEC §3·§7-B 정렬)
- IF-05 `DestinationQueryRequest`: **`AgvNo` 추가**(최종 pId·agvNo·barcode·inductionNo·qty·timeStamp).
- IF-05 `DestinationQueryResponse`: OK reason 주석 NORMAL·BUSY·FULL·PAUSED / NG OVER·COMPLETED·NO_DEST·OFFLINE. NG 시 chuteNo **null 포함** 직렬화.
- IF-08 `DepositPermissionRequest`: 원본 pId·chuteNo·agvNo. timeStamp는 **nullable 선택필드**(`string? = null`).
- IF-08 `DepositPermissionResponse`: allowed=true→reason="READY" 주석.
- IF-10 `DepositReportRequest`: 원본 pId·barcode·chuteNo·agvNo. qty·timeStamp **nullable 선택필드**.

**(B) 메모리 기준정보 리포지토리 (M4 교체점) `Wcs.Api/` 신규** — DB 없이 IF-05·IF-11 구동, 인터페이스+시드:
- `IOrderRepository`: 바코드/오더 매칭·목적지 조회·상태 판단(OVER/COMPLETED/NO_DEST/OFFLINE)·OK 시 예약 차감(중복 배정 방지).
- `IDepositRecorder`(IF-16 통합): IF-05 OK/NG **양쪽 투입 기록**(NG=DENIED 상당). M4서 piece/piece_event DB로.
- `ICellSelector`(IF-11): ①활성 셀 재사용 →②소속 빈 셀 →③없으면 FULL 요소.
- `IAgvFloorResolver`: agvNo→층, **M3는 설정 `Floors:AgvNoToFloor`**, M4서 agv.floor로. 매핑 없는 agvNo는 명시적 거부(추측 금지).
- 인메모리 상태 thread-safe.

**(C) 엔드포인트 `Program.cs`** (501→실제, minimal API)
- IF-05: 검증(pId 1~30000·필수)→오더 매칭→목적지·상태→(NULL이면 빈 슈트 AUTO, 없으면 NG·NO_DEST)→OK chuteNo+reason+예약차감 / NG reason+chuteNo=null. OK·NG **무관 투입기록**. 200(가부는 result 필드)/400.
- IF-08: 검증→`IPlcGateway.Latest`(논블로킹)→`IAgvFloorResolver` agvFloor→`DepositDecider.Decide`→WriteTgtFloor면 `EnqueueAsync(SetTgtFloor)` **완료 대기 X**→allowed=true→**reason="READY" 주입**(Core 무변경)/false→`Reason.ToWire()`. 즉시 200/400.
- IF-10: 검증→**멱등**(pId 중복 무해)→투입기록→3D(SORTER_3D)면 `ICellSelector`+`HandshakeOrchestrator` **IF-11 트리거**(슈트는 트리거 없음). **즉시 OK 반환**, 핸드셰이크는 백그라운드(완료 추적 M4). 200/400.

**(D) DI 배선 `Program.cs`+`appsettings.json`**: Plc/Timing 바인딩 → `ModbusMasterFactory.Create`→`IModbusMaster` / `PlcWriteQueue` 싱글톤 / `PlcPollingService`를 `IPlcGateway`+**IHostedService 기동**(M2 수동 Start/Stop·통합테스트 호환 유지 — 회귀 0) / `HandshakeOrchestrator` / 인메모리 리포지토리 시드 / `Floors:AgvNoToFloor`. **Wcs.Data DbContext는 M4 — 안 함.** appsettings `Plc:Transport`는 명시값(dev/sim=Tcp) 유지, 키 추가만.

**(E) S-RTU 코드리뷰 MINOR 4건 정리**(동작 변경 0): (1)ModbusRtuMaster fake-mode Connect 명명(`_externallyOwnedPort`)+XML 주석 (2)RtuTransportTests.cs:98 Task.Delay(50) 제거 (3)FakeSerialPort sync Read fail-loud/문서화 (4)ModbusRtuMaster 엔디안 필드 통일(기본 BigEndian).

**(F) 테스트 `Wcs.Tests.csproj`+신규**: `Wcs.Api` ProjectReference + `Microsoft.AspNetCore.Mvc.Testing` 추가, `public partial class Program` 노출. WebApplicationFactory로 IF-05/08/10 검증, IF-08 라이브는 Sim3ds 상대 실제 스냅샷.

## Out of Scope (M4 경계 명시)
M4 영속화 일체(Wcs.Data EF Core·엔티티·마이그레이션·16테이블·DB 시드 — M3는 인메모리 인터페이스+시드까지) / agvFloor DB 단일진실 전환(M3 설정 기반, 추상화만 마련) / S1~S9·FULL 정밀계산·오더완료 계산(M4) / 판정 로직(Wcs.Core) 동작 변경(READY API 주입) / 전송 추상화·핸드셰이크 로직 변경(결선만) / M5 운영 / 다중 소터 라우팅 / 인증·pId 정책(RCS Q 대기).

## Detected Project Type: Backend/API
실제 HTTP 엔드포인트 생성 — WebApplicationFactory 통합 테스트로 검증.

| 엔드포인트 | happy | error | 상태코드 |
|---|---|---|---|
| IF-05 destination-query | OK·chuteNo·reason∈{NORMAL,BUSY,FULL,PAUSED}·예약차감·기록 | NG·reason∈{OVER,COMPLETED,NO_DEST,OFFLINE}·chuteNo=null·NG여도 기록 / 검증실패 | 200(가부=result) / 400 |
| IF-08 deposit-permission | allowed=true·reason="READY" | false·WRONG_FLOOR(+SetTgtFloor 큐)/BUSY/FULL/PAUSED/OFFLINE / 검증실패 | 200(가부=allowed) / 400 |
| IF-10 deposit-report | OK; 3D면 IF-11 트리거 | 중복 pId 멱등 OK / 검증실패 | 200 / 400 |

## Evaluation Criteria
1. **엔드포인트 정합(★★★)**: 원본 HTML·SPEC §3 필드·reason·경로 일치(IF-05 agvNo, IF-08/10 원본필드+nullable, allowed=true→READY API 주입, NG chuteNo null).
2. **판정 재사용 무변경(★★★)**: Decide 결과 그대로 매핑, Core(ToWire(None)=null) 무변경, 라이브 스냅샷 판정, WriteTgtFloor면 큐 투입(완료 대기 X).
3. **M4 경계(★★★)**: 오더·목적지·예약·셀·agvFloor 인터페이스 뒤+인메모리 시드. Wcs.Data·EF Core 참조 0(grep). 교체점 1지점.
4. **회귀 0(★★★)**: 기존 28(Decider 15+M2 9+RTU 4) 단언·코드 변경 없이 GREEN, split 감소 없음. Core·PlcGateway 로직 무변경(MINOR 4 제외).
5. **IF-10→IF-11 트리거(★★)**: 3D 보고 시 핸드셰이크 트리거 관찰, 슈트는 트리거 0.
6. **장인성·설정(★★)**: 하드코딩 0, 예외 안 삼킴, thread-safe, MINOR 4 정리(동작 0).

## Completion Conditions (전부 필수)
- `dotnet build Wcs.sln` 성공 / `dotnet test Wcs.sln` 전부 GREEN(막히면 Bash). **기존 28 회귀 0(명시)** + 신규 통합테스트로 총계 증가.
- WebApplicationFactory류 자동 통합테스트: IF-05/08/10 happy·error + IF-08 라이브(Sim3ds 스냅샷→Decide→큐→응답) + IF-10→IF-11 트리거. 비결정 요소는 수회 연속 GREEN.
- IHostedService 결선이 M2 수동 Start/Stop 경로를 안 깸(회귀 0). DB(Wcs.Data) 참조 0. Core·전송·핸드셰이크 로직 무변경(MINOR 4=명명/주석/엔디안필드/sleep제거, 동작 0).
- DTO 원본 정렬(agvNo·nullable·READY 주석·NG null). 하드코딩 0.
- **커밋 전 `git rev-parse --abbrev-ref HEAD`로 `feat/m3-api` 확인**(lessons.md 2026-06-16 — develop 직접 커밋 0).

## Verification Scenarios (자동화)
- VS-1 IF-05 happy: 시드 매칭→200 OK·chuteNo·NORMAL·예약차감·기록.
- VS-2 IF-05 error: 미존재/할당불가→NG·chuteNo null·NG여도 기록 / pId범위·필드누락→400.
- VS-3 IF-08 라이브(핵심): Sim3ds 연결, 다른 층 agvNo→false·WRONG_FLOOR+TgtFloor 기입 관찰→이동완료 후 재호출→true·**READY**.
- VS-4 IF-08 분기: Ready=0→BUSY / OFFLINE 스냅샷→OFFLINE / 검증실패→400 / WriteTgtFloor=false면 큐 투입 0.
- VS-5 IF-10 happy+멱등: 슈트 보고→OK, 같은 pId 재보고→OK 상태무변경.
- VS-6 IF-10→IF-11(핵심): 3D 목적지 보고→핸드셰이크 셀지정 트리거 관찰 / 슈트→트리거 0(대조).
- VS-7 회귀: 기존 28 전부 GREEN.

## 미확정 처리 (추측 금지)
- IF-08 timeStamp / IF-10 qty·timeStamp: nullable 선택필드, RCS 미전송 허용(§7-B 확정 대기).
- WcsHold FULL/PAUSED: M3 기본 None + 기준정보 PAUSED만, **FULL 계산 M4**.
- agvNo 매핑 누락: 명시적 거부(추측 금지).
- IF-10→IF-11 완료추적·piece상태·알람·sorter_command: **M4**. M3는 트리거까지.
- IF-05 OFFLINE 소스·RCS Q1~7·레지스터 주소·P1/P2/P3: M2/SPEC §7 무변경.

> Planner self-check — Detected project type: Backend/API. Required scenario slots: 7 (IF-05 happy/error, IF-08 happy/error+live, IF-10 happy/idempotent+IF-11 trigger, regression). All slots filled: yes. M3 메모리/M4 경계 선 그음, Core·전송·핸드셰이크 무변경, 28 회귀 0, WebApplicationFactory 전제, MINOR 4 포함 — 전부 반영.
