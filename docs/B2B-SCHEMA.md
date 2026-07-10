# B2B-SCHEMA.md — B2B 스키마·서비스 알고리즘 캡처 (Generator 단일 근거)

> 작성: Planner Subagent (S-B2B-1) · 2026-07-08
> **목적**: 원본 프로젝트 `BowooTestBatchSystem_v2`(형제 폴더, 별도 repo)와 로컬 `TEST_ORDER_DB`는
> 후속 Generator·Evaluator의 접근 경계(pwd) **밖**이다. 그래서 이식에 필요한 **DDL 수준 실측 스키마**와
> **핵심 서비스 알고리즘**을 이 문서에 캡처한다. Generator는 **이 문서 + `docs/api-spec-ko.html`(와이어 계약·
> 실패 message 정본) + `docs/PROGRAM_STRUCTURE.md`(청사진)** 만으로 B2B-1을 구현할 수 있어야 한다.
> 원본 코드/DB는 참조 전용이었고 **수정/커밋하지 않았다**.

---

## 0. 실측 출처

- **DB 실측**: `sqlcmd -S localhost -d TEST_ORDER_DB -E` → `INFORMATION_SCHEMA.COLUMNS` / `sys.indexes` /
  `sys.foreign_keys` / `sys.check_constraints` / `sys.columns(is_identity)` 덤프. (아래 §1은 그 결과.)
- **엔티티/서비스**: 원본 `backend/Entities/*.cs`, `backend/Services/{WorkService,BoxService}.cs`,
  `backend/Dtos/*.cs`, `backend/Utils/{AppUtils,AppConstants}.cs`, `backend/Controllers/*.cs`,
  `backend/Middleware/*.cs`, `backend/Program.cs` 정독.
- **실측 CHECK 제약 = 0건**(원본 DB는 CHECK 없음). 검증은 전부 애플리케이션 DataAnnotations로 수행.

---

## 1. 테이블 스키마 (SQL Server 실측 DDL)

> 6개 테이블. **`auto_generate_config`는 제외**(자동생성 미이식 — 확정 방침).
> 모든 `id`는 `bigint IDENTITY` PK(클러스터드). 아래 타입은 SQL Server 실측치. SQLite 마이그레이션은
> EF Core provider가 대응 타입으로 생성(TEXT/INTEGER). 컬럼명은 원본이 **snake_case**(우리 프로젝트 컨벤션과 동일).

### 1.1 `test_data` — 등록된 테스트 데이터(마스터: bizDay·batch·barcode·chute 배치)
| 컬럼 | 타입 | NULL | 기본값 | 비고 |
|---|---|---|---|---|
| id | bigint IDENTITY | NO | | PK `PK_test_data` |
| biz_day | nvarchar(10) | NO | | 정규화 "YYYY-MM-DD"로 저장 |
| batch | nvarchar(10) | NO | | |
| barcode | nvarchar(50) | NO | | 유니크 아님(동일 바코드 다중 슈트 허용) |
| chute_no | nvarchar(10) | NO | | 3자리 zero-pad("001") |
| receive_time | datetime2 | YES | | unprocessed 조회 시 일괄 마킹(미작업=NULL) |
| created_at | datetime2 | NO | | (원본 `DateTime.Now`) |
| barcode2 | nvarchar(50) | YES | | Reject-Multi-Barcode 2번째 바코드(없으면 단일) |
- 인덱스: `IX_test_data_biz_day_batch(biz_day,batch)`, `IX_test_data_barcode(barcode)`, `IX_test_data_biz_day(biz_day)`

### 1.2 `test_log` — 투입/분류 로그 (LogType = INPUT | SORT)
| 컬럼 | 타입 | NULL | 비고 |
|---|---|---|---|
| id | bigint IDENTITY | NO | PK `PK_test_log` |
| log_type | nvarchar(10) | NO | "INPUT" 또는 "SORT" |
| biz_day | nvarchar(10) | NO | |
| batch | nvarchar(10) | NO | |
| barcode | nvarchar(50) | NO | |
| equipment_no | nvarchar(20) | YES | INPUT=inductionNo, SORT=chuteNo(3자리) |
| pid | nvarchar(50) | YES | RCS 부여 정수를 문자열로 저장(미검증) |
| status | nvarchar(5) | YES | "OK"/"NG" |
| reason | nvarchar(200) | YES | |
| log_time | datetime2 | YES | inTime/sortTime 파싱값(파싱 실패 시 now) |
| created_at | datetime2 | NO | |
| test_data_id | bigint | YES | test_data.id 참조(논리적 — **FK 제약 없음**), 처리행 매핑 |
- 인덱스: `IX_test_log_barcode`, `IX_test_log_log_type`, `IX_test_log_log_time`,
  `IX_test_log_biz_day_log_type_log_time(biz_day,log_type,log_time)`, `IX_test_log_test_data_id`
