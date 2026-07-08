# Sprint Contract — S-B2B-2 (test-data 관리 API + 기록 아카이브 + DataGenerator 페이지 + B2C/B2B 토글)

> Branch: `feat/b2b-2-datagen-toggle` · Base: `develop` @ PR #38 병합(B2B-1 스키마·RCS 5 API 존재)
> 작성: Planner Subagent · 2026-07-08
> 스펙 근거(정본): `docs/B2B-DATAGEN.md`(신규 캡처 — 관리 API·아카이브·화면·토글) +
> `docs/B2B-SCHEMA.md`(B2B-1 스키마·서비스·통합노트) + `docs/PROGRAM_STRUCTURE.md`(§2.1·§3.2.1·§6.1·§9.1·§11.2) +
> `docs/FRONTEND.md`(우리 프론트 스택·컨벤션). Generator·Evaluator는 **pwd 밖(원본) 접근 금지** — 위 문서가 유일 근거.

## Goal

원본 `BowooTestBatchSystem_v2`의 **프론트 전용 test-data 관리 계층**을 이 프로젝트로 완전 분리 이식한다.
B2B-2는: (1) 관리 API(수동생성·조회·초기화·삭제·엑셀업로드), (2) **★기록 아카이브(archived_at 소프트삭제)
재설계**, (3) **DataGenerator 프론트 페이지**(우리 스택 재개발), (4) **B2C/B2B UI 토글**(메뉴 세트 전환).
기존 B2C 17테이블·엔티티·코드·라우트 + B2B-1 산출물의 **계약 부분은 0 변경**(신규 추가·B2B add-only ALTER만).

## 확정 방침 (사용자 — 불변)

1. **완전 분리·무접촉**: 기존 B2C 17테이블·엔티티·코드·라우트 + B2B-1 산출물 계약(RCS 5 API·엔티티 형상·기존
   마이그레이션) **0 변경**. 신규 추가 + **B2B 테이블 add-only ALTER**(archived_at)만. 안정성 최우선.
2. **B2C/B2B UI 토글 = UI 전환만**: 헤더/사이드바 메뉴 세트 전환(B2C=모니터링·3DS워드·(F3 운영제어) /
   B2B=데이터생성·(후속 로그·비교·박스·설정)). **백엔드 API는 양쪽 상시 활성(모드 게이트 없음)**. 토글 상태는 프론트 전역.
3. **자동생성 미이식**(B2B-1 확정): `auto_generate_config`·자동생성 트리거·`auto-config` 페이지·`preview-chutes` 제외.
   **수동 생성(슈트범위+바코드개수 라운드로빈)·엑셀 업로드만 이식**.
4. **★ 기록 아카이브 (사용자·사수 확정 2026-07-08)**: test_data 삭제/초기화 시 `test_log`·`work_result`를
   **하드삭제 금지 → `archived_at` 소프트삭제(보존)**. 원본의 **barcode 키 하드 연관삭제**(§3.3·§11.2 위험 지적)를
   이식하지 말 것. 조회에 "삭제됨/보관" 필터로 아카이브분 조회 가능.

## Non-Goals (B2B-2 범위 밖 — B2B-3 후속)

- 로그 조회(`/api/logs/input`·`/sort`·`/api-calls`) + Logs 페이지.
- 결과 3-way 비교(`/api/test-data/comparison`) + ResultComparison 페이지.
- 박스 조회(`GET /api/boxes`) + Boxes 페이지(B2B-1에서 이미 B2B-3로 이연 확정).
- Excel export(`/api/logs/export`) + 자동생성/인쇄 **설정 페이지**(Settings). (인쇄 **기능** 자체는 DataGenerator에 포함.)
- 자동생성 전체(`auto_generate_config` 등) — 미이식 확정(불변).
- 실 `TEST_ORDER_DB` 재적용/시드 대조 — orchestrator/사용자 몫(Generator는 SQLite 더블만).

## Scope 결정 — 분할 권장(권장안) + 사용자 게이트 Questions

스코프가 크다(관리 API + 아카이브 마이그레이션[백] + 큰 프론트 페이지 + 토글[프론트] + 프론트 인프라 신규).
아래 4개 novel 결정은 **사용자 확정 게이트**다.

