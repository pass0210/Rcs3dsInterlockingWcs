[Sprint Contract] — S-B2C-EXCEL-UPLOAD (B2C 작업 데이터 엑셀 업로드 + 업로드용 엑셀 양식/템플릿 제공)

작성: Planner Subagent · 2026-07-26
근거(직접 읽어 확인): docs/B2C-DATAGEN.md, docs/ERD.md(WorkBatch/WcsOrder/OrderItem), 프로젝트 CLAUDE.md(절대규칙),
  기존 B2B 엑셀 업로드 선례(TestDataController.Upload / TestDataService.UploadExcelAsync / AppConstants),
  기존 B2B 엑셀 다운로드 선례(LogController.Export / LogExportService / frontend logs.ts exportLogs·triggerDownload),
  B2C 현행(B2cTestDataService.GenerateAsync / B2cTestDataController / b2cTestData.ts / B2cDataGenPage.tsx),
  tasks/lessons.md, tasks/feedback-archive.md(S-B2C-DATAGEN/FACILITY/UX + Playwright file_upload 교훈).

────────────────────────────────────────────────────────────────────────────
■ Goal
────────────────────────────────────────────────────────────────────────────
B2C(3D 소터) 데이터 생성 페이지에, 파라미터 폼(5-필드)의 **대안 입력 경로**로서 **엑셀 업로드로 작업(오더) 데이터를
직접 업로드**하는 기능을 추가한다. 동시에 사용자가 내려받아 채워 올릴 **표준 엑셀 양식(템플릿)** 을 제공한다.
업로드는 기존 생성과 동일하게 **목적지 미할당 오더/바코드**를 만든다(2a/2b 책임 분리 불변 — 목적지·셀 배정은 설비 관리).
검증(필수 컬럼 누락·형식 오류·중복·상한 초과)은 **Fail-Loud**로 표면화하고, **행별 오류 리포트**를 사용자에게 돌려준다.

