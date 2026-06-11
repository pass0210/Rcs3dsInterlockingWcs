# 작업 마일스톤 (순서 고정 — Done 충족 후 다음으로)

## M0. 솔루션 구성 + 빌드 그린
```bash
dotnet new sln -n Wcs
dotnet sln add src/Wcs.Core src/Wcs.PlcGateway src/Wcs.Api src/Wcs.Data src/Wcs.Sim3ds tests/Wcs.Tests
dotnet add src/Wcs.PlcGateway package FluentModbus
dotnet add src/Wcs.Sim3ds   package FluentModbus
dotnet add src/Wcs.Api      reference src/Wcs.Core src/Wcs.PlcGateway src/Wcs.Data
dotnet add src/Wcs.PlcGateway reference src/Wcs.Core
dotnet add src/Wcs.Data     reference src/Wcs.Core
dotnet add tests/Wcs.Tests  reference src/Wcs.Core
dotnet add tests/Wcs.Tests  package xunit && dotnet add tests/Wcs.Tests package xunit.runner.visualstudio && dotnet add tests/Wcs.Tests package Microsoft.NET.Test.Sdk
dotnet build
```
SDK 버전이 net10.0과 다르면 csproj TargetFramework 일괄 수정.
**Done**: `dotnet build` 성공, `dotnet test` 실행되고 DepositDeciderTests가 **RED**(NotImplemented) — 이게 정상 시작점.

## M1. 판정 엔진 (Wcs.Core)
- `DepositDecider.Decide` 구현 — docs/SPEC.md §2 표 7행이 그대로 tests/DepositDeciderTests.cs에 있음
- **Done**: `dotnet test --filter Decider` 전부 GREEN. 표에 없는 동작 추가 금지.

## M2. PLC 게이트웨이 + 시뮬레이터 핸드셰이크
- Sim3ds: SPEC §6 동작 구현(FluentModbus.ModbusTcpServer, :1502)
- PlcGateway: 폴링 BackgroundService(스냅샷 캐시) + Channel 기반 단일 쓰기 큐 + RMW 헬퍼(SetBit/ClearBit)
- C/R 핸드셰이크 오케스트레이터: SPEC §4 (C 쓰기→R 대기→대사→클리어)
- **Done**: 통합 테스트 — 시뮬레이터 상대로 셀 지정→적재 완료 왕복 1건 성공, R_Seq==C_Seq 검증, 로그에 레지스터 타임라인

## M3. API 3종 (Wcs.Api)
- IF-05/08/10 구현(스텁 교체). IF-08 = 스냅샷 캐시 읽기 → Decide → (쓰기 결정 시) 쓰기 큐 투입 → 즉시 응답(쓰기 완료 대기 안 함)
- IF-05 예약 차감(메모리 우선, M4에서 Data 연결), IF-10 멱등
- **Done**: Sim3ds + API 띄우고 curl 시나리오: IF-05 OK → IF-08(WRONG_FLOOR→이동→allowed) → IF-10 OK

## M4. 시나리오 검증 + 영속화
- Wcs.Data: 오더/예약/pId 이력/트랜잭션 로그 엔티티 + EF Core(SQL Server Express, 개발은 SQLite 허용)
- 시나리오 S1~S9 자동화(xUnit 통합 테스트, Sim3ds 고장 주입 사용):
  S1 정상 / S2 층 다름(차트②) / S3 분류 중 선기입·복귀·분류시작 클리어(차트③) / S4 핑퐁 차단(쓰기 이력 검증)
  / S5 R_Seq 불일치 알람 / S6 R_Flag 타임아웃 / S7 OFFLINE / S8 FULL·PAUSED / S9 다중 AGV 경합
- **Done**: 9개 시나리오 GREEN, 시뮬레이터 쓰기 이력으로 "TgtFloor≠0일 때 WCS 쓰기 0건" 입증

## M5. 운영 준비
- Windows Service 호스팅(UseWindowsService), Serilog 구조화 로깅(레지스터 변화+API 원문, rolling)
- WCS 재시작 시 레지스터 재독 동기화, OFFLINE 중 IF-05/08 응답 정책 적용
- **Done**: 서비스 등록 스크립트, 콜드스타트→정상 시나리오 통과, 운영 README
