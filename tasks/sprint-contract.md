[Sprint Contract] — S-TRACE-LOG-VIEWER

═══════════════════════════════════════════════════════════════════════════════
현장 추적용 전용 로그 파일 + 실시간 프론트 뷰어
═══════════════════════════════════════════════════════════════════════════════

- Goal:
  핸드셰이크/2층 제어 흐름의 5개 핵심 이벤트를 **상관키로 한 흐름씩 이어볼 수 있게** 전용
  로그 파일(D:\Rcs3dsInterlockingWcsLogs)에 기입하고, 프론트에 그 로그를 **실시간 표시**하는
  전용 화면을 신설한다. 5개 이벤트 계측은 **관측/로깅만** — 기존 판정·핸드셰이크·단일 쓰기
  큐의 동작·타이밍을 1바이트도 바꾸지 않는다. 기존 operation_log·Serilog·모니터링 SignalR은
  그대로 유지하고, 전용 로그는 그 위에 **얹는 추가 싱크**다(중복 억제는 아래 결정 참조).
  ★ 추가 요건(사용자 2026-07-28 확정): **각 트레이스 로그 줄에 "이벤트 번호(1~6)"를 포함**한다.
    이 번호 = 사용자가 6개 이벤트에 매긴 순번(추적 용이 목적, 이벤트 종류 태그이지 피스 상관키가
    아님). 매핑: **1=TgtFloor 펜딩큐 인큐 · 2=TgtFloor 펜딩큐 디큐(pop)** · 3=IF-10 도착 · 4=C 인큐 ·
    5=C 디큐 · 6=C 클리어. 모든 줄이 이 번호를 앞머리에 달아 "몇 번 이벤트"인지 즉시 식별.

  추적 대상 6개 이벤트(= 로그 이벤트 번호 1~6; 각 이벤트의 파라미터 + 시각, 상관키 포함):
    1 TgtFloor pending-floor 큐 **인큐** (2층 제어 — 소터별 FIFO enqueue)
    2 TgtFloor pending-floor 큐 **디큐(pop)** (관측 루프의 분류 사이클 pop)
    3 IF-10 요청 도착 (RcsController.DepositReport 진입)
    4 C영역(CellAssign) 기입 인큐 (HandshakeOrchestrator가 C를 큐에 넣는 시점)
    5 C영역 기입 디큐 = 실제 Modbus write (PlcGateway 컨슈머가 CellAssign 기입)
    6 C영역 클리어 관측 (PLC가 C를 읽고 C_Flag 1→0으로 클리어한 것을 WCS가 폴 스냅샷에서 관측)

───────────────────────────────────────────────────────────────────────────────
확정 결정 (2026-07-28 Phase-1 게이트 — Open Question 전부 해소)
───────────────────────────────────────────────────────────────────────────────
  · OQ6 "부여한 번호" = **이벤트 번호 1~6**(사용자 재확정 2026-07-28: TgtFloor 인큐=1, 디큐=2로 분리
    → 3=IF-10, 4=C인큐, 5=C디큐, 6=C클리어). 모든 트레이스 줄에 이 번호를 태그로 기입.
    이벤트 종류 식별용이며 피스 상관키가 아니다(상관키는 pId + (chuteNo,cSeq) 그대로).
  · OQ1 TgtFloor pop 상관 경계 **수용** — 큐 자료구조 무변경(로깅 전용). 디큐(번호 2)는 소터+층+FIFO로만
    상관(개별 pId 미포함), 인큐(번호 1)는 트리거 pId·inductionNo를 best-effort 컨텍스트로 남김.
  · OQ4 중복 정책 = **additive**. 기존 operation_log/Serilog 전부 유지, 전용 파일엔 이 5개만 추가.
  · OQ2 프론트 = **신규 전용 페이지 + 테이블**(네비 발견 가능). 필터=이벤트번호/pId/cSeq.
  · OQ3 ⑤ = **C_Flag 1→0 폴 관측**(R 클리어와 구분).
  · OQ5 롤링/보존 = **일(Day) 롤링 + 100MB 크기롤 + 30일 보존 + 파일명 `trace-.log`**(전부 설정값).

