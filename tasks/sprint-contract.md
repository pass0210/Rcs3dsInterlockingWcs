# Sprint Contract — S-B2B-1 (B2B 신규 스키마 + RCS 5개 API 이식)

> Branch: `feat/b2b-1-schema-api` · Base: `develop`
> 작성: Planner Subagent · 2026-07-08
> 스펙 근거(정본): `docs/B2B-SCHEMA.md`(신규 캡처) + `docs/api-spec-ko.html`(와이어 계약·실패 message) +
> `docs/PROGRAM_STRUCTURE.md`(§2 API계약·§3 알고리즘·§9 데이터개념) + `docs/wcs_rcs_interface_kr.html`(B2B 파트).

## Goal

원본 `BowooTestBatchSystem_v2`의 B2B(작업 테스트 데이터) 백엔드를 이 프로젝트로 **완전 분리 이식**한다.
B2B-1은 그 **기반 층**: 신규 6테이블 스키마 + 양 provider add-only 마이그레이션 + RCS 5개 API(엔드포인트·
서비스 로직) + 테스트. 기존 B2C(WCS↔RCS↔3DS) 17테이블·엔티티·코드·라우트는 **0 변경(무접촉)**.

## 확정 방침 (사용자 — 불변)

1. **완전 분리·무접촉**: 기존 17테이블·엔티티·코드·라우트·마이그레이션 **0 변경**. 신규 B2B만 추가. 안정성 최우선.
2. **신규 테이블 6개**: `test_data · test_log · work_result · box · box_item · api_call_log`
   (**`auto_generate_config` 제외** — 자동생성 미이식). 박스도 B2B 전용 신규.
3. **마이그레이션**: `Wcs.Migrations.SqlServer` / `Wcs.Migrations.Sqlite` 각각에 B2B 테이블 **추가만**(기존 ALTER 0).
4. **API 5개**(`api-spec-ko.html` 와이어 계약대로): unprocessed(GET·부수효과 ReceiveTime 마킹·자동생성 트리거 없음·
   0건=`[]`) · input · classification · results(최상위 JSON 배열) · box.
   - 응답 `{status:"S"|"F", message}`. **실패 message verbatim**(`docs/B2B-SCHEMA.md §4` = `api-spec-ko.html` 정본).
   - `pId·inductionNo` = RCS 자체생성 정수, **서버 미검증·그대로 저장**. chuteNo 3자리 zero-pad.
   - 범위: bizDay 8~10 · batch 1~10 · qty 1~9999.
   - **HTTP 코드**: 비즈니스 실패=200+F / 검증 실패=400 / 예외=500.
5. **비즈니스 로직**(`docs/B2B-SCHEMA.md §6`): qty 묶음(가용<qty 전량거부·Take N) · unprocessed 그룹핑
   (Batch→Barcode+ChuteNo, qty=COUNT, ReceiveTime 일괄 마킹) · results 사전 존재검증(전체거부·chuteNo 미검증) ·
   box (bizDay,batch,boxNo) 중복거부·barcode 미검증.

## Non-Goals (B2B-1 범위 밖 — 후속)

- `auto_generate_config` 및 자동생성 로직 전체(미이식 확정).
- 프론트 전용 조회 API: TestData 관리 GET · logs · comparison · **`GET /api/boxes`** · excel export → **B2B-2/3(프론트)**.
- 전역 인프라: rate limiter · CORS · OpenAPI · 전역 예외 미들웨어 · HealthChecks 확장(기존 `/health` 유지) → 필요 시 후속.
- 실 `TEST_ORDER_DB` 재적용/시드 대조 → **orchestrator/사용자 몫**(Generator는 SQLite 테스트 더블만).

## Scope 결정 — B2B-1 경계 확정(권장안) + 사용자 게이트 Questions

Planner 권장으로 아래를 **B2B-1에 포함**하되, D2·D4·D5는 사용자 확정 게이트다.

**포함(권장 확정)**
- 6엔티티(§2) + `WcsDbContext` DbSet/Configure 추가 + provider별 add-only 마이그레이션 2개.
- `IWorkService/WorkService`, `IBoxService/BoxService`, 5개 컨트롤러(`api/v1/works/*`).
- B2B 전용 `ApiResponse`·요청 DTO·`AppUtils.NormalizeBizDay`·`AppConstants`(D3 포맷·qty 9999) 신규 이식.
- xUnit 테스트(단위: 서비스 로직 / 통합: 5 API 계약, SQLite 더블).

