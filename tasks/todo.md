# TODO (sprint 간 추적 — Minor·이연 항목)

## M5 / CI 이연
- [x] [S-RCS-IF-REDESIGN P1] ~~full `dotnet test` teardown hang(BLOCKING-for-CI)~~ **해소됨(PR #12 teardown-fix 커밋)** — SorterBundleHandle.StopPollingAsync가 쓰기 큐 Writer.TryComplete()로 컨슈머 결정적 종료(빈 채널 CTS-only 취소 경쟁 해소). evaluator 6회 연속 exit0·70/70·hangdump0 + 독립 코드리뷰 APPROVE. PlcGateway 본문 무변경(Wcs.Api disposal 결선만).
- [ ] [정리] 미등록 dead code `PlcPollingHostedAdapter.StopAsync`가 큐 완료 누락(production DI 미등록·P2a 레거시 — SorterRegistryFactory로 대체됨). 제거 또는 동일 complete 추가(라이브 위험 0, 함정 제거용). 독립 코드리뷰 MINOR.

## S-RCS-IF-REDESIGN P2 이연 (Minor — 비차단)
- [ ] [S-RCS-IF-REDESIGN P2] 슈트 실패 푸시의 자율 복구 없음 — 소터는 관찰 타이머가 매 주기 재구동하나, 슈트는 상태변화 이벤트가 없으면 실패 푸시가 다음 IF-05/10까지 stale(자율 RCS 복구 폴러 없음). 확정3 경계 내·상태 오염 0(Acked 불변)이나, RCS 장시간 다운 후 복구돼도 무변화 슈트는 미동기. RCS 복구 시 부트스트랩 재기동 또는 주기 복구 스윕 필요하면 후속 스프린트.
- [ ] [S-RCS-IF-REDESIGN P2] `RcsPushOptions.Path` 기본 리터럴 운영 문서화 — 외부화된 설정 기본값(절대규칙 #7 위반 아님)이나 스펙 경로 변경 시 appsettings 오버라이드 가능함을 운영 문서에 명기 권고.

## 선행 sprint 이연(기록 유지)
- [ ] [S-M4-P2b] SorterRegistryFactory 번들 Dispose 누수(종료 시 _master/_cts/_clientLock 미dispose, 포트는 해제) — M5 graceful shutdown. ↑ teardown hang과 같은 뿌리.
- [ ] [S-M4-P3] IF-10 ContinueWith GetRequiredService throw 시 ReleaseCell 스킵 셀누수(호스트종료/DI오설정 한정) — M5.

## S-RCS-IF-REDESIGN P2 후속(코드리뷰 MINOR — 비차단)
- [ ] [P2] 슈트(CHUTE) 복구 재푸시 비대칭 — 관찰 타이머가 SORTER_3D만 재평가. RCS 다운으로 슈트 푸시 실패 시 다음 슈트 이벤트(예약/투입/비움) 전까지 stale(상태오염 0·확정3 "다음 전이 시"는 충족, "복구 감지 시"는 미충족). 한산한 슈트 장시간 stale 가능. → 슈트도 주기 재평가 또는 RCS 헬스 복구 시 전 목적지 재펌프(하트비트 결정과 함께 후속).
- [ ] [P2] teardown 중 disposed-CTS 접근 spurious error 로그 — DisposeAsync의 _cts.Dispose 후 lingering PumpAsync가 _cts.Token 접근 시 ObjectDisposedException→generic catch LogError(크래시·hang·미관찰예외 0·종료 클린). token 취득을 _stopped 가드로 감싸 조용히 종료 분기 권고.
