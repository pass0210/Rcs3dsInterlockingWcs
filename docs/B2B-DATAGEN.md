# B2B-DATAGEN.md — test-data 관리 API·아카이브·DataGenerator 화면 캡처 (Generator 단일 근거)

> 작성: Planner Subagent (S-B2B-2) · 2026-07-08
> **목적**: 원본 `BowooTestBatchSystem_v2`(형제 폴더, 별도 repo)와 로컬 `TEST_ORDER_DB`는 후속
> Generator·Evaluator의 접근 경계(pwd) **밖**이다. 이 문서는 B2B-2가 이식할 **프론트 전용 test-data
> 관리 API 계약·서비스 알고리즘**, **★기록 아카이브(archived_at 소프트삭제) 재설계**, **DataGenerator
> 화면/인터랙션 요구사항**, **B2C/B2B UI 토글 요구사항**을 캡처한다.
> Generator는 **이 문서 + `docs/B2B-SCHEMA.md`(B2B-1 스키마·서비스) + `docs/PROGRAM_STRUCTURE.md`
> (청사진) + 우리 프로젝트 기존 코드**만으로 B2B-2를 구현할 수 있어야 한다. 원본 코드/DB는 참조 전용
> 이었고 수정/커밋하지 않았다.
> B2B-1 산출물(엔티티·WcsDbContext·마이그레이션·RCS 5 API)은 `docs/B2B-SCHEMA.md`에, 그 계약은 **불변**.

---

## 0. 범위 요약

- **B2B-2 포함**: test-data 관리 API(수동생성 `generate` · 조회 `summary`/`detail` · 초기화 `reset` ·
  삭제 `delete` · 엑셀 `upload`) + **archived_at 아카이브 재설계** + **DataGenerator 프론트 페이지** +
  **B2C/B2B UI 토글**.
- **B2B-2 제외(→ B2B-3 후속)**: 로그 조회(`/api/logs/*`) · 결과 3-way 비교(`/api/test-data/comparison`) ·
  박스 조회(`GET /api/boxes`) · Excel export(`/api/logs/export`) · 인쇄/자동생성 설정 페이지.
- **미이식(B2B-1 확정 · 불변)**: `auto_generate_config` · 자동생성 트리거 · `auto-config` 페이지 ·
  `preview-chutes`(자동생성 전용). 수동 생성·엑셀 업로드만 이식.

---

## 1. 관리 API 계약 (프론트 전용 — RCS 계약 아님)

> 원본 라우트 접두 `/api/test-data`(원본 `TestDataController` [Route("api/test-data")]). 우리 프로젝트도
> 동일 접두 유지(프론트 기대·원본 계약 보존). 기존 라우트(`/api/monitor`·`/api/ops`·`/api/v1`)와 충돌 0.
> 응답 `{ "status":"S"|"F", "message":"..." }`(B2B-1 `B2BApiResponse` 재사용) 또는 원시 배열(조회).

| 메서드 | 경로 | 용도 | 요청 | 응답 |
|---|---|---|---|---|
| POST | `/api/test-data/generate` | 수동 생성(라운드로빈) | `GenerateRequest`(body) | `ApiResponse` |
| GET | `/api/test-data/summary?bizDay=` | 배치별 건수·수신시각 요약 | query `bizDay?` | 원시 배열 |
| GET | `/api/test-data/detail?bizDay=&batch=` | 상세(로그 LEFT JOIN) | query `bizDay`,`batch`(둘 다 필수) | 원시 배열 |
| POST | `/api/test-data/reset` | 수신시간 초기화 + 연관 로그/결과 **아카이브** | `List<long> ids`(body) | `ApiResponse` |
| DELETE | `/api/test-data` | 선택 삭제 + 연관 로그/결과 **아카이브** | `List<long> ids`(body) | `ApiResponse` |
| POST | `/api/test-data/upload` | 엑셀 업로드(신/구양식 판별) | multipart `file` | `ApiResponse` |

