# Sprint Feedback — S-B2B-2a (test-data 관리 API + 기록 아카이브[archived_at 소프트삭제], 백엔드)

**APPROVED** — Evaluator, 2026-07-08 (1 iteration to pass).

브랜치 `feat/b2b-2-datagen-toggle`(읽기 전용 — 커밋/수정/브랜치전환/fix 없음).
핸드오프 마커: `tasks/sprint-log.md:2535` `## IMPLEMENTATION COMPLETE (B2B-2a)`.
근거 문서: `docs/B2B-DATAGEN.md`(§1~3 캡처 정본)·`tasks/sprint-contract.md`(S-B2B-2a + 사용자 확정 Q1~Q4 블록).
프로젝트 타입: **Backend/API**(2a는 백엔드 단독 — 프론트 2b는 별도 스프린트). 검증 시나리오 백엔드 1~4 전항 PASS(fresh evidence).
실 `TEST_ORDER_DB`·원본 `BowooTestBatchSystem_v2` 대조는 범위 밖(pwd 경계).

---

## 검증 시나리오 결과 (fresh evidence)

### 1. ★ 아카이브 하드삭제 0 (핵심) — PASS
- **코드 판독(grep `RemoveRange|.Remove(|ExecuteDelete|DELETE` on TestDataService.cs)**: 실호출은 **L289 `_db.B2bTestData.RemoveRange(entities)` 단 1건**(등록 원장·delete 전용). test_log·work_result 에는 하드삭제 호출 0 — L318 `l.ArchivedAt = now`(UPDATE), L16은 주석. → 아카이브는 archived_at UPDATE만.
- **서비스 단위 테스트 3건 직접 실행 GREEN**으로 (a)~(e) 전수 단정:
  - `Reset_ArchivesAssociatedLogsAndResults_NoHardDelete`: reset 후 (a) `B2bTestLogs.Count()==2`·`WorkResults.Count()==1` **COUNT 불변**(하드삭제 0) (b) 모든 로그·결과 `ArchivedAt != null` (c) `GetDetailAsync(..., ArchivedOnly)` 노출(InputStatus="OK") (d) `Active` 미노출(InputStatus/SortStatus null) + test_data 행 유지·ReceiveTime=null. SORT 로그는 TestDataId=null(barcode 폴백)인데도 (BizDay,Batch,Barcode) 키 매칭으로 정상 아카이브됨.
  - `Delete_HardDeletesTestData_ButArchivesLogsAndResults`: delete 후 `B2bTestData.Count()==0`(등록 원장 하드삭제) + 로그/결과 `Count()==1` 잔존·`ArchivedAt != null`.
  - `Delete_ScopeLimited_OtherBatchSameBarcode_Untouched`: 배치 001 동일 barcode `BC-X` 삭제 시 **배치 002의 동일 barcode 로그·결과 `ArchivedAt == null`(미영향)** — 원본 barcode-only 광범위 삭제 결함 미재현.
- **API 통합 테스트 2건 GREEN**: `Reset_Api_ArchivesLogs_NoHardDelete`(실 HTTP `POST /reset` → DB 로그/결과 archived·잔존 + `detail?archived=active` inputStatus=null · `archived=archivedOnly` inputStatus="OK") · `Delete_Api_HardDeletesTestData_ArchivesLogs`(실 HTTP `DELETE /api/test-data` → test_data 하드삭제·로그/결과 archived 잔존).
- **범위 정확성 판독**: `ArchiveAssociatedAsync` 스코프 키 = `HashSet<(BizDay,Batch,Barcode)>`(값 튜플 — 문자열 구분자 충돌 원천 회피). test_log 대상 = `TestDataId in ids` OR `(BizDay,Batch,Barcode) in keys`(둘 다 archived_at==null 만), work_result 대상 = `(BizDay,Batch,Barcode) in keys`. 다른 배치의 동일 barcode 는 TestDataId·키 튜플 모두 불일치로 정확히 제외.

### 2. archived_at 마이그레이션 (양 provider add-only) — PASS
- **SqlServer `ef migrations script AddB2BTables AddB2BArchivedAt`**(fresh): 정확히
  `ALTER TABLE [work_result] ADD [archived_at] datetime2 NULL;` · `ALTER TABLE [test_log] ADD [archived_at] datetime2 NULL;` — **DROP/CREATE TABLE/기타 ALTER 0**.
- **SQLite `ef migrations script`**(fresh): `ALTER TABLE "work_result" ADD "archived_at" TEXT NULL;` · `ALTER TABLE "test_log" ADD "archived_at" TEXT NULL;` — 동일 add-only 2건.
- **SQLite 콜드스타트 end-to-end**: 임시 파일 DB에 전체 체인 `dotnet ef database update AddB2BArchivedAt` →
  `Applying 'Initial' → 'AddOperationLog' → 'AddB2BTables' → 'AddB2BArchivedAt' → Done.`(exit 0).
  스키마 실측(python sqlite3): 총 26테이블 중 **`archived_at` 보유 테이블 = 정확히 {test_log, work_result}**, `test_data`·`box`·`box_item`·`api_call_log` 미보유.
