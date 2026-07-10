# WCS 개발 하네스 — Claude Code 지침

물류 테스트라인 WCS(Warehouse Control System). 로봇(AGV)이 인덕션에서 트레이에 상품을 받아
목적지(슈트 또는 3D Sorter)에 전량 틸트한다. WCS는 가운데에서 양쪽을 중재한다.

```
[RCS(AGV)] --HTTP/JSON--> [WCS(이 프로젝트)] --Modbus RTU/TCP--> [3D Sorter PLC(VEICHI)]
   클라이언트                 API서버 + Modbus 마스터                슬레이브
```

## 절대 규칙 (위반 금지)
1. **PLC 쓰기는 단일 큐(Channel) 하나로만** 직렬화한다. API 핸들러가 Modbus를 직접 호출하지 않는다.
   D4는 플래그/상태가 한 워드에 있어 비트 수정 시 read-modify-write(RMW)가 필요하고, 동시 쓰기는 경합을 일으킨다.
2. **TgtFloor(D6)는 `TgtFloor==0`일 때만 쓴다.** 조건: `TgtFloor==0 && (층 다름 || Ready==0)`.
   진행 중(≠0)엔 절대 덮어쓰지 않는다(핑퐁 차단). FULL/PAUSED/OFFLINE이면 쓰지 않는다.
3. **WCS는 TgtFloor를 절대 클리어하지 않는다.** 클리어는 PLC가 분류 시작(Ready 1→0) 시점에 수행
   (도착 시엔 CurFloor만 기입, TgtFloor 유지 — 도착 즉시 비우면 재기입 왕복 발생).
4. **Ready(D4.2) 의미**: 1=받을 수 있음(정지·비분류) / 0=분류 중 **또는 이동 중**(둘 다 BUSY).
5. FULL(만재)·PAUSED(일시정지)·OFFLINE(폴 타임아웃/소켓 끊김)은 **WCS가 판단**한다. PLC는 Ready만 제공.
6. API 필드명은 `pId, agvNo, barcode, inductionNo, chuteNo, qty, timeStamp` — `loadQty` 아님(개명됨).
7. 모든 시간값(폴 주기, 500ms 재호출, R_Flag 타임아웃 등)은 appsettings.json — 하드코딩 금지.
   R_Flag 타임아웃은 고정 5초가 아니라 "분류 최대 소요 + 여유"(설정값).
8. 판정 로직은 Wcs.Core에 **순수 함수**로 — I/O·DI 의존 금지. 테스트가 스펙이다.

## 스펙 소스 (docs/ — 항상 이것이 정답)
- `docs/SPEC.md` — 응축 스펙(레지스터 맵, 판정 표, 핸드셰이크, 시뮬레이터 동작). **먼저 읽을 것.**
- `docs/ERD.md` — DB 스키마 17테이블. 대리키·p_id 순환·이력 분리 원칙 포함.
- `docs/wcs_3ds_interface.html` — WCS↔3DS Modbus 정의 + 타이밍 차트 ①②③
- `docs/wcs_rcs_3ds_master_spec.html` — 마스터 정의서(§6 투입 가부 표 = 판정 스펙)
- `docs/wcs_3ds_unified_sequence.html` — 통합 시퀀스(IF-05→08→10→11→12)
- `docs/wcs_rcs_interface_kr.html` — WCS↔RCS API 정의(필드·엔드포인트)

## 솔루션 구조
```
backend/src/Wcs.Core        판정 엔진(순수 C#): RegisterMap, 모델, DepositDecider  ← 의존성 0
backend/src/Wcs.PlcGateway  FluentModbus 마스터: 폴링 스냅샷 캐시 + 쓰기 큐
backend/src/Wcs.Api         ASP.NET Core MVC Controllers: IF-05/09/10 + IF-08 상태 푸시 + 모니터링 API/SignalR + Windows Service 호스트
backend/src/Wcs.Data        EF Core: 오더·예약·pId 이력·트랜잭션 로그(SqlServer 운영 / SQLite 테스트 — provider별 마이그레이션 분리)
backend/src/Wcs.Sim3ds      3DS PLC 시뮬레이터(FluentModbus, TCP 기본·RTU 옵션 --transport rtu) — 통합 테스트 상대역
backend/tests/Wcs.Tests     xUnit — DepositDeciderTests가 스펙 그 자체(Decide 판정 케이스는 처음엔 RED가 정상, ToWire 검증은 GREEN)
```

## 빌드/테스트 명령
```bash
dotnet build backend/Wcs.sln                     # 솔루션 빌드 (M0에서 sln 생성 후)
dotnet test backend/Wcs.sln                      # 전체 테스트
dotnet test backend/Wcs.sln --filter Decider     # 판정 테스트만
dotnet run --project backend/src/Wcs.Sim3ds   # 시뮬레이터 (기본 :1502)
dotnet run --project backend/src/Wcs.Api      # WCS API (기본 :5205)
```
TargetFramework은 `net10.0`. 설치된 SDK가 다르면(`dotnet --list-sdks`) 모든 csproj의
TargetFramework을 설치 버전(LTS 권장)으로 일괄 변경하라.

## 작업 순서
`TASKS.md`의 마일스톤(M0~M5)을 순서대로. 각 마일스톤의 Done 조건을 만족하기 전엔 다음으로 넘어가지 않는다.
구현 중 스펙이 모호하면 추측하지 말고 docs/SPEC.md의 "미확정 사항"에 기록하고 사용자에게 질문할 것.

## 코딩 컨벤션
- C# 12+, nullable enable, file-scoped namespace, record 적극 사용
- 로그: Serilog 도입 완료(콘솔+파일, 레지스터 변화 + API 원문 + 핸드셰이크 단계를 operation_log 테이블 및 파일에 구조화 기록)
- 예외를 삼키지 말 것 — PLC 통신 실패는 OFFLINE 상태 전이로 명시 처리
