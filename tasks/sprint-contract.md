# Sprint Contract — S-SQLSERVER-FK-CASCADE (SQL Server 1785 FK 캐스케이드 순환 제거)

> 작성: Planner Subagent · 2026-06-30
> 본 계약은 **WHAT / WHERE / 검증(Acceptance)** 만 규정한다. **HOW(어떤 FK에 어떤 DeleteBehavior를 줄지·마이그레이션 생성 절차)는 Generator가 결정**한다.
> 3-Tier: Planner(이 문서) → 사용자 확인 → Generator ↔ Evaluator 루프.

> ### 개정 (Amendment) — 2026-06-30 (사용자 승인, Option 1)
> **사유**: Generator가 §3①(빈 SQL Server 콜드스타트 `database update` 성공)과 §3⑤(기존 3 마이그레이션 무손상)이 **SqlServer에서 상호 배타**임을 입증. 1785는 삭제 시점이 아니라 **제약 생성(CREATE TABLE) 시점**에 발생 → `database update`가 가장 먼저 적용하는 **Initial 마이그레이션의 `CREATE TABLE sorter_command ... ON DELETE CASCADE`에서 즉시 1785**로 중단되고, 그 뒤의 증분 마이그레이션엔 도달하지 못함. SqlServer엔 "Initial이 적용된 DB"가 물리적으로 존재할 수 없으므로(애초에 1785로 실패) §3⑤의 보호 의도(이미 배포된 DB의 증분 적용)는 SqlServer에서 공허.
> **결정(사용자 승인)**: **Initial 마이그레이션 직접 수정** — 양 provider Initial.cs의 1785 유발 FK들을 NO ACTION으로 패치(마이그레이션 ID 동일 유지), 별도 신규 증분 마이그레이션은 폐기. SQLite는 캐스케이드 미강제·앱 캐스케이드 의존 0·EF 히스토리는 ID만 추적(콘텐츠 해시 아님)이라 기존 dev `wcs.db`도 무손상.
> **영향**: 아래 §2(2·3), §3(①·⑤), §4의 "신규 마이그레이션 1개씩 추가" 프레이밍을 "Initial 직접 수정"으로 대체. 무변경 가드(FK onDelete 메타만·구조 변경 0)는 **불변**.
>
> ### 개정 2 (Amendment 2) — 2026-06-30 (사용자 승인, 스쿼시)
> **사유**: Option 1로 FK 1785는 해소(콜드스타트가 FK 단계 통과 입증)됐으나, 직후 단계에서 **2차 잠복 버그(SQL Server 오류 207)** 발현 — SqlServer `Initial.cs`의 filtered unique index가 존재하지 않는 컬럼 `[is_active]`(물리명 `IsActive`) 참조(기존 타이핑 오류, `git show HEAD` 동일 확인). 더 중요한 사실: **Initial·P2a·P1 3개 마이그레이션 전부 SQL Server에서 한 번도 성공 적용된 적 없음**(1785가 늘 먼저 실패) → 히스토리 전체가 SQL Server 미검증, 207 외 하류 잠복 버그 잔존 가능. 문제 본질 = "SQLite로만 작성·검증돼 SQL Server 비호환 산출물이 누적된 미검증 히스토리".
> **결정(사용자 승인)**: **마이그레이션 스쿼시** — 양 provider의 기존 3개 마이그레이션을 전부 폐기하고, **현재(검증된) 모델에서 단일 `Initial`을 provider별로 재생성**(S-M4-P1 이중 provider 절차: 각 design-time factory로 독립 `migrations add`). 머신 생성이라 NoAction FK·올바른 컬럼명·올바른 필터가 모델에서 자동 반영 → 1785·207·잠복버그 클래스 일괄 제거. `OnModelCreating`의 Restrict 10개는 **모델 원천이므로 유지**(스쿼시가 이를 반영해 생성).
> **영향**: 아래 §2(2·3), §3⑤, §4의 "Initial 직접 수정/기존 3개 유지" 프레이밍을 **"양 provider 단일 Initial 재생성(스쿼시)"**으로 대체. 마이그레이션 ID는 새로 부여됨(베이스라인 무손상 기준 폐기 — 사용자 승인). 최종 **스키마(16테이블·FK·인덱스 구조)는 불변**(동일 모델에서 생성). 기존 dev `wcs.db`는 히스토리 불일치로 재생성 필요(소모성·테스트는 인메모리 SQLite라 무관).

