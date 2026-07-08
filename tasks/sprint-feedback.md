# Sprint Feedback — S-B2B-3a (조회 백엔드 API 6종: 로그·API호출이력·Excel·3-way 비교·박스 + 아카이브 필터)

**APPROVED** — Evaluator, 2026-07-08 (1 iteration to pass).

브랜치 `feat/b2b-3a-query-backend`(read-only 검증 — 커밋/수정/브랜치전환/fix 0). Evaluator 는 코드를 고치지 않음.
핸드오프 마커: `tasks/sprint-log.md:2737` `## IMPLEMENTATION COMPLETE — S-B2B-3a`(존재 확인).
Ground truth: `git status`/`git diff develop` + 실 `dotnet test` 출력 + 코드 직접 판독(Generator 요약 미신뢰).
프로젝트 타입: **Full-stack**(변경 표면은 백엔드 조회 API에 한정 — 프론트 0, 계약 명시대로 Web/UI 시나리오 N/A).
Stale/재전송 점검: 작업은 working tree 에 미커밋(M/??), 마커·로그 엔트리 fresh(2026-07-08), 브랜치 base=develop tip(efc6478). 재전송 아님 — 신규 작업.

---

## 정적·회귀 게이트 (fresh, 독립 실행) — PASS

    $ dotnet test backend/Wcs.sln --nologo
    → 통과!  - 실패:0, 통과:286, 건너뜀:0, 전체:286, 기간: 13 s (exit 0)

- **286/286 GREEN**(이전 271 + 신규 15, 회귀 0). Generator 보고 수치와 독립 재현 일치.
- `dotnet build`: 신규 컴파일러 경고 0. NU1903(SQLitePCLRaw.lib.e_sqlite3 2.1.10 transitive 취약성) 8건은 **선재 부채**(EF Sqlite 가 항상 끌어옴·본 스프린트 무관·코드 경고 아님·todo 격리) — 계약이 스코프 밖으로 명시.

## 무접촉 실증 (git diff develop) — PASS

- `git diff --numstat develop -- backend/`: 4개 기존 파일 **전부 삽입-only(삭제 0)**:
  `AppConstants.cs` 3/0 · `BoxService.cs` 27/0 · `Controllers/B2B/TestDataController.cs` 19/0 · `Program.cs` 4/0.
- 신규 파일 6개(전부 B2B 스코프): `B2B/QueryDtos.cs`·`B2B/LogService.cs`·`B2B/LogExportService.cs`·`Controllers/B2B/LogController.cs`·`Controllers/B2B/BoxesController.cs`·`tests/Wcs.Tests/B2B/QueryApiTests.cs`.
- **신규 마이그레이션 0**(`git status | grep migrat` 0건) · **ModelSnapshot 0**(`grep snapshot` 0건) — 스키마 변경 없음(읽기 전용) 실증.
- 기존 B2C 코드·B2B-1/2 서비스 무접촉: `BoxService.ProcessBoxAsync` 본문 무변경(같은 파일에 `GetBoxesAsync` additive) · `TestDataService`(ParseArchiveFilter/reset/delete) diff 0 · `TestDataController` 생성자 무변경(E5 를 **`[FromServices]` 메서드 주입**으로 얹어 생성자 계약 보존) · `Program.cs` = AddScoped 2줄 append 뿐(기존 배선 무접촉) · appsettings/RcsController/WorkService/엔티티 diff 0.

## SQL Server provider-중립 (코드 판독 — SQLite GREEN ≠ SQL Server valid) — PASS

- smell grep `strftime|EF.Functions.(Like|DateDiff)|FromSqlRaw|ExecuteSql|.Sqlite` → B2B 서비스/컨트롤러 **0건**.
- LINQ 형태: E1/E2 파생필드 = `.Select` 내 상관 서브쿼리 `FirstOrDefault()`(EF Core→SQL Server OUTER APPLY·양 provider 지원) · E3 = `Take(500)`(TOP/LIMIT) · E6 = 컬렉션 프로젝션(split query, 중립) · **E4/E5 는 `ToListAsync` 후 LINQ-to-objects**(완전 중립). provider-특이 API 미의존.

---

## 검증 시나리오 결과 (Backend/API — fresh HTTP 왕복, xUnit QueryApiTests 15건)