### 1.1 `GenerateRequest` (수동 생성 DTO — 원본 그대로)

| 필드 | 타입 | 검증 | ErrorMessage(정본) |
|---|---|---|---|
| BizDay | string | `[Required]`, `^\d{8}$|^\d{4}-\d{2}-\d{2}$` | `BizDay must be in YYYYMMDD or YYYY-MM-DD format.` |
| Batch | string | `[Required]`, `StringLength(10, Min=1)` | (기본형식) |
| ChuteNos | string | `[Required]`, `StringLength(200)`, `^[\d\s,\-]+$` | `ChuteNos may only contain digits, commas, hyphens, and spaces.` |
| BarcodeCount | int | `[Range(1, 10000)]` | `BarcodeCount must be between 1 and 10000.` |

> ⚠ `BarcodeCount` 상한은 **10000**(생성 개수). RCS API의 `Qty` 상한 9999(`AppConstants.QtyMaxPerRequest`)와
> **다른 값·다른 의미**다. 혼동 금지 — GenerateRequest.BarcodeCount = 10000, RCS qty = 9999.

- 관리 API의 검증실패(400) 형식: B2B-1 `InvalidModelStateResponseFactory` 경로분기는 현재
  `/api/v1/works/`만 `ApiResponse.Fail`로 형식화한다. **관리 API도 `{status,message}` 400을 내려면**
  경로 접두 allowlist에 `/api/test-data`를 **추가**(additive — 기존 `/api/v1/works/` 계약·B2C ProblemDetails 불변).
  → `AppConstants`에 `TestDataRoutePrefix = "/api/test-data"` 추가 후 팩토리 경로 체크를 두 접두로 확장.
  (대안: 컨트롤러에서 `ModelState` 수동 검사 후 `BadRequest(ApiResponse.Fail(...))`. Generator 판단, allowlist 확장 권장.)

### 1.2 `upload` 검증 (원본 컨트롤러 규칙 — 3중)

1. 파일 없음/0바이트 → 400 `Please select a file.`
2. 크기 > 10MB(`UploadMaxBytes`) → 400 `File size must be 10MB or less.`
3. 확장자(경로 제거 후 `Path.GetExtension`)가 `.xlsx`/`.xls` 아님 → 400 `Only Excel (.xlsx, .xls) files can be uploaded.`
4. MIME 화이트리스트(`...spreadsheetml.sheet` / `application/vnd.ms-excel` / `application/octet-stream`) 불일치 → 400 `Invalid file format.`
5. 파싱 성공 → 200 `S` `"{n}건 업로드 완료"`. 파싱 단계 실패 message는 §2.2.

- `[RequestSizeLimit(UploadMaxBytes)]` 속성. `UploadMaxBytes = 10MB` 상수는 원본 §3.5(현 B2B `AppConstants`엔 없음 → 추가).

---

## 2. 서비스 알고리즘 (원본 `TestDataService` 실측 캡처)

> 우리 프로젝트에 신규 `ITestDataService`/`TestDataService`(Scoped, `WcsDbContext` 주입) 이식.
> 슈트 파서·zero-pad·바코드 채번은 B2B-1 `AppUtils`/`AppConstants`와 정합해야 한다(단일 소스).

### 2.1 `GenerateAsync(GenerateRequest)` — 수동 라운드로빈 생성
1. `chuteNos = ParseChuteNos(req.ChuteNos)` — 콤마구분에서 `"a-b"` 범위 전개·단일 숫자, `HashSet<int>`
   중복제거 후 오름차순. 0개면 F `Invalid chute numbers`. `BarcodeCount <= 0`이면 F `Invalid barcode count`.