- ⚠ `test_data_id`는 인덱스만 있고 **DB FK 제약이 없다**(원본에도 없음). 우리 프로젝트도 FK 없이 이식(1785 회피·이력 불변).

### 1.3 `work_result` — 전체 작업 결과 (results 엔드포인트가 append)
| 컬럼 | 타입 | NULL | 비고 |
|---|---|---|---|
| id | bigint IDENTITY | NO | PK `PK_work_result` |
| biz_day | nvarchar(10) | NO | |
| batch | nvarchar(10) | NO | |
| barcode | nvarchar(50) | NO | |
| chute_no | nvarchar(20) | YES | 3자리 zero-pad |
| created_at | datetime2 | NO | |
- 인덱스: `IX_work_result_biz_day_batch(biz_day,batch)`

### 1.4 `box` — 박스 마감 헤더
| 컬럼 | 타입 | NULL | 비고 |
|---|---|---|---|
| id | bigint IDENTITY | NO | PK `PK_box` |
| biz_day | nvarchar(10) | NO | |
| batch | nvarchar(10) | NO | |
| box_no | nvarchar(50) | NO | |
| chute_no | nvarchar(10) | NO | 3자리 zero-pad |
| end_time | nvarchar(50) | YES | 클라이언트 문자열 그대로 저장 |
| created_at | datetime2 | NO | |
- 인덱스: `IX_box_biz_day_batch(biz_day,batch)`, **`IX_box_biz_day_batch_box_no(biz_day,batch,box_no) UNIQUE`**(재전송 방지)

### 1.5 `box_item` — 박스 내 품목 (box 1:N)
| 컬럼 | 타입 | NULL | 비고 |
|---|---|---|---|
| id | bigint IDENTITY | NO | PK `PK_box_item` |
| box_id | bigint | NO | **FK → box.id, ON DELETE CASCADE** (`FK_box_item_box_box_id`) |
| barcode | nvarchar(100) | NO | (주의: box_item.barcode는 100자, test_*는 50자) |
| qty | int | NO | 기본 1 |
- 인덱스: `IX_box_item_box_id(box_id)`
- **유일한 FK**(단일 캐스케이드 경로 → SQL Server 1785 위험 없음). 우리 프로젝트 ERD는 append-only+Restrict 지향이나
  box→box_item은 단일 경로라 CASCADE 안전. §7 결정 D3 참조.

### 1.6 `api_call_log` — RCS API 호출 원문 감사 로그
| 컬럼 | 타입 | NULL | 기본값 | 비고 |
|---|---|---|---|---|
| id | bigint IDENTITY | NO | | PK |
| endpoint | nvarchar(100) | NO | | 경로(예: "/api/v1/works/input") |
| http_method | nvarchar(10) | NO | | |
| request_body | nvarchar(max) | YES | | 마스킹 후 저장 |
| response_status | nvarchar(10) | YES | | "S"/"F" |
| response_body | nvarchar(max) | YES | | 4000자 truncate |
| http_status_code | int | NO | 0 | |
| duration_ms | bigint | NO | 0 | |
| client_ip | nvarchar(50) | YES | | |
| error_message | nvarchar(500) | YES | | |
| called_at | datetime2 | NO | getdate() | |
- 인덱스: `IX_api_call_log_called_at`, `IX_api_call_log_endpoint`

---

## 2. 엔티티 ↔ 컬럼 매핑 + JSON 직렬화

- C# 프로퍼티는 **PascalCase**, 컬럼은 **snake_case**(`[Column("...")]` 또는 `ToTable`/`HasColumnName`).
- API JSON은 **camelCase**(System.Text.Json). 예: `BizDay→bizDay`, `InductionNo→inductionNo`, `PId→pId`,
  `ChuteNo→chuteNo`, `BoxNo→boxNo`, `EndTime→endTime`, `InTime→inTime`, `SortTime→sortTime`, `Items→items`.
  → 계약 필드명 `pId·inductionNo·chuteNo·qty·bizDay·batch·barcode·status·reason` 와 정확히 일치.

