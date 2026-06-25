# 교훈 기록

> User correction 발생 시 즉시 기록. 같은 실수를 반복하지 않기 위한 규칙 축적용.
> sprint-feedback.md의 Evaluator 피드백과는 다름 (그건 feedback-archive.md로).

| 날짜 | 실수 | 원인 | 규칙 |
|------|------|------|------|
| 2026-06-16 | S-RTU 6커밋이 feat 브랜치가 아닌 로컬 develop에 올라가고 stale feat가 push됨 | 3-Tier 팀 에이전트가 **격리된 worktree가 아닌 공유 작업트리**에서 동작 — 어느 에이전트가 `git checkout`하면 HEAD가 전역 변경됨. 오케스트레이터가 커밋 직전 브랜치 미확인 | 커밋 직전 항상 `git rev-parse --abbrev-ref HEAD`로 feature 브랜치 확인. develop에 직접 커밋 0. (develop ahead 발견 시 `git branch -f feat <hash>` → `git branch -f develop origin/develop`로 복구) 또는 팀을 worktree 격리(`isolation: worktree`)로 운용 |