---

## 0. 배경 / 결함 (확정 사실)

실 SQL Server에 스키마 생성/콜드스타트 Migrate 시 **SQL Server 오류 1785** 발생:
`FK_sorter_command_piece_PieceId` 등 FK가 "한 테이블로의 **다중 캐스케이드 경로** 또는 순환"을 만들어 SQL Server가 `CREATE TABLE`/FK 생성을 거부한다.

- **원인**: EF 기본 DeleteBehavior(필수 관계 = `Cascade`)가 적용되어 여러 FK가 같은 자식 테이블로 캐스케이드 경로를 중첩시킨다. SQLite는 이 제약을 강제하지 않아 지금까지 전 테스트·라이브(SQLite)에서 드러나지 않았고, **운영 provider인 SqlServer에서 처음 발현**.
- **영향**: `dotnet ef database update`(SqlServer) 실패 + 앱 콜드스타트 자동 provision 실패. 콜드스타트 Migrate 경로는 `src/Wcs.Api/Startup/DbInitializer.cs`(M5-P1, `Database.Migrate()` — fail-loud로 전파)이므로 SqlServer 기동 자체가 막힌다.
- **운영은 SqlServer라 반드시 수정 필요**(`appsettings`의 `Database:Provider`를 SqlServer로 두면 기동 불가).

### 직접 확인한 다중 캐스케이드 경로 (현 Initial 마이그레이션 기준 — Generator 참고용, 해소 대상은 Generator가 최종 식별)
모든 필수 FK가 `onDelete: Cascade`로 생성되어 다음 수렴이 발생:
- `destination → cell`(Cascade) **그리고** `destination → piece`(Cascade)
- `cell → sorter_command`(Cascade) **그리고** `piece → sorter_command`(Cascade)
  → `destination`에서 `sorter_command`로 **두 캐스케이드 경로** = 1785 (대표 케이스, 메시지의 `FK_sorter_command_piece_PieceId`와 일치)
- `cell → cell_assignment`(Cascade) **그리고** `wcs_order → cell_assignment`(Cascade) → `cell_assignment`로 다중 경로
- `wcs_order → order_item → piece`, `destination → piece` 등 `piece` 및 그 자식(`piece_event`/`alarm`/`sorter_command`)으로의 다중 수렴.

> 주: `piece.OrderItemId`/`AgvId`/`InductionId`/`DestinationId`(P2a 이후 nullable), `wcs_order.DestinationId`, `chute_detail.PrinterId`, `alarm.PieceId`는 **nullable FK** → EF 기본이 이미 비-Cascade일 수 있으니 Generator가 현 ModelSnapshot/마이그레이션에서 실제 onDelete를 확인할 것. 필수 FK(non-null)가 Cascade인 것이 1785의 핵심.

### 앱의 캐스케이드 삭제 의존성 — 없음 (확인 완료)
`src/` 전체에 `.Remove(` / `.RemoveRange(` / `ExecuteDelete` / 명시적 `OnDelete`·`DeleteBehavior` 호출 **0건**(생성된 마이그레이션 파일 제외). 앱은 append-only 기록 + 일배치 퍼지(ERD §보존)로 동작하며 **FK 캐스케이드 삭제에 의존하지 않는다.** → 캐스케이드를 Restrict/NoAction으로 바꿔도 앱 동작 영향 0.

---

## 1. Goal (목표)

SqlServer 마이그레이션이 **실 SQL Server에 성공적으로 적용**되도록 FK 캐스케이드 순환/다중경로를 제거한다(`OnModelCreating`에서 DeleteBehavior 명시).
- **불변**: 테이블/컬럼/인덱스 구조, 엔티티 속성, 판정(Decide)·핸드셰이크·API 동작/필드, p_id 순환·이력 분리 원칙. **바뀌는 것은 FK 삭제 거동(메타)뿐**이며 앱은 캐스케이드 삭제에 의존하지 않으므로 동작 영향 0.
- 양 provider(SqlServer·Sqlite) 모델 정합(둘 다 `has-pending-model-changes` = No changes).

---

## 2. Scope — WHAT / WHERE

