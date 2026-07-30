[Sprint Contract] — S-TRACE-READY-PUSH-AND-DEFAULT

═══════════════════════════════════════════════════════════════════════════════
■ Goal (WHAT — Planner 정의)
═══════════════════════════════════════════════════════════════════════════════
두 가지를 additive·관측/로깅 전용으로 추가한다(회귀 0).

요청 1 — Ready 전이 + 슈트상태 push 시각 추적:
  전용 추적 로그(전용 파일 D:\Rcs3dsInterlockingWcsLogs + /trace 실시간 뷰어)에 신규 이벤트를
  추가해 아래 4개 시각을 관측·기록한다. 목적 = "Ready 전이 ↔ RCS 통지(IF-08 push) 지연" 측정.
    (a) 소터 Ready 워드 1→0 전이 관측 시각
    (b) 그 상태 변화로 WCS가 RCS로 나가는 IF-08 슈트상태 update push 전송 시각
    (c) 소터 Ready 워드 0→1 전이 관측 시각
    (d) 그 상태 변화로 나가는 IF-08 push 전송 시각
  기존 TraceLogService 6이벤트 스킴을 확장(신규 EventNo 부여)해 전용 파일 `[N] {json}` +
  SignalR `/trace` 둘 다 자동 기입되게 한다.

요청 2 — 프론트 기본화면을 추적 로그(/trace)로 변경:
  현재 기본 랜딩(B2C home = "데이터 생성" /b2c/test-data)을 추적 로그 /trace 로 바꾼다.
  기존 라우팅·다른 페이지 접근을 깨뜨리지 않는다.

═══════════════════════════════════════════════════════════════════════════════
■ 코드 조사 결과 (설계 근거 — Generator/Evaluator 공유 사실)
═══════════════════════════════════════════════════════════════════════════════
기존 6이벤트 스킴 (EventNo = "이벤트 종류(KIND) 태그", per-instance 상관키 아님 —
  TraceRecord docstring 명시):
    1 TGTFLOOR_ENQUEUE  (RcsController IF-05 + PendingFloorQueueRestorer 복원)
    2 TGTFLOOR_DEQUEUE  (SorterFloorReturnService 분류시작 클리어 pop)
    3 IF10_ARRIVAL      (RcsController DepositReport 진입)
    4 C_ENQUEUE         (TraceWiring — HS_C_SENT)
    5 C_DEQUEUE         (TraceWiring — CELL_ASSIGN write)
    6 C_CLEAR           (TraceWiring — C_Flag 1→0 register change delta)
  TraceRecord 필드 = {EventNo, Event, At, PId, CSeq, ChuteNo, DestId, CellNo, Floor,
  InductionNo, Trigger, Detail}. EventNo/Event/At 외 전부 nullable → 신규 이벤트 데이터
  (chuteNo/destId/floor + 방향/next_state는 Detail)를 그대로 수용(스키마 변경 불요).

