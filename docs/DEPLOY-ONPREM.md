# 온프레미스 배포 가이드 (WCS — 3DS Interlocking)

> 사내 PC/서버(Windows)에 WCS를 **Windows Service**로 배포하는 처음부터 끝까지의 절차.
> 클라우드(Azure) 배포는 `docs/DEPLOY-AZURE-RCS-TEST.md` 참조. 이 문서는 **온프레미스 전용**.

---

## 0. 배포 아키텍처 — 한눈에

**배포물은 `Wcs.Api` 실행 파일 하나뿐**입니다. 이 단일 프로세스가 다음을 모두 담당합니다.

```
[RCS(AGV)] --HTTP/JSON--> ┌─────────────────────────────────────┐ --Modbus RTU/TCP--> [3DS Sorter PLC]
                          │  Wcs.Api.exe (Windows Service)       │
   브라우저 --HTTP------> │   · REST API (IF-05/09/10 + 모니터링) │ --SQL--> [SQL Server]
                          │   · SPA(프론트) 정적 서빙 (wwwroot)   │
                          │   · 3DS Modbus 마스터(폴링/쓰기큐)     │
                          │   · IF-08 슈트상태 아웃바운드 푸시     │
                          └─────────────────────────────────────┘
```

- **프론트(React)** 는 별도 웹서버가 필요 없습니다. `vite build` 산출물이 API의 `wwwroot`로 들어가고, API가 같은 포트(**:5205**)에서 SPA + API를 함께 서빙합니다.
- **DB** 는 SQL Server(운영 provider). 기동 시 스키마 자동 적용(`MigrateOnStartup=true`).
- **호스팅** 은 `UseWindowsService()` — `scripts/install-service.ps1`로 서비스 등록.
- 대상 프레임워크: **.NET 10** (`net10.0`).

---

## 1. 사전 요구사항

### 1-1. 대상 서버(온프레미스 PC)
| 항목 | 요구 |
|---|---|
| OS | Windows 10/11 또는 Windows Server (x64) |
| .NET 런타임 | **ASP.NET Core Runtime 10.x (x64)** — 프레임워크 의존 게시 시 필수. *self-contained 게시*면 불요(§2 참조) |
| SQL Server | SQL Server 2019+ / Express 이상 (로컬 또는 사내 DB 서버 접근 가능) |
| PLC 연결 | RS-485(RTU): USB-RS485 어댑터 + COM 포트 / 또는 TCP: PLC IP 네트워크 도달 |
| 권한 | 서비스 등록·방화벽 설정을 위한 **관리자 권한** |

### 1-2. 빌드 머신(대상 서버에서 빌드해도 됨)
빌드는 대상 서버가 아닌 개발 PC에서 해도 됩니다(산출물만 서버로 복사). 빌드에만 필요:
- **.NET SDK 10.x** (`dotnet --version` = 10.0.x)
- **Node.js 18+** (프론트 빌드 — 런타임에는 불요)

> ℹ️ 서버에 SDK·Node를 깔기 싫으면 **개발 PC에서 게시 → 산출물 폴더만 서버로 복사**하는 것을 권장합니다.

---

## 2. 빌드 & 게시 (산출물 만들기)

> ⚠️ **소스 폴더(이 repo)를 그대로 서버에 넣는 게 아닙니다.** 아래 게시(publish) 산출물 폴더를 만들어 그것을 넣습니다.

배포할 브랜치를 먼저 확정하고 체크아웃합니다(예: `main` 또는 `develop`).

```bash
git checkout main            # 운영 릴리스 브랜치(팀 정책에 맞게)
git pull
```

### 2-1. 프론트 빌드 → wwwroot (반드시 게시보다 먼저)
```bash
cd frontend
npm ci
npm run build                # 산출물이 ../backend/src/Wcs.Api/wwwroot 로 들어감
cd ..
```

### 2-2. 백엔드 게시
프레임워크 의존(서버에 .NET 런타임 있음 — 산출물 작음):
```bash
dotnet publish backend/src/Wcs.Api/Wcs.Api.csproj -c Release -r win-x64 --self-contained false -o C:\BOWOO\Wcs.Api
```

