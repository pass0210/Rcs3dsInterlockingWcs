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
