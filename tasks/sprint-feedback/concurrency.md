# Sprint Feedback — S-AUDIT-D-HANDSHAKE-HARDENING
## Dimension: Concurrency / Timing (D② CFlagTimeout 결정성 + 실-Sim 타이밍 회귀 + flake 배제 + arming 불변식 보존)

**VERDICT: PASS**

Evaluator: concurrency/timing dimension (expert pool). 평가 범위 = 계약 §Evaluation Dimensions 2 —
D② CFlagTimeout '단독' 결정성(±ε·무한대기 배제), 실-Sim 잔류/타이밍 회귀 0, flake 배제(다회 반복),
arming 불변식·핸드셰이크 제어흐름 보존. 모든 증거는 이번 세션에서 직접 실행한 fresh tool output. 코드 미수정.
(D④ 원인분리·멱등 시맨틱·로그 정직성은 functional 차원 소관 — 본 리포트 범위 밖.)

---

### 0. Handoff · Ground-truth 확인
- `tasks/sprint-log.md` → `## IMPLEMENTATION COMPLETE (Generator, 2026-08-03)` 마커 존재 확인.
- 변경 표면 git 직접 판독(`git diff HEAD --stat`): 프로덕션 변경은 D④(RcsController/DbRepositories/Repositories)뿐.
  D② 관련 프로덕션 코드(`HandshakeOrchestrator.cs`·`PlcGateway.cs`) **diff 완전 비어있음(0 바이트)** — 핸드셰이크
  제어흐름·arming 무변경 물리 확인. Sim3ds `SetCResidue`는 `SetRResidue`와 동형 test-only 추가 헬퍼(런타임 경로 무접촉).
- 빌드: `dotnet build backend/Wcs.sln -c Debug` → 오류 0 / 경고 10(전부 선재 NU1903 SQLite 취약성). **신규 CS 경고 0**.

---

### 1. D② VS-1 — CFlagTimeout '단독' 결정성 (오케스트레이터 단위, 직접 재실행)

테스트: `CFlagTimeoutTests.VS1_CFlagResidue_Unconsumed_CFlagTimeoutAlone_Bounded_NoCWritten`
(실 SimServer + PlcPollingService + HandshakeOrchestrator 하니스, `[Collection("RealSimSerial")]`).

**fresh 실측 출력 (verbosity=detailed):**
```
[VS-1] Outcome=CFlagTimeout elapsed=508ms Detail=C_Flag still set after 500ms. Online=True Ready=True
```

- **단독성**: `Assert.Equal(HandshakeOutcome.CFlagTimeout, result.Outcome)` — 택일(CFLAG||RFLAG) 아닌 **정확히 CFlagTimeout 단독**. 실측 Outcome=CFlagTimeout.
- **무한대기 배제(±ε)**: elapsed=**508ms**, 주입 상한 CFlagTimeoutMs=500. 하한 400ms(실제 대기 실증) ≤ 508 ≤ 상한 3000ms.
  ε≈8ms — 상한 근처에서 **결정적으로 유계**. 즉시반환도 무한대기도 아님.
