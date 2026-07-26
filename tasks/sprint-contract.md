[Sprint Contract] — S-B2C-DATAGEN-UPLOAD (통합: 좌측 폼 콤팩트화 + 엑셀 업로드 오더-컬럼 도입)

════════════════════════════════════════════════════════════════════════════
- Goal:
  두 개의 확정 작업을 한 스프린트로 완료한다.
  (A) 프론트 레이아웃 콤팩트화 — B2C 데이터 생성 페이지(frontend/src/pages/B2cDataGenPage.tsx)의
      좌측 생성 카드 안 "엑셀 업로드 블록"을 접기/펼치기(disclosure, 기본 접힘) 로 감싸 좌측 폼의
      자연높이를 줄인다. 그 결과 하단 "배치 상세" 그리드가 헤더만이 아니라 **오더 행을 실제로
      표시**하게 만든다. S-UI-LAYOUT-FIX 폼 오버랩 회귀는 절대 금지.
  (B) 엑셀 업로드 오더-컬럼 도입 — 업로드 경로를 orderNo==barcode(1행=1오더=1바코드) 강제에서
      **1 오더 : N 바코드** 로 바꾼다. 엑셀에 "오더번호" 컬럼을 바코드 앞에 신설(총 6열).
      같은 (작업일자·배치명·차수·오더번호) 행들을 하나의 WcsOrder 로 묶고, 각 행의 바코드를 그
      오더의 order_item(planned_qty=행 수량)으로 만든다. 셀 할당은 오더 단위(기존 모델 그대로),
      업로드는 목적지 미할당 유지. 배치 내 바코드 유일. Q4 원자성·멱등 append 보존. 6열 양식 파일
      재생성. **생성 폼(GenerateAsync 5-파라미터)은 변경하지 않는다** — 업로드 경로 한정.

  ⚠ DB 스키마 변경 0 (마이그레이션 0). 데이터 모델(wcs_order 1:N order_item, cell_assignment 는
     OrderId 단위)은 이미 1오더:N바코드를 지원한다 — 업로드 파싱/그룹핑만 바꾼다.

