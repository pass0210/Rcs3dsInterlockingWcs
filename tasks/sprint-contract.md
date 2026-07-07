[Sprint Contract] — S-FIELD-20CELLS

── 배경 (WHY) ────────────────────────────────────────────────────────────────
현장 3D Sorter는 물리 20셀(4행 5열)이지만, PLC 매핑이 **15번 셀까지만** 완료되어 있다.
셀 16~20에 작업이 배정되면 미매핑 셀로 틸트 지령이 나가는 물리 리스크가 있다.
따라서:
  · 모니터링 화면은 물리 20셀을 그대로 보여준다(4×5=20 타일).
  · 그러나 **작업 배정·선택 가능 셀은 1~15만**이어야 한다 — 16~20은 어떤 경로로도 선택 불가.
현행 FIELD-16 시드(셀 1~16, 오더/배정 0701-CELL-01~16, 소터 chuteNo=1)는 이미 현장
DB(Rcs3dsInterlockingWcs @ localhost SQL Server)에 적용돼 있다. 이 스프린트는 그 상태에서
20셀·15가용으로 **전이**하고(멱등), 프론트를 4×5로 미러링한다.

── ★ 계획 단계 핵심 발견 (권장안의 근거) ─────────────────────────────────────
cell 테이블에는 이미 `Enabled bit` 컬럼이 존재하며, 그것이 **이미 게이트로 동작**한다:
  · EfCellSelector.SelectCell ②빈 셀 폴백: `c.Enabled` 필터로 비활성 셀 제외
    (DbRepositories.cs:628).
  · IF-05 SorterCanAcceptBarcode → HasFreeEnabledCell: `c.Enabled` 필터
    (DestinationStatusService.cs:188).
  · SorterFull 산출(ComputeSorterFull): Enabled 셀만 집계(:210) — 15셀 만재 판정 일관.
  · 모니터링 셀 목록(GetCells): `Enabled` 필터 **없음**(:195) → 비활성 셀도 반환 +
    DTO에 `Enabled` 포함(:221, CellStatusDto). 프론트는 `!cell.enabled`를 이미
    회색 "비활성" 타일로 렌더(SortingSection.tsx:85-86,101-102) + 범례에 "비활성" 존재(:63).
결론: **가용 게이트를 위한 신규 스키마/게이트 코드는 이미 존재한다.** 브리핑의 후보(a)
"가용 플래그 신설(마이그레이션)"은 이 발견으로 재프레이밍된다 → "기존 Enabled 재사용".
단, **주의(gap)**: `Enabled=0`은 ②빈 셀 폴백만 차단하고 ①활성 배정 재사용 경로
(HasAssignedCellWithRoom / SelectCell ①)는 Enabled를 보지 않는다. 따라서 셀 16~20이
**활성 cell_assignment를 갖지 않도록** 함께 보장해야 완전 차단이 성립한다(아래 §셀 16 처리).

── Goal (WHAT) ───────────────────────────────────────────────────────────────
현장 DB가 물리 20셀을 반영하되(모니터링 4×5=20 타일), 작업 배정·선택 가능 셀은 1~15로
제한된다. 셀 16~20은 배정 재사용·빈 셀 폴백 어느 경로로도 선택되지 않으며, PLC 매핑이
진행되면 데이터/설정 변경만으로(코드·스키마 무변경) 가용 상한을 15→20으로 확장할 수 있다.
프론트 셀 그리드는 5열 고정(4×5 물리 미러링), 미가용 셀 16~20은 기존 "비활성" 렌더로 시각 구분.