### E1 투입 로그 — PASS
- `E1_InputLogs_RawArray_WithDerivedChuteAndReceiveTime`: camelCase 원시 배열, `equipmentNo`=inductionNo, 파생 `chuteNo`="007"·`receiveTime` 채워짐, `archivedAt`=null.
- `E1_ZeroRows_EmptyArray`: 미존재 bizDay → `[]`(길이 0). `E1_InvalidCalendarDate_400_Message17`: `20261332` → 400 + `{status:F, message:"Invalid date: 20261332"}`(#17 국소 catch).

### E2 분류 로그 — PASS
- `E2_SortLogs_RawArray_ChuteInEquipmentNo`: SORT 행 `equipmentNo`="005"(chuteNo)로 계약 형태 확인.

### ★ 아카이브 필터 3상태 + DB COUNT 불변 — PASS
- `E1_ArchiveFilter_ThreeStates_CountInvariant`: 활성1+아카이브1 시드 후 `active`=1(제외)·`archivedOnly`=1(그 행만)·`all`=2(전부)·`bogus`→active=1(미인식 폴백). **DB `B2bTestLogs.Count()==2` 불변**(소프트삭제·하드삭제 0) 직접 단정.

### E3 API 호출 이력 — PASS
- `E3_ApiCalls_DateFilter_And_500Cap`: 대상일 505 + 타일 3 시드 → **정확히 500 반환**(`AppConstants.ApiCallLogMaxItems`, 하드코딩 0) + 날짜 필터로 대상 endpoint 만.

### E4 Excel export — PASS
- `E4_Export_Phase1_Duration_FloorMapping`: 유효 xlsx(ClosedXML 재파싱)·Content-Disposition `input_sort_logs`·미디어타입 spreadsheetml. Phase1(TestDataId 1:1) 슈트="010"·인덕션 1→**"2층"**·소요="5.0".
- `E4_Export_Phase2_Fallback_And_UnmatchedBlank`: Phase2 폴백((Batch,Barcode) zip) 슈트="020"·인덕션 3→**"1층"**·소요="3.0" / 미매칭 INPUT(인덕션 5)→층·슈트·소요 **전부 공백**.
- `E4_Export_MissingBizDay_400`: bizDay 누락 → 400 `"bizDay parameter is required."`.

### E5 3-way 비교 — PASS
- `E5_Comparison_Match_Mismatch_Missing`: 일치(isMatch·sortChuteNo==resultChuteNo)·불일치(3자 존재·슈트 다름→isMatch false·isMissing false)·누락(INPUT 만→isMissing·hasSort/hasResult false).
- **★ `E5_Comparison_BatchIncludedKey_NegativeControl`(음성 대조)**: 같은 bizDay·같은 barcode·다른 batch → B2 는 B1 로그를 **오매칭 안 함**(hasInput/hasSort/hasResult 전부 false·isMissing) + 양성 대조 B1 isMatch true. 이월 Barcode-only 결함 미재현 입증.
- `E5_Comparison_ArchiveFilter_ExcludesArchivedLogs`: archived 로그 → active 뷰 hasInput=false / archivedOnly 뷰 hasInput=true(셀 단위 아카이브 소비).

### E6 박스 목록 — PASS
- `E6_Boxes_RoundTrip_WithItems`: `POST /api/v1/works/box`(쓰기경로) 적재 → `GET /api/boxes`(읽기경로) 되읽기. bizDay 정규화("2026-08-11")·chuteNo 3자리("003")·endTime·items 2건(barcode·qty).
- `E6_Boxes_MissingBizDay_400`(400) · `E6_Boxes_ZeroRows_EmptyArray`(미존재 bizDay→`[]`).

### End-to-end 크로스레이어(쓰기→조회→아카이브→필터) — PASS
- E6 라운드트립(POST box → GET boxes) + E1/E5 아카이브 필터 3상태 + DB COUNT 불변이 쓰기경로↔읽기경로↔소프트삭제 생명주기를 컨트롤러→서비스→WcsDbContext→DB 로 횡단 검증.

---

## Craft / 공용화 점검 — PASS

- `ParseArchiveFilter` 는 `TestDataService.cs:62` **단일 정의**, LogController(2)·TestDataController·Boxes 가 static 재사용(중복 0·계약 "중복 금지" 준수). `ArchiveFilter` enum 단일 소스. `FailMessages.BizDayParameterRequired` 재사용(기존 WorksControllers 와 동일 상수).
- 신규 쓰기 DTO 0(읽기 전용) → `[StringLength]` 과잉 부여 0(계약 Craft 기준). E4/E6 bizDay 필수 누락 400·비존재 날짜 400·export 오류 400+Fail 모두 계약대로.

## Minor (비차단 — 참고, APPROVED 무관)

1. **E1/E2 파생 슈트·수신시각을 서브쿼리 2개로 뽑아 OUTER APPLY 2회**: 기능 정확·데이터 소규모라 무해하나, 한 서브쿼리에서 익명형 프로젝션으로 1회 APPLY 로 합칠 여지(경미 성능). 후속 참고.
2. **barcode 중복 시 파생 슈트 = 무순서 `FirstOrDefault()`(임의 1행)**: 원본 §3.2.6 "Barcode 단독" 동작 보존이 의도(DTO 주석 명시)이며 결함 아님. 다만 결정성이 필요해지면 OrderBy 명시 필요 — B2B-3b 프론트 표시 요구에 따라 재검토.

## 종합 판정

**Overall PASS — APPROVED.** 계약 6개 완료조건 전항 충족: (1) 286 GREEN·빌드 에러 0 (2) 6 엔드포인트 실 HTTP 계약 형태(원시 camelCase 배열/xlsx 바이너리/0건 []) (3) 아카이브 3상태 + DB COUNT 불변(하드삭제 0) (4) 3-way 일치/불일치/누락 + Batch 음성 대조 (5) Excel 유효 xlsx·Phase1/Phase2·소요시간·층매핑 (6) 무접촉(B2C·B2B-1/2·Program.cs 기존 배선·양 provider 마이그레이션·ModelSnapshot diff 0·신규 마이그레이션 0). provider-중립 코드 판독 통과. Minor 2건 비차단.

---

## FIX ITER 1 재검증 (4-Tier code-review #1·#4·#5) — PASS · APPROVED 유지

Evaluator delta 재검증, 2026-07-08 (read-only). fix 는 untracked 신규 파일 2개(`LogService.cs`·`LogController.cs`)에만 반영 — tracked 파일 numstat 불변(3/0·27/0·19/0·4/0), migration/snapshot 0. 핸드오프: `sprint-log.md` `## FIX ITER 1`.

### 재실행 (fresh)
- `dotnet test backend/Wcs.sln` → **288 통과 / 0 실패 / 0 건너뜀**(exit 0, 이전 286 + 신규 2). QueryApiTests 단독 → **17/17 GREEN**(3s). 빌드 신규 경고 0.
- SQLite-only smell grep(B2B) → **0건**(provider-중립 유지).

### #1 (LogService E1/E2 파생필드 Frankenstein 제거) — PASS
- 두 독립 무순서 상관 서브쿼리 → **단일 결정적 서브쿼리**(`OrderBy(d=>d.Id).Select(d=>new{ChuteNo,ReceiveTime}).FirstOrDefault()`)로 병합. 두 파생필드가 **동일 최소-Id 행**에서 취득 → 단일 OUTER APPLY(SQL Server)/단일 상관 서브쿼리(SQLite), 비결정성 제거·N+1 회피. 최종 TestLogRow 투영은 `ToListAsync` 후 in-memory(provider 중립).
- 회귀 테스트 `E1_DerivedFields_ComeFromSameRow_NoFrankenstein`: 동일 barcode 2행(001/08:00 vs 002/09:00) 시드 → 파생 chuteNo="001" **및** receiveTime=08:00 둘 다 최소-Id 행에서(섞임 0) 단정. 결정적 계약 고정. 내 원래 Minor #1(2회 APPLY)·#2(무순서) 동시 해소.

### #4 (LogController export 예외 처리) — PASS
- `catch (OperationCanceledException) { throw; }` — 클라이언트 취소가 **더 이상 400 이 아님**(상위 위임). catch 순서 정확(ArgumentException→OCE→generic; OCE 는 ArgumentException 아님이라 첫 블록 미포획, TaskCanceledException 은 OCE 파생이라 함께 포획).
- 그 외 예외 → `ILogger<LogController>.LogError`(서버 로그) + `Fail("Export failed.")` — **ex.Message 클라이언트 미노출**(정보 누출 차단). 생성자 `ILogger<T>` 주입은 ASP.NET Core 자동 등록이라 배선 변경 불요.

### #5 (3-way IsMatch both-null 오판정 방지) — PASS
- `isMatch = ... && sort!.EquipmentNo is not null && sort.EquipmentNo == result!.ChuteNo` — 슈트값 둘 다 null 일 때 `null==null`=true 오판정 차단.
- 회귀 테스트 `E5_Comparison_BothChuteNull_NotMatch`: INPUT+SORT(equip null)+RESULT(chute null) → hasInput/hasSort/hasResult 전부 true·**isMatch=false**·isMissing=false. **구코드 RED-catch 진성**(구코드는 isMatch=true 였음).

### 잔여 플레이크 정직 보고 (B2B-무관)
- Generator 보고 첫 full-suite 1회 `HandshakeResidueTests.S5_` blip = **선재 병렬-부하 타이밍 플레이크**(MEMORY: s9-flake-under-e2e-load·e2e-parallel-load-surfaces-integration-flakes). 본 스프린트 미유발 — B2B 테스트는 순수 HTTP + in-memory SQLite 로 타이밍/동시성 표면 0. 내 full-suite 1회 = 288 GREEN 클린 + QueryApiTests 단독 17/17 결정적 GREEN 로 B2B 정확성을 무관 플레이크와 분리 입증. S5_ 견고화는 별건(범위 밖).

### 미접촉 Minor(지시대로 유지 — 비차단)
- unbounded materialization(E5/E4 ToListAsync 전량 로드)·O(n²) match(선형 스캔)·FilterArchive DRY(TestLog/WorkResult 오버로드 중복)·comparison 주석 — 데이터 소규모라 무해, 후속 참고.

**FIX ITER 1 재검증 결과: #1·#4·#5 전부 fresh evidence PASS(회귀 테스트 2건 진성). S-B2B-3a APPROVED 유지 — 커밋 진행 가능.**