### 변경 대상 (IN)
1. **`src/Wcs.Data/WcsDbContext.cs` — `OnModelCreating`**: 1785를 유발하는 FK들의 `DeleteBehavior`를 명시(`.OnDelete(...)`).
   - 현재 `OnModelCreating`에는 어떤 FK에도 명시적 `.OnDelete()`가 없음(전부 EF 기본). 여기에 명시 추가.
2. **`src/Wcs.Migrations.SqlServer/Migrations/`** — **스쿼시**: 기존 마이그레이션 전부 삭제 후 현재 모델에서 **단일 `Initial` 재생성**(`SqlServerDesignTimeFactory` 경유, `--project`·`--startup-project` 둘 다 `Wcs.Migrations.SqlServer`) + 새 `WcsDbContextModelSnapshot.cs`. 마이그레이션 ID 새로 부여.
3. **`src/Wcs.Migrations.Sqlite/Migrations/`** — 동일 **스쿼시**: 전부 삭제 후 단일 `Initial` 재생성(`SqliteDesignTimeFactory` 경유) + 새 스냅샷.
   - 각 어셈블리는 **독립 ModelSnapshot 1개**(S-M4-P1 교훈). 양쪽 **독립**으로 `migrations add` → 양쪽 `has-pending-model-changes` = No changes 재확인.
   - `OnModelCreating`의 Restrict 10개는 모델 원천이므로 유지 → 재생성된 Initial이 NoAction FK·올바른 컬럼명·필터를 자동 반영.
   - (개정 전 신규 `FkRestrictNoCascade` 및 Option 1 in-place 산출물은 스쿼시로 모두 대체.)

### 변경 금지 (OUT) — 무변경 가드
- 테이블/컬럼/인덱스/UNIQUE/CHECK/PK 구조 — **새 컬럼·새 테이블·새 인덱스 0**. 변경되는 마이그레이션 산출물은 **FK 제약의 onDelete(ReferentialAction) 메타뿐**.
- `src/Wcs.Core/`(판정 엔진)·`src/Wcs.PlcGateway/`·`src/Wcs.Sim3ds/` — 0줄.
- API(`src/Wcs.Api/`)·핸드셰이크·DTO·엔티티 속성(`Entities.cs`) — 0줄(엔티티 nullable/타입 불변).
- `DbInitializer.cs`·시드·appsettings — 0줄(코드 경로 불변, 스키마만 정상화).
- provider 분기 동시성 토큰(RowVersion/XminRowVersion)·filtered index 분기 로직 — 불변.

### DeleteBehavior 선택 원칙 (Generator 결정 — 계약은 제약만)
- **유일 목표 제약**: SQL Server 1785(다중 캐스케이드 경로/순환) **해소**. 1785가 사라지는 한 구체 behavior 선택은 Generator 재량.
- 필수 FK(non-null)로 캐스케이드 순환·다중경로를 만드는 것 → `Restrict`(= NoAction) 권장. 앱이 캐스케이드 삭제에 의존하지 않음이 확인됐으므로 안전.
- nullable FK → 의미에 맞게 `SetNull` 또는 `NoAction`/`Restrict`. (단 SetNull도 SQL Server에선 "다중 SetNull 경로"가 또 다른 1785를 만들 수 있으니 검증으로 확인.)
- **의미 보존 원칙**: 데이터 삭제 거동을 바꾸되 **앱 런타임 동작에 영향 0**이어야 한다(append-only + 배치 퍼지 전제). 캐스케이드에 의존하는 신규 코드 도입 금지.
- 변경은 **필요한 FK에 국한**(1785 해소에 불필요한 FK까지 일괄 Restrict로 바꿔 스냅샷 노이즈를 키우지 말 것 — 단, 전역 일관 정책으로 규약 적용이 더 깔끔하다면 그 선택도 허용하되 무변경 가드는 "FK onDelete 메타만"을 유지).

---

## 3. Evaluation Criteria (가중치)