════════════════════════════════════════════════════════════════════════════
- Implementation Scope (파일별 · A/B 구분):

  [A · 프론트 레이아웃 — frontend/src/pages/B2cDataGenPage.tsx]
  A1. B2cExcelUpload 컴포넌트(현 491~566)를 disclosure 로 재구성:
      · 헤더 행(항상 표시): "엑셀 업로드" 라벨 + 접기/펼치기 토글 컨트롤. "양식 다운로드" 링크는
        접힘 상태에서도 접근 가능하도록 배치(헤더에 유지 권장).
      · 본문(파일 선택 input + 업로드 버튼 + 안내 문구 + 행오류 목록)은 토글로 펼칠 때만 렌더/표시.
      · **기본 접힘**(useState 초기값 false). 신규 공용 UI 컴포넌트 생성 금지 —
        components/ui 에 disclosure 없음. 이 파일 내 **로컬 구현**(버튼 + 조건부 표시).
      · 접근성: 토글은 <button type="button"> · aria-expanded={open} · aria-controls 로 본문 연결 ·
        키보드 Enter/Space 로 토글(native button 이면 기본 동작으로 충족). 회전 chevron 등 시각 표시.
      · 스페이싱 정리로 접힘 시 좌측 폼 자연높이 최소화(mt-4/pt-4/gap 등 콤팩트화 — 과함 금지).
  A2. 상단/하단 flex 배치 골격은 유지. 좌측 Card 의 self-start 자연높이 · 상단 grid min-h-0 ·
      하단 상세 min-h-0 flex-1 · <main> overflow-auto 계약은 그대로 둔다(레이아웃 주석 160~180·
      316~321 은 회귀 계약 — 삭제·약화 금지, disclosure 도입 사실을 반영해 주석만 갱신 가능).
  A3. 업로드 안내 문구(현 546~549) 갱신: 컬럼 "작업일자·배치명·차수·**오더번호**·바코드·수량",
      "한 오더에 여러 바코드(1 오더:N)" 사용법 · 미할당 · 멱등 · 원자성 문구 반영.
  A4. B2cExcelUpload 상단 설명 주석(현 488~490 "행 = 오더/바코드 1건")을 새 시맨틱으로 정정.
  ⚠ B2cGenerateForm(402~486) 및 그 안내 문구(474 "오더번호 = 바코드")는 **무접촉**(생성 폼 1:1 불변).

  [B · 백엔드 파싱/그룹핑 — backend/src/Wcs.Api/B2C/B2cTestDataService.cs]
  B1. ValidateUploadRows(순수 함수): OrderNo 파싱·검증 추가.
      · OrderNo 필수 + 안전문자(바코드와 동일 규칙 재사용 또는 전용 상수) 검증.
      · 배치 내 바코드 유일: 중복 판정 키는 **(작업일자·배치명·차수·바코드)** 유지
        (이 키가 "다른 오더가 같은 바코드" + "같은 오더에 같은 바코드 반복" 을 모두 잡는다).
        OrderNo 는 유일성 키에 넣지 않는다(같은 오더가 여러 행에 정당하게 반복되므로).
      · 파싱 결과 ParsedRow 에 OrderNo·Barcode 를 별도로 담는다.
      · 한 행 복수 사유는 기존대로 공백 결합 1개 RowError.
  B2. UploadExcelAsync(영속화): 그룹핑을 2단으로.
      · 1단 (작업일자·배치명·차수) → work_batch upsert(멱등, UQ(work_date,batch_no,wave_no)).
      · 2단 OrderNo 별 → WcsOrder upsert(멱등, UQ(WorkBatchId,OrderNo), 미할당 유지
        DestinationId=null·DestAssignType=null·RUNNING·GENERAL).
      · 3단 각 행 → order_item(Barcode=행 바코드, PlannedQty=행 수량) INSERT 만
        (기존 (OrderId,Barcode) 는 스킵 — reserved/sorted 실적 보존).
      · Q4 원자성 유지(행오류 하나라도 → 커밋 0, 트랜잭션 진입 전 조기 반환).
      · counts: ordersCreated(=신규 distinct 오더)·orderItemsCreated(=신규 바코드)·batches·dataRows.
        성공 메시지의 "오더 신규/항목 신규" 의미가 1:N 을 반영하도록 문구 점검.
      · (설계 판단) 배치 내 바코드 유일은 파일 내 검증이 정본. 재업로드 시 **다른** 오더가 기존
        배치 바코드를 재사용하는 교차-업로드 충돌 처리 여부는 Generator 가 결정(최소한 파일 내
        (배치·바코드) 중복은 반드시 행오류). 결정 사항을 docs 에 명시.

  [B · 헤더 상수/DTO — backend/src/Wcs.Api/B2C/B2cTestDataDtos.cs]
  B3. B2cConstants 헤더 상수 6열화: HdrOrderNo(예 "오더번호") 신설, 파싱 순서
      [작업일자][배치명][차수][오더번호][바코드][수량]. UploadHeaderMismatch 메시지 문구도 6열로.
  B4. B2cUploadRawRow / B2cUploadParsedRow 에 OrderNo 필드 추가(위치기반 6열 반영).
      OrderNo 안전문자 상수(UploadOrderNoRegex) 필요 시 추가(바코드 규칙 재사용 가능).
  B5. (선택·craft) 코드리뷰 후속 minor 동시 정리 가능(같은 파일 국소): batchNo 길이 100 상수화 ·
      DefaultPlannedQty 상수 추가 · zip-bomb 계약 문구 "materialize 전 캡" → 실동작 정정.
      과확장 금지 — 스코프 파일 내에서만, 위험 0 인 것만.

  [B · 컨트롤러 — backend/src/Wcs.Api/Controllers/B2C/B2cTestDataController.cs]
  B6. 조사 결과 컨트롤러 Upload 는 파일 레벨 검증(없음/0바이트/크기/확장자/MIME)만 수행하고
      컬럼 파싱을 하지 않는다 → **무변경 예상**. 헤더 문자열·컬럼 수에 의존하는 코드 없음(확인 완료).
      만약 변경이 필요해지면 그 사유를 sprint-log 에 명시(기본 가정: 0 diff).

  [B · 양식 파일 — frontend/public/b2c-order-upload-template.xlsx]
  B7. 6열로 **프로그램적 재생성**(바이너리 xlsx). 헤더 문자열은 B2cConstants.Hdr* 와 **정확히 일치**
      (위치기반 파싱 · 드리프트 0). 예시 행에 "**한 오더에 바코드 2건 이상**" 케이스 포함
      (예: 같은 오더번호 2행, 서로 다른 바코드). "설명" 시트가 있으면 6열로 갱신(오더번호 컬럼 설명 +
      1오더:N바코드 사용법 + 미할당·멱등·원자성·최대 행수).
      · 생성 경위 조사 결과: repo 에 **커밋된 양식 생성 스크립트 없음**(tools/·scripts/·*.csx 부재.
        기존 양식은 S-B2C-EXCEL-UPLOAD 커밋 e6afe05 에서 일회성 openpyxl/ClosedXML 로 생성된 것으로
        확인). → Generator 가 ClosedXML(백엔드에 이미 의존) 로 일회성 재생성한다(테스트 헬퍼 BuildXlsx
        패턴 재사용 가능). 재생성 방법을 sprint-log 에 남긴다. 헤더 정합은 라운드트립 테스트가 잠근다.

  [B · 테스트 — backend/tests/Wcs.Tests/B2C/B2cUploadTests.cs]
  B8. BuildXlsx/Xlsx 헬퍼 헤더를 6열(오더번호 포함)로, 모든 데이터 행을 6열로 갱신.
      추가/갱신 테스트(최소):
      · ValidateUploadRows 순수 단위: OrderNo 필수·안전문자, 배치 내 (배치·바코드) 중복 = 행오류,
        같은 오더 같은 바코드 반복 = 중복오류, 서로 다른 오더가 같은 바코드 = 배치내 중복오류,
        1 오더:N 바코드 정상 파싱(오더 1·item N).
      · UploadExcelAsync 서비스: 1 오더 2 바코드 xlsx → ordersCreated=1·orderItemsCreated=2 ·
        각 order_item.planned_qty=행 수량 · 미할당(DestinationId=null) · orderNo≠barcode 케이스 실증.
      · 원자성: 배치 내 바코드 중복 파일 → 200 F · rowErrors · 커밋 0.
      · 멱등: 재업로드 신규 0 · 기존 reserved/sorted 보존.
      · 정적양식 라운드트립(StaticTemplate_RoundTrips_ThroughParser): 새 6열 양식 재투입 → S ·
        예시행 단언을 새 예시 바코드/오더번호로 갱신(1오더:N 예시행 커버).
      · API 왕복(multipart): happy 200 S · batches 반영 · 배치내바코드중복 200 F.

  [B · 문서 — docs/B2C-DATAGEN.md]
  B9. §7 업로드 스펙 갱신: §7.1 컬럼표 6열화(오더번호 행 추가·바코드↔오더번호 분리·배치 그룹핑 =
      (작업일자·배치명·차수)·오더 그룹핑 = +오더번호), §7 서문 "한 행 = 오더/바코드 1건" → "1 오더:N
      바코드", §7.3 양식 예시행 설명 갱신, §7.5 검증 갱신, 배치내 바코드 유일 규칙 명시.

  [무접촉 경계 — diff 0 이어야 함]
  · GenerateAsync / BuildOrderNumbers(생성 폼 5-파라미터 1:1) · ResetAsync/초기화 · GetBatches/
    GetSummary/GetDetail 조회 · 마스터/디테일 그리드 조회 경로 · Wcs.Core · Wcs.PlcGateway ·
    HandshakeOrchestrator · 마이그레이션/스키마 · CLAUDE.md · tasks/workflow-*.md.
  · frontend/src/lib/b2cTestData.ts 는 파일만 POST 하므로 **무변경 예상**(B2C_UPLOAD_TEMPLATE_URL
    파일명 불변). 변경 필요 시 사유를 sprint-log 에 명시.

