# Sprint Feedback — Functional dimension

Sprint: **S-AUDIT-D-HANDSHAKE-HARDENING**
Dimension: **Functional** (D④ 원인 분리 · IF-10 멱등 시맨틱 · 현동작 고정 · 데이터 무결 · 로그 정직성)
Evaluator: functional (expert pool). Concurrency/timing 차원은 별도 Evaluator가 aggregate.
Date: 2026-08-03
Base: develop=459aaac / branch feat/audit-d-handshake-hardening

## VERDICT: **PASS**

기능 차원의 모든 완료 조건이 fresh 증거로 충족됨. D④ 원인 분리(enum)·컨트롤러 원인별 로그·200 멱등 바이트 보존·현동작 고정 테스트 실효·데이터 무결·회귀 0 확인. 정책 미변경(WARN 로깅만, alarm 승격 없음)·SPEC §7-B 등재 확인. Blocking 없음. Minor 1건(아래).

---

## Fresh evidence (지금 실제 실행)

### 1. D④ 기능 테스트 타깃 실행 (독립)
명령: `dotnet test backend/Wcs.sln --filter "…DepositRecorderCauseTests|…VS4_If10|…VS5_If10|…VS3_If10"`
```
통과!  - 실패:     0, 통과:     9, 건너뜀:     0, 전체:     9, 기간: 2 s - Wcs.Tests.dll (net10.0)
```
DepositRecorderCauseTests 5(NewRecord 전이/직삽입·Duplicate·DeniedReport·NoDestination) + ApiIntegrationTests VS-3/VS-4/VS-5 = 전건 GREEN.

### 2. 전체 회귀 스위트 (독립 재실행)
명령: `dotnet test backend/Wcs.sln`
```
통과!  - 실패:     0, 통과:   534, 건너뜀:     0, 전체:   534, 기간: 1 m 32 s - Wcs.Tests.dll (net10.0)
```
**534 = 524 baseline + 10 신규** 산술 일치. 실패 0·건너뜀 0. teardown hang 0.

### 3. 빌드 경고
명령: `dotnet build backend/Wcs.sln` → `grep -c "warning CS"` = **0**.
잔존 경고는 pre-existing NU1903(SQLitePCLRaw 패키지 취약성)뿐 — 신규 CS 경고 증가 0.

---

## 항목별 판정 (코드 직접 검사 + git ground-truth)