- **Q1 [스코프 분할] — 권장: 2-Phase 순차 (2a 백엔드 → 2b 프론트)**
  - (a) **[권장] 2a → 2b 순차 2스프린트**: `2a` = 관리 API + **아카이브 마이그레이션/서비스**(백엔드, 검증=xUnit) →
    병합 → `2b` = DataGenerator 페이지 + B2C/B2B 토글 + 프론트 인프라(프론트, 검증=tsc/eslint/브라우저).
    근거: 프론트 E2E(Evaluator 브라우저 의무)가 **실 API를 구동**해야 관측 가능 → 백엔드 선행·병합이 자연스럽다.
    B2B-1(백엔드 단독)·FRONTEND.md F1~F3 순차 선례와 정합. 스택 PR 병합 사고(memory) 회피 — 순차 병합이 안전.
  - (b) 단일 스프린트 + **Parallel Modules**(백/프론트 경계로 fan-out, worktree 격리): 계약(`docs/B2B-DATAGEN.md`)
    선고정 후 병렬 개발. 리스크: 프론트 E2E가 미병합 백엔드에 의존 → worktree 병합·스테일 베이스(memory) 리스크↑.
  - **권장 (a)**. 본 계약은 2a·2b 스코프를 모두 정의하되, 확정 시 **Generator는 2a(백엔드)부터 착수**.

- **Q2 [토글 전역상태 방식] — 권장: React Context + localStorage**
  - 현 프론트엔 전역 스토어 없음(main.tsx: QueryClientProvider>BrowserRouter>App).
  - (a) **[권장] React Context**(`UiModeProvider` — 원본 GlobalContext/UiContext 개념) + localStorage 영속(화이트리스트
    가드). 새 의존 0. mode(`b2c`|`b2b`) + B2B용 bizDay/autoRefresh를 함께 보관(B2B-3 재작업 방지).
  - (b) 라우트 기반(`/b2c/*`·`/b2b/*` 접두 — 딥링크·무스토어, 라우팅 재편) · (c) Zustand(신규 의존 — 과설계).

- **Q3 [reset/delete + 아카이브 시맨틱] — 권장: 아래**
  - **reset**: 선택 test_data `ReceiveTime=null`(행 유지) + 연관 `test_log`/`work_result` **archived_at 마킹**.
  - **delete**: 선택 test_data **하드삭제**(등록 원장 제거 — 정당) + 연관 로그/결과 **archived_at 마킹**.
    - test_data 자체를 소프트삭제할지 vs 하드삭제할지 = 이 게이트. **권장: 하드삭제**(원본 동작·로그/결과만 보존).
  - **아카이브 스코핑(원본 결함 교정)**: 원본은 `barcode` 단독 키로 배치 밖까지 하드삭제(§11.2 위험). 교정 →
    선택 행의 **`(BizDay,Batch,Barcode)` 조합 집합**(+ test_log는 `TestDataId in ids` 합집합)으로 스코프 한정.
    → 확정 필요: (권장) 스코프 한정 아카이브 vs 원본식 barcode 전역(비권장).

- **Q4 [아카이브 UI 필터 위치] — 권장: DataGenerator 상세 그리드 토글**
  - (a) **[권장] 상세 그리드에 "보관 포함/보관만"(`active|all|archivedOnly`) 토글** → detail 조회 `archived` 파라미터.
    reset/delete 직후 archived 행을 같은 화면에서 확인. (b) 별도 아카이브 뷰(B2B-3 로그 페이지로 이연).
  - **권장 (a)** — B2B-2 자체에서 아카이브 동작을 눈으로 검증 가능해야 함(사용자 확정 시나리오와 정합).

## Implementation Scope (Generator가 할 일 — WHAT)

### 2a — 백엔드(관리 API + 아카이브) — Q1 확정 시 선행
1. **아카이브 스키마**: `Wcs.Data.B2B.TestLog`·`WorkResult`에 `DateTime? ArchivedAt`(`archived_at` nullable) 추가.
   `WcsDbContext` Configure에 매핑 추가(다른 컬럼·인덱스 무변경).
2. **마이그레이션 2개**(provider별): `test_log`·`work_result`에 `AddColumn(archived_at)`만. **기존 B2C 17테이블·B2B
   다른 5테이블 무변경**. ModelSnapshot diff = 2컬럼 추가로만 국한.
