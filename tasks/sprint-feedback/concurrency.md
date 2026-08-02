# Sprint Feedback — S-AUDIT-C-DATA-INTEGRITY
## Dimension ②: Concurrency & Provider-fidelity (항목 ① 동시 IF-05 원자 예약 차감)

**VERDICT: PASS**

Evaluator: concurrency dimension (expert pool). 평가 대상: 계약 §Evaluation Dimensions 2 — 실 SQL Server provider
동시 IF-05 원자 예약 차감 실증. SQLite 대체 불가(rowversion 미증가 — lessons sqlserver-migration-prod-provider).
모든 증거는 이번 세션에서 직접 실행한 fresh tool output. 코드 미수정.

---

### 0. Handoff 확인
- `tasks/sprint-log.md` → `## IMPLEMENTATION COMPLETE (Generator, 2026-07-31)` 마커 존재 확인.
- 대상 코드 직접 판독: `DbRepositories.cs` `EfOrderRepository.QueryDestination` 원자 UPDATE 경로.

---

### 1. 실 로컬 SQL Server 스크래치 DB + from-scratch 마이그레이션

- 인스턴스: **Microsoft SQL Server 2025 (RTM-GDR) 17.0.1125.2, Enterprise Developer Edition** (localhost, 기본 인스턴스 MSSQLSERVER 실행 중). login=DESKTOP-236GOAR\USER, Trusted_Connection.
- 스크래치 DB `WcsAuditCConcur` 생성 후 from-scratch 마이그레이션:
  `dotnet ef database update --project backend/src/Wcs.Migrations.SqlServer --startup-project backend/src/Wcs.Migrations.SqlServer --connection "Server=localhost;Database=WcsAuditCConcur;Trusted_Connection=True;TrustServerCertificate=True"`
  → 9개 마이그레이션(Initial … 20260731021228_AddSorterCommandSortStartedAt) 순차 적용, **Done. EXITCODE=0**. 신규 마이그레이션 0(계약 준수).
- **rowversion 실증 (핵심 — SQLite 재현 불가)**: `order_item.RowVersion` 컬럼 DATA_TYPE = **`timestamp`** (SQL Server rowversion 동시성 토큰, null=YES). 실 prod provider에만 존재하는 물리 컬럼 확인.
- 현장/운영 DB(`Rcs3dsInterlockingWcs`)·포트(5205) **무접촉** — 별도 스크래치 DB·에페메랄 포트만 사용.

### 2. 실 SQL Server 백엔드로 앱 기동
- 빌드: `Wcs.Api` Release BUILD_EXITCODE=0 (선재 NU1903 SQLite 취약성 경고 + 선재 CS8604 1건만, 신규 경고 0).
- 기동: `Database:Provider=SqlServer`, `ConnectionStrings:WcsDb=<스크래치>`, `SeedOnStartup=true`, 에페메랄 포트 **127.0.0.1:5399**. (소터 chuteNo=30 = TCP dead-port 15999 → OFFLINE, IF-05 CHUTE 경로와 무관.)
- `/health` → `status=ok db=True sorters=1` (스크래치 DB 연결 확인). 시드 적용: dest=7·order=5·item=5·piece=0.
- 시나리오 준비: `order_item(TEST-BARCODE-1, CHUTE chuteNo=1, RUNNING)` planned_qty를 매 iter SQL로 축소·reserved_qty=0 리셋 → 동시 수용 상한 강제.

### 3. 동시 IF-05 병렬 실증 (같은 barcode, N=8 동시 발사, 각 요청 distinct pId)

`HttpClient.PostAsync` N개 즉시 발사 후 `Task.WaitAll` (HTTP 레벨 barrier). 매 iter 응답코드 분포 + DB 사후 상태(reserved_qty·piece status·piece_event OVER 감사) 검증.

| iter (pidBase) | planned | http codes | body OK/NG | reserved_qty after | RESERVED piece | DENIED piece | DENIED+OVER audit | verdict |
|---|---|---|---|---|---|---|---|---|
| 1000 | 1 | **200:8** | 1 / 7 | **1** | 1 | 7 | 7 | PASS* |
| 2000 | 1 | **200:8** | 1 / 7 | **1** | 1 | 7 | 7 | PASS |
| 3000 | 1 | **200:8** | 1 / 7 | **1** | 1 | 7 | 7 | PASS |
| 4000 | 1 | **200:8** | 1 / 7 | **1** | 1 | 7 | 7 | PASS |
| 5000 | 1 | **200:8** | 1 / 7 | **1** | 1 | 7 | 7 | PASS |
| 6000 | 1 | **200:8** | 1 / 7 | **1** | 1 | 7 | 7 | PASS |
| 7000 | 1 | **200:8** | 1 / 7 | **1** | 1 | 7 | 7 | PASS |
| 8000 | 3 | **200:8** | 3 / 5 | **3** | 3 | 5 | 5 | PASS |
| 9000 | 5 | **200:8** | 5 / 3 | **5** | 5 | 3 | 3 | PASS |
| 10000 | 1 | **200:8** | 1 / 7 | **1** | 1 | 7 | 7 | PASS |

