# 재개 노트 — S-M4-P3 (2026-06-17 17:55 일시정지)

> 사용자 퇴근으로 일시정지. **작업은 전부 working tree에 보존**(브랜치 `feat/m4-p3-scenarios`, 미커밋). 세션 닫아도 디스크에 유지됨. 내일 이 노트 + `tasks/sprint-contract.md`(S-M4-P3) 읽고 이어갈 것.

## 현재 상태 (어디까지 됐나)
- **Generator↔Evaluator 1차 기능 루프**: S1~S9 자동화 + alarm/sorter_command 영속화 결선 → 기능 GREEN(69 통과) 도달했었음.
- **독립 코드리뷰(Step 4.5)**: BLOCK 판정 → 동시성/생명주기 결함 3건 적출. **전부 수정 완료(코드 확인됨)**:
  - MAJOR-1: `PlcGateway.cs` OFFLINE 전이 원자화 — `_online` int + `Interlocked.Exchange(ref _online,0)==1` 승자만 발화 + ONLINE 복구 시 `Interlocked.Exchange(ref _online,1)` 리셋. (비원자 check-then-act 제거)
  - MAJOR-2: `Program.cs` OFFLINE 핸들러 scope+DI+Append 전체 단일 try/catch(Exception)+catch(ObjectDisposedException) — 폴 스레드 격리.
  - MAJOR-3: `DbRepositories.cs` Finalize + `Program.cs` alarm code switch에 `Offline`/`CFlagTimeout` 명시 분기(alarm code `OFFLINE`/`CFLAG_TIMEOUT`, status는 DB CHECK상 TIMEOUT 유지).
  - MINOR-3: `ScenarioTests.cs` S7 3-phase 보강(OFFLINE 1건 → RestartSimAsync로 ONLINE 복구 → 재차단 → alarm OFFLINE 행 ==2).
- **S7 고정 sleep → 조건 대기 전환**: flaky-masking `Task.Delay(2000)` 제거 + 전이는 WaitUntilAsync 전환 완료. 974행(초기 Online 대기)도 `WaitUntilAsync(IsSorterOnline)`로 교체 완료(17:42). **S7 영역 bare Task.Delay 0건.**
- **teardown Fail-Loud 확인**: 폴 루프 catch가 OFFLINE 전이를 가리지 않음(failures++·PublishOffline은 try 밖, try는 로거만) — 코드 근거로 evaluator·orchestrator 확인.

## 내일 재개 — 남은 단계 (정확히 2개)
1. **Evaluator fresh 재검증** (974 교체분 반영된 최종 working tree로): 빌드 0/0 · 전체 `dotnet test` GREEN(회귀 59 + 신규 S1~S9) · S6/S7·S7보강(==2) ≥5회 연속 · 무변경 가드(`git diff develop -- src/Wcs.Core src/Wcs.PlcGateway/HandshakeOrchestrator.cs src/Wcs.Sim3ds` = 동작 변경 0) · 단방향 경계(PlcGateway→Data 참조 0).
   - 일시정지 시점에 evaluator가 이 ×5 재검증을 **실행 중**이었음. 재개 시 evaluator 최종 verdict가 있으면 그걸 확인, 없으면 fresh 재실행.
   - **no-flood 판단 = 해결됨**: settle은 삭제된 게 아니라 `WaitUntilExactAsync(expected, stablePollMs:60, stableCount:5)`(헬퍼 1047~1063행)로 교체됨 — count가 ~300ms 동안 expected로 유지돼야 통과, flood로 2 되면 불일치→실패. 바운드 sleep보다 회귀 가드 강하고 무변경 가드(PlcSnapshot/Sim3ds) 안 건드림. **settle 복원 불필요.**
2. **Orchestrator 최종 코드리뷰**(teardown Fail-Loud 직접 확인) → **세분 커밋 + 푸시 + PR #11** (커밋 전 `git rev-parse --abbrev-ref HEAD` = feat/m4-p3-scenarios 확인 — S-RTU 브랜치 사고 교훈). 병합은 사용자.

## 절대 준수 (재개 시)
- 커밋·브랜치 전환은 **orchestrator만**(에이전트 금지). 커밋 전 HEAD 확인 필수.
- 무변경 가드: Wcs.Core 판정·HandshakeOrchestrator·Sim3ds 본문 0. PlcGateway는 이벤트 노출+전이 원자화+teardown 방어만(정상 경로 동작 불변).
- 미커밋 working tree: src/Wcs.Api/{DbRepositories,Program,Repositories,SorterGatewayRegistry}.cs · src/Wcs.PlcGateway/PlcGateway.cs · tests/Wcs.Tests/{ScenarioTests.cs(신규),ApiIntegrationTests.cs} · tasks/sprint-contract.md.