- **fallthrough 배제 검증(스켑틱)**: 하니스 RFlagTimeoutMs=3000. 만약 C_Flag 대기가 R 폴로 흘러갔다면 경과 ≈ 500(cflag)+3000(rflag)=~3500ms > 상한 3000 → 상한 단언 FAIL. 실측 508ms + Outcome==CFlagTimeout(RFlag 아님)이 fallthrough를 이중 배제.
- **진짜 C_Flag 타임아웃(OFFLINE 위장 아님)**: Detail `Online=True Ready=True` — 폴 응답은 계속(Online 유지)인데 C_Flag=1 미소비로 상한 초과. OFFLINE fallthrough였다면 Outcome=Offline로 단언 FAIL.
- **C 미기입**: `DoesNotContain HS_C_SENT` + `DoesNotContain CELL_ASSIGN`(WaitCFlagZeroAsync가 CellAssign 전 종결). 단언 통과.
- **단계 단독**: `Contains HS_CFLAG_TIMEOUT` + `DoesNotContain HS_R_RECV/HS_TIMEOUT(RFLAG)/HS_RSEQ_MISMATCH`. 통과.
- **설정 주입(#7)**: CFlagTimeoutMs=500을 `PlcGatewayOptions` 생성자로 주입. appsettings.json 실키 `Timing:CFlagTimeoutMs`(기본 5000) 확인 — 하드코딩 아님.
- 소스 경로 검증: `HandshakeOrchestrator.WaitCFlagZeroAsync`(:326-358)는 `deadline = Now + _opt.CFlagTimeoutMs`, C_Flag=1 지속 시 `HandshakeOutcome.CFlagTimeout` 반환 — R 폴(WaitRFlagAndProcessAsync :362) **이전** 단계라 CFLAG가 RFLAG로 새지 않음.

### 2. D② VS-2 — IF-10 유발 CFlagTimeout → alarm 'CFLAG_TIMEOUT' 단독 (API/영속화, 직접 재실행)

테스트: `E2EGroupCD_AlignHandshakeTests.D5b_CFlagTimeout_Deterministic_CflagAlarmAlone_TimeoutMapping`.

**fresh 실측 출력:**
```
[IF-11] 핸드셰이크 완료: pId=24050 cellNo=1 outcome=CFlagTimeout destId=6
[D5b VS-2] CFlagTimeout '단독' — alarm codes=CFLAG_TIMEOUT / sorter_command=TIMEOUT / piece=TIMEOUT
```

- **alarm 단독**: `Assert.Contains("CFLAG_TIMEOUT")` + `Assert.DoesNotContain("RFLAG_TIMEOUT")`. 실측 alarm codes = `CFLAG_TIMEOUT` 만(RFLAG_TIMEOUT 부재). 기존 D5의 `CFLAG||RFLAG` 택일 모호성 **실제로 제거**됨 — C_Flag 무한대기 회귀를 이제 잡는다.
- **현동작 고정 매핑**: sorter_command status=**TIMEOUT**, piece status=**TIMEOUT**. IF-11 로그가 `outcome=CFlagTimeout`로 핸드셰이크가 진짜 CFlagTimeout로 종결했음을 교차 확인.
- **결정성 장치**: `SetCResidue(C_Flag=1)` + `InjectNoResponse`(상태기계 정지)로 C_Flag 미소비 강제 + `UntilExactAsync(stableCount=4)`로 WCS가 C_Flag=1 **4연속 안정** 관찰 후 진행 → StartupClear 잔여 창 배제. CFlagTimeoutMs=500 주입.

### 3. flake 배제 — 타이밍/실-Sim 신규+회귀 **6회 반복** (1회 GREEN PASS 아님)

필터 = `CFlagTimeoutTests | HandshakeResidueTests | StartupClearTests | D5b_CFlagTimeout` (13 테스트), `--no-build` 독립 재실행:

| iter | 결과 | 시간 |
|---|---|---|
| 1 | 실패:0 통과:13 건너뜀:0 | 7s |
| 2 | 실패:0 통과:13 건너뜀:0 | 6s |
| 3 | 실패:0 통과:13 건너뜀:0 | 6s |
| 4 | 실패:0 통과:13 건너뜀:0 | 6s |
| 5 | 실패:0 통과:13 건너뜀:0 | 6s |
| 6 | 실패:0 통과:13 건너뜀:0 | 6s |

**6/6 결정적 GREEN. 타이밍 flake 0.** 신규 실-Sim/타이밍 테스트(VS-1·VS-2)가 매회 동일 통과. 각 iter 클린 종료
(teardown hang 0 — Harness.DisposeAsync의 `Queue.Writer.TryComplete()` = testhost-teardown-channel-race 교훈 반영).
포트 경쟁 견고화(`StartRobustAsync` 6회 재시도) + `StartupClearCompleted` 배리어가 s9-flake/e2e-parallel-load 교훈 실효.

### 4. arming 불변식 · D①/D③-part1 회귀 보존

- `HandshakeResidueTests` S1~S6·S5b + `StartupClearTests` VS1~VS3b(위 13건 중 12건) **6회 전건 GREEN**.
  arming(ArmRFlagZeroAsync) 훼손 0 · 허위 RSEQ_MISMATCH 0(VS-1이 `DoesNotContain HS_RSEQ_MISMATCH`도 단언) · StartupClear 회귀 0.
- HandshakeOrchestrator/PlcGateway diff 0(§0) — arming·안착지연·C_Flag 대기·R 폴·복귀 대기·ClearR 흐름 물리적 무변경.

### 5. 전체 스위트 GREEN + teardown hang 0 (독립 확인)

```
dotnet test backend/Wcs.sln -c Debug --no-build
통과!  - 실패:0, 통과:534, 건너뜀:0, 전체:534, 기간: 1 m 36 s
```
534 전건 통과 · 0 실패 · 0 건너뜀. 실행 후 제어 정상 반환(teardown hang 0). 계약 Completion Condition #1 충족.

### 6. 절대규칙 준수 (concurrency/timing 소관)
- **#1(단일 쓰기 큐)**: 핸드셰이크 EnqueueAsync/PlcWriteQueue 경로 diff 0 — 무변경.
- **#4(Ready 의미)**: 오케스트레이터 diff 0 — Ready 해석 무변경.
- **#7(CFlagTimeoutMs 설정 주입)**: appsettings `Timing:CFlagTimeoutMs`(기본 5000) 실키 + 테스트 500 주입. 하드코딩 0.

---

## 결론
**Concurrency / Timing 차원 = PASS.** D② CFlagTimeout이 실 Sim 위에서 **단독·유계(508ms@상한500)·무한대기 배제**로
결정적 재현됨(VS-1 단위 + VS-2 API가 alarm CFLAG_TIMEOUT 단독·RFLAG_TIMEOUT 부재로 기존 CFLAG||RFLAG 택일 모호성 제거).
신규 타이밍/실-Sim + arming 회귀 13건 **6회 반복 전건 GREEN(flake 0)**, 전체 534 GREEN·teardown hang 0.
HandshakeOrchestrator/PlcGateway diff 0으로 arming 불변식·제어흐름 물리 보존. 절대규칙 #1/#4/#7 위반 0.
