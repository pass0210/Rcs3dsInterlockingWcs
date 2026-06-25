# Sprint Contract — S-소터push운영상태 (소터 IF-08 push ready를 운영상태로 좁히고 SorterFull·PAUSED를 push에서 분리)

> 사용자 확정 2026-06-25. Planner는 WHAT/WHERE/검증만 정의 — 구현 방법(HOW)은 Generator 결정.
> 사용자 확인 대기 (Phase 1 게이트).
> 선행: S-소터셀수량full(PR #14 계열, 머지됨) — 소터 full=셀 작업수량 기반 + IF-05 dispatch 정정 + push ready 결선.

---

## Goal

소터(SORTER_3D) IF-08 아웃바운드 push의 `ready` 플래그를 **운영 상태(decision.Ready = online && 정렬(CurFloor==운영층) && Ready==1)** 만으로 좁힌다. 셀 만재(SorterFull)와 destination PAUSED는 **push에서 제외**한다 — 이 둘은 IF-05 dispatch 게이트에서만 차단된다(2단계 게이트 분리: "받을 수 있는 운영 상태인가"는 push, "지금 이 piece를 보낼 목적지가 있는가"는 IF-05).

현재 `DestinationStatusService.ComputeSorter`는 `ready = !full && !paused && decision.Ready`로 산출해 push·IF-05가 같은 `Compute().Ready`를 공유하던 구조였다. 이 변경 후:
- **push ready** = `decision.Ready`(운영 상태) 만. SorterFull·PAUSED는 push ready에 영향 없음.
- **IF-05 dispatch** = `r.Paused` + `SorterCanAcceptBarcode(셀 기준)` 소비(현행 유지, `r.Ready` 미사용).
- `DestinationReadiness`의 `Full`/`Paused`/`Reason` 필드는 (IF-05·내부 DenyReason 사유용) **계속 산출**하되, **소터 ready 합성에서만 제외**한다.

스펙 문서(`docs/wcs_rcs_interface_kr.html`)의 IF-08 소터 `ready` 정의와 IF-05 표를 위 확정 모델과 정합시킨다(필요시 `docs/SPEC.md`도).

---

## Implementation Scope (Generator가 수행할 것 — WHAT/WHERE)

### 변경 대상 파일
1. **`src/Wcs.Api/DestinationStatusService.cs`** — `ComputeSorter`의 소터 ready 합성을 운영 상태(`decision.Ready`)만으로 좁힌다(현재 `!full && !paused && decision.Ready`인 line 295 지점). `Full`/`Paused`/`Online`/`Reason` 산출 자체는 유지(IF-05·내부 사유 소비). `Full`/`Paused`를 ready 합성에서 빼되, 산출은 유지된다는 점을 코드/주석으로 명확히. DenyReason 우선순위 문구(현재 line 257~259, 295~304)도 새 모델과 일치하게 정정 — ready는 운영상태로만 판정되므로 Full/Paused가 ready=false를 만들지 않음을 반영하되, `Reason`은 IF-05/내부 사유로 여전히 Offline/Paused/Full을 구분 보존할지 Generator가 일관되게 결정(단 ready 합성에서 Full/Paused 제외는 필수). 클래스 헤더 주석(line 8~38, 244~259)의 소터 ready 정의 문구도 새 모델로 정정.
2. **`tests/Wcs.Tests`** — 아래 Verification Scenarios를 커버하는 테스트 추가/갱신. 실 DB(인메모리 SQLite seed)·가짜 RCS 수신 본문(push payload 캡처)·게이트웨이 snapshot을 ground-truth로. **인메모리 카운터 단독 단언 금지**. 기존 소터 push/IF-05 테스트 중 "셀 만재→push ready=false" 또는 "paused→push ready=false"를 단언하던 것이 있으면 새 모델(만재·paused는 push에 영향 없음)로 갱신(삭제가 아니라 단언 정정 — S-소터셀수량full 슈트 테스트 반전 선례 참조).
3. **`docs/wcs_rcs_interface_kr.html`** — 아래 "스펙 정정 대상" 라인들을 확정 모델과 정합.
4. **`docs/SPEC.md`** (필요시) — 소터 push ready 정의/판정 표에 push와 dispatch 2단계 게이트 분리가 반영돼 있지 않으면 정정. Generator가 SPEC.md를 읽어 해당 정의가 있는지 확인 후, 있으면 정합·없으면 무변경(불필요한 추가 금지).

### 스펙 정정 대상 (`docs/wcs_rcs_interface_kr.html` — Planner가 확인한 라인)
- **line 126** (§3 `ready` 필드 정의): 현재 "3D 소터 슈트: 추가로 온라인·정렬·정지(비분류)" — push ready에 "정지(paused)"가 포함돼 있음. → 소터 push ready = **온라인·정렬·비분류(운영 상태)** 만으로 정정. 만재·정지는 push가 아니라 IF-05 dispatch 게이트임을 명시.
- **line 172** (§5 IF-08 prose): 현재 "일반 슈트면 만재·정지(WCS 관리), 3D 소터 슈트면 추가로 분류 중·오프라인·미정렬" — 소터 push에 만재·정지가 섞여 있음. → 소터 push ready=false는 **분류 중·이동 중·미정렬·오프라인(운영 상태 BUSY/OFFLINE)** 만이고, 만재·정지는 IF-05 dispatch에서 차단됨을 명시. 슈트 push 정의(만재·정지)는 현행 유지.
- **line 216~217** (§6 IF-08 RCS 해석 표): line 217이 `full / paused / online=false`를 소터 push ready=false 사유로 명시. → 소터 push ready=false는 운영 상태(분류 중·이동 중·미정렬·오프라인)로 정정. full/paused는 push 사유에서 제외(IF-05 게이트). line 216의 "소터면 분류 중·오프라인도" 문구도 만재·정지 제외로 정합.
- **line 208** (§6 IF-05 표 `FULL / PAUSED → NG` generic): 현재 슈트·소터 무차별로 "FULL/PAUSED→NG". → 확정 모델 정합: **슈트는 full/paused여도 OK**(IF-05 dispatch 통과, readiness는 push로 별도 전달), **소터는 PAUSED면 NG·셀 기준(SorterCanAcceptBarcode) 못 받으면 NG**, OFFLINE은 IF-05에서 보지 않음(셀 있으면 OK). (이미 코드는 RcsController.cs:64~79·확정4로 이렇게 동작 — 문서만 generic이라 정정.)

### 무변경 가드 파일 (diff 0 — Generator가 절대 건드리지 않음)
- `src/Wcs.Core/` 전체(DepositDecider·Models·RegisterMap — 순수 판정 엔진).
- `src/Wcs.PlcGateway/` 전체(PlcGateway·HandshakeOrchestrator — Modbus 마스터·핸드셰이크).
- `src/Wcs.Sim3ds/` 전체(시뮬레이터).
- `src/Wcs.Data/` 스키마·`src/Wcs.Migrations.*`(DB 스키마/마이그레이션).
- `src/Wcs.Api/DestinationStatusPusher.cs` — push 멱등 기계(전이추적 Acked/Computed·per-dest Gate 락·PushInFlight·재시도·관찰 타이머·부트스트랩). **본문 무변경** — `Compute().Ready`가 매 관찰 주기 재산출돼 새 ready 의미를 자동 포착하므로 Pusher 코드 변경 불요.
- `src/Wcs.Api/RcsPushClient.cs` — push 페이로드 `{chuteNo, ready, timeStamp}`·HttpClient·백오프(무변경).
- `src/Wcs.Api/Controllers/RcsController.cs` — IF-05/09/10 인바운드. 특히 **IF-05 availability 콜백(line 64~79)이 `r.Ready`를 소비하지 않고 `r.Paused`+`SorterCanAcceptBarcode`만 소비함**(Planner가 코드로 확인). 이 무변경이 "소터 ready 의미 변경이 IF-05에 영향 없음"의 회귀 가드. (Generator는 변경 전 grep로 `.Ready` 소비처를 전수 확인하고, IF-05 경로에서 `r.Ready` 소비처 0임을 증거로 남길 것 — 만약 소비처가 발견되면 본 변경의 전제 위반이므로 Evaluator에 보고.)
- `ChuteCapacityService`(슈트 capacity 집계), `SorterCellQty`(셀 수량 산출 공유 로직), `EfCellSelector.SelectCell`(셀 배정), `SorterCanAcceptBarcode`/`HasAssignedCellWithRoom`/`HasFreeEnabledCell`/`ComputeSorterFull`(IF-05 piece-aware·full 산출 — 산출 로직 자체는 무변경, ready 합성에서만 제외).

> 함께 정리 가능한 이연 MINOR(선택 — scope 내, Generator 판단): todo.md의 `Compute` 본문 주석 "단일 원자 쿼리로 평가"(DestinationStatusService.cs:253)는 이번에 ComputeSorter 주석을 손대므로 함께 "같은 스코프 순차 읽기"로 정정 가능. orphan `SorterHasAssignedCellWithRoomForBarcode` 제거는 IF-05/테스트 영향이 있어 본 스프린트 표면 밖 — **무리하게 끌어오지 말 것**(별도 정리 sprint). 정리 항목은 push ready 변경과 충돌하지 않을 때만, 가독성 한정으로.

---

## Evaluation Criteria (가중치 — Evaluator 판정 기준)

1. **소터 push ready 운영상태 정확성 (★★★, 35%)** — 소터 push ready == `decision.Ready`(online && 정렬 && Ready==1). 셀 만재(SorterFull=true)·destination PAUSED는 push ready를 false로 만들지 **않는다**. busy(Ready==0 또는 미정렬)·offline은 push ready=false. 실 DB seed + 게이트웨이 snapshot으로 ground-truth 구성, push payload(가짜 RCS 수신 본문)의 `ready` 값으로 검증.
2. **push 전이 발화 정확성 (★★★, 25%)** — 운영 상태 전이(예: 미정렬→정렬, Ready 0→1, offline→online)에는 push가 발화. **셀 만재 전이(빈 셀→만재 또는 만재→여유)·paused 전이만으로는 소터 push 무발화**(운영 상태 불변이면 push ready 불변 → 전이 없음 → push 0건). 멱등 기계(전이당 1회·무변화 무발화)는 보존.
3. **회귀 0 — 슈트 push·IF-05·인바운드 (★★, 20%)** — 슈트 push ready(full/pause→false·비움/정지해제→true, ComputeChute 현행) 불변. IF-05 소터(셀 있으면 offline이어도 OK·paused면 NG·만재(셀없음)면 NG)·IF-05 슈트(full/pause여도 OK) 현행 유지. IF-09/IF-10 인바운드 동작 불변. 기존 테스트 단언 회귀 0(반전된 만재/paused push 단언만 정정 — diff로 정정 내역 확인).
4. **무변경 가드 (★★, 15%)** — Modbus 레지스터맵·핸드셰이크·Wcs.Core(DepositDecider 순수성)·DB 스키마/마이그레이션·push 멱등 기계(DestinationStatusPusher·RcsPushClient)·RcsController·SorterCellQty·ChuteCapacity·EfCellSelector `git diff` 0줄로 입증.
5. **스펙 문서 정합 (★, 5%)** — `docs/wcs_rcs_interface_kr.html` line 126·172·208·216~217 정정이 `git diff docs/`로 실재 확인. (필요시 SPEC.md.) 문서 주장 ≠ 실파일 변경 — diff로 대조(S-M4-P2b "정정 완료 거짓" 교훈).

---

## Completion Conditions (Evaluator 통과 최소 조건)

- `dotnet build` 경고 0 / 오류 0.
- `dotnet test` 전체 GREEN(신규 시나리오 포함) + testhost teardown **exit 0**(단언 PASS여도 exit≠0·`중단됨`·abort·hangdump면 미통과 — exit code·중단 라인 직접 확인. baseline 대조로 선재/도입 귀속: 선재 + 무변경 zone이면 명시·비차단, 본 변경 도입이면 차단 — S-RCS-IF-REDESIGN-P1 teardown 귀속 절차 적용).
- 타이밍/동시성 표적(소터 관찰 타이머 기반 push 전이·무발화 시나리오) **단독 ≥5회 연속 flaky 0**.
- 무변경 가드 파일 `git diff` 0(committed/staged/working/untracked 전부).
- 스펙 문서 정정 `git diff docs/`로 라인 단위 확인.
- IF-05 경로가 `r.Ready`를 소비하지 않음을 grep 증거로 확인(소터 ready 의미 변경이 IF-05에 무영향임의 구조적 근거).

---

## Parallel Modules

N/A (single module — Wcs.Api 한 산출 함수의 ready 합성 변경 + 그 검증 + 문서 정정. 모듈 경계 분할 없음).

## Evaluation Dimensions

functional only.
(단일 차원이나, 본 변경은 두 소비자(push·IF-05)가 같은 `Compute` 산출을 분기 소비하는 **크로스-엔드포인트 정합** 표면이다 — Evaluator는 S-소터셀수량full 6회째 메타교훈("두 엔드포인트가 같은 자원을 다른 로직으로 판정하면 크로스-엔드포인트 테스트 필수")을 적용해, push와 IF-05를 한 시나리오에서 연결 검증할 것. 보안/성능 별도 차원 불필요.)

---

## Detected Project Type: Backend/API

판별 근거(repo 신호 — 사용자 표현 아님): `src/Wcs.Api/Controllers/RcsController.cs`(서버측 controller·`[ApiController]`·`[Route]`) + ASP.NET Core 서버 진입점(`Program.cs`·Windows Service 호스트). 브라우저 UI 트리 없음(docs의 `.html`은 스펙 정의서이지 client-rendered view 아님). 따라서 정확히 하나: **Backend/API**.

---

## Verification Scenarios (Backend/API — mandatory)

> N = 10 (이 변경 표면에서 직접 결정: push 운영상태 5축 ①online·정렬·Ready=1 ②busy ③offline ④만재여도 ⑤paused여도 + 슈트 push 현행 ⑥ + IF-05 회귀 ⑦⑧ + push 멱등/무발화 ⑨ + 무변경 가드 ⑩). 모든 시나리오는 실 DB seed·가짜 RCS 수신 본문·게이트웨이 snapshot ground-truth 사용(인메모리 카운터 단독 금지).

### Explicit list of endpoints/surfaces touched by this sprint (method + path / 산출 함수)
- **(산출 함수)** `IDestinationStatusService.Compute(destId, SORTER_3D).Ready` — 소터 ready 합성(운영상태로 좁힘). push 소비자.
- **IF-08 아웃바운드 push** `POST {RCS}/api/v1/destination-status` `{chuteNo, ready, timeStamp}` — Pusher가 `Compute().Ready` 전이 시 발화(코드 무변경, ready 의미만 바뀜).
- **IF-05** `POST /api/v1/destination-query` — 소터 availability(`r.Paused`+`SorterCanAcceptBarcode`, `r.Ready` 미소비) 회귀 확인 대상.
- (회귀 확인용) **IF-09** `POST /api/v1/arrival-report`, **IF-10** `POST /api/v1/deposit-report` — 인바운드 동작 불변.

### Happy path per surface (expected input → expected output shape)
- **VS-1 소터 online·정렬·Ready=1 → push ready=true**: snapshot(Online=true, CurFloor==운영층, Ready==1), destination NORMAL·활성, 셀 여유. `Compute(SORTER_3D).Ready==true` + 부트스트랩/전이 push payload `ready==true`(가짜 RCS 수신 본문).
- **VS-6 슈트 full/pause → push ready=false · 비움/정지해제 → true (현행 유지)**: ComputeChute 경로. 슈트 destination full(hold=Full)→push ready=false, OnCleared→ready=true; paused→false, 정지해제→true. push payload로 확인.
- **VS-8 IF-05 슈트 OK 유지**: 슈트 destination이 full/paused여도 IF-05 result=="OK"·chuteNo 반환(확정4 현행). 실 HTTP 요청→응답 JSON 증거.

### Relevant error / boundary cases per surface (적용되는 것만 — 패딩 금지)
- **VS-2 소터 busy → push ready=false**: (a) Ready==0(분류 중/이동 중) 또는 (b) CurFloor≠운영층(미정렬). 두 하위 케이스 모두 `Compute().Ready==false` + push payload `ready==false`.
- **VS-3 소터 offline → push ready=false**: 게이트웨이 번들 없음(Online=false). `Compute().Ready==false`(Reason=Offline) + push payload `ready==false`.
- **VS-4 소터 셀 만재(SorterFull=true)인데 운영상태 ready → push ready=true** [핵심 회귀]: snapshot 운영상태 OK(online·정렬·Ready=1)지만 모든 셀이 작업수량 도달(SorterFull=true). `Compute().Full==true`(IF-05/내부 사유 산출 유지)이면서 `Compute().Ready==true`(만재가 push ready에 영향 없음) + push payload `ready==true`. 실 sorter_command(COMPLETED) JOIN piece.qty로 만재 상태 ground-truth 구성.
- **VS-5 소터 PAUSED인데 운영상태 ready → push ready=true** [핵심 회귀]: destination.Status==PAUSED(또는 IsActive==false)지만 운영상태 OK. `Compute().Paused==true`(산출 유지)이면서 `Compute().Ready==true`(paused가 push ready에 영향 없음) + push payload `ready==true`.
- **VS-7 IF-05 소터 회귀 (3축)**: (a) 셀 있으면 **offline이어도 OK**(IF-05는 online을 보지 않음 — 셀 기준) (b) **paused면 NG**(소터 paused 차단 우선) (c) **만재(셀 없음)면 NG**. 세 하위 케이스 실 HTTP 요청→응답 result 확인. `r.Ready`(운영상태) 변경이 이 세 결과에 영향 없음을 입증(IF-05는 `r.Paused`+`SorterCanAcceptBarcode`만 소비).
- **VS-9 push 전이당 1회·무변화 무발화 + 만재/paused 전이 무발화 (멱등 기계 보존)**: (a) 운영상태 전이 1회당 push 정확히 1건(같은 전이를 N스레드가 동시에 관찰해도 1건 — barrier 동시관찰 프로브로 클레임 경합 입증, 중복억제 경로만으로는 불충분 — S-RCS-IF-REDESIGN-P2 교훈). (b) **운영상태 불변인 채 셀 만재 전이(빈 셀↔만재)·paused 전이가 일어나도 소터 push 0건**(WaitUntilExactAsync stableCount로 폭주 0·무발화 부재 단언 — S-M4-P3/P2 no-flood 가드). (c) 관찰 주기마다 운영상태 무변화면 push 0건.
- **VS-10 무변경 가드**: Wcs.Core·PlcGateway·Sim3ds·Data·Migrations·DestinationStatusPusher·RcsPushClient·RcsController·SorterCellQty·ChuteCapacity·EfCellSelector `git diff` 0줄. + IF-05 경로 `r.Ready` 미소비 grep 0.

---

## 미확정 질문 (사용자에게 — 추측 금지 항목)

없음. 사용자 확정(2026-06-25)으로 push=운영상태(decision.Ready)·dispatch=IF-05(셀/paused)의 2단계 게이트 분리, 슈트 push·IF-05 현행 유지, 무변경 zone 전부가 명시됨. 메모리 `if05-dispatch-vs-push-operational` 확정 사항은 재질문하지 않음.

단, **`Reason` 필드의 소터 ready=true 시 의미**(운영상태 ready지만 Full=true/Paused=true일 때 내부 DenyReason을 None으로 둘지 Full/Paused로 둘지)는 IF-05/내부 로깅이 `Reason`을 어떻게 쓰는지에 달린 **구현 세부**이므로 Generator가 기존 소비처(현재 `Reason`은 외부 미노출·IF-05 NG 필터는 Block enum 사용)와 일관되게 결정한다. push payload에는 `ready` bool만 나가므로 `Reason` 결정이 와이어에 영향 없음 — Planner는 이를 구현 결정으로 위임(WHAT 아님).

---

> Planner self-check — Detected project type: Backend/API. Required scenario slots: 10 (VS-1 happy=소터 운영ready→push true, VS-6 happy=슈트 push 현행, VS-8 happy=IF-05 슈트 OK, VS-2 error=소터 busy→false, VS-3 error=소터 offline→false, VS-4 error=소터 만재여도 push true[핵심회귀], VS-5 error=소터 paused여도 push true[핵심회귀], VS-7 error=IF-05 소터 회귀 3축, VS-9 error=push 멱등·만재/paused 무발화, VS-10 boundary=무변경 가드+grep). All slots filled: yes.