또는 self-contained(서버에 런타임 불요 — 폴더 복사만으로 실행, 용량 큼):
```bash
dotnet publish backend/src/Wcs.Api/Wcs.Api.csproj -c Release -r win-x64 --self-contained true -o C:\BOWOO\Wcs.Api
```

### 2-3. 산출물 확인
`C:\BOWOO\Wcs.Api` 폴더에 다음이 있어야 합니다.
```
Wcs.Api.exe          ← 실행 파일(= 서비스 진입점)
wwwroot\             ← 프론트(SPA) — index.html 등
appsettings.json     ← 기본 설정(운영값은 §4에서 오버라이드)
*.dll                ← 의존 어셈블리
```
**이 폴더 전체가 "서버에 넣는 것"** 입니다.

---

## 3. 서버로 복사

`C:\BOWOO\Wcs.Api` 폴더를 온프레미스 서버의 배포 경로(예: `C:\BOWOO\Wcs.Api`)로 복사합니다. 경로는 자유지만 이후 단계의 경로와 일치시키세요.

---

## 4. 운영 설정 (`appsettings.Production.json`) — 가장 중요

서비스는 `ASPNETCORE_ENVIRONMENT=Production`으로 뜨며(§7), 이때 **`appsettings.json` 위에 `appsettings.Production.json`이 병합**됩니다. 배포 폴더에 **`appsettings.Production.json`을 새로 만들어** 아래 운영값만 오버라이드하세요(원본 `appsettings.json`은 건드리지 않아도 됨).

`C:\BOWOO\Wcs.Api\appsettings.Production.json` 예시(플레이스홀더 — 실값으로 교체):

```jsonc
{
  // ── ① DB 연결 (SQL Server) ────────────────────────────────
  "ConnectionStrings": {
    // (A) Windows 인증: 서비스 계정이 SQL 로그인을 가져야 함(§7-주의)
    "WcsDb": "Server=localhost;Database=Rcs3dsInterlockingWcs;Trusted_Connection=True;TrustServerCertificate=True"
    // (B) SQL 인증을 쓸 경우(권장·계정관리 단순):
    // "WcsDb": "Server=DBHOST\\SQLEXPRESS;Database=Rcs3dsInterlockingWcs;User Id=wcs_svc;Password=***;TrustServerCertificate=True"
  },

  "Database": {
    "Provider": "SqlServer",
    "MigrateOnStartup": true,     // 기동 시 스키마 자동 적용. 수동 적용하려면 false(§5-수동)
    "SeedOnStartup": false        // ★ 운영은 반드시 false — 테스트 시드 자동 삽입 금지
  },

  // ── ② 바인딩 주소/포트 ────────────────────────────────────
  "Urls": "http://0.0.0.0:5205",  // 전 인터페이스 :5205. 특정 IP만 열려면 http://192.168.x.x:5205

  // ── ③ 3DS PLC (Modbus) — 실 하드웨어 연결 ─────────────────
  "Sorters": [
    {
      "ChuteNo": 1,               // 3D Sorter 슈트번호(DB destination과 일치해야 함 — §6)
      // RS-485(현장) — Transport=Rtu:
      "Transport": "Rtu",
      "PortName": "COM3",         // ★ 실 COM 포트
      "BaudRate": 9600,           // ★ VEICHI PLC 실측값
      "Parity": "Even",           // ★
      "StopBits": "One",          // ★
      "UnitId": 1,                // ★ 슬레이브 ID
      "ReadTimeoutMs": 1000,
      "WriteTimeoutMs": 1000,
      "PollIntervalMs": 150,
      "OfflineAfterFailures": 3
      // 또는 네트워크 PLC면: "Transport": "Tcp", "Host": "192.168.0.50", "Port": 502
    }
  ],

  // ── ④ 인덕션 → 층 매핑 (양층 제어) ────────────────────────
  "Wcs": {
    "InductionFloorMap": { "1": 1, "2": 2 },   // ★ 실 인덕션 번호 → 층(1/2). 미매핑 인덕션은 NG(fail-loud)

    // ── ⑤ RCS로 슈트 수용상태 푸시(IF-08) ──────────────────
    "ChuteStatePush": {
      // RCS 수신 호스트가 준비되면 층별로 채움. 비우면 DORMANT(푸시 안 나감·정상 기동).
      "FloorHosts": {
        // "1": "http://192.168.0.151:3000",
        // "2": "http://192.168.0.152:3000"
      },
      "Path": "/api/UpdateChuteState"   // 기본 경로(RCS 정의에 맞게)
    }
  },

  // ── ⑥ 타이밍(현장 실측 후 조정) ───────────────────────────
  "Timing": {
    "RFlagTimeoutMs": 30000,           // = 분류 최대 소요 + 여유(현장 실측)
    "ReturnReadyTimeoutMs": 30000      // = 복귀 이동 최대 소요 + 여유
  }
}
```

