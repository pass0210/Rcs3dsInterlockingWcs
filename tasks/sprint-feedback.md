# Sprint Feedback — S-AUDIT-B-DEPLOY-HARDENING

## APPROVED (2026-08-03, iteration 1 · 2차원 Evaluator pool aggregate)

전 차원 PASS (AND). 상세: tasks/sprint-feedback/functional.md · tasks/sprint-feedback/deploy-safety.md.

### 재triage 반영
③ 운영 README는 이번 세션 DEPLOY-ONPREM.md(12절) 정비로 이미 해소 → SCOPE OUT. 남은 유효 **①(로그 위치)·②(install-service.ps1)** 구현.

### 차원 1 — Functional: **PASS**
- 로그 경로 게이트: `ServiceHostingEnvironment.ResolveWorkingDirectoryOverride`(서비스→baseDir/비서비스→null) 정확·ServiceHostingEnvironmentTests 5 GREEN(비공허)·게이트 블록 Program.cs:41-46 = UseWindowsService 직후·UseSerilog 이전(위치/순서 정확).
- install-service.ps1: 비내장계정 password 게이트(create 전 exit)·depend= MSSQLSERVER·Test-SqlReachable SELECT 1 3분기(null/false→미생성 중단·true→진행·-SkipSqlCheck 우회) 정확·AST 파싱 OK.
- /health 무변경·회귀 0(539=534+5)·#7(리터럴 경로 0)·#8(Wcs.Core diff 0).
- Minor(정보성): PS 화이트리스트가 `NT AUTHORITY\SYSTEM` 별칭 미인식→password 요구 가능(보수적 안전·sc.exe obj=는 LocalSystem이라 결함 아님).

### 차원 2 — Deployment-safety & Regression: **PASS**
- 로그 위치 결정성: (a) 코드흐름 확정 — SetCurrentDirectory(Program.cs:41-46)가 UseWindowsService 뒤·builder.Build()(File sink 생성) 앞 → 상대 "logs/"가 배포폴더 해석·조기 부트스트랩 로거 없음. (b) 순수 게이트 5 GREEN. (c) **동등 실-서비스 재현**(동일 Serilog.Sinks.File 6.0.0+동일 path+byte-identical 게이트 폐기 프로젝트): 서비스=배포폴더/logs 1건·System32 0·비서비스=CWD 불변. System32\logs 유입 종료 실증+논리 확정.
- 문서-코드 정합: DEPLOY-ONPREM.md §9-B step3·§10·§5-3 전부 정합(라인 대조·격차 0).
- 회귀 0: 539 GREEN·빌드 신규 경고 0·TraceLog D:\절대·정적서빙 ContentRoot=BaseDirectory(CWD 무관)·#8 Wcs.Core diff 0.
- 크래시루프 예방: 사전 SQL 점검(:149-169)이 sc.exe create(:171-190) 앞→실패 시 미생성. password 게이트 create 이전.
- SCOPE: README/CLAUDE.md/master_spec/appsettings 무접촉·기본 계정 LocalSystem 유지·in-scope 4파일뿐.

**APPROVED** — Step 4.5 코드리뷰 진행 가능. 커밋 스코프: Program.cs·ServiceHostingEnvironmentTests.cs(신규)·scripts/install-service.ps1·docs/DEPLOY-ONPREM.md + 프로세스 파일.

## Step 4.5 코드리뷰 (2026-08-03) — Critical 0 · MAJOR 1 · Minor 5 → 하드닝 후 APPROVED 유지
모듈1(CWD 게이트) 견고·부작용 0·인젝션 안전·문서 정확. 지적은 PowerShell 자격증명 처리 집중.
### FIX ITER (M1 보안 + m1·m5 하드닝 — 사용자 결정·Deploy-safety 재검증 PASS 유지)
- **M1 (MAJOR·보안)**: `sc.exe create ... password= <평문>` 커맨드라인 leak(감사4688/SIEM 평문 영속) 제거 → 비내장 계정 **New-Service -Credential(PSCredential·SCM API)**·내장/가상은 sc.exe 유지. 생성 경로 `password=` 토큰 0(유일 매치=금지 설명 주석). depend=≡-DependsOn·start=auto≡StartupType·후처리(description/failure/env) 동등.
- **m1**: 내장 화이트리스트에 NT AUTHORITY\SYSTEM·LOCAL SERVICE·NETWORK SERVICE 추가(별칭 오거부 제거).
- **m5**: password 가드 `($null -eq $Password) -or ($Password.Length -eq 0)`(빈 SecureString 거부).
- 재검증: AST 파싱 0·dotnet test **539 GREEN**(독립 재실행·N1 타임아웃 미재현=기지 flake 귀속)·Wcs.Core diff 0·모듈1/depend=/사전점검 무변경·§5-3 문서 정합.
### 잔여 Minor (등재 — 다음 스프린트/후속)
- **m2**: install-service.ps1 SecureString 평문이 GC heap에 잔류(zero-out 불가·Marshal BSTR 결정적 wipe 검토·admin 설치라 수용).
- **m3**: 사전 SQL 점검이 앱DB(-SqlDatabase) 대상 → 신선 호스트(DB 미존재·dbcreator 계정)에서 EF MigrateAsync가 만들 DB를 false abort. master 프로브 또는 "대상 DB 선존재 필요" 문서화 검토.
- **m4**: 사전점검 TrustServerCertificate=$true(-C) — localhost 기본은 무해, 원격 -SqlServer+SQL인증 시 미검증 채널 자격증명 프로브(MITM). 원격 시 한 줄 caveat.
- (정보성) PS 화이트리스트 별칭은 m1로 해소. FRONTEND.md:60 System32 언급은 CWD 문제 배경 설명(정확·묶음 E 문서 정리 대상).