2. `bizDay = NormalizeBizDay(req.BizDay)` — **반드시 정규화**(raw 8자리로 저장하면 unprocessed 조회 매칭 실패 회귀).
3. `for i in 0..BarcodeCount`: `chute = chuteNos[i % chuteNos.Count]`(라운드로빈),
   `TestData { BizDay, Batch=req.Batch, Barcode=GenerateBarcode(), ChuteNo=chute.ToString("D3"), CreatedAt=DateTime.Now }`.
4. `AddRange` → `SaveChanges`. S(`Success`).
- **`ParseChuteNos(string)`**(static, 재사용): 위 파싱 로직. (원본은 자동생성 preview에도 썼으나 B2B-2는 preview 미이식.)
- **`GenerateBarcode()`**(static): `$"BC{DateTime.Now:yyyyMMddHHmmssfff}{Random.Shared.Next(1000,9999)}"`.

### 2.2 `UploadExcelAsync(Stream)` — 엑셀 판별
- **라이브러리**: 원본은 `ClosedXML.Excel`(`XLWorkbook`). 우리 백엔드엔 없음 → **`ClosedXML` 패키지 추가 필요**
  (Wcs.Api). (대안 OpenXML SDK는 구현량↑. ClosedXML 권장 — 원본 검증됨.)
- 워크시트 1번 `RangeUsed().RowsUsed()`. 행 0개면 F `Excel file contains no data.`
- **헤더 자동감지**: 1행 1열 값이 날짜형(`IsDateLike`: 8자리 숫자 또는 `YYYY-MM-DD` 10자리)이면 헤더 없음 → startRow=0,
  아니면 헤더 있음 → startRow=1.
- **양식 판별(행별)**: 컬럼 = (1)BizDay (2)Batch (3)Barcode (4)col4 (5)col5.
  - `col5` 채워짐 → **5컬럼 신양식**: barcode2 = col4(빈이면 null), chuteNo = col5.
  - `col5` 빔 → **4컬럼 구양식 호환**: barcode2 = null, chuteNo = col4.
  - barcode(3열) 빈/공백 행은 skip.
  - chuteNo는 `int.TryParse` 성공 시 `ToString("D3")`, 실패 시 원문. bizDay는 `NormalizeBizDay`. CreatedAt=Now.
- 유효행 0개 → F `No valid data to upload.` 성공 → S `"{n}건 업로드 완료"`.
- 전체 try/catch: 예외 → F `Excel parsing error: {ex.Message}`.

### 2.3 `GetSummaryAsync(bizDay?)` — 배치 요약
- `bizDay` 있으면 `NormalizeBizDay` 후 `WHERE biz_day == nDay`.
- `GroupBy(BizDay, Batch)` → `{ BizDay, Batch, Count = COUNT, ReceiveTime = MAX(receive_time) }`.
- 정렬: `BizDay desc, Batch desc`. 원시 배열(camelCase) 반환.

### 2.4 `GetDetailAsync(bizDay, batch)` — 상세(로그 조인)
- `test_data WHERE biz_day==bizDay && batch==batch` 전체 로드(메모리).
- 정렬: `Barcode → ChuteNo(int 파싱 우선, 실패 int.MaxValue 후순위) → ChuteNo 문자열`(DB단 숫자정렬 불가 회피).
- 관련 `test_log` 조회 후 각 test_data 행에 INPUT/SORT 로그 매핑:
  - **INPUT**: `TestDataId == d.Id` 우선(LogTime desc first), 없으면 `Barcode==d.Barcode && TestDataId==null` 폴백.
  - **SORT**: 동일 규칙.
- 반환 행: `{ Id, BizDay, Batch, Barcode, Barcode2, ChuteNo, ReceiveTime, CreatedAt,
  InputStatus, InTime(=inLog.LogTime), SortStatus, SortTime(=sortLog.LogTime) }`.
- **★ 아카이브 필터**(신규): 기본은 `archived_at == null`인 로그만 매핑(아카이브분 제외). §3.4 참조.

