[Sprint Contract] — S-TWO-FLOOR-CONTROL 서브 스프린트 C2 (콜드스타트 복구: 기동 레지스터 클리어 + I-3 pending-floor 큐 재파생)

> 선행 병합: A(PR #76)·B(PR #77)·C1(PR #78). C는 Planner가 C1/C2/C3 3분할. 본 계약 = C2.
> 설계 권위(SOURCE OF TRUTH): docs/SPEC.md §4-B(기동 클리어)·§4-A(arming)·§6(Sim), docs/ERD.md(piece 상태·sorter_command).
> ⚠ 아티팩트 복원본: 이 계약 파일은 브랜치 생성/평가 stash 과정에서 C2 원본이 유실돼 오케스트레이터가 확정 내용(Planner 산출 + 사용자 게이트 결과 + 구현/평가 결과)으로 복원함. C2 코드·검증은 정상 완료(Generator 프롬프트에 전체 스코프 인라인 + stash 복구). 교훈: 브랜치 생성 후 계약 커밋 여부 즉시 검증 / Evaluator baseline stash는 pop 보장·-u 사용.

## Goal
WCS 프로세스가 (에러 등으로) 꺼졌다 다시 기동할 때 콜드스타트 1회 복구로 (1) WCS 소유·기입 레지스터를 위생 초기화하고 (2) 유실된 인메모리 pending-floor 큐를 미완료 piece에서 재파생하여, 잔류 핸드셰이크·잔류 목표층·유실 큐 없이 깨끗·정합 상태로 재개한다. SPEC §4-B + A 이연 I-3.

## Detected Project Type: Full-stack (backend 전용 — Web/UI N/A·frontend diff 0)

## Implementation Scope (WHAT)
- S1 기동 레지스터 클리어: 콜드스타트(기동 첫 유효 Online 폴 1회·무조건·이미 0이면 no-op) 시 단일 쓰기 큐(#1)로 C_CellNo(D0)·C_Seq(D1)·C_Flag(D4.0)/R_CellNo(D2)·R_Seq(D3)·R_Flag(D4.1)/TgtFloor(D6)=0. D4 RMW로 Ready(D4.2) 보존·CurFloor(D5) 미접촉. TgtFloor는 핑퐁 가드 우회 콜드스타트 전용 쓰기. IF-08 부트스트랩 push보다 먼저(clear-before-push 배리어). §4-A R_Flag reconcile 포섭·정합.
- S2 I-3 큐 재파생: 미완료 SORTER_3D piece에서 소터별 큐 읽기 전용 재구성(piece 단일 진실·상태 변경 0). 상태집합={RESERVED,PERMITTED,DEPOSITED,CELL_ASSIGNED}(수용확정·미LOADED). 순서=소터별 IF-05 순(CreatedAt→Id). 층 F=InductionFloorMap(설정) 파생·미매핑 skip+경보(Fail Loud). 관측 첫 소비 전 복원(restore-before-observe).
- S3 순서: S1=폴 온라인 후 && IF-08 부트스트랩 전. S2=관측 첫 소비 전.

Scope OUT: C3·D·D4.3·C1 병합 로직·Modbus 맵·frontend. 마이그레이션 0(스키마 무변경).

## 확정 결정 (사용자 게이트 — 2026-07-24, C2)
- (a) 절대규칙 #3 문언 정정 승인 → CLAUDE.md #3 "정상 운영 중 금지/콜드스타트 1회 리셋 허용"으로 오케스트레이터 별도 커밋(f0f5927). Generator는 CLAUDE.md 미변경.
- (c) I-3 = A안 piece 재파생(읽기 전용). 큐 DB 영속화·마이그레이션 안 함.
- (d~g) Planner 권장 확정: 상태집합/IF-05 순/미매핑 skip+경보 · 클리어 대상 C·R·TgtFloor(D4 RMW·Ready 보존·CurFloor 미접촉) · 트리거 기동 1회 무조건 · arming·C1 R-clear와 R 영역 정합.

## 절대규칙: #1 큐 경유·#3(개정)콜드스타트만·#4 Ready/CurFloor 보존·#7 하드코딩0·#8 Core diff0.

## Completion Conditions: 전체 GREEN(신규 포함, clean-env ≥5회 안정·TIME_WAIT 드레인 후·abort는 환경 귀속 시 배제) · S1/S2 실 Sim3ds 실증(클리어 before push·복원 before 관측·Ready/CurFloor 보존) · 절대규칙 코드 직독 · 마이그레이션 0(has-pending 양 provider "No changes") · 무접촉존 diff0.

## Verification Scenarios (Full-stack)
- Web/UI: N/A(frontend diff0).
- Backend/API: 기동 시퀀스 S1 클리어·S2 복원, 잔류 레지스터 주입·미완료 piece 시드·미매핑 경계.
- E2E: 실 Sim3ds(Tcp) 재시작 — 잔류 주입 기동→StartupClear(C/R/TgtFloor=0·Ready/CurFloor 보존)가 IF-08 부트스트랩보다 먼저→미완료 piece 큐 재파생→관측 정렬. Modbus 게이트·기동 서비스·DB(piece) 관통.

> Planner self-check — Detected project type: Full-stack. Required scenario slots: 3 (Web/UI[N/A·frontend diff0], Backend/API, E2E). All slots filled: yes.

## 로드맵: C3(스톨 감지기+pusher ComputeSorterFull 경량화) / D(파킹존) / ⏸D4.3(PLC 게이트 이연).