| # | 기준 | 가중치 | 검증 방법(Fresh evidence 의무) |
|---|------|--------|------------------------------|
| ① | **SqlServer 스키마 실제 적용 성공** | **40%** | 실 SQL Server(**localhost = SQL Server 2025**, LocalDB 2019 사용 금지)에 `dotnet ef database update --project src/Wcs.Migrations.SqlServer --startup-project src/Wcs.Migrations.SqlServer --connection "Server=localhost;Database=WcsCascadeTest;Trusted_Connection=True;TrustServerCertificate=True"` → **1785 0건**, 전 16테이블 + 모든 FK 생성 성공. 검증 후 테스트 DB **drop**(흔적 0). 적용 로그/테이블 목록 캡처. (함정: `--startup-project`를 `Wcs.Data`로 하면 "Wcs.Migrations.X.dll not found" 실패 — `--project`·`--startup-project` 둘 다 마이그레이션 어셈블리로.) |
| ② | 기존 **146 테스트(SQLite) GREEN · 회귀 0** | 25% | `dotnet test` 146/146 GREEN. split 불변. 단언/테스트 코드 변경 0(diff로 입증). |
| ③ | 양 provider **`has-pending-model-changes` = No changes** | 20% | `dotnet ef migrations has-pending-model-changes` 를 SqlServer·Sqlite 양 project로 각각 실행 → 둘 다 "No changes"(M4-P1 함정: 스냅샷 모델 정합). |
| ④ | **무변경 가드** | 10% | `git diff`가 `OnModelCreating` + 양 마이그레이션 신규 파일 + 양 ModelSnapshot에 국한. 신규 컬럼/테이블/인덱스 0. 마이그레이션 Up/Down이 **FK drop+recreate(onDelete 변경)만** 포함(컬럼 alter·테이블 변경 0). Core/PlcGateway/Sim3ds/API/Entities diff 0. |
| ⑤ | 마이그레이션 **스쿼시 — 최종 스키마 불변** (개정 2) | 5% | 양 provider **단일 `Initial`로 스쿼시**(기존 3개 폐기·재생성). 재생성 Initial이 만드는 **최종 스키마 = 기존 모델과 동일**(16테이블·컬럼·인덱스·UNIQUE·CHECK·PK 불변, FK onDelete만 NoAction). 변경 = 마이그레이션 히스토리 표현뿐(ID 새로 부여). `git diff`가 양 Migrations 디렉터리 + `OnModelCreating`에 국한, 그 외 0줄. |

---

## 4. Completion (Done 조건 — 전부 충족)

- [ ] `dotnet build` 0 error / 0 warning.
- [ ] `dotnet test` **146/146 GREEN**(회귀 0).
- [ ] **SqlServer `dotnet ef database update`(실 SQL Server) 성공 — 1785·207 재발 0**, 전 16테이블+FK+인덱스 생성(단일 Initial 적용), 완료 후 DB drop.
- [ ] **SQLite `dotnet ef database update` 성공**(또는 테스트의 EnsureCreated 경로 무파손 — SQLite 마이그레이션 적용도 1785 무관하게 정상).
- [ ] 양 provider **`has-pending-model-changes` = No changes**.
- [ ] `git diff`가 `src/Wcs.Data/WcsDbContext.cs`(OnModelCreating) + `src/Wcs.Migrations.SqlServer/Migrations/`(스쿼시: 기존 삭제+단일 Initial+스냅샷) + `src/Wcs.Migrations.Sqlite/Migrations/`(동일)에 **국한**. 그 외 0줄. 재생성 Initial의 최종 스키마가 기존과 동일(컬럼/테이블/인덱스/UNIQUE/CHECK/PK 불변, FK onDelete만 NoAction)임을 확인.
- [ ] 콜드스타트 회귀(선택·가능 시): SqlServer provider로 앱 기동(빈 DB) 시 `DbInitializer` Migrate가 1785 없이 성공해 정상 기동(또는 동등하게 `database update` 성공으로 입증).

---

## 5. Detected Project Type & Verification Scenarios

- **Project Type**: Backend / API (ASP.NET Core Minimal API + Controller + EF Core 이중 provider).
- **타입 슬롯 Verification Scenarios**:
  1. **SqlServer 적용 성공**: 실 SQL Server에 전 스키마 생성(1785 0) → drop.
  2. **SQLite 회귀 0**: 146 테스트 GREEN + SQLite 마이그레이션/EnsureCreated 무파손.
  3. **양 snapshot 정합**: SqlServer·Sqlite 둘 다 has-pending-model-changes No changes.
  4. **캐스케이드 의미 보존**: 앱 동작(판정/핸드셰이크/API) 불변 — FK 삭제 거동만 변경, 캐스케이드 의존 코드 0(현 상태 유지).

