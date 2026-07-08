# Sprint Contract — S-B2B-3a (조회 백엔드 API: 로그·비교·박스·Excel)

> Planner Subagent · 2026-07-08
> 상위 목표 "B2B-3 로그·비교·박스·설정"(4개 disabled 네비 활성화)의 **첫 번째 코히런트 서브스프린트**.
> 확립된 2a(백엔드)→2b(프론트) 분할 패턴을 그대로 따른다. 본 계약은 **백엔드 조회 API에 한정**하고,
> 프론트 페이지(B2B-3b)·설정 페이지는 아래 "Alternatives / Remaining phases"에 로드맵으로 명시한다.

---

## ⚠ Questions for user (착수 전 확인 — B2B-3 전체 스코프에 영향, 단 B2B-3a는 비차단)

**Q1 — "설정(Settings)" 페이지의 정체 (B2B-3 전체 스코프를 가름).**
원본 `Settings` 페이지 = **자동생성 규칙**(슈트범위/슈트당개수/고정바코드) + **인쇄 용지 크기** 두 섹션을 하나의
`auto-config` 엔드포인트에 통합 저장하는 화면이었다(PROGRAM_STRUCTURE §6.4). 그러나:
- `auto_generate_config`·자동생성 로직·`auto-config` 엔드포인트는 **미이식 확정(불변)** — 설정의 절반이 원천 부재.
- A4 라벨 인쇄(설정의 나머지 절반이 제어하는 대상)는 **B2B-2c로 이연**됐고, 사용자가 이번에 B2B-2c 대신 B2B-3를
  선택 → 인쇄 기능 자체가 아직 없음(설정만 이식하면 소비처 없는 고아 화면).

→ "설정" 페이지를 무엇으로 채울지 3안 중 택1이 필요하다(이것이 확정돼야 B2B-3b의 4번째 페이지가 정의됨):
- **(a) 권장 — 이번 단계에선 "설정"을 disabled 유지**(로그·비교·박스 3개만 활성화). 자동생성 미이식·인쇄 미구현이라
  이식할 백엔드 설정 계약이 없다. 인쇄(B2B-2c)를 나중에 하면 그때 인쇄 설정과 함께 부활.
- **(b) 인쇄/라벨 설정 전용** 페이지(용지 프리셋·바코드 심볼로지) — 단, 인쇄 기능(B2B-2c)을 이 스프린트에 흡수해야
  소비처가 생김(스코프 급증).
- **(c) B2B 일반 환경설정**(기본 업무일자·자동새로고침 간격·아카이브 기본 필터·바코드 타입) — localStorage 영속,
  백엔드 무관. 원본과 다른 재해석.

**B2B-3a(본 계약)에는 영향 없음** — 자동생성 미이식으로 어차피 백엔드 설정 엔드포인트가 없다. Q1은 B2B-3b 착수
전까지만 답하면 된다. 본 계약은 Q1과 독립적으로 진행 가능.

---

## [Sprint Contract]

### Goal
프론트 전용 **읽기 전용 조회 API 6종**을 additive로 신설한다 — 투입/분류 로그 조회, RCS API 호출 이력 조회,
투입+분류 통합 Excel 내보내기, 투입/분류/결과 3-way 비교, 박스+내품 목록 조회. 모든 로그/비교 조회는
**아카이브 필터(`active|all|archivedOnly`)를 소비**한다(B2B-2a에서 도입한 `archived_at` 소프트삭제 노출).
기존 B2C(17테이블·라우트·페이지)와 B2B-1/2 산출물(RCS 5 API·test-data 관리 API·엔티티·마이그레이션)은 **0 변경**.
이 백엔드가 확정되면 후속 B2B-3b 프론트 페이지(로그·비교·박스)가 곧바로 소비한다.

### Implementation Scope (Generator가 만들 것)

**신규 조회 엔드포인트 6종** (경로는 원본과 동일 — 후속 프론트가 그대로 소비):

