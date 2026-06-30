# 재개 노트 — S-SQLSERVER-FK-CASCADE (2026-06-30 세션 재시작)

> **재시작 사유**: in-process 좀비 에이전트 누적(이전 스프린트들의 evaluator·gen-m5p1·eval-m5p1) + TeamDelete 도구 언로드 → 사용자가 세션 초기화 승인. 재시작으로 좀비 제거 → FK 스프린트를 **정식 팀(TeamCreate)으로 깨끗이** 진행. **모든 작업물 디스크 보존.**

## 현재 상태 (어디까지 됐나)
- **브랜치 `fix/sqlserver-fk-cascade`** (develop 기반·체크아웃됨). develop은 이미 **M5-P1 병합**(PR #20, 9323cf3) 포함. FK 수정은 M5-P1과 독립(겹침 0).
- **계약 `tasks/sprint-contract.md` = S-SQLSERVER-FK-CASCADE 작성·on disk(uncommitted, 보존됨)**. Planner 완료. 사용자가 계획+SQL 버전(2025) 확인 → **사실상 승인, Step 3부터 진행**. Planner 재실행 금지.
- 작업트리 미커밋: `tasks/sprint-contract.md`·`tasks/workflow-*.md`(템플릿 동기화)·이 노트. `.claude/`는 세션 로컬.

## 재개 시 첫 동작 (정확히): 3tier-start Step 3부터
1. **정식 팀 생성**(재시작으로 좀비 제거됨 → TeamCreate 깨끗): `sprint-Rcs3dsInterlockingWcs`. generator·evaluator(opus, general-purpose, background) 스폰. (standalone 우회 불요.)
2. Generator↔Evaluator 루프 → 독립 코드리뷰(Step 4.5) → 커밋(HEAD=fix/sqlserver-fk-cascade 확인) → push → PR(base develop).

## 스프린트 핵심 (계약 전문은 sprint-contract.md)
- **결함**: 모든 필수 FK가 `onDelete: Cascade` → `destination→cell→sorter_command` + `destination→piece→sorter_command` 수렴 → **SQL Server 오류 1785**(다중 캐스케이드 경로). SQLite는 미강제라 그간 안 드러남. 운영=SqlServer라 필수 수정.
- **수정**: `src/Wcs.Data/WcsDbContext.cs` `OnModelCreating`에서 1785 유발 FK들의 `DeleteBehavior`를 **Restrict/NoAction**으로 명시 + **양 provider 마이그레이션 재생성**(Wcs.Migrations.SqlServer·Wcs.Migrations.Sqlite 각 신규 1개 + 각 ModelSnapshot).
- **Restrict 안전**: src에 `.Remove`/`RemoveRange`/`ExecuteDelete`/명시 OnDelete **0건**(grep 확인) → 앱 캐스케이드 삭제 미의존 → 동작 영향 0.
- **무변경 가드**: 테이블/컬럼/인덱스/판정/핸드셰이크/API/Entities/appsettings/DbInitializer **0줄** — FK onDelete 메타만. 새 컬럼·테이블·인덱스 0. 기존 3 마이그레이션 무손상 + 신규 1개씩.

## Acceptance (최우선 40% — fresh evidence 의무)
- **실 SQL Server 적용 성공**: `dotnet ef database update --project src/Wcs.Migrations.SqlServer --startup-project src/Wcs.Migrations.SqlServer --connection "Server=localhost;Database=WcsCascadeTest;Trusted_Connection=True;TrustServerCertificate=True"` → **1785 0건**, 전 16테이블+FK 생성. 완료 후 `DROP DATABASE WcsCascadeTest`(흔적 0).
- **인스턴스 주의**: `localhost` = **SQL Server 2025**(17.0, 사용자 실 인스턴스·연결 검증됨) 사용. `(localdb)\MSSQLLocalDB`=2019은 쓰지 말 것(실 환경=localhost 2025).
- 나머지: 146 테스트 GREEN·회귀 0 / 양 provider `dotnet ef migrations has-pending-model-changes`=No changes / git diff가 OnModelCreating+양 마이그레이션+양 스냅샷에 국한.

## 함정 (이번 세션서 학습)
- **ef 명령 startup-project**: `--startup-project`를 `Wcs.Data`로 하면 "Wcs.Migrations.X.dll not found" 실패. **`--project`와 `--startup-project` 둘 다 해당 마이그레이션 어셈블리**(Wcs.Migrations.SqlServer/Sqlite)로 지정해야 함.
- **S-M4-P1 이중 provider 함정**: 어셈블리당 독립 ModelSnapshot. 양 provider 각자 design-time factory로 따로 `migrations add` + 양쪽 No changes 재확인.
- SQL Server 2025 호환: EF Core 9가 2025에 정상 연결됨(이미 입증 — DB 생성까지 성공, FK 1785만 실패). 하위 호환 OK.

## FK 수정 PR 병합 후 → 실 3DS 테스트 준비 (다음 단계)
develop = PR#19 + M5-P1 + FK수정 상태에서:
1. **SQL Server DB + 16셀 데이터**(사용자: sqlcmd 직접 삽입):
   - DB명 `Rcs3dsInterlockingWcs`(localhost 2025). EF가 자동 생성(FK 수정 후 1785 없음) — `dotnet ef database update` 또는 콜드스타트.
   - **16셀 작업 데이터**(확정): 소터 chuteNo=30 + 셀 16개(cell_no 1~16·capacity=3) + work_batch(work_date=내일) + 오더/바코드 16개(`0701-CELL-01`~`16`·planned_qty=3) + cell_assignment 16건(바코드/오더-N↔셀-N 결정적 사전할당). 피스는 미생성(테스트 중 IF-05로 투입 시 생성). → sqlcmd INSERT 스크립트(`scripts/seed-field-16cells.sql`).
2. **appsettings 필드 구성(working tree·커밋 안 함)**: `Database:Provider=SqlServer` + `ConnectionStrings:WcsDb=Server=localhost;Database=Rcs3dsInterlockingWcs;Trusted_Connection=True;TrustServerCertificate=True` + `Sorters[0].Transport=Rtu`(시리얼 값=현장 확인) + `Database:SeedOnStartup=false`(dev 시드 끄고 sqlcmd 16셀만).
3. **현장 확인 필요**: RTU 시리얼(COM/baud/parity/stopbits/unitid)·**3DS 레지스터 주소 맵**(다르면 RegisterMap 코드 수정)·**cell_no 1~16이 실 3DS 셀 번호와 일치하는지**(C_CellNo로 나감).
4. 구동: `set ASPNETCORE_ENVIRONMENT` (Production이면 시드 off 기본) `dotnet run --project src/Wcs.Api` → curl로 IF-05(바코드)→IF-09→IF-10 → 핸드셰이크 관찰.

## 절대 준수
- 커밋·브랜치 전환 orchestrator만. 커밋 전 `git rev-parse --abbrev-ref HEAD`=fix/sqlserver-fk-cascade 확인. push→PR(base develop). 병합은 사용자.