**주의**: JSON에는 실제 주석(`//`)을 넣지 마세요(표준 JSON은 주석 불가). 위 예시의 `//`는 설명용이니 제거하고 값만 남기세요.

---

## 5. 데이터베이스 준비

### 5-1. 빈 DB 생성
SQL Server에 빈 데이터베이스를 만듭니다(또는 DBA에게 요청).
```sql
CREATE DATABASE Rcs3dsInterlockingWcs;
```
`MigrateOnStartup=true`면 **서비스 첫 기동 시 스키마가 자동 생성**됩니다(빈 DB 약 10초). 서비스 계정에 해당 DB `db_owner` 부여.

### 5-2. (선택) 마이그레이션 수동 적용 — `MigrateOnStartup=false`로 둘 때
빌드 머신에서 SQL 스크립트를 뽑아 서버 DBA가 실행:
```bash
# 멱등 SQL 스크립트 생성(오프라인)
dotnet ef migrations script --idempotent \
  --project backend/src/Wcs.Migrations.SqlServer \
  --startup-project backend/src/Wcs.Api \
  --context WcsDbContext -o migrate.sql
# 서버에서 적용(명령 타임아웃 없음)
sqlcmd -S <서버> -d Rcs3dsInterlockingWcs -E -C -i migrate.sql
```
> ℹ️ 앱의 기본 CommandTimeout은 30초라, 이미 스키마가 있는 비정상 DB에서 전체 재적용을 시도하면 타임아웃날 수 있습니다. **빈 DB에서 시작**하거나 위 sqlcmd 경로를 쓰세요(§12 트러블슈팅 참조).

---

## 6. 마스터데이터 등록 (필수 — 운영 시드는 off)

운영은 `SeedOnStartup=false`라 **빈 DB에는 소터/셀/슈트가 없습니다**(기동 직후 `/health`의 `sorters`가 `[]`). 실동작하려면 마스터데이터를 넣어야 합니다. 두 경로 중 하나:

**(A) 설비 관리 페이지(UI)** — 브라우저에서 `B2C → 설비 관리`로 소터(ChuteNo가 §4의 `Sorters` 설정과 일치)·셀·슈트를 등록.

**(B) 현장 시드 SQL** — 실 3DS 하드웨어 테스트용 20셀(가용 15) 시드가 준비돼 있습니다:
```bash
sqlcmd -S localhost -d Rcs3dsInterlockingWcs -E -C -f 65001 -i scripts/seed-field-20cells.sql
```
- 소터 `ChuteNo=1` destination + 물리 20셀(1~15 가용, 16~20 비활성) + 배치/오더/셀배정을 **멱등**하게 만듭니다.
- 현장 매핑이 16~20까지 확장되면 스크립트 상단 주석의 `@availMax`를 20으로 올려 재실행(코드/스키마 변경 없음).
- ⚠ 이 시드의 슈트번호(ChuteNo=1)·셀 수는 현장 구성에 맞게 조정하세요.

---

## 7. Windows Service 등록

