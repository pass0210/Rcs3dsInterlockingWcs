# Sprint Log — S-TRACE-LOG-VIEWER

(Generator가 IMPLEMENTATION COMPLETE 마커와 변경 요약을 여기에 추가)

## IMPLEMENTATION COMPLETE (Generator, 2026-07-28)

전용 추적 로그 sink + 실시간 프론트 뷰어. 6개 이벤트(1~6)를 각 줄 앞머리 이벤트번호 태그로 전용 파일에
기입 + SignalR trace 그룹으로 실시간 스트리밍 + REST 백로그 + 신규 프론트 페이지. 전부 additive·관측/로깅 전용.

### 변경 요약 (파일)
[백엔드 — 신규]
- `backend/src/Wcs.Api/Services/TraceLogService.cs` (신규) — 핵심. 포함:
  · `TraceLogOptions`(appsettings "TraceLog" — Enabled·Directory·FileNamePattern·RollingInterval·
     FileSizeLimitBytes·RetainedFileCountLimit·BacklogTakeDefault/Max·ClampTake). 기본 경로 D:\Rcs3dsInterlockingWcsLogs.
  · `TraceRecord`(파일 1줄·SignalR payload·백로그 반환의 단일 형상 — EventNo 1~6·Event·At(로컬 ms)·PId·
     CSeq·ChuteNo·DestId·CellNo·Floor·InductionNo·Trigger·Detail).
  · `ITraceLogger`(논블로킹 Log + OnEntry) / `ITraceBacklog`(파일 tail Read).
  · `TraceLogService` — OperationLogService 동형 논블로킹 채널 sink + 백그라운드 컨슈머가 OnEntry 발화 후
     **전용 Serilog File 로거**(전역 Log.Logger 와 격리)에 `[N] {json}` 1줄 기입. 롤링/크기/보존 = Serilog File 싱크.
     디렉터리 생성 실패는 fail-safe(파일 비활성·relay 계속). 백로그 = 롤링 파일 tail(FileShare.ReadWrite) + 필터 + clamp.
  · `TraceCorrelator` — C 흐름(이벤트 4·5·6) pId 실시간 상관(소터별 FIFO 등록 → C인큐 pop → cSeq→ctx 저장 →
     write 조회 → C클리어는 소터별 "미결 C"에서 해소). 소터 직렬 전제·미등록 fail-safe(pId 미상).
  · `TraceWiring.Wire` — 번들 기존 훅(HS_C_SENT/CELL_ASSIGN/C_Flag 델타)에 이벤트 4·5·6 추가 구독 결선.

[백엔드 — 계측 훅(관측/로깅 전용·기존 로직 무변경)]
- `Controllers/RcsController.cs` — 이벤트 1(IF-05 floorQueues.Enqueue 지점)·3(IF-10 DepositReport 진입) 발화 +
  TriggerSorterHandshake 에서 `correlator.RegisterHandshake` (ExecuteHandshakeAsync 직전, C 흐름 pId 상관).
- `Services/SorterFloorReturnService.cs` — 이벤트 2(관측 루프 분류-사이클 pop 지점) 발화(ITraceLogger 주입).
- `Services/PendingFloorQueueRestorer.cs` — 이벤트 1(재시작 복원 re-enqueue, 트리거=RESTORE) 발화 + 사영에 ChuteNo 추가.
- `Program.cs` — SorterRegistryFactory.StartAsync 관측 결선부에 `TraceWiring.Wire` 추가 구독(operation_log 구독과 나란히) +
  DI 등록(TraceLogOptions·TraceLogService as ITraceLogger/ITraceBacklog/IHostedService·TraceCorrelator).
