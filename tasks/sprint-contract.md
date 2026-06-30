# Sprint Contract — S-FIELD-SEED-16CELLS (실 3DS 하드웨어 테스트용 16셀 작업 데이터 생성 + 현장 구성)

> 작성: Planner Subagent · 2026-06-30
> 본 계약은 **WHAT / WHERE / 검증(Acceptance)** 만 규정한다. **HOW(sqlcmd 스크립트 작성 방식·INSERT/MERGE 구조·멱등 구현 방법·EF database update vs 콜드스타트 중 무엇으로 스키마 생성할지)는 Generator가 결정**한다.
> 3-Tier: Planner(이 문서) → 사용자 확인 → Generator ↔ Evaluator 루프.
> 직전 스프린트(S-SQLSERVER-FK-CASCADE)는 PR #21로 **이미 머지됨**(FK 1785·컬럼 207 해소). 이 계약은 그 결과를 전제로 한다.

> ### 개정 (Amendment) — 2026-06-30 (사용자 승인, "SqlServer로 전부 전환")
> **사유**: Generator가 base `appsettings.json`을 `Provider=SqlServer`로 두면 `dotnet test`가 105 실패함을 입증 — `WebApplicationFactory<Program>`가 base Provider로 EF SqlServer provider 서비스를 등록하는데 테스트 팩토리가 `DbContextOptions`만 SQLite로 교체하고 provider 서비스는 남겨 "Only a single database provider" 예외. C6(146 GREEN) ↔ §2 IN(base appsettings SqlServer)이 한 파일에서 상호 배타.
> **결정(사용자 승인)**: "SQLite는 사용 안 함 → **SqlServer로 전부 전환**." base `appsettings.json`을 SqlServer 현장 구성으로 바꾸고 **커밋**한다(working-tree-only 폐기). 테스트가 base=SqlServer에서도 146 GREEN이 되도록 **테스트 인프라(WebApplicationFactory 등 tests/)를 수정 허용**(provider 충돌 해소 — 예: 테스트가 `Database:Provider=Sqlite` config를 주입해 인메모리 SQLite 더블 강제). 테스트는 속도·격리·CI를 위해 **인메모리 SQLite 더블 유지**(제품 런타임 SqlServer와 무관 — 테스트를 실 SqlServer로 옮기는 것은 별도 스프린트).
> **영향**: §2 IN(3)·§2 OUT·C6·§7(5) 갱신 — appsettings는 이제 base 수정·**커밋**, tests/ 인프라 수정 허용(제품 로직·DbSeeder·마이그레이션·WcsDbContext·ERD는 여전히 0줄), C6는 base=SqlServer에서 146 GREEN. (이 fix는 5-iteration cap의 iteration 2.)

---

## 0. 배경 / 전제 (확정 사실)

실 3DS 하드웨어 연동 테스트를 위해, 운영 provider인 **SQL Server**(localhost = SQL Server 2025 인스턴스)에 작업 테스트 데이터를 적재하고 WCS를 현장 구성으로 기동한다.

- **스키마 생성은 더 이상 막히지 않는다**: 직전 FK 스프린트가 양 provider 마이그레이션을 스쿼시·재생성해 SQL Server에서 1785(다중 캐스케이드 경로)·207(존재하지 않는 컬럼 `[is_active]`) 모두 제거됨. 따라서 빈 SQL Server DB에 `dotnet ef database update`(또는 앱 콜드스타트 자동 Migrate)로 16테이블+FK+인덱스가 정상 생성된다.
- **이 스프린트는 코드를 바꾸지 않는다**: 제품 코드(src/)·마이그레이션·DbSeeder·ERD 스키마는 한 줄도 건드리지 않는다. 산출물은 (a) **신규 데이터 적재 sqlcmd 스크립트** + (b) **appsettings 현장 구성(working tree only, 커밋 금지)** 두 가지뿐이다.
- 데이터 적재 대상은 **운영 DB**(`Rcs3dsInterlockingWcs` @ localhost)이지 코드가 아니다.

### Generator가 반드시 알아야 할 결정적 사실 (직접 코드 확인 결과)

EF 매핑을 직접 확인했다. sqlcmd 스크립트는 다음을 정확히 반영해야 한다:

