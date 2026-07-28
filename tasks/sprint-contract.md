[Sprint Contract] — S-IF10-CWRITE-SETTLE-DELAY

═══════════════════════════════════════════════════════════════════════════════
현장 사실(사용자→PLC 벤더 확인): 3DS PLC에는 틸트 낙하 지연(TiltDelay)이 없다.
PLC는 C_Flag=1 감지 → C(셀 지정) 읽는 즉시 그 셀로 라우팅/틸트한다(안착 대기 0).
AGV는 3DS에 틸트하는 즉시(틸트 시작 시점 추정) IF-10을 보낸다. 따라서 WCS가 IF-10
수신 후 C를 지연 없이 쓰면, AGV가 떨군 제품이 소터에 물리적으로 안착하기 전에 소터가
움직여 오분류/낙하 위험이 있다.
요구: WCS가 IF-10 수신 후 C 기입(CellAssign 큐 투입) 전에 설정값만큼 "안착 지연(settle
delay)"을 두어, 제품 안착 후 C를 쓴다. (IF-10 HTTP 200 ack은 즉시 유지 — fire-and-forget.)
브랜치: feat/if10-cwrite-settle-delay (develop 기준).
═══════════════════════════════════════════════════════════════════════════════

## 폐기된 전제(부활 금지)
- 이전 스프린트 S-IF10-CWRITE-WAIT("C 기입 완료까지 대기 후 IF-10 응답")은 전제오류로
  폐기·develop 복귀됨. IF-10 응답을 지연시키는 어떤 접근도 금지. IF-10은 즉시
  200 {result:"OK"} ack — fire-and-forget. 지연은 오직 백그라운드 핸드셰이크 경로에만 둔다.

## 미확정 — 사용자/벤더 확인 필요 (추측 금지 — 값은 현장 확정)
- Q1. 안착 물리 소요(ms) 실측값 — 미정. appsettings 값을 "현장 실측 후 조정"으로 두고
  잠정 기본값으로 출하(아래 Default 정책). ※ 이 스프린트는 "지연 메커니즘 + 설정 결선"을
  구현하고, 정확한 ms는 현장 실측으로 채운다.
- Q2. IF-10 전송 시점이 틸트 "시작"인지 "완료"인지 — 사용자 "것 같음"(불확실). 틸트 시작이면
  settle = (낙하 소요 + 안착) , 완료면 settle = (안착만). 이 차이가 잠정값 산정 근거에 영향 —
  실측 시 함께 확인. (메커니즘은 두 경우 모두 동일: IF-10 수신 기준 지연.)
- Q3. 3DS가 "제품 안착 감지" 레지스터/센서를 Modbus로 노출하는가 — 있으면 시간지연 대신
  신호 폴링(안착 확인 후 C)이 더 견고. 이 스프린트는 노출 없음을 전제로 "시간 지연"을
  구현하되, 노출이 확인되면 후속 스프린트에서 신호 폴링으로 대체 가능하도록 지연 지점을
  단일 훅으로 캡슐화한다(설계 유연성만 확보 — 신호 폴링 구현은 본 스프린트 범위 밖).

────────────────────────────────────────────────────────────────────────────────
- Goal:
  3D 소터 IF-11 핸드셰이크에서 C(CellAssign) 큐 투입 직전에 설정값 기반 "안착 지연"을
  삽입한다. 지연은 백그라운드 핸드셰이크 경로에서만 발생하고, IF-10 HTTP 응답·요청 스레드를
  절대 블로킹하지 않는다. 지연값은 appsettings Timing 설정(소터 공통 + 소터별 오버라이드),
  코드 기본값 0(=현행과 동일·무해). C 기입은 반드시 기존 단일 쓰기 큐 경유(직접 Modbus 금지).
  기존 핸드셰이크 불변식(arming·R 폴링/대사·ClearR·복귀 대기·OFFLINE 종결)과 기존 테스트에
  회귀 0.

