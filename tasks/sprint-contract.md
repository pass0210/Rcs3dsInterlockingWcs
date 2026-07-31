[Sprint Contract] — S-IF08-PUSH-LOG-THROTTLE

- Goal:
  IF-08 chute-state push (WCS→RCS) 실패 재시도가 운영로그(operation_log WARN "FAIL")와
  추적로그(트레이스 이벤트 8/10 result:"FAIL")에 **매 복구-하트비트 주기마다 새 행으로 폭주**하는 것을
  **소스에서 억제**한다(RCS 호스트가 죽어 있으면 현재는 무한정 누적). 억제 대상은 오직 "반복되는 같은 실패"의
  로깅이다:
    · 첫 실패(전이)      → 두 sink 각각 정확히 1건 (신호 유지)
    · 반복되는 같은 실패 → 두 sink 각각 0건 (주기 재발신에도 추가 로그 0)
    · 복구(실패→성공)    → 정확히 1건 (현행 성공 로깅으로 자연 충족 — 회귀 0)
  **Fail-Loud 유지 — 완전 무음 금지.** 실제 push 재시도·복구 하트비트 재발신·Acked/Computed 전이 로직·
  전이당 1회 발신은 전부 **불변**(로깅만 조절). RCS로의 재시도는 성공할 때까지 계속되되 그 실패를 매번
  기록하지만 않는다.

