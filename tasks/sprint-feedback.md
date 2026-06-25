# Sprint Feedback — S-FOLDER-ORG (src 폴더 구조 정리 / 순수 파일 이동) — APPROVED

## Phase 3 Evaluate 결과 (Evaluator fresh evidence, working tree `refactor/src-folder-structure`, 2026-06-25)

**최종 판정: APPROVED** — 계약 Completion Conditions #1~#7·Evaluation Criteria ①~⑤·Verification Scenarios(Backend/API 슬롯 a/b/c + 구조정리 1~8) 전부 fresh 직접 실행 증거로 충족. 순수 이동(rename R100·내용 diff 0)·동작 보존(build 0/0·test 99/99·exit 0·teardown 회귀 0)·네임스페이스 불변·무변경 프로젝트 가드 전부 통과. 회귀 0.

### Ground-truth 확인 (stale 재핸드오프 아님)
- `git rev-parse HEAD` = `1bb0a62`, 브랜치 `refactor/src-folder-structure`. 본 스프린트 이동 15파일은 **staged(미커밋)** 상태 — 이미 커밋된 스프린트가 아님 → 정당 활성 핸드오프.
- `sprint-log.md` 최상단 `## IMPLEMENTATION COMPLETE (S-FOLDER-ORG)` 마커 존재 확인.
- working tree 변경: src 이동 15파일(staged R) + `tasks/sprint-contract.md`·`tasks/sprint-log.md`(하네스 산출물) + `.claude/`(untracked, 선재). src 코드 표면 외 변경 없음.

### Fresh evidence (직접 실행 — generator 주장 신뢰 아님)
- **clean build**: `dotnet build Wcs.sln --no-incremental` → **경고 0개 / 오류 0개**. 8개 프로젝트 전부 빌드(Migrations 2종·Wcs.Tests 포함). csproj 미편집으로 SDK 글로빙이 새 폴더 `**/*.cs` 자동 포착 입증.
- **full test**: `dotnet test Wcs.sln --blame-hang-timeout 120s` → **통과! 실패:0 통과:99 건너뜀:0 전체:99, EXIT CODE 0**. Blame 수집기 "모든 테스트 실행이 완료되었지만, 시퀀스 파일이 생성되지 않습니다"(teardown hang/dump 0 — 채널 경쟁 회귀 lesson 미재발). baseline 99와 동일 = 회귀 0.
- **EF 디자인타임 강검증**: `dotnet ef migrations list --project src/Wcs.Migrations.Sqlite --no-build` → DbContext 정상 해석·마이그레이션 3건 나열(Initial·P2a·P1_If09Arrival), exit 0. 이동이 디자인타임 발견에 무영향.

### Completion Conditions — 항목별 PASS/FAIL
- **[PASS] #1 build 경고0/오류0**: clean build 경고 0개/오류 0개 (위 fresh evidence).
- **[PASS] #2 test 전체 GREEN·count=99·exit 0·hang 0**: 99/99 GREEN, exit 0, Blame 시퀀스 파일 0(teardown 채널 경쟁 회귀 0).
- **[PASS] #3 rename만(신규/삭제 단독 없음)**: `git status --find-renames --short` = src 15파일 전부 `R `(rename), `??`/`D` 단독 항목 0. porcelain raw에 `RM`(rename+modify) 없음·`git ls-files --others -- src/` 빈 출력(untracked src 0).
- **[PASS] #4 내용 diff 0**: `git diff -M --cached --stat -- src/` = **"15 files changed, 0 insertions(+), 0 deletions(-)"**. `--numstat` 전 항목 `0 0`. `--summary` 전 항목 `rename ... (100%)` = R100 byte-identical. `+`/`-` 본문 라인 0.
- **[PASS] #5 네임스페이스 grep 불변**: Wcs.Api 11×`namespace Wcs.Api;`(Dtos·SorterGatewayRegistry·WcsTeardownGuard·WcsOptions·DbRepositories·Repositories·DestinationStatusPusher·ChuteCapacityService·DestinationStatusService·RcsPushClient·SorterCellQty) + 1×`namespace Wcs.Api.Controllers;`(RcsController) + Program/ProgramPartial 네임스페이스 없음(grep 미출현=정상). PlcGateway 6×`namespace Wcs.PlcGateway;`. 이동 파일은 폴더와 무관하게 평면 선언 유지(예: `Services/SorterCellQty.cs`도 `namespace Wcs.Api;`). 계약 기준값 정확 일치(self-check line 36 "12개"는 11개 오기 — Completion #5·핸드오프가 정확).
- **[PASS] #6 EF 디자인타임 무영향**: Migrations 2종 컴파일 성공(clean build 포함) + `dotnet ef migrations list` 강검증 통과.
- **[PASS] #7 무변경 프로젝트 git status 0**: `git status --find-renames --short -- src/Wcs.Core src/Wcs.Data src/Wcs.Sim3ds src/Wcs.Migrations.Sqlite src/Wcs.Migrations.SqlServer tests/` = 빈 출력. csproj/.sln/appsettings 글로브 = 빈 출력(편집 0).

### Evaluation Criteria — 항목별
- **[PASS] ① 동작 보존 ★★★**: build 0/0·test 99/99·exit 0·teardown 회귀 0·count baseline 동일.
- **[PASS] ② 순수 이동 입증 ★★★**: rename R100 15건·내용 diff 0·네임스페이스 불변. add/delete 단독 0.
- **[PASS] ③ MVC 레이어 정확성 ★★**: working-tree 파일 배치 확인 — Wcs.Api Services/(5)·Repositories/(2)·Dtos/(1)·Infrastructure/(3)·Controllers/RcsController·Program/ProgramPartial 루트. PlcGateway Modbus/(4)·PlcGateway/HandshakeOrchestrator 루트. 구 평면 경로 잔존 파일 0.
- **[PASS] ④ csproj/솔루션 무결성 ★★**: csproj/.sln 편집 0으로 빌드/테스트 발견 정상(SDK 글로빙 검증).
- **[PASS] ⑤ 문서/참조 정합 ★(비차단)**: CLAUDE.md "솔루션 구조"는 프로젝트별 역할만 기술·내부 폴더 미언급 → 새 폴더와 모순 0. 정정 불요(계약 명시대로).

### Verification Scenarios (Backend/API + 구조정리)
- **[PASS] (a) 엔드포인트 목록**: IF-05/IF-09/IF-10 핸들러 `Controllers/RcsController.cs` 무변경(diff 0). 라우트/시그니처 불변.
- **[PASS] (b) happy path / DI 해석**: WebApplicationFactory 기반 통합 테스트 GREEN(99 전체에 포함) → 이동된 타입(DestinationStatusService·RcsPushClient·SorterGatewayRegistry 등) DI 해석·앱 부팅 정상.
- **[PASS] (c) 에러 케이스**: 기존 400/FULL·PAUSED NG/OFFLINE 경로 테스트 분포 0 변화(99/99 GREEN, 신규 에러 케이스 0).
- **[PASS] 1 빌드 무결성 / 2 테스트 보존 / 3 git rename 순수성 / 4 네임스페이스 불변 / 5 Api MVC 배치 / 6 PlcGateway Modbus 배치 / 7 EF 디자인타임 / 8 테스트 타입 발견**: 위 Completion 증거로 전부 충족(8=test 99 GREEN이 곧 Wcs.Api.* 타입 발견·참조·실행 입증).