── Detected Project Type: Full-stack ─────────────────────────────────────────
(신호: 브라우저 진입점 frontend/src/pages/* 와 서버 라우트/컨트롤러
backend/src/Wcs.Api/Controllers/* 가 동일 레포에 공존. IF-05 백엔드 게이트 + 셀 그리드
프론트 두 계층을 함께 건드리므로 Full-stack.)

── Implementation Scope (Generator가 구현할 것) ──────────────────────────────
※ 아래 A안(권장) 기준. 게이트 메커니즘 최종 선택은 Questions 섹션의 사용자 확인 결과에
   따른다. B안 선택 시 §Questions에 명시한 스코프 델타를 적용(재계획 또는 Generator 확장).

1) 시드/DB (scripts/) — 16셀 → 20셀·15가용 전이 (멱등):
   a. 셀 1~15: `Enabled=1`, Capacity=3 유지(현행과 동일).
   b. 셀 16~20: 행 존재(20타일 요구) + `Enabled=0`. Capacity는 20셀 물리 일관성 위해 3 권장
      (비활성 셀은 선택되지 않으므로 값 자체는 무영향 — Generator 재량).
      → 셀 16은 UPDATE(Enabled 1→0), 셀 17~20은 INSERT(NOT MATCHED). 셀 1~15 무손상.
   c. 셀 16의 기존 데이터 처리 (0701-CELL-16):
      · 활성 cell_assignment(CellId=cell16, ReleasedAt IS NULL)를 **해제**(ReleasedAt=now). 멱등.
      · 오더 0701-CELL-16을 `Status='CANCELLED'`로 전이(멱등). → 이유: ①활성 배정 재사용 경로가
        Enabled를 보지 않는 gap을 닫고, ②barcode 0701-CELL-16이 유효 RUNNING 오더를 갖지 않게
        해 IF-05 NG를 **1~15 점유 상태와 무관하게 결정적**으로 만든다. order_item 행은 보존
        (append-only 원칙·이력). 예약/실적(reserved/sorted) 조작 금지.
      · (대안 하위옵션 — 오더를 RUNNING으로 두고 배정만 해제: 1~15가 모두 점유일 때만 NG가 성립해
        취약. 권장하지 않음. Generator는 CANCELLED 방식을 택하되, IF-05 barcode→목적지 해석
        경로에서 실제 NG가 나오는지 코드로 확인할 것.)
   d. 오더/아이템/배정의 N↔N 결정적 픽스처는 **1~15** 로 축소(15건 활성). 나머지 구조·소터
      chuteNo=1·work_batch(FIELD-16, WorkDate=2026-07-01)는 유지.
   e. 파일: 현행 scripts/seed-field-16cells.sql 갱신. 파일명은 S-FIELD-20CELLS 취지상
      `seed-field-20cells.sql`로 리네임 권장(Generator 재량 — 리네임 시 헤더 주석/설계결정
      블록도 20셀·15가용 기준으로 갱신하고, docs/기존 참조가 있으면 함께 갱신).
   f. 멱등성: 전 구간 IF NOT EXISTS / NOT EXISTS / MERGE / UPDATE 보정 유지. 2회 이상 실행해도
      셀 20·가용 15·배정 15·오더 CANCELLED(16) 불변. BEGIN TRAN/COMMIT + XACT_ABORT 유지.
   g. **매핑 확장 경로 문서화**: 셀 주석 또는 별도 확장 스니펫으로 "PLC 매핑 완료 시
      `UPDATE cell SET Enabled=1 WHERE CellNo BETWEEN 16 AND 20`(필요 시 오더/아이템/배정
      16~20 추가) 만으로 가용 상한 확장" 명기. 코드/스키마 변경 불요임을 남길 것.

2) 백엔드 게이트 (A안: 코드 변경 없음) — 회귀 방지 테스트만 추가 (backend/tests, SQLite 더블):
   · SelectCell: 셀 1~15가 전부 만재(작업수량 도달)이고 16~20이 Enabled=0·미배정일 때,
     SelectCell이 **16~20을 절대 반환하지 않음**(빈 셀 폴백 차단 단정).
   · IF-05 gate(SorterCanAcceptBarcode 또는 엔드포인트): 0701-CELL-16(또는 비활성 셀 대응
     바코드) → NG. 0701-CELL-01/-15 → OK.
   · SorterFull 일관성: 가용 15셀이 모두 만재 → SorterFull=true, 신규 바코드 IF-05 NG
     (16~20 비활성 셀은 full 산출에서 제외됨을 단정).
   · 테스트는 프로그래매틱 시드(SQLite in-memory 더블)로 20셀·15가용 상태를 구성한다
     (.sql 파일은 실 SQL Server 재적용 절차로 별도 검증 — §Completion 참조).

3) 프론트엔드 (frontend/src/pages/sections/SortingSection.tsx):
   · CellsCard 그리드를 **5열 고정**으로 변경(현행 `grid-cols-2 sm:grid-cols-3 lg:grid-cols-4
     xl:grid-cols-6` → 5열 고정). 20타일이 4행×5열로 물리 미러링. 좁은 폭에서 레이아웃이
     깨지지 않도록 처리(고정 5열 유지가 요건 — 필요 시 컨테이너 가로 스크롤/최소폭).
   · 미가용 셀(16~20) 시각 구분은 **기존 `!cell.enabled` 렌더로 자동 충족**(회색+"비활성" 배지).
     별도 데이터/컬럼 추가 없음. 범례의 "비활성" 배지 유지. → 무리한 신규 UI 추가 금지.
   · tsc/eslint 0 유지. CellStatus 타입은 이미 `enabled` 보유 — DTO/타입 변경 불요.

── Evaluation Criteria (Evaluator 판단 기준 + 가중) ──────────────────────────
Full-stack 4기준 구조:
  1. Integration Quality (★★★) — 시드 데이터(20셀·15가용) ↔ IF-05 게이트 ↔ 셀 선택 ↔
     모니터링 API/프론트 타일이 하나의 진실("가용=Enabled=1인 1~15")로 정합. 계층 경계 갭 0.
  2. Per-layer Quality (★★★)
     · Backend/API: IF-05 OK/NG 판정이 정확·결정적. 절대규칙(단일 큐, TgtFloor, Ready 의미)
       및 SPEC(§6 투입 가부) 위반 0. 순수 판정 로직 훼손 0.
     · Web/UI(frontend): 4×5 그리드가 물리 배치를 명료하게 미러링, 비활성 타일 구분이 자연스럽고
       일관(디자인 토큰·타이포·간격). AI-slop 아님.
  3. Craft (★★) — 시드 멱등성·트랜잭션 안전, 엣지(셀 16 전이·전량 만재), 테스트 커버리지,
     계층 간 타입 안전(enabled bool 일관).
  4. Functionality (★★) — 전체 사용자 여정(현장 소터 선택→20타일→16~20 비활성; IF-05 15셀
     정상·16 차단; 매핑 확장 데이터-온리)이 end-to-end로 성립. 데이터 무결성.

── Completion Conditions (Evaluator 통과 최소 조건) ──────────────────────────
[전체]
  · 전체 테스트 GREEN. 기준 카운트 175(=PR #31 병합 후 예상)이나 **하드코딩 금지** —
    develop 분기 후 기동 시 실제 카운트를 재확인하고, 신규 회귀 테스트가 그 위에 GREEN으로 추가.
  · 마이그레이션이 필요한 선택(B안)일 경우에만: SqlServer + Sqlite 양 provider 마이그레이션
    up/down 검증. A안(권장)은 스키마 무변경이라 마이그레이션 없음.
  · lint/type/format(check 모드) 독립 실행 결과 기록(tasks/sprint-feedback.md).
[백엔드/데이터]
  · SQLite 더블 테스트: (1) 1~15 만재 시 SelectCell이 16~20 미반환, (2) IF-05
    0701-CELL-01/-15 OK·0701-CELL-16 NG, (3) 15셀 전량 만재 → SorterFull·IF-05 NG.
[프론트]
  · 실행 중인 앱에서 현장 소터(3DS #01) 선택 시 20타일이 5열×4행으로 렌더, 16~20 회색 "비활성".
  · tsc·eslint 0. 콘솔 error/pageerror/React dev warning 0(Evaluator console.log 캡처).
[실 DB 재적용 — ★ 안전 절차 필수 (2026-07-03 오염 사고 교훈)]
  · 실 SQL Server 재적용은 **명시적 sqlcmd/SSMS 실행**으로만 — 앱 기동 시드 금지. 현장 DB의
    `Database:SeedOnStartup`은 false 유지(환경 암묵 발동 금지). appsettings로 실 DB에 직행하는
    자동 시드 벡터 없음을 확인.
  · **사전 백업 필수**: 재적용 전 대상 DB 백업(BACKUP DATABASE, 또는 최소한 cell·
    cell_assignment·wcs_order·order_item 스냅샷 내보내기). 백업 완료 확인 후에만 적용.
  · 트랜잭션: 시드 전체가 단일 BEGIN TRAN/COMMIT(+XACT_ABORT ON) 안에서 원자 적용.
  · 재적용 후 검증 쿼리로 다음 단정:
      - cell 20행(CellNo 1~20), Enabled=1 인 셀 정확히 15개(1~15), Enabled=0 이 5개(16~20).
      - 활성 cell_assignment(ReleasedAt IS NULL) 정확히 15건(01~15). 셀 16 배정 해제됨.
      - wcs_order 0701-CELL-16 Status='CANCELLED'. 01~15 RUNNING·데이터 무손상.
      - 멱등: 동일 스크립트 ×2 재실행 후 위 카운트 전부 불변.
  · (실 DB가 손닿는 환경이 아니면: 동일 스키마의 로컬 SQL Server 인스턴스에 빈 DB로
    `ef database update` 후 시드 적용해 검증하고, 그 절차를 sprint-feedback.md에 기록.
    "코드 리뷰로 대체" 금지 — 실제 실행 증거 필수.)

── Parallel Modules (선택 — Generator fan-out) ───────────────────────────────
A안 기준 경계-청결 2모듈(공유 파일 쓰기 없음):
  · [DATA] scripts/seed-*.sql 갱신 + backend/tests/* 회귀 테스트 추가.
  · [FE]   frontend/src/pages/sections/SortingSection.tsx 그리드 5열 고정.
두 모듈은 파일 경계가 분리되어 병렬 가능하나, 변경량이 작아 단일 Generator 순차도 무방
(orchestrator 재량). B안 선택 시 [BE-GATE](게이트 코드 + 마이그레이션 + DTO) 모듈이 추가되고
[FE]가 DTO 신필드에 의존하게 되어 순서 결합 발생 → 그 경우 단일 Generator 순차 권장.

── Evaluation Dimensions (선택 — Evaluator expert pool) ──────────────────────
functional only (단일 차원). 단, 실 DB 재적용 안전(백업·트랜잭션·멱등·비오염)은 functional
검증 안에서 **BLOCKING 서브체크**로 다룬다(2026-07-03 교훈). 별도 병렬 차원으로 분리하지 않음.

── Verification Scenarios (Full-stack, mandatory) ────────────────────────────
※ 아래는 A안(권장) 기준. B안 채택 시 §Questions의 스코프 델타에 대응하는 시나리오
   (신 플래그가 게이트 3술어·DTO·타일에 반영, 마이그레이션 up/down 양 provider)를 추가한다.

  === Web/UI (프론트 셀 그리드 — SortingSection) ===
  · 각 surface 기본 상태: /api 기동 + 현장 소터(chuteNo=1) 20셀 시드 상태에서 분류 현황 화면
    진입 → 소터 드롭다운 "3DS #01" 선택 → 셀 현황 카드에 **20개 타일이 5열×4행**으로 렌더.
    스크린샷으로 열 수(5)·행 수(4)·타일 총 20 확인.
  · sprint가 도입/변경하는 대체 상태: 셀 16~20 타일이 **회색+"비활성" 배지**(opacity 낮음,
    미배정 표기)로, 셀 1~15의 활성 상태(여유/점유/근접/만재)와 시각적으로 구분됨을 스크린샷 확인.
    (셀 1~15 중 만재/점유 타일이 있으면 그 상태 배지도 함께 확인.)
  · 관련 빈/에러 상태: 셀이 없는(또는 미존재) destId 선택 시 "등록된 셀이 없습니다"/EmptyRow
    경로가 정상 — 현장 소터에는 항상 20셀이 있으므로 대조 케이스로만 확인(회귀 방지).
  · 다크 모드: 앱에 다크 모드 토글이 존재하면 20타일·비활성 구분을 다크에서도 확인. 토글이
    없으면 N/A(사유: 앱은 단일 테마 디자인 토큰 사용으로 보임 — Evaluator가 토글 유무를
    실제 확인 후 판정, 있으면 검증·없으면 N/A 기록).
  · 변경 후 핵심 인터랙션 흐름: 소터 드롭다운에서 현장 소터 선택 → 그리드가 4×5로 갱신 →
    비활성 16~20 확인 → (있으면) 다른 소터 선택 시 그 소터의 셀 수/배치로 갱신되는지 확인.
    최종 스냅샷 1장이 아니라 select→갱신 상호작용을 번호 스크린샷으로 기록.

  === Backend/API ===
  · 이 sprint가 건드리는 엔드포인트(method + path):
      - POST /api/v1/destination-query   (IF-05 투입 목적지 조회 — OK/NG 판정)
      - GET  /api/monitor/sorters/{destId}/cells   (셀 현황 목록 — 20행 반환 확인)
      - (참고·회귀) GET /api/monitor/sorters, POST /api/v1/deposit-report(IF-10) — 직접 변경
        없으나 15셀 만재→SorterFull 일관 검증에 사용.
  · 엔드포인트별 happy path(입력→출력 형태):
      - POST /api/v1/destination-query { barcode:"0701-CELL-01", pId, agvNo, inductionNo, qty,
        timeStamp } → **OK**(소터 chuteNo=1 목적지, 셀 배정 가능). "0701-CELL-15"도 OK.
      - GET /api/monitor/sorters/{fieldSorterDestId}/cells → 20개 CellStatusDto 배열,
        CellNo 1~20, enabled=true 15개(1~15)·false 5개(16~20), 16~20 occupied=false·미배정.
  · 엔드포인트별 관련 에러/거부 케이스(해당하는 것만 — 패딩 금지):
      - POST /api/v1/destination-query { barcode:"0701-CELL-16", … } → **NG**(미매핑 셀 차단).
        1~15가 전부 점유든 아니든 결정적으로 NG여야 함(오더 CANCELLED 근거).
      - 가용 15셀 전량 만재 상태에서 임의 신규 바코드 destination-query → NG(SorterFull 일관).
      - (자동화 테스트 코드로 재현 — 일회성 curl 금지. SQLite 더블 통합 테스트로 상태 구성.)

  === End-to-end (2계층 이상 교차 데이터 흐름) ===
  · 시드 재적용 → API 기동 → 모니터링 프론트까지 관통:
    (1) 20셀·15가용 시드 상태에서 API 기동 →
    (2) GET /api/monitor/sorters/{destId}/cells 가 20행(enabled 15/비활성 5) 반환 →
    (3) 프론트 셀 그리드가 그 20행을 4×5로 렌더하고 16~20을 "비활성"으로 표시 →
    (4) POST /api/v1/destination-query barcode 0701-CELL-01 → OK / 0701-CELL-16 → NG →
    (5) IF-05 OK로 배정·적재가 진행돼도 대상 셀 번호가 항상 1~15 범위임을 확인(16~20 미도달).
    데이터(시드)가 게이트(IF-05/SelectCell)와 표시(모니터 API/프론트)에서 동일 진실로
    관측됨을 계층 왕복으로 증명.

> Planner self-check — Detected project type: Full-stack. Required scenario slots: 3
> (Web/UI 프론트 셀 그리드, Backend/API IF-05·cells 엔드포인트, End-to-end 시드→API→프론트
> 교차 흐름). All slots filled: yes.

── Questions (사용자 확인 필요 — Phase 1→2 게이트 대상) ───────────────────────
게이트 메커니즘 선택은 novel decision이므로 **구현 착수 전 사용자 확인**을 요청한다. 후보와
트레이드오프를 정리하고 권장안을 제시한다.

Q1. 셀 16~20 배정/선택 차단 메커니즘을 무엇으로 할 것인가?

  [A안 — 기존 Enabled=0 재사용 (★ 권장)]
   · 방식: 셀 16~20을 `Enabled=0`으로 시드 + 셀 16 배정 해제 + 오더 16 CANCELLED.
   · 장점: **스키마 마이그레이션 0, 게이트 코드 변경 0, DTO/타입 변경 0.** SelectCell②·
     IF-05 HasFreeEnabledCell·ComputeSorterFull·모니터 목록·프론트 "비활성" 렌더가 모두 이미
     Enabled를 존중 → 순수 시드 데이터 + 프론트 그리드 1줄 변경. 매핑 완료 시
     `UPDATE Enabled=1`만으로 확장(데이터-온리). 블라스트 반경 최소.
   · 단점/리스크: `Enabled`의 의미가 "운영 가용"과 "PLC 매핑됨"을 겸하게 되는 **의미 겸용**
     (semantic overloading). 기능상 동일(미매핑 셀 = 운영 불가)하나, 훗날 "매핑됐지만 정비로
     비활성" vs "매핑 안 됨"을 구분해야 하면 표현력이 부족. gap 보완 위해 셀 16 배정 해제가
     반드시 함께 수행돼야 함(①경로가 Enabled 무시).

  [B안 — 신규 전용 플래그(예: cell.PlcMapped bit) 신설]
   · 방식: 새 컬럼 추가 → EF 엔티티·마이그레이션(SqlServer+Sqlite 양 provider) → SelectCell②·
     HasFreeEnabledCell·HasAssignedCellWithRoom·ComputeSorterFull 술어에 `&& PlcMapped` 추가 →
     CellStatusDto/프론트 타일에 신필드 반영.
   · 장점: 의미 분리 명확("가용/정비"와 "PLC 매핑"이 독립 축). 배정 재사용 경로(①)까지
     플래그로 원천 차단 가능(A안의 gap을 코드로 닫음).
   · 단점/리스크: **블라스트 반경 大** — 마이그레이션(2 provider)·공유 게이트 로직 3~4곳·
     DTO·프론트 동시 변경. 절대규칙(순수 판정·단일 큐)·크로스-엔드포인트 동형 불변식
     (IF-05⟺SelectCell)을 4곳에서 동시에 유지해야 함. 지금 필요하지 않은 구분을 위해 위험을
     선지불. 회귀 위험 상승.

  [C안 — Capacity=0 semantics 재정의] → 기각 권고.
   · 현행 `IsCellAtCapacity(NULL/≤0=무제한)`에서 Capacity≤0은 **무제한**을 뜻함 → Capacity=0은
     의도와 정반대(무한 수용). 게다가 ②빈 셀 폴백은 Capacity를 보지 않고 Enabled·미점유만 보므로
     Capacity로는 선택 자체를 못 막음. 공유 SorterCellQty 의미 반전은 위험. 기각.

  [D안 — 셀 행을 15까지만(16~20 미생성)] → 기각 권고.
   · 모니터링 20타일(4×5) 요구와 정면 충돌(15타일만 렌더). 기각.

  → 권장: **A안**. 근거 = 스키마/게이트 코드 무변경으로 요구를 전부 충족하고, 매핑 확장이
     데이터-온리이며, "매핑됐지만 정비로 비활성" 같은 미도래 요구를 위해 위험을 선지불하지 않음
     (Minimal Impact·Simplicity First). A안의 gap(①경로 Enabled 무시)은 셀 16 배정 해제 +
     오더 CANCELLED로 완결적으로 닫힌다.
  → B안 선택 시 스코프 델타: [BE-GATE] 모듈 추가(엔티티+마이그레이션 2 provider + 게이트 술어
     3~4곳 + DTO), [FE] 신필드 의존, Verification에 마이그레이션 up/down·신플래그 게이트
     시나리오 추가. 이 경우 Planner 재계획 또는 Generator 확장으로 반영.

Q2. 셀 16 기존 오더(0701-CELL-16) 처리: 권장은 **오더 CANCELLED + 배정 해제**(order_item 행은
    이력 보존). "오더 RUNNING 유지 + 배정만 해제"는 1~15 전량 점유일 때만 NG가 성립해 취약 —
    권장하지 않음. 이 결정에 이견이 있는지 확인.

Q3. 시드 파일명: `seed-field-16cells.sql` → `seed-field-20cells.sql` 리네임 권장(내용이 20셀·
    15가용으로 실질 변경). 유지 선호 시 파일명은 그대로 두고 내용만 갱신 가능. 선호 확인.

── ★ 사용자 확정 (2026-07-07, Phase 1→2 게이트 통과) ─────────────────────────
Q1 게이트 방식: **A안 — 기존 Enabled 재사용** (스키마·백엔드 코드 변경 0, 회귀 테스트만 추가)
Q2 셀16 오더: **CANCELLED + 배정 해제** (order_item 이력 보존, IF-05 결정적 NG)
Q3 파일명: **seed-field-20cells.sql로 리네임** (헤더 주석 20셀·15가용 기준 갱신)
브랜치: feat/field-20cells — fix/handshake-rflag-residue(PR #31) 위에 스택, 병합 순서 #31→본 PR.
테스트 기준 카운트: 이 브랜치는 #31 포함이므로 175 기대(기동 시 실제 재확인).
