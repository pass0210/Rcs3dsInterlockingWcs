# Sprint Feedback — S-TRACE-LOG-VIEWER

## APPROVED (Evaluator, 2026-07-28, FIX ITERATION 1) — 최종 정본

FIX ITERATION 1(4-Tier 코드리뷰 CRITICAL 1건 수정) 재검증 완료 — 전 완료조건 + 전 시나리오 GREEN.
Ground truth: HEAD e46e77c(미커밋 fix) / branch feat/trace-log-viewer / SDK 10.0.301.
※ iteration-1 APPROVED 는 조기종결(HS_C_SENT 미도달) 경로의 _pending 누수·pId 오귀속을 놓쳤음(해피패스만 검증한
   Evaluator 사각 — evaluator-concurrency-blindspot). 4-Tier 코드리뷰가 CRITICAL 로 포착 → 수정 → 본 재검증으로 확정.

### CRITICAL 수정 검증 (TraceCorrelator _pending 누수 + pId 오귀속)
- 버그: RcsController 가 ExecuteHandshakeAsync 직전 RegisterHandshake 를 무조건 호출하나, HS_C_SENT(이벤트 4·유일
  소비자 ResolveCSent) 前 조기 종결(시작 OFFLINE·잔류 실패·안착 OFFLINE·C_Flag 대기 OFFLINE·CFlagTimeout)하면 등록 head
  미소비 → _pending 무한 증가 + FIFO 고아 head 가 다음 성공 핸드셰이크를 이전 피스 pId 로 오귀속(완료조건 #6 무력화).
- 수정(전부 Wcs.Api·HandshakeOrchestrator/PlcGateway/Wcs.Core 무접촉): RegisterHandshake 가 **소비플래그(Consumed)
  토큰** 반환 → ResolveCSent 가 head pop 시 Consumed=true → RcsController continuation **최상단에서 무조건**
  `DiscardPending(destId, token)`(idempotent: 소비됐으면 no-op, 미소비면 그 토큰만 identity 로 제거). SentCSeq 판정 불요.
  + 소터별 상한 MaxPending=32(WARN 축출) + PendingCount 진단.
- **코드 분석 — 설계 타당**: (a) Consumed 는 ResolveCSent(실 이벤트4 소비자)만 set → "head 가 소비됐는가"의 ground truth
  라, cSeq 증가 여부와 무관하게 정확. 코드리뷰의 `SentCSeq==0` 은 cSeq 증가 後·HS_C_SENT 前 종결(CFlagTimeout 등)을
  놓치므로 토큰 방식이 **엄밀히 우월** — 생성자의 override 정당. (b) 모든 큐/Consumed 접근이 sp.Lock 하 직렬화. (c)
  DiscardPending 위치가 continuation 최상단(`stopping` 조기 return·scope 생성 前) → 성공/실패/조기종결/호스트종료 전
  경로 실행. `.ContinueWith` 기본 옵션(항상 실행). (d) LinkedList.Remove(reg)=참조 identity → 동시 등록된 다음 피스 무영향.
- **격리 라이브 스택 실증(독립 재현)**: Sqlite scratch + 실 Sim TCP:1512 + API :5215 + TraceLog scratch(D:\실경로/5205/
  COM1/1502/운영DB 무접촉). ① 소터 online → Sim kill → OFFLINE → 조기종결 피스 pId=26401 IF-05+IF-10 → 전용 파일에
  **이벤트 1·3만(4·5·6 없음)** — HS_C_SENT 미도달 확인. ② Sim 재기동 → online → 성공 피스 pId=26402 IF-05+IF-10 →
  **이벤트 4·5(·6) 가 pId=26402·cSeq=1 로 기입**(raw 파일 인용):
  `[4] {"eventNo":4,...,"pId":26402,"cSeq":1,"cellNo":1,...}` / `[5] {"eventNo":5,...,"pId":26402,"cSeq":1,...}`.
  raw grep: **event-4 라인 중 26401 언급 0건** → 고아 head 가 없어 오귀속 0·누수 0. (event 6 은 재기동 후 C_Flag 폴
  샘플링 타이밍으로 본 ad-hoc run 에선 지연 — by-design 샘플링 의존·계약 명시. E2E N2 가 simLoopMs=150 으로 4·5·6 전량 +
  PendingCount==0 을 결정적 단언·GREEN.)

### 신규 회귀 테스트 3건 (자체 재실행 GREEN)
- unit Correlator_AbortedBeforeCSent_DiscardCleansPending_NoMisattribution — 등록(100) discard → PendingCount 0 →
  등록(200) ResolveCSent → **PId==200(≠100)** 오귀속 0. (구코드면 고아 head 100 pop → RED.)
- unit Correlator_PendingCap_BoundsUnboundedGrowth — 미소비 200건 → PendingCount ≤ 32.
- E2E N2_AbortedBeforeCSent_ThenSuccess_NoPendingLeak_NoMisattribution — 실 Sim OFFLINE-before-C → 재기동 → 성공 →
  PendingCount==0 + 이벤트 4·5·6 전부 successPid(≠abortedPid). 강한 회귀 가드.

### 빌드·테스트 (자체 재실행)
- `dotnet test backend/Wcs.sln` — **488 통과 / 0 실패 / 0 건너뜀**(독립 재실행). 485(iter-1) + 3(신규) = 488 산술 일치.
- `~Trace` 필터 **11/11 GREEN × 3회(flake 0)**. 488 − 11 = 477 baseline 불변 → **회귀 0**.
- 빌드 오류 0 / 경고 12 전부 선재(NU1903 ×10 + xUnit2013 ×2 untouched 라인). 신규 CS 경고 0.
- 프론트 tsc/lint/build exit 0(scratch outDir·wwwroot 무접촉). 선재 경고만.

### 회귀 0 (iter-1 검증 항목 재확인)
- 6개 이벤트 번호 태깅·상관: 성공 피스에서 1·2·3·4·5(·6) 정확·pId+(chuteNo,cSeq) 재구성 유지.
- additive: operation_log 종전대로(22행 STATE/PLC_WRITE/HANDSHAKE/API) · 전역 Serilog 트레이스 라인 0 · 전용 파일 6종만.
  D:\Rcs3dsInterlockingWcsLogs 무접촉. IF-05/IF-10 형상·400 검증 불변.
- 스코프/규칙: Wcs.Core/Migrations/WcsDbContext/Sim3ds소스/PlcGateway/HandshakeOrchestrator **zero-diff**(규칙 #8).
  규칙 #1(관측/로깅 전용): DiscardPending 은 인메모리 정리(write-queue/PLC-write 0). fix 는 RcsController+TraceLogService+
  TraceLogPage(note)만.

### IMPORTANT(문서만) — 한 소터 동시 IF-10 상관 교차 (계약 수용·스코프 밖)
TraceCorrelator 상단 주석 + 프론트 뷰어 note(TraceLogPage L114-117: "상관(pId↔cSeq)은 소터별 순차 dispatch 전제 —
한 소터 동시 투입 시 교차 가능") 로 알려진-한계 노출 완료. 직렬화는 시도 안 함(SPEC §6 순차 dispatch 전제) — **적정**.
(프론트 note 는 정적 텍스트 — 코드 검사 + tsc/lint/build 로 검증. 뷰어 동작은 iter-1 브라우저 검증 W1~W6 에서 확인 완료.)

### Minor (비블로킹 — tasks/todo.md 에 S-TRACE-LOG-VIEWER 섹션으로 등재 완료)
reconnect 재시드 · path-literal 기본값 · TailLines 휴리스틱 · Detail JSON 보간 · one-shot 컨슈머 루프 · 필터 그룹 churn
(코드리뷰 지시대로 미수정·오케스트레이터 후속 이관). 동시 IF-10 상관 교차는 문서화 완료·별도 스프린트.

전 완료조건(1~6) + 전 시나리오(W1~W6·B1~B3·E1~E2) GREEN — **APPROVED (fix iteration 1)**. 코드 수정/커밋/푸시 없음.
