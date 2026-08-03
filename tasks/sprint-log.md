# Sprint Log — S-AUDIT-D-HANDSHAKE-HARDENING

(Generator가 `## IMPLEMENTATION COMPLETE` + 변경 요약 + 테스트 결과 기록)

## IMPLEMENTATION COMPLETE (Generator, 2026-08-03)

실작업 = **D② (CFlagTimeout 결정적 단독 단언)** + **D④ (RecordDeposit 원인 분리 + 원인별 로그 + 현동작 고정)**.
동작 보존 리팩터 + 관측성/커버리지 강화. 핸드셰이크 제어 흐름 무변경. D①/D③-part1 회귀 보존. 스코프 밖(D①/D③ 코드·Wcs.Core·D③-part2) 무접촉.

### 파일별 변경 요약

**D④ — 프로덕션(WCS)**
- `backend/src/Wcs.Api/Repositories/Repositories.cs` — `enum DepositRecordResult { NewRecord, Duplicate, DeniedReport, NoDestination }` 신설(I/O 계층 — Wcs.Core 순수 #8 침범 0). `IDepositRecorder.RecordDeposit` 반환 `bool → DepositRecordResult`.
- `backend/src/Wcs.Api/Repositories/DbRepositories.cs` — `EfDepositRecorder.RecordDeposit` 6개 return을 원인별 매핑: 미존재chuteNo·무피스→`NoDestination`, 신규 직삽입/RESERVED→DEPOSITED 전이→`NewRecord`, 이미 DEPOSITED/CELL_ASSIGNED/LOADED→`Duplicate`, DENIED→`DeniedReport`, 유니크 위반 백스톱→`Duplicate`. 판정 로직·트랜잭션 경계 불변(반환 타입만 변경).
- `backend/src/Wcs.Api/Controllers/RcsController.cs` — IF-10 `DepositReport`에서 `if (!isNewRecord)` 단일 '멱등 OK' 합류를 `switch(recordResult)` 원인별 분기로: `Duplicate`→현행 INFO '멱등 OK' 유지 / `DeniedReport`→`_log` WARN + operation_log `IF10_DENIED_REREPORT`(WARN) / `NoDestination`→`_log` WARN + operation_log `IF10_NO_DESTINATION`(WARN). **전 케이스 200 OK·차단 동작 바이트 보존**('멱등 OK' 위장만 제거). `NewRecord`만 후속(FULL 집계·IF-11 트리거) 진행 — 기존 경로 불변.

**D② — 테스트 상대역(Sim3ds test-only helper, WCS 핸드셰이크 무변경)**
- `backend/src/Wcs.Sim3ds/SimSlave.cs` — `SetCResidue(int cCellNo, int cSeq)` test-only 헬퍼 추가(기존 `SetRResidue`와 동형: C 영역 + C_Flag=1 직접 세팅). 정상 런타임 동작 불변.
- `backend/src/Wcs.Sim3ds/SimServer.cs` — 파사드 `SetCResidue` 위임 노출.

**테스트**
- `backend/tests/Wcs.Tests/CFlagTimeoutTests.cs` (신규) — **VS-1**: 실 Sim + PlcPollingService + HandshakeOrchestrator 하니스. StartupClearCompleted 배리어 후 `InjectNoResponse`(상태기계 정지=C 미소비) + `SetCResidue`(C_Flag=1) 심고 `ExecuteAsync` → `Outcome==CFlagTimeout` **단독** + 경과 [CFlagTimeoutMs−100, CFlagTimeoutMs+2500]ms(무한대기 배제) + `HS_C_SENT`·`CELL_ASSIGN` 부재(C 미기입) + `HS_CFLAG_TIMEOUT` 발화·`HS_R_RECV`/`HS_TIMEOUT` 부재. CFlagTimeoutMs=500 설정 주입(#7). 포트 경쟁 재시도 견고화.
- `backend/tests/Wcs.Tests/E2E/E2EGroupCD_AlignHandshakeTests.cs` — **VS-2** `D5b_CFlagTimeout_Deterministic_CflagAlarmAlone_TimeoutMapping` 추가: IF-10 유발 핸드셰이크 CFlagTimeout → alarm `CFLAG_TIMEOUT` **단독**(RFLAG_TIMEOUT 부재) + sorter_command status=TIMEOUT + piece status=TIMEOUT 현동작 고정. `StartAsync` 헬퍼에 `cFlagTimeoutMs` 파라미터 추가(D5b는 500 주입). WCS 스냅샷 CFlag=1 **4연속 안정**(UntilExactAsync) 대기로 StartupClear 잔여 창 배제.
- `backend/tests/Wcs.Tests/E2E/E2EInfrastructure.cs` — `E2EWebApplicationFactory`에 `cFlagTimeoutMs=2000`(기본=기존 하드코딩값·회귀 0) 생성자 파라미터 추가 → `Timing:CFlagTimeoutMs` 결선.
- `backend/tests/Wcs.Tests/DepositRecorderCauseTests.cs` (신규) — **VS-3(원인 판정)**: EfDepositRecorder를 in-memory SQLite로 격리, 5원인 단위 단언(NewRecord 전이/직삽입·Duplicate·DeniedReport(불변+piece_event 무증가)·NoDestination(piece 0)).
- `backend/tests/Wcs.Tests/ApiIntegrationTests.cs` — `FakeModbusWebApplicationFactory`에 `CapturingLogger<RcsController> RcsLog` 등록(원인별 로그 캡처). **VS-4**(DENIED 재보고→200·DENIED 불변·piece_event 0·WARN·멱등OK 위장 제거) / **VS-5**(미존재 chuteNo·무피스→200·piece 0·WARN) / **VS-3**(신규→200·DEPOSITED·트리거 분기 도달·WARN/멱등OK 부재 / 재보고→200·'멱등 OK' INFO 유지) 추가.
- `backend/tests/Wcs.Tests/DataIntegrityAuditTests.cs` — RecordDeposit 호출부 `bool isNew → DepositRecordResult` 갱신(`Assert.Equal(NewRecord, ...)`).

**문서**
- `docs/SPEC.md §7-B` — (1) C_Flag 대기 타임아웃: 무한대기 배제 결정적 회귀 가드 등재(재시도/포기 정책은 여전히 미정) (2) IF-10 DENIED 재보고·미존재 chuteNo 처리: 현동작 확정·고정(정책 전환 미포함) (3) sorter_command SENT 행 내구화 시점 = 수용된 갭(D③-part2 SCOPE OUT — HS_C_SENT operation_log가 감사 앵커 커버).

### enum 설계 (HOW 결정)
- 명칭·배치: `Wcs.Api.DepositRecordResult`(Repositories.cs — I/O 계층). Wcs.Core 순수 #8 보존(판정이 DB 상태 의존이라 Core에 두지 않음).
- 값: 최소 4원인 = `NewRecord`(유일 성공·후속 트리거 진행) / `Duplicate`(진짜 중복·'멱등 OK' INFO) / `DeniedReport`(DENIED 재보고·WARN) / `NoDestination`(미존재 chuteNo·무피스·WARN). 시그니처: `DepositRecordResult RecordDeposit(int, string, int, int, int?, string?)`.

### alarm 판단 (사용자 게이트 D④)
- DENIED 재보고·미존재를 alarm으로 **승격하지 않음**(정책 전환 미포함 — SPEC §7-B 등재만). 기존 `IAlarmSink` 재사용 안 함. 관측은 `_log` WARN + operation_log(WARN) 두 sink로만(현동작 고정·차단 동작 불변). CFlagTimeout의 `CFLAG_TIMEOUT` alarm은 기존 결선 그대로(변경 0).

### 테스트 결과
- **RED→GREEN**: 신규 테스트는 원인 분리(enum) 전 코드에선 컴파일/단언 불가(bool 합류) → 구현 후 GREEN. VS-1은 arming-only 시절엔 CFlag 무한대기로 hang(무한대기 배제 실증).
- **신규 GREEN**: VS-1(CFlagTimeoutTests) 1 + VS-2(D5b) 1 + VS-3 원인판정 5(DepositRecorderCauseTests) + VS-3/4/5 컨트롤러 3 = **10건 신규 전건 GREEN**.
- **baseline 회귀 보존**: HandshakeResidueTests(S1~S6·S5b) + StartupClearTests(VS1~VS3b) = **11건 GREEN**(arming·StartupClear 훼손 0·허위 MISMATCH 0). E2EGroupCD D3~D11 기존 GREEN.
- **전체 `dotnet test backend/Wcs.sln`**: **534 통과 / 0 실패 / 0 건너뜀** — **3회 반복 전건 동일**(1m40s·1m40s·1m33s), teardown hang 0. 신규 실-Sim/타이밍 테스트(VS-1·VS-2·D④) **5회 반복 10/10 GREEN**(flake 0).
- **빌드**: 솔루션 빌드 오류 0·신규 CS 경고 0(기존 NU1903 패키지 취약성 경고만 잔존).
- **절대규칙**: #1(단일 쓰기 큐 — 핸드셰이크 EnqueueAsync 경로 무변경)·#4(Ready 의미 무변경)·#7(CFlagTimeoutMs·cFlagTimeoutMs 설정 주입)·#8(원인 판정 I/O 계층·Wcs.Core 무접촉) 위반 0.

## FIX ITER (Minor 1/2) — Generator, 2026-08-03
- **Minor 1 (fail-loud default)**: `RcsController.cs` IF-10 `switch(recordResult)`에 `default:` 추가 — 향후 enum 비-신규 값이 로그 없이 200으로 조용히 빠지는 '위장유실' 재현 차단. 미매핑 원인을 `_log` WARN + operation_log `IF10_UNMAPPED_CAUSE`(WARN)로 fail-loud, 응답은 200 멱등 유지(IF-10 계약 보존). 현 4값 exhaustive라 런타임 무영향.
- **Minor 2 (주석만)**: `DbRepositories.cs` 상태 전이 else 주석을 catch-all임을 정확히 반영하도록 정정(비정상 종단 상태 MISMATCH/TIMEOUT/CANCELLED도 이 else로 들어와 DEPOSITED로 '부활'→NewRecord = 선재 동작·이번 바이트 보존, 실 가드는 후속). **코드 로직 무변경**.
- enum 매핑·핸드셰이크·동작·200 멱등 전부 불변. #7/#8 유지. 신규 CS 경고 0(B2cFacilityService CS8604만 선재).