**게이트 Questions (novel 결정 — 사용자 확정 필요)**

- **Q1 [D1] api_call_log 채우는 방식**
  - (a) 원본 `RcsApiLoggingMiddleware`+`ApiCallLogQueue`(백그라운드 writer)를 이식하되 **경로를 `/api/v1/works/`로 한정**.
  - (b) 테이블만 만들고 채우기는 기존 `operation_log`/Serilog로 대체(미들웨어 미이식).
  - **권장 (a)**: api_call_log는 요청/응답 원문·duration·client_ip·http_status를 담는 **API 와이어 감사** 전용 테이블로,
    도메인 관측 스트림인 operation_log와 목적이 다르고 후속 프론트(B2B-2/3)가 이 테이블을 조회한다. 단 **반드시 경로 한정**
    (전 `/api/v1/` 접두 이식 시 기존 RCS 엔드포인트까지 기록 → 무접촉 위반). 큐 헬스체크는 이식 제외(우리 /health 유지).

- **Q2 [D2] 프론트용 `GET /api/boxes` 포함 여부**
  - (a) B2B-1 제외(BoxController는 POST `api/v1/works/box`만) — 조회는 프론트 스프린트와 함께.
  - (b) BoxService.GetBoxesAsync가 사소하니 함께 포함.
  - **권장 (a)**: B2B-1 = "RCS 수집(ingestion) 계층"으로 경계를 깔끔히. 조회 표면은 프론트와 함께 검증하는 게 자연스럽다.

- **Q3 [D3] 네이밍·ERD 원칙 적용도**
  - 테이블/컬럼명은 **원본 그대로 snake_case**(우리 컨벤션과 동일 — 충돌 없음, §7 확인). id `long ValueGeneratedOnAdd`.
  - `created_at`: 원본 `DateTime.Now`(로컬) vs 우리 ERD 원칙 **UTC**. **권장: `DateTime.UtcNow`**로 우리 ERD 정합.
    (단 log_time/receive_time은 클라이언트 inTime/sortTime 파싱값 — 원문 의미 보존.)
  - `box_item→box` FK: 원본 CASCADE. 단일 캐스케이드 경로라 1785 위험 없음 → **권장: CASCADE 유지**(원본 동작 보존).
  - test_log.test_data_id: **FK 없이 인덱스만**(원본 동일·1785 회피·이력 불변). CHECK 제약은 원본에 없으므로 미도입
    (검증은 DataAnnotations). — **사용자 확정 필요: created_at UTC vs 원본 로컬**.

- **Q4 [D4] api_call_log(및 400 형식) — 아래 D5와 묶어 Q1과 함께 확정**

- **Q5 [D5] 검증실패 400 형식 & ArgumentException 처리 (무접촉 필수)**
  - 원본은 **전역** `InvalidModelStateResponseFactory` + 전역 `GlobalExceptionMiddleware`로 400/500을 `ApiResponse` 형식화.
    우리 프로젝트엔 없고, **전역 도입 시 기존 컨트롤러 400/500 형식이 바뀐다(동작 변경 → 무접촉 위반)**.
  - **권장**: (i) `InvalidModelStateResponseFactory`를 **경로 분기**로 등록 — `/api/v1/works/`면 `ApiResponse.Fail(firstError)`,
    그 외는 기존 기본(ProblemDetails) 유지. (ii) `NormalizeBizDay`의 `ArgumentException`→400은 **B2B 컨트롤러/서비스 국소 try/catch**로.
    전역 미들웨어·전역 팩토리 무조건 교체는 금지. — **사용자 확정 필요**.

> Scope 분할 판단: B2B-1은 위 경계(6테이블+마이그레이션+5 API+로직+테스트, 프론트/자동생성/전역인프라 제외)로
> **단일 스프린트에 적정**하다(추가 분할 불요). api_call_log 미들웨어 이식이 부담되면 D1을 (b)로 축소해 더 줄일 수 있음.

## Implementation Scope (Generator가 할 일 — WHAT)

1. **엔티티 6개** 신규(`docs/B2B-SCHEMA.md §1·§2` 형상 그대로) — B2B 전용 네임스페이스.
2. **`WcsDbContext`에 DbSet 6 + Configure 메서드 6 추가**(ToTable/인덱스/FK/동시성 없음 — append 위주).
   기존 17테이블 Configure 및 ModelSnapshot 기존 엔트리 **diff 0**.