- `Hubs/WcsMonitorHub.cs` — `GroupTrace="trace"` + `SubscribeTrace`/`UnsubscribeTrace` 허브 메서드(옵트인).
- `Services/MonitorRelayService.cs` — ITraceLogger.OnEntry 구독 → "trace" 그룹 fire-and-forget 브로드캐스트("Trace").
- `Controllers/MonitoringController.cs` — `GET /api/monitor/trace?take=&eventNo=&pId=&cSeq=` 백로그(clamp·디렉터리 부재 시 빈 목록).
- `appsettings.json` — "TraceLog" 섹션 신설(전부 설정값·기본 D:\Rcs3dsInterlockingWcsLogs).

[프론트 — 신규 뷰어]
- `frontend/src/lib/signalr.ts` — `TraceEvent` 타입 + trace 옵트인 구독(subscribeTrace: 첫 구독=SubscribeTrace,
  마지막 해제=UnsubscribeTrace / connect·reconnected 시 재동기) + `conn.on('Trace')`.
- `frontend/src/lib/api.ts` — `TraceRecord` + `api.trace()` REST 백로그.
- `frontend/src/pages/TraceLogPage.tsx` (신규) — 백로그 시드 → SignalR append. 테이블(번호·시각·이벤트·pId·cSeq·
  chuteNo·cellNo·floor·detail) + 필터(이벤트번호/pId/cSeq) + 자동스크롤 + 연결배지 + empty 상태. 마운트 구독/언마운트 해제.
- `frontend/src/App.tsx` — `/trace` 라우트. `frontend/src/components/Layout.tsx` — b2c NAV "추적 로그" 항목.

[테스트]
- `backend/tests/Wcs.Tests/TraceLogTests.cs` (신규 7건) — TraceCorrelator 상관(피스 흐름/FIFO/미등록 fail-safe) +
  TraceLogService 파일 기입([N] 태그 fresh evidence)·백로그 tail/필터/clamp·디렉터리 부재·비활성 no-op. scratch temp 디렉터리.
- `backend/tests/Wcs.Tests/E2E/E2EGroupN_TraceLogTests.cs` (신규 1건) — 실 Sim+WCS 1피스 E2E: 6개 이벤트 전량 기입·
  번호 정확·pId+(chuteNo,cSeq) 상관 재구성 + REST 백로그 조회 + additive 회귀 0(operation_log/sorter_command 유지). per-test scratch 디렉터리.