### 2.5 `ResetReceiveTimeAsync(ids)` — 수신 초기화 + 연관 아카이브  ★재설계
### 2.6 `DeleteAsync(ids)` — 선택 삭제 + 연관 아카이브  ★재설계
> 원본은 **연관 test_log·work_result를 Barcode 키로 하드삭제(`RemoveRange`)**했다.
> §3에서 **하드삭제 금지 → archived_at 소프트삭제**로 재설계한다(사용자·사수 확정 2026-07-08).
> reset/delete의 test_data 처리 차이 + 아카이브 스코핑은 §3에 상세.

---

## 3. ★ 기록 아카이브 재설계 (B2B-2 핵심 — 원본과 다른 부분)

> **확정 방침(사용자·사수 2026-07-08)**: test_data 삭제/초기화 시 연관 `test_log`·`work_result`를
> **하드삭제 금지 → `archived_at` 소프트삭제(보존)**. 원본의 barcode 키 **하드 연관삭제**(§3.3·§11.2 위험 지적)를
> 이식하지 말 것. "삭제됨/보관" 필터로 아카이브분 조회 가능.

### 3.1 스키마 변경 (add-only ALTER — 양 provider)
- `test_log`에 `archived_at datetime2 NULL` 추가.
- `work_result`에 `archived_at datetime2 NULL` 추가.
- 엔티티(`Wcs.Data.B2B.TestLog`·`WorkResult`)에 `DateTime? ArchivedAt`(HasColumnName `archived_at`, nullable) 추가.
- 마이그레이션: `Wcs.Migrations.SqlServer`/`Wcs.Migrations.Sqlite` 각각 **B2B 테이블 컬럼 추가만**(`AddColumn`).
  **기존 B2C 17테이블 무변경 · B2B 다른 5테이블 무변경**. ModelSnapshot diff = test_log/work_result에 archived_at 추가로만 국한.
- (조회 성능: `IX_test_log_archived_at` 등 인덱스는 선택 — 데이터 규모상 필수 아님, Generator 판단.)

### 3.2 아카이브 스코핑 (원본 over-broad 결함 교정)
> 원본은 `logs WHERE barcodes.Contains(l.Barcode)` — 선택 배치 밖 **동일 바코드 전부**를 건드렸다(§11.2 위험).
> 교정: **선택 test_data 행의 `(BizDay, Batch, Barcode)` 조합 집합**으로 스코프를 좁힌다.
- 선택 `ids` → `entities = test_data WHERE id in ids` 로드 → 키 집합 `keys = { (BizDay,Batch,Barcode) }`.
- **test_log 아카이브 대상**: `TestDataId in ids` **또는** `(BizDay,Batch,Barcode) in keys`. `archived_at`이 이미
  세팅된 것 제외(재아카이브 방지).
- **work_result 아카이브 대상**: `(BizDay,Batch,Barcode) in keys`(work_result엔 TestDataId 없음). 동.
- 대상에 `ArchivedAt = DateTime.Now`(B2B 로컬타임) 세팅 → `SaveChanges`. **DELETE/RemoveRange 금지.**

### 3.3 reset vs delete 차이
- **reset(`ResetReceiveTimeAsync`)**: 선택 test_data 행 `ReceiveTime = null`(초기화·행 유지) + 연관 로그/결과 **아카이브**(§3.2).
  → 해당 배치가 다시 "미작업(unprocessed)"으로 조회 가능. 상세 그리드엔 입력/분류 상태가 사라짐(아카이브분 기본 제외).
- **delete(`DeleteAsync`)**: 선택 test_data 행 **하드삭제(`RemoveRange`)**(등록 원장 제거 — 정당) + 연관 로그/결과 **아카이브**(§3.2).
  → test_data는 요약/상세에서 사라지되, 로그/결과 이력은 archived로 보존.
  - (test_data 자체를 소프트삭제할지 vs 하드삭제할지 = 사용자 게이트 Q3. **권장: test_data 하드삭제**(원본 동작·등록 원장),
    로그/결과만 archived 보존.)
