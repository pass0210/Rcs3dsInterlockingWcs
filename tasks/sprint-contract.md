[Sprint Contract] — S-B2C-BARCODE-MULTI-FIX

═══════════════════════════════════════════════════════════════════════════════
현장 온프레미스 배포 중 실발생한 두 결함 수정 — "1 오더:N 바코드" 기능의 후속 갭.
브랜치: feat/b2c-barcode-multi-fix (develop 기준).
═══════════════════════════════════════════════════════════════════════════════

## 미확정 — 사용자 확인 필요
- 없음. 두 수정 방향은 사용자 지시로 확정.

────────────────────────────────────────────────────────────────────────────────
- Goal:
  Fix 1 — 데이터 생성 페이지(B2cDataGenPage.tsx) 하단 "배치 상세" 그리드를 **오더당 1행**에서
    **바코드(order_item)당 1행**으로 바꾼다. 한 오더에 바코드가 N개면 N행이 뜨고, 각 행은
    오더번호·바코드·계획·예약·분류·상태·목적지·할당셀을 표시한다. 데이터(order_item)는 이미
    전량 저장돼 있고, 표시만 1행/오더로 집계돼 있던 것이 근본(첫 바코드만 FirstOrDefault·수량 Sum).
  Fix 2 — IF-05 목적지 조회(DbRepositories.QueryDestination)가 한 바코드에 **여러 order_item**이
    매칭될 때(교차-배치 중복 업로드로 발생) 정렬 없는 .FirstOrDefault()로 **임의 오더**를 골라
    미배정 오더를 집으면 NG/NO_DEST를 반환하던 것을, **배정된(order.DestinationId != null) 오더를
    우선** 선택하도록 결정적으로 고친다. 배정 오더가 없으면 기존 동작(미할당 → AUTO 슈트배정 시도 →
    없으면 NO_DEST) 유지. 정상 1:1(단건 매치)은 동작 불변.

