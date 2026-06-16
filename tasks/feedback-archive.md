# Feedback Archive

스프린트별 평가에서 도출된 재사용 가능한 핵심 피드백.

## S-M0 (솔루션 구성 + 빌드 그린) — APPROVED (2026-06-15, 1 iteration to pass)

- **핵심 교훈 (M0-1)**: SDK 10.0.300에서 `dotnet new sln -n <Name>`은 클래식 `.sln`이 아니라
  `.slnx`(XML 형식)를 기본 생성한다. 계약/검증 명령이 `Wcs.sln`(클래식)을 전제하면
  `dotnet build Wcs.sln`이 MSB1009로 실패한다. 클래식 형식이 필요하면 `--format sln`을 명시.
  → 향후 sln 생성 시 산출물 확장자를 계약 문자 그대로 맞추고, 발산 시 보고서에 명시할 것.
- **검증 원칙 확인**: 생성자가 보고에서 명령을 임의로 바꿔 적으면(여기선 `Wcs.sln`→`Wcs.slnx`)
  발산이 가려진다. Evaluator는 항상 계약 문자 그대로의 명령으로 ground truth 재현해야 함.
- **PASS 구성**: Core 의존성 0(참조·패키지 모두), Sim3ds 프로젝트 참조 0(FluentModbus 패키지만),
  FluentModbus가 Core/Api/Data로 누출 안 됨, 테스트 패키지는 Wcs.Tests에만, net10.0 6개 유지.
  스켈레톤 .cs/.json 무변경 + DepositDecider.Decide 9 RED(NotImplementedException) / Wire 1 GREEN이
  M0의 정상 시작점.
- [CODE-REVIEW] sprint=S-M0 critical=0 major=0 minor=0 iter=0 opus=no (설정/배선 전용 — 소스 로직 0, 오케스트레이터 레벨 diff 리뷰로 가름)

## S-M1 (판정 엔진 DepositDecider) — APPROVED (2026-06-15, 1 iteration to pass)

- **핵심 교훈 (테스트가 스펙)**: SPEC §2 표 7행을 코드 분기 순서(Offline→Hold→Ready/층)와 1:1로
  대조하면 표 밖 동작을 즉시 잡을 수 있다. TgtFloor 쓰기 조건은 한 줄(`Tgt==0 && (cur!=agvFloor || !Ready)`)
  이고 Hold/Offline은 선행 우선순위에서 차단되어 쓰기 분기에 도달하지 않음 — 이 구조가 "Hold/Offline 쓰기 금지"를
  코드로 보장한다(별도 가드 불필요). C1=잔류 TgtFloor 허가 경계, C2=선기입 조건 충족해도 Hold/Offline 차단,
  C3=층일치·Ready=1이어도 Hold 우선 — 세 경계가 표의 함정을 정확히 인코딩.
- **순수성 검증법**: Decide가 static·무필드·DateTime/Random/I-O 없음 + `Wcs.Core.csproj`에 Reference/Package 0
  두 가지로 확정. 테스트의 `DateTimeOffset.UtcNow`는 Snap 헬퍼 한정(판정 로직 밖)이라 비순수 아님 —
  비순수 오탐 주의.
- **검증 환경**: 이 환경은 PowerShell 권한 거부 → Bash(Git Bash)로 `cd "<절대경로>" && dotnet ...` 실행.
  Evaluator는 도구가 막히면 우회 경로로라도 ground truth를 직접 재현할 것(요약 신뢰 금지).
- **E4 범위 판정**: 코드 surface는 src/tests. `tasks/` 하위 sprint-contract.md·sprint-log.md 변경은
  3-Tier 하네스 산출물이라 코드 범위 위반 아님 — `git diff --name-only HEAD`에서 src/tests만 필터해 판정.
- [CODE-REVIEW] sprint=S-M1 critical=0 major=0 minor=0 iter=0 opus=yes (독립 Opus 코드리뷰어 — SPEC §2 7행 1:1·순수성·TgtFloor 클리어 없음·C1~C3 단언 정확, 결함 0. 관찰: WcsHold enum 확장 시 fall-through는 현 enum에선 비결함)

## S-M2 (PLC 게이트웨이 + 시뮬레이터 핸드셰이크) — APPROVED (2026-06-15, 3 iterations to pass)