───────────────────────────────────────────────────────────────────────────────
계측 지점(WHERE — 코드 위치는 확정, HOW는 Generator 재량)
───────────────────────────────────────────────────────────────────────────────
  [번호 1] TgtFloor 큐 인큐: RcsController.DestinationQuery — `floorQueues.Enqueue(destId, fFloor)` 지점
        (여기서 pId·inductionNo·destId·chuteNo·floor 전부 가용).
     + 재시작 복원 경로 PendingFloorQueueRestorer.RestoreAsync의 재-enqueue도 번호 1 이벤트로
       발화(트리거=RESTORE로 구분).
  [번호 2] TgtFloor 큐 디큐(pop): SorterFloorReturnService.ObserveSorter — `_queues.TryPop(destId, out _)` 지점.
     ⚠ 이 두 지점(번호 1·2)에는 **기존 관측 훅이 없다** → 신규 관측 훅(로깅 전용)이 필요.
       SorterPendingFloorQueues·ObserveSorter의 판정/소비 로직은 무변경, 발화만 추가.
  (이하 WHERE의 ②=번호3 IF-10 · ③=번호4 C인큐 · ④=번호5 C디큐 · ⑤=번호6 C클리어)
  ② IF-10: RcsController.DepositReport 진입부 — 이미 `if10ReceivedAtUtc = DateTime.UtcNow`를
        캡처 중. 여기서 pId·barcode·chuteNo·agvNo 가용. 전용 로그 발화만 추가.
  ③ C 인큐: HandshakeOrchestrator.ExecuteAsync의 `EmitStage("HS_C_SENT", {cellNo,cSeq})`
        (기존 OnStage 훅 — bundle.SubscribeHandshakeStage로 구독됨).
  ④ C 디큐: PlcGateway.ProcessWriteAsync CellAssign case의 `EmitWrite("CELL_ASSIGN", …)`
        (기존 OnWrite 훅 — bundle.SubscribeWrite로 구독됨).
  ⑤ C 클리어: PlcGateway.EmitRegisterChanges의 `OnRegisterChange("C_Flag", 1, 0)`
        (기존 훅 — bundle.SubscribeRegisterChange). reg=="C_Flag" && old==1 && new==0만 필터.
        ⚠ C_Flag 1→0 순간 C_Seq(D1)도 0으로 클리어되므로 이 델타는 cSeq를 담지 못한다 →
          상관키는 소터별 "직전 미결 C"(마지막 CELL_ASSIGN의 cSeq/cellNo/pId)에서 해소.
          핸드셰이크는 소터별 직렬이라 미결 C는 항상 유일(모호성 없음).
        ⚠ 이 이벤트는 R 클리어(R_Flag 1→0, ClearR)와 **명확히 구분**한다(사용자 확인 항목 3).

