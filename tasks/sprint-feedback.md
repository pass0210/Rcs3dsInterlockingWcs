# Sprint Feedback — S-B2B-1 (B2B 신규 스키마 + RCS 5개 API 이식)

**APPROVED** — Evaluator, 2026-07-08 (1 iteration to pass).

브랜치 `feat/b2b-1-schema-api`(읽기 전용 — 커밋/수정/브랜치전환 없음).
핸드오프 마커: `tasks/sprint-log.md:2398` `## IMPLEMENTATION COMPLETE — S-B2B-1`.
근거 문서: `docs/B2B-SCHEMA.md`(캡처 정본)·`docs/api-spec-ko.html`(§4 message 정본)·`tasks/sprint-contract.md`(사용자 확정 Q1/Q3/Q5).
프로젝트 타입: **Backend/API**. 검증 시나리오 1~7 전항 PASS(fresh evidence). 실 TEST_ORDER_DB 대조는 범위 밖(사용자/orchestrator).

---

## 검증 시나리오 결과 (fresh evidence)

### 1. 무접촉(최대 리스크) — PASS
- `git diff --numstat`: 수정 tracked 4파일 **전부 deleted=0**(순수 insertion):
  `Program.cs +42/-0` · `WcsDbContext.cs +163/-0` · `ModelSnapshot(SqlServer) +357/-0` · `ModelSnapshot(Sqlite) +345/-0`.
  deleted=0 = 기존 엔트리 재정렬/변경 0(재정렬은 delete+add로 나타남). ModelSnapshot 변경은 B2B 6엔티티 추가로만 국한.
- `git diff --name-only`: B2C 파일(Entities.cs·RcsController.cs·Repositories·DbSeeder·기존 Initial/AddOperationLog 마이그레이션) **0건 등장**.
  신규는 전부 untracked B2B 디렉터리(`Wcs.Data/B2B/`·`Wcs.Api/B2B/`·`Controllers/B2B/`·`Wcs.Tests/B2B/`·양 provider AddB2BTables).
- Program.cs/WcsDbContext 변경은 순수 append(DbSet 6·Configure 6·DI 4줄·400 factory 경로분기·middleware 1줄) — 기존 코드 무수정.
- 신규 마이그레이션 `Up()`: **6 CreateTable + 14 CreateIndex**, `AlterTable/AlterColumn/AddColumn/DropColumn/DropForeignKey/DropTable` **0건**(op grep 카운트 SqlServer 확인). 기존 17테이블 무접촉.

### 2. 양 provider 마이그레이션 — PASS
- **SQLite up 성공(end-to-end)**: `dotnet ef database update`로 임시 파일 DB에 3개 마이그레이션 순차 적용 →
  `Applying '..._Initial' → '..._AddOperationLog' → '..._AddB2BTables' → Done.`(exit 0).
- **양 provider DDL script**: `ef migrations script <AddOperationLog>..<AddB2BTables>`:
  - SqlServer: **6 CREATE TABLE · 0 ALTER/DROP** · `CONSTRAINT FK_box_item_box_box_id ... ON DELETE CASCADE` · `CREATE UNIQUE INDEX [IX_box_biz_day_batch_box_no]`.
  - SQLite: **6 CREATE TABLE · 0 ALTER** · 동일 CASCADE FK · `CREATE UNIQUE INDEX "IX_box_biz_day_batch_box_no"`.
    생성 테이블 = `test_data·test_log·work_result·box·box_item·api_call_log`(정확히 6개).
- 컬럼 타입 §1 실측 DDL 일치: `bigint IDENTITY(1,1)`·`nvarchar(N)`·`nvarchar(max)`(request/response_body)·`datetime2`.
- 통합 테스트는 EnsureCreated로 동일 모델 기동(B2B 6테이블), 32건 GREEN.