3. **`ITestDataService`/`TestDataService`**(Scoped): `GenerateAsync`(라운드로빈) · `UploadExcelAsync`(신/구양식 판별) ·
   `GetSummaryAsync` · `GetDetailAsync`(로그 조인 + **아카이브 필터**) · `ResetReceiveTimeAsync`(**아카이브**) ·
   `DeleteAsync`(**아카이브**). 알고리즘·실패 message = `docs/B2B-DATAGEN.md §2·§3`. 슈트파서/zero-pad/채번은
   B2B-1 `AppUtils`/`AppConstants` 정합(단일 소스).
4. **`TestDataController`**(`[Route("api/test-data")]`, Controllers/B2B/) — 6 엔드포인트(§1 표). 검증실패 400 형식은
   `InvalidModelStateResponseFactory` 경로 allowlist에 `/api/test-data` **추가**(additive — `/api/v1/works/`·B2C 불변).
5. **DI/패키지**: `AddScoped<ITestDataService,TestDataService>()` append. **`ClosedXML` 패키지 추가**(Wcs.Api). `UploadMaxBytes` 상수 추가.
6. **테스트**: 서비스 단위(라운드로빈·엑셀 신/구양식·summary/detail·**아카이브 스코핑**) + API 통합(6 엔드포인트 계약 +
   **★아카이브 시나리오**). `B2bWebApplicationFactory` 재사용.

### 2b — 프론트(DataGenerator + 토글) — 2a 병합 후
7. **프론트 인프라**: 확인 다이얼로그(shadcn-style Dialog) + 토스트(경량) 신규(원본 UiContext 개념). B2B UI 상태
   Context(mode + bizDay + autoRefresh, Q2). `lib/api.ts`에 test-data 호출 추가(또는 TanStack Query 훅).
8. **DataGenerator 페이지**(`/data-generator`) — `docs/B2B-DATAGEN.md §4`: 3분할 레이아웃, 생성 폼(Enter 제출),
   엑셀 업로드, 요약/상세 그리드, **드래그·Shift·Ctrl 다중선택 + 우클릭 메뉴**, 컬럼 필터, reset/delete(확인 다이얼로그),
   **A4 라벨 인쇄(로컬 JsBarcode·듀얼바코드·XSS 방지 DOM 조립)**, **아카이브 토글(Q4)**.
9. **B2C/B2B 토글**(§5): `Layout.tsx` NAV를 모드별 2세트 + 동적 헤더 타이틀(disabled+phase 배지 재사용). `App.tsx`에
   `/data-generator` 라우트 + 기본 진입 리다이렉트를 활성 모드 기준으로. 기존 B2C 라우트·페이지 동작 보존.
10. **로컬 자산**: `frontend/public/JsBarcode.all.min.js` vendoring(외부 CDN 금지 — 폐쇄망).

> 기술 세부(컴포넌트 분해·훅 구조·트랜잭션 방식)는 Generator 재량. 계약은 형상·완료조건·검증만 고정.

## Done 조건

**공통**
- [ ] `dotnet build backend/Wcs.sln` 신규 경고 0. **기존 전체 테스트 스위트 GREEN 불변**(B2C·B2B-1 회귀 0) + 신규.
- [ ] 무접촉: 기존 B2C 17테이블·엔티티·라우트·페이지 + B2B-1 계약(RCS 5 API·엔티티 형상·기존 마이그레이션) diff 0.

**2a(백엔드)**
- [ ] `archived_at` add-only 마이그레이션 2개(provider별) up 성공 — `test_log`·`work_result`에 컬럼 추가만,
      기존 테이블(B2C·B2B 다른 5) 무변경. ModelSnapshot diff가 2컬럼 추가로만 국한.
- [ ] 6 관리 엔드포인트가 `docs/B2B-DATAGEN.md §1·§2` 계약 부합: generate 라운드로빈·bizDay 정규화, upload 신/구양식
      판별·3중 검증, summary 그룹핑·정렬, detail 로그 조인·정렬·아카이브 필터.
- [ ] **★아카이브**: reset/delete가 연관 `test_log`·`work_result`를 **DELETE 하지 않고** `archived_at` 마킹(하드삭제 0).
      스코프가 선택 `(BizDay,Batch,Barcode)`(+test_log는 TestDataId)로 한정(배치 밖 미영향).