1. **테이블명은 스네이크 케이스**(`destination`, `cell`, `cell_assignment`, `work_batch`, `wcs_order`, `order_item`). `WcsDbContext.ToTable(...)`로 매핑됨.
2. **컬럼명은 PascalCase**(엔티티 프로퍼티명 그대로). EF는 컬럼명을 매핑하지 않으므로 물리 컬럼은 `ChuteNo`, `DestType`, `CellNo`, `Capacity`, `Enabled`, `WorkDate`, `BatchNo`, `WaveNo`, `OrderNo`, `Barcode`, `PlannedQty`, `ReservedQty`, `SortedQty`, `AssignedAt`, `ReleasedAt`, `DestinationId`, `WorkBatchId`, `OrderId`, `CellId`, `CreatedAt`, `UpdatedAt`, `IsActive`, `Status` 등. (직전 스프린트 코드 코멘트에서 `[ReleasedAt]`·`[IsActive]`·`[Status]` 물리 PascalCase 명시 확인.)
3. **enum 컬럼은 문자열로 저장**(`HasConversion<string>()`). 즉 `DestType='SORTER_3D'`, `Status`(destination)`='NORMAL'`, `Status`(work_batch)`='RUNNING'`, `OrderType='GENERAL'`, `Status`(wcs_order)`='RUNNING'` 등 **문자열 리터럴**로 INSERT. 정수 코드 금지.
4. **PK는 bigint identity** → INSERT 시 `Id` 생략, 생성된 키는 `SCOPE_IDENTITY()`로 받아 FK에 사용(또는 자연키 조회로 매핑). `chute_detail`만 PK=FK(identity 아님)지만 이 스프린트는 chute_detail을 만들지 않는다.
5. **SQL Server provider에서 동시성 컬럼**: `RowVersion`은 rowversion(자동 생성·INSERT 금지), `XminRowVersion`은 Ignore(물리 컬럼 없음) → 둘 다 INSERT에서 생략.
6. **DbSeeder와의 충돌 회피**: `src/Wcs.Data/DbSeeder.cs`는 chuteNo=30 SORTER_3D + cell_no 1~3(소터 소속) + `work_batch(BatchNo="SEED", WaveNo=1)` + ORD-001~005를 시드한다. **본 스프린트 현장 구성은 `Database:SeedOnStartup=false`라 DbSeeder가 돌지 않는 것이 정상**이나, 스크립트는 (a) 소터 destination chuteNo=30이 이미 존재하면 재사용하고 (b) work_batch는 `UQ(WorkDate,BatchNo,WaveNo)` 충돌을 피하는 식별자를 쓰며 (c) cell_no 1~3이 이미 있어도 16셀이 멱등하게 보장되도록 작성한다. 어느 경로로도 DbSeeder 시드 데이터와 충돌(중복키 예외)하지 않아야 한다.

---

## 1. Goal

실 3DS 하드웨어 테스트를 위해 **SQL Server DB `Rcs3dsInterlockingWcs`(localhost 2025)에 결정적 16셀 작업 데이터를 적재**하고, 그 적재를 **재실행 안전(멱등)한 sqlcmd 스크립트**로 자산화한다. 동시에 WCS를 **SQL Server + RTU 현장 구성**으로 기동할 수 있도록 appsettings를 working tree에서 구성한다(커밋 금지). 적재된 데이터가 런타임에 실제로 동작함을 IF-05 기능 확인으로 입증한다.

## 2. Scope (IN / OUT)

### IN
1. **DB 스키마 생성**: 빈 SQL Server `Rcs3dsInterlockingWcs`에 머지된 마이그레이션으로 16테이블+FK+인덱스 생성(`dotnet ef database update` 또는 앱 콜드스타트 — Generator 선택). 1785·207 0건.
2. **신규 파일** `scripts/seed-field-16cells.sql` — 아래 §데이터 명세를 적재하는 재사용 sqlcmd 스크립트. 멱등(2회 이상 실행해도 중복/오류 0) 권장.
3. **현장 구성** `src/Wcs.Api/appsettings.json` **base 수정 + 커밋**(개정: SqlServer로 전부 전환):
   - `Database:Provider = "SqlServer"`
   - `ConnectionStrings:WcsDb = "Server=localhost;Database=Rcs3dsInterlockingWcs;Trusted_Connection=True;TrustServerCertificate=True"`
   - `Sorters[0].Transport = "Rtu"` (단 인메모리 SQLite 테스트가 실 Modbus를 쓰지 않으므로 무해해야 함 — 테스트가 Tcp/Sim sorter를 자체 오버라이드하는지 확인; Rtu가 146을 깨면 테스트 오버라이드로 격리)
   - `Database:SeedOnStartup = false`
   - (RTU 시리얼 값·OperationalFloor 등 현장 의존 값은 §8 미확정 — 현장 확인 후 채움. 이 스프린트에서 임의 추측 금지·기존 기본값 유지.)