| # | 메서드 · 경로 | 용도 | 응답 |
|---|---|---|---|
| E1 | `GET /api/logs/input?bizDay=&archived=` | 투입(INPUT) 로그 조회 | 원시 배열(camelCase) |
| E2 | `GET /api/logs/sort?bizDay=&archived=` | 분류(SORT) 로그 조회 | 원시 배열 |
| E3 | `GET /api/logs/api-calls?date=` | RCS API 호출 이력 조회(최대 500건) | 원시 배열 |
| E4 | `GET /api/logs/export?bizDay=&batch=` | 투입+분류 통합 Excel 다운로드 | `.xlsx` 바이너리 |
| E5 | `GET /api/test-data/comparison?bizDay=&archived=` | 투입/분류/결과 3-way 비교 | 원시 배열 |
| E6 | `GET /api/boxes?bizDay=&batch=` | 박스 목록 + 내품 조회 | 원시 배열 |

**서비스·컨트롤러 (전부 additive):**
- 신규 `ILogService`/`LogService` (Scoped, `WcsDbContext` 주입) — E1·E2·E3·E5의 로직.
  - 투입/분류 로그 조회(§3.2.6): `test_log` where (bizDay, log_type) + **아카이브 필터**. ChuteNo/ReceiveTime 등
    파생 필드는 상관 서브쿼리로 인라인해 **N+1 회피**(EF Core OUTER APPLY 번역). 원본의 **Barcode 단독 매칭** 특성 보존.
  - API 호출 이력(§3.2 표·§9.6): `api_call_log` where `called_at` 날짜 필터, **최대 500건**(`AppConstants` 상수).
  - 3-way 비교(§3.2.7): `test_data` 기준행 순회 → INPUT/SORT는 `TestDataId` 우선 매칭 + Barcode 폴백(**사용된 로그
    id 재사용 금지**), RESULT는 SORT.ChuteNo 우선. `IsMatch`(3자 존재 + SORT.ChuteNo==RESULT.ChuteNo)·`IsMissing`·
    셀 단위 `HasInput/HasSort/HasResult` 산출. **아카이브 필터** 소비(archived 로그/결과 제외 가능).
    - **결정(문서 권장 채택): 매칭 키에 Batch 포함**(원본 §3.2.7/§9.3 이월 결함 교정 — 같은 bizDay 다른 batch 오매칭
      방지). 신규 이식이므로 결함을 이식하지 않고 교정 채택. (원본 Barcode-only 동작은 재현하지 않는다.)
- 신규 `ILogExportService`/`LogExportService` (Scoped) — E4의 하이브리드 페어링 Excel 생성.
  - **Phase 1(정밀)**: `TestDataId`로 INPUT↔SORT 1:1 매칭(LogTime→Id 순 Queue). **Phase 2(폴백)**: 미매칭 INPUT을
    미사용 SORT와 `(Batch, Barcode)` 그룹 LogTime 오름차순 zip(§3.2.8).
  - **소요시간 = (SORT.LogTime − INPUT.LogTime) 초, `"F1"` 포맷, span≥0일 때만**(§3.2.9).
  - **인덕션→층 매핑**(1·2→"2층", 3·4→"1층", 그 외 공백) — 설비 고정 하드코딩 규칙 보존(§3.2.9).
  - 출력은 INPUT LogTime 오름차순. SORT 미매칭 시 슈트/소요시간 칸 공백. `ClosedXML`(이미 의존성) 재사용.
  - 기본 **active 로그만**(archived 제외 — 삭제/초기화된 데이터는 내보내지 않음).
- `IBoxService`에 **조회 메서드 추가**(`GetBoxesAsync(bizDay, batch?)`) — 기존 `ProcessBoxAsync` 무접촉(같은 파일에
  additive). 응답: `[{ id, bizDay, batch, boxNo, chuteNo, endTime, createdAt, items:[{barcode, qty}] }]`(§2.3 표).
- 신규 `LogController` (`[Route("api/logs")]`) — E1·E2·E3·E4.
- 신규 박스 조회 컨트롤러 (`[Route("api/boxes")]`) — E6.
- E5 비교는 **기존 `TestDataController`에 메서드 추가**(원본이 comparison을 TestDataController에 둔 것과 정합 — additive).
- 신규 응답 DTO/record(camelCase 직렬화). API 호출 이력·비교·박스·로그 행 record. `AppConstants`에
  `ApiCallLogMaxItems = 500` 추가(하드코딩 금지). 아카이브 파라미터 파싱은 기존 `TestDataService.ParseArchiveFilter`
  재사용 또는 공용화(중복 금지).
- Program.cs에 `AddScoped<ILogService, LogService>()`·`AddScoped<ILogExportService, LogExportService>()` **append**(기존 배선 무접촉).