원본 엔티티 프로퍼티(요약):
- `TestData`: Id, BizDay, Batch, Barcode, Barcode2?, ChuteNo, ReceiveTime?, CreatedAt
- `TestLog`: Id, LogType, BizDay, Batch, Barcode, EquipmentNo?, Pid?, Status?, Reason?, LogTime?, TestDataId?, CreatedAt
- `WorkResult`: Id, BizDay, Batch, Barcode, ChuteNo?, CreatedAt
- `Box`: Id, BizDay, Batch, BoxNo, ChuteNo, EndTime?, CreatedAt, Items(ICollection<BoxItem>)
- `BoxItem`: Id, BoxId, Box, Barcode, Qty(=1)
- `ApiCallLog`: Id, Endpoint, HttpMethod, RequestBody?, ResponseStatus?, ResponseBody?, HttpStatusCode, DurationMs, ClientIp?, ErrorMessage?, CalledAt

---

## 3. 와이어 계약 — 5개 RCS 엔드포인트

> 정본은 `docs/api-spec-ko.html`. 아래는 이식 대상 요약. 공통 응답: `{ "status":"S"|"F", "message":"..." }`.
> **비즈니스 실패 = HTTP 200 + status "F"**, **검증 실패 = HTTP 400**, **미처리 예외 = HTTP 500**.

### 3.1 `GET /api/v1/works/unprocessed?bizDay=...` — 미작업 조회 (부수효과 있음)
- **부수효과**: 조회된 미작업(receive_time IS NULL) 행에 `receive_time`을 **일괄 마킹**(= 수신 확인).
  **자동생성 트리거 없음**(auto_generate 미이식). **0건이면 빈 배열 `[]` 반환**(F 아님).
- 응답: **그룹 배열**. batch로 1차 그룹 → 그 안에서 (barcode, chuteNo)로 2차 그룹, `qty = COUNT`.
  ```json
  [ { "bizDay":"2026-07-08", "batch":"001",
      "items":[ { "barcode":"AB12", "chuteNo":"001", "qty":3 } ] } ]
  ```
- `bizDay` 쿼리 누락 → 400 `{status:F, message:"bizDay parameter is required."}`.
- 정렬: Batch → ChuteNo → Barcode.

### 3.2 `POST /api/v1/works/input` — 투입 로그 (INPUT)
- 요청: `bizDay, batch, inductionNo(int), chuteNo, pId(int), barcode?, status(OK|NG), reason?, inTime, qty(1~9999,기본1)`
- 로직: (barcode,bizDay,batch)로 test_data 후보 조회 → 이미 INPUT 로그 연결된 행 제외 →
  가용<qty면 **전량 거부**(부분 처리 없음) → `available.Take(qty)`만큼 test_log(INPUT) append.
- 성공 200 `{S, "Success"}`.

### 3.3 `POST /api/v1/works/classification` — 분류 로그 (SORT)
- 요청: `bizDay, batch, chuteNo, pId(int), barcode, sortTime, status(OK|NG), reason?, qty(1~9999,기본1)`
- 로직: (barcode,bizDay,batch) 후보 조회 → 요청 chuteNo(3자리 정규화) **일치 행만** 필터 →
  일치 0건이면 Chute mismatch F → 이미 SORT 처리된 행 제외 → 남은 게 0이면 "already fully classified" F →
  가용<qty면 전량 거부 → `Take(qty)`만큼 test_log(SORT) append.

### 3.4 `POST /api/v1/works/results` — 전체 작업 결과 (최상위 JSON 배열)
- 요청 본문은 **최상위 배열**: `[ { bizDay, batch, items:[ { barcode, chuteNo, qty(기본1) } ] } ]`
- 로직: (bizDay,batch)별 등록 barcode 집합을 미리 조회(HashSet 캐시, N+1 방지) →
  **사전 존재검증**: 비어있지 않은 barcode 중 하나라도 미등록이면 **전체 거부**(부분 INSERT 방지, 트랜잭션 진입 전) →
  `chuteNo`는 3자리 정규화(**chuteNo 자체는 미검증**) → item.qty만큼 work_result 반복 생성 → 트랜잭션 커밋.