4. **테스트 인프라 수정**(tests/ — base=SqlServer에서 146 GREEN 회복): `WebApplicationFactory` 계열이 base `Provider=SqlServer`로 인한 provider 충돌을 해소하도록 수정(권장: 테스트 setup이 `Database:Provider=Sqlite`를 in-memory config로 주입해 Program이 SQLite 분기 → 기존 인메모리 SQLite 더블 그대로, 또는 EF SqlServer provider 서비스 디스크립터 제거). **제품 코드(src/)·DbSeeder·마이그레이션·WcsDbContext·ERD는 불변**. 테스트 단언/토폴로지 의미 변경 0(provider 결선만).

### OUT (무변경 가드)
- `src/` **제품 코드 0줄**(Api·Core·PlcGateway·Data 로직·Sim3ds 등 — `appsettings.json`만 예외).
- `src/Wcs.Data/DbSeeder.cs` **0줄**.
- 양 provider 마이그레이션(`Wcs.Migrations.SqlServer`·`Wcs.Migrations.Sqlite`)·`WcsDbContext.cs` **0줄**.
- `docs/ERD.md` 및 **DB 스키마(테이블·컬럼·FK·인덱스·CHECK) 0 변경** — 데이터만 INSERT, 스키마 불변.
- 테스트 **단언/토폴로지/시나리오 의미 0 변경** — (개정) `tests/`는 **provider 결선(DI/config)만** 수정 허용(base=SqlServer 충돌 해소). 인메모리 SQLite 더블·146 테스트 수·시드 데이터·단언은 불변.

## 3. Detected Project Type

**Backend/API** (ASP.NET Core Minimal API + EF Core + SQL Server. 이번 스프린트의 가시 산출물은 DB 데이터 적재 + IF-05 엔드포인트로의 기능 확인. UI 없음.)

## 4. 데이터 명세 (확정 — 사용자 승인, 재질문 금지)

스크립트는 다음을 **정확히** 적재한다. 모든 enum/문자열은 §0의 결정적 사실(문자열 저장·PascalCase 컬럼) 준수.

| 대상 | 내용 |
|---|---|
| **소터 destination 1개** | `destination`: `DestType='SORTER_3D'`, `ChuteNo=30`, `Floor=NULL`, `Status='NORMAL'`, `IsActive=1`. 이미 존재하면(=DbSeeder가 만든 것) 재사용. |
| **셀 16개** | `cell`: 위 소터 destination 소속(`DestinationId`=그 소터). `CellNo` 1~16, `Capacity=3`, `Enabled=1`. `UQ(DestinationId,CellNo)` 충돌 없이 16개 보장(기존 1~3 있어도 멱등). |
| **work_batch 1개** | `work_batch`: `WorkDate='2026-07-01'`(내일), `Status='RUNNING'`. `BatchNo`/`WaveNo`는 ERD 기본·CHECK 준수 + `UQ(WorkDate,BatchNo,WaveNo)`로 DbSeeder의 `("SEED",1)`와 충돌하지 않는 식별자를 Generator가 선택. |
| **오더 16개** | `wcs_order`: 위 work_batch 소속. `OrderNo` = `0701-CELL-01`~`0701-CELL-16`. `OrderType='GENERAL'`, `DestinationId`=소터 destination, `DestAssignType='UPSTREAM'`, `Status='RUNNING'`. `UQ(WorkBatchId,OrderNo)` 준수. |
| **order_item 16개** | `order_item`: 각 오더 1행. `Barcode` = `0701-CELL-01`~`0701-CELL-16`(오더 N ↔ 바코드 N 동일). `PlannedQty=3`, `ReservedQty=0`, `SortedQty=0`. `UQ(OrderId,Barcode)` 준수. |
| **cell_assignment 16건** | `cell_assignment`: **결정적 N↔N 사전할당** — `CellNo=N`인 셀 ↔ `OrderNo=0701-CELL-N`인 오더(1~16). `AssignedAt`=현재, `ReleasedAt=NULL`(점유 중). 부분 유니크 `(CellId) WHERE [ReleasedAt] IS NULL` 준수(셀당 활성 1건). |
| **piece** | **생성하지 않음**(IF-05 투입 시 런타임 생성). |

