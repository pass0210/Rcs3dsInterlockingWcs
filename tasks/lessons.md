# 교훈 기록

> User correction 발생 시 즉시 기록. 같은 실수를 반복하지 않기 위한 규칙 축적용.
> sprint-feedback.md의 Evaluator 피드백과는 다름 (그건 feedback-archive.md로).

| 날짜 | 실수 | 원인 | 규칙 |
|------|------|------|------|
| 2026-06-16 | S-RTU 6커밋이 feat 브랜치가 아닌 로컬 develop에 올라가고 stale feat가 push됨 | 3-Tier 팀 에이전트가 **격리된 worktree가 아닌 공유 작업트리**에서 동작 — 어느 에이전트가 `git checkout`하면 HEAD가 전역 변경됨. 오케스트레이터가 커밋 직전 브랜치 미확인 | 커밋 직전 항상 `git rev-parse --abbrev-ref HEAD`로 feature 브랜치 확인. develop에 직접 커밋 0. (develop ahead 발견 시 `git branch -f feat <hash>` → `git branch -f develop origin/develop`로 복구) 또는 팀을 worktree 격리(`isolation: worktree`)로 운용 |
| 2026-06-30 | base `appsettings.json`을 `Provider=SqlServer`로 바꾸자 `dotnet test` 105/146 실패("Only a single database provider"). 1차 해결 시도(테스트 팩토리 `ConfigureAppConfiguration`에 `Database:Provider=Sqlite` 주입 / EF SqlServer provider 서비스 디스크립터 제거)가 **둘 다 무효** | ① `WebApplicationFactory<Program>`의 `IWebHostBuilder.ConfigureServices`/`ConfigureTestServices` 콜백 시점엔 minimal-API Program의 `AddDbContext` 등록이 그 `IServiceCollection`에 **안 보임**(EF/DbContext 디스크립터 0개) → "디스크립터 제거" 접근 무효. ② `ConfigureAppConfiguration` 주입은 Program top-level `builder.Configuration["Database:Provider"]` **읽기 이후** 병합돼 provider 선택을 못 되돌림(즉시 평가). IOptions로 지연 소비되는 키(RcsPush:* 등)는 먹히지만 즉시 평가 키는 안 먹힘 | minimal-API `WebApplicationFactory<Program>`에서 **Program이 즉시 평가하는 config 키(예: `Database:Provider`)를 테스트가 override하려면 `builder.UseSetting("키","값")`** (host setting — Program builder 생성 시점에 반영). `ConfigureAppConfiguration`/`ConfigureServices`는 너무 늦음. 디버깅 시 "콜백 시점의 `services`에 대상 디스크립터가 실제 있는지" 먼저 카운트로 확인(없으면 제거 접근 자체가 무효) |