**테스트 (xUnit, `B2bWebApplicationFactory` in-memory SQLite EnsureCreated 재사용):**
- 각 엔드포인트 서비스 로직 단위 + 컨트롤러 통합(실 HTTP 왕복). 아카이브 필터 3상태 동작. Excel 페어링(Phase1/Phase2)·
  소요시간·인덕션 층매핑. 3-way 비교 4상태(일치/불일치/누락) + Batch 포함 키(음성 대조: 다른 batch 동일 barcode 미오매칭).

### Evaluation Criteria (Backend/API 4기준 — Evaluator 판정 축)
1. **API Design Quality (★★★)** — 경로·응답 형태가 원본 계약(`/api/logs/*`·`/api/boxes`·comparison 원시 배열,
   export=.xlsx 바이너리)과 정확히 일치. 아카이브 파라미터 명명 일관(`archived=active|all|archivedOnly`, 미인식→active).
2. **Architecture Originality (★★★)** — 조회 로직이 순수 additive 서비스로 격리, 기존 서비스/미들웨어/라우트/마이그레이션
   0 변경. N+1 회피가 구조적으로 보장(상관 서브쿼리·집합 프리로드).
3. **Craft (★★)** — bizDay 비존재 날짜 → 400(#17 국소 try/catch), bizDay 누락 시 계약대로 처리, export 오류 400+Fail,
   3-way 매칭에서 사용된 로그 id 재사용 금지, api-calls 500 상한 준수. `[StringLength]`는 신규 **쓰기** DTO에만 필요한데
   본 스프린트는 **읽기 전용**이라 신규 쓰기 DTO 0(과잉 부여 금지).
4. **Functionality (★★)** — RCS 쓰기 API가 적재한 데이터를 조회 API가 정확히 되읽고, reset/delete 아카이브가 필터에
   반영됨. Excel이 유효한 xlsx로 열리고 투입→분류 페어링·소요시간이 산출됨.

### Completion Conditions (Evaluator PASS 최소 조건)
- `dotnet test backend/Wcs.sln` **전체 GREEN**(기존 스위트 회귀 0 + 신규 테스트). 빌드 에러 0.
- 6개 엔드포인트 전부 실 HTTP 왕복으로 계약 형태 확인(원시 배열/바이너리, camelCase, 0건이면 `[]`).
- 아카이브 필터 검증: reset/delete 후 `archived=active`는 해당 로그/결과 제외, `archived=archivedOnly`는 그 행만 반환,
  `all`은 전부 — **DB 행 COUNT 불변**(소프트삭제 재확인, 하드삭제 0).
- 3-way 비교: 일치/불일치/누락 각 케이스 + Batch 포함 키 음성 대조(다른 batch 동일 barcode 미오매칭) 단정.
- Excel export: 생성된 바이트가 유효 xlsx로 파싱되고 Phase1/Phase2 페어링·소요시간·층매핑이 기대대로 채워짐.
- **무접촉 실증**: `git diff` 상 기존 B2C 코드·B2B-1/2 서비스·`Program.cs` 기존 배선·양 provider 마이그레이션·
  ModelSnapshot **변경 0**(신규 배선 append 라인만). 신규 마이그레이션 **0개 기대**(스키마 변경 없음 — 읽기 전용).
- 정적 검사: `dotnet build` 경고(신규분) 0. (선재 NU1903 audit 부채는 스코프 밖 — todo 유지.)

### 절대 규칙 준수 체크(본 스프린트 해당분)
- B2B 테이블·코드 **완전 분리·additive** — 기존 B2C/B2B-1/2 계약 무변경. ✓ (라우트 `/api/logs`·`/api/boxes` 미사용 확인됨)
- **하드삭제 금지** — 본 스프린트는 읽기 전용, 삭제 경로 0. 로그/비교는 `archived_at` 필터를 소비(요구사항 충족).
- **마이그레이션**: 스키마 변경 없음 → 신규 마이그레이션 0 기대. 만약 조회 성능 인덱스를 추가한다면 **양 provider
  (SqlServer/Sqlite) 마이그레이션 각 1개 + ModelSnapshot diff 국한** 필수 — 단 데이터 규모상 인덱스 불요 권장(추가 지양).
- **`[StringLength]`**: 신규 쓰기 DTO 없음(읽기 전용) → 해당 없음. (재발 교훈은 쓰기 DTO 대상.)
- **하드코딩 금지(#7)**: api-calls 상한 500 → `AppConstants.ApiCallLogMaxItems` 상수. 인덕션→층 매핑은 설비 고정
  물리 규칙이라 문서화된 하드코딩 유지(원본 §3.2.9 — 시간값 아님, 규칙 #7 대상 아님).
- **SQL Server = prod provider**: 조회 LINQ가 SqlServer에서 번역 가능해야(OUTER APPLY 등). in-memory SQLite GREEN만으로
  닫지 말고, 최소한 LINQ가 provider-특이 API(SQLite 전용 함수 등)에 의존하지 않음을 코드 판독으로 확인.

### Parallel Modules
N/A (single module) — 신규 서비스 3종이 공유 파일(`Program.cs` 배선·`AppConstants`·`ParseArchiveFilter` 공용화)을
동시 수정할 여지가 있어 boundary-clean 분할이 아니다. 단일 Generator가 순차 구현. (조회 규모상 fan-out 이득 없음.)

### Evaluation Dimensions
functional only — 순수 조회 백엔드. 보안/성능 특이 표면 없음(익명 내부망 전제 유지·데이터 소규모). 표준 단일 차원 검증.

---

- Detected Project Type: **Full-stack**
  (리포 신호: `frontend/`의 브라우저 진입점(React SPA) + `backend/src/Wcs.Api`의 서버 컨트롤러 계층이 같은 repo에 공존.
   단, **본 서브스프린트가 실제로 건드리는 표면은 백엔드 조회 API에 한정**된다 — 프론트 페이지는 B2B-3b로 분리.)

- Verification Scenarios (per-type, Full-stack):

  === Applicable Web/UI scenarios (frontend surface this sprint touches) ===
  - **N/A (사유 명시)**: B2B-3a는 프론트엔드 표면을 0 건드린다(신규 페이지·네비 변경·라우트 추가 없음). 로그·비교·박스
    페이지와 `Layout.tsx` 네비 활성화는 후속 **B2B-3b**의 범위. 따라서 Web/UI 브라우저 시나리오 없음.
    (B2B-3b 착수 시 각 페이지 기본/필터/빈·에러 상태 + 다크모드 N/A[단일 라이트 테마] + 조회 인터랙션을 그 계약에서 채운다.)

  === Applicable Backend/API scenarios (backend surface this sprint touches) ===
  - **Endpoints touched (method + path)** — 6종:
    1. `GET /api/logs/input?bizDay=&archived=`
    2. `GET /api/logs/sort?bizDay=&archived=`
    3. `GET /api/logs/api-calls?date=`
    4. `GET /api/logs/export?bizDay=&batch=`
    5. `GET /api/test-data/comparison?bizDay=&archived=`
    6. `GET /api/boxes?bizDay=&batch=`
  - **Happy path per endpoint (입력 → 출력 형태)**:
    - E1/E2: `bizDay` 지정 → 해당 일자 INPUT/SORT 로그 원시 배열(각 행에 바코드·설비·PID·상태·사유·시각·슈트/수신시각).
      0건이면 `[]`. `archived` 미지정 → active(미아카이브)만.
    - E3: `date` 지정(또는 미지정=전체) → `api_call_log` 원시 배열 **최대 500건**(endpoint·method·status·httpStatusCode·
      durationMs·clientIp·calledAt 등, request/response body는 마스킹된 원문).
    - E4: `bizDay`(필수)+`batch?` → `.xlsx` 바이너리 + `Content-Disposition` 파일명. 투입 1행마다 매칭 분류·소요시간·
      인덕션 층 채움.
    - E5: `bizDay` → 바코드 단위 3-way 비교 배열(hasInput/hasSort/hasResult·isMatch·isMissing·양측 chuteNo).
    - E6: `bizDay`(필수)+`batch?` → 박스 배열, 각 박스에 내품(barcode·qty) 배열 포함.
  - **Relevant error cases per endpoint (해당하는 것만 — 패딩 금지)**:
    - 400: `bizDay` 비존재 날짜(예 `20261332`) → `NormalizeBizDay` `ArgumentException` 국소 catch → 400 + `{status:F, message:"Invalid date: ..."}`(#17). E4·E6의 `bizDay` **필수 누락** → 400 + Fail("... required.").
    - 400: E4 export 생성 오류 → 400 + Fail.
    - 200 빈 배열: 데이터 0건은 오류가 아니라 `[]`(E1·E2·E3·E5) / 빈 박스 목록(E6).
    - (401/403/404/422/500 미해당: 익명 내부망 전제·읽기 전용·존재하지 않는 경로는 프레임워크 라우팅. 패딩하지 않음.)

  === End-to-end data-flow (2+ 레이어 횡단) ===
  - **RCS 쓰기 → 조회 되읽기 → 아카이브 → 필터 반영** 크로스레이어 왕복:
    (1) 기존 RCS API로 test_data 등록 + `POST /input`·`/classification`·`/results`·`/box`로 로그/결과/박스 적재
    → (2) E1/E2/E5/E6 조회가 그 데이터를 정확히 되읽음(컨트롤러→LogService/BoxService→WcsDbContext→DB)
    → (3) `POST /api/test-data/reset` 또는 `DELETE /api/test-data`로 연관 로그/결과 아카이브(archived_at 세팅, 행 보존)
    → (4) 동일 조회를 `archived=active`(제외)·`archivedOnly`(그 행만)·`all`(전부)로 반복해 **필터가 아카이브 상태를
    정확히 반영**하고 **DB 행 COUNT는 불변**(하드삭제 0)임을 단정. 이 흐름이 쓰기경로↔읽기경로↔소프트삭제 생명주기를
    한 번에 횡단한다.

> Planner self-check — Detected project type: Full-stack. Required scenario slots: 3 (Applicable Web/UI [N/A+사유], Applicable Backend/API [endpoints·happy-path·error-cases], End-to-end data-flow). All slots filled: yes.

---

## Alternatives considered / Remaining phases (B2B-3 전체 로드맵)

**분할 판단 근거**: B2B-3 원래 범위(4 페이지 + 조회 백엔드 + Excel export + 설정 영속 + api-call-log 노출)는 단일
스프린트로 5-iteration cap 초과 위험이 크다. 확립된 **2a(백엔드)→2b(프론트)** 패턴을 재적용해 순차 분할한다.
- **기각한 대안 (a) 단일 스프린트 + Parallel Modules fan-out**: "엔드포인트+페이지" 모듈 3개가 `Layout.tsx`(네비)·
  `App.tsx`(라우트)·프론트 api 클라이언트·공용 UI 인프라를 공유 수정 → boundary-clean 아님(동시 파일 쓰기 충돌). 기각.
- **채택 (b) 순차 분할** — 본 계약을 **첫 서브스프린트(B2B-3a 백엔드 조회 API)**로 한정. 데모 가능한 증분(HTTP/테스트로
  검증)이며 iteration cap 내. 후속:

| 서브스프린트 | 범위 | 검증 |
|---|---|---|
| **B2B-3a (본 계약)** | 조회 백엔드 6종 + Log/Export 서비스 + Box 조회 + 3-way 비교 + 아카이브 필터 소비 | xUnit + HTTP 왕복 |
| **B2B-3b (후속)** | 프론트 3페이지(로그·결과비교·박스) — `Layout.tsx` 네비 활성화·`App.tsx` 라우트·페이지·컬럼필터/통합검색/Excel 다운로드·마스터-디테일. 기존 Toast/Dialog/DataGrid 재사용 | Playwright 브라우저 E2E + 콘솔 캡처 |
| **설정(Q1 확정 후)** | Q1 답에 따라 (a) disabled 유지 / (b) 인쇄+설정(B2B-2c 흡수) / (c) 일반 환경설정 | Q1 확정 시 별도 계약 |

**참고 사실(Generator 컨텍스트)**:
- 프론트 UI 인프라는 B2B-2b에서 이미 존재: `ToastProvider`·`ConfirmDialog`/`Dialog`·`DataGrid`·`ui/{table,select,badge,button,card}`·
  `UiModeProvider`(bizDay/autoRefresh 전역) → B2B-3b는 재사용(신규 무거운 UI 라이브러리 금지).
- `api_call_log` 미들웨어는 이미 `/api/v1/works/` 요청을 기록 중(Program.cs:281) → E3는 그 적재분을 읽기만 한다.
- `TestDataService.ParseArchiveFilter`·`AppUtils.NormalizeBizDay`·`NormalizeChuteNo`·`AppConstants`는 이미 존재 → 재사용/공용화.
