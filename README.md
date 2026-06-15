# Rcs3dsInterlockingWcs — 물류 테스트라인 WCS

물류 테스트라인의 **WCS(Warehouse Control System)**. 로봇(AGV)이 인덕션에서 트레이에 상품을
받아 목적지(슈트 또는 3D Sorter)에 **전량 틸트**한다. WCS는 가운데에서 로봇 제어 시스템(RCS)과
3D 분류기 PLC(3DS) 사이를 중재한다.

```
[RCS(AGV)] --HTTP/JSON--> [WCS(이 프로젝트)] --Modbus TCP--> [3D Sorter PLC(VEICHI)]
   클라이언트                 API 서버 + Modbus 마스터              슬레이브
```

## 무엇을 하는가

- **RCS ↔ WCS** (HTTP/JSON, RCS가 클라이언트, 인덕션 단위 실시간)
  - `IF-05` 목적지 조회 — 바코드·인덕션 정보로 목적지(chute)와 수량을 결정, 피스 추적 시작
  - `IF-08` 투입 가부 — 도착한 AGV가 지금 투입해도 되는지 판정(`allowed=true`까지 폴링)
  - `IF-10` 투입 보고 — 틸트 완료 보고(멱등)
- **WCS ↔ 3DS** (Modbus TCP, WCS가 마스터)
  - `IF-11/12` 셀 지정·적재 완료 C/R 핸드셰이크, 층 제어(TgtFloor), 상태 폴링(Ready·CurFloor)
- **WCS가 단일 진실**: 오더·예약·목적지 할당과 만재(FULL)·일시정지(PAUSED)·오프라인(OFFLINE)을 판단한다. PLC는 Ready만 제공한다.

## 설계 핵심

- **판정은 순수 함수**: 투입 가부(IF-08)는 `Wcs.Core`의 `DepositDecider`가 I/O 없이 결정한다 — Offline → Hold(Full/Paused) → Ready/층 비교 우선순위. 테스트가 곧 스펙이다.
- **PLC 쓰기는 단일 큐로 직렬화**: D4(플래그/상태) 한 워드의 비트 수정은 read-modify-write가 필요해, 모든 Modbus 쓰기를 단일 컨슈머 큐로만 내보내 경합을 막는다.
- **층 제어 핑퐁 차단**: `TgtFloor`는 `0`일 때만 쓰고, WCS는 절대 클리어하지 않는다(분류 시작 시 PLC가 클리어).
- **논블로킹 핫패스**: 폴링 스냅샷 캐시로 API는 PLC 쓰기 완료를 기다리지 않고 즉시 응답한다(3초 한계).

## 솔루션 구조

| 프로젝트 | 역할 |
|---|---|
| `src/Wcs.Core` | 판정 엔진(순수 C#): RegisterMap·모델·DepositDecider — 의존성 0 |
| `src/Wcs.PlcGateway` | FluentModbus 마스터: 폴링 스냅샷 캐시 + 단일 쓰기 큐 |
| `src/Wcs.Api` | ASP.NET Core Minimal API: IF-05/08/10 + Windows Service 호스트 |
| `src/Wcs.Data` | EF Core: 오더·예약·pId 이력·트랜잭션 로그 |
| `src/Wcs.Sim3ds` | 3DS PLC 시뮬레이터(FluentModbus TcpServer) — 통합 테스트 상대역 |
| `tests/Wcs.Tests` | xUnit — `DepositDeciderTests`가 판정 스펙 그 자체 |

## 기술 스택

- **.NET 10** / C# 12+ (nullable enable, file-scoped namespace, record)
- **ASP.NET Core** Minimal API (운영 시 Windows Service 호스팅)
- **FluentModbus** — Modbus TCP 마스터 및 시뮬레이터
- **EF Core** — SQL Server Express(개발은 SQLite 분기)
- **xUnit** — 판정 테스트
- **Serilog** — 구조화 로깅(레지스터 변화 + API 원문)

## 빌드 / 실행

```bash
dotnet build                          # 솔루션 빌드 (M0에서 sln 구성 후)
dotnet test                           # 전체 테스트
dotnet test --filter Decider          # 판정 테스트만
dotnet run --project src/Wcs.Sim3ds   # 3DS 시뮬레이터 (기본 :1502)
dotnet run --project src/Wcs.Api      # WCS API (기본 :5080)
```

모든 시간값(폴 주기, 재호출 간격, R_Flag 타임아웃 등)과 접속 정보는 `appsettings.json`에서 설정한다(하드코딩 금지).

## 스펙 문서 (`docs/`)

- `docs/SPEC.md` — 응축 스펙(레지스터 맵·판정 표·핸드셰이크·시뮬레이터 동작). **먼저 읽을 것**
- `docs/ERD.md` — DB 스키마 16테이블(M4 구현 기준)
- `docs/wcs_3ds_interface.html` — WCS↔3DS Modbus 정의 + 타이밍 차트
- `docs/wcs_rcs_3ds_master_spec.html` — 마스터 정의서(투입 가부 표 = 판정 스펙)
- `docs/wcs_3ds_unified_sequence.html` — 통합 시퀀스(IF-05→08→10→11→12)
- `docs/wcs_rcs_interface_kr.html` — WCS↔RCS API 정의(필드·엔드포인트)

> 확정 원본은 `docs/*.html` 4종이며, 충돌 시 HTML이 우선한다. `SPEC.md`는 그 응축본이다.

## 개발 로드맵 (마일스톤)

순서 고정 — 각 마일스톤의 Done 조건을 충족한 뒤 다음으로 넘어간다(`TASKS.md`).

| 단계 | 내용 |
|---|---|
| **M0** | 솔루션 구성 + 빌드 그린(판정 테스트는 RED가 정상 시작점) |
| **M1** | 판정 엔진(`DepositDecider`) 구현 — 판정 테스트 GREEN |
| **M2** | PLC 게이트웨이 + 시뮬레이터 C/R 핸드셰이크 |
| **M3** | API 3종(IF-05/08/10) 구현 |
| **M4** | 시나리오 검증(S1~S9) + 영속화(EF Core 16테이블) |
| **M5** | 운영 준비(Windows Service, Serilog 구조화 로깅) |

## 개발 방식

3-Tier 절차(Planner → Generator ↔ Evaluator → Code Review)로 진행하며, 기능(마일스톤)마다
브랜치를 나눠 작업하고 PR로 검토·병합한다. 상세 규칙은 [`CLAUDE.md`](CLAUDE.md) 참조.