- null/빈 배열 → F "No data to process." / 유효 item 0 → F "No valid data to process.".

### 3.5 `POST /api/v1/works/box` — 박스 마감
- 요청: `bizDay, batch, boxNo, chuteNo(1~10), items:[{barcode(1~100), qty(기본1)}], endTime?`
- 로직: bizDay 정규화 → chuteNo 3자리 정규화 → **(bizDay,batch,boxNo) 중복이면 F**(재전송 거부) →
  빈 barcode item 필터 → Box + BoxItems 트랜잭션 원자 저장. **barcode 미검증**(존재검증 없음).
- (참고) 골든패스 운영 순서는 `box → results`(코드가 강제하진 않음 — 운영 관례).

---

## 4. 실패 message 정본 (verbatim — 한 글자도 변경 금지)

> **정본 = `docs/api-spec-ko.html`**. 아래는 서비스 코드가 방출하는 문자열(§3.2~3.5 로직에 삽입).
> `{...}` 자리표시자는 런타임 값. Generator는 이 문자열을 **byte-for-byte** 복제하고 테스트로 고정한다.

**비즈니스 실패 (HTTP 200 + status "F")**
1. `Barcode not found, or bizDay/batch does not match the registered data.` — input; classification(후보 0)
2. `Not enough unprocessed rows: requested {N}, available {M}.` — input & classification
3. `Chute mismatch: barcode {barcode} expected chute(s) [{list}], received {chuteNo}.` — classification
4. `Barcode {barcode} in chute {chuteNo} has already been fully classified.` — classification
5. `No data to process.` — results(null/빈 배열)
6. `Barcode '{barcode}' not found, or bizDay/batch does not match the registered data.` — results(개별 barcode, **작은따옴표 포함**)
7. `No valid data to process.` — results(유효 item 0)
8. `Box already exists for the given bizDay/batch/boxNo.` — box(중복)
9. `Success` — 성공 message(status "S")

**검증 실패 (HTTP 400 — ModelState/DataAnnotations)**
10. `BizDay must be in YYYYMMDD or YYYY-MM-DD format.`
11. `Barcode may only contain letters, digits, hyphen, and underscore.`
12. `Status must be 'OK' or 'NG'.`
13. `The field Qty must be between 1 and 9999.` (DataAnnotations [Range] 기본형식)
14. `Items must contain at least one entry.` (results group / box)
15. `The {Field} field is required.` (DataAnnotations [Required] 기본형식)
16. `bizDay parameter is required.` (unprocessed GET 컨트롤러 체크)
17. `Invalid date: {value}` (NormalizeBizDay — 형식통과·존재하지않는 날짜, 예 20261332)
18. `Invalid request body.` (ModelState firstError 없음 fallback)

**예외 (HTTP 500)**
19. `Internal server error. (TraceId: {id})`

---

## 5. 유틸 의미

- **`NormalizeBizDay(bizDay)`**(AppUtils): 허용 입력 `YYYYMMDD` | `YYYY-MM-DD`. 빈 문자열은 그대로 반환.
  형식 통과하나 존재하지 않는 날짜(20261332) → `ArgumentException("Invalid date: ...")`. 성공 시 항상 `"YYYY-MM-DD"` 반환.
  저장·비교는 정규화 형태로 통일.
- **ChuteNo 정규화**: `int.TryParse` 성공 시 `ToString("D3")`(3자리 zero-pad), 실패 시 원문 유지.
  test_data·test_log(SORT)·work_result·box 전부 동일 규칙(비교 깨짐 방지).
- **상수**(AppConstants): `ChuteNoFormat="D3"`, `QtyMaxPerRequest=9999`, `LogTruncateDbLength=4000`.
- **pId·inductionNo**: RCS 자체생성 정수. 서버 **미검증**, `.ToString()`으로 문자열 컬럼(pid/equipment_no)에 그대로 저장.

---

## 6. 서비스 알고리즘 (순수 캡처 — 이식 시 유의점 포함)

### 6.1 `GetUnprocessedAsync` (부수효과 있는 GET)
1. `bizDay = NormalizeBizDay(bizDay)`.
2. `test_data` where `biz_day==bizDay && receive_time==null`, 정렬 Batch→ChuteNo→Barcode.
3. **[이식 변경]** 원본은 `data.Count==0`이면 `AutoGenerateAsync` 호출 → **B2B-1은 이 블록 삭제**. 0건이면 `[]` 반환.
   (auto_generate_config 미이식. 최근 문서 정비 결정 "0건=빈 배열"과 일치.)