\* iter1(pidBase 1000)의 "FAIL"은 하네스 다중-sqlcmd 파싱 글리치(RESERVED count 빈 문자열)일 뿐 — DB 직접 조회로 `pId 1001=RESERVED, 1002~1008=DENIED (RESERVED=1/DENIED=7)` 확인. 실질 결과는 PASS. 이후 iter는 단일 쿼리로 파싱 견고화.

계약 단언 전부 충족:
- **미처리 500 = 0건**: 10회 전부 응답코드 분포 `200:8` (500·비-200 전무). rowversion 패자가 500으로 소실되지 않음.
- **초과예약 0**: 최종 reserved_qty가 planned_qty와 **정확히 일치**(1/3/5). `reserved_qty ≤ planned_qty` 위반 0. 원자 WHERE(`reserved_qty+qty <= planned_qty`)가 K와 무관하게 상한을 정확히 강제.
- **DENIED 계약 보존**: 수용 못 한 요청(N−planned)마다 **piece(status=DENIED)** + **piece_event(IF05_REQ reason=OVER) + piece_event(IF05_RES reason=OVER)** 존재. 감사기록 소실 0.
- **한쪽 OK·다른쪽 NG(OVER) 정합**: OK=planned·NG(OVER)=N−planned, piece 총수=8(유령/누수 0).
- 서버 로그 스캔: `Unhandled`/HTTP 500/request `fail:` 전무. 유일한 Exception 라인은 dead-sorter(15999) `Could not connect within the specified time.` = 예상된 OFFLINE(핸들 처리·IF-05 무관). IF-05 결과 로그도 1 OK / 7 OVER로 일치.

### 4. flake 배제 (단독 ≥5회)
- planned=1·N=8 동시 시나리오를 **7회**(pidBase 1000·2000·3000·4000·5000·6000·7000·10000 중 planned=1은 8회) 단독 반복 — 전부 동일 결과(500=0·reserved_qty=1·DENIED 7건 모두 OVER 감사 보존). 추가로 다-당첨(planned=3/5) 변형 2회로 상한 캡핑 정확성 교차 확인. 총 10회 병렬 시나리오 전부 일관. 1회 성공 PASS 아님 — ≥5회 무flake 요건 초과 충족.

### 5. 코드 경로 검증 (diff vs develop)
- `git diff develop -- DbRepositories.cs`:
  - 제거: `-  item.ReservedQty += qty;` + `- _db.SaveChanges();` (추적 RMW 삭제).
  - 추가: `+ int affected = _db.OrderItems.Where(i => i.Id==item.Id && i.ReservedQty + qty <= i.PlannedQty).ExecuteUpdate(... ReservedQty => ReservedQty + qty, UpdatedAt=now)` → `+ if (affected == 0) { tx.Rollback(); overReserved = true; }` → tx 종료 후 `+ if (overReserved) { RecordDenied(...,"OVER",...); return ("NG",null,"OVER",...); }`.
- **원자 조건부 UPDATE 확인**: `WHERE reserved_qty+qty <= planned_qty`, 영향행 0 = OVER. tx 최초 write(:195).
- **추적 RMW 잔재 0**: repo 전역 `ReservedQty +=` 정규식 히트는 주석(:180) 1건뿐 — 실 코드 0.
- **pre-OVER↔차감 TOCTOU 폐쇄**: :97 pre-OVER는 tx 밖 fast-path stale-read일 뿐, 최종 권위는 원자 UPDATE의 WHERE. 10회 병렬 실증(초과예약 0·패자 전원 DENIED·500 0)이 TOCTOU 창 폐쇄를 경험적으로 입증.
- **절대규칙 #7**: 재시도 상수/하드코딩 0(원자 1회 — catch-retry 미채택, OQ-1 준수).

### 6. 정리 (흔적 0)
- 앱 프로세스 종료(PROC_ALIVE_AFTER=False).
- `DROP DATABASE WcsAuditCConcur` → `SCRATCH_DB_GONE`.
- 운영 DB `Rcs3dsInterlockingWcs` 최종 스냅샷 = 기준선과 동일: `piece=1 order_item=572 destination=1` (무접촉 확인).

---

## 결론
**Concurrency & Provider-fidelity 차원 = PASS.** 실 SQL Server 2025(rowversion=timestamp 물리 컬럼 확인) 위에서
같은 barcode 동시 IF-05 8-way 병렬을 planned=1/3/5로 총 10회 실증 — 미처리 500 = 0, 초과예약 0(reserved_qty가
planned_qty 정확 일치), DENIED 감사기록(piece DENIED + IF05_REQ/RES OVER) 전건 보존, OK/NG 정합, flake 0.
원자 조건부 ExecuteUpdate가 SQL Server rowversion 패자=미처리 500과 SQLite lost-update를 동시 해소하고
pre-OVER↔차감 TOCTOU를 닫음. 절대규칙 #7(재시도상수 0)·마이그레이션 무단생성 0 준수. 현장/운영 무접촉.