## 5. Evaluation Criteria (가중치) — Evaluator 판정 기준

> Evaluator는 fresh evidence(실 SQL Server 쿼리 결과·앱 기동 로그·테스트 실행 출력)로 판정한다. "코드를 읽어보니 맞다"는 불가.

| # | 기준 | 가중치 | 합격선 |
|---|---|---|---|
| C1 | **기능 검증(최우선)**: 앱을 SqlServer 구성으로 기동 후 IF-05로 바코드 `0701-CELL-XX` 질의 시 `result=OK`·`chuteNo=30` 반환(적재 데이터가 런타임에 실제 동작) | 30% | 최소 1개 바코드(예: `0701-CELL-01`)에 대해 OK+chuteNo=30. (스펙상 SORTER_3D면 chuteNo=30 반환 — 현 IF-05 동작 기준 Generator 확인) |
| C2 | **DB 스키마 생성 성공**: 빈 SQL Server에 마이그레이션 적용 → 16테이블+FK+인덱스 생성, **1785·207 0건** | 20% | `database update`(또는 콜드스타트) 무오류 + `sys.tables` 16개 확인 |
| C3 | **16셀 데이터 정확성**: 소터 `ChuteNo=30`/`DestType='SORTER_3D'` · 셀 `CellNo` 1~16·`Capacity=3`·`Enabled=1`(정확히 16행) · `work_batch.WorkDate='2026-07-01'`·`Status='RUNNING'` · 오더/바코드 `0701-CELL-01`~`16`(각 16행)·`PlannedQty=3` | 20% | 실 DB `SELECT COUNT/값` 쿼리로 행수·값 전수 확인 |
| C4 | **cell_assignment N↔N 결정적 16건 + FK 무결성**: `CellNo=N` ↔ `OrderNo=0701-CELL-N` 매핑이 16건 정확·`ReleasedAt` 전부 NULL·고아 FK 0(존재하지 않는 cell/order 참조 0) | 15% | 조인 쿼리로 N↔N 일치 16건 + 고아 0 확인 |
| C5 | **멱등/재실행 안전**: 스크립트 2회 연속 실행해도 중복 행·키 충돌·오류 0(2회차 후에도 셀 16·오더 16·cell_assignment 16 불변) | 10% | 2회 실행 후 행수 재확인 |
| C6 | **무변경 가드 + 회귀 0**(개정): `git diff`가 `scripts/seed-field-16cells.sql`(신규) + `src/Wcs.Api/appsettings.json`(base→SqlServer, **커밋**) + 테스트 인프라 provider 결선(tests/, DI/config만)에 국한 — **src 제품코드·DbSeeder.cs·마이그레이션·WcsDbContext.cs·ERD.md 0줄, 테스트 단언/토폴로지 의미 0 변경** · **base appsettings=SqlServer 상태로 `dotnet test` 146 GREEN**(회귀 0·인메모리 SQLite 더블 유지) | 5% | `git diff --stat` + base=SqlServer로 `dotnet test` 출력 |

## 6. Verification Scenarios (Backend/API — mandatory)

### Explicit list of endpoints touched by this sprint (method + path)
- 이 스프린트는 **엔드포인트 코드를 추가/수정하지 않는다**. 기능 검증에 사용하는(touch가 아니라 exercise하는) 엔드포인트는 **IF-05**(투입 가부 질의 — 바코드 → result/chuteNo). 정확한 method+path는 `src/Wcs.Api`의 IF-05 라우트를 Generator가 확인(예상: `POST /api/...` 계열, `wcs_rcs_interface_kr.html` 정의). API 필드명은 절대규칙 #6 — `pId, agvNo, barcode, inductionNo, chuteNo, qty, timeStamp`(loadQty 아님).

### Happy path per endpoint (expected input → expected output shape)
- **IF-05**: 입력 = 바코드 `0701-CELL-01`(+ 스펙상 필수 필드 pId/agvNo/inductionNo/qty/timeStamp). 출력 = `result=OK` + `chuteNo=30`(SORTER_3D 목적지). 이 출력이 나오면 "적재한 16셀 데이터가 IF-05 판정에 실제로 사용됨"이 입증됨.
- (선택) 동일 입력을 16개 바코드 `0701-CELL-01`~`16`에 대해 반복 시 전부 OK+chuteNo=30(전수 확인은 Evaluator 재량 — 최소 1개는 필수).

