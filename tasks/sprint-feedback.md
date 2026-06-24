# Sprint Feedback — S-RCS-IF-REDESIGN Phase 2 (IF-08 아웃바운드 목적지 상태 푸시) — APPROVED

## Phase 3 Evaluate 결과 (Evaluator fresh evidence, 미커밋 working tree `feat/rcs-if-redesign-p2`, 2026-06-24)

**최종 판정: APPROVED** — 계약 Verification Scenarios·Completion Conditions·Evaluation Criteria·사용자 확정 6건 전부 fresh 직접 실행 증거로 충족. teardown hang 재발 0(Phase 1 fix 회귀 0). 동시 전이 멱등은 committed 테스트 + 독립 Evaluator 프로브(32 동시관찰)로 이중 입증.

### Fresh evidence (직접 실행 — 주장 신뢰 아님)
- **build**: `dotnet build Wcs.sln -c Debug` → **경고 0개 / 오류 0개, exit 0**.
- **full test**: `dotnet test Wcs.sln --blame-hang-timeout 180s` → **통과! 실패:0 통과:76 건너뜀:0 전체:76, TEST_EXIT=0**. Blame 수집기 "시퀀스 파일이 생성되지 않습니다"(= teardown 전용 행 0). hangdump `.dmp`/`Sequence_*.xml` 생성 0건. **Phase 1 teardown-fix 회귀 0 — 신규 HttpClient/타이머/HostedService가 graceful shutdown으로 깨끗이 종료.** (기존 70 + 신규 푸시 6 = 76.)
- **flaky 0(타이밍/동시성 표적 ≥5회)**: `RcsPushTests` 필터 **5/5회 연속 6/6 GREEN·exit 0**. 동시성 핵심 `PUSH4_ConcurrentTransition` 단독 **5/5회 연속 1/1 GREEN·exit 0**. 비결정성 0.
- **동시 전이 멱등 독립 프로브(핵심 보강)**: PUSH4는 "전이 1회 + 무전이 16통지(중복억제 경로)"라, 같은 true→false 전이를 32스레드가 barrier로 **동시에 관찰**해 Gate 클레임 경합을 정면으로 치는 임시 프로브를 추가 실행 → **5/5회 정확히 1건(부트1+전이1=2, 중복 0·누락 0)**. P3 "단일 idle 경로라 동시 경합 미발생" 함정 차단. 프로브는 검증 후 삭제(working tree 복구 확인).

### 무변경 가드 (git diff develop — 직접 실행)
- `src/Wcs.PlcGateway/PlcGateway.cs`·`HandshakeOrchestrator.cs`·`src/Wcs.Sim3ds`·`src/Wcs.Core` → **diff stat 빈 출력(0줄)**. 레지스터맵/핸드셰이크/Sim3ds/Core 판정 불변. **소터 ready 전이용 추가 이벤트 노출 0** — 게이트웨이는 기존 `bundle.Latest` 주기 관찰만(Scope D (a)). 추가 이벤트 노출이 없으므로 정당성 검토 불요.
- `src/Wcs.Api/RcsController.cs`(인바운드 IF-05/09/10) → **0줄**. 인바운드 회귀 0.
- `src/Wcs.Api/DestinationStatusService.cs` → **0줄**(확정1 — 소터 full/paused 이연 보존). `ComputeSorter`는 여전히 `Full:false, Paused:false` 하드코딩, ready=`online && CurFloor==운영층 && Ready==1`만. Scope 확장 안 함 확인.
- 전체 변경 표면 = `Wcs.Api`만(ChuteCapacityService·Program.cs·WcsOptions·appsettings 4 modified + DestinationStatusPusher·RcsPushClient 2 신규) + 테스트 1 신규(RcsPushTests). DB 스키마/마이그레이션/Data 변경 0(인메모리 전이 추적). 보호 zone 0.