- Implementation Scope: (Generator가 구현할 것들)
  1. 반복 실패 억제(핵심): `ChuteStatePushClient.PushAsync`가 재시도 소진 시 내는 **세 FAIL 로그**
     — (a) `_opLog.Log(API, CHUTESTATE_PUSH, WARN, detail=…"FAIL"…)` (약 :189),
       (b) `EmitPushTrace(… "FAIL" …)` → 트레이스 이벤트 8(next_state=2)/10(next_state=3) (약 :192),
       (c) `_log.LogError(…재시도 소진…)` Serilog 파일/콘솔 라인 (약 :184-187) ← **OQ-4 확정: 억제 포함**
     — 를 **같은 실패가 반복될 때 셋 다 emit 하지 않는다**(운영로그·추적로그·Serilog 동일 정책 — OQ-3/OQ-4 확정).
     (재시도 루프 내 per-attempt `_log.LogWarning`은 스코프 밖 — 한 호출 내 국소.)
  2. 첫 실패 1건 보존: 같은 억제 단위(= route, next_state — OQ-2)에서 **첫 실패**는 두 sink 각각 정확히
     1건 남긴다. 이후 같은 단위의 반복 실패는 0건.
  3. 복구 1건 보존: 실패→성공 전이 시 **현행 성공 로깅 경로(:143-146 OK oplog + OK trace)를 그대로**
     내보낸다(성공 로깅 무변경). 억제 상태는 **성공 시 리셋**되어, 복구 후 다시 실패하면 그것이 새 "첫 실패"로
     1건 로깅되어야 한다(신호 재무장).
  4. 억제 단위 전이 시 재무장: 같은 route가 계속 실패하는 중이라도 산출 next_state가 바뀌면(예 BUSY↔READY)
     새 전이로 간주해 새 첫 실패 1건을 남긴다(OQ-2 권고 = 예).
  5. 설정화(절대규칙 #7): 억제 on/off·(있다면) 임계·"아직 실패 중" 요약 주기 등 모든 상수는
     appsettings `Wcs:ChuteStatePush` 섹션 값으로 외부화한다. 하드코딩 리터럴 0.
  6. **저빈도 "아직 실패 중" 주기 요약 로그 채택(OQ-1 확정)**: 첫 실패~복구 사이에 설정 주기(appsettings,
     기본은 하트비트 주기의 수십~수백 배 예: 수 분)마다 요약 1건. 완전 무음 금지(Fail-Loud). 이 요약은
     운영로그(또는 Serilog) 어느 sink로 낼지는 Generator 재량이되 저빈도·설정 주기·전이 리셋 시 초기화.
  7. 스레드안전: 억제 상태(직전 로깅한 실패 등)의 갱신은 Pusher의 per-route `RouteState.Gate` 락 안에서
     원자적으로 수행한다(비원자 check-then-act 금지). in-flight/전이 판정과 동일 임계구역.
  8. **불변 보존(로깅 외 전부)**: 4-시도 재시도·지수 백오프·DORMANT 가드·성공 판정(2xx+flag==1)·
     하트비트 재발신 루프·Acked/Computed/PushInFlight 전이 로직·전이당 단일 발신·성공 push 로깅은
     한 줄도 바꾸지 않는다.
  9. 절대규칙 #8: Wcs.Core 무접촉(본 변경은 Wcs.Api 계층). 판정 로직 무변경.
  10. 회귀 대상 테스트 갱신(로그-카운트 단언에 한함): 억제로 로그 행 수를 단언하는 테스트가 있으면 새 억제
     시맨틱에 맞게 갱신한다. **단 push 재시도/재발신 동작·Acked 갱신·전이당 1회 발신·delivery 카운트를
     단언하는 부분은 불변**이어야 한다(로깅만 조절). 대상: `ChuteStatePushTests`,
     `ChuteRecoveryPushHeartbeatTests`, `TraceReadyPushTests`, `RcsPushTests`,
     `SorterPushOperationalTests`, `TwoFloorHostRoutingTests`, E2E `E2EGroupL_TwoFloorHostPushTests`.

  ── HOW를 고정하지 않음(Generator 재량) ──
    · "직전 로깅한 실패" 상태를 어디에 둘지(후보: `RouteState`에 last-logged-result 필드 / 클라이언트가
      Pusher로부터 억제 힌트를 받음 / 기타), 정확한 메서드 시그니처·필드명·주입 방식은 **Generator가 결정**한다.
      계약은 결과 시맨틱(첫 1 / 반복 0 / 복구 1 / 양 sink 동일 / 동작 불변)만 고정한다.
    · 판정에 필요한 컨텍스트(route별 Computed/Acked/직전 결과)는 이미 Pusher가 보유하고, 클라이언트는 호출
      간 상태가 없다는 사실만 계약이 전제한다.

- Implementation Scope 밖(SCOPE OUT):
  · Wcs.Core / DepositDecider / 판정 로직(#8 zero-diff).
  · push 재시도·백오프·재발신·Acked/Computed·전이당 1회 발신·성공 로깅 동작(로깅 억제 외 무변경).
  · 프론트(TraceLogPage 등) 코드 — 이 스프린트는 소스 억제만(프론트 diff 0).
  · 재시도 루프 내 per-attempt `_log.LogWarning`(한 호출 내 국소 — OQ-4 결정 전까지 스코프 밖).

- Evaluation Criteria: (Evaluator 판정 기준 + 가중치 — Backend/API 기준, backend-only 변경)
  1. Functionality / Correctness (★★★) — 억제 시맨틱이 정확한가:
       · 첫 실패 = 두 sink 각 1건, 반복 실패 = 두 sink 각 0건(주기 N회 재발신에도 추가 0),
       · 복구 = 성공 로그 1건 + 억제 리셋(복구 후 재실패 시 다시 1건),
       · next_state 전이 시 새 첫 실패 1건.
  2. Architecture / Craft — 억제가 판정/전달과 비침습(★★★): 재시도·백오프·Acked/Computed·전이당 1회 발신·
     성공 로깅이 byte-identical(diff로 실증). 억제는 순수 로깅 게이트이며 delivery에 0 영향.
  3. Craft — 스레드안전·Fail-Loud(★★): 억제 상태 갱신이 per-route 락 내 원자적(check-then-act 없음),
     완전 무음 아님(첫 실패+복구 항상 남김; OQ-1 채택 시 저빈도 요약).
  4. Functionality — 설정화·경계(★★): #7 준수(상수 appsettings), Wcs.Core zero-diff(#8),
     양 provider(SqlServer 운영/SQLite 테스트) GREEN, 기존 전체 스위트 GREEN(회귀 0).

- Completion Conditions: (Evaluator PASS 최소 조건 — 전부 AND)
  C1. RCS 호스트가 계속 다운인 상태에서 하트비트가 같은 실패 전이를 **N 주기(N≥3)** 재발신해도
      operation_log의 CHUTESTATE_PUSH WARN "FAIL" 행이 **정확히 1건**, 트레이스 이벤트 8 또는 10의
      result:"FAIL" 레코드가 **정확히 1건**(합계 N건이 아님)임을 자동화 테스트로 실증.
  C2. 같은 시나리오에서 WCS→RCS PUT delivery 시도는 **매 주기 계속 발생**(재시도·재발신 불변)함을 실증 —
      즉 로그만 억제되고 push 동작은 안 죽음(수신측 카운트/시도 카운트로 확인).
  C3. 복구(호스트 재개) 시 성공 로그가 **정확히 1건** 남고, 그 직후 다시 실패시키면 **새 FAIL 1건**이
      남음(억제 리셋 실증).
  C4. next_state를 바꾼(BUSY↔READY) 상태로 계속 실패시키면 **새 FAIL 1건**이 추가로 남음(OQ-2 채택 시).
  C5. 억제 관련 상수가 appsettings `Wcs:ChuteStatePush`에 존재하고 코드에 하드코딩 0(#7). Wcs.Core
      git diff 0(#8). 억제는 per-route 락 내에서만 상태 갱신(코드 경로 직독으로 확인).
  C6. 성공 push 로깅(현행)·재시도·백오프·Acked/Computed 전이·전이당 1회 발신 diff 0(#8 부수 훅).
  C7. 양 provider 전체 테스트 스위트 GREEN(신규/갱신 포함), 회귀 0. 갱신된 테스트는 로그-카운트 단언만
      바뀌고 delivery/Acked/attempt 단언은 불변임을 diff로 확인.
  C8. (OQ-1 채택 시) 저빈도 요약 로그가 설정 주기로만 발화하고 완전 무음이 아님을 실증.

- Parallel Modules: N/A (single module — Wcs.Api 아웃바운드 push 로깅 억제. 경계 분할 없음.)

- Evaluation Dimensions: functional only
  (backend-only·단일 표면. 스레드안전은 별도 dimension이 아니라 Craft 기준 C5로 흡수 — 실 병렬 부하
   재현으로 검증. padding 금지.)

- Detected Project Type: Full-stack
  (repo 신호: `frontend/`(브라우저 진입점·`TraceLogPage.tsx` 등) + `backend/`(ASP.NET Core
   route/controller·server 호스트) 공존. 단 **이 스프린트 변경은 백엔드 전용** — 프론트 코드 변경 0.)

- Verification Scenarios (Full-stack, mandatory):

  === Applicable Web/UI scenarios (frontend surface this sprint touches) ===
  - 프론트 코드 변경 0 — 이 스프린트는 소스(백엔드) 억제만. 아래는 **간접 관측**(백엔드 결과의 시각 확인),
    프론트 파일 diff는 0이어야 함(회귀 스코프 게이트).
    · VS-U1 (간접): RCS 다운을 지속시킨 상태에서 `/trace` 뷰어(TraceLogPage)를 열면, 같은 실패 전이에 대해
      이벤트 8/10 result:"FAIL" 행이 **주기마다 새로 쌓이지 않고 1건에서 멈춰 있음**(폭주 소멸의 시각 확인).
      뷰어 렌더/네비게이션 로직은 무변경 — 표시되는 데이터(파일 tail)가 줄어든 것뿐.
    · VS-U2 (간접): 운영로그 모니터링 화면에서 동일 실패의 CHUTESTATE_PUSH WARN "FAIL" 행이 1건만
      존재(반복 억제). frontend 컴포넌트·API 클라이언트 diff = 0.
    · 그 외 default/alternate/empty/dark-mode 상태 슬롯: **N/A** — 이 스프린트는 UI 표면을 만들거나 바꾸지
      않음(프론트 무변경). 다크모드도 N/A(신규 UI 없음).

  === Applicable Backend/API scenarios (backend surface this sprint touches) ===
  - 인바운드 엔드포인트 계약 변경 없음(이 변경은 **아웃바운드** push 클라이언트 WCS→RCS의 로깅 동작).
    검증 표면 = (i) operation_log 행(CHUTESTATE_PUSH, level=WARN, detail result:"FAIL"),
    (ii) 트레이스 레코드(EventNo 8/10, Detail result:"FAIL"), (iii) WCS→RCS PUT 시도(수신측 fake host).
    · VS-B1 첫 실패: fake RCS 호스트를 다운(항상 실패)으로 두고 한 목적지의 수용상태를 한 번 전이시키면 →
      operation_log FAIL 정확히 1건 + 트레이스 FAIL(8 or 10) 정확히 1건. (억제 없이 첫 신호 유지.)
    · VS-B2 반복 억제: VS-B1 이후 하트비트가 같은 미동기 route를 N 주기 재발신해도 →
      operation_log FAIL 추가 0 + 트레이스 FAIL 추가 0. delivery 시도는 매 주기 발생(재발신 불변).
    · VS-B3 복구 1건: fake 호스트를 성공으로 전환하면 → 성공 로그(OK oplog + OK trace) 정확히 1건,
      이후 route 동기(Acked==Computed)로 재발신 정지. 성공 로깅 형상·필드 현행과 동일(무변경).
    · VS-B4 억제 리셋: VS-B3 복구 후 호스트를 다시 다운시키고 새 전이를 내면 → **새 FAIL 1건**(리셋 실증).
    · VS-B5 next_state 전이(OQ-2): 계속 실패 중 accept 값을 바꿔 next_state를 2↔3으로 전이시키면 →
      새 FAIL 1건 추가(같은 route라도 새 전이).
    · VS-B6 동작 불변: 위 전 과정에서 4-시도 재시도·백오프·PushAsync 반환 bool·Acked/Computed 전이·
      전이당 1회 발신·DORMANT 가드가 diff 0(코드 직독 + delivery/attempt 카운트 단언 불변).

  === End-to-end data-flow scenario (2+ layers) ===
  - VS-E1 (Pusher→Client→운영로그/트레이스 sink, RCS-down 하트비트 폭주 억제):
    실 `DestinationStatusPusher` 관찰 루프 + 실 `ChuteStatePushClient` + 다운 상태의 fake RCS 수신 서버 +
    실 operation_log(EF, SQLite scratch) + capturing 트레이스 sink를 한 스택에 결선. 목적지 1개를 실패
    전이시키고 관찰 주기를 N회(≥3) 돌린 뒤:
      (a) operation_log CHUTESTATE_PUSH WARN "FAIL" **CountAsync == 1**,
      (b) 트레이스 이벤트 8/10 result:"FAIL" 레코드 **count == 1**,
      (c) fake RCS 수신 PUT 시도 **count ≥ N**(재발신 살아있음 — 로그만 억제),
    를 **한 테스트에 병치**로 단언(억제 실효 + 폭주 부재 + 동작 불변을 분리 단언 — GREEN 하나로 합치지 말 것).
    이어서 fake RCS를 성공으로 전환 → 성공 로그 1건 + 재발신 정지(freeze) 확인(복구 실효).

- Open Questions (★ 사용자 게이트 확정 2026-07-31):
  · OQ-1 ✅ **저빈도 "아직 실패 중" 주기 요약 로그 채택**(설정 주기·저빈도). 완전 무음 금지.
  · OQ-2 ✅ **억제 단위 = (route, next_state)** — 같은 목적지라도 BUSY↔READY 전이는 새 첫 실패로 로깅.
  · OQ-3 ✅ **운영로그·추적로그 양쪽 동일 정책 억제.**
  · OQ-4 ✅ **재시도-소진 Serilog `_log.LogError`(약 :184-187)도 억제 포함**(같은 폭주원). 단 재시도 루프 내
    per-attempt `_log.LogWarning`은 스코프 밖(한 호출 내 국소).
  → 세 sink(operation_log WARN + 트레이스 8/10 + Serilog LogError) 모두 동일 억제. 요약 로그는 채택.

> Planner self-check — Detected project type: Full-stack. Required scenario slots: 3 (Applicable Web/UI scenarios [frontend surface — N/A/indirect, 무변경], Applicable Backend/API scenarios [outbound push 로깅 억제 표면], End-to-end data-flow scenario [Pusher→Client→oplog/trace RCS-down 폭주 억제]). All slots filled: yes.