────────────────────────────────────────────────────────────────────────────────
- Implementation Scope (파일별 · Generator가 "어떻게"를 결정):

  【Fix 2 — 배정-우선 결정적 선택】
  · backend/src/Wcs.Api/Repositories/DbRepositories.cs — EfOrderRepository.QueryDestination (44~50):
      현재 `.Where(barcode 일치 && Order.Status ∉ {COMPLETED,CANCELLED}).FirstOrDefault()`를
      **후보 전량 materialize → 순수 선택 규칙 적용**으로 교체. 선택 규칙:
        (1) 후보 중 order.DestinationId != null(배정)이 있으면 그 그룹을 우선.
        (2) 우선 그룹 내 tiebreak = **결정적**(다중 배정 오더가 같은 바코드를 갖는 최악 케이스).
            Generator가 규칙을 정하고 근거를 sprint-log에 기록(권장: 배정확정 최신 DestAssignedAt,
            그 다음 최소 OrderId — 또는 반대. 무엇이든 **결정적**이면 됨).
        (3) 배정 후보가 없으면 미배정 후보에서 **결정적**으로 1건 선택(예: 최소 OrderId) →
            이후 기존 AUTO 슈트배정/NO_DEST 흐름 그대로.
      선택 이후의 상태판정(COMPLETED/PAUSED/OVER/availability FULL·PAUSED·Unmapped)·예약차감·
      piece 삽입·트랜잭션·RecordDenied 경로는 **불변**(선택만 결정적으로 교체). 단건 매치 시
      기존과 동일 결과(회귀 0).
  · 순수 선택 규칙 분리(절대규칙 #8) — 위치는 Generator 결정(권장: Wcs.Core에 EF 무의존 순수
      static + 최소 projection record, 또는 Wcs.Api 내 pure static). 요건: I/O·DI·EF 타입 유입 0,
      입력=후보 projection 리스트, 출력=선택된 후보(또는 index) — 단위테스트로 규칙 고정.

  【Fix 2 — 경로 일관성 조사·명시 (계약 요건)】
  · DbRepositories.cs의 다른 바코드-해소 경로를 조사하고 sprint-log에 결론 기록:
      - IF-09 EfArrivalRecorder.RecordArrival — 활성 piece를 **PId**로 조회(바코드 아님).
      - IF-10 EfDepositRecorder.RecordDeposit — 활성 piece를 **PId**로 조회(fallback은
        chuteNo로 destination 조회 · 바코드→오더 아님).
      - EfCellSelector.SelectCell(chuteNo,barcode)·ReleaseEmptyAssignment — 바코드로 조회하나
        이미 **destination(chuteNo) 스코프**라 교차-목적지 모호성 없음.
    → 조사 결과 "바코드→목적지" 모호성은 IF-05 QueryDestination 고유임을 확인/반증하고 기록.
      IF-05는 필수. 다른 경로에도 동일 모호성이 실재하면 같은 결정적 규칙 적용(Generator 결정 + 근거).

  【Fix 1 — 배치 상세 per-barcode 조회 + DTO】
  · 배치 상세 전용 **per-item(order_item 단위) 조회 경로**를 둔다 — 신규 엔드포인트 vs 기존 재사용은
      Generator 결정(근거 sprint-log 기록). 제약:
      - GET /api/monitor/orders/{id}/items(OrderItemDto)는 (Id,Barcode,PlannedQty,ReservedQty,
        SortedQty)만 반환 → 배치 상세가 요구하는 orderNo·상태·목적지·할당셀이 없다. 재사용하려면
        오더-레벨 필드 보강이 필요하다.
      - 권장(비강제): B2cFacility에 batchId 스코프 per-item 조회 신설
        (예: GET /api/b2c/facility/batch-items?batchId= → 행=order_item, 각 행에
         orderId·orderNo·barcode·plannedQty·reservedQty·sortedQty(항목별) + 오더-레벨
         status·destinationId·destinationChuteNo·destType·assignedCellNo(오더에서 반복)).
      - 무엇을 택하든 **N+1 회피**(오더별 개별 호출 금지 — batchId 단일 조회로 join/materialize).
  · backend DTO — per-item 응답 record 정의(camelCase 미러).
  · ⚠ **B2cFacilityService.GetOrdersAsync(383~434)는 오더 단위 유지** — 설비 관리 배정 UI
      (useFacilityOrders / AssignOrderAsync — 배정은 오더 단위)가 이 경로를 쓴다. GetOrdersAsync·
      B2cOrderDto **무변경**.

  【Fix 1 — 프론트 그리드 per-barcode】
  · frontend/src/lib/b2cFacility.ts — 배치 상세용 per-item 조회 함수 + 훅 신설(또는 교체).
      기존 useFacilityOrders(설비 관리 배정용)는 **무변경**.
  · frontend/src/pages/B2cDataGenPage.tsx — BatchDetailGrid(311~399): 소스를 per-item 훅으로
      바꾸고 per-item 행 렌더. row key는 order_item.id. 컬럼 유지(오더·바코드·계획·예약·분류·상태·
      목적지·할당). 한 오더 N바코드 → N행. 빈/로딩/에러/절단 상태·낮은 뷰포트 레이아웃
      (min-h-[10rem]·페이지 스크롤 에스컬레이션) 보존.

  【테스트】
  · backend — IF-05 다중오더 배정우선(신규): 같은 바코드가 (배정 오더, 미배정 오더) 둘에 존재 →
      QueryDestination이 **배정 오더 목적지로 OK·chuteNo**. 미배정만이면 기존(AUTO/NO_DEST) 불변.
      단건 1:1 불변. 다중배정 tiebreak 결정성.
  · backend — 순수 선택 규칙 단위테스트: 후보 리스트 입력 → 결정적 선택(배정우선·tiebreak·폴백).
  · backend — per-item 배치 조회 테스트: 1 오더:N 바코드 배치 → 항목 N행·오더-레벨 필드 정확·
      batchId 스코프.
  · 기존 테스트(IF-05 1:1·설비 배정·업로드) 회귀 0.

  【docs】
  · docs/B2C-DATAGEN.md — 하단 디테일 그리드 표시 규칙을 "바코드(order_item)당 1행"으로 갱신.
  · docs/SPEC.md §7-B — "IF-05 동일 바코드 다중 목적지 선택 규칙 미정"을 **확정 규칙(배정-우선
      결정적 선택)**으로 갱신 + 조사 결론(IF-09/IF-10 PId 기반) 반영. todo.md 대응 항목 닫음.

  【무접촉 (변경 금지)】
  · 설비 관리 오더 배정 흐름(오더 단위) — GetOrdersAsync·B2cOrderDto·AssignOrderAsync·useFacilityOrders.
  · GenerateAsync·엑셀 업로드 파싱. DB 스키마·마이그레이션(조회/표시/선택만 — 새 마이그레이션 0).
  · Wcs.Core 판정 로직·PlcGateway·핸드셰이크. 절대규칙 #1~#8(특히 #7·#8) 준수.

