# SAFETY: PASS — S-F3a (B2C 운영 제어 백엔드 · PLC 쓰기 경로)

> SAFETY Evaluator · 2026-07-09 · dimension = 안전 경계(하드 게이트)만. functional은 별도(functional.md).
> Ground truth = git diff(develop) + 코드 직접 판독 + 독립 재실행 테스트. Generator 요약 불신, 파일 직접 확인.
> **재검증 = FIX ITER 1 반영본** (code review I-1 config 상한 + I-2 인메모리 재동기). 이전 302 GREEN 판정 → 305 GREEN 갱신.
> Handoff 마커: `tasks/sprint-log.md` L3005 `## IMPLEMENTATION COMPLETE — S-F3a`. fix-iter delta = `WcsOptions.cs`+`appsettings.json`(I-1), `OpsController.cs`/`DestinationControlService.cs` 갱신(I-1/I-2).

## Fix-iter delta 검증 대상 (working tree, 미커밋)
- **I-1(입력 위생):** `Wcs.Api/Infrastructure/WcsOptions.cs`(+48, `OpsWriteLimits`) · `Wcs.Api/appsettings.json`(+6, `Wcs:OpsLimits`) · `OpsController.cs` O4/O6에 상한 검증 추가(`IOptions<WcsOptions>` 주입).
- **I-2(상태 정합):** `DestinationControlService.cs` AlreadyInState 경로도 `ApplyPauseStateInMemory` 호출(DB↔인메모리 divergence self-heal).
- **불변(재확인):** OpsController O1~O6·enqueue 래퍼·감사 결선·PlcGateway 코어.

---

## 7개 안전 게이트 — 전부 PASS (fix 후 재확인)

### GATE 1 — 단일 쓰기 큐 (절대규칙 #1) : PASS
- no-direct-Modbus grep(`Wcs.Api` 전체): 히트는 선재 인프라 3파일(`Program.cs` 배선·`SorterGatewayRegistry.cs` 팩토리·`WcsTeardownGuard.cs` 종료)뿐. **OpsController·DestinationControlService·WcsOptions에 Modbus 직접 호출 0.** I-1은 검증 + `IOptions` 주입만 추가(Modbus 무관).
- 워드 쓰기 경로 불변: `GetBundle → Enqueue*Async → _polling.EnqueueAsync(PlcWrite.*) → 단일 컨슈머`. 런타임 증거: O5/O6 테스트가 컨슈머 전용 `PLC_WRITE` op-log 출현 단언(단일 큐 경유 이중 입증).

### GATE 2 — SAFE-3 ONLY (Q2 LOCK) : PASS
- `git diff develop -- backend/src/Wcs.PlcGateway/` = 빈 출력 → PlcWrite 유니온(SetTgtFloor·CellAssign·ClearR) 무변경, 신규 레코드 0. `WriteRawRegister`/`SetD4Bit` 미도입. I-1은 임의 레지스터 경로를 열지 않고 **좁힌다**(상한 추가).

### GATE 3 — TgtFloor 게이트(#2) + 비클리어(#3) : PASS
- 컨슈머 `TgtFloor==0` 가드(`PlcGateway.cs:486-496`) 무변경(diff 빈 출력). Ops는 같은 enqueue 경로 → 우회 불가.
- #3: `floor<1 → 400` 유지 + I-1로 `floor>{상한} → 400` 추가. 상한 검증은 enqueue **이전**(우회 아님·추가 위생). `currentTgtFloor`/`pingPongGuard` 정직 응답 유지. WCS가 TgtFloor에 0 쓰는 경로 없음.

### GATE 4 — 보호구역 PlcGateway/HandshakeOrchestrator 무변경 : PASS
- **`git diff develop -- backend/src/Wcs.PlcGateway/` = EMPTY (fix 후 재확인 = 0 lines).** HandshakeOrchestrator 무변경. 핸드셰이크 S1~S6: 전체 305 GREEN(핸드셰이크·시나리오 포함).

### GATE 5 — Sim 전용, 현장 위험 0 : PASS
- 테스트 = `SimWebApplicationFactory`(Transport=Tcp·127.0.0.1·동적 포트) + in-memory SQLite 스크래치 DB. COM1/RTU/SerialPort 실접근 0(유일 매치는 "미접근" 주석). 실 PLC 워드 쓰기 0. 현장 DB 오염 0.
- I-1 신규 테스트도 동일 팩토리(Sim TCP)로만 검증.

