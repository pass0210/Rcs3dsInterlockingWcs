[Sprint Contract]
Sprint ID: S-AUDIT-B-DEPLOY-HARDENING
Title: 2026-07-01 전체 감사 묶음 B — 운영(Windows Service) 배포 전 차단 (재triage 후)
Base: 최신 develop = 99d8038. feature 브랜치 feat/audit-b-deploy-hardening.

════════════════════════════════════════════════════════════════════════════
## ★ RE-TRIAGE 결과 (현재 코드·문서 직접 판정)
════════════════════════════════════════════════════════════════════════════
- ① Serilog 상대경로: **유효(미해소)** — IN SCOPE(주 작업). appsettings.json:24 `logs/wcs-.log`·
  appsettings.Development.json:28 `logs/wcs-dev-.log`(둘 다 상대)·Program.cs SetCurrentDirectory 0건.
  sc.exe 서비스 로그가 System32\logs로 실측(DEPLOY-ONPREM.md §9-B/§10) — 조용한 유실은 아니나
  위치 예측 불가·CWD 의존·제한계정 취약. (rollOnFileSizeLimit 1GB 유실은 묶음 A에서 이미 해소.)
- ② install-service.ps1: **4 하위이슈 유효**(부 작업) — :32 LocalSystem 기본, :55 password=/depend= 부재,
  :61 restart/5000 루프, 사전 SQL 점검 부재. 단 현장 표준=수동 sc.exe(§9-B)·NSSM 권장(§5-3)이라 '대안' 경로.
- ③ 운영 README: **해소 — SCOPE OUT**. DEPLOY-ONPREM.md 12절 정비(§2-3 SQL 계정/권한·§5 설정/서비스·
  §9/§9-B 재배포·§10 트러블슈팅·§11 WDAC)로 커버. 루트 README.md IF-08=푸시 de-stale 완료. 잔여=①의 로그
  위치 변경을 DEPLOY-ONPREM.md에 반영하는 것뿐.

════════════════════════════════════════════════════════════════════════════
## Goal
운영 Windows Service 배포 시 (a) 앱 로그가 예측가능·기록가능 위치에 결정적 생성되도록 Serilog 파일 sink
작업 디렉터리 기준 고정(현재 sc.exe 서비스는 System32\logs로 흘러 예측 불가·제한계정 취약), (b) 프로젝트
내장 서비스 등록 스크립트를 SQL 권한 실패·password·SQL 기동 순서·사전 점검 측면 견고화. 두 변경 모두
DEPLOY-ONPREM.md와 정합하도록 문서 갱신. ③ 운영 README는 SCOPE OUT(이미 커버). 절대규칙 #7(경로/설정
하드코딩 금지 — AppContext 파생/appsettings 오버라이드만)·#8(Wcs.Core 무변경).

## Implementation Scope
[모듈 1 — 로그 위치 결정성 (①)]
1. Windows Service(비대화형) 컨텍스트에서 Serilog 파일 sink 상대경로 `logs/`가 CWD(System32)가 아니라
   배포 폴더(exe 인접=AppContext.BaseDirectory 기준)로 해석되도록 앱 시작 지점에서 작업 디렉터리 기준 고정.
   위치 Program.cs(Serilog 초기화 이전). #7: AppContext.BaseDirectory 파생만(리터럴 경로 하드코딩 금지).
   appsettings의 `Serilog:WriteTo:File:path`는 상대(`logs/wcs-.log`) 유지 기본안(최종 위치=Q1·적용범위=Q2).
2. 회귀 비발생: 정적 서빙(WebRootPath=ContentRoot/wwwroot·CWD 무관)·전용 추적 로그(TraceLog=절대경로
   D:\Rcs3dsInterlockingWcsLogs)·operation_log/plc_event(DB·CWD 무관) 무영향 확인.
3. DEPLOY-ONPREM.md §9-B step3·§10 로그 위치 서술을 새 동작과 정합화(System32\logs→확정 위치).
[모듈 2 — install-service.ps1 견고화 (②)]
4. 계정≠LocalSystem 시 SecureString password 파라미터 + `obj= <계정> password= <값>` 전달.
5. 로컬 SQL 선기동 보장: `depend= <SQL 서비스명>`(파라미터화·기본값=Q4).
6. 설치 전 사전 SQL 도달성 점검(SELECT 1) 실패 시 서비스 미생성·중단·안내(5초 무한 크래시루프 예방).
7. 스크립트 헤더/주석 정정(:14-15 자동 Migrate 단언 오도 교정·:18 운영 README→DEPLOY-ONPREM.md 참조).
8. DEPLOY-ONPREM.md §5-3 대안 블록에 password/depend=/사전점검 반영.
[SCOPE OUT] ③ 운영 README(해소)·루트 README/CLAUDE.md/master_spec 일괄 정정(묶음 E)·base appsettings
Trusted_Connection 자체 변경(운영은 Production.json SQL 인증 오버라이드)·묶음 A 항목(해소)·기본 계정 하향(Q5·기본 미포함).

## Parallel Modules: N/A (두 모듈 다 DEPLOY-ONPREM.md 공유 편집 → 단일 모듈).
## Evaluation Dimensions:
1) Functional — 로그가 의도 위치에 실제 생성·/health 정상·스크립트 문법/로직 올바름·사전점검 동작.
2) Deployment-safety & regression — 로그 위치 변경이 DEPLOY-ONPREM.md와 정합·dev dotnet run/정적서빙/
   TraceLog/DB 기록 회귀 0·#7(경로 하드코딩 0)·#8(Wcs.Core 무변경) 준수.
