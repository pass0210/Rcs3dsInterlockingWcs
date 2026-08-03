# Sprint Feedback — S-AUDIT-D-HANDSHAKE-HARDENING

## APPROVED (2026-08-03, iteration 1 · 2차원 Evaluator pool aggregate)

전 차원 PASS (AND). 상세: tasks/sprint-feedback/functional.md · tasks/sprint-feedback/concurrency.md.

### 재triage 반영
D①(R_Flag arming)·D③-part1(StartupClear)은 S-HANDSHAKE-RESIDUE/양층 C2에서 이미 해소 → SCOPE OUT(회귀 보존 R-확인). D③-part2(SENT 저널 시점)는 사용자 게이트 SCOPE OUT(SPEC §7-B '수용된 갭' 등재). 남은 유효 **D②·D④** 구현.

### 차원 1 — Functional: **PASS**
- **D④ 원인 분리**: `DepositRecordResult{NewRecord,Duplicate,DeniedReport,NoDestination}` enum이 I/O 계층(Repositories.cs)·Wcs.Core git diff 0(#8). EfDepositRecorder 6 return 원인별 정확 매핑·트랜잭션 경계/branching 불변.
- **IF-10 원인별 로그**: switch — Duplicate→INFO '멱등 OK'(현행) / DeniedReport→WARN+IF10_DENIED_REREPORT / NoDestination→WARN+IF10_NO_DESTINATION. 전 케이스 200 OK·NewRecord만 후속 트리거(조기 return 바이트 보존).
- 현동작 고정 테스트 비공허(CapturingLogger 실메시지·고유 pId·DoesNotContain "멱등 OK"). 데이터 무결·정책 미변경(alarm 미승격). 회귀 0(534=524+10 산술 일치·PlcGateway/HandshakeOrchestrator diff 0·Sim3ds test-only). D④ 필터 9/9 GREEN.
- **Minor(비차단·등재)**: RcsController IF-10 switch에 default 없음 — 현 4값 exhaustive라 결함 아니나 향후 enum 확장 시 무로그 200 fail-loud 사각. default throw/switch expression 권고.

### 차원 2 — Concurrency/Timing: **PASS**
- **D② VS-1**(오케스트레이터 단위 재실행): Outcome=CFlagTimeout **단독**·elapsed 508ms(CFlagTimeoutMs=500 주입·ε=8ms·무한대기 배제)·Online=True(진짜 C_Flag 타임아웃)·HS_C_SENT/CELL_ASSIGN 부재. 하니스 RFlagTimeoutMs=3000이라 fallthrough면 ~3500>상한 → 이중 배제.
- **D② VS-2**(D5b·IF-10 유발): alarm=CFLAG_TIMEOUT 단독(RFLAG_TIMEOUT 부재)·sorter_command/piece=TIMEOUT. 기존 D5 CFLAG||RFLAG 택일 모호성 실제 제거.
- **flake 배제**: 타이밍+arming 회귀 13테스트(CFlagTimeout·HandshakeResidue S1~S6/S5b·StartupClear·D5b) **6회 반복 13/13×6 GREEN**·타이밍 flake 0·teardown hang 0.
- **arming 불변식 보존**: HandshakeOrchestrator.cs·PlcGateway.cs diff **0바이트**(제어흐름·arming 무변경). #1/#4 diff 0·#7 CFlagTimeoutMs appsettings 실키 주입.

**APPROVED** — Step 4.5 코드리뷰 진행 가능. 커밋 스코프: Repositories.cs·DbRepositories.cs·RcsController.cs·SimSlave.cs·SimServer.cs·docs/SPEC.md + 신규/수정 테스트(CFlagTimeoutTests·DepositRecorderCauseTests·E2EGroupCD·E2EInfrastructure·ApiIntegrationTests·DataIntegrityAuditTests) + 프로세스 파일.

## Step 4.5 코드리뷰 (2026-08-03) — Critical 0 · Major 0 · Minor 2 → 하드닝 후 APPROVED 유지
enum 매핑 6→4 정확·동작 바이트 보존·SetCResidue test-only 격리·SPEC 정확·#7/#8 clean.
### FIX ITER (Minor 1/2 하드닝 — 사용자 결정·Functional 재검증 PASS 유지)
- **Minor 1 (fail-loud default)**: RcsController IF-10 switch에 `default:` 추가 — 미매핑 원인 WARN(_log + operation_log `IF10_UNMAPPED_CAUSE`)+200 유지. 현 4값 exhaustive라 런타임 무영향(default 도달 불가)·무음 사각 제거(향후 enum 확장 대비). S-IF08 M4 동형.
- **Minor 2 (주석만)**: DbRepositories 상태전이 else 주석을 catch-all로 정정(executable byte-identical·로직 diff 0).
- 재검증: dotnet test **534 GREEN**(fresh 재실행·teardown hang 0)·Wcs.Core/PlcGateway/HandshakeOrchestrator diff 0·신규 경고 0. concurrency/timing 차원 무영향(핸드셰이크 무접촉).
### 잔여 Minor (등재 — 다음 스프린트/후속)
- **CR-M2(선재)**: RecordDeposit 상태전이 catch-all이 비정상 종단(MISMATCH/TIMEOUT/CANCELLED) piece도 DEPOSITED로 부활시켜 NewRecord 성공 처리 — 선재 동작(이번 바이트 보존). 명시 가드는 후속(감사 별개 항목). 관련: audit-20260701 §RecordDeposit destId 미교차검증.