### 사용자 확정 6건 — 코드로 직접 대조 (전부 충족)
1. **소터 full/paused 이연(확정1=a) PASS**: `DestinationStatusService.cs` diff 0 + `ComputeSorter` Full/Paused 하드코딩 false 유지. Phase 2가 소터 산출 안 건드림.
2. **재시도 설정화·기본 3회 지수백오프(확정2) PASS**: `RcsPushOptions.RetryCount=3 / RetryBaseDelayMs=1000 / RetryMaxDelayMs=4000`(record init 기본값) + appsettings 명시. `ComputeBackoffDelay`=`base << (attempt-1)` 상한 클램프 = 1s/2s/4s. 하드코딩 0·고정 sleep 0(전부 `_opt.*`).
3. **재시도 소진 후 최신값 유지·복구 재푸시(확정3) PASS**: `DestState.Computed`/`Acked` 분리. 푸시 성공 시만 `Acked=target`(PumpAsync:307), 실패 시 Acked 불변(미알림 유지). `Computed!=Acked` 잔존 → 다음 Observe/관찰이 재푸시. `PushAsync` 실패 시 false 반환(실패를 성공 간주 안 함). VS-PUSH-5가 거부→재시도 소진(baseline 유지)→복구→재푸시 도달 실증.
4. **BaseUrl 미설정 → 경고+비활성(확정4) PASS**: `StartAsync`가 `!IsEnabled` 시 LogWarning 후 return(전이 추적·관찰 타이머 미기동). `PushAsync`도 방어적 false. VS-PUSH-8: BaseUrl=null → 수신 0 + IF-05 인바운드 200(인바운드 정상). 크래시 0.
5. **기동 초기 스냅샷 후 전이만(확정5) PASS**: `StartAsync`가 `_states.Values` 전부 Compute→Computed 설정→PumpAsync(Acked=null이라 무조건 1회). VS-PUSH-6/7: 부트스트랩 7목적지(슈트5+PAUSED1+소터1) 각 정확히 1건 수신 후 stableCount:6 무변화(폭주 0).
6. **하트비트 OUT(확정6) PASS**: 주기 keep-alive 전송 코드 0. 푸시는 부트스트랩 + 전이 트리거(Observe→Pump)에서만. SorterObserveIntervalMs 타이머는 "관찰"이지 "전송"이 아님(전이 없으면 PumpAsync가 Computed==Acked로 즉시 return).

### 계약 Verification Scenarios — committed 테스트로 PASS
- **페이로드 정합 PASS**: VS-PUSH-7 — FakeRcsServer가 수신한 raw JSON을 `EnumerateObject`로 검사: 키 정확히 3개(`chuteNo`/`ready`/`timeStamp`), `full`/`paused`/`online` 키 부재 단언. camelCase 와이어(STJ 기본). timeStamp 포맷 `^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$`.
- **슈트 전이당 1건 PASS**: VS-PUSH-1 — 슈트4 true→false(OnReserved 만재)→false→true(OnCleared) 각 1건, 부트1+전이2=총 3건. WaitUntilExact stableCount:5로 중복 0 가드.
- **소터 전이당 1건 + 폴마다 폭주 0 PASS**: VS-PUSH-2/3 — 소터 false→true(CurFloor=2·Ready=1)→true→false(Ready 1→0) 각 1건. **무변화 폴 다수에도 stableCount:6 유지**(폭주 0·핵심). 부트1+전이2=총 3건.
- **무변화 0건 PASS**: VS-PUSH-3(통합) — 부트스트랩 후 stableCount:6 무변화. 전이 없는 폴/이벤트에서 푸시 0.
- **동일 전이 중복 억제 PASS**: VS-PUSH-4 — 만재 후 추가 예약(ready 여전히 false) 동시 16통지 → 전이 1건만(Acked==Computed면 PumpAsync 즉시 return). stableCount:8.
- **동시 전이 멱등 PASS(핵심)**: VS-PUSH-4 + 독립 프로브(32 barrier 동시관찰) 둘 다 전이당 정확히 1건. `Gate` 락 안에서 `(Acked!=Computed) && !PushInFlight` 원자 클레임 → 한 스레드만 성공, 나머지 즉시 return(누락은 in-flight 완료 후 재평가로 흡수). 비원자 check-then-act 0(P3 교훈 반영 코드 직접 확인 DestinationStatusPusher.cs:259-326).
- **RCS 미도달 재시도 PASS**: VS-PUSH-5 — 503 거부 → 재시도 소진(baseline 유지·미알림) → StopRejecting → 재평가 트리거 → 최신 ready=false 1건 도달.

