# Sprint Feedback — S-IF08-READY-PUSH (아웃바운드 수용상태 발신 재배선: 폐지 destination-status → 확정 `PUT /api/UpdateChuteState`, 단일 발신 소스 통합)

**APPROVED** — Evaluator, 2026-07-13 (1 iteration to pass).

브랜치 `feat/if08-ready-push`. 스프린트 변경분은 전부 **미커밋 working tree**(HEAD=`2ba2c9d`, docs 병합 커밋 — orchestrator 가 승인 후 커밋). 재핸드오프 아님(HEAD 에 이 스프린트 커밋 없음 · working tree 에만 존재). Backend/API 단일 차원(functional only, 계약 선언). Evaluator 는 코드를 고치지 않음.
Ground truth = git diff/status + 코드 직접 판독 + **독립 재실행 dotnet test**(full 1× + push군 5× + E2E군 5×) + **가짜 RCS 수신 서버(FakeChuteStateServer)가 실제 수신한 JSON 본문**. Generator 요약(327 GREEN 등)은 신뢰하지 않고 전부 독립 재현.

핸드오프 확인: `tasks/sprint-log.md` L3257 에 `## IMPLEMENTATION COMPLETE — S-IF08-READY-PUSH (Generator, 2026-07-13)` 마커 존재(파일 UTF-8 — L15 의 stale S-B2B-3b 마커가 아니라 파일 최말단의 IF08 마커가 정본) → 활성화 정당.

---

## [정적] 빌드 / 테스트 — 독립 재실행 fresh evidence

