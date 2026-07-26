# Rcs3dsInterlockingWcs — 물류 테스트라인 WCS

물류 테스트라인의 **WCS(Warehouse Control System)**. 로봇(AGV)이 인덕션에서 트레이에 상품을
받아 목적지(슈트 또는 3D Sorter)에 **전량 틸트**한다. WCS는 가운데에서 로봇 제어 시스템(RCS)과
3D 분류기 PLC(3DS) 사이를 중재한다.

```
[RCS(AGV)] --HTTP/JSON--> [WCS(이 프로젝트)] --Modbus RTU/TCP--> [3D Sorter PLC(VEICHI)]
   클라이언트                 API 서버 + Modbus 마스터                슬레이브
```

## 무엇을 하는가

- **RCS ↔ WCS** (HTTP/JSON — RCS가 WCS를 호출, 상품 단위 실시간)
  - `IF-05` `POST /api/v1/destination-query` — 바코드·인덕션으로 목적지(chuteNo)·수량 판정, 예약 차감, 피스 추적 시작
  - `IF-09` `POST /api/v1/arrival-report` — AGV 도착 보고 → WCS가 3DS를 운영층으로 정렬
  - `IF-10` `POST /api/v1/deposit-report` — 틸트 완료 보고(멱등). 3D 목적지면 이후 IF-11 셀 지정 트리거
- **WCS → RCS** (푸시)
  - `IF-08` **목적지 상태 푸시(UpdateChuteState)** — WCS가 목적지 수용 상태 변경 시 RCS로 푸시(`PUT {RCS}/api/UpdateChuteState`, snake_case `{chute_numbers[], next_states[]}`, `next_state` 3=받을 수 있음 / 2=받을 수 없음).
    RCS는 `3`이면 투입, `2`면 대기. (구 RCS 폴링 `deposit-permission`과 구 단일-`ready` `destination-status` 와이어는 모두 폐지 — UpdateChuteState 단일 채널로 통합.)
- **WCS ↔ 3DS** (Modbus RTU/TCP — WCS가 마스터)
  - `IF-06` 상태 폴링(Ready·CurFloor 등 스냅샷)
  - `IF-11/12` 셀 지정·적재 완료 C/R 핸드셰이크, 층 제어(TgtFloor)
- **모니터링** (읽기 전용) — `GET /api/monitor/*`(오더·피스·소터·셀·operation_log) + SignalR 허브 `/hubs/monitor`로 관제 콘솔에 실시간 제공. `GET /health`는 liveness(항상 200) + DB·소터 온라인 상태 JSON
- **WCS가 단일 진실** — 오더·예약·목적지 할당과 만재(FULL)·일시정지(PAUSED)·오프라인(OFFLINE)을 판단한다. PLC는 Ready만 제공.

## 설계 핵심

- **판정은 순수 함수** — 3D 소터 투입 가부는 `Wcs.Core`의 `DepositDecider`가 I/O 없이 결정한다(Offline → Full/Paused → Ready/층 비교 우선순위). 테스트가 곧 스펙이다.
- **2단계 게이트** — `IF-05`가 *어디로 보낼지*와 셀/관리 상태를 판정(dispatch)하고, `IF-08` 푸시가 *지금 받을 수 있는지*(운영 준비 ready)를 전달한다. 슈트 FULL/PAUSED는 IF-05에서 차단(소터만 NG, 슈트는 OK·보내고 대기).
- **PLC 쓰기는 단일 큐로 직렬화** — D4(플래그/상태) 한 워드의 비트 수정은 read-modify-write가 필요해, 모든 Modbus 쓰기를 단일 컨슈머 큐로만 내보내 경합을 막는다.
- **층 제어 핑퐁 차단** — `TgtFloor`는 `0`일 때만 쓰고, WCS는 절대 클리어하지 않는다(분류 시작 시 PLC가 클리어).
- **논블로킹 핫패스** — 폴링 스냅샷 캐시로 API는 PLC 쓰기 완료를 기다리지 않고 즉시 응답한다(3초 한계).
- **전송 추상화** — `IModbusMaster`로 TCP/RTU를 추상화해 판정·핸드셰이크·쓰기 큐·OFFLINE 처리를 전송 무관하게 재사용한다. 소터마다 독립 번들(포트당 1대).

## 솔루션 구조

