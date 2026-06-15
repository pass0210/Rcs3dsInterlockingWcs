# Sprint Feedback — S-M2 (PLC 게이트웨이 + 시뮬레이터 핸드셰이크)

## 판정 (재검증 #4 — 코드리뷰 수정 반영): APPROVED (유지)

코드리뷰 후속 수정 ground truth 재검증. 핵심: 락 밖 `_client` 접근(off-lock Disconnect) 제거 확인 + 회귀/데드락 없음.

### 재검증 #4 증거
- **build**: exit 0, 경고 0 / 오류 0.
- **test**: `dotnet test Wcs.sln` → **5회 연속 24/24 GREEN, 실패 0**(IT-4b 서버 단절·재기동 타이밍 flaky/데드락 배제).
  split: Decider 15(M1 회귀 0) + 통합 9(IT-4b 신규). 총 24.
- **off-lock 접근 해소**: 폴 catch의 `TryReconnect()`(=`_client.Disconnect()`)를 `_clientLock` 임계구역으로 이동
  (PlcGateway.cs L233-235 `WaitAsync`→`try TryReconnect()`→`finally Release`). 전 `_client.` 11개소 감사 결과
  모두 락 보호 또는 종료 후 단일스레드 경로(StopAsync L163·DisposeAsync L169 — 두 태스크 await 완료 후). 락 밖 접근 0.
  데드락 없음(finally Release 보장, 재연결은 보유 중 재획득 안 함). catch 내 WaitAsync는 ct 취소 시 OCE로 루프 종료 — 정상.
- **죽은 코드 제거**: `_writeCompletionTcs`/`_tcsDoor`/`WaitNextWriteCompletionAsync` 및 컨슈머 finally 삭제(grep 0건).
- **주석 정정**: 클래스 "BackgroundService→수동 StartAsync/StopAsync(M3 IHostedService)" / SimServer `InjectNoResponse`
  "OFFLINE 유발→RFlagTimeout 유발, 폴 응답 계속→Online 유지"(실제 동작과 일치하도록 정정 — 적절).
- **IT-4b 회귀 가드**: `IT4b_WritesDuringReconnect_NoCorruption` — 핸드셰이크 진행 중 서버 단절·재기동 후
  후속 핸드셰이크 Success + R_Seq==C_Seq 단언(첫 건은 Success/Offline/Timeout 허용 — 타이밍 비결정 합리적). 빈 단언 아님.
- 범위 클린: Core·Api.cs·Data·DepositDeciderTests 무변경.

→ 이전 APPROVED 유지 + 코드리뷰 결함 0 확인.

---

## 판정 (재검증 #3): APPROVED

3회차 재제출에서 FAIL-1·FAIL-2 모두 해소 확인. Evaluator ground truth 재검증(요약 불신, 명령 직접 재실행 + 소스 검사).

### 최종 증거
- **build**: `dotnet build Wcs.sln` → exit 0, 경고 0 / 오류 0.
- **test**: `dotnet test Wcs.sln` → **4회 연속 23/23 GREEN, 실패 0**(동시성·락 변경의 데드락/flaky 배제).
  split: `--filter Decider` 15(M1 회귀 0) + `~PlcGatewayIntegrationTests` 8(IT-3c 신규 추가). 총 23.
- **FAIL-1 해소**: `SimServer.StartAsync`가 동기(`return Task.CompletedTask`), `Task.Delay(80)` 제거.
  src 전체 하드코딩 numeric `Task.Delay` 0건(grep). GW `WaitUntilAsync(Online)` 폴링이 기동 대기 흡수.
- **FAIL-2 해소**: `PlcPollingService._clientLock = SemaphoreSlim(1,1)`(L107) 도입.
  - 폴 읽기: `WaitAsync`(L190)→`EnsureConnected`+`ReadHoldingRegistersUInt16Async`→`finally Release`(L202).
  - 쓰기 컨슈머: `WaitAsync`(L307)→`EnsureConnected`+`switch`(전 Write*)→`finally Release`(L359).
    early `return`(SetTgtFloor/CellAssign skip)도 finally로 Release 보장 — 락 누수 없음.
  - `RmwD4LockedAsync`(개명): read+write가 컨슈머 임계구역 안에서 원자 실행, 재획득 없음 → 데드락 없음.
  - `DisposeAsync`에서 `_clientLock.Dispose()`(L173). 폴 예외 시 finally Release 후 OFFLINE 전이 — E7 보존.
- **IT-3c 회귀 가드**: `IT3c_ConcurrentPollAndWrite_NoFrameCorruption` — 폴 진행 중 3건 연속 핸드셰이크
  각 Success + R_Seq==C_Seq 단언(빈 단언 아님). poll-vs-write 프레임 무결성 동작 입증.