════════════════════════════════════════════════════════════════════════════
- Evaluation Criteria (가중치):
  1. 업로드 오더-그룹핑 정확성 (★★★, B) — 1 오더:N 바코드 그룹핑, 배치 내 바코드 유일, orderNo≠
     barcode 케이스, planned_qty=행 수량, 미할당 유지. 순수 ValidateUploadRows 가 스펙.
  2. 프론트 레이아웃 정합 (★★★, A) — 접힘(기본) 상태에서 하단 상세 오더 행 실제 표시, 3뷰포트,
     폼 오버랩 0. disclosure 접근성(aria-expanded·키보드).
  3. 회귀 안전 (★★) — S-UI-LAYOUT-FIX 폼 오버랩 회귀 0 · 업로드 원자성(Q4) · 멱등 append ·
     **생성 폼 불변** · 마이그레이션 0 · 무접촉 경계 diff 0.
  4. Craft (★★) — 콘솔 청결(React dev-warning·pageerror 0) · 회귀 계약 주석 보존/정정 ·
     하드코딩 금지(헤더·상한 상수화 · 절대규칙 #6/#7) · 순수함수 분리(#8) · 양식↔파서 헤더 단일 소스.
  5. Scope 준수 — 명시 스코프 파일로 한정, 무접촉 영역 손대지 않음.

════════════════════════════════════════════════════════════════════════════
- Completion Conditions (전부 충족해야 PASS):
  C1. `dotnet test backend/Wcs.sln` 전량 GREEN(신규 오더-컬럼/1:N/배치내바코드유일 테스트 포함) —
      Evaluator 독립 재실행. ValidateUploadRows 는 순수함수라 단위테스트가 스펙(절대규칙 #8).
  C2. frontend tsc / lint / build digit-exact 0 신규 에러·경고(Evaluator 독립 실행).
  C3. 뷰포트 700 / 900 / 1080px 각각(Playwright 실측):
      · 900 / 1080px 기본(접힘) 상태 → 하단 "배치 상세" 그리드에 오더 행이 **페이지 스크롤 없이
        in-place 로** 실제 렌더(헤더만이 아님) · 폼 오버랩 0.
      · 700px 기본(접힘) 상태 → 하단 "배치 상세" 오더 행이 **페이지 스크롤(main overflow-auto)로
        도달 가능**하면 PASS. **사용자 결정(2026-07-26): 짧은 창에서 페이지 스크롤 허용** — 폼이
        접혀도 ~540px 라 700px 에서 상세를 in-place 노출하는 것은 물리적으로 불가(폼은 계약상
        무접촉). 상세 Card 에 최소 높이 하한을 줘 700px 에서 <main> overflow-auto 가 페이지 스크롤로
        오더 행에 도달하게 한다(900/1080 은 하한 비활성·in-place 무변경). 폼 오버랩 0 은 불변 요건.
      · 펼침 상태(전 뷰포트) → 폼 오버랩 0. (짧은 뷰포트 펼침 시 콘텐츠는 위 페이지-스크롤
        에스컬레이션으로 도달 가능 — 오버랩 0 이 불변 요건, 이 메커니즘 허용.)
  C4. 엑셀 접기/펼치기 동작 실증: 토글 클릭 + 키보드(Enter/Space) 로 열림/닫힘 전환 ·
      aria-expanded 값이 상태와 일치 · 접힘 시 본문 미표시.
  C5. E2E(cross-layer): 오더번호 컬럼이 있는 .xlsx(한 오더에 바코드 2건 포함) 업로드 →
      **오더 1건·item 2건**(오더 단위) DB 생성 확인 + 마스터 그리드(오더 총/미할당·항목 수) ·
      디테일 그리드에 반영. 잘못된 파일(배치 내 바코드 중복 등) → 행별 오류 목록 렌더 · 커밋 0
      (해당 배치 미출현). 콘솔 에러 0.
  C6. 새 양식 다운로드 → 그 파일 그대로 재업로드 성공(양식↔파서 헤더 왕복 정합 · 라운드트립 GREEN).
  C7. git diff: 변경이 명시 스코프 파일로 한정. GenerateAsync·초기화·조회·백엔드 무관 영역·
      **마이그레이션 diff 0**(DB 스키마 무변경 — 기존 order_item 1:N 재사용). 컨트롤러/ b2cTestData.ts
      변경 시 사유가 sprint-log 에 기록되어 있을 것(기본 가정 0 diff).

════════════════════════════════════════════════════════════════════════════
- Parallel Modules: N/A (single module).
    A·B 가 frontend/src/pages/B2cDataGenPage.tsx 를 **공유 편집**하므로(A=레이아웃/disclosure,
    B=업로드 안내 문구·컬럼 안내) 병렬 워크트리 분할 시 동일 파일 쓰기 충돌. 단일 Generator 권장.
- Evaluation Dimensions: functional only.

════════════════════════════════════════════════════════════════════════════
- Detected Project Type: Full-stack
  (저장소 신호: frontend React 컴포넌트 트리(frontend/src/pages·components) + 서버 라우트/컨트롤러
   (backend/src/Wcs.Api/Controllers/B2C) 가 같은 repo 에 공존 → Full-stack.)

- Verification Scenarios (Full-stack — 모든 슬롯 충족):

  === Web/UI (프론트 surface: B2cDataGenPage 좌측 생성 카드 + 하단 배치 상세) ===
  - Default state of each surface touched:
      · 데이터 생성 페이지 로드 → 좌측 생성 카드 안 엑셀 업로드 블록 **접힘(기본)**: "엑셀 업로드"
        라벨 + 양식 다운로드 링크 + 토글만 보이고 파일 input/업로드 버튼/안내 미표시.
      · 하단 "배치 상세" 그리드: 배치 선택 시 오더 행이 실제로 표시(1080/900/700px 각 스냅샷).
  - Each alternate state introduced:
      · 업로드 블록 **펼침**: 토글 클릭/Enter/Space → 파일 input·업로드 버튼·안내 문구·행오류 슬롯 표시,
        aria-expanded=true. 다시 토글 → 접힘, aria-expanded=false.
      · 파일 선택 후 업로드 버튼 enabled(미선택 시 disabled) 상태 스냅샷.
  - Relevant empty / error state surfaced:
      · 배치 미선택 시 하단 상세 EmptyRow("상단에서 배치 행을 선택…").
      · 배치 내 바코드 중복/오더번호 누락 파일 업로드 → 에러 토스트 + 행별 오류 목록(행번호+사유) 렌더.
  - Dark mode variant:
      N/A — 프로젝트는 단일 라이트 테마(docs/B2C-DATAGEN.md §4 "다크모드 N/A"). 사유 명시.
  - Key interaction flow after the change:
      접힘 기본 → 하단 상세 오더 행 가시(핵심 목표) → 토글 펼침 → 양식 다운로드 → 6열 파일 선택
      (1오더:2바코드) → 업로드 → 성공 토스트 + 마스터(오더/미할당/항목) 갱신 + 행 선택 → 디테일
      그리드에 그 오더의 2 바코드 item 표시.

  === Backend/API (backend surface) ===
  - Endpoints touched (method + path):
      · POST /api/b2c/test-data/upload (유일 변경 경로 — 파싱/그룹핑). 컨트롤러 파일검증은 무변경 예상.
      · (무접촉·회귀확인용) GET /api/b2c/test-data/batches · GET /api/b2c/facility/orders?batchId=.
  - Happy path per endpoint (input → output shape):
      · upload: 6열 xlsx, 동일 오더번호 2행(바코드 상이·수량 상이) → 200 {status:"S",
        counts:{ordersCreated:1, orderItemsCreated:2, batches:1, dataRows:2}}. 다중 오더/다중 배치도 검증.
      · batches: 업로드 후 해당 배치 orderTotal=오더수 · orderUnassigned=오더수 · itemTotal=바코드수.
  - Relevant error cases per endpoint:
      · upload 200 F(+rowErrors): 배치 내 바코드 중복(다른 오더 동일 바코드 / 같은 오더 반복 바코드) ·
        오더번호 누락 · 작업일자/차수/수량 형식오류 · 헤더 6열 불일치 · 데이터행 0 · 행수>1000 ·
        사용범위 팽창(행/열 상한). 전부 커밋 0(원자성).
      · upload 400: 파일 없음/0바이트 · >10MB · 확장자≠.xlsx(.xls 거부) · MIME 불일치(컨트롤러 선행).
      · ValidateUploadRows 순수 단위(I/O 무의존) = 위 판정의 스펙 테스트.

  - At least one end-to-end data-flow scenario crossing 2+ layers:
      양식 다운로드(GET 정적 /b2c-order-upload-template.xlsx) → 브라우저 파일 업로드(POST multipart)
      → ClosedXML 파서/ValidateUploadRows → Wcs.Data 트랜잭션(work_batch UQ 멱등 → OrderNo 별
      WcsOrder → 행별 order_item) → GET /batches(orderUnassigned) + 하단 디테일 그리드(1 오더 아래
      N 바코드) 3계층 관통. + 역방향 왕복: 새 양식 재업로드 성공(헤더 정합).

  검증 환경: 프론트 Playwright MCP 헤드리스(vite dev, 기본 http://localhost:5173 — 존재 시
  .claude/ports.local.json 우선). 백엔드는 Evaluator 가 필요 시 기동하되 **포트는
  .claude/ports.local.json 의 이번 스프린트 할당값을 읽어 구성**한다(하드코딩 금지). ⚠ 사용자 로컬
  실 서비스 포트(5205/1502·COM1/RTU)는 **절대 사용 금지**(ports.local.json note 명시). 스크린샷은
  screenshots/S-B2C-DATAGEN-UPLOAD_{YYYYMMDD-HHMMSS}/ 에 번호순 저장 + console.log 캡처(dev-warning/
  pageerror 0 BLOCKING).

> Planner self-check — Detected project type: Full-stack. Required scenario slots: 8
  (UI-default, UI-alternate, UI-empty/error, UI-darkmode(N/A+사유), UI-key-interaction,
   API-endpoints, API-happy, API-error; + cross-layer E2E). All slots filled: yes.