- **ModelSnapshot diff(양 provider)**: 각 `+4/-0`(deleted=0) = `ArchivedAt` 프로퍼티 블록 2개(test_log·work_result)만 삽입, **기존 엔트리 재정렬/삭제 0**. 마이그레이션 `Down()`은 대칭 `DropColumn` 2건(롤백 add-only 반전).

### 3. 관리 API 계약 — PASS (SQLite 더블 통합 테스트 직접 실행)
- **generate**: 라운드로빈 `chuteNos[i % chuteNos.Count]` + `ChuteNo.ToString("D3")`(zero-pad) + `NormalizeBizDay`(YYYY-MM-DD). `Generate_RoundRobin_*`(슈트 "1-2"×5 → 001×3·002×2)·`Generate_RangeAndSingles_Parsed_Deduped_Sorted`("1-3,5,5,2" → {1,2,3,5})·API `Generate_RoundTrip_Summary_Detail`(정규화 "2026-07-20"·count 4·001×2/002×2). BarcodeCount 상한 10000(`AppConstants.BarcodeCountMax` — RCS qty 9999와 별개 상수 확인).
- **upload**: ClosedXML `XLWorkbook`. 헤더 자동감지(`IsDateLike`) + 5컬럼 신양식(col5=chute, col4=barcode2)/4컬럼 구양식(col4=chute, barcode2=null) 판별 + barcode 빈행 skip. 3중 검증 400(파일없음/0바이트·10MB초과·확장자·MIME 화이트리스트). `Upload_NewFormat5Col_*`·`Upload_OldFormat4Col_Headerless_*`·`Upload_EmptyBarcodeRowsSkipped_*`·`Upload_EmptyWorkbook_*` + API `Upload_HappyPath/EmptyFile/WrongExtension/WrongMime`.
- **summary**: `GroupBy(BizDay,Batch)` + Count + `Max(ReceiveTime)` + BizDay/Batch desc(Ordinal). `Summary_GroupsByBatch_CountAndMaxReceiveTime_OrderedDesc`.
- **detail**: 로그 LEFT-JOIN(TestDataId 우선·barcode 폴백) + ChuteNo **숫자정렬**(int 파싱 우선·실패 int.MaxValue) + 아카이브 필터 `active|all|archivedOnly`. `Detail_SortsAndMapsInputSortLogs`.
- **DTO [StringLength]**: `Batch StringLength(10,Min1)`·`ChuteNos StringLength(200)`·`ChuteNos RegularExpression(^[\d\s,\-]+$)`·`BizDay RegularExpression`(길이 8/10 로 바운드)·`BarcodeCount Range(1,10000)`. 과길이/범위위반 → ModelState → **allowlist(/api/test-data) → B2BApiResponse.Fail** 400. `Generate_BarcodeCountOutOfRange_400_AllowlistFail`("BarcodeCount must be between 1 and 10000.")·`Generate_BadBizDayFormat_400_AllowlistFail` 실증.
- **ApiResponse 형식**: `B2BApiResponse{status,message}` camelCase. 조회는 원시 배열(camelCase).

### 4. 무접촉 — PASS
- `git status`/`git diff HEAD`: 수정 tracked = `AppConstants.cs(+10/-0)`·`AppUtils.cs(+49/-0)`·`FailMessages.cs(+30/-0)`·`EntitiesB2B.cs(+8/-0)`·`WcsDbContext.cs(+4/-0)`·`Program.cs(+7/-2)`·`Wcs.Api.csproj(+2/-0)`·양 ModelSnapshot(+4/-0). 나머지 전량 **신규 untracked**(TestDataDtos·TestDataService·Controllers/B2B/TestDataController·마이그레이션 4·테스트 2).
- **B2C 17테이블 `Entities.cs`·`RcsController`·기존 컨트롤러/라우트·B2B-1 RCS 5 API(`WorksControllers.cs`)·`WorkService.cs`(RCS)·기존 마이그레이션(Initial/AddOperationLog/AddB2BTables) = diff 0**(수정 목록에 미등장).
- **Program.cs allowlist = additive**: `InvalidModelStateResponseFactory`가 works 접두 `||` test-data 접두 OR 매칭 → B2BApiResponse.Fail. **그 외(B2C monitor/ops) 경로는 `builtInFactory(context)`(ProblemDetails)로 그대로 위임**(fall-through 보존, L80). works 분기·B2C 400 형식 불변.
- **WcsDbContext**: test_log·work_result Configure 에 `ArchivedAt.HasColumnName("archived_at").IsRequired(false)` 2줄 append만 — 기존 컬럼·인덱스 매핑 무변경.
- **ModelSnapshot add-only**(§2 재확인): archived_at 2 프로퍼티만.

### 5. 회귀 · baseline — PASS
- `dotnet build backend/Wcs.sln -p:NuGetAudit=false`: **경고 0개 · 오류 0개**(신규 경고 0 — ClosedXML 0.104.2 컴파일 경고 유발 없음).
- `dotnet test backend/Wcs.sln` **×2회**: 두 번 모두 `통과! 실패: 0, 통과: 270, 건너뜀: 0, 전체: 270`(13s, exit 0) — **결정적**.
- 필터 분할: `--filter FullyQualifiedName~TestData` = `통과: 23`(신규). 270 − 23 = **247 baseline 불변**(계약 명시 247과 일치·회귀 0).
- 오펀 프로세스 0(Wcs.Api/Wcs.Sim3ds/testhost none)·포트 5080/1502 free(검증 후 정리). 임시 SQLite 콜드스타트 DB 삭제.