### Error case 재확인 (전부 배제 유지)
E1 단일 컨슈머 쓰기 / E2 RMW `(current|set)&~clear` 비트 보존 / E3 §6 분류·이동 직렬·Ready 블립 없음 /
E4 SetTgtFloor TgtFloor≠0 스킵·WCS TgtFloor 클리어 없음 / E5 Core·Api.cs·Data·DepositDeciderTests 무변경·하드코딩 시간값 0 /
E6 P1/P2 보수(알람+상태재확인까지만, 재시도/리셋 미구현) / E7 OFFLINE 명시 전이·예외 비삼킴. 모두 PASS.

### 잔여 minor (차단 아님)
- `src/Wcs.Sim3ds/Program.cs` 옵션 리터럴이 appsettings 미바인딩 — 독립 실행 entrypoint(테스트 표면 밖).
  M3 배선 시 IConfiguration 바인딩으로 정리 권장.

→ Completion Conditions 전부 충족. **APPROVED.**

---

## (재검증 #2 기록 — 보존)

## 판정 (재검증 #2): FAIL — FAIL-1 해소, FAIL-2 미해소

재제출 검증(ground truth): build exit 0(경고/오류 0), `dotnet test Wcs.sln` **3회 연속 22/22 GREEN**(flaky 아님).
- **FAIL-1 (매직넘버 고정 sleep) — 해소됨.** `SimServer.cs:95-96`에서 `Task.Delay(80)` 제거,
  GW `WaitUntilAsync(Online)` 폴링이 흡수. 회귀 없음(3회 GREEN로 입증).
- **FAIL-2 (단일 소켓 read/write 경합) — 미해소(필수 차단 유지).**
  `PlcGateway.cs` 재검사: `_client` 직렬화 프리미티브(`SemaphoreSlim`/`Mutex`/`lock`/`_ioGate`) 전무
  (`lock`은 `_tcsDoor` 2곳뿐 — 테스트 TCS용, 소켓 무관). L145-146 `_pollTask`/`_writeTask` 두 태스크가
  여전히 같은 `_client`를 동시 사용: 폴 읽기 L224 ↔ 쓰기 L301/315/326 ↔ RMW read+write L346/355. 결함 그대로.
  재제출 메시지에도 FAIL-2 언급 없음. 아래 FAIL-2 수정 지침대로 처리 후 재제출 요망.

---

## (1차 검증 기록 — 보존)

Evaluator가 GROUND TRUTH로 검증함 — Generator 요약 불신, 모든 명령 직접 재실행 + 소스 검사.
**핵심 메커니즘·절대규칙·통합 시나리오는 모두 통과**했으나, 하드코딩 고정 sleep 1건(매직넘버)과
동시 트랜잭션 안전성 결함 1건으로 FAIL. 둘 다 국소적·실행가능한 수정.

---

## Verification Scenarios (PASS)

### V1 (build) — PASS
`cd "<proj>" && dotnet build Wcs.sln` (Bash 경유):
```
빌드했습니다.
    경고 0개
    오류 0개
경과 시간: 00:00:04.54
BUILD_EXIT=0
```
exit 0, 경고 0 / 오류 0. PASS.

### V2 (full test GREEN, flaky 아님) — PASS
`dotnet test Wcs.sln --no-build` **3회 연속 실행** 전부 동일:
```
RUN 1: 통과!  - 실패: 0, 통과: 22, 전체: 22, 기간: 2 s
RUN 2: 통과!  - 실패: 0, 통과: 22, 전체: 22, 기간: 2 s
RUN 3: 통과!  - 실패: 0, 통과: 22, 전체: 22, 기간: 2 s
```
구성 검증(필터):
- `--filter Decider` → 15 통과 (M1 회귀 0)
- `--filter ~PlcGatewayIntegrationTests` → 7 통과 (IT1·IT2a·IT2b·IT3a·IT3b·IT4·IT5)
3회 모두 GREEN, 시간 일정 → 현재 규모에서 flaky 아님. PASS.

### V3 (시나리오 충실) — PASS
소스 검사(PlcGatewayIntegrationTests.cs) — 빈 단언/이름만 통과 아님 확인:
- IT-1: 결과=Success 단언 + SentCSeq==ReceivedRSeq + WaitUntil로 종료 시 C_Flag·R_Flag=0 + 타임라인 NotEmpty. 실제 왕복.
- IT-2a 일치=Success / IT-2b `InjectRSeqOverride=999`→RSeqMismatch·ReceivedRSeq==999·Detail "mismatch". 실제 고장주입.
- IT-3a `InjectRSeqOverride` 없이 CellAssign→R_Flag=1 시 Ready=1 단언으로 RMW 비트 보존 입증. IT-3b SetTgtFloor(2) 후 SetTgtFloor(99) 스킵→TgtFloor==2 유지(PollForDuration 후).
- IT-4 서버 Stop→`!Online` 대기(timeout=WriteTimeoutMs*(N+1)+여유, 설정 유도값)→재기동→Online 복구.
- IT-5 RFlagTimeoutMs=300 + InjectRFlagDelayMs=5000→RFlagTimeout·Detail "RFLAG_TIMEOUT". P1까지만 단언(재시도 단언 없음 — 적절).
PASS.

