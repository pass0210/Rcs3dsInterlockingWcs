# Sprint Feedback — S-TWO-FLOOR-WRITE-ON-CLEAR

(Evaluator가 PASS/FAIL 및 APPROVED를 여기에 기록)

════════════════════════════════════════════════════════════════════════════
## Evaluation — 2026-07-29 (Evaluator, fresh self-run) — **APPROVED**
════════════════════════════════════════════════════════════════════════════

Branch feat/two-floor-write-on-clear · HEAD 28b213f(develop merge base) · 구현 전량 working tree 미커밋(Generator no-commit 정합). 무접촉 경계 diff 0 확인: Wcs.Sim3ds / Wcs.PlcGateway / RcsController / PendingFloorQueueRestorer / SorterGatewayRegistry.

SAFETY-CRITICAL — bar HIGH. 열거된 위험(오층 기입·early/double/under pop)을 전수 실증 부재로 확인. 모든 Completion Condition C1–C6 + Verification Scenario를 fresh evidence로 통과.

### C1 — `dotnet test backend/Wcs.sln` 전량 GREEN (자체 재실행)
- **493 passed / 0 failed / 0 skipped** (1m28s, 독립 재실행 — Generator 보고 불신 원칙). raw:
  `통과!  - 실패: 0, 통과: 493, 건너뜀: 0, 전체: 493 - Wcs.Tests.dll (net10.0)`
- baseline 회귀 0 **구조적 확인**: 수정 3개 테스트파일 Fact/Theory 수가 HEAD==working tree로 동일(DepositDeciderTests 17·SorterStallDetectorTests 8·E2EGroupAB 12) → 삭제/은닉 0. 신규 WriteOnClearTests 5 [Fact]만 추가. 실 baseline 488 + 신규 5 = 493(Generator "487+6" 라벨은 산술 오기 — 순증 493/0·삭제 0이라 무해).