(APPROVED=두 차원 AND. 동시성 차원 무관·미선언.)

## Detected Project Type: Backend/API
(backend/src/Wcs.Api ASP.NET Core + Windows Service 호스트·Program.cs 부트스트랩. 프론트 변경 0. 변경 표면=
 startup/logging 인프라 + 배포 스크립트 + 배포 문서.)

## Verification Scenarios (Backend/API)
- Endpoints touched: 없음(HTTP 계약 무변경). liveness는 기존 GET /health(무변경) 사용.
- Happy path: (기동/로그=실제 해피패스) Windows Service 컨텍스트 기동 시 wcs-*.log가 의도 위치(배포 폴더 하위
  logs·Q1)에 생성·System32\logs 신규 유입 0. GET /health→200 {status,db,sorters} 불변. install-service.ps1
  유효 인자 실행→서비스 생성+depend=/failure/env 의도대로(생성 후 정리 가능 검증).
- Error cases: 제한 계정 기동 시 파일 로그 의도 위치 기록 또는 쓰기실패 진단가능(무음 삼킴 아님) / DB 도달불가
  MigrateAsync throw fatal 진단이 의도 위치에서 확인 / 사전 SQL 점검 실패→서비스 미생성·중단 / 계정≠LocalSystem인데
  password 미제공→사전 요구·검증(sc start 1069 예방).
- Operational verification(필수·인프라 스프린트 실제 표면): 로그 경로 변경은 **실 서비스 컨텍스트 기동으로 실증**
  (dotnet run은 CWD=프로젝트라 갭 재현 못 함 — 실 sc.exe/Windows Service 또는 IsWindowsService 경로 강제 동등 재현).
  install-service.ps1은 정적 검토+PowerShell AST 파싱 통과+로직 워크스루(가능 시 폐기용 서비스명 dry-run→즉시 정리·
  불가 시 정적 대체 명시). DEPLOY-ONPREM.md §9-B step3/§10 로그 경로가 코드 새 동작과 일치(문서-코드 격차 0).
  무회귀: dotnet build 신규 경고 0·dotnet test 기존 GREEN·TraceLog/정적서빙/DB 기록 무영향.

## Evaluation Criteria (weights)
- (35%) 로그 위치 결정성: 서비스 컨텍스트 wcs-*.log 확정 위치 실 생성 실증·System32\logs 유입 0·#7 준수(리터럴 0).
- (25%) 스크립트 견고화: password(SecureString)·depend=(파라미터)·사전 SQL 점검 동작·크래시루프 서비스 미생성·파싱 통과.
- (20%) 문서 정합: DEPLOY-ONPREM.md 로그 위치(§9-B/§10)·§5-3 스크립트 절 코드와 정합·격차 0·③ SCOPE OUT 증거.
- (15%) 무회귀: 빌드 경고 0(신규)·기존 테스트 GREEN·dev dotnet run 로그·정적서빙·TraceLog·DB 무영향·#8 Wcs.Core diff 0.
- (5%) 범위 준수: 묶음 E/C·기본 계정 하향 등 스코프 밖 변경 0.

## Completion Conditions (AND)
1. 서비스 컨텍스트 실 기동(또는 IsWindowsService 동등 재현)에서 wcs-*.log 의도 위치 생성 실증·System32\logs 신규 유입 0.
2. #7 위반 0(경로 리터럴 하드코딩 없음)·#8 위반 0(Wcs.Core diff 0).
3. install-service.ps1 password/depend=/사전점검 포함·PowerShell 파싱 통과+정적 로직 검토 통과(dry-run 환경 허용 시).
4. DEPLOY-ONPREM.md 로그 위치·스크립트 서술이 새 코드와 정합(대조).
5. dotnet build 신규 경고 0·dotnet test 기존 GREEN.
6. dev dotnet run 로그·정적서빙·TraceLog·DB 기록 무회귀.

## Open Questions (★ 사용자 게이트 확정 2026-08-03)
Q1. ✅ **로그 위치 = exe 인접 `<배포폴더>\logs`**(AppContext.BaseDirectory 기준·#7·NSSM AppDirectory 동작 동일).
    기존 System32\logs → 이 위치로 변경 → DEPLOY-ONPREM.md §9-B step3·§10 로그 확인 경로를 `C:\BOWOO\Wcs.Api\logs`류로 갱신 필수.
Q2. ✅ **IsWindowsService 게이트**(서비스 컨텍스트에만 작업디렉터리 고정). dev `dotnet run` 로그는 프로젝트/logs 유지(회귀 0).
Q3. ✅ **install-service.ps1 견고화 포함**(저비용). 단 DEPLOY-ONPREM.md에 현장 표준=수동 sc.exe/NSSM, 이 스크립트=대안임을 명시.
Q4. ✅ **depend= MSSQLSERVER**(로컬 기본 인스턴스). 파라미터화하되 기본값=MSSQLSERVER.
Q5. ✅ **기본 계정 LocalSystem 유지**(인프라만 추가·기본값 하향 미포함 — 감사 C-17은 후속).

> Planner self-check — Detected project type: Backend/API. Required scenario slots: 3 (endpoints-touched·happy-path-per-endpoint·error-cases-per-endpoint) + 1 보강(operational-verification). All slots filled: yes.