### 결론
순수 파일 이동 스프린트의 핵심 명제 "위치만 바뀌고 동작/내용 0 변경"을 fresh tool output으로 입증. R100 rename 15건·numstat 0/0·build 0/0·test 99/99 exit 0·네임스페이스 grep 불변·무변경 프로젝트 가드 빈 출력. **BLOCKING/FAIL 0. APPROVED.**

---

# Sprint Feedback — S-소터push운영상태 (소터 IF-08 push ready를 운영상태로 좁힘) — APPROVED

## Phase 3 Evaluate 결과 (Evaluator fresh evidence, 미커밋 working tree `feat/sorter-push-operational`, 2026-06-25)

**최종 판정: APPROVED** — 계약 Verification Scenarios(VS-1~10)·Completion Conditions·Evaluation Criteria(35/25/20/15/5)·크로스-엔드포인트 정합(push·IF-05가 같은 Compute 산출을 분기 소비) 전부 fresh 직접 실행 증거로 충족. 무변경 가드 11개 경로 diff 0·IF-05 `r.Ready` 미소비 grep 0·teardown 회귀 0·baseline 귀속 명확(90→99=+9 신규).

### Ground-truth 확인 (stale 재핸드오프 아님)
- `git rev-parse HEAD` = `7b12098`(PR #15 머지 = 직전 스프린트). 본 스프린트 변경은 **working tree 미커밋/untracked**(이미 커밋된 스프린트 아님 → 정당 활성 핸드오프). 브랜치 `feat/sorter-push-operational`.
- `sprint-log.md` 최상단 `## IMPLEMENTATION COMPLETE (S-소터push운영상태)` 마커 존재 확인.
- 변경 파일(working tree): `docs/wcs_rcs_interface_kr.html`·`src/Wcs.Api/DestinationStatusService.cs`·`tests/Wcs.Tests/{ApiIntegrationTests,SorterCellFullnessTests}.cs`·신규 `tests/Wcs.Tests/SorterPushOperationalTests.cs`(+ tasks/ 하네스 산출물). 소스 변경 = `DestinationStatusService.cs` 1개뿐(계약 single-module 일치).

### Fresh evidence (직접 실행 — generator 주장 신뢰 아님)
- **build**: `dotnet build Wcs.sln` → **경고 0개 / 오류 0개, BUILD_EXIT=0**.
- **full test**: `dotnet test Wcs.sln --blame-hang-timeout 120s` → **통과! 실패:0 통과:99 건너뜀:0 전체:99, FULL_SUITE_EXIT=0**. Blame 수집기 "모든 테스트 실행이 완료되었지만, 시퀀스 파일이 생성되지 않습니다"(teardown hang/dump 0건). 중단/abort 라인 0.
- **baseline 귀속**: 본 스프린트 변경을 `git stash`로 제거 후 HEAD(`7b12098`) baseline 측정 → **통과:90 exit 0, teardown 클린**. 복원 후 99 = 90 baseline + 9 신규 SorterPushOperational. teardown은 baseline에서도 이미 클린 → 본 변경이 teardown 회귀 도입 0. (stash pop 클린 복원 확인.)
- **flaky 0(타이밍/동시성 표적 ≥5회 단독)**: `VS9|VS2|VS3|EC7|EC5|VS1` 필터(12건 — VS-9a barrier 16스레드 동시관찰·VS-9b no-flood·VS-2 전이·VS-3 offline·EC-7 만재 churn 무발화·EC-5 동시 churn·VS-1) **5/5회 연속 12/12 GREEN·exit 0·시퀀스 파일 0**. 비결정성 0.
  ```
  RUN 1: exit=0 통과:12 (4s)   RUN 2: exit=0 통과:12 (3s)   RUN 3: exit=0 통과:12 (4s)
  RUN 4: exit=0 통과:12 (3s)   RUN 5: exit=0 통과:12 (3s)
  ```
- **신규 suite 단독**: `SorterPushOperationalTests` 9/9 GREEN. `SorterCellFullnessTests`(반전 EC-1/3/5/7+HP-5 포함) 14/14 GREEN.

### Verification Scenarios — 항목별 PASS/FAIL (실 DB seed·게이트웨이 snapshot·가짜 RCS push payload ground-truth)
- **[PASS] VS-1 (소터 online·정렬·Ready=1 → push ready=true)**: `VS1_...` — `AlignSorterAsync`(SetReady/CurFloor=2/TgtFloor=0) → `Compute(SORTER_3D).Ready==true`·`Online==true`·`Reason==None` + 가짜 RCS 수신 본문 `LastFor(sorterChute).Ready==true`(실 HTTP push payload). `DestinationStatusService.cs:313` `ready = decision.Ready`.
- **[PASS] VS-2 (소터 busy → push ready=false, 2 하위)**: `VS2_...[Theory]` — (a) `SetReady(false)`→`Reason==Busy` (b) `SetCurFloor(1)`(미정렬)→`Reason==NotAligned`. 두 케이스 모두 `Compute().Ready==false` + push payload `LastFor.Ready==false`(true→false 전이 관찰).
- **[PASS] VS-3 (소터 offline → push ready=false)**: `VS3_...` — `SetFailReads(true)`(읽기 IOException 주입 = 진짜 OFFLINE, Disconnect 즉시재연결 회피) → snap.Online=false → `Compute().Online==false`·`Ready==false`·`Reason==Offline` + push payload `LastFor.Ready==false`.
- **[PASS] VS-4 [핵심회귀] (셀 만재여도 운영상태 ready → push ready=true)**: `VS4_...` — `MakeSorterFull`(Capacity=5·3셀 전부 점유·각 sorter_command COMPLETED+piece.qty=5 = 실 JOIN ground-truth) → **`Compute().Full==true` AND `Compute().Ready==true` 공존**·`Reason==None` + push payload `LastFor.Ready==true`. 만재가 push에 무영향 입증.
- **[PASS] VS-5 [핵심회귀] (PAUSED여도 운영상태 ready → push ready=true)**: `VS5_...` — `destination.Status=PAUSED`(실 DB) → **`Compute().Paused==true` AND `Compute().Ready==true` 공존**·`Reason==None` + push payload `LastFor.Ready==true`. paused가 push에 무영향 입증.
- **[PASS] VS-6 (슈트 push full/pause→false·비움/정지해제→true 현행 유지)**: 회귀 — `RcsPushTests.PUSH1_Chute_ReadyTransition`(만재→ready:false·비움→ready:true 전이당 1건)·부트스트랩 PAUSED 슈트6 `LastFor(6).Ready==false`. ComputeChute(`:247-252`) 무변경. 전체 99 GREEN에 포함·기존 단언 불변.
- **[PASS] VS-7 (IF-05 소터 3축 — r.Ready 무영향)**: `VS7_...` 실 HTTP `POST /api/v1/destination-query` — (a) offline(`SetFailReads`)+셀있음 → `Compute().Ready==false`인데 `result=="OK"`·chuteNo 반환(IF-05는 online 안 봄) (b) `Status=PAUSED` → `result=="NG"`·chuteNo=null (c) `MakeSorterFull`+정렬(`Compute().Ready==true`) → `SorterCanAcceptBarcode==false`·`result=="NG"`. 운영상태 ready 변화가 세 결과에 무영향(IF-05는 `r.Paused`+`SorterCanAcceptBarcode`만 소비) 입증.
- **[PASS] VS-8 (IF-05 슈트 full/pause여도 OK)**: 회귀 — `RcsController.cs:69-70`(슈트는 `DestinationBlock.None` 무조건 통과) + `SorterCellFullnessTests`의 슈트 IF-05 OK 단언(이전 스프린트 반전분) 불변. 전체 GREEN 포함.
- **[PASS] VS-9 (push 멱등 + 만재/paused 무발화)**: (a) `VS9a_...` — 부트스트랩 1건(ready=false) 안정 후 운영상태 전이(정렬) 발생, **16스레드 Barrier 동시관찰** `NotifyChuteChanged` → `WaitUntilExact(2, stableCount:8)`로 정확히 2건(부트1+전이1, 중복 0) = 클레임 경합 멱등 입증(중복억제만 아닌 동시관찰 프로브). (b) `VS9b_...`·`EC7_...` — 운영상태 불변인 채 `MakeSorterFull`+`Status=PAUSED` 전이 → `WaitUntilExact(baseReady, stableCount:8~10)`로 **소터 push 0건**(no-flood)·`LastFor.Ready==true` 유지. (c) 무변화 폴 폭주 0.
- **[PASS] VS-10 (무변경 가드 + grep)**: `git diff HEAD --stat`로 Wcs.Core·PlcGateway·Sim3ds·Data·Migrations.{Sqlite,SqlServer}·DestinationStatusPusher.cs·RcsPushClient.cs·RcsController.cs = **빈 출력(0줄)**. IF-05 `r.Ready` 미소비: `grep "\.Ready" RcsController.cs` → line 170 `decision.Ready`(IF-09 DepositDecision 로깅) 1건뿐 — `r.Ready`(DestinationReadiness) 소비 0. IF-05 콜백(`:64-79`)은 `r.Paused`+`SorterCanAcceptBarcode`만 소비(코드 직접 확인).

### Evaluation Criteria 충족 (가중치)
- **[PASS] ① 소터 push ready 운영상태 정확성 (★★★ 35%)**: `ready = decision.Ready`(`:313`). VS-1(true)·VS-2(busy false)·VS-3(offline false)·VS-4(만재+ready 공존)·VS-5(paused+ready 공존) 실 push payload로 입증.
- **[PASS] ② push 전이 발화 정확성 (★★★ 25%)**: VS-9a 전이당 1건(동시 16관찰 중복 0)·VS-9b·EC-7 만재/paused 전이 무발화(no-flood). 멱등 기계(DestinationStatusPusher) 무변경 보존.
- **[PASS] ③ 회귀 0 (★★ 20%)**: 슈트 push(VS-6)·IF-05 소터3축(VS-7)·IF-05 슈트(VS-8)·IF-09/IF-10 인바운드 불변. 반전 단언(EC-1/3/5/7·HP-5)은 삭제 아닌 정정(diff로 확인) — IF-05 NG·reason=FULL/Paused 회귀 가드 단언 보존. baseline 90 회귀 0.
- **[PASS] ④ 무변경 가드 (★★ 15%)**: VS-10 — 11개 경로 git diff 0줄.
- **[PASS] ⑤ 스펙 문서 정합 (★ 5%)**: `git diff HEAD -- docs/wcs_rcs_interface_kr.html` 실제 4개 라인영역(126·172·208·216~217) 정정 확인(diff 인용). 소터 push ready=운영상태·만재/정지는 IF-05 dispatch로 명문화. SPEC.md는 push-ready 정의 부재(폐지된 폴링 모델)라 무변경(계약 "있으면 정합·없으면 무변경" 준수 — 허위 deliverable 주장 0).

### Completion Conditions — 전부 충족
build 0/0·full GREEN exit 0·teardown 클린·표적 5회 flaky 0·무변경 가드 diff 0·docs diff 라인 확인·IF-05 r.Ready 미소비 grep 0.

### Minor 관찰 (비차단 — 다음 sprint Generator 참고)
- `DestinationStatusService.cs:34` `LoadedQtyByCell` 본문 주석에 "(LOADED)" 표현이 남았으나 실 산출원은 sorter_command(COMPLETED) JOIN — 코드는 정확, 주석 단어만. 비차단(이번 변경 표면 밖, 산출 로직 무변경 zone).
- 계약 §46에서 언급된 orphan `SorterHasAssignedCellWithRoomForBarcode`(인터페이스 `:84`)는 여전히 존재하나 계약이 "본 스프린트 표면 밖·무리하게 끌어오지 말 것"으로 명시 → 정당한 scope-out. 별도 정리 sprint 권장.

## Step 4.5 독립 코드리뷰 (orchestrator, opus, 팀 외부) — APPROVE (BLOCKING/MAJOR/MINOR 0)

독립 Opus 코드리뷰어가 7가설 적대적 검증 후 **APPROVE** — 차단 사유 0:
1. **크로스엔드포인트 분리**: `Compute()` 3소비처 grep — push=`.Ready`만/IF-05=`.Paused`만/`r.Ready` IF-05 소비 0(구조적 입증).
2. **Reason 무붕괴**: `Compute().Full/Reason` production 미소비(테스트만) + ComputeSorter가 hold=None 주입 → DepositDecider Full/Paused 분기 도달불가 → stale 누출 0. `ready=true ⟹ reason=None`.
3. **push 전이**: Pusher가 ready bool에 agnostic → 운영전이만 발화·만재/paused 무발화 구조 보장. full torn-read가 ready(=decision.Ready, DB 셀쿼리 미사용)에 무영향.
4. **무변경 가드**: 11파일 diff 0. 5. **엣지 6케이스**: `ready⟹online` 불변식. 6. **스펙 4라인** 과/소정정 0. 7. **반전 7건** 정정·IF-05 NG 가드 보존.

정보성 2건(비차단·todo 등재): ①`DestinationReadiness.Full/Paused/Reason` 현재 production 미소비(dead-but-consistent — 향후 소비처 생기면 ready=true&&Full=true 의미 재확인) ②`docs/SPEC.md` §2 구 IF-08(deposit-permission 폴링) 모델 기술 잔존(선재 문서부채·스코프밖).

→ BLOCKING 0 → Step 5 커밋 진행. 7회째 독립 리뷰 중 P2b에 이은 2번째 클린 통과(산출 단일지점 변경+소비처 grep+크로스엔드포인트 연결 테스트로 사전 방어).

---

# Sprint Feedback — S-M4-P4 (소터 셀 만재 판정 m4p4) — APPROVED

## Phase 3 Evaluate 결과 (Evaluator fresh evidence, 미커밋 working tree `feat/sorter-cell-fullness`, 2026-06-24)

**최종 판정: APPROVED** — 계약 Verification Scenarios(HP-1~3·EC-1~6)·Completion Conditions·Evaluation Criteria(40/25/15/10/10)·사용자 확정 4건(Q1~Q4) 전부 fresh 직접 실행 증거로 충족. 보호 zone diff 0·DB 스키마 무변경·teardown 회귀 0. 동시성 원자성은 SQL 단일 쿼리 구조 + EC-5 내부 불변식 + quiesce 등가성으로 입증.

### Fresh evidence (직접 실행 — generator 주장 신뢰 아님)
- **build**: `dotnet build Wcs.sln` → **경고 0개 / 오류 0개, BUILD_EXIT=0**.
- **full test**: `dotnet test Wcs.sln --blame-hang-timeout 120s` → **통과! 실패:0 통과:83 건너뜀:0 전체:83, TEST_EXIT=0**. Blame 수집기 "시퀀스 파일이 생성되지 않습니다"(teardown 전용 행 0). hangdump 생성 0건. **기존 76 회귀 0 + 신규 7 = 83.** (76 = Phase 2 baseline.)
- **flaky 0(타이밍/동시성 표적 ≥5회)**: `SorterCellFullnessTests | RcsPushTests` 필터 **5/5회 연속 13/13 GREEN·exit 0**. 비결정성 0.
  ```
  RUN 1: exit=0 통과:13 실패:0 (5s)
  RUN 2: exit=0 통과:13 실패:0 (5s)
  RUN 3: exit=0 통과:13 실패:0 (5s)
  RUN 4: exit=0 통과:13 실패:0 (5s)
  RUN 5: exit=0 통과:13 실패:0 (5s)
  ```

### 사용자 확정 4건(Q1~Q4) — 코드 직접 검증
- **[PASS] 확정1 (Q1 — IF-05 piece-aware 오더 재사용 예외)**: `RcsController.cs:65-74` availability 콜백 — `r.Paused`면 무조건 차단(우선), `r.Full && SORTER_3D && SorterHasActiveAssignmentForBarcode(id, barcode)`면 `DestinationBlock.None`(OK), 아니면 `Full`(NG). 슈트는 예외 미적용. `SorterHasActiveAssignmentForBarcode`(`DestinationStatusService.cs:107-110`)는 `EfCellSelector.SelectCell` ①분기(`DbRepositories.cs:578-585`)와 **predicate 완전 동형**(`ReleasedAt==null && Cell.DestinationId==id && Order.Items.Any(Barcode==bc)`), `.Any()` 읽기 전용 — 배정 부수효과 0. IF-05 OK→IF-10 SelectCell ①분기 재사용 일관(같은 셀 누적). 행동 입증: HP-2(OK·reason=NORMAL), EC-1(NG·reason=FULL).
- **[PASS] 확정2 (Q2 — 기존 소터 관찰 타이머 재사용, 별도 변화원 0)**: `git diff develop -- src/Wcs.Api/DestinationStatusPusher.cs` = **0줄**. `git diff develop -- src/Wcs.Api/ChuteCapacityService.cs` = **0줄**. `grep NotifyCellChanged|CellChanged|OnCellChanged src/ tests/` = **0건**(새 cell-change 변화원 없음). 기존 `RunSorterObserveLoopAsync`(`DestinationStatusPusher.cs:198`)가 매 주기 `_status.Compute`(line 233) 호출 → DB-aware ComputeSorter가 cell_assignment 변화를 자동 포착. 행동 입증: EC-3/HP-3(셀 점유/해제 전이가 타이머에 포착돼 푸시).
- **[PASS] 확정3 (Q3 — IServiceScopeFactory 주입, captive 회피)**: `DestinationStatusService` 생성자(`:76-86`)가 `IServiceScopeFactory`(싱글톤) 주입. `ComputeSorter`(`:156`)·`SorterHasActiveAssignmentForBarcode`(`:102`)가 `_scopeFactory.CreateScope()`로 scoped `WcsDbContext` 취득. `Program.cs` DI 등록은 `AddSingleton<IDestinationStatusService, DestinationStatusService>()` 불변(주석만 변경) — `IServiceScopeFactory` 자동 해석. 싱글톤이 scoped DbContext를 직접 주입하지 않음(안티패턴 회피).
- **[PASS] 확정4 (Q4 — 마이그레이션 0·DB 스키마 무변경)**: `git diff develop -- src/Wcs.Migrations.Sqlite src/Wcs.Migrations.SqlServer src/Wcs.Data` = **0줄**. 신규/untracked 마이그레이션 파일 0. cell/cell_assignment/`(cell_id) WHERE released_at IS NULL` 부분유니크 전부 기존. 읽기 전용 조회만 추가.

### Verification Scenarios — 항목별 PASS/FAIL (실 DB·가짜 RCS ground-truth)
- **[PASS] HP-1 (IF-05/Compute 빈셀 있음 + 정렬→ready)**: `HP1_EC6_...` — 빈셀 3개 시드(`FreeCellCount==3` DB 단언) → 미정렬 Compute `Full=false·Ready=false`(decision.Reason) → 정렬(CurFloor=2·Ready=1) Compute `Full=false·Ready=true`. 가짜 RCS·실 cell DB 기준.
- **[PASS] HP-2 (IF-05 오더 재사용 예외)**: `HP2_...` — ORD-003 3셀 전부 점유(`FreeCellCount==0`) + `SorterHasActiveAssignmentForBarcode(sorterId,"TEST-BARCODE-3")==true` → IF-05 실 HTTP `{result:"OK", chuteNo:30}`, piece_event reason=NORMAL·PieceStatus.RESERVED(DB 단언). 목적지 Compute는 여전히 `Full=true`(새 오더 수용 불가) — IF-05만 예외.
- **[PASS] HP-3 (푸시 full→!full 재푸시)**: `EC3_HP3_...` — 셀 1개 해제(`FreeCellCount==1` DB) → 관찰 타이머가 !full 전이 감지 → 가짜 RCS `LastFor(chute).Ready==true` + `CountFor` 전이당 정확히 1건(WaitUntilExact stableCount:6 폭주 0).
- **[PASS] EC-1 (소터 FULL→NG)**: `EC1_...` — 3셀 전부 점유 + 새 오더(ORD-SORTER-OTHER, barcode "SORTER-OTHER-BC", 활성 assignment 없음=재사용 불가) → Compute `Full=true·Ready=false·Reason=Full` + IF-05 실 HTTP `{result:"NG", chuteNo:null}`(200, 도메인 거부) + piece_event reason=FULL(DB 단언).
- **[PASS] EC-2 (소터 PAUSED/비활성→NG)**: `EC2_...` — 케이스A Status=PAUSED → Compute `Paused=true·Reason=Paused` + IF-05 실 HTTP NG·chuteNo=null. 케이스B IsActive=false → Compute `Paused=true·Reason=Paused`(직접 호출 단언). 비활성은 IF-05 와이어상 NO_DEST 경로(`DbRepositories.cs:120`가 availability 이전 차단 — 코드 확인)로 NG, ComputeSorter 비활성→paused 매핑 산출원 정확성은 Compute 직접 단언. 테스트 주석이 이 경로를 정확히 명시(허위 주장 없음).
- **[PASS] EC-3 (푸시 !full→full ready=false)**: `EC3_HP3_...` — 정렬 ready=true 안정 후 3셀 전부 점유(`FreeCellCount==0`) → 관찰 타이머 full 전이 → 가짜 RCS `Ready==false` + 전이당 정확히 1건(중복 0·무변화 폴 폭주 0, stableCount:6).
- **[PASS] EC-4 (paused 단독 전이)**: `EC4_...` — NORMAL ready=true → Status=PAUSED(셀 무변) → 가짜 RCS `Ready==false` 1건. full과 독립적 paused 단독 전이 입증.
- **[PASS] EC-5 (동시성 원자성)**: `EC5_...` — 6스레드 동시 배정/해제(각 40회 토글) + 관찰자가 Compute 반복 호출. **내부 불변식**(`full ⟹ !ready`, `ready ⟹ !full && !paused && online`) 위반 **0건**(observations>0). quiesce 후 full⟺빈셀0 등가성 확정(전부점유→full=true·ready=false / 전부해제→full=false). **원자성 구조 근거**: `ComputeSorter` full 쿼리(`:174-178`)는 `db.Cells.Any(c=> enabled && !db.CellAssignments.Any(active))` — EF가 단일 SQL(상관 서브쿼리)로 번역, 중간 materialize·check-then-act 분리 없음. `ready = !full && !paused && decision.Ready`는 두 read 완료 후 합성 → `full && ready` 구조적 불가. paused/full 두 쿼리는 동일 스코프 별도 round-trip이나, 불변식이 합성 후 자기일관 → torn-read 위험 없음.

### 무변경 가드 (git diff develop — 직접 실행)
- **보호 zone 0줄**: `git diff develop -- src/Wcs.PlcGateway src/Wcs.Sim3ds src/Wcs.Core/DepositDecider.cs src/Wcs.Data src/Wcs.Migrations.Sqlite src/Wcs.Migrations.SqlServer | wc -l` = **0**. `src/Wcs.Core` 전체 디렉터리 diff = **0줄**(DepositDecider 순수성·레지스터맵 불변).
- **RcsController 인바운드 무변경**: diff는 IF-05 `availability` 람다 + 주석 3줄에만 국한. IF-09(arrival)·IF-10(deposit) 핸들러 본문 0줄 변경 — 인바운드 회귀 0.
- **DenyReason 우선순위 변경(Full→Paused 우선)**: 와이어 결과 동일(둘 다 block!=None→NG). 소터가 full&&paused 동시일 때 내부 reason 라벨만 PAUSED(우선) — 계약 명시 우선순위(Offline>Paused>Full)와 일치, 와이어 회귀 0.

### Evaluation Criteria 충족 (가중치)
- **(40%) full/paused 산출 정확성**: 빈셀0→Full·ready=false / 빈셀≥1→!Full / PAUSED·비활성→Paused·ready=false. 단일 원자 쿼리. 오더 재사용 예외(Q1) 정확. HP-1/2·EC-1/2/5로 입증. **충족**.
- **(25%) 두 소비자 결선**: IF-05 소터 full/paused→NG(chuteNo=null), piece-aware 예외 정확. 푸시 ready 전이 반영(false/true 재푸시) 전이당 1회. EC-1/2/3/4·HP-2/3로 입증. **충족**.
- **(15%) 회귀 0**: 기존 76 전부 GREEN. 보호 zone diff 0. **충족**.
- **(10%) DI·경계 정합**: IServiceScopeFactory 경유(captive 0). DepositDecider 순수·Wcs.Core 의존성 0 불변. **충족**.
- **(10%) 동시성·예외 격리**: 빈셀 평가 SQL 원자. Observe/RunSorterObserveLoop 이중 try-catch(`:203-220`,`:231-241`)로 Compute 예외가 관찰 루프를 죽이지 않음. teardown exit 0. **충족**.

### Completion Conditions
- [x] dotnet build 경고 0, dotnet test 전체 GREEN(신규 VS 포함) — 83/83 exit 0.
- [x] VS 전부 자동화 테스트 통과 — 가짜 RCS 수신 본문·실 cell_assignment DB 상태 ground-truth(인메모리 카운터 단독 PASS 아님).
- [x] 무변경 영역 diff 0(Modbus·Sim3ds·DepositDecider·핸드셰이크·스키마).
- [x] Evaluator 독립 재실행으로 generator 주장 검증(fresh evidence).

### 메타 관찰(비차단)
- **EC-5 프로브 강도**: 내부 불변식 + quiesce 등가성만 검사(별도 free-count 재조회 비교는 읽기시점차 위양성이라 배제 — 정당). 진성 동시 클레임 경합(barrier 동시관찰)은 아님. **단 이번 경합 표면은 P2(아웃바운드 클레임)와 달리 "단일 SQL 원자 read"이므로 합성 모순 자체가 구조적 불가** — barrier 프로브로 추가 입증할 app-level claim 상태가 없음(공유 가변 상태는 DB 1테이블, 부분유니크가 일관성 보장). 따라서 P2 barrier 프로브 교훈은 이 스프린트엔 비적용(검토 후 판단). full⟹!ready 불변식이 SQL 원자성을 정확히 반영.
- **신규 테스트 파일 untracked**: `tests/Wcs.Tests/SorterCellFullnessTests.cs`는 아직 `git add` 전(working tree에 존재·컴파일·실행됨). 커밋은 team-lead — staging 포함 확인 권고.

### 결론
계약 전 항목 충족. 4-Tier 독립 코드리뷰(orchestrator Step 4.5)를 거쳐 commit 진행 권고. 커밋·push는 team-lead.

---

# Sprint Feedback — S-소터셀수량full + 슈트 IF-05 정정 — APPROVED

## Phase 3 Evaluate 결과 (Evaluator fresh evidence, 미커밋 working tree `feat/sorter-cell-qty-full`, 2026-06-25)

**최종 판정: APPROVED** — 계약 Verification Scenarios(HP-1~5·EC-1~7=12)·Completion Conditions·Evaluation Criteria(30/20/15/15/10/10)·사용자 확정 4건(Q1~Q4) 전부 fresh 직접 실행 증거로 충족. 보호 zone diff 0·DB 스키마/마이그레이션 무변경·teardown 회귀 0. 셀 수량 산출은 sorter_command(COMPLETED) JOIN piece.qty(piece별 1건·cell_assignment 비사용) DB ground-truth로 단언. 동시성은 단일 응답 내부 불변식(full⟹!ready) + EC-5 375회 관찰 0모순 + quiesce 수렴으로 입증.

### Fresh evidence (직접 실행 — generator 주장 신뢰 아님)
- **build**: `dotnet build Wcs.sln` → **경고 0개 / 오류 0개, BUILD_EXIT=0** (8개 프로젝트 — 마이그레이션 어셈블리 2개 포함).
- **full test**: `dotnet test Wcs.sln --no-build --blame-hang-timeout 120s` → **통과! 실패:0 통과:88 건너뜀:0 전체:88, TEST_EXIT=0**. Blame "시퀀스 파일이 생성되지 않습니다"(teardown 전용 행 0·hangdump 0). 기존 83 → 88 = +12 신규 SorterCellFullnessTests − 7 구. teardown hang 재발 0.
- **신규 SorterCellFullnessTests 단독**: 필터 실행 → **통과:12 exit 0**(HP-1/2/5·EC-1~7, EC-6은 Theory 3행).
- **flaky 0 (타이밍/동시성 표적 ≥5회)**: `SorterCellFullnessTests | RcsPushTests` 필터 **5/5회 연속 18/18 GREEN·exit 0**. 비결정성 0. (RUN 1~5 각 exit=0 통과:18 실패:0, 중단/hangdump 0.)
- **EC-5 동시성 프로브 강도**: console 캡처 → `[EC-5] 6스레드 동시 적재/배정/해제 + Compute 375회 — 내부 모순 0건`. 375 관찰은 단일 idle 경로(P3 함정)가 아닌 진성 경합 샘플.

### 사용자 확정 4건(Q1~Q4) — 코드 직접 검증
- **[PASS] Q1 (push ready도 셀 작업수량 반영)**: `ComputeSorterFull`(`DestinationStatusService.cs:192-226`) = `빈 enabled 셀 없음 AND 모든 활성 배정 셀 작업수량 도달`. 빈셀≥1→false(`:206`), 배정셀 중 미달≥1→false(`:217-221`), 둘 다 없으면 true(`:225`, enabled 0개도 true). `ComputeSorter`(`:289`)가 이 합성 full 산출, `ready = !full && !paused && decision.Ready`(`:292`). IF-05 piece-aware는 셀 수량 산출원(`LoadedQtyByCell`) 공유. 푸시 변화원=기존 150ms 관찰 타이머(`DestinationStatusPusher.cs` diff 0). 행동 입증: HP-5·EC-7.
- **[PASS] Q2 (sorter_command COMPLETED JOIN piece.qty 합)**: `LoadedQtyByCell`(`:157-172`) = COMPLETED sorter_command JOIN piece.qty, DISTINCT (CellId,PieceId,Qty)로 piece별 1건(재시도 중복 0), GroupBy(CellId) SUM. cell_assignment는 qty 산출 비사용(점유 판정·오더 셀 식별 전용 — grep 확인). EfSorterCommandJournal.Finalize Success→COMPLETED 정합. 행동 입증: EC-1(실 sorter_command 행 ground-truth)·EC-6.
- **[PASS] Q3 (Capacity NULL/≤0 = 무제한)**: `IsCellAtCapacity`(`:178-179`) = `capacity is int cap && cap > 0 && currentQty >= cap`. NULL/≤0→무제한, 양수일 때만 `>=`. 행동 입증: EC-4(NULL+현재100×3→Full=false·OK)·EC-6(경계 등호).
- **[PASS] Q4 (슈트 full·paused 둘 다 OK·소터 NG 유지, 차단점 2곳 dest 분기)**: 차단점①`DbRepositories.cs:73-79`(order-level PAUSED를 SORTER_3D일 때만)·`:128-129`(blocked=!IsActive||(SORTER_3D&&Status!=NORMAL)). 차단점②`RcsController.cs:67-79`(dt!=Sorter3D→None 슈트 통과, 소터만 Paused/Full 차단·piece-aware 예외). ChuteCapacity 집계 무변경(diff 0), OnReserved 슈트만(`:83`·초과 허용). 행동 입증: HP-3/4·EC-3.

### Verification Scenarios — 항목별 PASS/FAIL (실 sorter_command/cell.Capacity DB·가짜 RCS 본문 ground-truth)
- **[PASS] HP-1 (배정 셀 여유→OK·NORMAL)**: Capacity=10·cellNo1 현재3(실 sorter_command COMPLETED), 3셀 점유(FreeCellCount==0) → HasRoom==true + Compute.Full==false + IF-05 실 HTTP {OK,chuteNo} + piece_event reason=NORMAL.
- **[PASS] HP-2 (새 오더 빈 셀→OK)**: 빈셀3 + 셀 미보유 바코드 → Compute.Full==false + IF-05 {OK}. free-cell 회귀 가드.
- **[PASS] HP-3 (슈트 FULL→OK)**: `If05_Chute_Full_ThenCleared_Normal`(반전) — OnReserved(workFullQty)→GetHold==Full 확인 후에도 IF-05 {OK,chuteNo=1}, 비움 전후 둘 다 OK.
- **[PASS] HP-4 (슈트 PAUSED→OK)**: `If05_Chute_Paused_Ng`·`VS2_If05_PausedOrder_NgPaused`·`S8_Chute_Paused_Ng`(반전) — 슈트 PAUSED → IF-05 {OK,chuteNo=6}. order-level PAUSED 차단이 슈트 미적용.
- **[PASS] HP-5 (push ready 일부 여유→유지·Q1)**: 빈셀0·cell1·2 도달(현재5=Cap5)·cell3 미달(현재3) → Compute.Full==false + 가짜 RCS Ready==true 유지 + CountFor 무변화(stableCount:8 폭주 0).
- **[PASS] EC-1 (셀 full→NG·FULL)**: SORTER-FULL-BC 배정셀 현재5≥Cap5·전셀 도달·빈셀0 → HasRoom==false + Compute.Full==true·Ready==false·Reason==Full + IF-05 {NG,chuteNo=null}(200) + piece_event reason=FULL. 실 sorter_command 행 ground-truth.
- **[PASS] EC-2 (빈셀0+오더 미보유→NG)**: 3셀 점유+전부 도달·SORTER-NEW-BC(배정 없음) → 재사용 불가 + Full==true + IF-05 NG. m4p4 회귀 가드.
- **[PASS] EC-3 (소터 PAUSED NG 유지)**: 소터 PAUSED → Compute.Paused==true·Reason==Paused + IF-05 NG. (B) 정정이 소터 미파손.
- **[PASS] EC-4 (Capacity NULL=무제한)**: 시드 NULL 단언+3셀 점유+현재100×3 → HasRoom==true(무제한) + Full==false + IF-05 OK.
- **[PASS] EC-5 (동시성 원자성)**: 6스레드 적재(sorter_command COMPLETED)/배정/해제 churn + Compute 375회 → 내부 불변식(full⟹!ready, ready⟹!full&&!paused&&online) 위반 0건. quiesce 전부도달→Full=true·ready=false / 빈셀1→Full=false 수렴. 구조 근거: ready가 동일 materialized full에서 합성→full&&ready 단일 응답 구조적 불가. ComputeSorterFull 2쿼리이나 계약 명시 eventually-consistent + 응답내 불변식 자기일관 → torn-read가 모순 불생성. m4p4 경계(단일 SQL 원자 read=불변식 단언 충분, barrier 비적용) 동형.
- **[PASS] EC-6 (셀 경계 ≥ 등호)**: Theory 3행 — 4<5=OK / 5==5=NG / 6>5=NG. HasRoom==(currentQty<capacity) + IF-05 결과 일치.
- **[PASS] EC-7 (push ready=false 전이·Q1)**: 빈셀0+cell3만 여유(현재3)→ready=true 안정(stableCount:6)→cell3 +2(현재5 도달)→가짜 RCS Ready==false + CountFor 전이당 1건(폭주 0)→빈셀1 복귀(assignment 해제)→Ready==true 재푸시 1건.

### 무변경 가드 (git diff develop — 직접 실행)
- **보호 zone 0줄**: `git diff --stat develop -- src/Wcs.PlcGateway src/Wcs.Sim3ds src/Wcs.Core src/Wcs.Data src/Wcs.Migrations.Sqlite src/Wcs.Migrations.SqlServer` = **empty(0줄)**. DepositDecider 순수·레지스터맵·스키마·마이그레이션 불변.
- **ChuteCapacity 집계 0줄 / 푸시 멱등 기계(DestinationStatusPusher) 0줄**: 각 `git diff --stat develop` = 0. 전이추적·per-dest 락·in-flight·150ms 관찰 타이머·집계 모델 무변경 — Compute 내부 SorterFull 의미만 수량 반영 확장.
- **production diff**: RcsController(23줄·IF-05 availability 람다+주석)·DbRepositories(16줄·차단점 dest 분기)·DestinationStatusService(173줄·신규 산출 함수). 인바운드 IF-09/IF-10 핸들러 본문 0줄. merge-base==develop@HEAD(클린 descendant).
- **의도적 반전 5건(슈트 NG→OK)**: ApiIntegrationTests 3 + ScenarioTests 2 — assertion만 NG→OK 갱신(삭제 아님·HTTP 왕복 구조 유지). 계약은 3건 명시했으나 ScenarioTests 동일행위 2건 추가(미반전 시 RED)는 정당. 소터 NG는 EC-1/2/3 불변 단언.

### Evaluation Criteria 충족 (가중치)
- **(30%) 소터 셀 수량 full 산출 정확성**: COMPLETED JOIN piece.qty·DISTINCT piece·중복 0 ≥ Capacity(양수)→full, NULL/≤0=무제한. 동일 스코프 스냅샷. EC-1/4/5/6. **충족**.
- **(20%) IF-05 결합 모델 정확성**: 새 오더(빈셀≥1)=OK / 기존 오더 여유=OK / full+빈셀0=NG. read predicate가 IF-10 SelectCell ①분기와 동형(drift 0). HP-1/2·EC-1/2/6. **충족**.
- **(15%) push ready 수량 반영(Q1)**: SorterFull=빈셀0 AND 전배정셀 도달일 때만 false. 여유≥1→유지. 전이당 1건. HP-5·EC-7. **충족**.
- **(15%) (B) 슈트 OK + 소터 NG 유지**: 차단점①② 슈트 통과·소터 불변. 슈트 readiness 푸시 계속. HP-3/4·EC-3·반전 5건. **충족**.
- **(10%) 회귀 0 + 의도적 반전**: 88/88 GREEN·보호 zone diff 0·슈트 NG 5건 OK 반전(삭제 아님)·무변경 항목 diff 0. **충족**.
- **(10%) 동시성·경계·DI 정합**: 셀full⟹piece NG / SorterFull⟹push ready=false(EC-5 375회 0모순)·IServiceScopeFactory(captive 0)·DepositDecider 순수·Wcs.Core diff 0·예외 격리·teardown exit 0. **충족**.

### Completion Conditions
- [x] dotnet build 경고 0, dotnet test 전체 GREEN(신규/반전 포함) — 88/88 exit 0.
- [x] VS 전부 자동화 통과 — 실 sorter_command/cell_assignment/cell.Capacity DB + 가짜 RCS 수신 본문 ground-truth.
- [x] 무변경 영역 diff 0(Modbus·Sim3ds·DepositDecider·핸드셰이크·스키마·ChuteCapacity 집계).
- [x] Evaluator 독립 재실행 검증(fresh evidence — HTTP 본문·piece_event.reason·DB 셀수량·push 카운트 raw).

### 메타 관찰(비차단 — Minor)
- **스테일 테스트명 4건**: 반전된 슈트 테스트가 옛 `_Ng` 이름 유지(`If05_Chute_Paused_Ng`·`VS2_If05_PausedOrder_NgPaused`·`S8_Chute_Paused_Ng`·`If05_Chute_Full_ThenCleared_Normal`) — 본문은 OK 단언. 동작 무영향(주석에 반전 명시)이나 가독성 저하. 다음 정리 sprint에서 `_Ok` 개명 권고. (계약이 "삭제 금지·갱신"만 요구·assertion 정확 — 비차단.)
- **ComputeSorterFull 2-쿼리**: 단일 SQL 1문이 아닌 동일 스코프 2 round-trip(cells+occupancy, loaded-qty). 계약 "단일 원자 쿼리" 문구 대비 실제 요구 불변식(응답내 full⟹!ready + eventually-consistent)은 충족(EC-5 375회 0모순). 상관 서브쿼리로 단일 SQL화 가능하나 현 구조로 계약 불변식 위반 0 — 비차단. Step 4.5 코드리뷰에서 구조 재확인 권고.

### 결론
계약 전 항목(HP-1~5·EC-1~7·Completion·Criteria·확정 Q1~Q4) fresh 증거로 충족. 무변경 zone diff 0·teardown exit 0·flaky 0(5/5). 4-Tier 독립 코드리뷰(orchestrator Step 4.5)를 거쳐 commit 진행 권고 — 신규 SorterCellFullnessTests staging 포함 확인. 커밋·push는 team-lead.

**APPROVED**

---

## 재평가 — 독립 코드리뷰 BLOCK(MAJOR-1 + MINOR-2) 수정 후 (Evaluator fresh evidence, 2026-06-25)

**판정: APPROVED (수정 검증 완료)** — 독립 코드리뷰가 적발한 MAJOR-1(IF-05 room 게이트 ↔ IF-10 SelectCell 용량 무지 비대칭 → 오더 다중 셀 시 full 셀 재사용으로 Capacity 초과 적재)과 MINOR-2(주석)가 해소됨을 fresh 직접 실행으로 확인. 내 1차 APPROVE는 동형성을 assignment-finding predicate에만 검증하고 **room/capacity 차원·SelectСell 셀 선택**을 검증 안 한 사각이었음 — 정당한 BLOCK.

### 코드리뷰 지적 핵심(내 1차 사각)
- IF-05 room 게이트(`SorterHasAssignedCellWithRoomForBarcode`)는 "오더 배정 셀 중 ∃여유 셀"인데, `EfCellSelector.SelectCell` ①분기는 `FirstOrDefault`로 **임의(용량 무관)** 배정 셀 재사용 → 오더가 full 셀+여유 셀 동시 보유 시 IF-05 OK인데 SelectCell이 full 셀 골라 초과 적재. "IF-05 OK ⟹ 적재 가능" 위반. (내 "subset" 논리 오류: IF-05는 ∃여유셀, SelectCell은 임의 첫 셀 — 같은 셀 보장 없음.)

### 수정 검증 (코드 직접 + 동작)
- **[PASS] 셀 수량 로직 단일 추출(byte-consistent)**: 신규 `src/Wcs.Api/SorterCellQty.cs`(internal static)가 `LoadedQtyByCell`·`IsCellAtCapacity`를 한 곳에 두고 IF-05·SelectCell·SorterFull 3자 공유. `DestinationStatusService` private 복사본 제거·위임. → "동형"을 복제-주장이 아니라 **단일 구현 공유**로 구조 보장(drift 원천 차단).
- **[PASS] SelectCell ①분기 용량 인식**: `assignedCells`(그 오더 활성 배정 셀)를 `OrderBy(CellNo)`(결정적) 후 `SorterCellQty.IsCellAtCapacity`로 **여유 셀(현재<Capacity) 첫 셀만** 재사용. 전부 full이면 ②빈 셀 폴백, 없으면 ③null. (`DbRepositories.cs` SelectCell diff 확인 — `roomCell = assignedCells.FirstOrDefault(!IsCellAtCapacity(...))`.)
- **[PASS] 루트 원인 완결 — availability piece 단위 교정**: 기존 availability는 목적지-단위 `SorterFull`(다른 오더 여유 셀 포함)로 분기해 "A 셀 full·빈셀0이어도 B 오더 여유 셀 때문에 SorterFull=false면 A piece OK로 샘" 잔여 홀 존재. → `IDestinationStatusService.SorterCanAcceptBarcode` 신규 = `HasAssignedCellWithRoom(order) OR HasFreeEnabledCell` — **SelectCell 비-null 조건과 biconditional 동형**. `RcsController` availability: 소터 Paused 차단 후 `SorterCanAcceptBarcode ? None : Full`. 목적지-단위 SorterFull은 푸시 ready 전용 유지(피·목적지 단위 명확 분리). 동형성 검증: ∃여유배정셀→SelectCell① non-null / 없고 ∃빈셀→SelectCell② non-null / 둘 다 없음→SelectCell null & CanAccept false. биconditional 성립.
- **[PASS] EC-8(크로스-엔드포인트 정합)**: 오더 X가 cell1(full·현재5=Cap5)+cell2(여유·현재2) 보유·빈셀0 → IF-05 실 HTTP OK + `SelectCell("ORDX-BC")` == **cell2(여유, full 아님)** + 적재 후 현재수량 ≤ Capacity(실 sorter_command SUM ground-truth, 초과 0). **이 단언이 옛 FirstOrDefault 버그면 cell1 반환으로 FAIL** — 버그를 정확히 포착. console: `[EC-8] SelectCell 여유셀2 선택·초과 적재 0`.
- **[PASS] EC-9(piece 단위 — 다른 오더 여유 셀 무용)**: A(cell1 full)+B(cell2 여유)+빈셀0 → `Compute.Full==false`(B 여유) BUT `SorterCanAcceptBarcode("ORDA-BC")==false`·IF-05(A) 실 HTTP **NG**·`SelectCell("ORDA-BC")==null` / `SorterCanAcceptBarcode("ORDB-BC")==true`·IF-05(B) OK·`SelectCell("ORDB-BC")==2`. **EC-5 단일 Compute 관찰자로는 못 잡던 IF-05↔IF-10 크로스-엔드포인트 일관성**을 정면 단언. console: `[EC-9] IF-05(A) NG·SelectCell(A) null / IF-05(B) OK·SelectCell(B)=2 (동형)`.
- **[PASS] MINOR-2**: `ComputeSorterFull` 주석 "단일 원자 쿼리" → "같은 스코프 2-쿼리 순차(보수적 스냅샷)"로 정정 + record 내부 불변식(full⟹!ready) 성립 명시.

### Fresh evidence (직접 실행)
- **build**: `dotnet build Wcs.sln` → 경고 0/오류 0, BUILD_EXIT=0.
- **full test**: `dotnet test Wcs.sln --no-build --blame-hang-timeout 120s` → **실패:0 통과:90 전체:90, TEST_EXIT=0**(88→90 = +EC-8 +EC-9). Blame "시퀀스 파일 미생성"=teardown hang 재발 0·hangdump 0. **기존 88 회귀 0**.
- **flaky 0**: `SorterCellFullnessTests|RcsPushTests`(20건) **5/5회 연속 GREEN·exit 0**.
- **EC-8/EC-9 console ground-truth 캡처**(위 인용).

### 무변경 가드 재확인 (git diff develop)
- **보호 zone 0줄**: `git diff --stat develop -- src/Wcs.PlcGateway src/Wcs.Sim3ds src/Wcs.Core src/Wcs.Data src/Wcs.Migrations.Sqlite src/Wcs.Migrations.SqlServer src/Wcs.Api/ChuteCapacityService.cs src/Wcs.Api/DestinationStatusPusher.cs` = empty(0). 푸시 멱등 기계·capacity 집계·스키마·마이그레이션 불변.
- **DbRepositories 변경 국한**: 4 hunk — QueryDestination 차단점 2(원 스프린트)·EfCellSelector 클래스 doc 주석(`@@-547` 컨텍스트)·SelectCell ①분기 용량 인식. `EfDepositRecorder.RecordDeposit` 멱등 로직·`EfAlarmSink`·`EfSorterCommandJournal`·`EfArrivalRecorder`·`EfAgvFloorResolver` 본문 0줄.
- **신규 파일**: `src/Wcs.Api/SorterCellQty.cs`(?? untracked·컴파일·실행됨). 커밋 시 staging 포함 확인(team-lead).

### 결론(재평가)
독립 코드리뷰 MAJOR-1·MINOR-2 해소 확인. 크로스-엔드포인트 불변식("IF-05 OK ⟺ SelectCell 적재 가능 ⟹ Capacity 초과 0")이 단일 공유 로직(SorterCellQty) + piece 단위 게이트(SorterCanAcceptBarcode) + EC-8/EC-9 명시 단언으로 보강됨. 회귀 0(90/90)·teardown exit 0·flaky 0(5/5)·무변경 zone diff 0. team-lead가 수정분 재리뷰 후 커밋 권고 — 신규 SorterCellQty.cs·SorterCellFullnessTests staging 포함 확인.

**APPROVED (수정 후 재검증)**