### GATE 6 — 하드코딩 타이밍 0(#7) + 마이그레이션 0 : PASS
- **바운드 = CONFIG 유래(리터럴 아님):** `WcsOptions.OpsLimits`가 `builder.Services.Configure<WcsOptions>(builder.Configuration.GetSection("Wcs"))`(Program.cs:47, 선재)로 `appsettings.json → "Wcs:OpsLimits"`(MaxTgtFloor:20·MaxCellNo:1000·MaxCellSeq:30000) 바인딩. OpsController는 `wcsOptions.Value.OpsLimits.EffectiveMax*` 사용 — **하드코딩 리터럴 상한 0.**
- **하드 타입 상한(wrap 절대 방지):** `RegisterCeiling = short.MaxValue(32767)`는 언어 상수. `EffectiveMax* = Math.Min(설정값, RegisterCeiling)` → 설정을 아무리 크게 잡아도 32767 초과 유효화 불가 → 컨슈머 `(short)` 캐스트 음수 wrap 원천 차단(설정 오설정 방어).
- **설정 기본값 sane:** MaxTgtFloor=20(운영층 2·물리 층수 상회), MaxCellNo=1000(SPEC 16셀 대폭 상회), MaxCellSeq=30000(32767 아래 헤드룸). 정상 값 거부 없고 타입 상한 아래인 합리적 안전 천장.
- **마이그레이션 0:** `git diff develop --name-only -- '*Migrations*' '*Snapshot*'` = 0. config 키 추가는 스키마 중립(DB 무영향). `destination_event.OperatorId` 선재.

### GATE 7 — 감사 무결성(append-only, operator 귀속) : PASS
- clear/pause/resume → `destination_event(CLEARED|PAUSED|RESUMED, OperatorId)` **Add-only**(update/delete 없음). operatorName 필수(공백/누락 → 400). 워드 쓰기 자동 감사 = 컨슈머 `PLC_WRITE`.
- **I-2 재확인:** AlreadyInState는 여전히 event 중복 append 0(감사 잡음 방지). 인메모리 `ApplyPauseStateInMemory`만 추가 호출 → DB Status ↔ ChuteState.IsPaused **수렴(divergence 아님·self-heal)**. 인메모리 flip은 `ChuteCapacityService.ApplyPauseStateInMemory` 내 `_rwLock.EnterWriteLock()` 임계구역, `RaiseChuteStateChanged`는 락 밖. DestinationControlService는 호출 시 락 미보유(DB scope dispose 후) → 락 순서/데드락/상태 발산 위험 0.

---

## I-1 검증 (over-limit → 400 · ZERO enqueue)
- 코드: O4는 `floor<1`(400) → `floor>maxFloor`(400) **둘 다 `GetBundle`/`EnqueueSetTgtFloorAsync` 이전**. O6는 `cellNo/seq<1` → `>EffectiveMax*` 검증 후에만 `EnqueueCellAssignAsync`. 바운드 초과값은 큐에 절대 도달 못 함.
- 테스트: `O4_FloorAboveBound_Returns400_NoEnqueue`(floor=21 설정상한 바로 위·floor=70000 타입상한 초과 → 둘 다 400, **D6 미변경으로 enqueue 0 단언**), `O6_CellNoOrSeqAboveBound_Returns400_NoEnqueue`(cellNo>1000·seq>30000·타입상한 초과 → 400). 전부 GREEN.

## Fresh 재실행 증거 (Evaluator 독립 실행, fix 반영본)
- `dotnet build backend/Wcs.sln` — 오류 0, 경고 10(전부 선재 NU1903 SQLitePCLRaw CVE·신규 CS 경고 0).
- `dotnet test --filter OpsControllerTests` — **16/16 GREEN**(13→16, +3 신규: O4/O6 상한 + 추가). 7s.
- `dotnet test backend/Wcs.sln`(전체, foreground blocking) — **305 GREEN / 0 실패 / 0 skip**(18s). 302→305(+3). 회귀 0.
- 위생: `Wcs.Sim3ds.exe` orphan 0. 잔류 testhost.exe 2개(중복 concurrent run 잔재)는 확인 후 정리 완료.

## 판정
FIX ITER 1(I-1 config 상한+하드 short 천장 · I-2 인메모리 self-heal) 반영 후 7개 안전 게이트 전부 재충족. 바운드는 config 유래·리터럴 아님·하드 short.MaxValue 천장으로 wrap 불가, over-limit는 400·enqueue 0, 보호구역(PlcGateway) 바이트 무변경, safe-3 유지, 마이그레이션 0(config 추가는 스키마 중립), 감사 append-only, I-2는 divergence 없이 write-lock 하 수렴. **SAFETY: PASS.**
(functional 차원은 functional.md 참조 — APPROVED는 두 차원 AND.)