**2b(프론트)**
- [ ] `/data-generator` 렌더 + 생성/업로드/조회/reset/delete/인쇄 플로우 동작. 다중선택(드래그·Shift·Ctrl)·우클릭 메뉴 동작.
- [ ] B2C/B2B 토글로 **메뉴 세트·헤더 타이틀·기본 진입**이 전환. 기존 B2C 페이지 동작 보존. 백엔드 모드 게이트 없음.
- [ ] 아카이브 UI 토글로 archived 행 조회 가능(Q4). 인쇄가 로컬 JsBarcode로 동작(외부 CDN 요청 0).
- [ ] `tsc --noEmit` 0 · `eslint .` 0 · 브라우저 콘솔 error 0.

## 검증 시나리오 (Full-stack)

> **Evaluator는 fresh evidence 의무**: 아래를 직접 실행한 출력으로 판정(캐시·추정 금지).
> baseline 테스트 수는 sprint 시작 시 확정(현재 xUnit `[Fact]`/`[Theory]` 선언 230개; InlineData 전개 실측치로 고정).

**백엔드(2a)**
1. **마이그레이션 무접촉 대조**: 신규 마이그레이션 `Up()`에 `archived_at` `AddColumn`(test_log·work_result)만.
   기존 테이블 `Alter/Drop` 0. 양 provider(SqlServer 콜드스타트 up 가능 시 / Sqlite EnsureCreated) 성공.
2. **관리 API 계약**: generate(라운드로빈 슈트 배분·bizDay 정규화 저장) · upload(5컬럼 신양식/4컬럼 구양식 판별·헤더 자동감지·
   3중 검증 실패 message) · summary(그룹·MAX(receiveTime)·정렬) · detail(INPUT/SORT 조인·TestDataId 우선·정렬).
3. **★아카이브 핵심 시나리오(테스트로 단정)**: test_data 생성 → 관련 test_log/work_result 시드 →
   (a) `reset(ids)`: test_data.receive_time=null, 연관 로그/결과가 **DB에 존재하며** `archived_at != null` +
   `detail?archived=active`엔 미노출·`archived=archivedOnly`엔 노출. (b) `delete(ids)`: test_data 행 제거되되
   연관 로그/결과는 **삭제되지 않고** archived. (c) 스코프: 다른 배치의 동일 barcode 로그는 **미영향**(원본 결함 미재현).
4. **회귀 0**: `dotnet test backend/Wcs.sln` — 기존 스위트 전부 GREEN + 신규 B2B-2 테스트 GREEN.

**프론트(2b)**
5. **페이지 플로우**(Playwright/`.mcp.json`): `/data-generator` 로드 → 생성 폼 제출 → 요약/상세 조회 → 상세 다중선택
   (드래그·Shift·Ctrl) → 우클릭 체크 → delete 확인 다이얼로그 → archived 토글로 보존 확인. 콘솔 error 0.
6. **토글**: B2C↔B2B 전환 시 사이드바 메뉴 세트·헤더 타이틀·기본 진입 경로가 바뀌고, 기존 모니터링/3DS 페이지는 정상.
7. **인쇄·폐쇄망**: 인쇄 팝업이 로컬 `/JsBarcode.all.min.js`만 로드(network 탭 외부 CDN 0), 라벨이 A4 규격으로 렌더.
8. **정적 게이트**: `tsc --noEmit`·`eslint .` 0.

## Risks & Mitigation

- **무접촉 위반(최대 리스크)**: (a) InvalidModelStateResponseFactory 경로 allowlist 확장이 B2C ProblemDetails를 바꾸면 안 됨
  → `/api/test-data` **추가만**(기존 분기 보존)·테스트로 B2C 400 형식 불변 확인. (b) Layout/App 토글 개편이 기존 B2C 라우트
  동작을 바꾸면 안 됨 → 기존 페이지 렌더·라우팅 회귀 확인.
- **아카이브 하드삭제 잔존**: 리팩터 중 `RemoveRange(logs/results)`가 남으면 계약 위반 → 검증3으로 **DB 잔존 단정**.
- **아카이브 스코프 과잉**: 원본 barcode 전역 키를 그대로 이식하면 배치 밖 오염 → `(BizDay,Batch,Barcode)` 한정·검증3(c).
- **ModelSnapshot 오염**: archived_at 추가 시 스냅샷 재생성으로 기존 엔트리 재정렬 위험 → diff가 2컬럼 추가로만 국한 확인.
- **프론트 신규 의존 남발**: 확인 다이얼로그·토스트·날짜입력을 무거운 라이브러리로 도입 금지 → shadcn-style 자작 + native
  `<input type="date">`. JsBarcode는 로컬 vendoring(CDN 금지).
