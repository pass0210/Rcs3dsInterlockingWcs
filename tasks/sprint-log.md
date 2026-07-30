# Sprint Log — S-TWO-FLOOR-WRITE-ON-CLEAR

## IMPLEMENTATION COMPLETE (Generator, 2026-07-29)

### 변경 요약 (스코프: SorterFloorReturnService + 순수 DepositDecider + 테스트. Sim3ds·PlcGateway·RcsController·PendingFloorQueueRestorer·SorterGatewayRegistry 무변경)

**S4 — 순수 게이트(Wcs.Core, 규칙 #8)**
- `DepositDecision.Allow(int? writeTgtFloor = null)` 오버로드 추가(기존 `Allow()`는 null 기본으로 호환). `Models.cs`.
- `DepositDecider.Decide`: 정렬-유휴(Ready==1 && CurFloor==F) 케이스가 `TgtFloor==0`이면 `Allow(F)`(write-on-clear),
  `TgtFloor!=0`이면 `Allow(null)`. **`.Ready`/`.Reason`은 불변**(Ready=true·None) — `.WriteTgtFloor`/`.TgtFloorValue`만 변경.
  busy(Ready==0)·NotAligned 케이스는 종전대로(TgtFloor==0에서 write). 푸시 계약(ComputeSorter/Pusher가 floor=CurFloor로
  `.Ready`/`.Reason`만 소비) byte-identical 보존 확인.

**S1/S2/S3 — 관측 루프(SorterFloorReturnService.ObserveSorter)**
- POP 트리거(S2): 구 "분류 사이클(Ready 1→0→1) 제자리" pop → **TgtFloor 비영→0 클리어 에지** pop으로 교체. 에지당 정확히 1 pop.
  - `ObserveState`: `PrevReady`/`CycleStartFloor` 제거 → `Armed`(첫 TgtFloor==0 관측 후 무장)/`PrevTgtFloor` 추가.
  - **재량 결정(에지 감지 방식)**: 계약의 "baseline on first obs" 대신 **"첫 TgtFloor==0 관측에서만 무장(arm-on-0)"** 채택.
    이유(안전): StartupClear는 첫 Online 스냅샷 게시 전에 큐 투입되나 처리는 비동기라 **첫 관측이 잔류 비영(예: 2)을 볼 수 있음** →
    "baseline=current"면 StartupClear의 2→0을 분류-시작으로 오인해 복원 큐 머리를 조기 pop(over-pop=오분류/안전사고). arm-on-0은
    잔류를 볼 때까지 무장을 보류해 이 스퓨리어스 pop을 원천 차단. 계약의 goal (a)/(b)(스퓨리어스 pop 0·OFFLINE fabricated 에지 0)을
    더 강하게 충족. OFFLINE 시 `Armed=false`로 재무장.
  - pop 후 같은 틱에 새 머리를 기입(OQ1), 큐 비면 미기입·TgtFloor 0 유지(OQ2).
- WRITE 트리거(S1): 구 `!ready 조기반환 · CurFloor==F 스킵` 제거. 이제 큐 비지 않으면 매 틱 `IsPaused`(저비용·I-2) 후
  `DepositDecider.Decide`에 위임 — write-during-busy·same-floor hold 실현은 순수 함수가 담당(TgtFloor==0이면 F write). FULL 미차단(Q5),
  Paused/Offline 차단(#2). 단일 쓰기 큐 경유(#1)·WCS는 0 미기입(#3)·fresh-read dedup은 PlcGateway(무변경) 유지.
  - **재량 결정(DepositDecider에 전면 위임)**: `IsPaused`를 `TgtFloor!=0`일 때 스킵하지 않고 큐 비지 않으면 매 틱 호출 — 이는 기존
    `TwoFloorWriteGateI2Tests`(VS-E2a "IsPaused 매 틱·Compute 0")의 불변식을 보존하기 위함(수정 없이 GREEN). 규칙 #8 정합(판정=순수).
- STALL 재조정(S3·재량 결정): 구 조건(유휴 ∧ TgtFloor==0 ∧ 머리 존재)은 새 모델에서 성립 불가(머리 있으면 즉시 F 기입→TgtFloor≠0).
  새 under-pop 시그니처 = **유휴(Ready==1) ∧ 정렬(CurFloor==머리) ∧ 머리 불변 N틱**(AGV abandonment). busy/미정렬/오프라인/PAUSED/
  큐 빔/pop 진행에서 리셋. 관측 전용(쓰기·pop 0). 에피소드당 1회·재무장. `DetectStall` 시그니처에서 `ready` 파라미터 제거(snap에서 파생).
- 트레이스 event2(S3·재량 결정): EventNo=2·"TGTFLOOR_DEQUEUE"·피스당 1회 불변, `Trigger` "SORT_CYCLE"→**"SORT_START_CLEAR"**,
  Detail(curFloor·remainingDepth) 유지. 타이밍만 분류-시작으로 시프트(E2EGroupN N1이 Floor/PId만 단언 — 무영향).
- 문서: `WcsOptions` ObserveIntervalMs/StallSuspectTicks XML doc를 새 에지-pop·abandonment-stall 모델로 갱신(리터럴 변경 0·규칙 #7 유지).

### 테스트 결과 (dotnet test backend/Wcs.sln — SQL Server 아님·기존 SQLite 테스트 provider)
- 전량 GREEN: **493 통과 / 0 실패** (baseline 487 + 신규 C4 6건 = 493, 산술 일치·회귀 0). full 5회 중 3회 493/493.
- 신규 결정적 테스트 `WriteOnClearTests`(C4, 6건): C4-1 write-during-busy·C4-2 same-floor hold·C4-3/4 one-pop-per-clear+빈큐 park+
  다음피스 복구·C4-5a StartupClear 잔류 2→0 스퓨리어스 pop 0·C4-5b OFFLINE 재무장 fabricated pop 0+재무장 실효. FakeModbusMasterForApi 하니스.
- 업데이트(계약 C3 + 필연적 파생):
  - `DepositDeciderTests`: Row1·FloorParam_F1_AtFloor1 → WriteTgtFloor false→true(.Ready/.Reason 불변 단언 유지). 비영-TgtFloor 케이스(C1/Row3/Row5/ping-pong) write=false 유지.
  - `SorterStallDetectorTests`: `Stall_HeadAlignedIdle_*`를 abandonment 시그니처로 재작성(재무장은 Ready 토글 — TgtFloor 토글은 클리어
    에지 pop 유발하므로 금지). D6는 S1 정렬값 유지(=1)로 단언 변경. 나머지 CC1.1(빈큐·사이클·오프라인·PAUSED·비활성)·크로스레이어는 무변경 GREEN.
  - `E2EGroupAB_NormalAndGateTests.A3`(계약 미열거이나 write-on-clear가 직접 반증하는 superseded "aligned=no write" 계약 — S1
    "even CurFloor==head"의 필연): "D6 쓰기 0" → "same-floor hold D6=2 1건·이동 없음(CurFloor 2 유지)"으로 개정. **의도(스퓨리어스 재정렬
    이동 0) 보존**. A4(미정렬→write 1건)·C7(OFFLINE→write 0)·K1/K2/K3·L/M·TwoFloorHostRouting·push군 전부 무변경 GREEN.
- flake 귀속(교훈 e2e-parallel-load-surfaces-integration-flakes): full 부하에서 (a) E2EGroupN N1(실-Sim 트레이스, isolation 4/4 GREEN),
  (b) RtuTransportTests VT4(PlcGateway 단위·1000ms WaitUntil, isolation 3/3 GREEN, 내 변경 코드 무접촉) 각 1회 저빈도 flake — 둘 다 선재
  환경 flake로 귀속. **C4-5b는 내가 도입한 테스트 flake였고 근본수정 완료**(오프라인 창이 관측 주기보다 짧아 Armed 재설정을 놓침 → 오프라인
  감지 후 정착 대기 추가; 현장 오프라인 창 ≫ 관측 주기 반영). 수정 후 full 2회 연속 493/493.

### 정적 검사 (C6)
- `dotnet build backend/Wcs.sln`: 오류 0. 경고 13(전부 선재: NU1903 ×10 [SQLitePCLRaw advisory, base develop 선재]·CS8604 [B2cFacilityService]·
  xUnit2013 ×2 [ChuteStatePushTests·TwoFloorHostRoutingTests, 미접촉 라인]). **신규 경고 0**(변경 파일에서 CS/analyzer 경고 0).
- 포맷터/린터: 백엔드 전용 포맷터 미구성(not-configured). 프론트엔드 무변경(TraceLogPage 표시-전용 결과만 — 코드 diff 0).

### 절대규칙 증명
- #1 TgtFloor 기입 전부 `bundle.EnqueueSetTgtFloorAsync`(단일 쓰기 큐) 경유 — 직접 Modbus 0. #2 write는 관측 TgtFloor==0에서만·비영 미덮어씀
  (DepositDecider가 구조적 보장). #3 WCS는 D6에 0 미기입(K1/K2/K3 `DoesNotContain →0` 유지·write 경로 값 ∈ {1,2}). #7 주기·임계 appsettings.
  #8 판정은 Wcs.Core 순수 함수(관측 루프는 I/O·상태 트리거만·Compute heavy 미호출).
