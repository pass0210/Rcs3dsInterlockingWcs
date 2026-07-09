# SAFETY: PASS — S-F3B-FOLLOWUP (셀입력 라벨 · Ready-아닐-때 수동쓰기 무시 · PLC 코어 fresh-read 가드)

> SAFETY Evaluator · 2026-07-09 · dimension = 안전 경계(하드 게이트)만. functional은 별도(functional.md).
> Ground truth = git diff(develop) + 코드 직접 판독 + 독립 재실행 테스트(Sim3ds TCP + in-memory SQLite 전용). Generator 요약 불신·파일 직접 확인.
> Handoff 마커 확인: `tasks/sprint-log.md` 말미 `## IMPLEMENTATION COMPLETE — S-F3B-FOLLOWUP` 존재.
> 검증 방식: `dotnet test`만 사용(절대 `dotnet run --project Wcs.Api` 미사용 — S-2 트랩: 기본 Production=COM1/RTU+현장 DB). 실 PLC 워드 쓰기 0·현장 DB 접근 0.

## 대상 diff (develop 기준)
```
backend/src/Wcs.Api/Controllers/OpsController.cs   | +58  (Ready 사전점검 409 + O6 cFlagGuard advisory)
backend/src/Wcs.PlcGateway/PlcGateway.cs           | +20 / -8  (두 가드 판정원천 _latest → fresh FC03)
backend/src/Wcs.Sim3ds/SimServer.cs                | +19  (test seam SetReady(bool) — 테스트 전용)
backend/tests/... (OpsControllerTests +121, PlcGatewayIntegrationTests +66)
frontend/src/lib/ops.ts +3 · frontend/src/pages/sections/OpsControls.tsx +71
```

---

## 5개 안전 게이트 — 전부 PASS

### GATE 1 — PROTECTED-ZONE 최소성 · 큐/락/RMW/의미 보존 · #2/#3 : PASS
- **최소 diff(load-bearing만):** `PlcGateway.cs` 변경은 정확히 두 가드의 **판정 원천 교체**뿐 (`PlcGateway.cs:486-519`).
  - `SetTgtFloor`: `var snapTgt = _latest; if (snapTgt.TgtFloor != 0)` → `var freshTgt = await _master.ReadHoldingRegistersAsync(RegisterMap.TgtFloor,1,ct); if (freshTgt[0] != 0)` (:492-499).
  - `CellAssign`: `var snapC = _latest; if (snapC.CFlag)` → `var freshFlags = await _master.ReadHoldingRegistersAsync(RegisterMap.Flags,1,ct); if ((freshFlags[0] & RegisterMap.D4.C_Flag)!=0)` (:512-519).
  - 나머지: 큐 구조·`_writeQueue`·`RunWriteConsumerAsync`·`RmwD4LockedAsync`·`EmitWrite` 관측 훅·OFFLINE 전이·이벤트 = **무변경**. 로그 문구만 `·fresh-read` 접미 추가.
