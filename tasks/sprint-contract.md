# Sprint Contract — S-S5-FLAKE (핸드셰이크 S5 테스트 결정적 안정화)

> Planner Subagent · 2026-07-08
> 대상: `backend/tests/Wcs.Tests/HandshakeResidueTests.cs` 의 **S5**
> (`S5_ResidueClearNotReflected_TerminalTimeout_NoCWritten`).
> 이 테스트는 `dotnet test backend/Wcs.sln` **전체 스위트를 xUnit 기본 병렬로** 돌릴 때만 간헐 실패하고,
> 단독 실행·재실행에서는 통과한다. 최근 여러 스프린트의 Evaluator가 매번 "첫 실행 S5 blip = 기존 flake"로
> 귀속해 왔다(B2B 작업의 회귀 아님 — B2B는 순수 HTTP + in-memory SQLite로 타이밍 표면 없음).
> **이번 스프린트의 목표는 이 flake를 근본 원인 규명 후 결정적으로 제거하는 것. 마스킹 금지.**

---

## ⚠ Questions for user (착수 전 확인 — 각 항목 권장 기본값 있음. 답 없으면 기본값으로 진행)

- **Q1 — 근본 원인이 (a) 테스트 하네스/시뮬레이터 경합 vs (b) 실 프로덕션 타이밍 버그로 밝혀지면 어디까지 손대나.**
  **권장 기본값:** 이번 스프린트는 **테스트 하네스/Sim 경합을 결정적으로 고친다**(하단 근본원인 가설 H1이 유력 —
  프로덕션 핸드셰이크 동작은 현행이 스펙상 옳다). 만약 재현 과정에서 **진짜 프로덕션 결함**(예: arming/폴 루프의
  실제 타이밍 버그)이 드러나면, **프로덕션 코드를 고치기 전에 문서화하고 사용자에게 먼저 보고**한다
  (`Wcs.PlcGateway` 핸드셰이크 코어 = 보호구역, 절대규칙 #1~#5). Sim/테스트 전용 수정이 강하게 선호됨.

- **Q2 — 반복 실행 증명의 N.** **권장 기본값 N = 10**(전체 스위트 `dotnet test backend/Wcs.sln` **연속 10회 전부 GREEN**).
  교훈("s9-flake-under-e2e-load")이 "1회 GREEN은 신뢰 금지"라고 명시하므로 N=1은 불가. 시간이 허용하면 20회 권장.
  추가로 S5를 **단독** + **인위적 병렬 부하 하 스트레스**(하단 시나리오)로도 GREEN이어야 한다.

---

## Goal

`HandshakeResidueTests.S5`가 병렬 부하 하에서 간헐 실패하는 **진짜 메커니즘을 재현·규명**하고,
**고정 sleep·임의 재시도·어서션 약화 없이(절대규칙 #7)** 결정적으로 통과하도록 만든다.
수정은 **테스트 하네스/Sim 계층 우선**이며, 프로덕션 핸드셰이크 동작(`HandshakeOrchestrator`)은 원칙적으로 불변.

---

## Implementation Scope (Generator가 할 일 — 순서 강제: REPRODUCE → ROOT-CAUSE → FIX)

`systematic-debugging` / workflow-rules Error Recovery 4-Phase 규율을 따른다. **재현 없는 수정 불가.**

### 1. REPRODUCE (수정 착수 전 필수 — 이 단계 산출물 없이는 FIX 진입 금지)
- 전체 스위트를 **반복 실행해 실패를 실제로 재현**한다: `dotnet test backend/Wcs.sln`를 **≥10~20회** 돌리거나
  (예: 반복 스크립트), 그리고/또는 S5를 **인위적 병렬 부하** 하에서 반복 실행해 특정 경합을 유발한다.
- **실제 실패를 캡처**한다: 어느 `Assert`가 깨졌는지, `result.Outcome`의 실제값(예: `RFlagResidueTimeout`
  아닌 값), `HS_C_SENT` 발화 여부, 그때의 타이밍/로그. 캡처된 실패 메시지 원문을 `tasks/sprint-log.md`에 인용.
- **주의(귀속 정확성):** S5에는 실패 지점이 둘 이상 있을 수 있다 —
  (i) 전제 대기 `WaitUntilAsync(() => h.Gw.Latest.RFlag, 2000, "GW가 잔류 R_Flag=1 관찰")` 타임아웃,
  (ii) 본 어서션 `Assert.Equal(RFlagResidueTimeout, result.Outcome)` / `DoesNotContain(HS_C_SENT)` 실패.
  **어느 쪽인지 메시지로 확정**한 뒤에만 원인을 귀속한다(추정 금지).

### 2. ROOT-CAUSE (가설 확인 — Generator가 재현으로 어느 것이 참인지 확정)
후보 가설(코드 근거 포함 — Planner 사전 분석. Generator는 재현으로 검증·확정):

- **H1 (유력) — Sim sticky-residue 재천명 ↔ GW 폴 사이의 일시적 `R_Flag=0` 창.**
  arming(`HandshakeOrchestrator.ArmRFlagZeroAsync`, `HandshakeOrchestrator.cs:151`)은 잔류 감지 시
  **ClearR를 단 1회** 큐에 넣고(`:170`), 이후 `_gw.Latest.RFlag==0`이 관찰되면 arming 완료로 간주해 `null` 반환
  → C 기입 진행. 그런데 Sim의 sticky 고장(`SimServer.RunSimLoopAsync`, `SimServer.cs:277`)은
  **WCS의 ClearR RMW가 서버 버퍼에 `R_Flag=0`을 실제로 쓴 뒤**, 다음 Sim 루프(`SimLoopMs=10ms`)의
  `PullFromServerLocked`→재천명→`FlushToServerLocked` **시점에야** `R_Flag=1`을 되돌린다.
  그 사이 `[RMW write, 다음 Sim flush]` 구간(최대 ~`SimLoopMs`, 부하 시 Sim 루프 지연으로 확대)에
  서버 버퍼는 `R_Flag=0`을 노출한다. 이때 GW 폴(`PollIntervalMs=30ms`)이 그 창을 샘플링하면
  `_gw.Latest.RFlag`가 0이 되고, arming이 **거짓 완료**(HS_R_ARMED) → C 기입(HS_C_SENT) →
  outcome이 `RFlagResidueTimeout`이 아니게 됨 → S5 어서션 실패.
  xUnit 기본 병렬에서 무거운 실 Sim/웹호스트 테스트(E2E·ScenarioTests·ApiIntegrationTests)와 **동시 CPU 경합**이
  Sim 루프/폴 위상을 흔들어 이 창의 샘플링 확률을 키운다 → 간헐성. **성격: Sim 충실도(fidelity) 갭 = 테스트 하네스 결함**
  (실 무ack PLC는 WCS 쓰기를 무시하므로 `R_Flag`가 애초에 0으로 떨어지지 않는다 → 프로덕션 arming 동작은 옳다).
- **H2 — 교차 테스트 포트/자원 경합.** 각 테스트는 `GetFreePort()`(TOCTOU) + `StartRobustAsync` 재시도로 격리되고,
  Harness는 Sim/GW/큐를 인스턴스별 소유(공유 static 없음, `_cSeq`는 인스턴스 필드). 재현으로 **공유 상태·포트 충돌이
  실제 S5 실패에 기여하는지** 확인(유력하지 않음 — 확인 후 배제 가능).
- **H3 — teardown 채널 경합.** 이미 `DisposeAsync`에서 `Queue.Writer.TryComplete()`로 완화됨
  (`HandshakeResidueTests.cs:99`). 이는 종료 행/크래시를 유발할 뿐 **S5 어서션 실패(잘못된 outcome)의 원인은 아님** — 배제 검증.
- **H4 — 비동기 로그/스냅샷 경합.** S5 어서션이 읽는 `h.Stages`는 `OnStage`(HandshakeOrchestrator만 발화)로 채워지고
  모든 `EmitStage`가 `await ExecuteAsync` 내에서 **동기 완료**되므로 S5의 특정 어서션엔 해당 없을 가능성 높음 — 배제 검증.

> Generator는 재현 근거로 **정확히 하나의 근본 원인을 명명**하고 `tasks/sprint-log.md`에 기록한다.
> "부하가 높아서"는 근본 원인이 아니다 — 어느 공유 값/창이 어떤 순서로 잘못 관찰되는지까지 특정할 것.

### 3. FIX AT ROOT (결정적 — 확인된 근본 원인이 요구하는 방식으로)
확인된 원인에 맞춰 **아래 제약 안에서** 수정:
- **H1이 확정이면(유력) — Sim 충실도 수정(테스트 하네스, 선호):** sticky 고장을 "ClearR가 반영되지 않음"으로
  **충실히** 모델링해, WCS(=GW 폴)가 **`R_Flag=0`을 한 번도 관찰하지 못하도록** 일시 창을 제거한다.
  후보(Generator가 실현 가능성 확인 후 택1):
  - Sim의 재천명을 WCS 쓰기와 **동일 경로에서 동기적으로** 수행(예: FluentModbus `ModbusServer`의 쓰기/변경
    통지 훅이 있으면 그 핸들러에서 즉시 `R_Flag=1` 복원 — 창 ≈ 0), 또는
  - sticky 모드에서 WCS의 R_Flag clear가 **서버 버퍼에 0으로 남지 않도록**(쓰기 즉시 복원) 모델링.
  - 어느 쪽이든 결과 불변식: **"ClearR 미반영" 고장 하에서 GW의 `_gw.Latest.RFlag`는 arming 창 내내 1로 유지**되어
    outcome이 **결정적으로 `RFlagResidueTimeout`**, C 미기입.
- **격리가 진짜 원인이면(H2류):** xUnit collection으로 해당 자원 직렬화 또는 테스트별 고유 포트/자원.
  단 **단순 직렬화(DisableParallelization)는 부하를 낮춰 확률만 줄이는 마스킹**이므로,
  H1이 참인 한 직렬화를 근본 수정으로 채택하지 말 것(정당한 자원 경합이 입증된 경우에만).
- **금지(절대):** 고정 `Thread.Sleep`/`Task.Delay`로 창 회피, 임의 재시도로 실패 은폐, 어서션 약화/삭제,
  하드코딩 ms(모든 타임아웃은 이미 `PlcGatewayOptions`/appsettings `Timing:*` — 절대규칙 #7).
- **프로덕션 불변 원칙:** `Wcs.PlcGateway`(`HandshakeOrchestrator`·`PlcPollingService`)는 **보호구역**.
  근본 원인이 진짜 프로덕션에 있지 않은 한 손대지 않는다. 만약 프로덕션 수정이 불가피하면 **Q1대로 먼저 보고**,
  최소 영향 원칙, 그리고 "핸드셰이크 동작 불변" 입증(회귀 시나리오 S1~S4·S6 전부 유지)을 추가로 통과시킬 것.

### 4. 하우스키핑
- 실행 후 **고아 `Wcs.Sim3ds.exe` 프로세스 없음**을 확인(교훈: MSB3021/파일잠금 = 고아 exe). 있으면 kill 후 재빌드.
- 새 하드코딩 타임값 0(추가 설정이 필요하면 `PlcGatewayOptions` + appsettings `Timing:*`에 키로).

---

## Evaluation Criteria (Evaluator 판정 기준 + 가중치)

- **[40%] 결정성 증명:** `dotnet test backend/Wcs.sln` **연속 10회(Q2, N=10) 전부 GREEN**을 Evaluator가
  **독립 재실행**해 fresh 증거(각 회차 요약 출력 원문)로 확인. 1회 GREEN은 불충분(교훈 명시).
- **[25%] 근본 원인 정합성:** 명명된 근본 원인이 캡처된 재현과 **인과적으로 일치**하고, 수정이 그 원인을 실제로
  제거하는지(수정 되돌리면 재현이 되살아나는 대조 — stash/revert 후 flake 재출현 확인). "부하 탓" 수준 귀속 불허.
- **[20%] 마스킹 아님:** 고정 sleep·임의 재시도·어서션 약화·하드코딩 ms 부재. 수정이 S5의 **원 의도**
  (ClearR 미반영 → 터미널 타임아웃 → C 미기입)를 **보존**. Sim 충실도가 실 PLC 무ack 의미와 어긋나지 않음.
- **[10%] 회귀 0 + 프로덕션 불변:** 나머지 스위트(현재 ~288건 + 이번 추가분) 전건 GREEN. `Wcs.PlcGateway`
  핸드셰이크 코어 미변경(변경했다면 Q1 보고 + 불변 입증).
- **[5%] 위생:** 고아 Sim3ds.exe 없음, 빌드 클린, 변경은 최소 영향(테스트/Sim 우선).

---

## Completion Conditions (Evaluator PASS 최소 조건 — 전부 충족해야 PASS)

1. **캡처된 재현이 문서화됨**: 수정 전 실패의 실제 어서션/outcome/타이밍이 `tasks/sprint-log.md`에 원문 인용.
2. **근본 원인 1개 명명** + 그 원인이 재현과 일치함이 대조(revert 시 재현)로 뒷받침.
3. **`dotnet test backend/Wcs.sln` 연속 10회 전부 GREEN**(Evaluator 독립 재실행 fresh 증거).
4. **S5 단독 GREEN** + **인위적 병렬 부하 하 스트레스 GREEN**.
5. **회귀 0**: 전체 ~288건(+이번 추가분) 전건 GREEN, 실패/스킵으로 은폐 없음. 특히 S1~S4·S6가 새로 flaky해지지 않음.
6. **새 하드코딩 타임값 0**(모든 타이밍 설정 경유), 고정 sleep/임의 재시도/어서션 약화 없음.
7. **고아 `Wcs.Sim3ds.exe` 없음**, 빌드 클린.
8. 프로덕션 핸드셰이크 코어를 건드렸다면 Q1대로 사전 보고 + 핸드셰이크 동작 불변 입증 통과.

---

- **Parallel Modules:** N/A (single module — 단일 테스트/Sim 경합 수정, 파티션 불가).
- **Evaluation Dimensions:** functional only (결정성·근본원인 단일 차원. 보안/성능 표면 없음).

- **Detected Project Type:** **Backend/API**
  (프로젝트 신호: `backend/src/Wcs.Api`에 ASP.NET Core Controllers + 서버 엔트리포인트 존재.)
  단, **이번 스프린트의 변경 표면에는 HTTP 엔드포인트가 없다** — 대상은 내부 컴포넌트 seam
  (`HandshakeOrchestrator.ExecuteAsync`/`ArmRFlagZeroAsync`, `PlcPollingService` 폴/쓰기 루프,
  `SimServer` 고장주입)과 이를 구동하는 xUnit 하네스다. 아래 Backend/API 슬롯을 **그 컴포넌트 seam 단위로**
  구체화한다(제네릭 금지). 프로덕션 API 코드는 불변 목표.

- **Verification Scenarios (Backend/API — 컴포넌트 seam 단위로 구체화):**

  - **이번 스프린트가 건드리는 seam 목록(엔드포인트 대체 — 내부 진입점):**
    - HTTP 엔드포인트: **없음(0개)** — 프로덕션 API/컨트롤러 미변경.
    - seam ①: `SimServer.InjectStickyRResidue` 고장주입 경로(`Wcs.Sim3ds/SimServer.cs`) — sticky 재천명 로직.
    - seam ②: `HandshakeOrchestrator.ExecuteAsync(cellNo)` → `ArmRFlagZeroAsync`
      (`Wcs.PlcGateway/HandshakeOrchestrator.cs`) — 잔류 arming/터미널 종결 판정(관찰만; 원칙적 불변).
    - seam ③: `HandshakeResidueTests.S5` 및 공용 하네스(`StartRobustAsync`/`Harness`/`WaitUntilAsync`).

  - **Happy path per seam (기대 입력 → 기대 출력 shape):**
    - **S5 정상(고친 후):** 잔류(`R_CellNo=20,R_Seq=123,R_Flag=1`) + `InjectStickyRResidue=true` + `clearConfirmMs=300`
      입력 → `result.Outcome == RFlagResidueTimeout`, `SentCSeq == 0`, `ReceivedRSeq == 123`, `ReceivedRCellNo == 20`,
      Stages에 `HS_R_RESIDUE_TIMEOUT` 존재 · `HS_C_SENT` **부재**. **이 결과가 병렬 부하와 무관하게 결정적.**
    - **Sim 충실도 불변식:** sticky 고장 활성 구간에서 `_gw.Latest.RFlag`가 arming 창 동안 관측상 **한 번도 0이 되지 않음**
      (수정의 핵심 관찰). 재현 하네스가 이 불변식을 직접 단언(가능하면).

  - **Relevant error/edge cases per seam (해당되는 것만 — 패딩 금지):**
    - **회귀 보존(무잔류 정상 — S4):** `R_Flag=0` 시작 → arming 즉시 진행, 잔류 대사 미발화, 연속 건 성공. 수정이 이를 깨지 않음.
    - **회귀 보존(진짜 무응답 — S6):** `InjectNoResponse=true` → `RFlagTimeout`(잔류 타임아웃과 구분) 보존.
    - **회귀 보존(잔류→대사→성공 — S1/S2, 기동 reconcile — S3):** sticky 아님(정상 ClearR 반영) 경로에서 arming이
      정상 완료돼 `Success`가 결정적 유지(수정이 정상 ClearR 반영 창을 손상시키지 않음 — S1~S3가 flaky해지지 않을 것).
    - **부하 스트레스:** S5(및 필요시 S1~S6)를 인위적 병렬 부하 하에서 반복 → 결과 불변.
    - **teardown:** 반복 실행 후 종료 행/크래시 없음, 고아 Sim3ds.exe 없음(H3 완화 유지 확인).

  - **Web/UI 시나리오:** **N/A** — 이번 스프린트는 프론트엔드 표면을 전혀 건드리지 않는다(백엔드 테스트/Sim 인프라 전용).

> Planner self-check — Detected project type: Backend/API. Required scenario slots: 3 (endpoints/seams touched, happy path per seam, error/edge cases per seam). All slots filled: yes.