이 스프린트는 새 도메인 규칙을 만들지 않는다 — 기존 B2C generate 계약(멱등 upsert·미할당·barcode==orderNo)과
기존 B2B 업로드/다운로드 관용구를 **B2C로 이식·재사용**하는 것이 핵심이다. 절대규칙(#1 PLC 무접촉·#6 필드명·
#7 하드코딩 금지·#8 순수 함수 파싱) 준수. Wcs.PlcGateway·Wcs.Core·HandshakeOrchestrator 무접촉.

────────────────────────────────────────────────────────────────────────────
■ ★ USER GATE (승인 없이는 착수 불가 — 이 스프린트의 crux) ★
────────────────────────────────────────────────────────────────────────────
아래 Q1~Q5는 양식 컬럼·스키마 기입·파서·응답 형상을 좌우한다. **Q1은 BLOCKING**(컬럼 구성을 모르면
템플릿도 파서도 만들 수 없음). Planner 권장안을 명시하되, 사용자 확정 후 Phase 2 진입.

Q1 (BLOCKING · 양식 컬럼 구성 = 핵심). 엑셀 한 행의 단위는?
  · (A) **배치 단위 행** — 파라미터 폼과 동형: [작업일자, 배치명, 차수, 계획수량(생성 개수 N), 바코드접두].
        각 행이 기존 generate 처럼 "{접두}-NN" 오더 N건을 생성. 파일 1개 = 여러 배치.
  · (B) **오더/바코드 단위 행** — [작업일자, 배치명, 차수, 바코드, (선택)계획수량]. 각 행 = 오더 1건(barcode==orderNo).
        임의 실바코드를 직접 올릴 수 있음(접두-NN 자동생성이 아님).
  ▶ Planner 권장: **(B) 오더/바코드 단위 행**. 사용자 요청이 "작업(오더) 데이터를 업로드"·"엑셀 행으로 직접
     업로드"이고, 파라미터 폼(접두 자동생성)과의 **차별점**이 곧 "임의 바코드를 행마다 직접 지정"이기 때문.
     권장 컬럼(헤더 고정·한글):
       | 작업일자(필수) | 배치명(필수) | 차수(선택·기본1) | 바코드(필수·=오더번호) | 계획수량(선택·기본1) |
     스키마 기입: WorkBatch(작업일자·배치명·차수 UQ) → WcsOrder(OrderNo=바코드·GENERAL·**DestinationId=null 미할당**)
       → OrderItem(Barcode=바코드·PlannedQty=계획수량). 목적지/셀 컬럼 **없음**(2b 소관 — 미할당 유지).
     하위 확인: (Q1a) 목적지/셀 컬럼을 양식에 넣어 업로드 시 바로 배정할지? → 권장 **아니오(미할당 유지)**.
              (Q1b) RefNo/RefName(송장·매장) 등 선택 컬럼 필요? → 권장 **불포함(최소 컬럼)**.
  ※ (A) 선택 시: 컬럼=파라미터 폼과 동일, 파서가 각 행마다 GenerateAsync 로직 재호출(재사용 극대화).

Q2 (엑셀 라이브러리). **이미 ClosedXML 0.104.2 가 Wcs.Api.csproj 에 존재**(B2B 업로드/내보내기가 사용 중).
  ▶ Planner 권장: **ClosedXML 재사용**(신규 라이브러리 도입 0). 별도 승인 불요로 판단 — 이견 있으면 지적만.

Q3 (템플릿 = 서버 동적 생성 vs 정적 파일).
  ▶ Planner 권장: **서버 동적 생성 다운로드 엔드포인트**(`GET /api/b2c/test-data/template`) — LogController.Export 관용구
     미러. 헤더 행 + 예시 행 1~2개 + 컬럼 설명(주석 행 또는 별도 "설명" 시트) 포함. **파서와 템플릿이 같은
     코드베이스**라 컬럼 드리프트 0(정적 파일은 파서와 어긋날 위험). 정적 파일 원할 경우만 대안.

Q4 (append vs replace · 부분 실패 정책).
  ▶ Planner 권장: **append(멱등 upsert — generate 와 동일, 같은 배치·바코드 재업로드 시 카운트 불변)** +
     **원자적 전체 거부**: 행 검증 오류가 하나라도 있으면 **커밋 0**(트랜잭션 롤백) + 전체 오류행 리포트 반환.
     이유: 테스트 데이터 정합성 — 절반만 들어간 배치는 재테스트를 오염시킴. (대안: 유효행만 커밋·오류행 skip →
     B2B 현행 관용구지만 B2C 재테스트 특성상 비권장.) **사용자 확정 필요**.

Q5 (상한 · 파일 형식).
  ▶ Planner 권장: 파일 크기 **10MB**(B2B `UploadMaxBytes` 재사용/미러), 사용범위 팽창방어 행/열 상한(B2B
     `UploadMaxRows`/`UploadMaxColumns` 미러), **데이터 행 상한 = 1000**(B2cConstants.GenerateCountMax 재사용·
     한 업로드 오더 총량). 확장자 **.xlsx 전용**(권장 — 신규 양식이라 레거시 .xls 불요. B2B는 .xls도 허용).
     모든 상한은 B2cConstants 상수(하드코딩 금지·절대규칙 #7). **사용자 확정 필요**(행 상한·`.xls` 허용 여부).

────────────────────────────────────────────────────────────────────────────
■ Implementation Scope (Generator 가 만들 것)
────────────────────────────────────────────────────────────────────────────
[백엔드 — Wcs.Api/B2C, 라우트 접두 /api/b2c/test-data 재사용(무충돌)]
1. **업로드 엔드포인트** `POST /api/b2c/test-data/upload` (신규, B2cTestDataController 에 추가):
   · `IFormFile file` 멀티파트 수신. 파일-레벨 3중 검증(파일 없음/0바이트, >크기상한, 확장자·MIME 화이트리스트)은
     컨트롤러가 **400 + Fail** 로 선행(B2B TestDataController.Upload 미러). MIME 화이트리스트/상수는 B2cConstants.
   · 파싱/행검증 실패(구조·행오류·유효행 0·팽창 초과)는 **200 + status "F"** + 오류 리포트(파일레벨 400 과 구분).
2. **파서/생성 서비스** `B2cTestDataService.UploadExcelAsync(Stream, ct)` (신규 인터페이스 메서드):
   · ClosedXML 로 워크북 로드 → 팽창방어(행/열 상한 조기 차단) → 헤더 인식 → **행별 파싱·검증**(순수 검증 로직은
     I/O 무의존 헬퍼로 분리 — 절대규칙 #8 정신, 테스트 가능). Q1 확정 컬럼을 스키마에 기입.
   · 기존 GenerateAsync 의 **배치/오더/아이템 upsert·트랜잭션 구조 재사용**(work_batch UQ 멱등 → wcs_order 미할당
     upsert → order_item INSERT·기존 reserved/sorted 보존). Q4 확정 원자성 정책 적용.
   · 미할당 유지: `DestinationId=null`·`DestAssignType=null`·GENERAL·RUNNING (2a 슬림 계약 불변).
   · 감사: operation_log 카테고리 STATE, action `B2C_UPLOAD`(성공 INFO·실패/거부 WARN — 전수), 행수·배치 기록.
3. **템플릿 엔드포인트** `GET /api/b2c/test-data/template` (신규):
   · ClosedXML 로 헤더 행 + 예시 행 + 컬럼 설명을 담은 .xlsx 를 서버 생성 → `File(bytes, xlsx-mime, fileName)` +
     Content-Disposition (LogController.Export 미러). 컬럼 정의는 파서와 **단일 상수/헬퍼 공유**(드리프트 0).
4. **응답 DTO**: 업로드 결과는 행별 오류를 담도록 확장 — `B2cUploadResponse`(또는 B2cManagementResponse +
   `rowErrors: [{ row:int, message:string }]` 선택 필드). counts = ordersCreated·orderItemsCreated·batches·dataRows.
   기존 B2cManagementResponse 소비처 무영향(additive). DataAnnotations 400 형식 분기는 기존 allowlist(/api/b2c/test-data)
   재사용 — 신규 배선 0.
5. **상수**: B2cConstants 에 업로드 상한 추가(UploadMaxBytes·UploadMaxRows·UploadMaxColumns·데이터행 상한·MIME
   화이트리스트·양식 컬럼 헤더 문자열). 하드코딩 금지(절대규칙 #7).
   · DI: `IB2cTestDataService` 확장(기존 등록 재사용 — 신규 서비스 없음). 마이그레이션 **0**(스키마 무변경 — 기존
     work_batch/wcs_order/order_item 컬럼만 사용).

[프론트 — frontend/src, B2cDataGenPage.tsx 생성 카드 내부]
6. **양식 다운로드 버튼**: `b2cTestData.template()` → blob 다운로드(logs.ts triggerDownload 관용구 재사용/미러).
7. **업로드 UI**: 파일 선택 input(accept=.xlsx) + 업로드 버튼(파일 미선택 시 disabled·업로드 중 로딩) — B2B
   GenerateForm.tsx 업로드 블록 미러. 컬럼 안내 문구(양식과 일치). 성공 시 배치 그리드/디테일 invalidate + 입력 리셋.
8. **결과/오류 피드백**: 성공 토스트("N건 업로드 완료") + 배치 그리드 갱신. 실패 시 토스트 + **행별 오류 목록**
   (row 번호 + 사유)을 렌더(rowErrors 소비). 성공 판정 = res.ok && status==="S"(200 F 오인 금지 — 기존 함정).
9. **클라이언트**: b2cTestData.ts 에 `upload(file)`(FormData·multipart, Content-Type 수동지정 금지) +
   `template()`(blob) 추가. 반환형에 rowErrors 반영.

[문서]
10. docs/B2C-DATAGEN.md 에 업로드/템플릿 계약 섹션 추가(엔드포인트 표·양식 컬럼·검증·원자성·상한 — 확정 게이트 반영).

────────────────────────────────────────────────────────────────────────────
■ Evaluation Criteria (Evaluator 판정 기준 + 가중치)
────────────────────────────────────────────────────────────────────────────
- (30%) **업로드 정확성**: 확정 컬럼(Q1)이 work_batch/wcs_order/order_item 에 정확히 기입되고 **미할당(DestinationId=null)**
  유지. barcode==orderNo 규약·멱등 upsert(재업로드 카운트 불변)·기존 reserved/sorted 보존.
- (25%) **검증·Fail-Loud**: 파일레벨(없음/크기/확장자·MIME) 400, 구조/행오류/유효행0/팽창초과 200 F + **행별 오류
  리포트**. Q4 확정 원자성(전체거부 or 유효행커밋)이 코드·테스트로 실증. 예외 삼킴 0(파싱 실패 명시 F).
- (15%) **템플릿 정합**: 템플릿 다운로드가 파서가 기대하는 헤더와 **정확히 일치**(단일 소스 공유로 드리프트 0).
  헤더+예시+설명 포함. Content-Disposition 파일명·xlsx MIME.
- (15%) **프론트 UX**: 양식 다운로드→채움→업로드→성공/오류 표시 흐름이 실제 브라우저에서 동작. 미선택 disabled·
  로딩·성공 후 그리드 갱신·행오류 렌더. 기존 5-파라미터 폼·초기화 무손상.
- (10%) **경계 준수·회귀 0**: PLC/Core/Handshake 무접촉, 절대규칙 #1·#6·#7·#8 준수, 마이그레이션 0,
  `dotnet test backend/Wcs.sln` 전체 GREEN(기존 테스트 회귀 0). operation_log 감사 기록.
- (5%) **테스트가 스펙**: UploadExcelAsync 순수 파싱/검증 단위테스트(정상·필수누락·형식오류·중복·상한초과·빈파일) +
  API 왕복 테스트(happy·400·200F) + 템플릿 라운드트립(템플릿을 파서에 재투입 시 오류 0).

────────────────────────────────────────────────────────────────────────────
■ Completion Conditions (Evaluator 통과 최소 조건)
────────────────────────────────────────────────────────────────────────────
1. `dotnet build backend/Wcs.sln` 성공 + `dotnet test backend/Wcs.sln` 전체 GREEN(회귀 0).
2. 신규 백엔드 테스트(UploadExcelAsync 단위 + upload/template API) 추가·GREEN. **템플릿→파서 라운드트립** 테스트 존재
   (다운로드한 템플릿을 그대로 업로드하면 예시행이 파싱되거나, 예시행 제거 시 유효행 0 이 정확히 F).
3. 실제 브라우저(Playwright MCP)에서 Web/UI·E2E 시나리오 전 항목 재현 + 스크린샷. ★ file_upload 픽스처는 **프로젝트
   루트 내부**(예: gitignored screenshots/)에 두고 **소문자 드라이브 `c:\`+백슬래시** 경로로 전달(feedback-archive #548).
4. 업로드로 생성된 오더가 `GET /api/b2c/test-data/batches` 새 배치 + `GET /api/b2c/facility/orders?batchId=` **미할당**으로
   실재 확인(E2E 데이터 흐름).
5. 마이그레이션 0·PLC/Core/Handshake diff 0 실증(git diff). 절대규칙 위반 0.
6. docs/B2C-DATAGEN.md 업데이트 반영.

- Parallel Modules: N/A (single module). 백엔드↔프론트가 계약(엔드포인트·응답형상)으로 강결합이고 파일 공유 없이
  분할하기엔 규모가 작아 순차 구현이 적합. 기본 1/1/1.
- Evaluation Dimensions: functional only. 보안/성능 민감면 아님(내부 관리 도구·인증 경계 불변·업로드 상한으로
  DoS 방어). 단, 업로드 팽창(zip-bomb) 방어는 functional 판정에 포함.

- Detected Project Type: **Full-stack**
  (신호: React 업로드 UI(frontend/src/pages/B2cDataGenPage.tsx·lib/b2cTestData.ts) + ASP.NET Core 멀티파트
   업로드/파일 다운로드 엔드포인트(Wcs.Api) + EF Core 기입(Wcs.Data). .mcp.json playwright enabled·headless.)

────────────────────────────────────────────────────────────────────────────
■ Verification Scenarios (Full-stack — 필수)
────────────────────────────────────────────────────────────────────────────
=== Applicable Web/UI scenarios (B2cDataGenPage 생성 카드) ===
- Default state of each surface: 생성 카드 = 기존 5-파라미터 폼 + **신규 "엑셀 업로드" 블록**(양식 다운로드 버튼 +
  파일 선택 input[accept=.xlsx] + 업로드 버튼). 초기 = 파일 미선택 → **업로드 버튼 disabled**, 오류 목록 미표시.
- Each alternate state the sprint introduces:
  · 파일 선택됨 → 업로드 버튼 enabled.  · 업로드 중 → 버튼 "업로드 중…"·disabled.
  · 업로드 성공 → 성공 토스트 + 생성 결과 배치 그리드에 새 배치 출현 + 파일 입력 초기화.
  · 행오류 존재 → 오류행 목록(행번호+사유) 렌더 + 에러 토스트.  · 양식 다운로드 클릭 → .xlsx 다운로드 트리거.
- Relevant empty / error state: (a) 잘못된 파일(비-xlsx/빈 파일/필수 컬럼 누락/형식 오류 행) 업로드 → 에러 토스트 +
  행별 오류 리포트 표면화(Fail-Loud). (b) 데이터 행 0(헤더만) → "유효 데이터 없음" F. (c) 템플릿 다운로드 실패 → 에러 토스트.
- Dark mode variant: **N/A** — B2C 페이지는 단일 라이트 테마(docs/B2C-DATAGEN.md §4 "단일 라이트 테마·다크모드 N/A").
- Key interaction flow after the change: 양식 다운로드 → (양식대로 행 채움) → 파일 선택 → **업로드** → 성공 토스트 +
  "생성 결과 — 최근 배치"에 업로드 배치 표시 + 배치 클릭 시 하단 디테일에 **미할당** 바코드/오더 표시.

=== Applicable Backend/API scenarios ===
- Endpoints touched (method + path):
  · POST /api/b2c/test-data/upload  (신규 · multipart IFormFile)
  · GET  /api/b2c/test-data/template (신규 · .xlsx 다운로드)
  (기존 generate/batches/summary/detail/reset 는 무접촉 — 회귀 대상으로만 재실행.)
- Happy path per endpoint:
  · upload: 유효 .xlsx(M 오더행·K 배치) → 200 { status:"S", message:"…N건 업로드 완료", counts:{ordersCreated,
    orderItemsCreated, batches, dataRows} }. 생성 오더 전부 DestinationId=null(미할당). 재업로드 시 신규 카운트 0(멱등).
  · template: 200 + application/vnd.openxmlformats-officedocument.spreadsheetml.sheet + Content-Disposition
    attachment; filename=…xlsx. 본문 = 헤더+예시+설명. 그 파일을 upload 에 재투입하면 파싱 성공(라운드트립).
- Relevant error cases per endpoint (해당하는 것만 — 패딩 금지):
  · upload 400: 파일 없음/0바이트, 크기 > 상한, 확장자 ≠ 허용, MIME 화이트리스트 불일치(파일레벨 선행 검증).
  · upload 200 F: 구조 오류(필수 헤더 누락)·행별 검증 오류(작업일자 형식·비존재 날짜·빈 바코드·바코드 문자셋·차수
    범위·계획수량 범위)·유효 데이터행 0·사용범위 팽창 초과 → status "F" + rowErrors. (Q4 원자성: 오류 시 커밋 0.)
  · template: 서버 생성 예외 → 400 + Fail(원문 미노출·서버 로그). (기타 4xx는 이 표면에 비해당 — 인증/권한 경계 불변.)

=== End-to-end data-flow scenario (2+ layers) ===
양식 다운로드(GET template) → (행 채움) → 업로드(POST upload, multipart) → Wcs.Api 파서(ClosedXML·행검증) →
Wcs.Data 트랜잭션(work_batch UQ 멱등 → wcs_order 미할당 → order_item) → 프론트가 GET /api/b2c/test-data/batches 로
새 배치·오더수 확인 + GET /api/b2c/facility/orders?batchId= 로 바코드가 **미할당**임을 확인. (연장 확인: 설비 관리(2b)
에서 셀 배정 후 IF-05 가 그 바코드를 정상 라우팅 — 업로드가 기존 파이프라인과 정합함을 실증.) DB↔파서↔UI 3계층 관통.

> Planner self-check — Detected project type: Full-stack. Required scenario slots: 9 (Web/UI: default state, alternate states, empty/error state, dark-mode variant, key interaction flow; Backend/API: endpoints touched, happy path per endpoint, error cases per endpoint; E2E: cross-layer data-flow). All slots filled: yes.

---

## ✅ 확정 결정 (사용자 게이트 — 2026-07-26, S-B2C-EXCEL-UPLOAD)

- **(Q1) 엑셀 행 단위 = 오더/바코드 단위 행** — 한 행 = 실제 오더 1건(사용자가 실제 바코드값 직접 입력·자동생성 아님). 컬럼(권장): 작업일자·배치명·차수·바코드·수량. 목적지 미할당 유지(2b에서 할당). orderNo==barcode 멱등 계약 재사용.
- **(템플릿) 정적 파일 템플릿** — ⚠ 계약 초안의 "GET /api/b2c/test-data/template 동적 생성"이 **아니라 정적 .xlsx 파일**로 제공. Generator가 헤더·예시행·컬럼 설명이 든 .xlsx를 생성(ClosedXML 1회 생성 or 직접)해 **프론트가 다운로드할 정적 자산**(frontend/public/ 또는 Wcs.Api/wwwroot)으로 커밋. 프론트 "양식 다운로드" 버튼은 이 정적 파일 링크. (동적 엔드포인트 미구현.)
- **(Q4) 멱등 append + 오류 시 전체 거부(atomic)** — 기존 데이터에 추가(orderNo==barcode upsert), 한 행이라도 검증 실패 시 전체 롤백 + 행별 오류 리포트 반환.
- **(Q5) 기본 제한 · .xlsx만** — B2B 동일 제한(AppConstants 최대 바이트/행/열·zip-bomb 가드), .xls 거부. 최대 행수는 기존 생성 상한(1000)에 맞춤.
- **(Q2) ClosedXML(이미 존재) 사용** — 신규 라이브러리 도입 없음.

**스코프 조정**: 백엔드 = 업로드 엔드포인트(POST /api/b2c/test-data/upload)만(동적 템플릿 엔드포인트 제외) + 정적 템플릿 .xlsx 자산. 프론트 = B2cDataGenPage 업로드 UI + 정적 양식 다운로드 버튼.
