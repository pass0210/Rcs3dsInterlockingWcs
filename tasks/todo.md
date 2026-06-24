# TODO (sprint 간 추적 — Minor·이연 항목)

## M5 / CI 이연
- [x] [S-RCS-IF-REDESIGN P1] ~~full `dotnet test` teardown hang(BLOCKING-for-CI)~~ **해소됨(PR #12 teardown-fix 커밋)** — SorterBundleHandle.StopPollingAsync가 쓰기 큐 Writer.TryComplete()로 컨슈머 결정적 종료(빈 채널 CTS-only 취소 경쟁 해소). evaluator 6회 연속 exit0·70/70·hangdump0 + 독립 코드리뷰 APPROVE. PlcGateway 본문 무변경(Wcs.Api disposal 결선만).
- [ ] [정리] 미등록 dead code `PlcPollingHostedAdapter.StopAsync`가 큐 완료 누락(production DI 미등록·P2a 레거시 — SorterRegistryFactory로 대체됨). 제거 또는 동일 complete 추가(라이브 위험 0, 함정 제거용). 독립 코드리뷰 MINOR.

## 선행 sprint 이연(기록 유지)
- [ ] [S-M4-P2b] SorterRegistryFactory 번들 Dispose 누수(종료 시 _master/_cts/_clientLock 미dispose, 포트는 해제) — M5 graceful shutdown. ↑ teardown hang과 같은 뿌리.
- [ ] [S-M4-P3] IF-10 ContinueWith GetRequiredService throw 시 ReleaseCell 스킵 셀누수(호스트종료/DI오설정 한정) — M5.