- FK 없음(test_log.test_data_id 인덱스만) → test_data 하드삭제해도 archived 로그의 test_data_id는 고아 참조가 되나
  **이력 불변 원칙상 무해**(로그는 발생 사실의 불변 기록).

### 3.4 조회 필터 (아카이브 노출)
- `GetDetailAsync`(및 후속 로그 조회)에 아카이브 필터 파라미터 추가. 권장 형태: `archived` enum/문자열
  `active`(기본·archived_at==null만) | `all` | `archivedOnly`. (또는 bool `includeArchived`.)
- 기본 = active(아카이브분 제외). 프론트 "보관 포함/보관만" 토글이 이 파라미터를 전달(§4.5).
- **아카이브 핵심 시나리오(테스트로 단정)**: reset/delete 후 test_log·work_result 행이 **DB에서 사라지지 않고**
  `archived_at != null`로 세팅됨 + `archived=archivedOnly` 조회 시 그 행들이 반환됨.

---

## 4. DataGenerator 화면 요구사항 (우리 스택으로 재개발)

> 원본은 React18 + Context + axios + react-datepicker + CSS 클래스. **원본 JS/CSS를 복사하지 말고**
> 화면 기능·인터랙션을 **우리 스택(React19 + TS + Vite + Tailwind v4 + shadcn-style + TanStack Query)**으로
> 재현한다. 기존 `frontend/src` 구조(`components/ui/*`, `lib/api.ts`, `pages/*`)에 통합.
> 경로: `/data-generator`(B2B 메뉴 세트의 기본 진입).

### 4.1 레이아웃 (3분할)
- **좌측 카드**: 생성 폼(배치·슈트범위·바코드개수) + 엑셀 업로드.
- **중앙 카드**: 요약 그리드(날짜·배치·수량·수신시간, 컬럼 필터·행 선택 체크박스·행 클릭 시 상세 로드).
- **우측 카드**: 상세 그리드(바코드·슈트·투입상태·투입시간·분류상태·분류시간, 컬럼 필터·다중선택·우클릭 메뉴).

### 4.2 생성 폼
- 필드: **날짜**(전역 bizDay — disabled 표시, §5.3), **배치**(text, 예 "001"), **슈트 번호**(text, 예 "1-3, 5, 6"
  — 힌트 "쉼표로 구분, 범위는 하이픈"), **바코드 개수**(number, min 1).
- `<form onSubmit>`으로 감싸 Enter 제출. 미입력 필드 있으면 경고 토스트. 성공 시 요약 리로드 + 폼 리셋.
- 호출: `POST /api/test-data/generate { bizDay, batch, chuteNos, barcodeCount:int }`.

### 4.3 엑셀 업로드
- `<input type="file" accept=".xlsx,.xls">` + 업로드 버튼. 힌트 "컬럼: 날짜, 배치, 바코드, 슈트".
- `multipart/form-data`, field `file`. 성공/실패 토스트. 성공 시 요약 리로드 + 파일 인풋 초기화.
- 호출: `POST /api/test-data/upload`.

### 4.4 요약·상세 그리드 + 다중선택 인터랙션 (핵심)
- **요약 행 클릭** → 해당 (bizDay,batch) 상세 로드 + 상세 선택/드래그/컨텍스트 상태 **동기 초기화**.
- **요약 체크 + 수신 초기화 버튼**: 체크된 배치들의 상세를 `Promise.all` 병렬 조회 → id 취합 → 한 번에
  `POST /api/test-data/reset`(ids). 진행/완료 토스트. **확인 다이얼로그 필수**(danger).
