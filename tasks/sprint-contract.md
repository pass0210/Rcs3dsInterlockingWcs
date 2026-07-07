# Sprint Contract — S-HANDSHAKE-RESIDUE (핸드셰이크 R_Flag 잔류 대사 — off-by-one 연쇄 차단)

> **감사 A-1 현장 발현 수정 스프린트** — 2026-07-06 현장 라이브 진단으로 확정된 결함.
> `HandshakeOrchestrator`가 R_Flag를 **레벨**로 읽어 직전 건(또는 PLC 기동)의 잔류 R_Flag=1을
> 새 건의 응답으로 오인 → 허위 RSEQ_MISMATCH → off-by-one 연쇄 자가지속.
> WHAT/WHERE/검증만 규정. **정확한 메서드·시그니처·appsettings 키명·값·구현 배치는 Generator 재량(제약 내). 코드 구현 0.**

## 0. 메타

| 항목 | 값 |
|------|-----|
| Sprint ID | S-HANDSHAKE-RESIDUE |
| Branch | `fix/handshake-rflag-residue` (생성 완료 — 현재 체크아웃) |
| Base | `develop` (PR #30까지 병합) |
| Detected Project Type | **Backend/API** (아래 투명성 노트 참조) |
| Scaling | **1 Planner / 1 Generator / 1 Evaluator** (단일 컴포넌트·팬아웃 없음) |
| Test baseline | **169 GREEN** (기동 시 `dotnet test`로 실제 카운트 재확인 — 완료 시 기존 전원 GREEN 유지 + 신규 시나리오 = 169+N) |
| 스펙 소스 | `docs/SPEC.md` §4(C/R 핸드셰이크)·§6(Sim3ds 동작) / `tasks/audit-20260701-full.md` A-1 / 현장 실측(2026-07-06) |
| 배경 상태 | 감사 A-1 CONFIRMED. 현장 로그로 5연쇄 재현 확인, 잔류 없는 깨끗한 상태에선 연속 2사이클 성공 확인. 수정 방향은 사용자 보고·확정 완료 — 본 계약은 이를 WHAT으로 구체화. |

> **투명성 노트(프로젝트 타입)**: 레포는 구조상 Full-stack(`frontend/` + `backend/`)이다. 그러나 **이 스프린트의 변경 표면은 100% 백엔드 Modbus 핸드셰이크 계층**(`Wcs.PlcGateway` + `Wcs.Sim3ds` 테스트 더블 + `Wcs.Tests` + `docs/SPEC.md`)이며 **frontend·브라우저 표면 접촉 0**이다. 교차되는 "레이어"는 오케스트레이터 ↔ 단일 쓰기 큐 ↔ Modbus ↔ Sim-PLC로, frontend↔backend 데이터 흐름이 아니다. 따라서 Full-stack의 브라우저 E2E 슬롯은 **적용 불가(false gate)**이고, 정답 검증은 **Backend/API 필수 검증 = 자동 테스트 코드 실행**(Sim3ds Modbus 더블 기반 xUnit 통합 테스트)이다. Playwright/브라우저 검증은 이 스프린트에 해당 없음.

## 1. 목표 (WHAT · 한 줄)

`HandshakeOrchestrator`의 R단계 레벨-읽기 결함을 **"C 기입 전 R_Flag==0 관찰 보장(arming)"** 으로 무장하고, **기동 시 잔류 R_Flag 대사(reconciliation)** 를 추가해, 직전 건·PLC 기동 잔류가 새 건의 응답으로 오소비되어 발생하는 **허위 RSEQ_MISMATCH off-by-one 연쇄를 근본 차단**한다. 모든 PLC 쓰기는 단일 큐 경유(절대규칙 #1), 대기 상한은 전부 appsettings(절대규칙 #7), 잔류 대사 발생은 operation_log(HANDSHAKE)에 잔류값 포함 기록(관측성).

## 2. Scope IN

### 2A. 핸드셰이크 시작 시 잔류 대사 (arming) — `HandshakeOrchestrator`
- **핵심 불변식**: 핸드셰이크 1건은 **R_Flag==0을 1회 관찰한 뒤에만** 이후 R_Flag==1 상승을 자기 응답으로 수용한다. 0 확인 이후의 레벨 읽기는 에지 감지와 등가.
- **동작(WHAT)**: C(CellAssign) 큐 투입 **이전**에 스냅샷 R_Flag를 확인 →
  - R_Flag==0 이면 → 그대로 진행(추가 지연 0 — 깨끗한 경로는 기존 타이밍 보존).
  - R_Flag==1 이면 → **잔류로 간주**: (1) WARN 로그 + (2) operation_log(HANDSHAKE) 기록 — 잔류값 `rCellNo`/`rSeq` 포함(§2D 관측성) + (3) **ClearR 선행 큐 투입**(절대규칙 #1 — 큐 경유) + (4) 폴링 스냅샷에서 **R_Flag==0 확인 대기** → 확인 후에만 C 기입 진행.
- **R_Flag==0 확인 대기 타임아웃**: 하드코딩 금지(절대규칙 #7) — 신규 appsettings 타이밍값(예: `Timing:RFlagClearConfirmTimeoutMs`, 키명·값 Generator 재량)에서 읽는다. 초과 시 **C를 기입하지 않고** 명확한 terminal Outcome으로 종결(§2C·시나리오 5). 대기 중 OFFLINE 감지도 기존 대기 루프들과 동형으로 명확 종결.
- 배치(대사 로직을 `ExecuteAsync` 내 C 투입 직전 별도 단계로 둘지, `WaitCFlagZeroAsync`류와 나란히 둘지 등)와 폴링·에지 채널(`RFlagRaised`) 소비 여부는 **Generator 재량**. 절대규칙 #1(쓰기=큐)·#7(타이밍=설정)만 불가침.

### 2B. 기동 시 잔류 R_Flag reconciliation — `Wcs.PlcGateway`
- **동작(WHAT)**: 소터 게이트웨이 기동 후 **첫 유효 폴에서 R_Flag==1**이면 PLC 기동 잔류로 간주 → **ClearR 큐 투입**(절대규칙 #1) + **WARN 로그 + operation_log 기록**(§2D — 잔류값 포함). PLC 기동 직후 R 영역 테스트 잔류(실측: R_CellNo=20, R_Seq=123)를 새 핸드셰이크가 소비하기 전에 차단.
- **위치·방식은 Generator 재량**(폴 루프 내 1회 게이트 / 레지스트리 StartAsync 완료 후 훅 등) — 단 **쓰기는 반드시 단일 큐 경유**(절대규칙 #1, API/오케스트레이터 직접 Modbus 호출 금지).
- **근거 명기(주석)**: 기동 잔류를 지우면 그 응답의 대기자는 없고 C_Seq 카운터도 리셋되므로 잔류 유지는 후속 전 건 오소비를 낳는다 — 클리어가 정당한 복구. (계약 문서화 요구, 코드 주석에도.)

### 2C. 잔류 대사 실패 경로 terminal Outcome — `HandshakeOrchestrator`
- R_Flag==0 확인 대기가 타임아웃(ClearR 미반영 — 소터 오프라인·PLC 무ack 등)하면 **C를 기입하지 않고** 명확·테스트 가능한 terminal Outcome으로 종결(무한 대기·더티 상태 진행 금지).
- 신규 `HandshakeOutcome` 값 추가 vs 기존값(예: OFFLINE 감지 시 `Offline`) 재사용은 **Generator 재량** — 단 (a) 조용히 성공/진행하지 않고, (b) 결과에 사유가 드러나며, (c) 테스트로 단정 가능해야 한다. `HandshakeResult` record 형상 확장이 필요하면 기존 소비처(관측 싱크·테스트) 회귀 0을 지킬 것.

### 2D. 관측성 — operation_log(HANDSHAKE) 잔류 기록
- 잔류 대사(2A)·기동 reconcile(2B) 발생 시 **operation_log의 HANDSHAKE 카테고리**에 잔류값(`rCellNo`/`rSeq`)을 포함해 기록 — 현장 원인 추적용.
- 기록 경로는 **기존 관측 훅 재사용**: `HandshakeOrchestrator.OnStage(action, detailJson)`(잔류 대사용 신규 action 예: `HS_R_RESIDUE`) / PlcGateway 측은 기존 `OnWrite`/전이 훅 및 로깅. **훅은 부수 기록 전용 — 핸드셰이크·폴 본동작 의미·타이밍 0 변경, 핸들러 예외 격리(fail-safe)**(기존 S-OBSERVABILITY 계약 동형). 신규 폴 루프·신규 DB 초크포인트 도입 0.

### 2E. Sim3ds — R 잔류 프리셋 수단 확인/추가 (테스트 더블)
- **먼저 확인**: `SimServer`가 실측 PLC 동작을 이미 모사하는가 — (i) C 지령 수락 시 C_Flag 자체 클리어(현재 `RunSimLoopAsync` L169-175 = 예), (ii) SortDuration 후 R 에코+R_Flag=1(L232-240 = 예), (iii) ClearR까지 R 영역 유지·자체 클리어 안 함(현재 Sim은 R 자체 클리어 없음 — WCS ClearR로만 R 비움 = 예). **에코 지연은 SortDurationMs로 모사됨.**
- **부재 확인분만 추가**: 테스트가 "핸드셰이크 시작 시점에 R_Flag=1(+R_CellNo/R_Seq 지정값) 잔류"를 결정적으로 세팅할 **프리셋 수단이 없으면** 추가(예: `Options`에 초기 R 잔류 필드, 또는 기동 후 R 영역 직접 세팅 API — HOW는 Generator). 실측 잔류값 (R_CellNo=20, R_Seq=123) 재현 가능해야 함.
- Sim의 **기존 동작·기본 무잔류 초기화(현재 `StartAsync`가 `Array.Clear` 후 Ready=1만 세팅)는 보존** — 프리셋은 명시 opt-in.

### 2F. 문서 동기화 — `docs/SPEC.md` §4
- §4 C/R 핸드셰이크에 **잔류 대사 규칙** 추가: "R단계는 레벨이 아니라 arming(C 기입 전 R_Flag==0 관찰 보장) 기반 — 시작 시 R_Flag==1이면 잔류로 대사(WARN+operation_log+ClearR 선행) 후 R_Flag==0 확인하고 C 기입" + "기동 첫 폴 R_Flag==1은 잔류로 클리어". 필요 시 §7-B의 관련 미확정 항목에 A-1 해소 1줄 교차 표기(선택).

### 2G. 신규 테스트 (`backend/tests/Wcs.Tests/`)
- §4 검증 시나리오 1~6을 자동 테스트로 결선(SQLite in-memory 더블 + Sim3ds Modbus 더블 — 기존 통합 테스트 패턴 재사용). 잔류 프리셋(2E)으로 결정적 재현.

## 3. Scope OUT (0 변경 — 무변경 가드)

- **동시 핸드셰이크 직렬화(F1b, todo.md)** — **OUT**. SPEC는 소터당 물리 직렬 dispatch 전제. 본 스프린트는 이를 **해결하지 않되 악화 금지**. ⚠ **주의(계약 명기)**: 잔류 대사 ClearR이 같은 소터에서 **진행 중인 다른 핸드셰이크의 응답을 지울 수 있다** — 순차 dispatch 전제가 유지되는 한 안전(한 소터엔 동시에 1건뿐). 동시 IF-10 허용은 별도 후속 스프린트.
- **R_Flag 타임아웃 재시도 vs 포기 정책(SPEC §7-B)** — **OUT**(여전히 미정). 본 스프린트는 진짜 무응답 타임아웃 경로(시나리오 6)를 **회귀 보존**만 한다.
- **핸드셰이크 정상 경로 의미 불변**: 깨끗한 상태의 성공/불일치/타임아웃 판정·C_Seq 증가·한 건씩 직렬·`OnStage` 기존 action 시그니처는 **불변**(2A 잔류 경로·2C terminal outcome·2D 신규 action 추가만).
- **frontend 0 변경**: 이 스프린트는 백엔드 전용. `frontend/` 디렉터리 diff 0.
- **셀 20/15 제약(S-FIELD-20CELLS)** — **OUT**(별도 스프린트, 다루지 않음).
- **Wcs.Core 판정 엔진 무접촉**: `DepositDecider`·`RegisterMap`·`PlcSnapshot` 의미 불변(순수 함수 — 절대규칙 #8). `RegisterMap` 상수 변경 0.
- **DB 스키마·마이그레이션 0**: operation_log는 조회/기록만(기존 경로), 스키마 변경 없음.
- **appsettings 기존 값 불변**: Sorters[]/Provider/ConnectionStrings/기존 Timing 값 0 변경(신규 `RFlagClearConfirm` 류 타이밍 키 **추가만**).

## 4. Verification Scenarios (Backend/API — 필수, per-type 슬롯)

> 서피스 정의: 이 스프린트의 검증 표면은 **HTTP 엔드포인트 형상 변경이 아니라** 내부 핸드셰이크 실행 경로다. 아래 "surface"를 (a) `HandshakeOrchestrator.ExecuteAsync(cellNo, ct)` 소터별 핸드셰이크(운영상 IF-10 `POST /api/v1/deposit-report` → TriggerSorterHandshake로 구동), (b) `PlcPollingService` 기동 폴 reconciliation, (c) 신규 appsettings 타이밍 키, (d) Sim3ds 잔류 프리셋 테스트 더블 표면으로 매핑한다. 모든 시나리오는 **Sim3ds Modbus 더블 기반 자동 xUnit 통합 테스트**로 검증(수동 curl/코드리뷰 대체 금지).

**Slot 1 — 이 스프린트가 건드리는 surface(엔드포인트/실행 경로) 목록**:
- `HandshakeOrchestrator.ExecuteAsync` — R단계 arming(2A) + 잔류 대사 실패 terminal outcome(2C) 추가.
- `Wcs.PlcGateway`(PlcPollingService 또는 레지스트리 기동 경로) — 기동 첫 폴 잔류 reconciliation(2B). 쓰기는 단일 큐 경유.
- 신규 appsettings 타이밍 키(예: `Timing:RFlagClearConfirmTimeoutMs`) — R_Flag==0 확인 대기 상한.
- `SimServer` — R 잔류 프리셋 수단(2E, 부재 시 추가).
- `docs/SPEC.md` §4 — 잔류 대사 규칙 문서(2F).

**Slot 2 — Happy path (정상 입력 → 기대 결과 형상)**:
- **[S1] 잔류→대사→성공**: R 영역에 (R_CellNo=20, R_Seq=123, R_Flag=1) 프리셋 → 핸드셰이크 시작 → **잔류 대사(ClearR 선행) 후 R_Flag==0 확인 → C 기입 → 진짜 응답 대사** → `HandshakeOutcome.Success`. (기존 레벨-읽기 코드였다면 이 케이스는 `RSeqMismatch`였음 — 회귀 대조로 fix 입증.) operation_log에 잔류값(rCellNo=20/rSeq=123) 포함 HANDSHAKE 기록 존재.
- **[S4] 무잔류 정상 경로 회귀**: 깨끗한 상태(R_Flag=0)에서 연속 2건 핸드셰이크 → 2건 모두 `Success`, **추가 지연·잔류 대사 발화 0**(깨끗한 경로 기존 동작·타이밍 보존).

**Slot 3 — 관련 에러/에지 케이스 (Planner가 해당분만 선정 — 패딩 금지)**:
- **[S2] off-by-one 연쇄 재현→전건 성공**: 잔류 존재 상태에서 같은 소터 **연속 3건**(현장 back-to-back 재현) → **3건 모두 `Success`**. (기존 코드: 3건 모두 `RSeqMismatch` 연쇄 — 자가지속 연쇄가 차단됨을 단정.)
- **[S3] 기동 reconcile**: 게이트웨이가 R_Flag=1(+R_CellNo/R_Seq 잔류) 상태에서 폴링 시작 → 잔류가 **ClearR로 클리어됨**(스냅샷 R_Flag==0 도달) + **WARN 로그** + operation_log HANDSHAKE 잔류 기록. 이후 첫 핸드셰이크가 잔류를 오소비하지 않음.
- **[S5] R_Flag==0 확인 타임아웃**: 잔류 대사 ClearR이 반영되지 않는 상황(소터 오프라인/PLC 무ack 모사) → 신규 확인-대기 타임아웃 경로가 **C를 기입하지 않고** 명확한 terminal Outcome(2C)으로 종결(무한 대기·더티 진행 없음). 테스트로 outcome 단정.
- **[S6] 진짜 R_Flag 무응답 타임아웃 회귀**: 응답 자체가 없는 경우(Sim `InjectNoResponse` 등) → 기존 `RFlagTimeout` 경로가 그대로 동작(회귀 보존). arming 도입이 이 경로를 훼손하지 않음.
- **[S7] 전체 스위트 GREEN + 빌드 경고 0**: `dotnet test backend/Wcs.sln` → 기존 169 전원 GREEN + 신규 S1~S6 GREEN(합계 169+N), 실패 0. `dotnet build` 경고 0(기존 관행). 동시성/타이밍 취약분은 기존 flake 교훈대로 **≥5회 반복 + stash 대조**로 회귀 귀속(1회 GREEN 신뢰 금지 — S9/E2E flake 교훈).

## 5. Deliverables & 완료 조건 (Completion Gate)

> **Fresh evidence 의무**: 모든 PASS는 "지금 실제로 돌린" raw 증거(테스트 러너 요약 raw line·`git diff --stat`·operation_log/로그 발췌·`dotnet run`+Sim3ds 콘솔이 필요하면 그 출력)를 `tasks/sprint-feedback.md`에 인용. Generator 보고·추정·이전 결과만으론 PASS 금지.

- **① fix 입증(핵심)**: S1·S2가 GREEN이고, **동일 시나리오가 수정 전 코드에선 `RSeqMismatch`였음**을 대조 증거(stash/이전 커밋 대비 또는 arming 제거 시 RED)로 제시 — "레벨→arming" 전환이 실제로 연쇄를 끊었음을 입증.
- **② 기동 reconcile(S3)·확인 타임아웃(S5)·무응답 회귀(S6)** 각각 자동 테스트 GREEN + 명확 outcome 단정.
- **③ 무잔류 회귀(S4)**: 깨끗한 경로 성공 + 잔류 대사 미발화(추가 지연 0) 확인.
- **④ 전체 169+N GREEN**(S7): raw 요약 인용. 타이밍 취약분 ≥5회 반복 + stash 대조.
- **⑤ 절대규칙 준수 입증**: #1 — 모든 쓰기(잔류 ClearR·기동 reconcile ClearR)가 **단일 큐 경유**임을 소스/`OnWrite` 발화로 확인(오케스트레이터·API 직접 Modbus 호출 0). #7 — R_Flag==0 확인 타임아웃이 **appsettings에서 바인딩**(하드코딩 grep 0). #8 — `Wcs.Core` 판정 무접촉. #3 — TgtFloor 클리어 0(R 클리어만, 본 스프린트 대상·정당).
- **⑥ 관측성**: 잔류 대사·기동 reconcile 발생 시 operation_log(HANDSHAKE)에 잔류값 포함 기록됨을 실증(테스트 또는 라이브 로그 발췌).
- **⑦ 무변경 가드**: `git diff --stat` 판독 → 변경이 §2 IN 표면(`Wcs.PlcGateway`·`Wcs.Sim3ds`·`Wcs.Tests`·`docs/SPEC.md`·`appsettings*.json` 신규 타이밍 키)에만 국한. `git diff -- frontend` 빈 출력. `git diff -- backend/src/Wcs.Core` 판정 의미 불변. DbSeeder·마이그레이션·Sorters/Provider/ConnectionStrings 값 diff 0.
- **⑧ 관측 무영향(fail-safe)**: 신규 `OnStage` action·기록 콜백이 논블로킹·예외 격리(핸드셰이크·폴 본동작 지연/중단 0) — 소스 확인 + ④ 타이밍 회귀로 실증.

**Completion**: ①~⑧ 전부 PASS + `tasks/lessons.md`에 A-1 교훈(레벨 vs 에지/arming·잔류 대사·기동 reconcile·off-by-one 연쇄 기전) 1행 + `docs/SPEC.md` §4 동기화 완료 + 프로세스/포트 정리(:1502·:5080 free) + git status 핸드오프 동일.

## 6. 함정 (Traps)

1. **깨끗한 경로 타이밍 회귀**: arming을 "항상 R_Flag==0 대기"로 구현하면 정상 건마다 지연 추가 위험. **R_Flag가 이미 0이면 지연 0으로 즉시 진행**(대기는 잔류 케이스에만). ③으로 실증.
2. **PollIntervalMs(150) > RFlagPollMs(100) 창**: A-1의 실창. 잔류 대사는 이 창을 닫는 것이 목적 — R_Flag==0 확인을 **폴링 스냅샷 갱신 기준**으로 하되, 확인 대기 폴 간격과 타임아웃을 appsettings로.
3. **ClearR 미반영 시 무한 대기**: R_Flag==0 확인 루프에 반드시 타임아웃(2C·S5) + OFFLINE 감지 종결. 더티 상태로 C 기입 진행 금지.
4. **동시 핸드셰이크(F1b)와 혼동 금지**: 본 건은 '동시'가 아니라 '순차 연속'에서도 발생하는 별개 근본원인. per-소터 락으로 해소되지 않음. 잔류 ClearR이 동시 진행 건 응답을 지울 수 있다는 주의(순차 dispatch 전제) — Scope OUT에 명기, 악화 금지.
5. **Sim R 자체 클리어 금지 보존**: 실측 PLC는 ClearR을 ack으로 R 유지(자체 클리어 안 함). Sim이 이 동작을 바꾸면 잔류 재현·회귀가 오염됨 — Sim 기존 R 유지 동작 보존, 프리셋만 opt-in 추가.
6. **테스트 flake 귀속**: 핸드셰이크 통합 테스트는 타이밍 취약. 1회 GREEN 신뢰 금지 — ≥5회 반복 + stash 대조로 회귀 귀속(S9/IT4b/E2E 부하 flake 교훈). 고아 `Wcs.Sim3ds.exe`가 포트 점유·빌드 파일잠금 유발 가능 — 실패 시 kill 후 재시도.
7. **관측 훅 fail-safe**: 신규 잔류 기록 콜백에서 동기 I/O·블로킹·예외 누수 금지(EmitStage try/catch 동형). 폴/핸드셰이크 핫패스 비지연.
8. **테스트 provider 함정(교훈)**: 통합 테스트는 in-memory SQLite 더블. `Database:Provider` 즉시평가 키 override는 `builder.UseSetting`으로(2026-06-30 교훈) — 단 본 스프린트는 DB 스키마 무변경이라 대개 무관.

## 7. Questions / Assumptions (모호점)

> 수정 방향은 사용자 보고·확정 완료(배경). 아래는 Generator 재량 설계점과 전제 — **블로킹 질문 없음**. 진행 중 스펙 모호가 새로 발견되면 `docs/SPEC.md` "미확정 사항"에 기록.

- **A1 (전제)**: 소터당 **순차 dispatch** 유지(SPEC 물리 직렬 전제). 동시 IF-10 직렬화(F1b)는 본 스프린트 밖 — 잔류 대사는 이 전제 위에서 안전.
- **A2 (Generator 재량)**: R_Flag==0 확인 타임아웃 키명·기본값, arming 로직 배치, `RFlagRaised` 에지 채널 소비 여부, 신규 `HandshakeOutcome` 값 추가 vs 기존값 재사용 — 전부 Generator 결정(제약: 절대규칙 #1·#7·#8, terminal outcome은 테스트 가능·비-silent).
- **A3 (전제)**: 기동 reconcile은 첫 유효(Online) 폴 기준 1회. 클리어된 잔류 응답의 대기자는 없고 C_Seq 리셋 상태이므로 유지가 아니라 클리어가 정당한 복구.
- **A4 (확인 대상)**: Sim3ds가 실측 PLC 동작(C_Flag 자체 클리어·SortDuration 에코 지연·ClearR까지 R 유지)을 이미 모사함(2E에서 확인). **부재 확인분은 R 잔류 프리셋뿐** — 그것만 추가.

> Planner self-check — Detected project type: Backend/API. Required scenario slots: 3 (Slot 1 surface 목록, Slot 2 happy path[S1·S4], Slot 3 error/edge[S2·S3·S5·S6·S7]). All slots filled: yes.