### C2 — 양층 거동 스위트 GREEN·불변식 무약화
- **E2EGroupK 파일 HEAD 대비 diff 0**(K3 [A,A,B] I-1 가드가 새 구현에 무수정 GREEN = 최강 non-regression 증거).
- 표적 스위트 3회 반복: WriteOnClear·E2EGroupK·SorterStallDetector·E2EGroupAB·TwoFloorHostRouting·TwoFloorWriteGateI2 = **39/39 ×3 GREEN·flake 0**.
- K3 단언 실체(무약화): enqueue 후 큐 [1,1,2] 유지(조기 pop 0) → A1 분류 시작 후 정확히 [1,2]·`DoesNotContain "D6...→2"`(1층 hold, 조기 이동 0) → A2 후에야 2층 이동 → `DoesNotContain "D6...→0"`(규칙#3). K1/K2/L/M/push군 전부 무변경·GREEN.

### C3 — 갱신 테스트 의도 보존(약화 아님)
- DepositDeciderTests Row1·FloorParam_F1: `WriteTgtFloor false→true` + `TgtFloorValue==F` 단언 추가, **.Ready/.Reason(true/None) 단언 유지**(푸시 계약). 비영-TgtFloor 케이스(C1 residual·Row3/5·핑퐁) write=false 유지.
- SorterStallDetectorTests: 재무장을 **Ready 토글**로(TgtFloor 토글은 클리어 에지 pop 유발하므로 배제 — 올바른 선택). 관측 전용 단언 `master.GetTgtFloor()==1`(감지기가 S1 정렬값 미변경)로 적응(구 `==0`과 동등 강도의 "무 side-effect"). once-per-episode·2에피소드·큐 불변 유지.
- E2EGroupAB.A3: "D6 0건"→"same-floor hold D6=2 정확 1건(stableCount6)·`DoesNotContain "이동 시작"`·CurFloor 2 유지" — 의도(스퓨리어스 재정렬 이동 0) 보존이며 더 구체적.
- ScenarioTests의 DepositDecider.Decide 콜사이트 전수 감사: S2(NotAligned)·S3(Busy)·S4/S9(핑퐁) — 전부 이 스프린트 무영향 케이스, 갱신 누락 0.

### C4 — 신규 결정적 테스트(격리 하니스 — 현장 무접촉)
- WriteOnClearTests 5건 FakeModbusMasterForApi(인메모리 슬레이브·에페메랄, 실 5205/COM1/prod DB 무접촉): C4-1 write-during-busy(Ready==0 중 새 머리 D6 기입)·C4-2 same-floor hold(CurFloor==head도 기입)·C4-3/4 one-pop-per-clear+빈큐 park+다음피스 복구·C4-5a StartupClear 잔류 2→0 스퓨리어스 pop 0·C4-5b OFFLINE 재무장.

### C5 — 절대규칙 라이브 증명 (격리 스택 실구동)
- env override(Provider=Sqlite·scratch DB·TCP Sim 1512·seed·Urls 5215) 격리 기동 → IF-05(induction1→floor1)→IF-09→IF-10 폐루프. 트레이스 REST(GET /api/monitor/trace) 실응답:
  `{"eventNo":2,"event":"TGTFLOOR_DEQUEUE","trigger":"SORT_START_CLEAR","chuteNo":30,"floor":1,"detail":"{\"curFloor\":1,\"remainingDepth\":0}"}` — 분류 시작 시각(event4/5 셀배정과 동초)에 피스당 정확히 1건.
- api.log D6 타임라인: `[쓰기 큐] SetTgtFloor → D6=1`(write-on-clear same-floor hold) 1건뿐 · 큐 빔(remainingDepth=0) 후 추가 쓰기 0(OQ2 park) · 유일 D6=0은 `[쓰기 큐] StartupClear`(콜드스타트, 규칙#3 허용 예외). **정상운영 WCS 0 미기입(#3) 라이브 실증**. 전 기입 단일 쓰기 큐 경유(#1)·직접 Modbus 0. Models.cs 변경은 WriteTgtFloor/TgtFloorValue에만 additive·.Ready/.Reason byte-identical(#8). 주기·임계 리터럴 무변경(#7).
- arm-on-first-TgtFloor==0 재량 결정 VERIFIED: StartupClear 잔류 2→0을 pop 에지로 오인 안 함(C4-5a·라이브 event2 0·복원 머리 보존) & 진짜 클리어는 포착(K3·라이브 loop). 계약의 baseline-on-first-obs보다 안전(over-pop 원천 차단).

### C6 — 정적 검사
- `dotnet build`: 오류 0 · 경고 13 전부 선재(NU1903×10 SQLitePCLRaw advisory·CS8604 B2cFacilityService·xUnit2013×2). **변경 파일 신규 경고 0**. 포맷터 backend 미구성(not-configured).

### Verification Scenarios
- Web/UI 기본상태: TraceLogPage(/trace) 렌더·9컬럼·6이벤트 레전드 정합·SignalR "실시간 연결됨". 콘솔(세션격리 all:false) **0 errors / 0 warnings / 0 pageerror**(all:true 버퍼의 5290/5190·2026-07-28 에러는 선행 브라우저 세션 stale — 내 5173→5215 세션 무관).
- Web/UI event-2 timing: event2 "TgtFloor 디큐" 1행(chuteNo30·floor1)·분류 시작 타이밍 렌더. 스크린샷 evidence 저장(S-TWO-FLOOR-WRITE-ON-CLEAR_tracelog_event2.png).
- Backend happy/empty-queue/AAB: 상기 라이브 loop + K3 + C4로 전수 실증.
- 두 선재 flake(E2EGroupN.N1·RtuTransportTests.VT4·둘 다 무접촉 코드) 격리 각 3/3 GREEN·내 full run 미발현 → 선재 환경 flake로 귀속(회귀 아님).

### Findings
- **MINOR 1(비차단·todo.md 등재)**: `SorterFloorReturnService.FireStallWarning` Serilog WARN 문자열이 구 스톨 조건("유휴·TgtFloor=0·머리 불변")을 기술 — 재조정된 abandonment 발화 상태에선 정렬(CurFloor==머리)·WCS write-on-clear로 TgtFloor=머리(비영)라 "TgtFloor=0"이 실제와 모순, "정렬" 절 누락. 구조화 operation_log detail은 실제 snap.TgtFloor 정확 기록·관측 전용(오분류/pop/쓰기 영향 0). fail-loud 정직성 차원 문구 1줄 정정 권고.
- **정보성 nit**: Generator baseline 산술 라벨 "487+6"은 실제 488+5(무해 — 순증 493/0·삭제 0).

### 판정: **APPROVED** — C1–C6 전 조건 + 전 Verification Scenario를 fresh evidence로 통과. 안전 위험(오층·early/double/under pop) 전무 실증. MINOR 1건 todo 등재(비차단). 커밋 전 오케스트레이터 코드리뷰 패스(Step 4.5) 권고.

## Step 4.5 코드리뷰 결과 (2026-07-30) — Ready to merge: Yes (Critical 0 · Important 0 · Minor 3)
BLOCKING/Critical 0 → 병합 무차단. 강점 확인(리뷰어, 코드수준): arm-on-first-0 에지 상태머신이 콜드스타트 잔류(2→0 무-pop)·OFFLINE 재동기(팬텀 에지 0)·빈큐 park·같은층·다중에지 전 경로에서 정확(에지당 1 pop·FIFO·K3 홀드 불변). pop→top-of-tick snap 읽기 후 same-tick 새 head re-peek write(torn read 없음). 절대규칙 #1(EnqueueSetTgtFloorAsync만)·#2(TgtFloor==0에서만 write)·#3(0 write 경로 0)·#7·#8 코드수준 보존. push .Ready/.Reason byte-identical(소비자 DestinationStatusPusher:439·Service:277/304 확인). DetectStall abandonment 재도출 정합(정렬 게이트·observe-only·once-per-episode). OFFLINE 갭 중 클리어 미-pop은 안전방향(under-pop→stall fail-loud, over-pop 아님)·C4_5b로 테스트·DetectStall 백스톱.
### Minor (다음 sprint — 비차단)
- [CR-MINOR-1] = 위 MINOR 1(FireStallWarning WARN 문구 구조건 기술) — todo 등재됨.
- [CR-MINOR-2] E2EGroupK_TwoFloorReturnTests.cs:159(및 :154 WriteLine) 주석이 구식(pop=분류사이클 Ready 1→0→1)을 기술 — 테스트 본문은 clear-edge pop 검증으로 정확·GREEN. 주석만 정정.
- [CR-MINOR-3] write-latency 창 중복 SetTgtFloor(head) 재기입 — bounded·PlcGateway fresh-read D6!=0 dedup으로 idempotent. 결함 아님(무액션).