- **상세 그리드 다중선택**(시각 강조용 `selectedIndices` Set — 체크박스와 별개, 우클릭 메뉴로 체크 반영):
  - 일반 클릭: 단일 선택 + 드래그 시작 + anchor 갱신.
  - 드래그(mouseEnter): dragStart~현재 연속 범위 덮어쓰기. **window `mouseup`으로 그리드 밖에서도 드래그 종료 보장.**
  - Shift+클릭: anchor~클릭 연속 범위(드래그 미시작).
  - Ctrl/Cmd+클릭: 개별 토글 누적(드래그 미시작).
  - INPUT(체크박스) 위 클릭·비좌클릭은 제외.
- **우클릭 컨텍스트 메뉴**: "선택영역 체크 (n)" / "선택영역 해제 (n)" → selectedIndices의 행 id를 실제
  체크 Set에 추가/제거. 외부 클릭·스크롤·Esc로 자동 닫힘.
- **상세 삭제 버튼**: 체크된 행 → 확인 다이얼로그(danger) → `DELETE /api/test-data`(ids). 성공 토스트.
- **컬럼 필터**: 각 컬럼 헤더에 텍스트 필터(부분일치·대소문자 무시). 헤더 정렬과 버블링 충돌 방지(stopPropagation).
- **자동 새로고침**: autoRefresh on이면 refreshInterval마다 요약(+선택배치 상세) 재조회. 이전 요청은
  `AbortController`로 취소(TanStack Query `refetchInterval` + `signal`로 자연스럽게 대체 가능).

### 4.5 아카이브 UI 필터 (신규 — §3.4)
- 상세 그리드에 "보관 포함/보관만"(또는 `active|all|archivedOnly`) 토글 → detail 조회 파라미터 `archived` 전달.
- 기본 active. reset/delete 후 archived 행을 이 토글로 확인 가능. (배치 위치 = 게이트 Q4.)

### 4.6 A4 라벨 인쇄 (폐쇄망 로컬 JsBarcode)
- 체크된 상세 행 → `window.open` 팝업에 A4 라벨 그림. **99.14×67.48mm, 2열×4행(8칸/페이지)**,
  페이지 여백 상13.97/하13.03/좌4.83/우4.94mm, 열간격1.95mm, 행간격0, 모서리4mm.
- 좌측 바코드 영역 + 우측 "CHUTE No." + 3자리 슈트번호(`padStart(3,'0')`). `barcode2` 있으면 좌측 상/하 듀얼 바코드.
- 바코드 타입 선택(CODE128/CODE93/ITF). 유효하지 않으면 CODE128 폴백.
- **XSS 방지**: 라벨 카드는 `innerHTML` 금지, `createElement`/`textContent`로 DOM 조립.
- **폐쇄망 핵심**: 외부 CDN 금지. `JsBarcode`는 **로컬 번들 자산**(`${origin}/JsBarcode.all.min.js`)을 팝업에 `<script src>`로 주입.
  → `frontend/public/JsBarcode.all.min.js`로 **로컬 vendoring**(예: `npm i jsbarcode` 후 `node_modules/jsbarcode/dist/JsBarcode.all.min.js`를 public/로 복사, 또는 커밋). npm 레지스트리 접근은 pwd 경계 밖 아님(원본 repo 접근과 무관).
- JsBarcode 로드 완료까지 100ms×최대 50회 폴링 후 `print()`→`close()`. 팝업 차단 시 토스트 안내.
- (참고) 원본 `PrintLabel.jsx`는 dead import — 실제 구현은 페이지의 `handlePrint`. 우리도 페이지 핸들러로 재구현.

### 4.7 필요한 프론트 인프라 (현 frontend에 부재 — 신규)
- **확인 다이얼로그**(delete/reset 확인) + **토스트**(success/warning/error): 현 `components/ui`엔 없음.
  → shadcn-style Dialog + 경량 Toast 컴포넌트 신규(또는 radix-ui 프리미티브). 원본 `UiContext`(비차단 토스트+await confirm) 개념 재현.
