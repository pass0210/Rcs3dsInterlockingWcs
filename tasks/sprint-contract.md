[Sprint Contract] — S-AUDIT-D-HANDSHAKE-HARDENING
(2026-07-01 전체 감사 묶음 D — 핸드셰이크 견고화, 운영 투입 전)
Base: 최신 develop = 459aaac. feature 브랜치 feat/audit-d-handshake-hardening.

════════════════════════════════════════════════════════════════════════════
RE-TRIAGE RESULT (필수 재triage — 현재 코드 직접 판정)
════════════════════════════════════════════════════════════════════════════
- D① (R_Flag 레벨읽기 → 허위 RSEQ_MISMATCH·off-by-one 자가지속): ✅ 해소 — SCOPE OUT.
    S-HANDSHAKE-RESIDUE의 arming(ArmRFlagZeroAsync HandshakeOrchestrator.cs:260-322 — C 기입 전
    RFlag==0 선관찰=에지 등가). 실증 HandshakeResidueTests S1/S2/S3(실 SimServer·back-to-back 잔류
    허위 RSEQ_MISMATCH 0·DoesNotContain HS_RSEQ_MISMATCH). 감사 A-1 권고(a) 그대로 구현.
- D③-part1 (기동 시 잔류 R_Flag ClearR + 로그): ✅ 해소 — SCOPE OUT.
    양층 C2 StartupClear(PlcGateway.cs:710-729 — R 영역 D2/D3·R_Flag 비트 clear+로그). 실증
    StartupClearTests VS1 + HandshakeResidueTests S3.
- D② (CFlagTimeout 단독 결정적 테스트 부재): ⛳ 유효 — IN SCOPE. 현 D5(E2EGroupCD)는
    `CFLAG_TIMEOUT || RFLAG_TIMEOUT` 택일이라 C_Flag 무한대기 회귀 통과.
- D④ (IF-10 RecordDeposit false 3원인 '멱등 OK' 합류): ⛳ 유효 — IN SCOPE. RecordDeposit bool 반환
    (DbRepositories.cs:478) 3원인이 RcsController.cs:249-253 '멱등 OK' 합류.
- D③-part2 (journal.CreateSent 투입 직후로): ❓ 유효하나 저우선·설계 긴장 → Open Question(기본 SCOPE OUT).

────────────────────────────────────────────────────────────────────────────
- Goal:
  이미 해소된 D①·D③-part1을 회귀 없이 보존한 채 남은 견고화 2건을 닫는다.
  (1) D② — C_Flag 무한대기 회귀를 결정적으로 잡는 CFlagTimeout '단독' 단언 테스트 추가
      (현 유일 타임아웃 E2E는 CFLAG||RFLAG 택일이라 RFLAG만으로 통과).
  (2) D④ — IF-10 RecordDeposit 실패 3원인(진짜중복 / DENIED재보고 / 미존재chuteNo·무피스)이
      전부 bool false→'멱등 OK' INFO로 합류해 오도하는 것을, 원인별 분리(enum)+원인별 로그
      (WARN/alarm 후보)로 닫고 미검증 2분기 '현동작 고정' 테스트 추가.
  전 작업은 200-OK 멱등 응답 시맨틱 + 기존 성공/불일치/타임아웃/OFFLINE/잔류/복귀 전 시나리오
  바이트 보존하는 '동작 보존 리팩터 + 관측성/커버리지 강화'. 핸드셰이크 제어 흐름 무변경.

