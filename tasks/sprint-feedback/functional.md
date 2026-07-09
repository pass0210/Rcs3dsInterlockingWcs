# FUNCTIONAL 평가 — S-F3a (B2C 운영 제어 백엔드 Ops API + 런타임 전이)

> FUNCTIONAL Evaluator · 2026-07-09 · Backend/API 차원. Safety 경계는 별도 SAFETY Evaluator 소관.
> 모든 증거는 fresh tool output(본 세션 직접 실행). Generator 요약을 신뢰하지 않고 git ground truth + 재실행으로 검증.

## FUNCTIONAL: PASS (fix-iter 재검증 — code-review I-1/I-2 반영, 305 GREEN)

---

## ★ FIX-ITER 재검증 (code-review I-1/I-2 적용 후 — 최신 verdict)

### 적용된 픽스 (git ground truth로 확인 — Generator 주장 아님)

- **I-1 — 워드 쓰기 상한 검증(silent (short) wrap 방지):**
  - `WcsOptions.cs`: 신규 `OpsWriteLimits` 레코드 — `MaxTgtFloor`/`MaxCellNo`/`MaxCellSeq`(설정값) + 하드 타입 상한 `RegisterCeiling = short.MaxValue(32767)` 언어 상수. `EffectiveMaxX => Min(설정값, RegisterCeiling)`.
  - `appsettings.json`: `Wcs:OpsLimits {MaxTgtFloor:20, MaxCellNo:1000, MaxCellSeq:30000}` + 주석(#7 준수 — 도메인 상한=설정값, 타입 상한=언어 상수).
  - `OpsController.SetTgtFloor`(O4)·`CellAssign`(O6): `[FromServices] IOptions<WcsOptions>` 주입 후 `floor>EffectiveMaxTgtFloor`/`cellNo>EffectiveMaxCellNo`/`seq>EffectiveMaxCellSeq` → `400 BadRequest`. **검증이 `GetBundle`/`Enqueue*Async` 이전**이라 초과값은 enqueue 0(큐 미투입).
- **I-2 — 멱등 전이 인메모리 재동기:** `DestinationControlService.TransitionAsync` 리팩터 — DB 쓰기는 `!alreadyInState`에서만, `ApplyPauseStateInMemory`(CHUTE)는 Transitioned·AlreadyInState **공통 경로**에서 호출. DB Status↔인메모리 IsPaused divergence를 멱등 재요청 1회로 self-heal.
- **보호구역 재확인:** `git diff --stat backend/src/Wcs.PlcGateway/` **빈 출력**(코어 무변경 유지). 신규 migration/ModelSnapshot **0건**. `frontend/` **무변경**.

### 회귀 0 — 전체 스위트 GREEN (foreground, block)

```
dotnet test backend/Wcs.sln
통과!  실패: 0, 통과: 305, 건너뜀: 0, 전체: 305, 기간: 19s   (Wcs.Tests.dll net10.0)
```
- 305 = 이전 302 + 신규 3(bound/reconcile). 실패 0 / 건너뜀 0.

### 신규 3건 결정성 + 명시 통과

```
# isolated OpsControllerTests x3 → 매회 실패 0, 통과 16, 건너뜀 0 (13 기존 + 3 신규, 결정적)
# 신규 3건 명시:
통과 OpsControllerTests.O4_FloorAboveBound_Returns400_NoEnqueue [456 ms]
통과 OpsControllerTests.O2_Idempotent_ReconcilesDivergentInMemoryFlag [66 ms]
통과 OpsControllerTests.O6_CellNoOrSeqAboveBound_Returns400_NoEnqueue [271 ms]
     통과: 3
```
- **O4_FloorAboveBound**: floor=21(설정 상한 20 바로 위)·floor=70000(short.MaxValue 초과) 둘 다 400 + enqueue 0 단언(`PLC_WRITE/SET_TGTFLOOR` 부재·`STATE/OPS_SET_TGTFLOOR` 부재·Sim D6=0 유지).
- **O6_CellNoOrSeqAboveBound**: cellNo=1001·seq=30001·cellNo=70000 각각 400 + enqueue 0(`PLC_WRITE/CELL_ASSIGN`·`STATE/OPS_CELL_ASSIGN` 부재).
- **O2_Idempotent_ReconcilesDivergentInMemoryFlag**: DB만 PAUSED로 직접 전환(서비스 우회)해 인메모리 IsPaused=false divergence 조성 → `GetHold=None`(게이트 열림) 확인 → 멱등 pause 1회 → `outcome=AlreadyInState` + `GetHold=Paused`(self-heal) 단언.

### 위생 (fix-iter 재확인)

- **빌드**: `오류 0개 / 경고 10개`. 경고 전량 `NU1903`(SQLitePCLRaw 2.1.10 advisory) — 전 프로젝트 공통 선재 부채, F3a/픽스 무관·신규 0. → 0 error / 0 new warning.
- **고아 프로세스 0**: 재실행 후 standalone `Wcs.Api.exe`/`Wcs.Sim3ds.exe` 잔류 없음(OpsControllerTests는 in-process Sim; 별 exe 미기동). 코디네이터 경고한 외부 file-lock 재현 없음(빌드 정상 3.77s).
- **안전 규율**: 검증 전량 Sim3ds TCP + in-memory SQLite 스크래치. COM1/RTU/현장 DB 미접근.

---

## (초기 검증 원문 — 302 기준, 참고용 보존)

### 0. 핸드오프·ground-truth 게이트

- **`## IMPLEMENTATION COMPLETE — S-F3a` 마커 존재 확인.** `tasks/sprint-log.md`는 stray null byte로 git이 binary로 취급하나 `grep -a`로 마커 판독 가능. 자동 FAIL 사유(마커 부재) 아님.
- **브랜치 = `feat/f3a-ops-backend`.** F3a 변경 = working-tree 미커밋. stale-resend 아님(diff가 계약+픽스와 일치).

### 3. OpsController O1~O6 동작 — 계약 대비 [Completion #2]

13개 원 [Fact] + 3 신규 = 16 통합 테스트, 실 HTTP 왕복(WebApplicationFactory + Sim3ds TCP):

- **O4 SetTgtFloor**: 200 + Sim D6 반영 + `PLC_WRITE/SET_TGTFLOOR`(컨슈머 EmitWrite 전용 = 단일 큐 경유 증거) + `STATE/OPS_SET_TGTFLOOR`. 핑퐁: TgtFloor≠0 재요청 시 `pingPongGuard=true` 정직 보고 + D6 미덮임(#2). floor<1 → 400(#3). **floor>상한 → 400·enqueue 0(I-1)**.
- **O5 ClearR**: 200 → Sim R_Flag=0·RCellNo=0·RSeq=0 + `PLC_WRITE/CLEAR_R`.
- **O6 CellAssign**: 200 → Sim C 수신 + `PLC_WRITE/CELL_ASSIGN`. **cellNo/seq>상한 → 400·enqueue 0(I-1)**.
- **O2/O3 소터**: pause→IF-05 NG(dispatch 게이트 PAUSED)·resume→OK 복원 + DB Status + `destination_event(PAUSED/RESUMED, operator_id)`.
- **O2/O3 슈트**: GetHold None→Paused→None(인메모리) + DB Status. 멱등: 재요청 AlreadyInState + event 중복 0. **divergence self-heal(I-2)**.
- **O1 clear / A-8**: FULL 슈트 → clear → None 복구 실증 + `destination_event(CLEARED, operator_id)`.
- **edge**: operatorName 누락/공백 → 400; O4~O6 비-SORTER_3D/미등록 → 404; O1 비-CHUTE/미존재 → 404.
- **라우트 충돌 0**: `/api/ops/*` ⊥ `/api/v1/*`·`/api/monitor/*`(동일 factory에서 IF-05와 공존 실증).

### 4. 단일 큐 경유·안전 워드 3종

- 워드 쓰기(O4~O6)는 `GetBundle`→`Enqueue*Async`→`_polling.EnqueueAsync(PlcWrite.*)` 위임(신규 래퍼 = 기존 레코드 재사용). `PLC_WRITE`는 컨슈머에서만 발화 → 테스트의 PLC_WRITE 단언 = 컨트롤러 직접 Modbus 호출 부재의 런타임 증거.

### 5. 감사·마이그레이션 [Completion #4/#6]

- clear/pause/resume → `destination_event`(operator_id) + `STATE` 경량. 워드 쓰기 → 컨슈머 `OnWrite` 자동 `PLC_WRITE` + Ops `STATE` 1행. 마이그레이션 0(기존 스키마 재사용, 경량 Q-b).

### 7. 비차단 관찰(Minor — functional PASS 불변)

1. **테스트 호스트 teardown 시 `System.ObjectDisposedException` (OperationLogService.FlushBatchAsync)**: 호스트 종료 중 disposed IServiceProvider 스코프 생성 → `catch(Exception)` WARN 로깅 후 배치 드롭(fail-safe by design). **F3a 미접촉 파일**·관측 스트림 한정(도메인 감사 destination_event 별도·불변)·전건 GREEN. 기존 teardown 경쟁 계열(memory "testhost-teardown-channel-race"). F3a 회귀 아님. 후속 teardown 정리 스프린트에서 flush drain 개선 여지.
2. **`tasks/sprint-log.md` stray null byte**: git binary 취급. 마커는 `grep -a` 판독 가능하나 커밋 전 텍스트 정규화 권장.

---

## 결론

Completion Conditions #1~#8 전부 충족(functional 차원). code-review I-1(상한 검증·wrap 방지)·I-2(멱등 인메모리 재동기) 반영 후 **305/305 GREEN**·신규 3건 결정적·O1~O6 계약대로 동작·A-8 해소·operator_id 감사 결선·PlcGateway 코어 및 프론트 무변경·migration 0·빌드 0 error/0 new warning·고아 0. **FUNCTIONAL: PASS.**