관리자 PowerShell에서(배포 폴더 기준 경로 지정):
```powershell
cd C:\BOWOO\Wcs.Api          # 또는 repo의 scripts\ 위치
.\scripts\install-service.ps1 `
    -BinPath   C:\BOWOO\Wcs.Api\Wcs.Api.exe `
    -Environment Production
sc.exe start WcsApi
```
스크립트가 하는 일:
- 서비스 `WcsApi` 생성(자동 시작 `start=auto`)
- 실패 복구: 1·2·이후 재시작(5초 후), 카운터 1일 리셋
- `ASPNETCORE_ENVIRONMENT=Production` 주입(→ dev 시드 off · `appsettings.Production.json` 로드)

상태/제어:
```powershell
sc.exe query  WcsApi         # 상태
sc.exe stop   WcsApi
sc.exe start  WcsApi
```

### ⚠️ 서비스 계정과 SQL 접근 (가장 흔한 배포 실패 원인)
`install-service.ps1` 기본 계정은 **LocalSystem**입니다. LocalSystem은 SQL Server에 **머신 계정(`DOMAIN\서버명$`)** 으로 접속합니다. `Trusted_Connection`(Windows 인증)을 쓴다면 그 로그인을 SQL에 만들어야 합니다:
```sql
CREATE LOGIN [DOMAIN\SERVERNAME$] FROM WINDOWS;   -- 워크그룹이면 다른 방식
USE Rcs3dsInterlockingWcs;
CREATE USER [DOMAIN\SERVERNAME$] FOR LOGIN [DOMAIN\SERVERNAME$];
ALTER ROLE db_owner ADD MEMBER [DOMAIN\SERVERNAME$];
```
대안(더 단순·권장):
- **도메인 서비스 계정**으로 등록: `install-service.ps1 -Account "DOMAIN\svc-wcs"` (그 계정에 SQL 권한 부여)
- **SQL 인증**으로 전환: `appsettings.Production.json`의 `WcsDb`를 `User Id=…;Password=…` 형식으로(§4-①-B)

---

## 8. 방화벽 / 네트워크

인바운드 규칙 추가(관리자 PowerShell):
```powershell
New-NetFirewallRule -DisplayName "WCS API 5205" -Direction Inbound -Protocol TCP -LocalPort 5205 -Action Allow
```
- **RCS → WCS** (IF-05 투입판정 · IF-09 도착 · IF-10 핸드셰이크)와 **LAN 브라우저 → 모니터링 UI** 가 이 포트로 들어옵니다.
- **WCS → RCS**(IF-08 푸시)는 아웃바운드이며 `ChuteStatePush:FloorHosts`의 호스트로 나갑니다(RCS 측 수신 포트 개방 필요).
- PLC가 TCP면 WCS → PLC 아웃바운드(기본 502) 경로 확인.

---

## 9. 기동 검증

1. **서비스 실행 중**: `sc.exe query WcsApi` → `STATE : RUNNING`
2. **로그**: `C:\BOWOO\Wcs.Api\logs\wcs-YYYYMMDD.log` (Serilog 일 롤링, 14일 보존)
   - `Now listening on: http://0.0.0.0:5205`
   - `[DbInitializer] Migrate 완료 — 스키마 보장됨`
   - 소터 online (Modbus 연결 성공) / OFFLINE이면 PLC 연결 점검
   - `[IF-08푸시] … DORMANT` = RCS 호스트 미설정(정상 — 인바운드는 동작)
3. **헬스 체크**: 브라우저/curl
   ```
   http://<서버IP>:5205/health   →  {"status":"ok","db":true,"sorters":[...]}
   ```
4. **모니터링 UI**: `http://<서버IP>:5205` 접속 → 소터/셀 타일 표시(마스터데이터 등록 후)
5. **RCS 연동**: RCS에서 IF-05 호출이 200으로 처리되는지, 로그에 핸드셰이크 단계가 찍히는지 확인

---

## 10. 업데이트 / 재배포

