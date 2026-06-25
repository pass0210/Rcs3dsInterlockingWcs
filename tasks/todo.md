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

## S-소터셀수량full 후속(코드리뷰 재리뷰 MINOR — 비차단·다음 정리/M5)
- [ ] [정리] `SorterHasAssignedCellWithRoomForBarcode`(`DestinationStatusService.cs:74,121`) orphan — production 호출자 0(IF-05는 `SorterCanAcceptBarcode`만 사용), 테스트 5곳(HP-1·EC-1·EC-2·EC-6)에서만 호출. 둘 다 내부 `HasAssignedCellWithRoom` 공유라 현재 정합하나, 향후 갈라지면 테스트가 production 경로를 못 지킴. → 인터페이스에서 제거 + 테스트를 `SorterCanAcceptBarcode`로 통일하거나 "테스트 introspection 전용" 명시. (정확성 무해.)
- [ ] [정리] `Compute` 본문 주석(`DestinationStatusService.cs:253`)에 "단일 원자 쿼리로 평가" 잔존 — `ComputeSorterFull` 주석은 2-쿼리로 정정됐으나 이 1줄 미반영. 주석 불일치(정확성 무해). → "같은 스코프 순차 읽기"로 정정.
- [ ] [정보성] 동일 오더 **동시 IF-10 2건**이 같은 여유 셀을 둘 다 읽고 적재 시 일시 Capacity 초과(soft-threshold, 1건 바운드·자가수렴). m4p4에도 없던 용량 모델 본질 특성이며 이 fix가 악화 안 함(SelectCell tx는 배정 INSERT만 직렬화, sorter_command 적재는 별도 핸드셰이크 콜백). 계약 §88-89 "단일 응답 내부 불변식 + eventually-consistent" 범위 내(교차 IF-10 직렬 capacity 강제 미약속). 강제 직렬화 필요 시 후속(만재센서 도입과 함께 재검토).

## S-소터push운영상태 후속(코드리뷰 정보성 — 비차단)
- [ ] [문서부채·선재] `docs/SPEC.md` §2(line 20·60)가 **폐지된 구 IF-08(deposit-permission 폴링) 모델**을 기술 — RCS 재설계로 IF-08=WCS→RCS 상태 푸시로 대체됐으나 SPEC.md는 미반영(canonical 정의서 wcs_rcs_interface_kr.html은 최신). 이 스프린트 이전부터의 문서 부채. 별도 문서 정리에서 SPEC.md IF-08 섹션을 푸시 모델(+소터 push=운영상태 / IF-05=셀·관리 게이트 2단계)로 갱신 권고.
- [ ] [정리·정보성] `DestinationReadiness.Full`/`Paused`/`Reason` 필드가 현재 **production 미소비**(테스트 introspection·내부 산출 전용 — IF-05는 `SorterCanAcceptBarcode`·`r.Paused`만, push는 `r.Ready`만). dead-but-consistent. 향후 이 필드들의 소비처가 생기면 "ready=true && Full=true 공존" 의미를 재확인할 것(현재는 무해).

## S-RCS-IF-REDESIGN P2 후속(코드리뷰 MINOR — 비차단)
- [ ] [P2] 슈트(CHUTE) 복구 재푸시 비대칭 — 관찰 타이머가 SORTER_3D만 재평가. RCS 다운으로 슈트 푸시 실패 시 다음 슈트 이벤트(예약/투입/비움) 전까지 stale(상태오염 0·확정3 "다음 전이 시"는 충족, "복구 감지 시"는 미충족). 한산한 슈트 장시간 stale 가능. → 슈트도 주기 재평가 또는 RCS 헬스 복구 시 전 목적지 재펌프(하트비트 결정과 함께 후속).
- [ ] [P2] teardown 중 disposed-CTS 접근 spurious error 로그 — DisposeAsync의 _cts.Dispose 후 lingering PumpAsync가 _cts.Token 접근 시 ObjectDisposedException→generic catch LogError(크래시·hang·미관찰예외 0·종료 클린). token 취득을 _stopped 가드로 감싸 조용히 종료 분기 권고.