### Evaluation Criteria (★★★ 1·2 / ★★ 3·4)
1. **API Design Quality(★★★) PASS**: 페이로드 `{chuteNo,ready,timeStamp}` 정확(개별 플래그 미포함). 엔드포인트=`{BaseUrl}{Path}` 설정 조합(`CombineUrl` 슬래시 정규화, 하드코딩 0). `IHttpClientFactory.CreateClient("RcsPush")` 경유 — `new HttpClient(` grep=주석 3건뿐(직접 생성 0). 2xx→true, 비2xx→재시도 경로.
2. **Architecture Originality(★★★) PASS**: ready=Phase 1 `Compute` 재사용(Pusher 내 Compute 호출 2지점=부트스트랩·Observe, 새 판정 0). 변화원 둘(슈트 콜백·소터 관찰)이 공통 `Observe→PumpAsync` 수렴. 전이 추적=chuteNo별 `Computed`/`Acked` 단일 상태(주기 전송 아님이 구조로 보장). 게이트웨이 본문 무변경(diff 0).
3. **Craft(★★) PASS**: 재시도 전부 설정값(고정 sleep 0·하드코딩 0). 푸시 예외 삼킴 0(PushAsync는 false 수렴 + LogWarning/LogError, PumpAsync는 OCE/Exception 분리 catch). 동시 전이당 1회 멱등(원자 클레임). 빌드 경고 0·teardown 클린(멱등 StopAsync `Interlocked _stopped` + CTS 정리 + 관찰 task await). busy-loop 방지: 실패 시(ok==false) PumpAsync return하고 다음 Observe가 재구동(백오프 없는 바쁜 재시도 회피).
4. **Functionality(★★) PASS**: 슈트/소터 전이당 1건(가짜 RCS 수신 단언), 폴마다 폭주 0, RCS 미도달 재시도 동작, Phase 1 인바운드 회귀 0(76/76 중 70 기존 GREEN).

### Fail-Loud PASS
재시도 소진=`LogError`("재시도 소진 — RCS 미도달… 미알림 유지"). 비2xx/전송예외=`LogWarning`(시도횟수·status 포함). 관찰 루프 예외=`LogError` 후 다음 주기 재시도(이벤트 영구 손실 방지·P3 핸들러 교훈 동형). 미관찰 예외 0(PumpAsync `_ = PumpAsync(st)` fire지만 내부 전 구간 try/catch로 미관찰 0).

## Minor (비차단 — 다음 sprint Generator 읽음, todo.md 등재 권고)
- **[MINOR / 설계 명확화] 슈트 실패 푸시의 자율 복구 없음**: 확정3은 "다음 전이 또는 복구 감지 시 재푸시"를 허용하고 구현은 **이벤트 구동 복구**(다음 슈트 상태변화가 재구동). 소터는 관찰 타이머가 매 주기 재구동하나, **슈트는 상태변화 이벤트가 없으면 실패 푸시가 다음 이벤트까지 stale**(자율 RCS 복구 폴러 없음). 확정3 경계 내(위반 아님)·상태 오염 0(Acked 불변)이나, RCS가 장시간 다운 후 복구돼도 무변화 슈트는 다음 IF-05/10까지 미동기. 운영상 RCS 복구 시 부트스트랩 재기동 또는 주기 복구 스윕이 필요하면 후속 스프린트 고려. (VS-PUSH-5가 명시적 후속 이벤트로 복구를 유도하는 것이 이 특성의 방증.)
- **[MINOR / 정합성] `Path` 기본값 리터럴**: `RcsPushOptions.Path` 기본 `"/api/v1/destination-status"`는 record init 기본값(설정 미지정 시 사용). 하드코딩이 아니라 외부화된 설정의 기본값이므로 절대규칙 #7 위반 아님(BaseUrl과 동일 설정 섹션, appsettings에 명시). 단 스펙 경로 변경 시 appsettings로 오버라이드 가능함을 운영 문서에 명기 권고.

## 독립 4-Tier 코드리뷰
APPROVED 후 Step 4.5 게이트는 orchestrator(team-lead)가 별도 수행 — 본 평가는 functional 차원(계약 Evaluation Dimensions=functional only, 단 동시성 표면 명시 강제). 동시성 영역(PumpAsync 원자 클레임·Acked/Computed 분리·StopAsync 멱등·관찰 task 수명)은 메타교훈(5회 반복: 기능 GREEN ≠ 결함 없음)상 **독립 코드리뷰에서 전이 추적 원자성·푸시 경합·재시도 상태 오염·타이머/HostedService disposal을 코드 직접 검사 필수**. 본 Evaluator는 32-동시 프로브로 멱등을 행동 입증했으나, 정적 구조 리뷰는 별도 게이트.

---

# Sprint Feedback — S-RCS-IF-REDESIGN Phase 1 (인바운드 + 구조 전환) — APPROVED (조건부)