3. **마이그레이션 2개**(provider별) — B2B 6테이블 CreateTable + 인덱스(UNIQUE `IX_box_biz_day_batch_box_no` 포함)만.
   기존 테이블 ALTER 0.
4. **서비스**: WorkService(unprocessed/input/classification/results — **auto-gen 블록 제거**), BoxService(§6 로직).
5. **컨트롤러 5개** + DTO + ApiResponse + AppUtils/AppConstants. 실패 message verbatim(§4).
6. **DI/Program.cs 배선**(최소·경로 한정 — Q1/D5 확정 반영). 기존 배선·미들웨어 순서 보존.
7. **테스트**: DepositDecider 스타일 순수 서비스 단위 테스트(qty 묶음·그룹핑·존재검증·chute 매칭·중복거부) +
   API 통합 테스트(SQLite 더블, 5 엔드포인트 계약·부수효과·실패 message·results 배열·pId 미검증 저장).

> 기술 세부(코드 구조·헬퍼 분해·트랜잭션 방식)는 Generator 재량. 계약은 **형상·완료조건·검증**만 고정.

## Done 조건

- [ ] B2B 6엔티티 + WcsDbContext 배선. 기존 ModelSnapshot 기존 엔트리 변경 0.
- [ ] `Wcs.Migrations.SqlServer`/`Wcs.Migrations.Sqlite` add-only 마이그레이션 2개 — 양 provider **up 성공**,
      기존 테이블 스키마 **무변경**(신규 CreateTable만).
- [ ] 5개 RCS API가 `docs/B2B-SCHEMA.md §3` 계약 부합: 라우트·요청/응답·부수효과(unprocessed ReceiveTime 마킹·
      0건 `[]`·자동생성 없음)·results 최상위 배열·pId 미검증 저장·chuteNo 3자리.
- [ ] 실패 message **verbatim**(§4) — 서비스 방출 문자열이 정본과 byte-for-byte 일치(테스트로 고정).
- [ ] 비즈니스 로직: qty 묶음(가용<qty 전량거부) · unprocessed 그룹핑 · results 사전 존재검증(전체거부·chuteNo 미검증) ·
      box 중복거부(barcode 미검증).
- [ ] HTTP 코드: 비즈니스 실패 200+F / 검증실패 400 / 예외 500. 기존 엔드포인트 400/500 형식 **불변**(무접촉).
- [ ] **기존 전체 테스트 스위트 GREEN 불변**(B2C 회귀 0) + 신규 B2B 테스트 GREEN.
- [ ] `dotnet build backend/Wcs.sln` 경고 0(기존 기준 대비 신규 경고 0).

## 검증 시나리오 (Backend/API — 표면 백엔드)

> **Evaluator는 fresh evidence 의무**: 아래를 직접 실행한 출력으로 판정(캐시·추정 금지).

1. **마이그레이션 무접촉 대조**: 신규 마이그레이션의 `Up()`에 기존 17테이블 관련 `AlterTable/AlterColumn/DropXxx`
   **0건**임을 diff로 확인. `WcsDbContextModelSnapshot` 변경이 B2B 6테이블 추가로만 국한.
2. **양 provider up**: SqlServer/Sqlite 각각 `dotnet ef database update`(또는 통합 테스트 EnsureCreated) 성공.
   SQL Server는 빈 DB 콜드스타트 up 검증(가능 환경). SQLite in-memory 더블로 통합 테스트 기동.
3. **5 API 계약 부합**: 통합 테스트로 성공/실패 경로 — 특히 (a) unprocessed 부수효과(2회 호출 시 2회차 빈 배열) +
   0건 `[]`, (b) input/classification qty 묶음·가용부족 전량거부, (c) classification chute mismatch/이미분류,
   (d) results 최상위 배열·존재검증 전체거부·chuteNo 미검증 저장, (e) box (bizDay,batch,boxNo) 중복거부,
   (f) pId·inductionNo 미검증 그대로 저장.
4. **실패 message verbatim**: 각 F 경로 응답 message가 §4와 정확히 일치.
5. **HTTP 코드**: 검증실패(잘못된 status·qty 범위·bizDay 형식) 400, 비즈니스 실패 200+F, NormalizeBizDay 비존재날짜 처리.
6. **회귀 0**: `dotnet test backend/Wcs.sln` — 기존 스위트 전부 GREEN(수치는 §Planner self-check로 sprint 시작 시 baseline 고정).
7. **실 TEST_ORDER_DB 대조는 이 계약 범위 밖**(orchestrator/사용자). Generator는 SQLite 더블만.