- **핵심 교훈 (단일 큐 ≠ 단일 소켓 안전)**: 절대규칙 #1(쓰기 단일 큐)을 SingleReader Channel + 단일 컨슈머로
  지켜도, 폴 루프(읽기)와 쓰기 컨슈머(쓰기·RMW)가 **같은 ModbusTcpClient를 별 태스크에서 동시 사용**하면
  소켓 레벨 직렬이 아니다. FluentModbus ModbusTcpClient는 단일 소켓·단일 트랜잭션 버퍼라 동시 호출
  thread-safe 아님 → 프레임 교차·버퍼 경합. 해법: 공유 `SemaphoreSlim(1,1)`로 모든 `_client` 트랜잭션을
  감싸고 **RMW read+write를 한 임계구역**으로 묶음(폴 읽기·각 Write·RMW read+write가 모두 게이트 통과).
  컨슈머 임계구역 내 early `return`도 try/finally로 Release 보장 — 락 누수 없음. SemaphoreSlim 비재진입이나
  RmwD4LockedAsync가 재획득 안 하므로 데드락 없음.
- **회귀 가드**: "폴 진행 중 다수 핸드셰이크 연속(IT-3c)"으로 R_Seq==C_Seq 대사가 매 건 성공함을 단언하면
  프레임 무결성을 동작으로 입증. 직렬 핸드셰이크라도 폴(30ms)이 계속 돌아 poll-vs-write 교차를 커버.
- **검증 원칙 재확인**: 생성자 1차 재제출이 FAIL-2를 "수정 완료" 없이 원 구현만 재기술 → Evaluator가
  `grep`로 `_client` 직렬화 프리미티브 부재(0건)를 ground truth로 확인해 미해소 적발. 요약 신뢰 금지·소스 재검사 필수.
- **flaky 회피 확정법**: 동시성/락 변경은 데드락·타이밍 리스크 → `dotnet test` 4회 연속 GREEN + 테스트 split
  (15 Decider + 8 통합) 불변 확인으로 결정성 입증. FAIL-1(고정 sleep 80ms 제거)은 GW Online 폴링이 흡수.
- **코드리뷰 후속 (off-lock 접근)**: 단일 소켓 직렬화는 폴 읽기·쓰기뿐 아니라 **Disconnect/재연결**도 포함해야 완전.
  폴 catch의 TryReconnect(_client.Disconnect())가 락 밖이면 진행 중 쓰기 트랜잭션과 경합 → `_clientLock` 임계구역으로
  이동해 해소. 검증법: 전 `_client.` 사용처를 grep해 각각 (a)락 보유 중 (b)종료 후 단일스레드 중 하나임을 확인.
  회귀 가드 IT-4b(핸드셰이크 중 서버 단절·재기동 후 후속 핸드셰이크 Success)로 동작 입증. 죽은 코드(테스트 동기화용
  TCS)도 제거 — 실제 테스트는 폴링 대기(WaitUntilAsync) 사용이라 불필요했음.
- [CODE-REVIEW] sprint=S-M2 critical=0 major=1 minor=4 iter=1 opus=yes (독립 Opus 코드리뷰어가 BLOCKING 1건 적발: off-lock _client.Disconnect 경쟁 — 폴 catch의 TryReconnect가 _clientLock 밖에서 실행돼 쓰기 트랜잭션과 소켓 경합. 기능 테스트 24/24 GREEN였지만 구조적으로 못 잡는 동시성 버그. fix-only 1 iter로 해소: Disconnect를 _clientLock 임계구역으로 + IT-4b(단절-중-핸드셰이크) 회귀 가드 추가. minor 4: 죽은 TCS 동기화 코드·"BackgroundService" 주석·InjectNoResponse 주석·IT-3c 과대명명 — 함께 정리. 재검증 RESOLVED, _client. 전 사용처 락 보호 확인, 데드락 없음)
- **메타 교훈**: 기능 Evaluator APPROVED ≠ 코드리뷰 통과. 4-Tier 코드리뷰가 테스트·기능검증이 구조적으로 못 잡는 동시성 결함을 머지 전 한 겹 더 걸러냄. 동시성 코드는 반드시 독립 리뷰.