## Phase 3 Evaluate 결과 (Evaluator fresh evidence, working tree feat/rcs-if-redesign 기준, 2026-06-24)

**최종 판정: APPROVED** — 계약 Verification Scenarios·Completion Conditions·Evaluation Criteria 전부 fresh 직접 실행 증거로 충족. 단 아래 **teardown hang은 BLOCKING-for-CI**로 명시 등재(스프린트 도입 아님·선재·계약 무변경 zone 근본원인이라 Phase 1 차단 사유는 아님 — todo.md/M5 추적 필수).

### 핵심 주의: full `dotnet test` teardown hang — 깨끗한 GREEN 아님 (그러나 선재 입증)
- **fresh 실측**: `dotnet test Wcs.sln --no-build` → **EXIT_CODE=1**, `활성 테스트 실행이 중단되었습니다. 이유: 테스트 호스트 프로세스 작동이 중단됨` + `통과! 실패:0 통과:70` 후 `테스트 실행이 중단되었습니다`. 즉 **단언 70/70 PASS이나 testhost가 teardown에서 행→run abort→exit 1**. team-lead가 관찰한 그 증상 그대로.
- `--blame-hang-timeout 120s`로도 EXIT=1 + hangdump 수집(`testhost_..._hangdump.dmp`), `모든 테스트 실행이 완료되었지만 시퀀스 파일 미생성`(= teardown 전용 행). crash 시점 표기 테스트 S8_Chute_Paused_Ng(원인 불명). → Generator의 "`--blame-hang-timeout 90s`로 결정적 통과(5/5 GREEN)" 주장은 **내 fresh 증거와 불일치**(여전히 exit 1·abort). 이 문구는 정정 필요.
- **선재 입증(결정적)**: 커밋 베이스라인 `develop @ 1501ccd`를 worktree로 띄워 동일 명령 실행 → **동일 EXIT=1 + 동일 teardown hang(69 통과 후 abort·90s hangdump)**. 즉 Phase 1이 도입한 것이 아니라 P3 베이스라인의 환경적 선재 이슈(브랜치는 +1 테스트로 69→70만 차이).
- **격리 진단**: 클래스별 단독·소그룹은 전부 clean exit 0 — ApiIntegrationTests(24), DepositDecider+gateway(19), socket scenario 6클래스(11), S8(2) 전부 exit 0·hangdump 0. **full-suite 조합 teardown에서만** 발생(다중 IHost/PlcPollingService 폴루프 정리 + xUnit collection teardown 순서 상호작용). 근본원인 = `Wcs.PlcGateway` 폴 루프/IHost disposal = **계약 "무변경 유지" zone** → Phase 1이 고칠 수 없음(고치면 계약 위반).
- **판정 논리**: team-lead 지침 "teardown 크래시=FAIL 사유 명시"를 준수해 **명시·정량 기록**한다. 단 M4-P3 선례(Generator가 도입한 ObjectDisposedException)와 달리 여기선 **무변경 baseline에서 동일 재현 → 스프린트 결함 아님 + 계약상 수정 금지 zone**. 따라서 Phase 1 APPROVED를 차단하지 않되, **CI gate로는 BLOCKING**(M5 PlcGateway graceful shutdown에서 근본해소). todo.md 등재 필수.