4. 조회 행 전부 `receive_time = now` → SaveChanges. (원본은 auto-gen+마킹을 한 트랜잭션으로 감쌌으나
   auto-gen 제거 후에도 마킹 저장은 유지. 단순 SaveChanges 또는 트랜잭션 — Generator 판단, 원자성만 보장.)
5. 그룹핑: `GroupBy(Batch)` → 내부 `GroupBy(Barcode,ChuteNo)`, `qty=Count()`.

### 6.2 `ProcessInputAsync`
1. NormalizeBizDay. 2. `test_data` where (barcode,bizDay,batch). 없으면 F(msg#1).
3. 후보 id 중 이미 INPUT 로그(test_log where log_type=="INPUT" && test_data_id in 후보) 연결된 것 제외 → available.
4. `available.Count < qty` → F(msg#2). 5. logTime = TryParse(inTime) ?? now.
6. `available.Take(qty)` 각각 test_log(INPUT) 생성(equipment_no=inductionNo.ToString(), pid=pId.ToString()). SaveChanges. S.

### 6.3 `ProcessClassificationAsync`
1. NormalizeBizDay. 2. 후보 조회. 없으면 F(msg#1).
3. reqChuteNo = 3자리 정규화. matchedByChute = 후보 where chute_no==reqChuteNo.
4. matchedByChute 0건 → F(msg#3, validChutes=후보 chute_no distinct 정렬).
5. matched id 중 이미 SORT 처리 제외 → available. available 0 → F(msg#4).
6. `available.Count < qty` → F(msg#2). 7. sortTime=TryParse(sortTime)??now.
8. `available.Take(qty)` test_log(SORT) 생성(equipment_no=reqChuteNo). SaveChanges. S.

### 6.4 `ProcessResultsAsync`
1. null/빈 → F(msg#5). 2. groupKeys distinct (NormalizeBizDay 적용).
3. 키별 등록 barcode HashSet(Ordinal) 캐시.
4. 각 group의 item 중 비어있지 않은 barcode가 캐시에 없으면 즉시 F(msg#6) — **트랜잭션 진입 전 전체 거부**.
5. work_result 엔티티 생성: 빈 barcode skip, chuteNo 3자리 정규화, `item.qty`만큼 반복.
6. entities 0 → F(msg#7). 7. 트랜잭션으로 AddRange+SaveChanges+Commit. S.

### 6.5 `ProcessBoxAsync`
1. NormalizeBizDay. 2. chuteNo 3자리 정규화.
3. `Boxes.Any(biz_day&&batch&&box_no)` → 있으면 F(msg#8).
4. 빈 barcode item 필터 → BoxItem 목록. 5. Box(+Items) 트랜잭션 원자 저장. S.

---

## 7. 우리 프로젝트 통합 노트 & 충돌 실측 (Generator·Evaluator 필독)

우리 프로젝트는 **dual-provider EF Core**(`WcsDbContext` + `Wcs.Migrations.SqlServer`/`Wcs.Migrations.Sqlite`),
snake_case `ToTable`, `id` long `ValueGeneratedOnAdd`, 명명 인덱스(`HasDatabaseName`), FK는 SQL Server 1785 회피 위해
Restrict 기본, enum→string+MaxLength(CHECK 대체), created_at UTC(ERD 원칙). API는 MVC 컨트롤러 + Serilog + operation_log.

### 충돌/경계 실측
- **테이블명 충돌 없음**: 기존 17테이블(destination/cell/.../operation_log, work_batch, wcs_order)과
  B2B 6테이블(test_data/test_log/work_result/box/box_item/api_call_log)은 이름이 전부 다르다. ✅
- **라우트 충돌 없음**: 기존 `RcsController [Route("api/v1")]` 하위는 `destination-query`·`arrival-report`·
  `deposit-report`. B2B는 `api/v1/works/*` + (프론트용 `api/boxes` GET은 **B2B-1 범위 밖**, §8). 겹치지 않음. ✅
  단 Program.cs 말미 catch-all `app.Map("/api/{**rest}", NotFound)` 는 리터럴 컨트롤러 라우트보다 **낮은 우선순위**라
  B2B 컨트롤러가 먼저 매칭된다(무해).
- **`/health` 충돌**: 우리 Program.cs가 이미 `app.MapGet("/health", ...)`. 원본은 HealthChecks를 `/health`·
  `/health/ready`에 매핑 → **원본 HealthChecks 이식 금지**(우리 /health 유지). api_call_log 큐 헬스체크도 이식 제외.

### 인프라 이식 시 "무접촉" 제약 (기존 엔드포인트 동작 보존)
- **api_call_log 미들웨어(RcsApiLoggingMiddleware)**: 원본은 `/api/v1/` **접두 전체**를 잡는다 → 그대로 이식하면
  기존 `destination-query` 등도 api_call_log에 기록되어 **동작 변경**. → 이식 시 **경로를 `/api/v1/works/`로 한정**해야 함.
- **ModelState 400 형식**: 원본 Program.cs는 `ApiBehaviorOptions.InvalidModelStateResponseFactory`를 **전역**으로
  덮어 `ApiResponse.Fail(firstError)`(위 msg#10~15,18)를 낸다. 우리 프로젝트는 이 설정이 **없다**(기본 ProblemDetails).
  전역으로 바꾸면 기존 컨트롤러 400 형식이 바뀐다(동작 변경). → **경로 분기 팩토리** 권장: path가 `/api/v1/works/`로
  시작하면 `{status:F,message}` 형식, 그 외는 기존 기본 동작 유지. (§8 결정 D5.)
- **ArgumentException→400**: 원본은 전역 GlobalExceptionMiddleware가 처리. 우리는 없음. NormalizeBizDay가
  구조통과·비존재 날짜에 던지는 `ArgumentException`을 400으로 바꾸려면 → **B2B 컨트롤러/서비스 내 try/catch**로 국소 처리
  권장(전역 미들웨어 추가는 기존 동작 위험). (§8 결정 D5.)
- **Rate limiter / CORS / OpenAPI**: 원본 Program.cs에 있으나 **전부 전역** → 기존 엔드포인트 영향. **B2B-1 이식 제외 권장**
  (필요 시 후속). Rate limiter를 꼭 넣어야 하면 B2B 컨트롤러에 `[EnableRateLimiting]` 속성으로 국소 적용.

### 마이그레이션
- B2B 6엔티티를 **`WcsDbContext`에 DbSet+Configure 추가** → 각 provider 프로젝트에 **add-only 마이그레이션 1개씩**
  (`Wcs.Migrations.SqlServer`, `Wcs.Migrations.Sqlite`). **기존 테이블 ALTER 0**(신규 CreateTable만). ModelSnapshot에는
  B2B 테이블만 추가되어야 한다(기존 엔트리 diff 0 확인). 콜드스타트/기존 데이터 무영향.
- 테스트 더블: 통합 테스트는 in-memory SQLite + `EnsureCreated()`(기존 `FakeModbusWebApplicationFactory` 패턴) →
  마이그레이션 없이도 B2B 테이블 생성. 실 마이그레이션 up 검증은 §검증 시나리오 참조.

### DI 추가(최소)
- `IWorkService/WorkService`, `IBoxService/BoxService` (Scoped).
- api_call_log 이식 시: `ApiCallLogQueue`(Singleton) + 백그라운드 writer(HostedService) — **경로 한정 미들웨어**와 함께.
- ApiResponse·DTO·AppUtils·AppConstants는 **B2B 전용 네임스페이스**로 신규 이식(기존과 격리, 이름만 겹칠 경우 충돌 회피).

---

## 8. B2B-1 범위 밖(명시적 제외) — 후속 스프린트

- `auto_generate_config` 테이블·엔티티·AutoGenerate 로직·`AutoGenerateConfigService` — **미이식**(확정).
- 프론트 전용 조회 API: `TestDataController`(GET 관리), `LogController`(logs), `ResultComparison`(비교),
  `GET /api/boxes`(박스 목록), `LogExportService`(excel) — **B2B-2/3(프론트)와 함께**.
- 전역 인프라(rate limiter·CORS·OpenAPI·전역 예외 미들웨어·HealthChecks 확장) — 필요 시 후속, B2B-1 제외.
- 실 `TEST_ORDER_DB` 재적용/시드 대조 — **orchestrator/사용자 몫**(Generator는 우리 프로젝트 테스트 DB[SQLite 더블]만).