```powershell
sc.exe stop WcsApi
# (개발 PC에서 새로 빌드+게시한 산출물로) C:\BOWOO\Wcs.Api 폴더 덮어쓰기
#   ※ appsettings.Production.json 은 보존(덮어쓰지 않도록 주의)
sc.exe start WcsApi
```
- `MigrateOnStartup=true`면 신규 마이그레이션이 기동 시 자동 적용됩니다.
- 서비스 재등록은 불요(바이너리 교체만). 서비스 정의를 바꿔야 할 때만 `uninstall-service.ps1` → `install-service.ps1`.

---

## 11. 제거 / 롤백

```powershell
# 서비스 제거
.\scripts\uninstall-service.ps1        # 또는: sc.exe stop WcsApi; sc.exe delete WcsApi
```
- 롤백: 이전 게시 산출물 폴더로 되돌린 뒤 `sc.exe start WcsApi`.
- 스키마는 하위호환 마이그레이션이라 바이너리 다운그레이드 시에도 대개 문제 없으나, 마이그레이션을 되돌려야 하면 DBA와 협의(백업 우선).

---

## 12. 트러블슈팅

| 증상 | 원인 / 조치 |
|---|---|
| 서비스가 뜨자마자 멈춤(STATE STOPPED) | 로그(`logs/wcs-*.log`) 확인. 대개 **DB 연결 실패**(§7 서비스계정 SQL 권한) 또는 **appsettings 오설정**. |
| `SqlException: Cannot open database` / 로그인 실패 | 서비스 계정 SQL 로그인 미생성(§7-⚠). SQL 인증으로 전환하거나 머신/도메인 계정 로그인 부여. |
| 마이그레이션 타임아웃(Error -2, 30s) | DB가 **스키마는 있는데 `__EFMigrationsHistory`가 없는 비정상 상태**. 빈 DB로 재생성하거나 §5-2 sqlcmd로 적용. (로컬 개발 DB에서 관측된 케이스.) |
| `/health`의 `sorters`가 `[]` | 마스터데이터 미등록(§6) — 설비 관리 UI 또는 시드 SQL로 소터/셀 등록. |
| 소터 OFFLINE | PLC 연결 문제 — COM 포트/보드레이트/UnitId(§4-③) 또는 TCP Host/Port·케이블·전원 확인. |
| 포트 5205 바인딩 실패(address in use) | 이전 인스턴스/다른 앱이 점유. `Get-NetTCPConnection -LocalPort 5205` 로 확인 후 종료. |
| IF-08 푸시가 안 나감 | 정상일 수 있음 — `FloorHosts` 비었으면 DORMANT. RCS 호스트 설정 시 활성화(§4-⑤). |
| 빌드 시 파일 잠금/`Fatal error` | 고아 MSBuild 노드 — `MSBUILDDISABLENODEREUSE=1` 설정 + 잔여 dotnet 프로세스 종료 후 재빌드. |

---

## 13. 배포 체크리스트

- [ ] 배포 브랜치 확정·체크아웃
- [ ] `npm ci && npm run build` (프론트 → wwwroot)
- [ ] `dotnet publish … -o C:\BOWOO\Wcs.Api` (self-contained 여부 결정)
- [ ] 산출물 서버로 복사
- [ ] `appsettings.Production.json` 작성(DB 연결·PLC·InductionFloorMap·(선택)RCS 호스트·SeedOnStartup=false)
- [ ] SQL Server 빈 DB 생성 + 서비스 계정 권한
- [ ] 마스터데이터 등록(설비 관리 UI 또는 seed SQL)
- [ ] `install-service.ps1` 실행 + `sc.exe start WcsApi`
- [ ] 방화벽 5205 인바운드 개방
- [ ] `/health` 200 · 모니터링 UI · 소터 online · RCS IF-05 연동 확인
- [ ] 로그 롤링/보존 정상 확인

---

*최종 갱신: 2026-07 · 대상 브랜치 develop 기준. 설정 키의 상세 주석은 `backend/src/Wcs.Api/appsettings.json` 참조.*