## 동시성·fail-safe 코드 판독 (사각 교훈 적용)
- **reset/delete 트랜잭션 원자성**: `ArchiveAssociatedAsync`는 추적 엔티티 변경만(자체 SaveChanges 없음). reset·delete 각각 **단일 `SaveChangesAsync`** → EF 기본 트랜잭션으로 (로그/결과 archived_at UPDATE + test_data ReceiveTime null 또는 RemoveRange)가 원자 커밋. delete 는 아카이브 마킹을 RemoveRange 전에 수행(entities 키 필요) — 순서 정당.
- **아카이브 범위 쿼리 정확성**: DB 로드는 `barcodes.Contains(l.Barcode) || (TestDataId in ids)` 광범위, 이후 in-memory `matchById || keys.Contains((BizDay,Batch,Barcode))` 정밀 필터. 값-튜플 집합이라 구분자 충돌 없음. 다른 배치 동일 barcode 는 TestDataId(타 배치 소속)·키 튜플 모두 불일치 → 정확 제외(테스트 실증).
- **엑셀 스트림/크기 처리**: 컨트롤러가 `file.Length`(멀티파트 버퍼링 기지값)로 10MB 선검사 → 초과 400. `RequestSizeLimit(정확10MB)`는 멀티파트 오버헤드로 유효 10MB 파일을 413 선점하므로 의도적 미도입(주석 문서화), Kestrel 기본 ~28MB 하드백스톱. 서비스는 `using XLWorkbook(stream)` + 전체 try/catch → 파싱 예외 `Excel parsing error: {msg}` F.

## Minor (비차단 — 다음 스프린트 Generator 읽기)
- **[S-B2B-2a] Batch/ChuteNos 과길이 전용 400 테스트 부재**: `[StringLength(10)]`·`[StringLength(200)]` 속성은 존재하고 allowlist→400 경로는 BarcodeCount Range·BizDay 형식 테스트로 이미 실증됨(동일 ModelState 경로). 과길이 입력 자체를 직접 단정하는 테스트는 없음 — S-B2B-1 code-review 잔여 갭(ResultItem.ChuteNo·BoxRequest.EndTime)과 함께 후속에서 동형 테스트 추가 권고. (BizDay 는 StringLength 없으나 RegularExpression 이 길이 8/10 로 바운드 — SQL Server truncation 위험 0.)
- **[S-B2B-2a] GenerateBarcode 랜덤 상한**: `Random.Shared.Next(1000, 9999)` 는 1000~9998(9999 미포함, 4자리 유지). 유니크 제약 없음(원본 동일·테스트 도구라 비차단). 대량 동일-ms 생성 시 이론적 충돌 가능하나 test_data.barcode 는 유니크 아님(다중 슈트 허용 설계).

---

**판정: APPROVED.** 백엔드 검증 1~4 전항 fresh evidence PASS.
아카이브 하드삭제 0(RemoveRange=test_data 1건뿐·로그/결과 archived_at UPDATE만·COUNT 불변·스코프 한정 배치 밖 미영향)·양 provider add-only 마이그레이션(콜드스타트 스키마 archived_at∈{test_log,work_result}만·ModelSnapshot +4/-0)·6 관리 API 계약(라운드로빈 D3·ClosedXML 신구양식·3중검증 400·summary/detail 필터·숫자정렬)·무접촉(B2C·B2B-1 계약 diff 0·Program allowlist additive·builtInFactory fall-through 보존)·회귀 0(247 baseline 불변 + 신규 23 = 270 GREEN ×2)·빌드 경고 0 전부 충족.

## Code Review (4-Tier Step 4.5 — S-B2B-2a)
- **[Important·처리] #1 바코드 채번 중복** — 대량 생성 시 BC{ts}{rand4} 충돌 거의 확정. 사용자 확정=원본 중복 허용(수용) → docs/B2B-DATAGEN.md §2.1 수용 기록. (유일성 필요 시 배치 내 단조 카운터 후속.)
- **[Important·fix] #2 엑셀 zip-bomb** — 10MB=압축크기, 팽창 OOM 가능 → 파싱 직후 행(100k)·열(64) 상한 가드 조기 F(AppConstants 상수). 테스트 1건.
- **[Minor·todo] 대량 IN 절**(reset/delete/archive 최대 10000 파라미터 — EF9 OPENJSON/SQLite 인라인이라 실무 안전) / **detail Map O(행×로그)**(초대형 배치 메모리) / **동시 중복 delete→500**(단일 사용자 도구 저확률) / **MIME 화이트리스트에 octet-stream**(확장자+실파싱 백스톱) / **ValidationRules 이원화**(중복 아님·위치 분산). 전부 폐쇄망·원본 동일 맥락 수용.