- `dotnet build backend/Wcs.sln`: **오류 0**. 경고 전부 선재 **NU1903**(`SQLitePCLRaw.lib.e_sqlite3` 2.1.10 취약성 advisory) × 5 프로젝트(Wcs.Api/Data/Migrations.Sqlite/Migrations.SqlServer/Tests). 신규 경고 0. (MSBuild 요약 라인 "경고 10개" = restore/build 단계 중복 계수 — 근원은 동일 NU1903 하나. 계약 Completion #4 가 NU1903 을 선재 advisory 로 명시 제외.)
- `dotnet test backend/Wcs.sln`(full, 독립 재실행): **`통과! 실패:0 통과:327 건너뜀:0 전체:327` (18s, exit 0)**. 회귀 0·skip 0.
- **결정성(계약 Completion #2 — 실-Kestrel/폴링 I/O flake 이력[S9·testhost teardown] 대비 ≥5회 반복 필수)**:
  - push 군 `--filter FullyQualifiedName~Push|SorterCellFullness|Field20Cells` **5회 연속 = 52/52 GREEN**(각 8s). (계약 언급 42 의 상위집합 — SorterCellFullness+Field20+전 Push 클래스 포함.)
  - E2E 군 `--filter FullyQualifiedName~E2E` **5회 연속 = 50/50 GREEN**(각 14~18s).
  - flake 0. 단일 run 신뢰 회피 규칙 준수.

## [Completion #3 / VS-8] 폐지 와이어 grep-clean — PASS

- 프로덕션(`backend/src`): `destination-status|RcsPush|IRcsPushClient|DestinationStatusPushPayload|RcsPushOptions` **0건**.
- `appsettings.json`: 폐지 심볼 0건. `SorterObserveIntervalMs` 는 `Wcs:ChuteStatePush` 섹션으로 **이전됨**(SC-5) — 유일 매치.
- 테스트(`backend/tests`) 정밀 grep(`\bIRcsPushClient\b|\bDestinationStatusPushPayload\b|\bRcsPushOptions\b|\bRcsPushClient\b|destination-status`): **0건**. 잔존 매치는 전부 `DestinationStatusPusher`(SC-3 통합 단일 소스 — 존치 심볼, 폐지 아님). 픽스처명 `RcsPushWebApplicationFactory`·파일명 `RcsPushTests.cs` 는 SC-8 이 명시 존치를 요구한 산물(폐지 심볼 아님). "gone everywhere" 확정.
- 삭제 확인(git status): `RcsPushClient.cs` (D), `ChuteStatePusher.cs` (D).

## [Completion #5] 마이그레이션 0 · [Completion #6] 인프라 격리 — PASS

- `git status`: migration/snapshot 파일 변경 **0건**. 스키마 무접촉.
- 무접촉 재확인: `Wcs.PlcGateway`·`Wcs.Core`·`Wcs.Data` diff **0**(git status 에 해당 경로 변경 없음 — 절대규칙 #1/#2/#3/#4/#5, SC scope-OUT 준수).
- 검증 인프라: FakeChuteStateServer(Kestrel `127.0.0.1:0` 동적 포트) + E2E Sim3ds(`GetFreePort()` 동적 TCP `127.0.0.1`) + in-memory SQLite(named, per-factory Guid). **COM1/RTU·실 PLC·원격/현장 DB 무접촉 확인**(고정 :1502/:5205 미사용 — 전부 동적/loopback).

---

## [★★★ API 계약 정합성] — PASS

- **와이어 형태(VS-9)**: 가짜 수신 서버가 기록한 실제 요청으로 입증 — `last.Method=="PUT"`(positive), RawBody 키 `chute_numbers`/`next_states` **Contains** ∧ `chuteNumbers`/`nextStates`/`chuteNo`/`ready`/`timeStamp` **DoesNotContain**(camelCase·폐지키 함정 차단), 두 배열 동일 길이·인덱스 정렬·길이 1, 값 ∈ {2,3}. (RcsPushTests.PUSH6_7 + ChuteStatePushClientTests.CS_PUSH_7.)
- **상태 매핑(SC-2)**: `NextStateOpen=3`(accept)/`NextStatePause=2` 상수 LOCKED. accept 합성 = `Compute().Ready && !Compute().Paused` — 코드 직접 확인(DestinationStatusPusher.ComputeAccept).
  - 슈트: `Compute().Ready = !full && !paused` → accept = !full && !paused(만재·정지 반영).
  - 소터: `Compute().Ready = decision.Ready`(online·정렬·Ready==1, SorterFull·paused 제외) → accept = 운영 ready && !paused. **paused 재접힘 + SorterFull 제외** 확인.
- **성공/실패 판정**: 2xx && `flag==1` 성공 / `{result:"Failed"}`·flag≠1·비2xx 실패(status code 만 보지 않음). ChuteStatePushClient.IsSuccessBody 코드 확인 + CS_PUSH_4/5 실증.

## [★★★ 아키텍처 — 단일 발신 소스·동시성 멱등] — PASS (코드 직접 검사)

- **단일 소스(SC-3/VS-7)**: 변화원 3종(슈트 capacity 콜백 ① · 소터 스냅샷 관찰 타이머 ② · 운영자 `OnTransition` ③)이 전부 `Observe → PumpAsync` 단일 경로로 수렴. 발신값은 항상 단일 술어 `ComputeAccept`(현재 DB Status 직접 read) 산출 — 갈라진 두 소스(구 DestinationStatusPusher ready + ChuteStatePusher pause) 는 `ChuteStatePusher.cs` **삭제**로 소멸. 같은 chuteNo 에 한쪽 3·다른 쪽 2 모순 발신이 **구조적으로 불가**.
  - 실증: PUSH4(16스레드 동시 통지 → 정확히 1건) · SorterPushOperational VS-9a(N스레드 동시 관찰 → 1건) · CS_PUSH_3(정지 중 관찰 타이머 계속 돌아도 모순 3 발신 0 — stableCount 10회 관측).
- **전이당 1회 멱등(중복 0·누락 0)**: `PumpAsync` per-dest `Gate` 락에서 `PushInFlight` 클레임 + `Acked==Computed` 값기반 억제 → I/O 는 락 밖 → 완료 후 락에서 `Acked` 갱신 + 재평가. 동시 클레임은 락 직렬화로 1개만 성공(나머지 즉시 반환). 성공만 `Acked` 갱신, 실패 시 `Acked` 불변 → 다음 관찰이 재푸시(복구). **비원자 check-then-act 없음** — 클레임·판정·Acked 갱신 전부 동일 락 안.
- **관심사 분리**: 전이 감지(DestinationStatusPusher) ↔ 1건 전송+지수 백오프 재시도+Fail-Loud(ChuteStatePushClient) 분리 유지.
- **teardown 경쟁 방어**: `StopAsync` `Interlocked.Exchange(_stopped)` 1회 실행 · CTS `CancelAsync` ObjectDisposedException catch · observe task await 예외 흡수. `PumpAsync` OperationCanceledException catch(PushInFlight 리셋) · `_cts?.Token ?? None`. 픽스처 `_fakeWriteQueue.Writer.TryComplete()` before DisposeAsync(testhost teardown 패턴 준수).

## [★★ Craft] — PASS

- 하드코딩 0: BaseUrl·Path·RetryCount·RetryBaseDelayMs·RetryMaxDelayMs·HttpTimeoutMs·SorterObserveIntervalMs 전부 `ChuteStatePushOptions`(appsettings). URL/타이밍 리터럴 0(절대규칙 #7). 백오프 shift overflow 가드(`Min(attempt-1,30)`) 존재.
- DORMANT: `BaseUrl` null → `IsEnabled=false` → StartAsync 경고 후 전체 비활성(구독 안 함·관찰 타이머 미기동·HTTP 0·크래시 0). 클라이언트도 방어적 재확인 false.
- Fail-Loud: 재시도 소진 시 명시 ERROR 로깅 + false 반환 + operation_log WARN 부수 기록. 성공 위장 0.

## [★★ Functionality] — PASS

- IF-05 dispatch 회귀 0: 소터 2단계 게이트 분리 보존 — push ready(운영상태)는 SorterFull/Paused 무반영, IF-05 는 `SorterCanAcceptBarcode`+`r.Paused` 소비. SorterCellFullness EC-1/EC-2/EC-3(만재·PAUSED → IF-05 NG) + SorterPushOperational VS7 실증.

---

## Verification Scenarios — VS-1 ~ VS-11 (전부 자동 xUnit · 가짜 수신 본문 입증)

| VS | 내용 | 커버 테스트(가짜 수신 본문 단언) | 판정 |
|----|------|-------------------------------|------|
| VS-1 | 소터 분류 사이클 2→3→2 전이당 1건·무변화 폭주 0 | RcsPushTests.PUSH2_3 (부트1+전이2=3건, stableCount 6) | **PASS** |
| VS-2 | 기동 부트스트랩 전 활성 목적지 1회 | RcsPushTests.PUSH6_7 (슈트5+PAUSED1+소터1=7건, 무변화 안정) | **PASS** |
| VS-3 | 운영자 pause 합성(운영 ready여도 paused→2) | ChuteStatePushObserverTests.CS_PUSH_3 + SorterPushOperational VS-5 (`Compute().Ready=true`∧`Paused=true`이나 발신 2) | **PASS** |
| VS-4 | 슈트 만재/정지→2, 해소→3 | RcsPushTests.PUSH1 (3→2→3) + CS_PUSH_1/CS_PUSH_2 | **PASS** |
| VS-5 | 소터 셀 만재 무영향(발신 3 유지)+IF-05 여전히 차단 | 발신측 SorterPushOperational VS-9b/VS-4(만재 전이 push 0·last=3) · IF-05측 SorterCellFullness EC-1/EC-2 + VS-7(만재 NG) | **PASS** |
| VS-6 | DORMANT: 발신 0·크래시 0·인바운드 정상 | RcsPushTests.PUSH8 + CS_PUSH_6/CS_PUSH_6c (전이 다수에도 수신 0, IF-05 200) | **PASS** |
| VS-7 | 단일 소스·이중/모순 발신 금지 | RcsPushTests.PUSH4(동시16→1) + CS_PUSH_3(no-contradiction) + VS-9a | **PASS** |
| VS-8 | 폐지 와이어 완전 제거(grep) | 프로덕션+appsettings+테스트 정밀 grep 0건 | **PASS** |
| VS-9 | 와이어 형태(PUT·snake_case·인덱스정렬·{2,3}) | RcsPushTests.PUSH6_7 + CS_PUSH_7 (RawBody 파싱 이중 단언) | **PASS** |
| VS-10 | 전체 스위트 GREEN(0 회귀) 독립 실행 | full 327/327 + push 52×5 + E2E 50×5 GREEN | **PASS** |
| VS-11 | RCS 미도달→재시도(설정3)→복구 후 최신값 도달(Fail-Loud) | RcsPushTests.PUSH5(503 중 성공 0→복구 재푸시 최신 2) + CS_PUSH_5(503/Failed/flag0→false, 복구 true) | **PASS** |

**모든 Completion Condition(1~6) 충족. 11개 VS 전부 PASS. 동시성 정합(단일소스·전이당1회·teardown) 코드 직접 검사 통과.**

---

## Repeat detection

- 이 스프린트는 feedback-archive S-CHUTESTATE-PUSH 가 확립한 교훈(가짜 수신 서버 본문 = 유일 신뢰 증거 · snake_case JsonPropertyName 캡처 이중단언 · DORMANT 3중 실증 · 관찰-전용 additive 코어 무변경 · 실-Kestrel/폴링 ≥5× 결정성 · 스택 PR 금지/현장 DB 무접촉)을 **정확히 계승·적용**했다. 반복된 **결함**은 0 → lessons.md 승격 불요(반복 교훈은 반복된 실패에 한함).

## Minor (비블로킹 — 다음 sprint Generator 참고, APPROVED 불변)

- **소터는 관찰 타이머(SorterObserveIntervalMs)로 실패 후 자동 재푸시가 보장되나, 슈트는 주기 관찰이 없어 push 최종 실패 후 재푸시가 "다음 capacity 이벤트/운영자 전이"에 의존**한다(주기적 reconcile 부재). 이는 계약 정합(SC-6 전이-트리거 · SC-7 클라 레벨 3회 재시도 · VS-11 이 subsequent 전이로 복구 입증 · 주기 reconcile/coalescing 은 명시 scope-OUT)이며 결함 아님. 다만 RCS 장기 다운 중 슈트 상태가 다음 슈트 이벤트까지 stale 로 남을 수 있으므로, 후속 배치/주기 reconcile 스프린트에서 슈트 주기 재푸시(또는 부트스트랩성 재동기) 도입을 고려할 것.
- S-CHUTESTATE-PUSH Step 4.5 에서 이연된 항목(활성화 시 재동기화 협의 · `DetailJson` 수동 보간 STJ 통일 등)은 이 스프린트가 재도입하지 않았고 여전히 이연 상태 — 활성화(고객사 host 제공) 시점에 함께 처리.

→ **결론: functional 단일 차원 PASS, 계약 Completion 1~6 + VS-1~11 + 와이어/동시성 전부 충족. APPROVED.**

**APPROVED — S-IF08-READY-PUSH**

## Code Review Pass (Step 4.5 — 독립 리뷰, 2026-07-13)

**판정: Ready to merge = Yes. Critical/BLOCKING 0.**

강점: 계약 바이트 정합([JsonPropertyName] snake_case 강제·단일 Compute 호출 합성·IsSuccessBody 엄격 판정),
단일 발신 소스가 "운 좋게"가 아니라 구조적으로 건전(per-dest Gate + PushInFlight claim + 성공 후
Acked≠Computed 재수렴 루프 — 모순 발신 불가·최종상태 유실 창 없음), Fail-Loud 보존, 테스트가 실수신
JSON으로 입증(WaitUntilExact 결정성 폴링·bare sleep 없음).

### Minor (비블로킹 — 다음 sprint Generator 참고)
1. **[Important→후속] 슈트 복구 하트비트 부재** — DestinationStatusPusher.cs:242-244(관찰 루프 소터
   전용)+:361-366. 슈트 FULL 전이 push가 RCS 장애로 재시도 소진되면, 만재라 후속 용량 이벤트도 없어
   RCS가 "받을 수 있음"으로 무기한 오인 가능. 선재 동작(Phase 2 이월)·계약 VS-11 허용 — 병합 비차단.
   후속: 관찰 루프에 "Acked≠Computed인 슈트 재평가"(chute Compute=인메모리 GetHold라 비용 ~0) 확장.
2. Program.cs:198 `IDestinationChangeNotifier` DI 등록이 사장(이벤트 구독으로 대체됨·선재) — 제거 후보.
3. 구 `Wcs:RcsPush` 키가 남은 수기 appsettings는 조용히 무시됨(진단 없음) — 양 와이어 DORMANT 출하라 수용.
4. StartAsync에서 `_cts` 생성(:161)이 부트스트랩 루프(:153-158) 뒤 — 부트스트랩 발신이 CancellationToken.None로
   나감(영향 극소). `_cts` 생성을 부트스트랩 앞으로 재배치 후보.