────────────────────────────────────────────────────────────────────────────
- Implementation Scope (Generator — WHAT):
  [D② — CFlagTimeout 결정적 단언] (테스트 중심·프로덕션 변경 최소/0)
  · 오케스트레이터 단위: C_Flag=1 잔류(미소비) 상태 → ExecuteAsync → Outcome==HandshakeOutcome.
    CFlagTimeout '단독' 단언(택일 아님). 경과 ≤ CFlagTimeoutMs+ε(무한대기 배제). 타임아웃 설정 주입(#7).
  · API/영속화: IF-10 유발 핸드셰이크 CFlagTimeout → alarm code "CFLAG_TIMEOUT" 단독 +
    sorter_command status=TIMEOUT / piece status=TIMEOUT 현동작 고정. (재시도/포기 '정책'은 SPEC §7-B 미확정·무변경.)
  [D④ — RecordDeposit 원인 분리 + 원인별 로그 + 현동작 고정 테스트]
  · IDepositRecorder.RecordDeposit 반환 bool→'원인 구분 결과 타입'(최소: 신규기록/진짜중복/
    DENIED재보고/미존재chuteNo·무피스). enum명·배치·시그니처는 Generator 결정(HOW). 판정은 DB I/O 의존
    → I/O 계층(Repositories)에 둔다(Wcs.Core 순수 #8 침범 금지).
  · RcsController IF-10 원인별 로깅: 진짜중복→현행 '멱등 OK'(INFO) 유지, DENIED재보고→WARN(+alarm 후보),
    미존재chuteNo·무피스→WARN. 전 케이스 여전히 200 OK(응답 보존·정책 변경 아님).
  · '현동작 고정' 테스트: (a) DENIED piece 재보고→200 OK·DENIED 불변·piece_event 무증가·DENIED WARN.
    (b) 미존재 chuteNo·무피스→200 OK·piece 0·NoDestination WARN. (c) 정상 신규→신규기록·트리거 정상 /
    같은 pId 재보고→진짜중복·'멱등 OK' 유지(회귀).
  [D③-part2 — Open Question 대기(기본 SCOPE OUT)] 채택 시에만 sorter_command SENT 행 내구화.
  [공통 회귀·불변식 보존] #1(단일 쓰기 큐)·#4(Ready 의미)·#7(타임아웃 설정)·#8(Wcs.Core 순수) 전부 보존.
    HandshakeOrchestrator 제어 흐름(arming·안착지연·C_Flag 대기·R 폴·복귀 대기·ClearR) D②에서 무변경(관측만).
    D①/D③-part1 회귀: HandshakeResidueTests(S1~S6·S5b)·StartupClearTests(VS1~VS3b) 전건 GREEN 유지.

- SCOPE OUT (해소 확인·착수 금지·회귀 보존만):
  · D①(arming 해소)·D③-part1(StartupClear 해소) — 위 증거. · F1b 동시 IF-10 직렬화(별개 근본원인·todo:127).
  · 오더 완료/인덱스/동시 IF-05(묶음 C/기타) — 본 계약 밖.

────────────────────────────────────────────────────────────────────────────
- Detected Project Type: Backend/API
  (변경 표면 100% 서버측 C#: HandshakeOrchestrator·RcsController·DbRepositories(IDepositRecorder)·
   xUnit+실-Sim. 브라우저 대면 파일 0 변경. 레포에 frontend/ 존재하나 본 스프린트 미변경 → operative 타입
   Backend/API. 필수 검증=자동화 테스트 실행.)

- Verification Scenarios (Backend/API):
  · Endpoints touched: POST /api/v1/deposit-report (IF-10) — RecordDeposit 원인 분리(D④)+CFlagTimeout(D②) 진입점.
    신규 엔드포인트 없음(오케스트레이터 단위 테스트는 HTTP 없이 직접).
  · Happy path: IF-10 정상 신규 → 200 {result:"OK"}·RecordDeposit=신규기록·piece RESERVED→DEPOSITED·
    (3D)IF-11 트리거/(슈트)만재 집계. 동작 보존.
  · Error/branch: 진짜중복→200·'멱등 OK'·미트리거 / DENIED재보고→200·DENIED 불변·piece_event 무증가·WARN /
    미존재chuteNo·무피스→200·piece 0·WARN / CFlagTimeout→alarm "CFLAG_TIMEOUT" 단독·TIMEOUT 매핑. (400은 D-4 기존 커버.)
  · Sprint-specific (N=9):
    VS-1 [D②단위] C_Flag 잔류→Outcome==CFlagTimeout 단독 + 경과 ≤ CFlagTimeoutMs+ε(설정 주입).
    VS-2 [D②API] IF-10 CFlagTimeout→alarm "CFLAG_TIMEOUT" 단독 + sorter_command/piece TIMEOUT 매핑.
    VS-3 [D④정상/중복] 신규=신규기록·트리거 정상 / 재보고=진짜중복·'멱등 OK' 유지.
    VS-4 [D④DENIED] DENIED 재보고→200·DENIED 불변·piece_event 무증가·DENIED WARN.
    VS-5 [D④미존재] 미존재 chuteNo·무피스→200·piece 0·미존재 WARN.
    VS-6 [D①회귀] HandshakeResidueTests S1/S2/S3 GREEN(arming 훼손 0·허위 MISMATCH 0).
    VS-7 [회귀] 핸드셰이크 전 시나리오 GREEN(성공/RSeqMismatch/RFlagTimeout/OFFLINE/잔류/복귀·StartupClear).
    VS-8 [flake 배제] 실-Sim 통합 신규/기존 다회 반복 결정적 GREEN(RSeqMismatch/타이밍 flake 0·신규 테스트 견고화).
    VS-9 [D③-part2 조건부] 채택 시에만 journal.CreateSent 투입 직후 SENT 행 내구 단언. 미채택 N/A.

────────────────────────────────────────────────────────────────────────────
- Evaluation Criteria (가중): API Design(원인 명료 구분·200 멱등 보존·alarm/상태 일관·로그 정직 fail-loud) ★★★ /
  Architecture(원인 판정 I/O 계층·Wcs.Core 순수 #8·핸드셰이크 흐름 무변경 additive 테스트) ★★★ /
  Craft(결정적 타임아웃 ±ε·설정 주입 #7·실-Sim flake 방어·엣지 처리·멱등 무결) ★★ /
  Functionality(회귀 0·데이터 무결·#1/#4 보존) ★★.

- Completion Conditions (AND):
  1. 전체 dotnet test GREEN·회귀 0. 2. D② VS-1·VS-2 신규 GREEN + CFlagTimeout '단독' 단언(Evaluator 독립 재실행).
  3. D④ VS-3·VS-4·VS-5 신규 GREEN·RecordDeposit 원인 구분 타입·컨트롤러 원인별 로그·200 멱등 불변.
  4. D①/D③-part1 회귀 보존(HandshakeResidueTests·StartupClearTests GREEN). 5. flake 배제 fresh 출력 인용(허위 MISMATCH/타이밍 0).
  6. 빌드 경고 증가 0·#1/#4/#7/#8 위반 0. 7. 정책 미확정(DENIED 재보고·CFlag 재시도)은 SPEC §7-B 등재만.

────────────────────────────────────────────────────────────────────────────
- Parallel Modules: N/A (single module·핸드셰이크 코어 인접 회귀 위험→직렬 1/1/1·fan-out 금지).
- Evaluation Dimensions: functional, concurrency/timing (2개·병렬 Evaluator 풀).
  · functional: D④ 원인 분리·IF-10 멱등 시맨틱·현동작 고정·데이터 무결·로그 정직성.
  · concurrency/timing: D② CFlagTimeout 결정성(±ε·무한대기 배제)·실-Sim 잔류/타이밍 회귀·flake 배제(다회)·arming 불변식 보존.
  APPROVED = 두 차원 모두 PASS(AND).

────────────────────────────────────────────────────────────────────────────
- Open Questions (★ 사용자 게이트 확정 2026-08-03):
  1. [D③-part2] ✅ **SCOPE OUT**(이번 미포함). journal.CreateSent 현 위치(핸드셰이크 완료 후) 유지. 크래시 감사 앵커는
     HS_C_SENT operation_log가 이미 커버. SPEC §7-B에 '수용된 갭(sorter_command SENT 행은 핸드셰이크 후 저널)'로 등재.
     VS-9 = N/A.
  2. [D④ 정책] ✅ **현동작 고정 + 원인별 WARN/alarm 로깅**. 원인 분리(enum)+원인별 로그(DENIED재보고·미존재→WARN,
     진짜중복→INFO '멱등 OK' 유지). 200 멱등 응답·차단 동작 보존. **정책 전환(DENIED 재보고 alarm 승격 등)은 이번 미포함**
     — SPEC §7-B 등재만. (alarm은 '후보' 수준 — Generator가 기존 IAlarmSink 재사용 여부 판단, 정책 변경 아님.)
  3. [프로젝트 타입] ✅ Backend/API 확정(프론트 미변경).

> Planner self-check — Detected project type: Backend/API. Required scenario slots: 3 (Endpoints touched, Happy path per endpoint, Error/branch cases per endpoint) + 9 sprint-specific(VS-1…VS-9). All slots filled: yes.