### Completion Conditions (계약 8항 — fresh 실측)
1. **build**: `dotnet build Wcs.sln` exit 0, **경고 0 / 오류 0**.
2. **전체 테스트 GREEN(단언)**: 70/70 통과·실패 0(단, 위 teardown hang으로 run은 abort). 회귀 + 신규(IF-05 필터·IF-09·deposit-permission 부재) 포함.
3. **flaky 0(타이밍 민감 ≥5회)**: 실 Sim 소켓 표적(S1/S5/S6/S7/S2-4-9 + P2bSim4/5/6 = 11) 단독 필터 **5/5회 연속 clean GREEN, exit 0, hangdump 0**. 비결정성 0 확정.
4. **deposit-permission 잔존 0**: src grep → 설명 주석 3건뿐(DTO 타입·엔드포인트·핸들러 0). `DepositPermissionRequest/Response` 타입 src 0(contract/log 문서에만). 호출 시 404/405(`If08_DepositPermission_Removed_Returns404Or405`).
5. **IF-05 reason 부재**: DTO `DestinationQueryResponse(string Result, int? ChuteNo)` — reason 필드 자체가 없음(직렬화 불가). VS-1/VS-2/If05_* 테스트가 OK→chuteNo, NG→null 단언.
6. **IF-09 2층 정렬(실 Fake 통합)**: `If09_3dSorter...`가 사전 CurFloor=1·TgtFloor=0 → `TgtFloor==2` 기입 관찰(WaitForRegister). 이미정렬(CurFloor=2)·슈트전용 → TgtFloor 0 유지(쓰기 0). 핑퐁 차단(TgtFloor≠0 추가쓰기 0)은 S9 `Assert.False(dec2.WriteTgtFloor)`. IF09_ARRIVAL DB행은 ScenarioTests S1 `PieceEvents.AnyAsync(EventType==IF09_ARRIVAL)`.
7. **Core 순수성 불변 + 게이트웨이/Sim 무변경**: `git diff develop` → PlcGateway.cs=0·HandshakeOrchestrator.cs=0·Sim3ds/=0줄. RegisterMap/PlcSnapshot.FromRegisters 본문 0(diff는 DenyReason/DepositDecision 시그니처만 — 계약 허용). Wcs.Core.csproj Reference/Package 0, DepositDecider static·무필드·DateTime/Random/IO 0.
8. **하드코딩 floor-2 grep 0**: 설정 1지점(`WcsOptions.OperationalFloor`=2 + appsettings `"OperationalFloor":2`). DepositDecider/DestinationStatusService/Controller 전부 파라미터·DI 경유. 로직 리터럴 `2` 0.
   - **마이그레이션 pending 0(양 provider)**: `has-pending-model-changes` Sqlite·SqlServer 둘 다 "No changes". IF09_ARRIVAL 마이그레이션은 enum→string·CHECK 없음이라 Up/Down 의도적 비어있음(이력·스냅샷 동기화 목적).
   - **HTML 5건 working tree 포함**: git status → 4 modified + `wcs_rcs_interface.html` 신규(untracked) 확인.