- **프론트 E2E가 실 API 의존**: Q1(a) 순차 채택 시 2a 병합 후 2b — E2E가 실 관리 API를 구동해 관측(권장 근거).

## Planner self-check

- [x] 원본 `TestDataService`(generate/upload/summary/detail/reset/delete)·`TestDataController`·`GenerateRequest` 정독 →
      알고리즘·계약·실패 message를 `docs/B2B-DATAGEN.md §1·§2`로 캡처. 원본은 **참조 전용**, 수정/커밋 없음.
- [x] 원본 하드 연관삭제(barcode 키 `RemoveRange`) 확인 → 사용자 확정 아카이브(archived_at)로 §3 재설계(스코프 한정·교정 포함).
- [x] 원본 `DataGenerator.jsx`(934줄) 정독 → 레이아웃·다중선택(드래그/Shift/Ctrl)·우클릭 메뉴·A4 인쇄(로컬 JsBarcode·
      듀얼바코드·XSS DOM 조립)를 `§4`로 캡처. 우리 스택(React19+TS+Tailwind)으로 재개발 지시(원본 복사 금지).
- [x] 우리 프로젝트 실측: 백엔드 B2B-1 산출물(엔티티·WcsDbContext·마이그레이션·RCS 5 API·B2bWebApplicationFactory·
      경로분기 팩토리·SPA 정적서빙)·프론트 구조(Layout NAV·App Routes·main Provider·`components/ui`·전역 스토어 부재)·
      ClosedXML 부재·라우트 충돌 0 확인 → §5·§6·Implementation Scope에 반영.
- [x] 스코프 분할 판단: 2a(백)→2b(프론트) 순차 권장 + 단일+Parallel Modules 대안 — Q1로 사용자 위임.
- [x] novel 결정 4건(스코프 분할 Q1 · 토글 전역상태 Q2 · reset/delete+아카이브 시맨틱 Q3 · 아카이브 UI 필터 Q4)을
      권장안과 함께 게이트 Question으로. 기술 세부는 Generator 몫.
- [ ] **사용자 확정 대기**: Q1(분할) · Q2(토글 상태) · Q3(reset/delete+아카이브 스코프) · Q4(아카이브 필터 위치).
      확정 후 Generator 착수(Q1(a)면 2a 백엔드부터). Generator/Evaluator는 pwd 밖 접근 금지 —
      `docs/B2B-DATAGEN.md` + `docs/B2B-SCHEMA.md` + `docs/PROGRAM_STRUCTURE.md`가 유일 근거.

── ★ 사용자 확정 (2026-07-08, Phase 1→2 게이트) ──────────────────────────────
Q1 스코프: **2a 백엔드 → 2b 프론트 순차 분할**. **이번 = S-B2B-2a(백엔드만)**: test-data 관리 API
   (generate 수동생성·summary·detail·reset·delete·upload) + archived_at 마이그레이션/서비스 + 아카이브 조회.
   프론트(생성 페이지·B2C/B2B 토글)는 B2B-2b(2a 병합 후).
Q2 토글 전역상태: React Context + localStorage(mode+bizDay+autoRefresh) — B2B-2b에서.
Q3 reset/delete + 아카이브: reset=ReceiveTime 초기화(미처리 복귀)+연관 로그 archived_at / delete=test_data
   하드삭제+연관 로그 archived_at. **아카이브 범위=선택 행의 (BizDay,Batch,Barcode) 집합 + test_log는 TestDataId로**
   (원본 barcode-only 광범위 하드삭제 교정 — 하드삭제 0). 
Q4 아카이브 UI: 상세 조회 active|all|archivedOnly 3상태 필터(2b). 2a에서 API가 이 필터 파라미터 지원.
무접촉: 기존 B2C + B2B-1 계약(RCS 5 API·엔티티 계약 컬럼) 0 변경. archived_at는 test_log·work_result
   add-only ALTER(신규 B2B 테이블·기존 B2C 무접촉). ModelState 팩토리 allowlist에 /api/test-data 추가(additive).
신규 의존성: ClosedXML(엑셀 파싱) — 백엔드. 자동생성/auto-config/preview-chutes 제외(확정).