[조사 A] Ready 전이 관측 지점 — **확정**:
  PlcGateway.EmitRegisterChanges 가 폴 스냅샷 델타에서 "Ready" 1→0·0→1 을 `OnRegisterChange
  (reg,old,new)` 로 발화한다(PlcGateway.cs:598). 이는 이벤트 6(C_Flag)이 이미 쓰는
  `bundle.SubscribeRegisterChange` 훅과 동일 경로 → 신규 Ready 이벤트는 **TraceWiring.Wire 안에
  이벤트 6과 나란히 "reg=='Ready'" 핸들러를 추가 구독**하면 된다. PlcGateway/HandshakeOrchestrator
  무접촉(절대규칙 #8 — 기존 콜백 얹기만).

[조사 B] IF-08 push 전송 지점 — **확정**:
  실제 PUT 전송 = ChuteStatePushClient.PushAsync 의 `http.PutAsJsonAsync(url, payload)`
  (ChuteStatePushClient.cs:117) → RCS 층 호스트로 `{chute_numbers:[chuteNo], next_states:[3|2]}`.
  이 클라이언트는 이미 operation_log CHUTESTATE_PUSH(성공/실패)를 남긴다(그대로 유지).
  push 결정은 DestinationStatusPusher.PumpAsync(전이당 1회 멱등, per-route).

[조사 C — ★ 핵심] Ready 전이 ↔ push 상관관계 — **직접 인과 아님(코드 확정)**:
  IF-08 push 는 Ready 레지스터 에지가 **직접 호출로 트리거하지 않는다.** 별개의 주기 관찰
  루프(DestinationStatusPusher.RunSorterObserveLoopAsync, 주기 = Wcs:ChuteStatePush:
  SorterObserveIntervalMs=150ms)가 매 틱 소터별 `accept = Ready && !Paused` 를 (dest, 층-호스트)
  route 마다 재산출하고, route 의 next_state 가 직전 Acked 와 달라질 때만 PushAsync 를 호출한다.
  결과:
    · 시간적으로 분리됨(Ready 에지 이후 다음 관찰 틱에 발신).
    · **route(층-호스트)별**로 발신 — 소터가 층-호스트 N개면 한 Ready 전이가 최대 N건의 push
      전송을 낳는다(각 route 1건).
    · Ready 1→0: accept→false → 모든 route next_state=2 → 3이던 route마다 push.
    · Ready 0→1: accept→true, 그러나 next_state=3 은 **CurFloor==그 route 층**인 route만.
      나머지 층 route 는 2 유지(변화 없음 → push 0건일 수 있음). 즉 0→1 전이는 push 0/1/N건.
    · push 는 Ready 외 원인(pause/resume·capacity·CurFloor 변화)에서도 나감.
  ⇒ "그 전이로 나간 그 push" 를 **인과 토큰으로 특정할 수 없다.** 상관은 chuteNo/destId(소터
    scope) + 시각 순서 + next_state(2=1→0쪽 / 3=0→1쪽)로만 이뤄진다. 지연 지표는 이에 맞춰 정의
    (아래 이벤트 구조 참조). 이 사실을 계약이 명문화하며 4개 시각을 이 비인과 모델 위에서 기록한다.

[조사 D] 전송 파이프 EventNo-무관성 — **확정**:
  · SignalR relay(MonitorRelayService.OnTraceEntry → Broadcast(GroupTrace,"Trace",rec)) EventNo 무관.
  · REST 백로그(TraceLogService.Read / GET /trace) EventNo 필터 무관(TryParse+필터 제너릭).
  · 프론트 전송 타입 TraceRecord/TraceEvent = `eventNo:number`(제너릭). api.trace(eventNo?) 제너릭.
  ⇒ 신규 EventNo 는 전송 계층 무변경으로 파일·SignalR·REST 백로그에 자동 흐른다.

[조사 E] 프론트 /trace 뷰어 신규 이벤트 렌더 — **부분 자동/부분 하드코딩**:
  · 테이블 행: TraceLine 이 `EVENT_META[eventNo] ?? {label: row.event, tone:'neutral'}` 폴백을
    가져 **미등록 EventNo 도 자동 렌더**(서버 event 명·중립색·제너릭 컬럼). "전체" 필터에서 보임.
  · 그러나 EVENT_META(1~6 하드코딩)·필터 드롭다운(`[1,2,3,4,5,6]` 하드코딩)·"6개 이벤트" 카피는
    신규 이벤트를 반영 못 함 → 라벨/색·드롭다운 필터 항목·문구 갱신이 필요(아래 Scope).

[조사 F] 기본화면 uiMode 분기 — **단일 소스 확인**:
  · frontend/src/lib/uiMode.ts `homePathFor(mode)`: b2b→'/data-generator', b2c→'/b2c/test-data'.
    기본 mode='b2c'. App.tsx ModeHome(`/`·`*`)·Layout.ModeToggle 가 이 함수를 공용.
  · /trace 는 **b2c NAV 세트에만** 존재(Layout NAV_SETS.b2c). b2b NAV 엔 없음.
  ⇒ 권장: homePathFor('b2c') → '/trace' 로 변경, b2b 는 '/data-generator' 유지(아래 Open Q4).

═══════════════════════════════════════════════════════════════════════════════
■ ★ 설계 논점 & Open Questions
═══════════════════════════════════════════════════════════════════════════════
✅ 사용자 게이트 확정(2026-07-30) — 아래 OQ 권장과 다른 부분은 **이 블록이 정본**:
  · OQ1 = **신규 EventNo 4개(7·8·9·10)**, 각 시각 1개(사용자 선택 — 권장안 A가 아닌 대안 B):
      7  = READY_1TO0           : Ready 워드 **1→0** 전이 관측   (Detail: reg,old=1,new=0,curFloor)
      8  = CHUTESTATE_PUSH_BUSY : IF-08 push 전송 중 **next_state==2**(busy/not-ready) (Detail: next_state=2,result,attempts,host)
      9  = READY_0TO1           : Ready 워드 **0→1** 전이 관측   (Detail: reg,old=0,new=1,curFloor)
      10 = CHUTESTATE_PUSH_READY: IF-08 push 전송 중 **next_state==3**(ready)          (Detail: next_state=3,result,attempts,host)
    4개 시각 매핑: (a)=7 · (b)=8 · (c)=9 · (d)=10. Ready 방향은 EventNo(7 vs 9)로, push 방향은
    EventNo(8 vs 10)=next_state(2 vs 3)로 구분. (아래 OQ1 권장안 A[7·8 2개]는 채택 안 함 — 참고 이력.)
  · OQ2 = **모든 IF-08 PUT 전송 계측**(전송 지점 단일 훅). 각 전송을 next_state로 8(==2)/10(==3)에
    분기. 소터 chuteNo로 식별. Ready 에지와 인과 링크 없음(조사 C) — chuteNo+시각+next_state 상관.
  · OQ4 = **B2C만 /trace**. homePathFor('b2c')→'/trace', b2b→'/data-generator' 유지.
  · OQ3/OQ5 = 확정(소터 scope·pId/cSeq/cellNo=null; 뷰어 EVENT_META/필터/문구 갱신).

(원문 OQ 이력 — 참고용, 정본은 위 확정 블록)
OQ1 — 이벤트 구조 (권장: 신규 EventNo 2개):
  [권장안 A] 2개 신규 EventNo, 방향은 필드로:
    · 7 = READY_TRANSITION : Ready 1→0 AND 0→1 둘 다(동일 KIND). 방향은 Detail의 old/new 로 구분
        (이벤트 6 C_CLEAR 가 이미 old/new 를 Detail 로 담는 관례와 동형).
        필드: ChuteNo·DestId·Floor(=관측 시점 CurFloor)·Trigger="READY_EDGE",
        Detail={reg:"Ready", old, new, curFloor}.
    · 8 = CHUTESTATE_PUSH_SEND : IF-08 PUT 전송 시각(전송 지점 계측). 방향은 Detail의 next_state
        (2 vs 3)로 구분. 필드: ChuteNo·DestId·Floor(=route 층/host)·Trigger="IF08_PUSH",
        Detail={next_state, result, attempts, host}.
    4개 시각 매핑: (a)=7[old1new0] (b)=8[next_state2] (c)=7[old0new1] (d)=8[next_state3].
  [근거] EventNo 는 기존 관례상 "이벤트 종류 태그"다 — 1→0 과 0→1 은 같은 KIND(방향만 다름)이므로
    한 번호+방향필드가 정합. push 도 마찬가지(같은 전송 종류, next_state 로 방향 표현). 또한 조사 C
    대로 push 는 전이당 0/1/N건이라 시각 4개를 EventNo 4개로 1:1 고정하는 모델이 물리적으로 성립
    안 함 → 필드 기반이 정직. 레전드/필터도 2개만 늘어 단순.
  [대안 B] 7/8/9/10 네 EventNo(각 시각 1개). 단점: KIND 관례 위반·레전드/필터 2배·8vs10 은 결국
    next_state 로만 구분돼 필드 중복·0/N건 현실과 불일치.
  ▶ 사용자 확정 요청: 권장안 A(7·8) 채택 여부. (미회신 시 A로 진행.)

OQ2 — Ready↔push 상관 방식 (권장: 비인과·소터 scope 상관):
  조사 C 결론에 따라 이벤트 8은 "특정 Ready 에지가 유발한 push"를 인과로 잇지 않고 **실제 전송
  지점에서 모든 IF-08 PUT 전송을 계측**한다(소터·슈트 공통 전송 chokepoint). 소터 push 는
  chute_numbers=[소터 chuteNo]라 chuteNo 로 식별 가능. 지연 지표 = 뷰어/분석에서 같은 chuteNo 의
  이벤트7(new값) → 그 직후 이벤트8(next_state 부합) 시각차. push 트랜지언트 특성(N route·0건 가능)은
  Detail(host/next_state)로 노출.
  ▶ 사용자 확정 요청: 이벤트 8을 (i) 모든 IF-08 전송 계측(권장·전송 지점 단일 훅) vs (ii) 소터
    Ready-driven 전송만으로 제한. (미회신 시 (i)로 진행 — 정직·additive·최소침습.)

OQ3 — 상관키/스코프 (확정 명시):
  신규 이벤트 7·8 은 **소터 + chuteNo/destId(+층) scope**다. 피스 pId·cSeq·cellNo 없음
  (PId/CSeq/CellNo=null). 기존 3~6 의 pId/cSeq 피스-상관과 다른 층/소터 scope 경계임을 명시
  (이벤트 1·2가 이미 이 scope를 씀 — 이벤트 2는 pId=null). 뷰어 상관은 pId 대신 chuteNo 로.

OQ4 — 기본화면 uiMode 분기 (권장: b2c만 /trace):
  homePathFor('b2c') → '/trace'. b2b → '/data-generator' 유지.
  [근거] /trace 는 b2c(관제) NAV 전용 페이지다. b2b(작업 테스트 데이터 생성 도구)가 /trace 로
    랜딩하면 (1) Layout 헤더 타이틀 매칭이 b2b NAV 에 /trace 부재로 b2b 첫 항목("데이터 생성")으로
    폴백하는 cosmetic 불일치(todo.md 기존 항목과 동류), (2) b2b 우측 컨트롤(업무일자) 표시 등
    의미 부정합이 생긴다. 사용자가 말한 "현재 기본화면=데이터 생성"은 기본 mode=b2c 의 랜딩
    (/b2c/test-data)을 가리키므로, b2c 랜딩만 /trace 로 바꾸면 요구 충족.
  ▶ 사용자 확정 요청: (i) b2c 만 /trace(권장) vs (ii) 양 모드 다 /trace vs (iii) 다른 조합.
    (미회신 시 (i)로 진행.)

OQ5 — /trace 뷰어 신규 이벤트 렌더 범위 (확정):
  테이블 행은 자동 렌더되나(조사 E), 신규 이벤트가 1급으로 보이려면 EVENT_META 라벨/색 추가 +
  이벤트 필터 드롭다운 항목 추가 + "6개 이벤트" 문구 갱신이 필요 → Scope 에 포함. 백엔드 REST/
  SignalR 전송은 제너릭이라 무변경.

═══════════════════════════════════════════════════════════════════════════════
■ Implementation Scope (Generator 가 해야 할 것 — HOW 는 Generator 재량)
═══════════════════════════════════════════════════════════════════════════════
[백엔드 — 관측/로깅 전용]
  S1. Ready 전이 이벤트(7·9) 발화: TraceWiring.Wire 안에서 `bundle.SubscribeRegisterChange`
      로 reg=="Ready" 델타를 추가 구독 → **1→0이면 EventNo:7(READY_1TO0), 0→1이면 EventNo:9(READY_0TO1)**
      trace.Log(TraceRecord{EventNo:7|9, ChuteNo/DestId/Floor=CurFloor, Detail={reg,old,new,curFloor}}).
      기존 이벤트 6(C_Flag) 구독 무변경. 논블로킹(trace.Log=Channel.TryWrite)·예외 격리(폴 스레드 비차단·fail-safe).
  S2. 슈트상태 push 전송 이벤트(8·10) 발화: **모든 IF-08 PUT 전송**을 전송 지점(ChuteStatePushClient.
      PushAsync 의 실제 PUT, Wcs.Api 계층 내)에서 계측 → **next_state==2이면 EventNo:8(CHUTESTATE_PUSH_BUSY),
      next_state==3이면 EventNo:10(CHUTESTATE_PUSH_READY)**. trace.Log(TraceRecord{EventNo:8|10,
      ChuteNo=payload.chute_numbers[0], DestId(가용 시)·Floor/host, Detail={next_state, result,
      attempts, host}}). 기존 operation_log CHUTESTATE_PUSH 유지(대체 금지). DORMANT(층호스트 미설정)
      시 전송 없음 → 이벤트 8/10 미발화(자연스러운 no-op). 논블로킹·fail-safe(로깅 실패가 push 를 막지 않음).
      · DestId 가 전송 지점에서 미가용하면 chuteNo 기반 best-effort(계약 허용) — HOW 는 Generator.
      · next_state 가 2/3 외 값이면(이론상) 안전 처리(로깅 스킵 또는 일반 태그) — HOW 는 Generator.
  S3. TraceLogService 헤더 주석/이벤트 목록(1~6 → 신규 포함)·관련 docstring 갱신(문서 정합).
  S4. appsettings 무하드코딩 준수: 신규 이벤트는 기존 TraceLog 설정을 재사용(신규 설정 키 불요).
      방향/next_state 는 런타임 데이터이지 설정값 아님(절대규칙 #7 무위반). 리터럴 경로/호스트 0.

[프론트 — 뷰어 + 기본화면]
  S5. TraceLogPage: EVENT_META 에 신규 이벤트(**7·8·9·10**) 라벨/색조 추가 + 이벤트 필터 드롭다운
      배열에 신규 번호 4개 추가 + "6개 이벤트" 문구를 실제 개수(10)/설명으로 갱신. 기존 1~6 렌더 무변경.
      (신규 이벤트는 chuteNo·floor·detail 컬럼으로 표현; pId/cSeq/cellNo 는 "—".)
      권장 라벨: 7="Ready 1→0" · 8="슈트상태 push(busy)" · 9="Ready 0→1" · 10="슈트상태 push(ready)".
  S6. 기본화면: uiMode.ts homePathFor('b2c') = '/trace'(OQ4 확정 반영). ModeHome/`/`·`*`·ModeToggle
      단일 소스 반영. 다른 라우트·페이지 접근 무변경(회귀 0).

[스코프 밖 — 명시 제외]
  · PLC 쓰기·핸드셰이크·push 결정 로직·라우팅 변경 0(관측만).
  · 동시 IF-10 직렬화·pId↔cSeq 갭(기존 알려진 한계) 미해결(스코프 밖).
  · Ready↔push 인과 링크 신설(불가·조사 C) 0 — 소터 scope 상관만.
  · TraceLog 전송 계층(SignalR/REST) 코드 변경 0(제너릭).

═══════════════════════════════════════════════════════════════════════════════
■ 제약 (절대규칙 — 계약 명시)
═══════════════════════════════════════════════════════════════════════════════
  · #1 단일 쓰기 큐: 본 스프린트는 로깅 전용 — 제2 write 경로·PLC write 0. 훅은 관측만, sink 는
    Channel.TryWrite. (해당 없음이나 명시: 신규 코드에 EnqueueSet*/WriteRegister/Modbus 호출 0.)
  · #7 하드코딩 0: TraceLog 경로/롤링/보존·push 호스트 전부 설정값 재사용. 신규 리터럴 경로/호스트 0.
  · #8 Wcs.Core 순수: 로깅은 Wcs.Api 계층에서만. PlcGateway/HandshakeOrchestrator/Wcs.Core/
    ChuteStatePushClient 판정로직 무접촉(이벤트 8 계측은 Wcs.Api 소속 클라이언트 내 부수 훅).
  · 논블로킹·fail-safe: 발화가 폴 루프·push 핫패스를 블로킹하지 않음(기존 논블로킹 sink 패턴 유지).
    로깅/훅 예외는 격리 — 본 동작(폴·전송·응답) 무영향.
  · 회귀 0: 기존 operation_log(REG_CHANGE(Ready) POLL_CHANGE · CHUTESTATE_PUSH API)·전역 Serilog·
    모니터 SignalR·기존 6 트레이스 이벤트·기존 테스트 전부 무영향(additive). 기본화면 변경이 기존
    라우팅/타 페이지 접근을 깨지 않음.

═══════════════════════════════════════════════════════════════════════════════
■ Evaluation Criteria (Evaluator 판정 기준 — Full-stack 4축 + 가중)
═══════════════════════════════════════════════════════════════════════════════
  1. Integration Quality (★★★): Ready 에지(PLC/Sim) → 이벤트7(파일 `[7]{json}` + SignalR + 뷰어 행)
     → push 발신(fake RCS) → 이벤트8(`[8]{json}` + 뷰어) 의 층-scope 데이터 흐름이 끊김 없이 관통.
     chuteNo 로 7↔8 상관 가능·지연 산출 가능. 전송 계층 무변경으로 자동 흐름.
  2. Per-layer Quality (★★★): [BE] 훅이 기존 콜백 "추가 구독"만·논블로킹·fail-safe, 절대규칙
     #1/#7/#8 코드 게이트 통과(write-queue/Modbus/리터럴 0). [FE] 신규 이벤트 라벨/필터/렌더가
     기존 뷰어 패턴과 정합·기존 6이벤트 무회귀.
  3. Craft (★★): 방향/결과(old/new·next_state·result)가 Detail 에 정직 기록. DORMANT·전송 실패·
     디렉터리 부재 등 엣지에서 fail-safe(500 없음·본 동작 비차단). 문서/주석 정합(개수·목록).
  4. Functionality (★★): 4개 시각 전부 관측·기록됨(요구 (a)~(d)). 기본화면이 /trace 로 랜딩.
     회귀 0(operation_log 종전 카운트·전역 Serilog 트레이스 0줄·기존 테스트 GREEN).

═══════════════════════════════════════════════════════════════════════════════
■ Completion Conditions (PASS 최소 조건)
═══════════════════════════════════════════════════════════════════════════════
  C1. 격리 라이브 스택(실 Sim + Sqlite scratch + API --Urls 오버라이드 + --TraceLog:Directory=
      scratch + fake RCS 층호스트 설정)에서 소터 Ready 1→0·0→1 을 실제로 태워 전용 파일에
      `[7] {json}`(old/new 정확)·`[8] {json}`(next_state 정확)이 raw 로 확인됨. 기존 1~6 무영향.
  C2. GET /trace 가 eventNo=7·8 필터로 신규 레코드 반환(camelCase TraceRecord 형상 불변). 디렉터리
      부재 시 [] (200). 백로그·SignalR 무변경 자동 흐름 확인.
  C3. 회귀 3축 동시: (i) 전용 파일엔 신규 포함 이벤트만·전역 logs/wcs-*.log 에 트레이스 라인 0,
      (ii) operation_log REG_CHANGE(Ready)·CHUTESTATE_PUSH 종전대로 기록(대체 아님),
      (iii) 기존 테스트 전량 GREEN(신규분 산술 일치·회귀 0).
  C4. 프론트: 기본 URL('/') 진입이 /trace 로 랜딩(b2c). 뷰어에서 신규 이벤트가 라벨/색으로 표시되고
      이벤트 필터 드롭다운으로 선택 가능. 기존 6이벤트·타 페이지·b2b 랜딩(/data-generator) 무회귀.
      브라우저 콘솔 pageerror 0·React dev-warning 0.
  C5. 절대규칙 코드 게이트: 신규 코드 경로에 write-queue/PLC-write/리터럴경로/리터럴호스트 0,
      Wcs.Core/PlcGateway/HandshakeOrchestrator/ChuteStatePushClient 판정로직 zero-diff, 논블로킹
      (Channel.TryWrite + 예외격리) 확인. lint/tsc/build/format exit 0.

═══════════════════════════════════════════════════════════════════════════════
■ Parallel Modules (Generator fan-out)
═══════════════════════════════════════════════════════════════════════════════
  N/A (단일 응집 additive 변경). 기본 1/1/1 유지.

■ Evaluation Dimensions (Evaluator expert pool)
  functional only. (보안/성능 민감 신규 표면 없음 — 논블로킹은 functional 게이트로 흡수.)

═══════════════════════════════════════════════════════════════════════════════
- Detected Project Type: Full-stack
  (신호: frontend/src 브라우저 진입 SPA(React Router) + backend/src/Wcs.Api 서버측 컨트롤러·
   호스트가 동일 저장소에 공존 — Full-stack.)

═══════════════════════════════════════════════════════════════════════════════
■ Verification Scenarios (Full-stack — mandatory)
═══════════════════════════════════════════════════════════════════════════════
=== Applicable Web/UI scenarios (프론트 surface = TraceLogPage · uiMode/App 라우팅) ===
  U1 [기본 상태/기본 라우트] '/' 진입(기본 mode=b2c) → /trace 로 redirect·랜딩(헤더 "추적 로그").
     b2b 토글 후 랜딩 = /data-generator(불변). 스크린샷으로 확인.
  U2 [기본 상태/뷰어 렌더] /trace 에 기존 1~6 + 신규 7·8 행이 렌더 — 신규 행이 라벨(예: "Ready 전이",
     "슈트상태 push")·색조로 표시, chuteNo/floor/detail 컬럼 채워지고 pId/cSeq/cellNo="—".
  U3 [대체 상태/필터 상호작용] 이벤트 드롭다운에 7·8 항목 존재 → 7 선택 시 Ready 전이 행만, 8 선택
     시 push 전송 행만 필터. chuteNo 로 한 소터 흐름 좁혀 7→8 시각차 확인(navigate→select→assert).
  U4 [빈/에러 상태] 로그 없을 때 "표시할 추적 로그가 없습니다" + 연결 배지 표시. 백엔드 일시 중단 시
     graceful(연결 끊김 배지·auto-retry) — pageerror 0(의도적 5xx 는 예외 명시).
  U5 [다크모드] N/A — 앱에 다크모드 토글/`.dark`/prefers-color-scheme 없음(단일 테마·CSS 토큰).

=== Applicable Backend/API scenarios (엔드포인트: GET /trace — 기존, 신규 EventNo 반영) ===
  B1 [엔드포인트·happy] GET /trace?eventNo=7 / ?eventNo=8 → 신규 레코드 배열(camelCase TraceRecord
     형상 불변: eventNo/event/at/pId/cSeq/chuteNo/destId/cellNo/floor/detail). 필터 없는 GET /trace
     는 기존+신규 혼재(시계열 오름차순).
  B2 [파일 sink] 라이브 태운 뒤 전용 파일에 `[7] {…"old":1,"new":0…}`·`[8] {…"next_state":2…}` raw
     확인 + 전역 Serilog 파일에 트레이스 라인 0(격리). 기존 1~6 형상 불변.
  B3 [operation_log additive] REG_CHANGE(Ready) POLL_CHANGE 행 + CHUTESTATE_PUSH API 행이 종전대로
     기록됨(트레이스가 대체하지 않음 — 카운트 병치 확인).
  B4 [에러/DORMANT] 층호스트 미설정(DORMANT) → PUT 전송 0 → 이벤트 8 미발화(no-op). TraceLog
     디렉터리 부재 → GET /trace = [] (200, 500 없음). 로깅 실패가 push/폴 비차단(fail-safe).

=== End-to-end data-flow scenario (2+ 계층 관통) ===
  E1 [Ready 1→0 → push=2 관통] 실 Sim 으로 소터 Ready 1→0 유도 → (PLC 폴)이벤트7[old1new0] 파일
     +SignalR 도달 → (fake RCS 층호스트 설정)관찰루프가 accept=false 산출 → PushAsync PUT → 이벤트8
     [next_state2] 파일+뷰어 도달. 같은 chuteNo 로 7→8 페어링·지연(Δt) 산출 가능함을 실증.
  E2 [Ready 0→1 → push=3 관통] Ready 0→1(+CurFloor 해당 층) → 이벤트7[old0new1] → 그 route push
     next_state=3 → 이벤트8[next_state3]. (조사 C: 해당 층 route 만 3, 타 층 route 는 미전이 가능 —
     0/1/N건 특성이 detail(host/next_state)로 관측됨을 확인.)

> Planner self-check — Detected project type: Full-stack. Required scenario slots: 3 (Web/UI, Backend/API, end-to-end). All slots filled: yes.