---

## Error cases (배제 결과)

- **E1 절대규칙 #1 (단일 큐)** — PASS(배제). 솔루션 전체 Modbus 쓰기 호출부 4건 전부 PlcGateway.cs 내부:
  L301/L315/L326(ProcessWriteAsync) + L355(RmwD4Async). ProcessWriteAsync는 RunWriteConsumerAsync에서만,
  RmwD4Async는 ProcessWriteAsync에서만 호출. Channel `SingleReader=true`, 단일 리더는 RunWriteConsumerAsync뿐.
  HandshakeOrchestrator는 EnqueueAsync만 호출(L101·L185·L193). 단일 쓰기 컨슈머 구조적 강제됨.
- **E2 RMW** — PASS(배제). `RmwD4Async`: `(ushort)((current | set) & ~clear)` (PlcGateway.cs L352).
  RegisterMap D4 비트 C_Flag=1<<0·R_Flag=1<<1·Ready=1<<2 확인. set/clear 외 비트 보존. IT-3a가 Ready 보존 동작 입증.
- **E3 §6 정정(분류·이동 직렬, Ready 블립 금지)** — PASS(배제). SimServer `_isSorting`/`_isMoving` 플래그로 직렬화.
  C_Flag 감지 조건 `cFlag && !sorting && !moving`(L159). 분류 종료 시 복귀 있으면 Ready=0 유지한 채 이동(L255-257),
  없으면 Ready=1 1회 set(L264) — 중간 1→0→1 블립 없음.
- **E4 핑퐁/클리어** — PASS(배제). SetTgtFloor는 `_latest.TgtFloor != 0`이면 return(L296-300). WCS 코드에 TgtFloor=0 쓰기 없음.
  TgtFloor=0 클리어는 Sim의 분류 시작 시점에서만(SimServer L209).