| 프로젝트 | 역할 |
|---|---|
| `backend/src/Wcs.Core` | 판정 엔진(순수 C#): RegisterMap·모델·DepositDecider — 의존성 0 |
| `backend/src/Wcs.PlcGateway` | FluentModbus 마스터: 폴링 스냅샷 캐시 + 단일 쓰기 큐 + TCP/RTU 어댑터(`IModbusMaster`) |
| `backend/src/Wcs.Api` | ASP.NET Core MVC: RCS 인터페이스(IF-05/09/10) + IF-08 상태 푸시 + 모니터링 API/SignalR + SPA(wwwroot) 서빙 + Windows Service 호스트 |
| `backend/src/Wcs.Data` | EF Core: 오더·예약·pId 이력·트랜잭션/operation 로그 |
| `backend/src/Wcs.Sim3ds` | 3DS PLC 시뮬레이터(FluentModbus) — TCP 기본·RTU 옵션, 통합 테스트 상대역 |
| `backend/src/Wcs.Migrations.SqlServer` | SQL Server(운영) EF Core 마이그레이션 |
| `backend/src/Wcs.Migrations.Sqlite` | SQLite(개발·테스트) EF Core 마이그레이션 |
| `backend/tests/Wcs.Tests` | xUnit — `DepositDeciderTests`가 판정 스펙 그 자체 |
| `frontend/` | 관제 콘솔 SPA(Vite + React + TypeScript). 빌드 산출물을 `Wcs.Api/wwwroot`로 서빙 |

## 기술 스택

- **.NET 10** / C# 12+ (nullable enable, file-scoped namespace, record)
- **ASP.NET Core MVC**(Controllers) — 운영 시 Windows Service 호스팅, SPA 정적 서빙
- **FluentModbus** — Modbus RTU/TCP 마스터 및 시뮬레이터
- **EF Core** — 운영 **SQL Server** / 개발·테스트 **SQLite**(마이그레이션은 provider별 분리 프로젝트)
- **Serilog** — 콘솔·파일 구조화 로깅 + `operation_log`(레지스터 변화·API 원문·전 동작 기록)
- **xUnit** — 판정·핸드셰이크·통합 테스트
- **프론트엔드** — React 19 · TypeScript · Tailwind · SignalR · TanStack Query/Table

## 빌드 / 실행

`net10.0` SDK 필요(`dotnet --list-sdks`로 확인). 모든 시간값·접속 정보는 `appsettings.json`에서 설정한다(하드코딩 금지).

### 백엔드

```bash
dotnet build backend/Wcs.sln                    # 솔루션 빌드
dotnet test  backend/Wcs.sln                    # 전체 테스트
dotnet test  backend/Wcs.sln --filter Decider   # 판정 테스트만
dotnet run --project backend/src/Wcs.Api        # WCS API — http://0.0.0.0:5080
dotnet run --project backend/src/Wcs.Sim3ds     # 3DS 시뮬레이터 — TCP 127.0.0.1:1502 (기본)
```

시뮬레이터는 RTU로도 기동할 수 있다(현장 리허설 · RS-485). 설정 우선순위는 CLI(`--*`) > 환경변수(`SIM3DS_*`) > `appsettings.Sim3ds.json` > 코드 기본값:

```bash
dotnet run --project backend/src/Wcs.Sim3ds -- --transport rtu --port COM6 \
  --baud 9600 --parity Even --stopbits One --unit 1
```
> 절차·시리얼 페어 준비법은 [`docs/RTU-REHEARSAL.md`](docs/RTU-REHEARSAL.md) 참조.

### 프론트엔드 (`frontend/`)

```bash
npm install
npm run dev       # Vite dev 서버 :5173 (프록시 /api·/hubs → :5080)
npm run build     # 산출물 → backend/src/Wcs.Api/wwwroot (Wcs.Api 단일 서버가 SPA+API 서빙)
```

## 데이터베이스

- **운영 = SQL Server**, **개발·테스트 = SQLite**. provider별로 마이그레이션 프로젝트가 분리돼 있다(`Wcs.Migrations.SqlServer` / `Wcs.Migrations.Sqlite`).
- 스키마 단일 진실은 [`docs/ERD.md`](docs/ERD.md)(17테이블).
- `scripts/seed-field-20cells.sql` — 현장 20셀 소터 시드. `scripts/install-service.ps1` / `uninstall-service.ps1` — Windows Service 등록/해제.

## 스펙 문서 (`docs/`)

- [`docs/SPEC.md`](docs/SPEC.md) — 응축 스펙(레지스터 맵·판정 표·핸드셰이크·시뮬레이터 동작). **먼저 읽을 것**
- [`docs/ERD.md`](docs/ERD.md) — DB 스키마 17테이블(단일 진실)
- [`docs/FRONTEND.md`](docs/FRONTEND.md) — 관제 콘솔 설계
- [`docs/RTU-REHEARSAL.md`](docs/RTU-REHEARSAL.md) — 실 PLC 없이 WCS↔Sim3ds RS-485 사전 검증 절차
- [`docs/wcs_rcs_interface_kr.html`](docs/wcs_rcs_interface_kr.html) — WCS↔RCS API 정의(한글, 필드·엔드포인트)
- [`docs/wcs_rcs_3ds_master_spec.html`](docs/wcs_rcs_3ds_master_spec.html) — 통합 마스터 정의서(IF 목록·플로우·확정 사항)
- [`docs/wcs_3ds_interface.html`](docs/wcs_3ds_interface.html) — WCS↔3DS Modbus 정의 + 타이밍 차트
- [`docs/wcs_3ds_unified_sequence.html`](docs/wcs_3ds_unified_sequence.html) — 통합 시퀀스 다이어그램

> 확정 원본은 `docs/*.html`이며 충돌 시 HTML이 우선한다. `SPEC.md`는 그 응축본(코드 기준)이다.
> (영문 원본 `wcs_rcs_interface.html`, 프론트 디자인 분석 `DESIGN-airbnb.md`도 `docs/`에 있다.)

## 개발 방식

3-Tier 절차(Planner → Generator ↔ Evaluator → Code Review)로 진행하며, 기능(마일스톤·스프린트)마다
브랜치를 나눠 작업하고 PR로 검토·병합한다. 마일스톤 이력은 [`TASKS.md`](TASKS.md), 상세 규칙은
[`CLAUDE.md`](CLAUDE.md) 참조.