- **fresh read가 `_clientLock` 안(원자 read-then-write, #1 보존):** `ProcessWriteAsync`는 `_clientLock.WaitAsync`(:479) … `finally _clientLock.Release()`(:546) 임계구역 하나. fresh read(:492/:512)는 그 안. `_clientLock` 획득처 = 폴 루프(:255), 재연결(:388), 컨슈머(:479) 셋뿐 — 폴/쓰기가 같은 락 직렬. **read+write 프레임 교차 없음.** 컨슈머 내부 `_master.Read...`는 락 재획득 안 함(기존 `RmwD4LockedAsync`와 동일 패턴) → reentrancy/deadlock 0.
- **가드 의미 불변:** SetTgtFloor는 `TgtFloor != 0`이면 skip(#2 핑퐁), CellAssign은 `C_Flag==1`이면 skip. 임계값·방향 동일 — 원천만 정확화.
- **#3(WCS 비클리어) 온전:** SetTgtFloor case는 `WriteSingleRegisterAsync(TgtFloor, (short)floor)`만; 0-클리어 경로 없음. O4 컨트롤러는 `req.Floor < 1 → 400`(`OpsController.cs:140`) 유지 → floor>=1만 수락, 수동 클리어 미노출.

### GATE 2 — 자동/오케스트레이트 경로 무손상 (crux) : PASS
- **`HandshakeOrchestrator.cs` diff = 0** (git 확인). **`Wcs.Core/**`(DepositDecider·RegisterMap·모델) diff = 0.** **`RcsController.cs` diff = 0** (AlignSorterToOperationalFloor 무변경).
- **Ready 게이트가 공유 컨슈머 case에 없음:** `PlcGateway.cs`의 SetTgtFloor/CellAssign case에 `Ready` 판정 부재(코드 판독 확인). Ready 게이트는 `OpsController.ReadyPrecheck`(수동) + FE에만 존재. → 자동 IF-09(Ready==0에 TgtFloor 기입) 무영향.
- **테스트 입증(fresh):** `IF09_AutoAlign_WritesTgtFloor_EvenWhenReadyZero_NoRegression` GREEN — Sim Ready=0에서도 D6 기입. 핸드셰이크 `IT1/IT2/IT3`(IT3c 포함) 그룹 **7/7 GREEN**(격리 재실행). fresh-read는 C 기입 시 C_Flag==0(정상)이라 통과 → 무회귀.

### GATE 3 — Ready 사전점검 manual-only + 정확 : PASS
- O4(`:153`)·O6(`:227`) enqueue **직전** `ReadyPrecheck(bundle,...)` 호출 → `!Online` 또는 `!Ready`면 `Conflict(409)` 반환·enqueue 0·WARN 감사 1행(`OpsController.cs:245-270`).
- **O5 ClearR(`:182-202`)는 ReadyPrecheck 미호출** — 무조건 enqueue(복구 도구, Q1). 확인.
- 사전점검 원천 = `bundle.Latest`(폴 스냅샷, **읽기만**) — 신규 동기 Modbus 읽기 표면 0(#1). Ready는 초 단위 레벨이라 ~150ms stale 허용, 서브-폴 rapid-double은 GATE 4 컨슈머 가드가 최종 차단.
- 테스트: `O4_NotReady_Returns409_NoEnqueue`·`O6_NotReady_Returns409_NoEnqueue`·`O5_ClearR_NotReady_StillAllowed`·`O6_CellAssign_CFlagGuard_ReportsAdvisory` 모두 GREEN.

### GATE 4 — fresh-read 가드 결정적 + provider/PLC-safe : PASS
- **하드코딩 타이밍 0:** 신규 상수·sleep·타임아웃 도입 없음(diff 확인). 결정성은 큐 직렬 + `_clientLock` read+write 원자성에서 나옴(타이밍-luck 아님).
- **deadlock/reentrancy 0:** fresh read는 이미 보유한 `_clientLock` 안에서 `_master`를 직접 호출(재획득 없음) — `RmwD4LockedAsync` 기존 패턴과 동형.
- **결정성 실측:** `IT3d_RapidDoubleCellAssign_SecondRejected_ByFreshRead` 격리 **3/3 GREEN**(131/134/125ms). Sim 상태루프 동결(InjectNoResponse) 후 CellAssign 2연타 → 2번째 skip 로그 + C 영역 11/101 유지(22/202 미덮어씀) 이중 단언.
- **비-vacuous 확인:** IT3d는 실 TCP Sim 서버 레지스터를 GW 폴로 관측해 C 영역 보존을 단언하고 skip-로그 출현을 대기 — 가드를 `_latest`로 되돌리면 서브-폴 창에서 2번째가 통과해 skip 로그 미발생 → 대기 타임아웃 RED. 테스트가 fix에 load-bearing(코드 판독으로 확인).

### GATE 5 — 마이그레이션 0 · 신규 PLC 쓰기 타입 0 · safe-3 : PASS
- **Migrations diff = 0** (`Wcs.Data/Migrations/` git 확인). `cFlagGuard`는 응답 DTO 전용·DB 무관.
- **PlcWrite 타입 = SetTgtFloor·CellAssign·ClearR 셋뿐**(`PlcGateway.cs:30/33/36`) — 신규 타입 0. Sim `SetReady`는 슬레이브 test seam(마스터 쓰기 큐 아님).

---

## SAFETY 하드 제약 (검증 인프라) — 준수
- 전 과정 `dotnet test`(SimWebApplicationFactory: in-memory SQLite + 동적 포트 TCP Sim; PlcGatewayIntegrationTests: ephemeral TCP Sim). **`dotnet run --project Wcs.Api` 미사용** → COM1/RTU·현장 SqlServer 미접근. 실 PLC 워드 쓰기 0.
- 검증 전 고아 `Wcs.Sim3ds.exe`/`Wcs.Api.exe` 0 (tasklist 확인).

## 회귀 관찰
- **전체 스위트: 318/318 GREEN × 3 연속**(각 18s). + IT3d 3/3, 신규 7군, 핸드셰이크 7군 격리 GREEN.
- 앞선 1회 run에서 1/318 실패(비-changed-path) 관측되었으나 **후속 4회 full run 전부 318/318**로 미재현 — `tasks/lessons.md` s9-flake-under-e2e-load / e2e-parallel-load-surfaces-integration-flakes(부하 하 저빈도 타이밍 flake)와 정합. changed-path 테스트(IT3d·Ops·핸드셰이크)는 격리·반복 결정적 → 이 변경의 회귀 아님.
- `WcsTeardownGuard SocketException` 로그 = 문서화된 testhost-teardown-channel-race(PASS 후 teardown 노이즈, pass/fail 무관). 
- 빌드 0 오류 · 신규 CS 경고 0(경고 10건 전부 선재 NU1903 SQLitePCLRaw).

## 결론
**SAFETY: PASS.** PROTECTED-ZONE 변경은 fresh-read 가드 2개로 최소화(`_clientLock` 내부 원자 read-then-write, 단일 큐·RMW·의미 불변, #2/#3 보존). 공유 컨슈머 case에 Ready 게이트 미주입 — 자동 IF-09·오케스트레이트 핸드셰이크 무손상(diff 0 + 테스트 GREEN). Ready 사전점검은 OpsController 수동 경로 전용·읽기만·O5 제외. 마이그레이션 0·신규 PLC 쓰기 타입 0. Sim/스크래치-DB 전용 검증(실 COM1/RTU·현장 DB 미접근).