### Relevant error cases per endpoint
- **미적재 바코드 → NG 대비**: 적재되지 않은 임의 바코드(예: `0701-CELL-99`)로 IF-05 질의 시 `result=NG`(또는 스펙상 미등록 응답) — 적재 데이터가 "있는 것만" 매칭됨을 음성 대조로 확인(가짜 OK 방지). 정확한 NG 사유 문자열은 현 IF-05 동작 기준(reason은 WCS DB 기록·응답 미포함 — m4p4 메모).
- (4xx/5xx 신규 케이스 없음 — 엔드포인트 미변경. 401/403은 해당 없음.)

### 데이터 무결성 검증 시나리오 (이 스프린트 본질 — 위 C3·C4·C5를 재현하는 쿼리)
- V-DI-1: `SELECT COUNT(*) FROM cell c JOIN destination d ON c.DestinationId=d.Id WHERE d.ChuteNo=30 AND c.Capacity=3 AND c.Enabled=1` = **16**.
- V-DI-2: `SELECT COUNT(*) FROM order_item oi JOIN wcs_order o ON oi.OrderId=o.Id WHERE oi.Barcode LIKE '0701-CELL-%' AND oi.PlannedQty=3` = **16**, 바코드 집합 = `{0701-CELL-01..16}`.
- V-DI-3: N↔N 조인 — `cell.CellNo`와 `wcs_order.OrderNo` 끝 2자리가 일치하는 cell_assignment가 정확히 16건·`ReleasedAt` 전부 NULL.
- V-DI-4: 멱등 — 스크립트 재실행 후 V-DI-1~3 결과 불변.
- V-DI-5: 고아 FK 0 — cell_assignment의 CellId·OrderId가 전부 실재 행 참조.

## 7. Completion Conditions (Evaluator PASS 최소 조건)

1. 실 SQL Server `Rcs3dsInterlockingWcs`에 16셀 데이터가 ERD 정합하게 존재(C2·C3·C4 충족).
2. **IF-05 기능 확인**: 바코드 `0701-CELL-01`(최소 1개)에 OK+chuteNo=30(C1 충족) — fresh 기동·실 응답 캡처.
3. 스크립트 멱등(C5 — 2회 실행 무오류·행수 불변).
4. **무변경 가드**: src 제품코드·마이그레이션·DbSeeder·WcsDbContext·ERD 0줄 변경(테스트는 provider 결선만). base=SqlServer 상태로 `dotnet test` **146 GREEN 회귀 0**(C6).
5. appsettings 현장 구성은 **base `appsettings.json` 수정 + 커밋**(개정 — SqlServer로 전부 전환).
6. 산출 파일 `scripts/seed-field-16cells.sql` 존재·재사용 가능.

## 8. 현장 확인 필요 항목 (미확정 — 사용자/현장에 질문 슬롯, 이 스프린트에서 추측 금지)

이 값들은 appsettings·현장 하드웨어 의존이라 **데이터 적재의 합격 조건에 포함하지 않는다**. 현장 셋업 시 사용자가 확정한다. Generator는 placeholder로 두고 명시 표기한다:

- **RTU 시리얼 파라미터**: `Sorters[0]`의 `PortName(COMx)` · `BaudRate` · `Parity` · `StopBits` · `UnitId`. (현 appsettings 기본값 COM1/9600/Even/One/1 — 현장 실측으로 교체.)
- **3DS 레지스터 주소 맵**: 실 VEICHI PLC의 D레지스터 주소가 `RegisterMap`과 다르면 코드 수정 필요(별도 스프린트 — 이 스프린트 범위 아님).
- **cell_no 1~16 ↔ 실 3DS C_CellNo 일치 여부**: WCS가 셀 번호를 C_CellNo로 내보내므로(절대규칙 영역), 실 3DS의 물리 셀 번호 체계가 1~16과 일치하는지 현장 확인. 불일치 시 cell_no 매핑 재적재 필요.
- **IF-05 정확한 method/path 및 필수 요청 필드**: Generator가 `src/Wcs.Api` 라우트로 확인(기능 검증 C1 수행 전 필수).

---

> Planner self-check — Detected project type: Backend/API. Required scenario slots: 3 (Endpoints touched, Happy path per endpoint, Relevant error cases per endpoint). All slots filled: yes.
