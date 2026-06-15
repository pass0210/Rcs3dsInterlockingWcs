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
