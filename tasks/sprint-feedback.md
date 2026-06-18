# Sprint Feedback — S-M4-P3 (시나리오 S1~S9 + alarm/sorter_command 영속화) — APPROVED

## Phase 3 Evaluate 결과 (evaluator2 fresh evidence, working tree 기준)

**최종 판정: APPROVE** — 7항 전부 fresh 직접 실행, 주장 아닌 실측 수치.

1. **build**: exit 0, 경고 0 / 오류 0.
2. **full test**: 총 69 / 통과 69 / 실패 0, exit 0. ObjectDisposedException 0줄(grep -c=0). 회귀 59 + 신규 S1~S9 10 = 69.
3. **타이밍 민감 ≥5회 연속**: S6RFlagTimeout + S7Offline(==2 보강·no-flood 포함) 5회 연속 전부 `실패:0 통과:2`. flaky 0.
4. **MAJOR 수정 코드 확인**:
   - MAJOR-1 PlcGateway.cs:308 `Interlocked.Exchange(_online,0)==1` 승자만 OnOfflineTransition 발화 / :241 ONLINE 복구 시 `_online=1` 리셋. 비원자 check-then-act 제거.
   - MAJOR-2 Program.cs:556~575 OFFLINE 핸들러 scope+DI+Append 단일 try/catch(ObjectDisposedException + Exception). IF-11 콜백도 scope 생성 catch(ObjectDisposedException). 폴 스레드 격리.
   - MAJOR-3 DbRepositories.cs:716~746 + Program.cs:367~374 Offline/CFlagTimeout 명시 분기, alarm code OFFLINE/CFLAG_TIMEOUT로 사유 구분.
5. **S7 고정sleep 0**: ScenarioTests.cs S7 영역(964~1029) bare Task.Delay 0건. no-flood = `WaitUntilExactAsync(expected, stableCount:5)` — flood로 어긋나면 실패하는 강한 회귀가드.
6. **무변경 가드**: `git diff develop -- src/Wcs.Core src/Wcs.PlcGateway/HandshakeOrchestrator.cs src/Wcs.Sim3ds` = 빈 diff. PlcGateway.cs(+30/-4)는 계약 허용분만(이벤트 노출·Interlocked 원자화·로거 disposed 방어; failures++/PublishOffline은 try 밖 → Fail-Loud 보존).
7. **단방향 경계**: src/Wcs.PlcGateway에 Wcs.Data/EntityFrameworkCore/DbContext 참조 0. csproj ProjectReference=Wcs.Core 단독.

## 시나리오별 PASS (전부)
S1 정상→sorter_command COMPLETED+CSeq==RSeq / S2 WRONG_FLOOR+D6 1건 / S3 BUSY 선기입·분류시작 클리어 / **S4 TgtFloor≠0 구간 D6 쓰기 ==1·추가 0건(핵심)** / S5 R_SEQ_MISMATCH alarm+MISMATCH / S6 RFLAG_TIMEOUT alarm+TIMEOUT 1행(재시도 0) / S7 OFFLINE 3-phase(==2)+no-flood / S8 FULL·PAUSED / **S9 단일 TgtFloor 선점·타층 D6 0·클리어 후 양보**.

## Orchestrator 최종 코드리뷰 (Step 4.5 fix 재확인)
MAJOR-1/2/3 수정 정확(diff 직접 확인). Fail-Loud 보존(OFFLINE 판정이 로거 try 밖). 갭 결선 단방향 경계 유지. EF sink 단일 트랜잭션·UTC·rollback+throw.
- **MINOR-1(M5 이연, 비차단)**: IF-10 ContinueWith에서 GetRequiredService 2줄이 inner try 밖 → DI 해석이 던지면 말미 `cellSelector.ReleaseCell` 스킵→셀 누수 가능. 단 호스트 종료/DI 오설정 한정(정상 운영 0). M5 정리 후보.

## 독립 코드리뷰 이력
1차 BLOCK(MAJOR 3건) → iter2 수정(Interlocked 원자화·핸들러 격리·명시 분기·S7 3-phase·고정sleep→WaitUntilExactAsync) → 재검증 APPROVE. 상세 [[feedback-archive]] S-M4-P3 [CODE-REVIEW] 라인.