───────────────────────────────────────────────────────────────────────────────
상관키(Correlation) — 설계 결정
───────────────────────────────────────────────────────────────────────────────
  두 부류의 흐름이 존재한다. 하나의 상관키로 억지로 묶지 않는다.

  (A) 피스 흐름(이벤트 ②→③→④→⑤ = 한 물리 피스):
      · **1차 상관키 = pId** (RCS 부여, IF-10에 존재). 운영자가 "한 피스"를 따라가는 키.
      · **기술 조인키 = (chuteNo/destId, cSeq)**. cSeq는 소터별 단조 증가라 C/R 핸드셰이크
        1건을 유일하게 식별. ③④는 cSeq를 네이티브로 보유, ⑤는 소터별 미결 C에서 해소.
      · pId 전파: pId는 IF-10·TriggerSorterHandshake에 가용하나 ③④⑤ 발화 시점엔 아직
        연결돼 있지 않다. Generator는 pId를 C 흐름 이벤트에 **실시간으로 실어야** 한다 —
        (i) 기존 depositedAtUtc처럼 핸드셰이크에 상관 컨텍스트를 선택적으로 전달하거나,
        (ii) 소터별 (cSeq→pId) in-메모리 매핑을 C 인큐 시점에 채우는 방식.
        어느 쪽이든 **단일 쓰기 큐 우회·제2 write 경로 신설 금지, Wcs.Core 무접촉**.
        WHAT 요구: C 흐름 각 레코드는 pId·cSeq·cellNo·chuteNo(destId)를 모두 담는다.
      · ★ 사용자-부여 번호(OQ6): 확정되면 이 번호도 피스 흐름 전 레코드(②③④⑤)에 전파해
        1차 표시 상관키로 쓴다(pId와 별개면 둘 다 기입).

  (B) 층-큐 흐름(이벤트 ① = 소터·층 단위, FIFO):
      · TgtFloor 큐 인큐/디큐는 **피스 단위가 아니라 소터+층 단위**다(층 1건이 다수 피스를
        겸할 수 있음 — 큐 [A,A,B]에서 A 정렬 1회가 A 피스 2건을 수용). pId로 묶는 것은
        의미상 틀리다.
      · 상관키 = **(chuteNo/destId, floor, 큐 시퀀스/FIFO 위치)**. 인큐 레코드는 트리거 pId·
        inductionNo·사용자-부여 번호를 **best-effort 컨텍스트**로 함께 남긴다(IF-05 시점 가용).
        디큐(pop)는 큐가 floor(int)만 저장하므로 pId/번호 없이 소터+층+FIFO로 상관한다.
      · 로그에 이 경계를 명시(TgtFloor 이벤트는 층-scope임을 운영자가 오해하지 않게).
      · 큐 자료구조(SorterPendingFloorQueues: ConcurrentQueue<int>)는 **변경하지 않는다**
        (로깅 전용 원칙 — (pId,floor) 튜플로 바꾸면 공유 상태 변경이라 스코프 밖). → 사용자
        확인 항목 1 참조.