### 3. 5 API 계약 부합 — PASS (SQLite 더블 통합 테스트 직접 실행)
32건 B2B GREEN이 하위 항목 전수 커버:
- (a) unprocessed 부수효과(2회차 빈배열)·0건 `[]`·자동생성 없음 — `Unprocessed_MarksReceiveTime_SecondCallEmpty_AndZeroRowsEmptyArray`·`Unprocessed_ZeroRows_ReturnsEmptyArray_NoTrigger`.
- (b) input/classification qty 묶음·가용<qty 전량거부(INSERT 0) — `Input_AvailableLessThanQty_RejectsAll_NoInsert`·`Input_NotEnough_Returns200F_Message2`.
- (c) classification chute mismatch·이미분류 — `Classification_ChuteMismatch_*`·`Classification_AlreadyFullyClassified/Classified_*`.
- (d) results 최상위 JSON 배열·사전 존재검증 전체거부(부분 INSERT 0)·chuteNo 미검증 저장(정규화만) — `Results_TopLevelArray_UnregisteredBarcode_RejectsAll_Message6`·`Results_HappyPath_*_ChuteNotValidated`("88"→"088").
- (e) box (bizDay,batch,boxNo) 중복거부·barcode 미검증 — `Box_Duplicate*_Message8`(UNREG 바코드 1차 성공·정규화 후 동일키 재전송 F·INSERT 0).
- (f) pId·inductionNo 미검증 그대로 저장 — `Input_HappyPath_*_PidStoredUnverified`(pId=2100000000/2147480000 → pid 문자열, inductionNo → equipment_no).

### 4. 실패 message verbatim — PASS (18/19 구현·전수 byte-for-byte 일치, #19는 계약 Non-Goal)
- `FailMessages.cs` + DTO `ValidationRules` + 컨트롤러/`AppUtils` 방출 문자열 18종을 `docs/api-spec-ko.html`(§6 표) grep 대조 → **전부 byte-for-byte 일치**:
  #1 BarcodeNotFound · #2 NotEnoughRows · #3 ChuteMismatch · #4 AlreadyClassified · #5 NoData · #6 ResultBarcodeNotFound(작은따옴표 포함) ·
  #7 NoValidData · #8 BoxAlreadyExists · #9 Success · #10 BizDay형식 · #11 Barcode문자 · #12 Status · #13 Qty Range(DataAnnotation 기본형식) ·
  #14 Items · #16 bizDay required · #17 Invalid date · #18 Invalid request body(#15 Required는 프레임워크 기본형식).
- 테스트가 각 문자열을 정확히 assert(예 `"Chute mismatch: barcode BC-C expected chute(s) [005], received 001."`·`"Barcode 'BC-UNREG' not found, ..."`·`"Invalid date: 20261332"`).
- **[관찰 — 비차단] #19 `Internal server error. (TraceId: {id})` 미구현**: 원본은 전역 `GlobalExceptionMiddleware` 방출이나, 이는 계약 Non-Goals("전역 예외 미들웨어 — 필요 시 후속") + D5("전역 미들웨어 무조건 교체 금지")로 **명시 범위 밖**. #19는 "서비스 방출 문자열"이 아니며, HTTP 500 상태코드 자체는 프레임워크 기본으로 전달(컨트롤러는 ArgumentException만 catch, 그 외 예외 전파). 후속 스프린트에서 전역 핸들러 도입 시 #19 verbatim + 500-path 테스트 필요(아래 Minor 등재).