- **E5 하드코딩/범위(범위)** — PASS(배제). `git diff` 코드 surface: Wcs.PlcGateway/*, Wcs.Sim3ds/*, appsettings.json(키 추가만),
  Wcs.Tests/*. Wcs.Core / Wcs.Api/*.cs / Wcs.Data **무변경**(`git diff --stat HEAD` 확인). DepositDeciderTests.cs 무변경.
  appsettings에 CFlagTimeoutMs·Sim3ds.* 키 추가만. → 범위는 PASS. (단, 하드코딩 시간값은 아래 FAIL 참조)
- **E6 §7-A 과구현** — PASS(배제). P1(RFlagTimeout): 알람 로그 + Online·Ready 재확인 + 결과 반환까지만(HandshakeOrchestrator L201-211,
  재시도/포기 미구현). P2(CFlagTimeout): 알람 + 상태 재확인까지만(L129-137). 추측 동작 없음.
- **E7 OFFLINE 예외삼킴** — PASS(배제). 폴 실패 시 LogWarning/LogError 후 `PublishOffline()`로 Online=false 명시 전이(L211-216).
  소켓 예외 즉시 OFFLINE. 쓰기 컨슈머 예외도 LogError + PublishOffline(L270-274). 조용한 무시 없음.

---

## FAIL 항목 (수정 필요 — 둘 다 국소)

### FAIL-1 (E5 + NOTE "매직넘버 시간값 통과해도 FAIL") — 하드코딩 고정 sleep
- **위치**: `src/Wcs.Sim3ds/SimServer.cs:97`
  ```csharp
  // 서버가 포트를 수신 준비할 때까지 짧게 대기
  await Task.Delay(80, outerCt).ConfigureAwait(false);
  ```
- **Expected**: 절대규칙 #7 "모든 시간값 appsettings — 하드코딩 금지" + 계약 장인성 기준 #3
  "고정 sleep 대신 폴링/대기 — flaky 회피". 시간값은 설정 주입.
- **Actual**: `80`이 소스에 직접 박힌 매직넘버 고정 sleep. 서버 수신 준비 대기를 임의 80ms로 가정.
  CI/부하 환경에서 80ms 안에 리스닝 안 되면 클라이언트 첫 연결 실패 → 잠재 flaky(현재 통과는 운).
- **수정 지침**:
  - 최소: `SimServer.Options`에 키 추가(예 `StartupSettleMs`, 기본 80) — 매직넘버 제거. appsettings `Sim3ds`에도 대응 키.
  - 권장: 고정 sleep 제거하고 `_server.Start(ep)` 후 실제 수신 가능 여부를 짧은 폴링/재시도로 확인.
    (GW측은 이미 `WaitUntilAsync(() => _gw.Latest.Online)`로 폴링 대기 중이므로, Sim의 고정 sleep은
     테스트에 사실상 불필요 — StartAsync에서 제거해도 GW Online 폴링이 흡수 가능한지 확인 후 제거 권장.)

### FAIL-2 (장인성 ★★ / 아키텍처 ★★★ — 동시 트랜잭션 안전성) — 단일 소켓 read/write 경합
- **위치**: `src/Wcs.PlcGateway/PlcGateway.cs:145-146`
  ```csharp
  _pollTask  = Task.Run(() => RunPollLoopAsync(_cts.Token));      // _client 읽기 (L224)
  _writeTask = Task.Run(() => RunWriteConsumerAsync(_cts.Token)); // _client 쓰기 (L301/315/326) + RMW read+write (L346/355)
  ```
- **Expected**: 절대규칙 주석 "동시 쓰기는 경합을 일으킨다" 의 취지 — 단일 `ModbusTcpClient`(단일 TCP 소켓·
  단일 트랜잭션 버퍼)에서 트랜잭션이 직렬화되어야 프레임 손상/응답 교차가 없다. FluentModbus `ModbusTcpClient`는
  **단일 트랜잭션 클라이언트로 동시 호출에 thread-safe 하지 않음**.
- **Actual**: 폴 루프(읽기)와 쓰기 컨슈머(읽기+쓰기 RMW 포함)가 **같은 `_client`를 동시에** 사용하며
  둘 사이 직렬화(lock/SemaphoreSlim) 없음. 폴 주기(30~150ms)와 쓰기/RMW가 겹치는 순간 같은 소켓에서
  요청/응답 프레임이 교차할 수 있음(트랜잭션 ID 혼선·버퍼 경합). 현재 통과는 로컬 sim·짧은 윈도우로 인한 운이며,
  부하/지연 시 간헐 예외 또는 잘못된 스냅샷 → 이후 OFFLINE 오판/RMW 손상으로 번질 수 있음.
  ※ 절대규칙 #1(쓰기 단일 큐)·E2(RMW 비트 보존)는 *쓰기끼리는* 단일 컨슈머라 직렬이지만,
    **읽기(폴)와 쓰기가 별 태스크라 소켓 레벨에서 직렬이 아님** — 이 부분이 결함.
- **수정 지침** (택1):
  - (A) 폴 읽기와 쓰기 컨슈머가 공유하는 `SemaphoreSlim _ioGate = new(1,1)`로 모든 `_client` 트랜잭션
    (ReadHoldingRegisters / Write* / RMW read+write 묶음)을 감싸 직렬화. RMW는 read+write 한 쌍을 한 임계구역으로.
  - (B) 또는 폴링도 쓰기 큐 컨슈머와 동일 단일 루프/단일 태스크로 통합해 소켓 접근점을 하나로.
    (단일 컨슈머가 폴+쓰기를 번갈아 수행) — 절대규칙 #1 정신과 더 일치.
  - 회귀 방지: 동시 부하(폴 진행 중 다수 CellAssign/ClearR 연속 투입) 하에서도 스냅샷·RMW 무결성 유지되는
    통합 테스트 1건 추가 권장(IT-3 확장).

---

## Completion Conditions 대비
- build exit 0 / test 22 GREEN(3회) — 충족
- 통합 시나리오 IT-1~5 자동화 GREEN — 충족
- 절대규칙(단일 큐 쓰기·RMW·TgtFloor≠0 스킵·WCS 클리어 안 함) — 충족
- **하드코딩 시간값 0 — 미충족(FAIL-1: SimServer.cs:97 `Task.Delay(80)`)**
- Wcs.Core·Wcs.Api·Wcs.Data 무변경 — 충족
- 레지스터 타임라인 로그 출력 — 충족(SimServer.LogTimeline, 테스트 DisposeAsync에서 출력)

→ FAIL-1(필수: 매직넘버 제거) + FAIL-2(소켓 트랜잭션 직렬화) 수정 후 재제출.

## 참고 (minor, 차단 아님 — 여력 시)
- `src/Wcs.Sim3ds/Program.cs:13-16` 옵션값(TiltDelay/SortDuration/MoveDuration)이 appsettings 바인딩 없이
  소스 리터럴. 독립 실행 entrypoint라 테스트 표면 밖이나, "설정 주입" 일관성 위해 IConfiguration 바인딩 권장.