────────────────────────────────────────────────────────────────────────────────
- Evaluation Criteria (가중치):
  ★★★ Fix 2 정확성 — 같은 바코드가 배정/미배정 두 오더에 있을 때 IF-05가 **배정 오더 목적지로
       OK+chuteNo** / 미배정만이면 기존 AUTO·NO_DEST 불변 / 단건 1:1 불변 / 다중배정 tiebreak 결정적 /
       경로 일관성 조사·결론 sprint-log 기록.
  ★★★ Fix 1 표시 — 배치 상세가 1 오더:N 바코드에서 **N행**(order_item 단위) 실측 + 설비 관리
       배정 UI **무손상**(오더 단위 유지).
  ★★  회귀 — 기존 IF-05 1:1·설비 배정·엑셀 업로드·모니터 표면 불변.
  ★★  Craft — 순수 선택 규칙 분리(단위테스트)·주석·콘솔 청결·N+1 회피.
  (Scope) — 무접촉 경계 준수(GetOrdersAsync 오더단위·스키마·마이그레이션 0·git diff 한정).

────────────────────────────────────────────────────────────────────────────────
- Completion Conditions (전부 충족):
  1. dotnet test 전량 GREEN — 신규(다중오더 배정우선·순수 선택규칙·per-item 조회) 포함, Evaluator
     독립 재실행. baseline 대조 `총 - 신규 = 기존`.
  2. 프론트 tsc / lint / build 각 exit 0 (build 스크래치 outDir — 유저 서빙 wwwroot 무접촉).
  3. Playwright(MCP 헤드리스): 배치 상세가 1 오더:N 바코드에서 **N행 표시** 실측(행 수 계측) +
     설비 관리 배정 UI 정상(오더 단위·배정/해제 동작).
  4. E2E(cross-layer): 같은 바코드가 배정 오더 + 미배정 오더에 존재하도록 조성 → 업로드/생성 →
     한쪽만 배정 → IF-05가 **배정 오더 목적지로 OK + chuteNo**(응답 본문 증거) + 그 배치 상세 per-barcode N행.
  5. 마이그레이션 diff 0 · git diff 스코프 한정(무접촉 경계 파일 diff 0).
  6. 콘솔 캡처 BLOCKING 0(React dev-warning·pageerror·의도치 않은 4xx/5xx 없음).
  7. sprint-log.md에 `## IMPLEMENTATION COMPLETE` + 경로 일관성 조사 결론 + Generator 재량 결정
     (엔드포인트 신규 vs 재사용·tiebreak 규칙) 근거 기록.

────────────────────────────────────────────────────────────────────────────────
- Parallel Modules: N/A (single module — 백/프론트 per-item DTO 계약 강결합·공유 파일 가능). 1/1/1.
- Evaluation Dimensions: functional only (N+1·인덱스는 Craft에 흡수).