## Risks & Mitigation

- **무접촉 위반(최대 리스크)**: 전역 미들웨어/팩토리/rate limiter 도입이 기존 엔드포인트 동작을 바꿀 수 있음.
  → D1·D5 권장(경로 한정·국소 처리) 준수. 마이그레이션 add-only diff 검사(검증1).
- **ModelSnapshot 오염**: 단일 `WcsDbContext`에 B2B 추가 시 스냅샷 재생성으로 기존 엔트리가 재정렬/변경될 위험.
  → add-only migration 생성 후 스냅샷 diff가 B2B 테이블 추가로만 국한되는지 확인.
- **실패 message 표류**: 리팩터로 문자열이 미세 변경되면 계약 위반. → 테스트로 문자열 고정(검증4).
- **`/health` 라우트 충돌**: 원본 HealthChecks 이식 금지(우리 MapGet 유지). §7 확인.

## Planner self-check

- [x] 원본 DB 스키마를 **INFORMATION_SCHEMA/sys로 실측 덤프**(컬럼·타입·NULL·기본값·PK/UNIQUE 인덱스·FK·identity) →
      `docs/B2B-SCHEMA.md §1`로 캡처. CHECK 제약 0건 확인.
- [x] 원본 엔티티·서비스(WorkService/BoxService)·DTO·컨트롤러·유틸·미들웨어·Program.cs 정독 → §2·§3·§5·§6·§7 캡처.
      원본은 **참조 전용**으로만 열람, 수정/커밋 없음.
- [x] 실패 message 정본을 `api-spec-ko.html`에서 추출 → §4에 19종 정리(자리표시자 포함).
- [x] 우리 프로젝트 충돌 실측: 테이블명 충돌 0 / 라우트 충돌 0 / `/health` 충돌 有(이식 금지) /
      미들웨어·400 팩토리 전역성 위험 식별 → §7·D1·D5.
- [x] dual-provider 마이그레이션 구조·테스트 하네스(FakeModbusWebApplicationFactory + EnsureCreated + SQLite) 확인.
- [x] 스코프 경계 확정(6테이블+마이그레이션+5 API+로직+테스트) + Non-Goals 명시 + 게이트 Q1~Q5로 novel 결정 사용자에 위임.
- [ ] **사용자 확정 대기**: Q1(api_call_log 방식) · Q3(created_at UTC vs 로컬) · Q5(400/예외 처리 경로 한정 방식).
      확정 후 Generator 착수. (Generator/Evaluator는 pwd 밖 접근 금지 — `docs/B2B-SCHEMA.md`가 유일 근거.)

── ★ 사용자 확정 (2026-07-08, Phase 1→2 게이트) ──────────────────────────────
Q1 [D1] api_call_log: **미들웨어 이식(경로 /api/v1/works/ 한정)** — RcsApiLoggingMiddleware+ApiCallLogQueue+
   BackgroundWriter 이식하되 /api/v1/works/ 접두만 기록(기존 RCS destination-query 등 미기록 = 무접촉). 큐 헬스체크 미이식.
Q2 [D2] GET /api/boxes: **제외** — B2B-1은 수집 계층(POST works/*)만. 조회는 B2B-3(프론트).
Q3 [D3] created_at: **원본 로컬타임(DateTime.Now)** — 원본 BowooTestBatchSystem_v2 동작 보존.
   ⚠ 기존 B2C 테이블은 UTC이나 B2B는 로컬 — 분리 테이블이라 무방하되 코드/스키마 주석에 "B2B는 원본 호환 로컬타임(B2C UTC와 상이)" 명기.
   (log_time/receive_time은 클라이언트 inTime/sortTime 파싱값 — 원문 의미 보존.)
Q5 [D5] 400/예외: **경로 한정·국소** — InvalidModelStateResponseFactory 경로 분기(/api/v1/works/만 ApiResponse.Fail,
   그 외 기존 ProblemDetails 유지) + ArgumentException은 B2B 국소 try/catch. 전역 미들웨어/팩토리 무조건 교체 금지.
box_item→box FK: CASCADE 유지(단일 경로·1785 위험 없음). test_log.test_data_id: FK 없이 인덱스만(원본 동일).
실행: 단일 Generator(백엔드). 기존 B2C 무접촉·경로한정이 최대 리스크 — Evaluator가 무접촉 diff·회귀 중점 검증.
