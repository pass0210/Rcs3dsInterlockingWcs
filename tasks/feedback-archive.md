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