───────────────────────────────────────────────────────────────────────────────
Implementation Scope (Generator가 구현할 것)
───────────────────────────────────────────────────────────────────────────────
  [백엔드 — 전용 싱크]
  1. 전용 로그 싱크 신설(Wcs.Api 계층 — 절대규칙 #8: 로깅은 I/O). IOperationLogger/
     OperationLogService와 **동형의 논블로킹 백그라운드 채널 싱크**로 만든다:
     발화는 즉시 반환(폴/핸드셰이크/HTTP 핫패스 무블로킹), 컨슈머가 별도 스레드에서 파일 기입.
     · 파일 위치·롤링·보존·파일명·outputTemplate 전부 **appsettings 설정값**(절대규칙 #7).
       기본 경로 = `D:\Rcs3dsInterlockingWcsLogs`(폴더 없으면 생성). 코드에 경로 리터럴 0.
     · 포맷 = **구조화 1줄/이벤트**: 각 줄에 시각(로컬·ms)·event종류(5종 중 1)·상관키
       (사용자-부여 번호·pId·cSeq·chuteNo/destId·cellNo·floor)·이벤트별 파라미터. 추적 파싱 용이.
     · 구현 방식(전용 Serilog 서브로거+File 싱크 + 필터 vs 전용 채널+파일 writer)은 Generator
       재량. 단 **이 5개 이벤트만** 이 파일에 들어가고, 기존 operation_log/Serilog 파일에는
       영향 0(별도 로거 인스턴스/서브로거로 격리).
  2. 5개 계측 지점 발화 결선(위 WHERE):
     · ③④⑤는 기존 훅(SubscribeHandshakeStage/SubscribeWrite/SubscribeRegisterChange)을
       **추가 구독**(operation_log 구독과 나란히 — Program.cs 관측 결선부와 동형). 기존 구독
       무변경.
     · ①②는 신규 로깅 훅(발화만 추가). 판정/소비/응답 로직 무변경.
     · 소터별 "미결 C"(cSeq→pId·cellNo·사용자번호) in-메모리 추적으로 ⑤ 상관 해소.
  3. 전용 로그 백로그 시드용 REST 엔드포인트 신설(기존 GET /api/monitor/operation-log 패턴
     재사용 — take clamp). 최근 N개 트레이스 레코드를 구조화 JSON으로 반환. 로그 디렉터리
     부재 시 500이 아니라 생성 후 빈 목록 반환.
  4. 실시간 전달 — 기존 모니터링 SignalR **재사용**:
     · WcsMonitorHub에 트레이스 옵트인 그룹(예: "trace") + Subscribe/Unsubscribe 허브 메서드
       추가(GroupOpLogPoll 옵트인 패턴과 동형).
     · MonitorRelayService가 전용 싱크의 OnEntry(OperationLogService.OnEntry 동형)를 구독해
       "trace" 그룹으로 fire-and-forget 브로드캐스트(예외 격리·논블로킹 — 기존 Broadcast 패턴).
     · 신규 push 메서드 + DTO(카멜케이스, MonitorHubContracts 동형).

  [프론트 — 전용 뷰어]
  5. 신규 페이지 + 라우트(App.tsx) + 좌측 네비 항목(Layout NAV_SETS) 추가. 발견 가능해야 함
     (고아 페이지 금지 — Evaluator 규칙). OpLogTail.tsx를 템플릿으로:
     · 접속 시 REST 백로그(최근 N) 시드 → 이후 SignalR로 실시간 append(시계열, 최신 하단,
       행수 상한 유지).
     · 필터: event종류 + 상관키(사용자번호/pId/cSeq)로 한 피스 흐름을 좁혀 보기.
     · 표시 형식 = 테이블(시각·event·사용자번호·pId·cSeq·chuteNo·cellNo·floor·detail 컬럼).
       상관키로 그룹/필터해 ②→③→④→⑤ + 관련 ① 순서를 이어볼 수 있게.
  6. "창 닫히면 실시간 스트림 종료" — 뷰어 페이지가 마운트 시 "trace" 그룹 구독, 언마운트/창
     닫힘 시 구독 해제. 마지막 창이 닫히면 그룹이 비어 서버 push는 no-op(서비스·서버는 계속
     동작). 재접속 시 백로그 재시드 + 실시간 재스트리밍. (앱 수명 monitorHub 연결은 유지하되
     trace 구독만 페이지 수명에 종속시키는 방식 권장 — HOW는 Generator 재량.)

  [설정]
  7. appsettings에 전용 트레이스 로그 섹션 신설(경로·rollingInterval·fileSizeLimit·
     retainedFileCount·파일명 패턴·백로그 take 기본/상한·트레이스 그룹 활성 등). 기본값 제시,
     하드코딩 0.

───────────────────────────────────────────────────────────────────────────────
중복(기존 operation_log와의 관계) — 설계 결정
───────────────────────────────────────────────────────────────────────────────
  · 기본 결정 = **추가(additive)**. 기존 operation_log/Serilog는 지금처럼 HANDSHAKE/PLC_WRITE/
    POLL_CHANGE/API를 계속 남긴다(무변경·회귀 0). 전용 파일은 그 위에 **큐레이트된 5개 흐름을
    상관키와 함께** 별도로 남기는 추가 싱크다. 기존 로그에서 이벤트를 "이동/제거"하지 않는다.
  · "전용 파일에만 남기고 operation_log 중복 제거" 옵션은 사용자 확인 항목 4로 남긴다(권장=추가).

───────────────────────────────────────────────────────────────────────────────
제약(반드시 준수 — 위반 시 FAIL)
───────────────────────────────────────────────────────────────────────────────
  · 절대규칙 #1: 5개 이벤트 계측은 관측/로깅만. 단일 쓰기 큐 우회·제2 write 경로 신설 금지.
    인큐/디큐 로깅은 **기존 단일 큐 지점에 훅만** 건다.
  · 절대규칙 #7: 로그 경로·롤링·보존·파일명 = appsettings. `D:\Rcs3dsInterlockingWcsLogs`는
    기본값. 하드코딩 금지.
  · 절대규칙 #8: Wcs.Core 순수성 유지. 판정 로직 무변경. HandshakeOrchestrator/PlcGateway
    (EF 비의존 계층)는 전용 싱크에 의존하지 않는다 — ILogger·콜백 이벤트만 발화하고 Wcs.Api
    싱크가 파일에 기록(기존 operation_log 패턴과 동일).
  · 성능: 5개 발화가 핸드셰이크/응답/폴 경로를 블로킹하지 않는다(논블로킹 enqueue + 백그라운드
    파일 writer + fire-and-forget SignalR). 발화·기록 실패가 본 동작을 막지 않는다(fail-safe).
  · 회귀 0: 기존 operation_log/Serilog/모니터링 SignalR/기존 테스트 전부 무영향.

- Completion Conditions (Evaluator PASS 최소 조건):
  1. WCS+Sim3ds 실행 후 IF-05→IF-10→핸드셰이크 1피스를 태우면 D:\Rcs3dsInterlockingWcsLogs에
     생성된 전용 파일에 **5개 이벤트가 구조화 1줄씩** 기입되고, 사용자-부여 번호·pId·cSeq로
     한 흐름을 이어 추적 가능함을 실제 파일 내용으로 확인(fresh evidence).
  2. 전용 파일에는 이 5개 이벤트만 들어가고, 기존 operation_log/Serilog 파일 내용/스키마는
     무변경(회귀 0). 기존 테스트 스위트 전부 GREEN(Generator·Evaluator 독립 재실행).
  3. 프론트 신규 뷰어가 네비에서 발견 가능하고, 백로그 시드 + 실시간 append가 브라우저에서
     동작(스크린샷·콘솔 캡처). 창을 닫으면 스트림이 종료되고 서비스는 계속(재접속 시 재시드).
  4. 로그 경로/롤링/보존이 appsettings에서 읽히고 기본이 D:\Rcs3dsInterlockingWcsLogs임을
     설정 변경으로 확인(하드코딩 리터럴 부재).
  5. 발화 경로가 논블로킹임을 코드 구조로 확인(핫패스에서 동기 파일 I/O·SaveChanges 없음).
  6. ★ 모든 트레이스 레코드에 **이벤트 번호(1~6)** 가 정확히 태깅됨을 파일·화면에서 확인
     (1=TgtFloor인큐·2=TgtFloor디큐·3=IF-10·4=C인큐·5=C디큐·6=C클리어). 번호로 이벤트 종류를
     즉시 식별 가능. 피스 흐름(3~6)은 pId+(chuteNo,cSeq)로 상관되어 한 흐름 재구성 가능.

- Parallel Modules: N/A (single module — 백엔드 계약이 프론트 소비의 선행 의존).
- Evaluation Dimensions: functional only (관측/로깅 전용 additive 기능. 논블로킹은 완료조건 5).

───────────────────────────────────────────────────────────────────────────────
- Detected Project Type: Full-stack
  (레포 신호: frontend/src/**/*.tsx React SPA 진입점 + backend/src/Wcs.Api Controllers·SignalR
   허브가 같은 레포에 공존.)

───────────────────────────────────────────────────────────────────────────────
- Verification Scenarios (Full-stack — 필수)

  === Applicable Web/UI scenarios (신규 트레이스 뷰어 표면) ===
  W1 Default state: 뷰어 진입 시 REST 백로그(최근 N) 시드 → 테이블 렌더(시각·event·사용자번호·
     pId·cSeq·chuteNo·cellNo·floor·detail 컬럼). 스크린샷으로 확인.
  W2 Live-append state: 백엔드에서 1피스를 태워 새 트레이스 이벤트가 SignalR로 도착 → 테이블
     하단에 실시간 append(자동 스크롤). before/after 스크린샷.
  W3 Correlation/filter state: 사용자번호(또는 pId/event종류/cSeq) 필터로 한 피스의 ②③④⑤가
     한 화면에서 이어짐을 확인(click-through: 필터 입력 → 좁혀진 결과 스크린샷).
  W4 Empty state: 트레이스 이벤트가 아직 없을 때 "표시할 로그 없음" 안내(크래시·빈 흰화면 아님).
  W5 Error/disconnected state: 허브/백엔드 미가용 시 연결 상태 표시·에러 안내, 콘솔 uncaught
     예외 0(console.log 캡처 — BLOCKING 규칙). React dev-mode warning 0.
  W6 Window-close → stream ends: 뷰어 탭을 닫으면 그 클라이언트 구독 종료(서버는 빈 그룹 push
     no-op·서비스 계속). 재오픈 시 백로그 재시드 + 실시간 재스트리밍.
  Dark mode: N/A — 앱은 단일 고정 테마(라이트/다크 토글 부재).

  === Applicable Backend/API scenarios (백엔드 표면) ===
  B1 Endpoints/surfaces touched:
     · 신규 REST: GET 전용 트레이스 백로그(method+path — Generator 확정, /api/monitor 하위
       권장). take clamp + 커서(기존 operation-log 엔드포인트 패턴).
     · 신규 SignalR: WcsMonitorHub SubscribeTrace / UnsubscribeTrace + TraceEvent push.
     · 회귀 대상(무변경 확인): POST /api/v1/destination-query(IF-05), POST /api/v1/deposit-report
       (IF-10) — 응답 형상·상태코드 불변.
  B2 Happy path:
     · GET 백로그 → 최근 N개 구조화 트레이스 레코드(200). take 상한 clamp.
     · IF-10 → {result:"OK"} 불변; 5개 발화 결과 전용 파일에 5줄 생성(파일 내용 assert),
       각 줄에 사용자-부여 번호 포함.
     · IF-05 → {result,chuteNo} 불변; TgtFloor 인큐 이벤트 1줄(트리거·floor·pId·번호·큐 depth).
  B3 Error cases:
     · 로그 디렉터리 부재 → 자동 생성 후 빈 목록(500 금지).
     · take 파라미터 범위 밖 → clamp(또는 400) — 기존 정책 동형.
     · IF-05/IF-10 검증 실패(pId 범위·barcode 등) → 기존 400 동작 불변(회귀 확인).

  === End-to-end data-flow scenario (2+ 계층 교차) ===
  E1 전(全) 피스 흐름 계측 E2E: 실 Sim3ds + WCS 기동 → IF-05 POST(→ ① TgtFloor 인큐 + 관측
     루프 pop 시 디큐) → IF-10 POST(→ ② 도착) → 핸드셰이크 진행(→ ③ C 인큐 → ④ C 디큐/Modbus
     write → Sim이 C 읽고 C_Flag 1→0 클리어 → ⑤ C 클리어 관측) → **전용 파일에 5개 상관 레코드**
     (사용자번호·pId·cSeq로 한 흐름 재구성 가능)가 남고, **동시에 SignalR trace 그룹으로
     스트리밍**됨을 검증. 그런 뒤 브라우저 뷰어가 그 5건을 렌더함을 확인(계층: PLC/Gateway →
     Api 싱크 → 파일 + SignalR → 프론트).
  E2 Zero-regression E2E: 위 흐름에서 기존 operation_log에도 5개 이벤트가 종전대로 남고
     (additive), 기존 monitor/oplog SignalR·기존 xUnit 스위트가 전부 GREEN.

───────────────────────────────────────────────────────────────────────────────
- Open Questions for User — ✅ 전부 확정됨(2026-07-28 게이트, 위 "확정 결정" 블록이 정본). 아래는 이력.
  1. 상관키: 권장 = pId 1차 + (chuteNo,cSeq) 기술 조인. TgtFloor 큐 이벤트는 소터+층 scope
     (pId 아님) — 큐 자료구조 무변경 원칙상 **pop은 피스 단위 상관 불가**. 이 경계 수용 확인.
  2. 프론트: 신규 전용 페이지(권장) vs 기존 모니터 탭. 표시 = 테이블(권장) vs 타임라인.
  3. ⑤ "C영역 클리어" = C_Flag 1→0 폴 관측(권장·R 클리어와 구분) 확인.
  4. 전용 파일에 5개만 — 기존 operation_log에도 계속 남길지(권장=additive·회귀 0) vs 전용에만.
  5. 보존/크기/롤링: 권장 = 일(Day) 롤링 + 크기롤(100MB) + 보존 N일(30일) + 파일명 trace-.log.
  6. ★ "내가 부여한 번호" = 정확히 무엇인가? (pId / orderNo / barcode / agvNo / 기타)
     그 번호를 **모든 트레이스 줄**에 기입(피스 흐름 ②③④⑤ 전파). pId와 다르면 둘 다 기입.
     TgtFloor 디큐(pop)는 구조상 그 번호를 못 실을 수 있음(경계 명시).

───────────────────────────────────────────────────────────────────────────────
> Planner self-check — Detected project type: Full-stack. Required scenario slots: 3
  (Applicable Web/UI scenarios [W1–W6], Applicable Backend/API scenarios [B1–B3],
  end-to-end data-flow [E1–E2]). All slots filled: yes.