- **bizDay 선택 + autoRefresh**: 현 B2C 프론트엔 전역 업무일자/자동새로고침 상태 없음. §5.3 참조(B2B UI 상태).
- **날짜 입력**: 새 의존 회피 위해 native `<input type="date">` 권장(react-datepicker 미도입).

---

## 5. B2C/B2B UI 토글 요구사항

### 5.1 확정 방침 (불변)
- **UI 전환만**: 헤더/사이드바에서 **메뉴 세트 전환**. 백엔드 API는 양쪽 상시 활성(모드 게이트 없음).
  - **B2C 세트**: 모니터링(`/monitor`) · 3DS 워드(`/sorters`) · (운영 제어 F3 — 현 disabled 배지 유지).
  - **B2B 세트**: 데이터 생성(`/data-generator`) · (후속 B2B-3: 로그·비교·박스·설정 — disabled 배지로 예고).
- 기존 B2C 라우트·페이지·Layout **동작 보존**(토글은 메뉴 목록·헤더 타이틀·기본 진입만 바꿈).

### 5.2 현 구조 통합 지점 (우리 코드 실측)
- `frontend/src/components/Layout.tsx`: `NAV` 배열(현 3항목) + 하드코딩 헤더 타이틀 "실시간 모니터링".
  → NAV를 **모드별 2세트**로, 헤더 타이틀을 **모드/활성 페이지 기반 동적**으로. disabled+phase 배지 패턴 재사용.
- `frontend/src/App.tsx`: `Routes`에 `/data-generator` 추가. `/` 리다이렉트는 활성 모드의 기본 페이지로.
- `frontend/src/main.tsx`: `QueryClientProvider > BrowserRouter > App`. **전역 상태 저장소 없음** → 토글 상태 도입(§5.3).

### 5.3 전역 상태 방식 (게이트 Q2 — 권장 = React Context + localStorage)
- 현재 전역 스토어 부재. 토글(mode: `b2c`|`b2b`) + B2B용 bizDay/autoRefresh를 담을 경량 상태 필요.
- **권장**: React Context(`UiModeProvider` — 원본 `GlobalContext`/`UiContext` 개념) + localStorage 영속화
  (화이트리스트 가드로 손상값 폴백). 새 의존 없음. main.tsx의 Provider 계층에 삽입.
- 대안: (b) 라우트 기반(`/b2c/*`·`/b2b/*` 접두 — 딥링크·무스토어, 라우팅 재편) · (c) Zustand(신규 의존 — 과설계).
- bizDay/autoRefresh는 B2B-3(로그·비교·박스)도 공용하므로 지금 Context에 함께 두어 재작업 방지 권장.

---

## 6. 통합·검증 노트

- **라우트 충돌 0**: `/api/test-data/*`는 기존 `/api/monitor`·`/api/ops`·`/api/v1`·`/api/v1/works`와 무충돌.
  Program.cs 말미 catch-all `app.Map("/api/{**rest}", NotFound)`는 리터럴 컨트롤러 라우트보다 후순위(무해).
- **DI**: `AddScoped<ITestDataService, TestDataService>()` append(기존 배선 무접촉). ClosedXML 패키지 추가.
- **테스트 하네스**: `B2bWebApplicationFactory`(INSTANCE-level 고유 in-memory SQLite, EnsureCreated) 재사용.
  단위: 서비스 로직(라운드로빈·엑셀판별·아카이브 스코핑). 통합: 관리 API 계약 + **아카이브 시나리오**.
- **프론트 검증**: tsc/eslint 0, 콘솔 error 0(브라우저). 생성/조회/삭제 플로우 + 토글 메뉴 전환 + 인쇄 팝업.
- **무접촉**: 기존 B2C 17테이블·엔티티·라우트·페이지, B2B-1 산출물 계약(RCS 5 API·엔티티 형상·기존 마이그레이션)
  0 변경. B2B 테이블 add-only ALTER(archived_at)만. 기존 전체 테스트 스위트 GREEN 불변 + 신규.