### D④ 원인 분리 — PASS
- `DepositRecordResult{NewRecord,Duplicate,DeniedReport,NoDestination}` enum이 **I/O 계층** `Wcs.Api/Repositories/Repositories.cs`에 배치(Wcs.Core 아님). `IDepositRecorder.RecordDeposit` 반환 `bool → DepositRecordResult`.
- **Wcs.Core git diff = 0** (절대규칙 #8 침범 0): `git diff --stat backend/src/Wcs.Core/` → 빈 결과.
- `EfDepositRecorder.RecordDeposit`(DbRepositories.cs:478-579) 6개 return 원인별 정확 매핑 확인:
  - piece null + dest null → `NoDestination` (L502)
  - piece null + dest 존재 → 직삽입 → `NewRecord` (L532)
  - 이미 DEPOSITED/CELL_ASSIGNED/LOADED → `Duplicate` (L540)
  - DENIED → `DeniedReport` (L547)
  - RESERVED/QUERIED/PERMITTED → DEPOSITED 전이 → `NewRecord` (L566)
  - 유니크 위반 catch 백스톱 → `Duplicate` (L572)
- **판정 로직·트랜잭션 경계 불변**: `BeginTransaction`/`Rollback`/`Commit`/`SaveChanges` 위치, piece 조회 필터(IsActive·ArchivedAt==null·OrderByDescending(Id)), 상태 전이·PieceEvent 삽입 전부 diff에서 반환 타입 변경 외 무변경. 최종 `catch { Rollback(); throw; }` 재던짐 보존(예외 삼킴 없음).

### RcsController IF-10 원인별 로그 + 200 바이트 보존 — PASS
- RcsController.cs:252-281 `if (recordResult != NewRecord)` 가드 후 `switch`:
  - `Duplicate` → INFO "중복 보고 — 멱등 OK"(현행 유지)
  - `DeniedReport` → `_log` WARN + `_opLog` `IF10_DENIED_REREPORT`(WARN)
  - `NoDestination` → `_log` WARN + `_opLog` `IF10_NO_DESTINATION`(WARN)
- **전 케이스 200 OK** `return Ok(new DepositReportResponse("OK"))`(L280) — 응답 형상 불변.
- **`NewRecord`만 후속 트리거**: 비-NewRecord는 L280 조기 return으로 FULL 집계(L295)·IF-11 트리거(L308)에 도달 불가 → 기존 `isNewRecord` 게이팅과 바이트 등가. `_log`=`ILogger<RcsController>`(L26)이므로 테스트의 CapturingLogger<RcsController> 싱글턴 등록이 실제 발화를 가로챔.

### 현동작 고정 테스트 실효(비공허) — PASS
- CapturingLogger<T>(PushLogThrottleTests.cs:50)는 `formatter(state,exception)`로 **실제 포맷된 메시지**를 `(level,message)`로 캡처 — 공허 아님. 테스트는 고유 pId(`pId={26010/26020/26030}`)로 필터해 공유 픽스처 누적 격리.
- **VS-4**(DENIED 재보고): 200·`Result="OK"`·piece DENIED 불변·piece_event 무증가(`Assert.Empty`)·WARN "DENIED piece 재보고" 존재·`DoesNotContain("멱등 OK")`(위장 제거 실증).
- **VS-5**(미존재 chuteNo=9999·무피스): 200·piece 0(`Assert.Empty`)·WARN "미존재 chuteNo"·`DoesNotContain("멱등 OK")`.
- **VS-3**(신규→중복 회귀): 1차 신규 → 200·DEPOSITED·트리거-분기 도달("IF-11 트리거 없음" INFO)·WARN 부재·멱등 OK 부재 / 2차 재보고 → 200·INFO "중복 보고 — 멱등 OK" 유지·WARN 부재.
- DepositRecorderCauseTests 5건: 원인 판정을 in-memory SQLite로 격리 단언(상태 불변·piece_event 무증가·piece 0 포함).

### 데이터 무결 — PASS
- VS-4: piece.Status DENIED 불변 + PieceEvents 무증가 단언. VS-5: piece 0행 단언. DepositRecorderCauseTests: DENIED 불변·piece_event empty·NoDestination piece 0 단언. sorter_command 상태는 비-NewRecord 경로에서 미생성(조기 return으로 TriggerSorterHandshake 미도달) — 현동작 보존.

### 회귀 0 / 무변경 보존 — PASS
- 전체 534/0/0(위 fresh). `git diff --stat backend/src/Wcs.Core/` = 빈 결과(#8). `git diff --stat backend/src/Wcs.PlcGateway/` = 빈 결과(HandshakeOrchestrator/PlcGateway 제어흐름 diff 0). Sim3ds 변경은 test-only 헬퍼 `SetCResidue`(SimSlave.cs·SimServer.cs) 추가뿐 — 정상 런타임 경로 무변경(기존 `SetRResidue`와 동형). DataIntegrityAuditTests는 `bool isNew → DepositRecordResult` 호출부 갱신(`Assert.Equal(NewRecord,…)`)만.

### 정책 미변경 — PASS
- DENIED 재보고·미존재를 alarm으로 **승격 안 함**: 컨트롤러 switch에 `IAlarmSink` 호출 없음, `_log` WARN + `_opLog`(WARN) 두 sink만. 200 멱등 응답 불변. SPEC §7-B에 IF-10 DENIED 재보고·미존재 chuteNo 처리를 **"현동작 확정·고정"**(정책 전환 미포함)으로 등재 확인 — 정책 전환이 아님.

---

## Minor (비-blocking — 다음 sprint Generator가 읽음)
- **[S-AUDIT-D] RcsController IF-10 switch에 `default`/exhaustiveness 없음**: 현재 4-값 enum에서 비-NewRecord 3값(Duplicate/DeniedReport/NoDestination) 전부 case 처리되어 **현 시점 결함 아님**(모든 도달 가능 값이 로깅됨). 단 향후 enum에 5번째 값이 추가되면 switch를 통과해 로그 없이 200 OK로 조용히 빠져나가는 fail-loud 사각이 생긴다. `default: throw new InvalidOperationException(...)` 또는 C# switch expression 전수 처리로 컴파일/런타임 방어 권장. (평가 메모 evaluator-concurrency-blindspot의 "switch default" 사각과 동류 — 방어적 등재.)

---

## 결론
Functional 차원 **PASS**. 위 완료 조건(회귀 0·원인 구분 타입·컨트롤러 원인별 로그·200 멱등 불변·데이터 무결·정책 미변경·경고 증가 0) 전부 fresh 증거로 충족. APPROVED는 concurrency/timing 차원 PASS와의 AND — 그 차원은 별도 Evaluator 판정. (본 파일은 functional 전용, 공유 sprint-feedback.md 미변경.)

---

## FIX ITER 재검증 (Minor 1/2 하드닝) — 2026-08-03 — **PASS 유지**

코드리뷰 Minor 2건 fix만 재검증(전체 재검증 아님). 코드 수정 없음.

### Minor 1 (fail-loud default) — OK
- `RcsController.cs` IF-10 `switch(recordResult)`에 `default:` 추가(git diff 확인): 미매핑 원인 → `_log` WARN + `_opLog` `IF10_UNMAPPED_CAUSE`(WARN) + `return Ok(...)` 200 멱등 유지(RCS에 500 미던짐 — IF-10 응답 계약 보존).
- **런타임 무영향 확인**: 현 4값(NewRecord는 L252 가드로 switch 진입 불가 + Duplicate/DeniedReport/NoDestination 3 case 처리)이 exhaustive → default 도달 불가. 무음(silent) 아님(fail-loud) — 내 이전 Minor 지적을 정확히 닫음. `IF10_UNMAPPED_CAUSE`는 기존 두 case와 동형 시그니처(OperationLogCategory.API·OperationLogLevel.WARN)로 컴파일 성공.

### Minor 2 (주석만) — OK
- `DbRepositories.cs` 상태 전이 else 주석을 "catch-all(위 게이트 밖 그 외 상태 → DEPOSITED 부활, 선재 동작·바이트 보존)"로 정정 + 트레일링 return 주석 정정. **executable 라인 byte-identical**(git diff상 변경은 주석 텍스트뿐 — `piece.Status=DEPOSITED`·SaveChanges·Commit·`return NewRecord` 불변). 로직 diff 0.

### 회귀/규칙 재검증 (fresh)
- 전체 `dotnet test backend/Wcs.sln` → **534 통과 / 0 실패 / 0 건너뜀** (1m38s). D④ 기존(VS-3/4/5·DepositRecorderCause) 포함 전건 GREEN. 회귀 0.
  - ※ 1차 시도는 고아 `testhost(PID 45624)`가 출력 DLL을 잠가 MSB3021/3027 빌드 실패(코드 결함 아님 — 알려진 환경 이슈). 해당 PID kill 후 재실행 → 534 GREEN(위). teardown hang 0.
- `git diff --stat backend/src/Wcs.Core/ backend/src/Wcs.PlcGateway/` = **빈 결과**(HandshakeOrchestrator/PlcGateway/Core diff 0 — 핸드셰이크 무접촉, #8 유지).
- 신규 CS 경고 0: `Wcs.Api` 빌드의 유일 CS 경고 CS8604는 `B2C/B2cFacilityService.cs`(이번 미변경 파일)의 선재 경고 — 변경 2파일(RcsController·DbRepositories)발 신규 경고 0. #7 유지.

**FIX ITER 후에도 Functional 차원 PASS 유지.** Minor 1은 해소 완료(더 이상 미등재). concurrency/timing 차원은 이 fix에 영향 없음(핸드셰이크 무접촉).