────────────────────────────────────────────────────────────────────────────────
- 설계 논점 결정(WHAT — Generator가 임의결정 금지, HOW 세부는 Generator):

  ◆ D1. 지연의 위치와 arming과의 상대 순서 【확정】
    핸드셰이크 1건의 순서를 다음으로 확정한다:
      (1) OFFLINE 사전 확인
      (2) arming (R_Flag==0 관찰 보장; 잔류 시 ClearR 선행 + R_Flag==0 확인)
      (3) ★안착 지연(settle delay)★   ← 신규. arming 이후, C 기입 이전.
      (4) C_Flag==0 대기(WaitCFlagZeroAsync — Online 재확인 포함)
      (5) CellAssign(C) 큐 투입  = C 기입
      (6) R 폴링 → 대사 → 복귀 대기 → ClearR
    즉 **안착 지연은 arming "후", C 기입 "전"** 에 둔다. 근거:
      · arming은 읽기(+잔류 시 ClearR)라 소터를 물리적으로 움직이지 않아 지연을 그 뒤로 미뤄도
        안전하다. 지연을 arming 뒤에 두면 (a) arming 소요(잔류 대사 시 최대
        RFlagClearConfirmTimeoutMs)가 IF-10 수신 이후 경과 벽시계에 자연 반영되어 중복 대기가
        줄고(D2 기준시점과 정합), (b) arming이 조기 종결(잔류 타임아웃/OFFLINE)되면 어차피
        폐기될 핸드셰이크에 지연을 낭비하지 않는다.
      · 지연 직후 C 기입 직전에 (4) C_Flag==0 대기가 Online을 재확인하므로, 지연 도중 OFFLINE이
        발생해도 C가 절대 기입되지 않는다(더티 진행 금지·D3와 정합).

  ◆ D2. 지연의 기준 시점 【권장·확정: IF-10 수신 시각】
    지연의 기준(anchor)은 **IF-10 수신 시각**(틸트 시각 근사)으로 한다. 핸드셰이크 시작 시각이
    아니다. 실제 대기량 = max(0, SettleDelayMs − (지연 지점 도달 시점 − IF-10 수신 시각)).
    근거: 물리적 안착 시계는 AGV가 틸트한 순간(≈IF-10 수신)부터 흐른다. IF-10 수신과 지연 지점
    사이에는 투입 기록(DB)·셀 선택·번들 조회·태스크 스케줄·OFFLINE 사전확인·arming(잔류 시 최대
    수 초)이 이미 경과한다. handshake-start 기준(또는 무조건 SettleDelayMs 대기)은 이 경과분을
    이중 계상해 과대 지연을 유발한다. IF-10 수신 기준 + 잔여 clamp(≥0)가 물리적으로 정확하다.
    · 구현 유연성: 지연 지점 이전 경과가 잠정 기본값에 비해 무시할 수준임을 Generator가 실측·기록하면
      무조건 지연(Task.Delay(SettleDelayMs))으로 단순화 허용(단, 기준시점 선택을 sprint-log에 명시).
      기본 요구는 IF-10 수신 기준 잔여 대기.
    · 단조/시계: 경과 계산은 단조 시계 권장(벽시계 역행 방지) — 세부는 HOW.

  ◆ D3. 지연 도중 호스트 종료/OFFLINE 종결 정책 【확정 — 더티 진행 금지】
    · 호스트 종료(ApplicationStopping 취소): 지연 대기는 취소 토큰(현행 `stopping`)을 존중해
      즉시 중단하고 **C를 기입하지 않고** 깔끔히 종결(기존 종료 방어와 정합 — ContinueWith 콜백은
      이미 stopping 취소 시 영속화를 건너뜀). 취소 전파(OCE)든 terminal outcome 반환이든
      "C 미기입 + 더티 진행 0"이면 허용.
    · OFFLINE(종료 아님) 발생: 지연 종료 후 C 기입 직전 (4) C_Flag==0 대기의 Online 재확인이
      Offline outcome으로 종결시켜 **C 미기입**을 보장한다. (권장) 지연 대기 자체도 Online을 관찰해
      조기 종료하면 응답성이 좋다(HOW 재량) — 단 최소 요구는 지연 후 C 기입 전 Online 재확인으로
      OFFLINE 시 C 미기입.
    · 어떤 경우에도 절대규칙 #1(단일 쓰기 큐) 유지 — 지연은 순수 대기이며 큐/Modbus를 건드리지 않는다.

  ◆ D4. 설정 키 이름/위치 · 공통 vs 소터별 【확정】
    · 위치: appsettings `Timing` 하위. 키명 `SettleDelayMs`(Generator가 최종 확정 가능하나 의미
      명확한 ms 단위 키). 코드 바인딩 대상: `PlcGatewayOptions`(게이트웨이/핸드셰이크가 소비 —
      Program.cs가 "Timing" 섹션을 `Get<PlcGatewayOptions>()`로 읽음) 신규 필드. `TimingOptions`
      미러도 정합 유지(다른 소비자 대비). 두 record 모두 코드 기본값 0.
    · 공통 단일값 + 소터별 오버라이드: 현행 Timing 구조(RFlagTimeoutMs 등 전부 공통 +
      `Sorters[].Timing` 오버라이드 + `BuildGatewayOptions`에서 `t?.X ?? common.X` 병합)를 그대로
      따른다. → `SorterTimingOverride.SettleDelayMs`(nullable) 추가 + `BuildGatewayOptions`에
      `SettleDelayMs = t?.SettleDelayMs ?? commonTiming.SettleDelayMs`. 근거: 소터마다 낙하 높이·
      기구가 달라 안착 시간이 다를 수 있으므로 소터별 오버라이드가 필요(기존 모든 Timing 키와 동형).
    · Sim3ds의 `TiltDelayMs`와 혼동 금지: Sim의 TiltDelayMs는 "시뮬레이터가 흉내내는 PLC 내부
      지연" 모델이고, 본 스프린트의 SettleDelayMs는 "WCS가 C 기입 전에 두는 안착 대기"로 별개 개념·
      별개 위치다. Sim 동작·설정은 무변경.

  ◆ D5. 기본값(default) 정책 【확정 — 코드 기본 0, appsettings 0(실측 후 켜기) — 사용자 승인 2026-07-28】
    · 코드/record 기본값 = 0. 이유: (a) 0이면 현행과 완전 동일(무해·회귀 0), (b) 게이트웨이/핸드셰이크
      옵션을 직접 생성하는 ~20개 기존 테스트(ExecuteAsync 호출부·DefaultGwOpt)가 SettleDelayMs를
      지정하지 않으므로 record 기본을 0으로 두면 기존 타이밍이 바이트 동일하게 유지된다(결정성 보존).
    · appsettings.json `Timing:SettleDelayMs` = **0으로 출하**(사용자 결정: "실측 후 켜기") +
      `_comment_SettleDelayMs`에 "★현장 실측 후 조정 — 3DS PLC는 TiltDelay 0. IF-10 수신~안착 물리
      소요를 실측해 이 값을 양수로 설정하면 안착 지연 활성. 0이면 지연 비활성(현행 동작)." 명시.
      즉 이 스프린트는 **지연 메커니즘 + 설정 훅만 배선**하고 운영 기본은 비활성(0)으로 출하한다.
      현장 실측값을 넣는 순간(양수) 보호가 켜진다. 잠정 참고 브라켓(실측 시 시작점) = 수백 ms 수준
      (Sim TiltDelayMs 200과 동 규모)이나, 출하값은 0이며 값은 사용자가 현장에서 직접 채운다.
    · ⚠ 함의: 출하 상태(0)에서는 보호가 꺼져 있다 — 안착 전 C 기입 위험은 사용자가 실측값을
      SettleDelayMs에 넣기 전까지 잔존한다(사용자 인지·승인). SettleDelayMs>0 동작은 테스트 하니스가
      양수값을 주입해 검증한다(Completion #2).

────────────────────────────────────────────────────────────────────────────────
- Implementation Scope (파일별 · Generator가 "어떻게"를 결정):

  【핸드셰이크 — 안착 지연 삽입】
  · backend/src/Wcs.PlcGateway/HandshakeOrchestrator.cs — ExecuteAsync:
      arming(ArmRFlagZeroAsync) 반환 후 · C_Flag==0 대기/CellAssign 이전에 안착 지연 삽입(D1 순서).
      지연량 = D2 기준(IF-10 수신 시각 anchor, 잔여 clamp≥0), 값 = _opt.SettleDelayMs. 취소 토큰
      존중(D3). SettleDelayMs<=0이면 지연 완전 생략(추가 대기 0·경로 무변경). 관측 훅(OnStage)에
      HS_SETTLE_WAIT 등 부수 스테이지 1건 발화 권장(기존 EmitStage 패턴 — 핸드셰이크 의미 불변).
      ★ 시그니처는 **역호환 유지**: `ExecuteAsync(int cellNo, CancellationToken ct = default)`에
        anchor(IF-10 수신 시각)를 **선택적(optional) 파라미터**로 추가(예 `DateTime? depositedAtUtc
        = null` — null이면 anchor=현재/handshake-start). ~20개 기존 호출부가 무수정 컴파일되어야 함
        (회귀 0 하드 제약). 필수 파라미터 추가 금지.
  · backend/src/Wcs.PlcGateway/PlcGateway.cs — PlcGatewayOptions: `SettleDelayMs`(int, 기본 0) 추가
      + 요약 주석(하드코딩 금지·절대규칙 #7 근거).

  【설정 결선 — Wcs.Api】
  · backend/src/Wcs.Api/Infrastructure/SorterGatewayRegistry.cs — SorterBundleHandle
      .ExecuteHandshakeAsync: anchor 전달을 위해 선택적 파라미터 추가(역호환) → _handshake.ExecuteAsync에
      위임. (번들 코어 로직 무변경 — 박막 위임.)
  · backend/src/Wcs.Api/Controllers/RcsController.cs — DepositReport/TriggerSorterHandshake:
      IF-10 수신 시각(anchor)을 캡처해 ExecuteHandshakeAsync에 전달. 이미 조회하는 piece.DepositedAt
      (=IF-10 투입 보고 시각) 재사용 또는 컨트롤러 진입 시점 단조 시각 캡처(HOW 재량). fire-and-forget
      구조(`_ = bundle.ExecuteHandshakeAsync(...).ContinueWith(...)`)·IF-10 200 즉시 응답 **불변**.
  · backend/src/Wcs.Api/Program.cs — TimingOptions.SettleDelayMs(기본 0) 미러 추가;
      SorterTimingOverride.SettleDelayMs(int?) 추가; BuildGatewayOptions에 병합
      (`SettleDelayMs = t?.SettleDelayMs ?? commonTiming.SettleDelayMs`).
  · backend/src/Wcs.Api/appsettings.json — Timing에 `SettleDelayMs`=**0**(사용자 결정: 실측 후 켜기) +
      `_comment_SettleDelayMs`("현장 실측 후 조정 — 양수로 설정 시 활성, 0=비활성/현행"). Sorters[].Timing는
      `{}` 유지(공통 상속 — 오버라이드 예시는 주석으로).

  【테스트】
  · 게이트웨이/핸드셰이크(HandshakeReturnClearTests 하니스 패턴 재사용):
      - S1 SettleDelayMs>0 → C(CELL_ASSIGN 쓰기 / HS_C_SENT 스테이지)가 핸드셰이크 시작(또는 IF-10
        anchor) 이후 최소 ~SettleDelayMs 경과 전에는 발생하지 않음(하한 실측). SettleDelayMs=0 →
        추가 지연 0(현행과 동일 — 상한 실측).
      - S2 지연 도중 OFFLINE → C 미기입(CELL_ASSIGN 스테이지/쓰기 부재) + terminal outcome.
      - S3 지연 도중 호스트 종료(취소) → C 미기입·깔끔 종결(더티 진행 0).
      - S4 arming 순서 보존: 잔류(R_Flag=1 프리셋) → ClearR 선행 → 안착 지연 → C. 깨끗한 경로(R_Flag=0)는
        arming 즉시 통과 후 지연 → C.
      - S5 anchor(D2): IF-10 수신~지연 지점 경과가 SettleDelayMs를 이미 초과하면 추가 대기 ≈0(잔여
        clamp) 실증.
  · 설정 바인딩: 공통 Timing:SettleDelayMs + 소터별 오버라이드가 BuildGatewayOptions에서 정확히 해소
      (오버라이드 있음/없음).
  · API: IF-10(3D) 200 즉시 응답이 SettleDelayMs와 무관(응답 지연 없음) — 응답 왕복 시간이 SettleDelayMs에
      영향받지 않음 실측 + C 쓰기는 그 후 지연 발생(백그라운드).
  · 회귀: 기존 HandshakeReturnClearTests·HandshakeResidueTests·PlcGatewayIntegrationTests·
      MultiSorterSameBusTests·ScenarioTests·Sim3dsRtuTests·ApiIntegrationTests·E2E 전량 GREEN(코드 기본 0).

  【docs】
  · docs/SPEC.md §4/§6 — "3DS PLC는 TiltDelay 0(안착 대기 없음). WCS가 IF-10 수신 후 C 기입 전
      SettleDelayMs 안착 지연(설정)을 둔다(arming 후·C 전, IF-10 수신 기준, 코드 기본 0/운영 양수,
      OFFLINE·종료 시 C 미기입)"을 반영. §4-A/§4-B/복귀 대기 서술과 정합. Q1~Q3 미확정 기록.

  【무접촉 (변경 금지)】
  · IF-10 응답 형상·타이밍(즉시 200 {result:"OK"} ack·fire-and-forget) — 절대 지연 금지(폐기된
    S-IF10-CWRITE-WAIT 부활 금지).
  · arming(잔류 대사·ClearR 선행·R_Flag==0 확인)·R 폴링/대사·복귀 대기(Ready==1)·ClearR·OFFLINE
    종결·StartupClear·2층 제어·pending-floor 큐 — 로직 불변(지연 삽입 외 무변경).
  · 단일 쓰기 큐(절대규칙 #1)·TgtFloor 게이트(#2/#3)·Ready 의미(#4)·D4 RMW·Wcs.Core 순수성(#8).
  · Sim3ds(TiltDelayMs 등 시뮬레이터 모델)·프론트엔드 전부·DB 스키마/마이그레이션(0).

────────────────────────────────────────────────────────────────────────────────
- Evaluation Criteria (가중치):
  ★★★ 안착 지연 정확성 — SettleDelayMs>0에서 C(CellAssign) 기입이 안착 지연 경과 전에는 발생하지
       않음(하한) · SettleDelayMs=0에서 현행과 동일(추가 대기 0) · 지연 위치가 arming 후·C 전(D1) ·
       기준시점 IF-10 수신(D2, 잔여 clamp).
  ★★★ 비블로킹 & 종결 안전 — IF-10 200 즉시 응답(응답이 SettleDelayMs에 영향 0) · 지연 도중
       OFFLINE/호스트 종료 시 C 미기입·더티 진행 0(D3) · C 기입은 단일 큐 경유(#1).
  ★★  설정 결선 — Timing:SettleDelayMs 공통 + 소터별 오버라이드 정확 해소 · 코드 기본 0/appsettings
       출하 0(실측 후 켜기) · 하드코딩 0(절대규칙 #7).
  ★★  회귀 — 기존 핸드셰이크/게이트웨이/E2E 테스트 전량 GREEN · ExecuteAsync 시그니처 역호환(기존
       호출부 무수정) · Sim/프론트/스키마 무접촉.
  (Scope) — 무접촉 경계 준수(IF-10 응답 타이밍·Sim·마이그레이션 0·git diff 한정).

────────────────────────────────────────────────────────────────────────────────
- Completion Conditions (전부 충족):
  1. dotnet test 전량 GREEN — 신규(S1~S5·설정 바인딩·API 비블로킹) 포함, Evaluator 독립 재실행.
     baseline 대조 `총 − 신규 = 기존`(기존 회귀 0).
  2. SettleDelayMs>0 하니스에서 C 기입 시점이 anchor+지연 하한 이후임을 관측 훅/레지스터 전이로 실증
     (fresh 로그/스테이지 인용). SettleDelayMs=0에서 동일 경로가 추가 지연 없이 통과 실증.
  3. IF-10(3D) HTTP 응답 왕복이 SettleDelayMs와 무관(즉시 200) — 자동화 테스트로 응답 시간 vs
     SettleDelayMs 독립 실증(코드 리뷰 대체 금지).
  4. OFFLINE-중-지연 및 종료-중-지연에서 CELL_ASSIGN(C) 미발생 실증(스테이지/쓰기 부재 단언).
  5. 하드코딩 스캔: SettleDelayMs 관련 코드에 리터럴 지연 상수 0(전부 설정 경유). appsettings
     `Timing:SettleDelayMs`=0 출하 + 주석에 "현장 실측 후 조정"(양수=활성) 존재.
  6. git diff 스코프 한정(무접촉 경계 diff 0 — IF-10 응답 로직·Sim·마이그레이션). 마이그레이션 diff 0.
  7. sprint-log.md에 `## IMPLEMENTATION COMPLETE` + Generator 재량 결정(anchor 캡처 방식·키명 확정·
     HS_SETTLE_WAIT 스테이지 유무·무조건-지연 단순화 채택 시 근거) 기록.

────────────────────────────────────────────────────────────────────────────────
- Parallel Modules: N/A (single module — PlcGateway 지연 삽입 + Wcs.Api 설정 결선이 시그니처로
    강결합). 1/1/1.
- Evaluation Dimensions: functional only (타이밍/동시성은 Craft·Functionality에 흡수).

────────────────────────────────────────────────────────────────────────────────
- Detected Project Type: Full-stack
  (레포 구조 신호: frontend/src/*.tsx 브라우저 진입점 + backend/src/Wcs.Api/Controllers/*.cs 서버
   라우트가 같은 레포에 공존 → 규칙상 Full-stack. ※ 단, 이 스프린트의 실제 변경 표면은 전적으로
   백엔드(Wcs.PlcGateway 타이밍 + Wcs.Api DI/컨트롤러/appsettings)이며, 프론트 파일·API 응답 형상·
   cross-layer 데이터 흐름은 무변경 → Web/UI·cross-layer 슬롯은 근거 있는 N/A, 실검증은 Backend/API에
   집중.)

────────────────────────────────────────────────────────────────────────────────
- Verification Scenarios (Full-stack — 슬롯 전부 채움; UI/cross-layer는 근거 N/A):

  === Web/UI ===
  · Default state: N/A — 이 스프린트는 프론트 파일을 전혀 건드리지 않고 새 화면/상태를 만들지 않는다
    (게이트웨이 타이밍 + 백엔드 설정 결선 only). 모니터 UI는 C 쓰기가 지연만큼 늦게 표시될 뿐 신규
    표면 0.
  · Alternate states: N/A — 동일(신규 UI 상태 없음).
  · Empty/error state: N/A — 동일(신규 UI 오류 상태 없음).
  · Dark mode: N/A — 프로젝트 단일 테마 + 프론트 무변경.
  · Key interaction flow: N/A — 사용자 브라우저 상호작용 변경 없음(변화는 WCS↔PLC Modbus C 쓰기
    타이밍, 비가시).

  === Backend/API ===
  · Endpoints touched: POST /api/v1/deposit-report (IF-10) — **응답 형상/타이밍 불변**(200
    {result:"OK"} 즉시). 행위 변경은 백그라운드: 3D 목적지에서 C(CellAssign) 큐 투입 전에 SettleDelayMs
    안착 지연이 삽입됨(arming 후·C 전, IF-10 수신 기준). 다른 엔드포인트 무변경.
  · Happy path:
    - IF-10(3D, SettleDelayMs>0) → 즉시 200; 백그라운드 C 기입은 안착 지연 경과 후에만 발생 →
      정상 R 대사/복귀로 완료. 응답 시간은 SettleDelayMs와 독립.
    - IF-10(3D, SettleDelayMs=0) → 현행과 완전 동일(추가 지연 0).
    - IF-10(슈트/비-3D) → 핸드셰이크 미트리거 → 안착 지연 무관(경로 무변경).
    - 중복 IF-10(멱등) → 즉시 200, 핸드셰이크/지연 재발 없음.
  · Error cases:
    - 지연 도중 소터 OFFLINE → C 미기입(더티 진행 0), terminal outcome(Offline 계열).
    - 지연 도중 호스트 종료(ApplicationStopping) → 취소 존중, C 미기입, 영속화 스킵.
    - 3D인데 빈 셀 없음(FULL)/번들 없음(OFFLINE) → 기존대로 ExecuteHandshake 미진입(지연 무관).
    - 입력 검증 실패(pId/barcode/chuteNo/qty 상한) → 기존대로 400(지연 이전 단계 불변).

  === Full-stack — cross-layer 데이터 흐름 ===
  · N/A(프론트↔백 cross-layer 변경 없음) — 근거: 변경은 WCS 내부 백엔드 경계(HTTP IF-10 수신 →
    Modbus C 쓰기 타이밍)에 국한되며 RCS↔WCS HTTP 계약·프론트 데이터 흐름은 불변. 다만 "IF-10 200
    즉시 응답(HTTP) + C 쓰기 지연(Modbus)"의 층 경계 동작은 위 Backend/API happy/​error 시나리오에서
    HTTP 응답 시간 vs Modbus C 쓰기 시점의 독립성으로 실증한다(둘 다 백엔드 계층).

────────────────────────────────────────────────────────────────────────────────
- 검증 인프라 격리 (현장 오염 0):
  · 백엔드/핸드셰이크: 실 SimServer(TCP·격리 포트) + PlcPollingService + HandshakeOrchestrator
    직접 번들(기존 HandshakeReturnClearTests 하니스). 실 PLC·COM1/RTU 절대 미접촉.
  · API 비블로킹 검증: 격리 백엔드(전용 여유 포트, appsettings 5205 이기도록 --urls 최우선) + Sqlite
    scratch 또는 전용 DB(현장 운영 DB 무접촉). 소터 경로 필요 시 SimServer/데드-TCP override.
  · SettleDelayMs 관측 테스트는 짧은 지연값(수십~수백 ms) + 조건 폴링(고정 sleep 금지)으로 결정성 확보.

────────────────────────────────────────────────────────────────────────────────
> Planner self-check — Detected project type: Full-stack. Required scenario slots: 9
  (Web/UI: default-state, alternate-state, empty/error-state, dark-mode, key-interaction-flow [5개 모두
   근거 N/A — 프론트 무변경]; Backend/API: endpoints-touched, happy-path, error-cases [3개 실검증];
   Full-stack: cross-layer-data-flow [근거 N/A — 층 경계 동작은 Backend/API에서 실증]).
  All slots filled: yes.