### Evaluation Criteria (★★★ 1·2 / ★★ 3·4)
1. **API Design Quality(★★★) PASS**: Controller 이관(RcsController `[Route("api/v1")]`)·엔드포인트 이중생성 0(Program.cs `MapControllers()`만, 인라인 MapPost 0). IF-05 `{result,chuteNo}`(NG=null). IF-09 camelCase 와이어 정합. deposit-permission DTO/엔드포인트 0. 검증실패만 400, 가부 200+result.
2. **Architecture Originality(★★★) PASS**: DestinationStatusService.Compute가 슈트(ChuteCapacity hold)·소터(스냅샷+DepositDecider) 단일 산출 경로 → IF-05 NG 필터 소비 + Phase 2 푸시 재사용 확장점(개별 full/paused 외부 미노출). Core 순수성 유지. 2층 설정 1지점.
3. **Craft(★★) PASS**: IF-09 TgtFloor 쓰기 = 번들 전용 큐 `EnqueueSetTgtFloorAsync`(절대규칙 #1, 핸들러 직접 Modbus 0)·조건 `TgtFloor==0 && (CurFloor≠운영층||Ready==0)`(#2)·WCS 클리어 0(#3). fire-and-forget `.ContinueWith` IsFaulted 로깅. 입력검증(pId 1~30000·barcode·qty·chuteNo). 빌드 경고 0.
4. **Functionality(★★) PASS**: IF-05 FULL/PAUSED→NG·BUSY/NORMAL→OK 실동작(If05_* 테스트). IF-09 도착 DB기록+3D 2층정렬·슈트 무정렬. IF-10/IF-11 회귀 0(S1 sorter_command COMPLETED·VS-5 멱등·VS-6 트리거/대조). HTML 5건 커밋 대상.

### Phase 2 스코프 준수 PASS
아웃바운드 푸시 클라이언트(`POST /destination-status`) **미구현 확인**: src 전역 grep → `destination-status`는 DestinationStatusService.cs 주석 1건(Phase 2 확장점 설명)뿐. 실제 HttpClient POST·IRcsClient·푸시 호출 0. `full`/`ready` 산출 함수만 존재 — 스코프 위반 없음.

### 회귀·전환 추적 PASS
sprint-log.md "회귀·전환 명세"에 구 단언 삭제/유지/재타겟 근거 명시됨(VS-3/4 IF-08 삭제→404테스트+IF-09재타겟, P2a hold→IF-05 필터 재타겟, S1/S5/S6 폴링단계 제거, S8 FULL/PAUSED 재타겟, S2/3/4/9+Decider 2층기준 재작성). 실제 코드와 대조 일치(WRONG_FLOOR/.Allowed 잔존 = 전환설명 주석뿐, 로직 0).

## Minor (비차단 — 다음 sprint Generator 읽음)
- **[BLOCKING-for-CI / M5] full `dotnet test` teardown hang**: 위 상술. 선재(develop 동일)·계약 무변경 zone(PlcGateway 폴루프/IHost disposal) 근본원인. M5 graceful shutdown(SorterRegistryFactory IAsyncDisposable + 번들 DisposeAsync, P2b 교훈)에서 해소. 현 운영: CI는 `--blame-hang-timeout`로 단언 결과는 얻되 exit 1은 별도 처리 필요. **todo.md 등재 필수.**
- **[MINOR] sprint-log 문구 정정 권고**: "`--blame-hang-timeout 90s`로 결정적 통과(5/5 GREEN)"는 내 fresh 실측(여전히 exit 1·abort·hangdump)과 불일치. "단언 70/70 PASS이나 teardown hang으로 run abort(선재)"로 정정이 정확.

## 독립 4-Tier 코드리뷰
APPROVED 후 Step 4.5 게이트는 orchestrator(team-lead)가 별도 수행 — 본 평가는 functional 차원(계약 Evaluation Dimensions=functional only). 동시성 표면은 기존 게이트웨이 무변경이라 신규 위험 낮음(계약 명시). 단 메타교훈(인메모리/단일프로세스 GREEN ≠ 결함 없음)상 IF-09 fire-and-forget·ContinueWith 콜백 경로는 독립 리뷰 권장.

---

# Sprint Feedback — S-M4-P3 (시나리오 S1~S9 + alarm/sorter_command 영속화) — APPROVED

## Phase 3 Evaluate 결과 (evaluator2 fresh evidence, working tree 기준)

**최종 판정: APPROVE** — 7항 전부 fresh 직접 실행, 주장 아닌 실측 수치.

1. **build**: exit 0, 경고 0 / 오류 0.
2. **full test**: 총 69 / 통과 69 / 실패 0, exit 0. ObjectDisposedException 0줄(grep -c=0). 회귀 59 + 신규 S1~S9 10 = 69.
3. **타이밍 민감 ≥5회 연속**: S6RFlagTimeout + S7Offline(==2 보강·no-flood 포함) 5회 연속 전부 `실패:0 통과:2`. flaky 0.
4. **MAJOR 수정 코드 확인**:
   - MAJOR-1 PlcGateway.cs:308 `Interlocked.Exchange(_online,0)==1` 승자만 OnOfflineTransition 발화 / :241 ONLINE 복구 시 `_online=1` 리셋. 비원자 check-then-act 제거.
   - MAJOR-2 Program.cs:556~575 OFFLINE 핸들러 scope+DI+Append 단일 try/catch(ObjectDisposedException + Exception). IF-11 콜백도 scope 생성 catch(ObjectDisposedException). 폴 스레드 격리.
   - MAJOR-3 DbRepositories.cs:716~746 + Program.cs:367~374 Offline/CFlagTimeout 명시 분기, alarm code OFFLINE/CFLAG_TIMEOUT로 사유 구분.
5. **S7 고정sleep 0**: ScenarioTests.cs S7 영역(964~1029) bare Task.Delay 0건. no-flood = `WaitUntilExactAsync(expected, stableCount:5)` — flood로 어긋나면 실패하는 강한 회귀가드.
6. **무변경 가드**: `git diff develop -- src/Wcs.Core src/Wcs.PlcGateway/HandshakeOrchestrator.cs src/Wcs.Sim3ds` = 빈 diff. PlcGateway.cs(+30/-4)는 계약 허용분만(이벤트 노출·Interlocked 원자화·로거 disposed 방어; failures++/PublishOffline은 try 밖 → Fail-Loud 보존).
7. **단방향 경계**: src/Wcs.PlcGateway에 Wcs.Data/EntityFrameworkCore/DbContext 참조 0. csproj ProjectReference=Wcs.Core 단독.

## 시나리오별 PASS (전부)
S1 정상→sorter_command COMPLETED+CSeq==RSeq / S2 WRONG_FLOOR+D6 1건 / S3 BUSY 선기입·분류시작 클리어 / **S4 TgtFloor≠0 구간 D6 쓰기 ==1·추가 0건(핵심)** / S5 R_SEQ_MISMATCH alarm+MISMATCH / S6 RFLAG_TIMEOUT alarm+TIMEOUT 1행(재시도 0) / S7 OFFLINE 3-phase(==2)+no-flood / S8 FULL·PAUSED / **S9 단일 TgtFloor 선점·타층 D6 0·클리어 후 양보**.

## Orchestrator 최종 코드리뷰 (Step 4.5 fix 재확인)
MAJOR-1/2/3 수정 정확(diff 직접 확인). Fail-Loud 보존(OFFLINE 판정이 로거 try 밖). 갭 결선 단방향 경계 유지. EF sink 단일 트랜잭션·UTC·rollback+throw.
- **MINOR-1(M5 이연, 비차단)**: IF-10 ContinueWith에서 GetRequiredService 2줄이 inner try 밖 → DI 해석이 던지면 말미 `cellSelector.ReleaseCell` 스킵→셀 누수 가능. 단 호스트 종료/DI 오설정 한정(정상 운영 0). M5 정리 후보.

## 독립 코드리뷰 이력
1차 BLOCK(MAJOR 3건) → iter2 수정(Interlocked 원자화·핸들러 격리·명시 분기·S7 3-phase·고정sleep→WaitUntilExactAsync) → 재검증 APPROVE. 상세 [[feedback-archive]] S-M4-P3 [CODE-REVIEW] 라인.

---

## Step 4.5 독립 코드리뷰 (orchestrator, Opus) — APPROVE · MINOR 2건 (차기/M5 이연)
- [MINOR] IF-09 fire-and-forget ContinueWith 로깅 비대칭 (RcsController IF-09 콜백) — IF-10은 SafeLog(try/catch)로 감싸나 IF-09 미적용. teardown 중 로거 throw 시 미관찰 예외 가능하나 WcsTeardownGuard(InvalidOperationException 흡수)로 완화. IF-10과 동일 try/catch 래핑 권고.
- [MINOR] IAgvFloorResolver dead registration — 2층 고정 정렬로 `.Resolve()` 호출 0. 계약상 "기록용 잔존 허용"이나 정리 권고(이연).
- teardown hang 독립 귀속 결론: **선재**(신규 수명주기 컴포넌트 0, Phase1은 완화). BLOCKING/MAJOR 0 → 커밋 진행.

---

## TEARDOWN FIX 검증 (Evaluator fresh evidence, 미커밋 working tree, 2026-06-24)

**판정: APPROVED.** Phase 1에서 BLOCKING-for-CI로 등재했던 full-suite teardown hang(testhost abort·exit 1)이 근본 해소됨. 6회 전체 suite(1+5) 전부 EXIT=0·70/70·abort 0건을 직접 실행으로 확인. 단언 약화·hang 은폐 없음. 무변경 가드 유지.

### 검증 대상 (미커밋 diff)
- `src/Wcs.Api/SorterGatewayRegistry.cs`: `SorterBundleHandle`에 `PlcWriteQueue?` 주입(기본 null) + `StopPollingAsync()`가 `_writeQueue?.Writer.TryComplete()` 선행 후 `_polling.StopAsync()`.
- `src/Wcs.Api/Program.cs`: `SorterRegistryFactory.StartAsync` 1줄 — 번들 생성 시 동일 `writeQueue` 인스턴스 전달(line 229).
- 테스트 3파일: `ApiIntegrationTests.cs`(FakeModbusWAF + P2bSimHandshakeTests dispose), `PlcGatewayIntegrationTests.cs`, `ScenarioTests.cs`(S234_9 + S8ApplicationFactory) — 각 종료 경로에 `Writer.TryComplete()`. `S8ApplicationFactory.Dispose(bool)`는 동기 `base.Dispose(disposing)` 제거(IHost 종료를 async DisposeAsync에 일임 — sync-over-async 데드락 회피).
- `tasks/sprint-log.md` 갱신, `tasks/testrun1~4.log` 삭제.

### 항목별 PASS/FAIL

1. **[PASS] 전체 suite EXIT=0 + abort 0 + 70/70**: `dotnet test Wcs.sln --no-build` → `통과! 실패:0 통과:70 건너뜀:0 전체:70`, `TEST_EXIT=0`. "테스트 호스트 프로세스 작동이 중단됨"/abort/exit 1 미발생.
2. **[PASS] ≥5회 연속 전체 suite 클린**: 5회 연속 전부 EXIT=0·70/70·abort 0·hangdump 0. (1회 선행 포함 총 6회 전부 클린. 소요 5~6s — 수정 전 행/abort 대비 결정적 종료.)
   ```
   RUN 1 EXIT=0  통과:70 실패:0  (5s)
   RUN 2 EXIT=0  통과:70 실패:0  (5s)
   RUN 3 EXIT=0  통과:70 실패:0  (5s)
   RUN 4 EXIT=0  통과:70 실패:0  (5s)
   RUN 5 EXIT=0  통과:70 실패:0  (5s)
   선행 단독 RUN: EXIT=0 통과:70 (6s)
   ```
3. **[PASS] 회귀 0**: 70 단언 전부 PASS 유지. Phase 1 시나리오·IF-05/09/10·핸드셰이크·alarm·OFFLINE 전이 단언 변경 0(테스트 diff는 dispose 경로만, Assert 라인 무변경 직접 확인). 타이밍 민감 표적(Scenario+PlcGateway+P2bSimHandshake, 16건) 단독 3회 연속 전부 EXIT=0·16/16.
4. **[PASS] 정상 동작 무영향**: `Writer.TryComplete()`는 `StopPollingAsync()`(=`SorterRegistryFactory.StopAsync`, IHostedService 종료 경로)에서만 호출 — 정상 운영 쓰기 경로 영향 0. 코드로 확인: (a) 종료-한정 호출처 단 1곳(Program.cs:293). (b) unbounded 채널 `TryComplete()`는 **이미 큐된 in-flight 쓰기를 드레인한 뒤** `await foreach`가 정상 종료 — 대기 쓰기 유실 없음. (c) `_writeQueue?`null 경로(생성자 기본 null)는 기존 `_polling.StopAsync()`만 수행(구동작 보존). (d) `Program.cs`에서 `PlcPollingService`와 `SorterBundleHandle`에 동일 `writeQueue` 인스턴스 전달 확인(line 214→219→229) — 컨슈머가 읽는 채널과 complete 대상 채널 동일.
5. **[PASS] 무변경 가드 유지**: `git diff develop -- src/Wcs.PlcGateway/PlcGateway.cs src/Wcs.PlcGateway/HandshakeOrchestrator.cs src/Wcs.Sim3ds` = 빈 diff(3건 전부 0줄). 이 fix는 Wcs.Api(+테스트)에만 국한.
6. **[PASS] 테스트 변경 정당성**: 4개 dispose 지점에 `Writer.TryComplete()` 추가(컨슈머 결정적 종료)뿐, 단언/타임아웃/시나리오 본문 무변경. `S8ApplicationFactory.Dispose(bool)`의 동기 `base.Dispose` 제거는 leak 아님 — 사용처 `S8FullPausedTests.DisposeAsync()`가 `await _factory.DisposeAsync()`를 명시 호출(line 1150) → override async `DisposeAsync()`가 `base.DisposeAsync()` 유지(line 1110)로 IHost 비동기 정상 종료. hang 은폐(teardown 스킵) 아님 — 호스트는 정상 disposal.

### 빌드/오케스트레이터 보강 확인
- `dotnet build Wcs.sln -c Debug` → 경고 0 / 오류 0.
- 종료 후 orphan testhost/vstest 프로세스 0건(teardown 실제 완료 입증 — exit 0가 leak 은폐가 아님).
- 6회 실행 중 신규 hangdump `.dmp` 생성 0건(13:00 이후 mtime dmp 없음). TestResults에 잔존하는 dmp 5건은 전부 08:52 이전 — 수정 전 행 재현 시 생성된 stale 산출물(이번 diff 외·untracked). 정리는 team-lead 재량(검증 결론에 무영향).

### 비고
- sprint-log.md는 teardown-fix 전체 호(WcsTeardownGuard·RcsController ContinueWith 안전화·FakeSerialPort·TestAssemblyInit 포함)를 서술하나, 그 컴포넌트들은 **이미 07cc992(Phase 1)에 커밋**됨(검증 범위 밖, brief 명시). 이번 미커밋 검증 대상은 채널 완료(`Writer.TryComplete()`) 결선 — full-suite teardown 데드락의 마지막 결정적 종료 조각. 6회 전체 suite 클린은 그 결합 효과를 입증.
- 결론: Phase 1 BLOCKING-for-CI 항목 해소 확인. 커밋은 team-lead 진행.