### 5. HTTP 코드 · 무접촉 400 — PASS
- 검증실패=400(`ApiResponse.Fail`): `Input_InvalidStatus/QtyOutOfRange/BadBizDayFormat_Returns400_Message12/13/10`·`Box_EmptyItems_Returns400_Message14`·`Unprocessed_MissingBizDay_Returns400_Message16`.
- ArgumentException(#17 비존재 날짜 20261332)=400: B2B 5컨트롤러 국소 try/catch → `Input_InvalidCalendarDate_Returns400_Message17`.
- 비즈니스 실패=200+F(#2/#3/#4/#5/#6/#8), 성공=200+S.
- **기존 엔드포인트 400 형식 불변**: `InvalidModelStateResponseFactory`가 `/api/v1/works/`만 `B2BApiResponse.Fail(firstError)`,
  그 외는 **캡처한 `builtInFactory(context)`에 그대로 위임**(ProblemDetails) — 코드 판독 확인. 기존 non-works 400 테스트(`VS2_If05_PIdOutOfRange_Returns400`·`If09_InvalidPId/ChuteNo_Returns400` — `/api/v1/destination-query`·`/api/v1/arrival-report`)가 210 GREEN에 포함 → 형식 보존 실증.
- 500 전역 팩토리/미들웨어 무조건 교체 없음(경로분기·국소 처리만).

### 6. api_call_log 경로 한정 — PASS
- 미들웨어가 `Request.Path.StartsWithSegments("/api/v1/works")`(세그먼트 경계 매칭 — `/api/v1/worksXxx` 오매칭 없음)로만 기록, 그 외 경로는 즉시 `_next` 통과(무영향).
- `ApiCallLog_RecordsWorksPath_ButNotExistingRcsEndpoint`: `/api/v1/works/box` 기록 O · `/api/v1/destination-query`(기존 RCS) 기록 X 확정(비동기 writer 폴링 5초 후 단언).
- `created_at`/`called_at` = `DateTime.Now`(로컬타임, Q3) — 엔티티·마이그레이션·Configure 주석에 "B2C UTC와 상이" 명기 확인.

### 7. 회귀 0 + baseline — PASS
- `dotnet build backend/Wcs.sln -p:NuGetAudit=false`: **경고 0개 · 오류 0개**(신규 경고 0).
- `dotnet test backend/Wcs.sln` **×2회**: 두 번 모두 `실패: 0, 통과: 242, 건너뜀: 0, 전체: 242`(exit 0).
- 필터 분할(귀속 확정): 비-B2B `실패:0 통과:210`(**회귀 0**) + B2B `실패:0 통과:32`(신규) = 242.
- **팩토리 격리**: `B2bWebApplicationFactory`는 INSTANCE-level `_dbName=B2bTest_{Guid}` + 앵커 연결(`Complete()` teardown) —
  기존 `FakeModbusWebApplicationFactory`의 static `_dbName` double-seed 충돌 회피(S-CLEANUP-FIELD 교훈 적용). 전체 병렬 스위트에서 무충돌 실증.

## 동시성·fail-safe 코드 판독 (사각 교훈 적용)
- **ApiCallLogQueue**: `Channel.CreateBounded`(cap 10000·`DropOldest`·`SingleReader=true`·`SingleWriter=false`) — 다중 요청 스레드 `TryWrite`(논블로킹·스레드 안전)·단일 백그라운드 리더. `Complete()`로 teardown 결정적 종료(channel-race 방어).
- **ApiCallLogBackgroundWriter**: 배치(1~100) SaveChanges. `OperationCanceledException` + 일반 `Exception` catch → **경고 로그 후 드롭**(기록 실패가 본 처리 안 막음·삼킴 아님). 종료 시 잔여 드레인.
- **미들웨어 fail-safe**: 논블로킹 enqueue만 — 응답 비지연. 예외 시 스트림 원복 후 재던짐(기본 처리 위임)·마스킹은 로그 기록 전용(컨트롤러엔 원본 전달·`EnableBuffering` 되감기).
- **서비스 트랜잭션**: results/box는 명시 트랜잭션(원자 AddRange+Commit). input/classification은 단일 SaveChanges 원자성. qty 그룹핑=`Count<qty` 선체크 후 `Take(qty)`.

## Minor (비차단 — 다음 스프린트 Generator 읽기)
- **[S-B2B-1] #19 500 verbatim body 미구현**: 전역 예외 미들웨어(Non-Goal) 도입 시 `Internal server error. (TraceId:{id})` verbatim + 강제예외→500 통합 테스트 추가. 현재 500 상태코드는 프레임워크 기본으로 전달됨.
- **[S-B2B-1] input/classification 동시성 TOCTOU(이론)**: 동일 barcode 동시 요청 시 test_log에 UNIQUE 제약이 없어(§1 실측=제약 0) 가용행 이중 소비 가능. **원본 동작과 동일**하고 B2B는 테스트데이터 수집 도구라 비차단. 필요 시 후속에서 낙관적 동시성/UNIQUE 검토.

---

**판정: APPROVED.** 검증 1~7 전항 fresh evidence PASS. 무접촉(deleted=0·B2C diff 0·마이그레이션 add-only)·양 provider up·5 API 계약·18/19 message verbatim(#19는 계약 Non-Goal)·경로 한정 400/api_call_log·회귀 0(210 불변)+신규 32=242 GREEN·빌드 경고 0 전부 충족.

## Code Review (4-Tier Step 4.5 — S-B2B-1)
- **[BLOCKING·fix iter2] #1 문자열 길이 검증 누락** — batch/reason/barcode/boxNo/chuteNo [StringLength] 부재. SQL Server(prod)에서 과대입력 400→500(계약 batch 1~10 위반), SQLite 더블 은폐(lesson sqlserver-migration-prod-provider 재현). → DataAnnotations 추가 + 400 검증 테스트. (fix 진행 중)
- **[fix iter2] #5 Enqueue fail-safe** — RcsApiLoggingMiddleware Enqueue/Mask try/catch로 감사 로깅 예외가 본 API 방해 못하게. (fix 진행 중)
- **[Minor·todo] #2 동시 중복 box → 500** — BoxService AnyAsync 후 INSERT TOCTOU, UNIQUE 인덱스가 무결성 백스톱이나 응답 200+F(#8) 아닌 500. 순차 dispatch 전제상 드묾. DbUpdateException→#8 매핑 고려.
- **[Minor·todo] #3 input/classification read→insert TOCTOU** — test_data_id UNIQUE 없음, 원본 동일·순차 dispatch 허용. 위험만 기록.
- **[Minor·todo] #6 malformed JSON 400 message** — 프레임워크 파싱 텍스트가 spec #18 대신 노출. RCS 정상 JSON이라 엣지.
- **[Minor·todo] #7 명시 트랜잭션 중복** — results/box의 BeginTransaction이 SaveChanges 단일 트랜잭션과 중복(무해, 의도 명시).
- **[Nit] ApiResponse.cs 파일명 vs B2BApiResponse 타입명 불일치 / Mask 정규식 공백처리·이스케이프 미마스킹(B2B 페이로드 무해) / AppUtils·AppConstants 일반명(네임스페이스 격리됨).**
- **[권고] SQL Server provider로 과대입력 400/500 케이스 검증 추가**(SQLite 더블이 길이류 결함 구조적 은폐) — #1 fix 시 반영.

---

## FIX ITER 2 재검증 (code-review BLOCKING #1 길이검증 + #5 fail-safe) — Evaluator, 2026-07-08

> 이전 APPROVED 보존. 본 섹션은 fix iteration 2 delta 재검증. 핸드오프: `tasks/sprint-log.md:2454` `## FIX ITER 2`.
> 변경은 신규 B2B 파일 3개(`Dtos.cs`·`RcsApiLoggingMiddleware.cs`·`B2bApiTests.cs`)만 — 기존 B2C 무접촉 유지.

### 1. #1 문자열 길이 검증 — 열거 필드 PASS · "원천 해소"는 **부분** (잔여 2필드)
- `Dtos.cs` `[StringLength]` 추가값이 `docs/B2B-SCHEMA.md §1` 컬럼 정의와 일치:
  Batch(전 4 DTO) `10,Min1` · Input/Classification Barcode `50` · ResultItem Barcode `50` · BoxItemDto Barcode `100` ·
  Input/Classification ChuteNo `20`(equipment_no nvarchar(20)) · Box BoxNo `50` · Box ChuteNo `10` · Reason `200`. ✅ 스키마 정합.
- 신규 3 테스트 실행 GREEN, 실제로 400+`ApiResponse{status:"F"}` 단정(코드 판독):
  `Input_BatchTooLong`(11자)·`Input_BarcodeTooLong`(51자 all-letter — 정규식 통과, StringLength만 발동해 길이경로 격리)·`Box_ItemBarcodeTooLong`(101자) → 전부 `HttpStatusCode.BadRequest` + `body.Status=="F"`.
- DataAnnotations가 **컨트롤러 진입 시** 차단(경로분기 `InvalidModelStateResponseFactory`→`ApiResponse.Fail(firstError)`) → DB 도달 전 400. SQLite 더블은 길이 미강제라 은폐하나 DataAnnotations는 provider-무관 차단. 기존 non-works 400/500(ProblemDetails) 형식 불변(builtInFactory 위임 · 210 baseline GREEN).
- **★ 잔여 동일-클래스 갭 2건(NEW FINDING — SQLite 테스트에 은폐·SQL Server 500 가능)**:
  - `ResultItem.ChuteNo`(Dtos.cs:142, StringLength 없음) → `work_result.chute_no nvarchar(20)`. `NormalizeChuteNo`는 int 파싱 실패(비숫자 또는 int 범위 초과 숫자)시 원문 유지 → 21자↑ chuteNo가 SQL Server에서 truncation 500.
  - `BoxRequest.EndTime`(Dtos.cs:171, StringLength 없음) → `box.end_time nvarchar(50)`. §3.5 "클라이언트 문자열 그대로 저장" · 형식/길이 검증 0 → 51자↑ endTime이 500.
  - 둘 다 #1 BLOCKING과 **동일 버그 클래스**(과길이 문자열→SQL Server 500, SQLite 은폐 — sqlserver-migration-prod-provider 교훈 해당). 열거 필드(barcode/batch/boxNo)만 닫혔고 이 tail 2필드는 미해소 → **"SQL Server 500 원천 해소"는 대부분이나 완전하지 않음**. 통제 필드(chute 번호·타임스탬프)라 현실 위험은 free-form barcode보다 낮으나 실재·비회귀. → **후속 fix 권고**(barcode 51자와 동형 테스트 2건 추가 + StringLength(20)/StringLength(50)).

### 2. #5 api_call_log Enqueue fail-safe — PASS
- `RcsApiLoggingMiddleware.Enqueue` 전체(Mask/ExtractStatus/Truncate/TryEnqueue)를 `try { … } catch { /* 삼킴 */ }`로 격리(코드 판독) → 감사 로깅 예외가 본 API 응답 경로를 절대 방해하지 않음. 관측 훅 fail-safe 원칙 준수.

### 3. 회귀 · 무접촉 — PASS
- 빌드 `경고 0 · 오류 0`. `dotnet test backend/Wcs.sln` **×10회**: 9회 `통과! 실패:0 통과:245`(210 baseline + B2B 35 = 245), 1회 `실패:1`(핸드셰이크 flake — 아래 4). B2B 35 = 서비스 16 + API 19(신규 길이 3 포함).
- `git diff --name-only`: 수정은 Program.cs·WcsDbContext·양 ModelSnapshot(전부 append) + tasks만. **핸드셰이크/PlcGateway/Core/Sim3ds/ScenarioTests/HandshakeResidueTests diff 0**(git 확인). B2B는 신규 파일 격리.

### 4. ★ S5 flake 귀속 판정 — **PRE-EXISTING(회귀 아님)** · APPROVED 유지
Generator는 `S5RSeqMismatchTests`(~1/8)로 지목했으나 fresh 관측 결과는 더 명확·더 강한 pre-existing 근거:
- **(a) 전체 스위트 ≥5회(실측 10회)**: 9 GREEN / 1 FAIL. 관측된 실패는 `HandshakeResidueTests.S5_ResidueClearNotReflected_TerminalTimeout_NoCWritten`(RUN 6) — Generator가 지목한 테스트와 **다름**. 실패가 핸드셰이크 S5 테스트군을 **roam**(고정 실패 아님) = 로직 회귀가 아닌 타이밍 경합 특성.
- **(b) 단독 반복**: `S5RSeqMismatch` 단독 8/8 GREEN(결정적). 단일 테스트 격리 시 항상 통과.
- **(c) 결정적 귀속(stash보다 강함)**: 핸드셰이크 S5 테스트군만 필터(`HandshakeResidueTests|S5RSeqMismatch`, **B2B 0개 로드**) ×8 → 1회 flake 재현(run 3). 즉 **B2B 코드 없이 핸드셰이크 테스트들만으로도 상호 병렬 경합으로 flake 발현** → B2B와 인과 无. (git stash 미실시 — 이 재현이 더 결정적·untracked Generator 산출물 보존이 안전.)
- **근본**: 실 Sim 소켓·타이밍 민감 핸드셰이크 테스트의 xUnit 기본 병렬 실행 시 CPU/소켓 경합(문서화 flake 클래스: s9-flake-under-e2e-load · e2e-parallel-load-surfaces-integration-flakes). B2B는 (i) 핸드셰이크 코드 0줄 접촉, (ii) api_call_log 미들웨어가 non-works 즉시 통과(S5 요청 경로 런타임 ~0 추가), (iii) SQLite 경량 통합/단위 테스트라 병렬 총부하만 미미 증가. **B2B 유발 회귀 아님 · 수정 대상 아님 → 별도 이관(todo)**.

### 판정
FIX ITER 2 delta 재검증: **#1 열거 필드 PASS · #5 PASS · 회귀 0 · S5 = pre-existing(회귀 아님)** → **이전 APPROVED 유지**.
단 **완전 "SQL Server 500 원천 해소"는 미달** — 잔여 2필드(`ResultItem.ChuteNo`→nvarchar(20)·`BoxRequest.EndTime`→nvarchar(50)) 동일-클래스 갭을 후속 fix로 닫을 것을 권고(coordinator 스코프 결정). 비회귀·통제필드라 APPROVED 반전은 아님.

### Minor (추가 등재)
- **[S-B2B-1] #1 잔여 SQL Server 500 갭 2필드**: `ResultItem.ChuteNo` `[StringLength(20)]`·`BoxRequest.EndTime` `[StringLength(50)]` 추가 + 과길이 400 테스트 2건. SQLite 은폐 클래스.