- `backend/tests/Wcs.Tests/TraceTestDoubles.cs` (신규) — NopTraceLogger/CapturingTraceLogger.
- `TestAssemblyInit.cs` — 테스트 프로세스 전역 env `TraceLog__Directory`=temp(실경로 D:\ 무접촉·절대규칙 #7 테스트 지침).
- `E2E/E2EInfrastructure.cs` — E2E 팩토리에 옵션 `traceLogDir`(per-test scratch) + `simLoopMs`(기본 10) 추가(테스트 인프라).
- 기존 3개 서비스 생성자 호출부(SorterStallDetectorTests·PendingFloorQueueRestorerTests·TwoFloorHostRoutingTests)에 NopTraceLogger 인자.

### 재량 결정
- **상관 전파 방식**: 계약 옵션 (ii) — RcsController 가 핸드셰이크 직전 소터별 FIFO 로 (pId,cellNo,chuteNo) 등록,
  이벤트 4(HS_C_SENT, cSeq 확정)에서 pop 해 cSeq→pId 상관 성립. 이벤트 5는 cSeq→ctx 조회, 이벤트 6은 소터별 "미결 C"
  에서 해소(소터 직렬 전제 — 모호성 0). HandshakeOrchestrator/PlcGateway 무접촉(절대규칙 #8) — 기존 콜백만 소비.
- **엔드포인트 경로**: `GET /api/monitor/trace`(기존 operation-log 백로그 패턴 재사용, /api/monitor 하위).
- **페이지 라우트**: `/trace`, b2c NAV "추적 로그".
- **sink 구현 방식**: 전용 채널 + 백그라운드 컨슈머 + **전용 Serilog File 서브로거**(롤링/크기/보존을 File 싱크로 획득,
  전역 로그와 인스턴스 격리). 파일 1줄 = `[N] {json}`(앞머리 [N] 이벤트번호 태그 + 백로그 파싱 대칭 JSON).
- **이벤트 6 결정성**: C_Flag 1→0 은 폴 관측(계약 명시)이라 Sim 이 C_Flag 를 즉시(10ms) 클리어하면 30ms 폴이 놓친다.
  E2E 는 현장 PLC 처럼 Sim 루프를 150ms 로(테스트 인프라 옵션 simLoopMs) 늘려 dwell 확보 → 결정적 관측(3회 반복 flake 0).

### 테스트 결과
- 신규: TraceLog/E2EGroupN 필터 8/8 GREEN × 3회 반복(flake 0).
- 전체: `dotnet test backend/Wcs.sln` **485/485 GREEN × 2회 연속**(실패 0). baseline 477 + 신규 8 = 485(산술 일치·회귀 0).
- 빌드: 오류 0. 경고 = 선재 NU1903(SQLitePCLRaw)뿐 — touched 파일 CS 경고 0.
- 프론트: `npm run typecheck`·`npm run lint`·`npm run build`(tsc+vite, wwwroot 산출) 전부 성공(선재 chunk>500kB·signalr PURE 경고만).
- 격리: 전용 로그는 테스트 전역 env(temp)·E2E per-test scratch 디렉터리 — 실경로 D:\Rcs3dsInterlockingWcsLogs 무접촉.

### 미확인(Evaluator 브라우저 검증 권장 — W1~W6)
- 백엔드 계측·상관·번호 태깅·백로그·SignalR 는 자동 테스트로 실증. 프론트 뷰어의 실제 브라우저 렌더(W1 기본·W2 라이브
  append·W3 필터·W4 empty·W5 콘솔 0·W6 창닫힘 스트림 종료·재시드)는 라이브 스택 + Playwright 로 확인 권장.

## FIX ITERATION 1 (Generator, 2026-07-28) — 코드리뷰 CRITICAL 1건 수정

### CRITICAL — TraceCorrelator `_pending` 누수 + pId 오귀속 (수정 완료)
증상: `RcsController.TriggerSorterHandshake` 가 `ExecuteHandshakeAsync` 직전 `RegisterHandshake` 를 **무조건** 호출하는데,
`HandshakeOrchestrator.ExecuteAsync` 는 `HS_C_SENT`(이벤트 4·유일 소비자 ResolveCSent) 前에 조기 종결 경로가 있다 —
시작 OFFLINE·잔류(arming) 실패·안착지연 OFFLINE(전부 cSeq 증가 前) + **cSeq 증가 後·HS_C_SENT 前** 의 C_Flag 대기
OFFLINE·CFlagTimeout. 그럼 등록 head 가 소비되지 않아 (a) `_pending` 무한 증가, (b) FIFO 특성상 고아 head 가 매핑을
한 칸 밀어 다음 성공 핸드셰이크가 이전 피스 pId 로 오귀속(완료조건 #6 무력화·off-by-N 자가지속).

수정(전부 Wcs.Api — 절대규칙 #8 준수·HandshakeOrchestrator/PlcGateway/Wcs.Core 무접촉):
- `TraceCorrelator` 재설계 — `_pending` 을 소터별 (락 + LinkedList) 로 교체. `RegisterHandshake` 가 **소비 플래그(Consumed)
  를 지닌 토큰**을 반환. `ResolveCSent`(HS_C_SENT)가 head pop 시 Consumed=true. 신규 `DiscardPending(destId, token)` 은
  토큰이 미소비면 그 토큰만 identity 로 정확히 제거(동시 등록된 다음 피스 무영향), 이미 소비됐으면 no-op(**idempotent**).
  → `RcsController` continuation(성공·실패·조기종결·호스트종료 어떤 경로에서도 항상 실행)의 **최상단에서 무조건**
    `correlator.DiscardPending(destId, traceToken)` 호출. **SentCSeq 판정에 의존하지 않아** cSeq 증가 後 조기종결
    (CFlagTimeout·OFFLINE-during-C_Flag-wait, SentCSeq≥1)까지 전부 포섭한다.
    ※ 코드리뷰가 제시한 `result.SentCSeq==0` 판정은 **불완전**(cSeq 증가 後 HS_C_SENT 前 종결 경로를 놓침 + Offline 은
      SentCSeq≥1 에서 pre/post-HS_C_SENT 구분 불가)이라, 더 견고한 토큰-소비-플래그 방식으로 대체했다.
- 방어 심화: 소터별 `_pending` 상한 `MaxPending=32` — discard 누락이 무한 증가하지 못하게 최오래 항목 WARN + 축출(Fail Loud).
- `PendingCount(destId)` 진단 API 추가(테스트·누수 검증용). `TraceCorrelator` 는 선택적 ILogger 주입(테스트는 인자 없이 생성).

### IMPORTANT(문서만) — 한 소터 동시 IF-10 상관 교차
등록은 IF-10 도착 순서이나 cSeq 는 ExecuteAsync 내부에서 나중에 부여되므로 한 소터 **동시** IF-10 은 pId↔cSeq 교차 가능.
현 코드베이스는 동시 IF-10 을 직렬화하지 않음(lessons: single-sorter-concurrent-handshake-gap). 계약이 순차 dispatch(SPEC §6)를
전제하므로 스코프 밖. → `TraceCorrelator` 상단 주석에 알려진-한계 명시 + 프론트 뷰어(`TraceLogPage`)에 한 줄 note
("상관(pId↔cSeq)은 소터별 순차 dispatch 전제 — 한 소터 동시 투입 시 교차 가능") 경량 노출. 직렬화는 시도 안 함(스코프 밖).

### 변경 파일
- `backend/src/Wcs.Api/Services/TraceLogService.cs` — TraceCorrelator 재설계(토큰+Consumed·DiscardPending·MaxPending·
  PendingCount·ILogger·serial-dispatch 한계 주석).
- `backend/src/Wcs.Api/Controllers/RcsController.cs` — RegisterHandshake 반환 토큰 캡처 + continuation 최상단 무조건 DiscardPending.
- `frontend/src/pages/TraceLogPage.tsx` — 순차 dispatch 한계 한 줄 note.
- `backend/tests/Wcs.Tests/TraceLogTests.cs` — 신규 2건(Correlator_AbortedBeforeCSent_DiscardCleansPending_NoMisattribution
  = 조기종결 후 누수 0 + 성공 피스 자기 pId 상관·오귀속 0 · Correlator_PendingCap_BoundsUnboundedGrowth = 상한 bounded).
- `backend/tests/Wcs.Tests/E2E/E2EGroupN_TraceLogTests.cs` — 신규 1건(N2: 실 Sim OFFLINE-before-C 종결 → 재기동 → 성공
  핸드셰이크 → _pending=0(누수 0) + C흐름 4·5·6 이 성공 pId 로만 상관·조기종결 pId 오귀속 없음).

### 스코프 밖(코드리뷰 지시대로 미수정 — 오케스트레이터가 후속 스프린트로 sprint-feedback 등재): reconnect 재시드 · path-literal
  기본값 · TailLines 휴리스틱 · Detail JSON 보간 · one-shot 컨슈머 루프 · 필터 그룹 churn.

### 테스트 결과(fix iteration)
- 신규 트레이스 필터(TraceLog/E2EGroupN) 11/11 GREEN × 3회 반복(flake 0). 기존 8 + 신규 3(unit 2 + E2E 1) = 11.
- 전체 `dotnet test backend/Wcs.sln` **488/488 GREEN × 2회 연속**(실패 0). 직전 iteration 485 + 신규 3 = 488(산술 일치·회귀 0).
- 빌드 오류 0 · touched 파일(TraceLogService·RcsController) CS 경고 0 · 선재 NU1903 만.
- 프론트 typecheck/lint/build 전부 성공. 스코프 무변경(Wcs.Core/Migrations/WcsDbContext/Sim3ds 소스 무접촉·미커밋).
