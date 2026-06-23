# TODO (sprint 간 추적 — Minor·이연 항목)

## M5 / CI 이연
- [ ] [S-RCS-IF-REDESIGN P1] **full `dotnet test` teardown hang(BLOCKING-for-CI)** — 단언 70/70 PASS이나 testhost가 teardown에서 행→run abort→exit 1. 선재(develop @ 1501ccd 동일 재현)·근본원인은 계약 무변경 zone(PlcGateway 폴 루프/IHost disposal). M5 graceful shutdown(SorterRegistryFactory IAsyncDisposable + 번들 DisposeAsync — P2b 교훈)에서 해소. CI는 `--blame-hang-timeout`로 단언 결과는 얻되 exit 1 별도 처리 필요.
- [ ] [S-RCS-IF-REDESIGN P1] sprint-log.md 문구 정정 — "`--blame-hang-timeout 90s`로 결정적 통과(5/5 GREEN)" → "단언 PASS이나 teardown hang으로 run abort(선재)"가 정확(Evaluator fresh 실측 불일치).

## 선행 sprint 이연(기록 유지)
- [ ] [S-M4-P2b] SorterRegistryFactory 번들 Dispose 누수(종료 시 _master/_cts/_clientLock 미dispose, 포트는 해제) — M5 graceful shutdown. ↑ teardown hang과 같은 뿌리.
- [ ] [S-M4-P3] IF-10 ContinueWith GetRequiredService throw 시 ReleaseCell 스킵 셀누수(호스트종료/DI오설정 한정) — M5.