---

## 6. 함정 / 선행 교훈 (반드시 회피)

- **S-M4-P1 — EF 이중 provider 마이그레이션 함정**: 어셈블리당 ModelSnapshot 1개. provider별 output-dir만 나누고 스냅샷을 공유하면 마지막 생성 provider가 다른 쪽 스냅샷을 덮어 증분이 손상된다. → **양 provider 각각 독립적으로 마이그레이션 add**(각자의 design-time factory: `SqlServerDesignTimeFactory`·`SqliteDesignTimeFactory` 경유, `--project`를 해당 마이그레이션 어셈블리로 지정). add 후 **양쪽 `has-pending-model-changes` = No changes 재확인**.
- **S-M4-P2a — Ignore() vs 물리 컬럼**: 문서/주장은 실제 마이그레이션 산출물과 대조(이번엔 컬럼 변경이 없어야 하므로, 마이그레이션 Up에 컬럼/테이블 alter가 끼면 스코프 위반).
- **무변경 입증법**: 보호 영역 `git diff` 0바이트 + 146 split 불변(여러 회 GREEN). FK onDelete 외 산출물이 마이그레이션에 들어오면 즉시 스코프 위반.
- **메타 교훈(반복 확인)**: 인메모리/SQLite 단일 경로 테스트는 실 SqlServer DDL 경로(1785 같은 provider 고유 제약)를 구조적으로 못 본다 → **이번 acceptance는 반드시 실 SQL Server `database update`를 fresh로 실행해 입증**(SQLite GREEN만으로 닫지 말 것).

---

## 7. 미확정 사항 / 사용자 확인 필요

> 아래는 Generator/Evaluator 진행 중 막히면 사용자에게 질문. ("SqlServer 수정 진행" 자체는 재질문 금지 — 확정.)

1. **실 SQL Server 테스트 DB 접속 정보**: ①번 acceptance를 위해 Generator/Evaluator가 사용할 localhost SQL Server 인스턴스·연결문자열(예: `Server=localhost;Database=WcsCascadeTest;Trusted_Connection=True;TrustServerCertificate=True;`)이 필요.
   - **기본안: LocalDB(`(localdb)\mssqllocaldb`)로 검증**(SqlServerDesignTimeFactory 기본 연결과 동일 계열, LocalDB도 1785를 동일하게 강제하므로 검증 유효). 별도 인스턴스가 제공되면 `--connection` 인자로 그것 사용. 검증 후 DB drop.
   - LocalDB/SQL Server가 머신에 미설치라 적용 실행 불가하면 그때 사용자에게 접속 정보를 질문.
2. (정보) DeleteBehavior 구체 선택은 Generator 재량이나, 만약 nullable FK SetNull이 또 다른 1785/원치 않는 NULL화를 유발하면 NoAction/Restrict로 통일 — 진행 중 판단.

---

## 8. Planner Self-Check

- [x] WHAT/WHERE/검증만 규정, HOW(구체 behavior·생성 절차)는 Generator에 위임.
- [x] 절대규칙 점검: PLC 큐·TgtFloor·Ready 등 런타임 규칙과 무관(스키마 메타 변경). 절대규칙 8(연결문자열 하드코딩 금지)은 acceptance용 테스트 DB 연결을 `--connection` 인자/design-time factory로 처리(운영 appsettings 불변)하여 준수.
- [x] 무변경 가드가 구조적 변경(컬럼/테이블/인덱스/판정/API)을 전부 배제하고 FK onDelete 메타로 한정.
- [x] S-M4-P1 이중 provider 함정(독립 스냅샷·양쪽 No changes) 명시.
- [x] 앱 캐스케이드 의존성 부재를 코드 grep으로 사전 확인(Restrict 안전성 근거).
- [x] acceptance 최우선 = 실 SQL Server `database update` 성공(1785 0). SQLite GREEN만으로 닫지 않도록 명시.
- [x] 기존 3 마이그레이션 유지 + 신규 1개씩 증분(베이스라인 무손상).
- [x] 미확정(실 SQL Server 접속 정보)만 질문 슬롯으로 분리, 수정 진행 여부 재질문 없음.
