[Sprint Contract]
Sprint: S-AUDIT-A-FIELD-QUICKFIX
(2026-07-01 전체 감사 묶음 A — 현장 quick-fix · 한 달 경과 재triage 반영)

────────────────────────────────────────────────────────────────────────
★ RE-TRIAGE RESULT (필수 재triage — 현재 코드 직접 확인 완료)
────────────────────────────────────────────────────────────────────────
결론: 묶음 A의 세 항목(①②③) 전부 이미 해소됨. 원인 스프린트 =
S-CLEANUP-FIELD(APPROVED 2026-07-07, fan-out 3모듈, feedback-archive.md "누적
코드리뷰 Minor + 7/01 감사 잔여 정리"). 감사 D표의 묶음 A 세 항목이 각각 그
스프린트의 D-1/D-2/D-3/D-4로 구현·테스트 완료. todo.md:22의 [묶음 A] 체크박스만
갱신되지 않아(stale) 이 스프린트가 재발행됨.

→ 프로덕션 코드 신규 구현 없음. 이 계약은 "해소 확인(verification) + 추적
  reconciliation" 성격이며, HEAD에서 회귀가 발견될 때만 그 항목이 구현 범위로
  전환된다(우발적 조건부). Verification이 전부 PASS면 본 스프린트를 CLOSE하고
  todo.md를 정리.

- Goal:
  묶음 A(① OFFLINE 로그 폭주+유실 / ② /health / ③ IF-05·IF-10 입력 상한)가 현재
  HEAD에서 실제로 해소·유지되고 있음을 신선한 증거(fresh evidence)로 독립 확인하고,
  stale 추적을 정리한다. 신규 프로덕션 코드는 기대하지 않는다 — 회귀가 확인된
  항목만 S-CLEANUP-FIELD 베이스라인으로 복원.

- Implementation Scope:
  1. [프로덕션 코드 변경 = 없음(기대치)] 세 항목 전부 S-CLEANUP-FIELD에서 구현·
     테스트 완료. Generator는 신규 기능을 만들지 않는다.
  2. [추적 reconciliation — 유일한 비-조건부 산출물·비-코드] tasks/todo.md:22 [묶음 A]를
     "해소(2026-07-07 S-CLEANUP-FIELD D-1/D-2/D-3/D-4, 테스트 CleanupFieldM1Tests)"로
     마킹. 프로덕션·스펙 파일 무변경.
  3. [조건부 — 회귀 발생 시에만] Verification Scenario 중 하나라도 HEAD에서 FAIL하면
     그 특정 동작만 S-CLEANUP-FIELD 베이스라인으로 복원. 다른 항목·리팩터·"하는 김에"
     변경 금지. 기대: 없음.
  ※ 절대규칙: #1(PLC 쓰기 0 추가)·#7(상한/주기/롤링 appsettings)·#8(Wcs.Core 무관).

- Evaluation Criteria (Backend/API):
  1. Functionality/Data-integrity (★★★): 과대/음수/과길이 입력 DB 도달 전 400·오염 0,
     /health 항상 200·부수효과 0, OFFLINE 지속 중 ERROR 스택 반복 없음.
  2. Craft (★★): 400은 malformed-edge 거부(business-NG 아님)·멱등/DENIED 계약 보존,
     /health 읽기 전용, 로그 억제 Fail-Loud 유지(전이 1회+주기 요약).
  3. Architecture (★★): 상한/주기/롤링 appsettings(#7), 검증이 컨트롤러 edge(provider-gap 무관).
  4. Verification honesty (★★): 모든 PASS를 fresh tool output으로 뒷받침(코드 존재≠검증).

- Completion Conditions:
  - Backend/API Verification Scenario 전부 fresh 증거 PASS(HTTP 왕복 + 로그 카운트 실측).
    전량 PASS = 묶음 A 해소 확인 → APPROVED(build 불필요).
  - `dotnet test backend/Wcs.sln` GREEN(특히 CleanupFieldM1Tests 전건)·회귀 0.
  - todo.md:22 reconciliation 반영.
  - 회귀가 하나라도 있으면 그 항목 복원 후 재검증 GREEN까지 미-APPROVED.

- Parallel Modules: N/A. Evaluation Dimensions: functional only.

- Detected Project Type: Full-stack (frontend/ 브라우저 진입점 + backend/ 컨트롤러 공존.
  단 묶음 A 변경 표면은 100% 백엔드 — 프론트 파일 0 접촉).

- Verification Scenarios:
  === Web/UI (touched frontend) ===
  - N/A — 프론트엔드 파일 무접촉(PLC 로깅·Serilog 설정·/health·RcsController 검증뿐).
  === Backend/API (touched backend) ===
  - VS-1 [/health 정상]: GET /health → 200, {status:"ok", db:true, sorters:[{chuteNo,online,lastPollAt}]}.
  - VS-2 [/health 부수효과 0]: 연속 2회 → 둘 다 200·레지스터/상태 불변(쓰기 0).
  - VS-3 [IF-05 정상 무회귀]: 정상 요청 → 200 {result:"OK", chuteNo}.
  - VS-4 [OFFLINE 로그 억제]: 지속 OFFLINE → (a) 전이 ERROR 1회 (b) 스택 1회 (c) 지속 폴 ≥N에도 반복 0 (d) 복구 INFO 1회. CleanupFieldM1Tests D1_* fresh 재실행.
  - VS-5 [rollOnFileSizeLimit]: appsettings(.Development).json에 rollOnFileSizeLimit=true·fileSizeLimitBytes 설정 존재·바인딩 실증.
  - VS-6 [IF-05 barcode 과길이 → 400]: 201자 → 400(500 아님)·piece 미생성.
  - VS-7 [IF-05 qty 오버플로 → 400]: int.MaxValue → 400·piece 미생성.
  - VS-8 [IF-05 timeStamp 과길이 → 400]: 31자 → 400.
  - VS-9 [IF-10 음수 qty → 400]: -5 → 400(500 아님).
  - VS-10 [IF-10 barcode 과길이 → 400]: 201자 → 400·business-NG piece 미생성.
  === Cross-layer E2E ===
  - N/A — 묶음 A에 계층 횡단 신규 흐름 없음(단일 백엔드 계층 완결). 억지 E2E 금지.

────────────────────────────────────────────────────────────────────────
SCOPE OUT — 이미 해소 확인 (현재 코드 직접 검증, file:line 증거)
────────────────────────────────────────────────────────────────────────
[① OFFLINE 로그] S-CLEANUP-FIELD D-1+D-2: PlcGateway PublishOffline() Interlocked
  CAS(_online 1→0) 전이당 1회(:561-579)·전이 시 스택 LogError 1회(:502)·지속은 Debug/
  N폴 WARN 요약(:508-515·OfflineLogSummaryEveryPolls 설정)·복구 INFO 1회(:446).
  rollOnFileSizeLimit=true·fileSizeLimitBytes=104857600(appsettings.json:26-27·Dev:30-31).
  테스트 CleanupFieldM1Tests.cs:78-154. (A-12 logs/ 상대경로는 묶음 B 소관.)
[② /health] S-CLEANUP-FIELD D-3: Program.cs:342-367 MapGet — liveness 200·db=CanConnect()·
  sorters=AllBundles.Latest{chuteNo,online,lastPollAt}. 읽기전용. 테스트 :361-395.
[③ 입력 상한] S-CLEANUP-FIELD D-4: IF-05(RcsController.cs:56-68) pId 1~30000·barcode≤200·
  qty>0·qty≤MaxQtyPerRequest(설정)·timeStamp≤30 → 위반 400. IF-10(:218-230) barcode≤200·
  chuteNo>0·qty 0~Max(음수 거부)·timeStamp≤30 → 400. const BarcodeMaxLength=200·
  TimeStampMaxLength=30(:32-33)=WcsDbContext HasMaxLength 정합. 테스트 :399-460.
[SCOPE OUT 별개] A-14 2차(FlushBatchAsync 행격리)·S-B2B-1 #1(ResultItem/BoxRequest StringLength).

────────────────────────────────────────────────────────────────────────
Open Questions (사용자 확인)
────────────────────────────────────────────────────────────────────────
- OQ1(#7 tension): BarcodeMaxLength/TimeStampMaxLength가 const(DB 컬럼 종속·마이그레이션
  바운드라 스키마-정합 const가 정확). qty 상한은 설정값. → 현행 유지 권고.
- OQ2(로그 보존): rollOnFileSizeLimit로 retainedFileCountLimit(base14/dev7)가 크기-롤 파일도
  카운트 → 초고빈도일 달력보존 <14/7 가능. "14일 보존" 의도 재확인(비차단).
- OQ3(closeout): 세 항목 해소·테스트됨. (a) 검증-only 실행 후 CLOSE, 또는 (b) todo
  reconciliation만·build skip 중 택1. 신규 build 대상 없음.

> Planner self-check — Detected project type: Full-stack. Required scenario slots: 3 (Web/UI [N/A·근거], Backend/API [VS-1..VS-10], cross-layer-E2E [N/A·근거]). All slots filled: yes.