────────────────────────────────────────────────────────────────────────────────
- Detected Project Type: Full-stack
  (frontend/src/*.tsx(Vite React) + backend/src/Wcs.Api/Controllers/*.cs 공존 → Full-stack.)

────────────────────────────────────────────────────────────────────────────────
- Verification Scenarios (Full-stack — 필수 슬롯 전부):

  === Web/UI (배치 상세 그리드 · 설비 관리 배정 UI) ===
  · Default state: 배치 미선택 시 빈 안내(EmptyRow); 배치 선택 후 per-item 행(1오더:N바코드면 N행).
    설비 관리 오더 목록은 오더 단위 1행/오더(무손상).
  · Alternate states: 배치 상세 loading/error/절단힌트 정상; N=1 배치와 N≥2 배치 각각 선택 시
    행 수 = 그 배치 order_item 수와 일치(계측).
  · Empty/error: 오더 없는 배치 → "이 배치에 오더가 없습니다."; per-item 조회 실패 → ErrorRow.
  · Dark mode: N/A (프로젝트 단일 테마).
  · Key interaction flow: 마스터 그리드에서 1오더:N바코드 배치 클릭 → 하단 배치 상세가 바코드마다
    1행으로 확장. 설비 관리는 여전히 오더 단위 목록·배정/해제 동작.

  === Backend/API ===
  · Endpoints touched: IF-05 목적지 조회(RcsController → QueryDestination, 배정-우선 선택으로 행위
    변경) · 배치 상세 per-item 엔드포인트(신규/보강 — Generator 확정 후 method+path 확정) ·
    (무변경 확인) GET /api/b2c/facility/orders 오더 단위.
  · Happy path: IF-05(다중매칭·배정존재) → OK+배정오더 chuteNo; IF-05(단건 1:1) → 불변 OK/chuteNo;
    per-item 조회 batchId → order_item 배열(항목별+오더레벨 필드), 1오더:N → N개.
  · Error cases: IF-05(다중매칭·배정없음·슈트 자동배정 불가) → NG/NO_DEST(불변); IF-05(매칭 없음)
    → NG/NO_DEST(불변); per-item 미존재 batchId → 빈 배열 200; 잘못된 파라미터 → 400.

  === Full-stack — cross-layer 데이터 흐름 ===
  · 중복 바코드 E2E: 같은 바코드를 두 오더(A=미배정, B=배정)에 조성(업로드/생성 + 설비 관리에서 B만
    배정) → RCS IF-05 호출(HTTP) → WCS가 배정 오더 B 목적지로 OK+chuteNo(응답 본문 증거) → 그 배치
    데이터 생성 페이지 배치 상세가 per-barcode N행(브라우저 실측).

────────────────────────────────────────────────────────────────────────────────
- 검증 인프라 격리 (현장 오염 0):
  · 백엔드: 유저 실서비스 포트(5205/5173) **미사용**. 격리 백엔드는 전용 여유 포트
    (`-- --urls http://127.0.0.1:<port>`가 appsettings 5205를 이기도록 최우선 지정 — Urls 키 함정).
  · DB: env override(Sqlite scratch 또는 전용 SQL Server DB) + MigrateOnStartup — 현장 운영 DB 무접촉.
  · 소터: 0-DB-소터면 폴링 미개통(실 PLC 무접촉); 소터 경로 필요 시 dead-TCP override. NEVER COM1/RTU.
  · 프론트: Vite 격리 포트(strictPort) + VITE_API_TARGET을 격리 백엔드로. 콘솔 캡처는 세션 격리 판독.

────────────────────────────────────────────────────────────────────────────────
> Planner self-check — Detected project type: Full-stack. Required scenario slots: 8
  (Web/UI: default-state, alternate-state, empty/error-state, dark-mode(N/A), key-interaction-flow;
   Backend/API: endpoints-touched, happy-path, error-cases;
   Full-stack: cross-layer-data-flow). All slots filled: yes.
