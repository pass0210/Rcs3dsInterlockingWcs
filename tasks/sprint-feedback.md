# Sprint Feedback — S-HARDENING-1 (슈트 복구 하트비트 + 멀티소터 해제 격리 회귀 잠금 + 핫패스 인덱스 2종 + 라이드얼롱 2건)

**APPROVED** — Evaluator, 2026-07-13 (1 iteration to pass).

브랜치 `feat/hardening-1`. 스프린트 변경분은 전부 **미커밋 working tree**(HEAD=`ded2bea`, 이전 스프린트 커밋 — orchestrator 가 승인 후 커밋). 재핸드오프 아님(HEAD 에 이 스프린트 커밋 없음 · working tree 에만 존재). Backend/API 단일 차원(functional only, 계약 선언). Evaluator 는 코드를 고치지 않음. 커밋/푸시 0.
Ground truth = git diff/status + 변경 코드 직접 판독 + **독립 재실행 dotnet test**(full 1× + push군 5× + E2E군 5×) + **가짜 RCS 수신 서버(FakeChuteStateServer)가 실제 수신한 본문** + **양 provider 스크래치 DB `ef database update` 실적용 + 스키마 카탈로그 조회**. Generator 요약(330 GREEN 등)은 신뢰하지 않고 전부 독립 재현.

핸드오프 확인: `tasks/sprint-log.md` **L3307**(파일 최말단)에 `## IMPLEMENTATION COMPLETE — S-HARDENING-1 (Generator, 2026-07-13)` 마커 존재(L3078~L3257 의 stale 마커 4건이 아니라 말단 마커가 정본) → 활성화 정당.

---

## [Completion #1] 빌드 — PASS

- `dotnet build backend/Wcs.sln`: **오류 0**. 경고 전부 선재 **NU1903**(`SQLitePCLRaw.lib.e_sqlite3` 2.1.10 advisory — 계약 명시 제외) × 5 프로젝트. **신규 경고 0**.
- 빌드 전 고아 프로세스 점검(`Wcs.Sim3ds.exe`/`Wcs.Api.exe` tasklist): 0건 — MSB3021 함정 배제 상태에서 빌드.

## [Completion #2 / VS-4 후단] 테스트 — 독립 재실행 fresh evidence — PASS

- full `dotnet test backend/Wcs.sln`(처음부터 재실행): **`통과! 실패:0 통과:330 건너뜀:0 전체:330` (19s)**. 327 선재 + 신규 3(HB-1·HB-2·EC-14) 산술 일치. 회귀 0·skip 0.
- **결정성(flake 이력 대비 ≥5회 반복)**:
  - push 군 `--filter FullyQualifiedName~Push|SorterCellFullness|Field20Cells`(신규 하트비트·EC-14 포함 — `ChuteRecoveryPushHeartbeatTests` 는 "Push" 매치, EC-14 는 SorterCellFullness 매치): **5회 연속 = 55/55 GREEN**(각 8~9s). 직전 스프린트 52 + 신규 3 = 55 산술 일치.
  - E2E 군 `--filter FullyQualifiedName~E2E`: **5회 연속 = 50/50 GREEN**(각 15~17s).
  - flake 0. 단일 run 신뢰 회피 규칙 준수.

## [Completion #3 / VS-1·VS-5·VS-6] 항목 1 — 슈트 복구 하트비트 — PASS

**가짜 RCS 실수신 본문 ground-truth**(detailed 콘솔에서 fresh 캡처):

- **VS-1(HB-1, `HB1_ChuteRecoveryHeartbeat_FullChutes_Redelivered_WithoutSubsequentEvents`) GREEN** —
  `[HB-1] RCS 다운 중 만재 전이 2건 — 성공 delivery 슈트1=1·슈트2=1(부트뿐·stale)` →
  `[HB-1] 복구 후 이벤트 0으로 만재 2건 재도달(각 1건) — 이후 시도 정지(총 11건에서 stable)`.
  = (a) RCS 503 중 만재 슈트 2건 전이 → 재시도 소진(실패 시도 Accepted=false 관측·성공 delivery 는 부트스트랩뿐) (b) 복구 (c) **후속 슈트 이벤트 0**으로 관찰 주기 경과만으로 최신 상태(next_state=2)가 각 슈트 정확히 1건 재도달 → 이후 전체 발신 시도 자체 정지(폭주 0). 계약 VS-1 의 "만재 2건 복구 재도달" 정확 재현.
- **VS-5(HB-2, `HB2_SyncedChute_NoHeartbeatResend_NoContradiction`) GREEN** —
  `[HB-2] 동기 슈트 재발신 0·전체 시도 stable(8건)·이력 [3,2] 무모순`.
  = 동기(Acked==Computed) 슈트는 관찰 주기(30ms) 반복에도 재발신 0(성공+실패 시도 모두 stable) + 슈트4 수신 이력 정확히 `[3,2]` — 같은 chuteNo 모순 발신 0.
- **VS-6(DORMANT)**: 기존 `PUSH8`·`CS_PUSH_6`·`CS_PUSH_6c` 가 커버(BaseUrl 미설정 → `StartAsync` 조기 return 으로 관찰 루프 자체 미기동 — 하트비트 포함 발신 0·크래시 0·인바운드 정상). 셋 다 full 및 push 군 5× 반복에 포함되어 GREEN.
- **S-IF08 계약 성질 보존(코드 직접 판독 + 회귀 실측)**: 하트비트는 `RunSorterObserveLoopAsync` 안에서 **미동기(`!Acked.HasValue || Acked != Computed`, Gate 락 하 판독) 슈트만** `Observe(st)` 호출 — 동기 슈트는 Observe 자체를 안 탐(폭주 구조적 0). 발신은 기존 `Observe→ComputeAccept(단일 술어)→PumpAsync(Gate 락 + PushInFlight 클레임 + 값기반 억제)` 단일 경로 그대로 — 별도 병렬 발신 경로/이중 소스 재도입 0, 모순 발신 구조적 불가, 전이당 1회 멱등 불변. 신규 타이밍 상수 0(기존 `SorterObserveIntervalMs`(appsettings, 운영 150ms) cadence 재사용 — 절대규칙 #7). 관찰 루프 예외 흡수+로깅 유지(예외 삼킴 0 — Fail-Loud 로그 후 다음 주기 재시도).
- 직전 스프린트(S-IF08) 아카이브의 minor "슈트는 주기 관찰이 없어 RCS 장기 다운 중 stale" 를 정확히 닫음 — 반복 이슈 아님(해소 방향).

## [Completion #4 / VS-2] 항목 2 — 멀티소터 셀 해제 격리 회귀 테스트 — PASS

- **EC-14(`EC14_ReleaseEmptyAssignment_MultiSorter_DestinationScoped_NoCrossRelease`) GREEN** —
  `[EC-14] 멀티소터 해제 격리 — A(cell1) 해제·B(cell1·동일 바코드·최신 배정) 생존(교차 해제 0).`
  소터 A(시드 30)·B(신규 31)가 **같은 CellNo=1·같은 바코드** 활성 배정 보유 → A 에 `ReleaseEmptyAssignment` → A 해제(양성 대조 — 공허 통과 아님) + **B 생존** DB 카운트 단언 + B 오더 기준 재확인.
- **회귀 민감화 실효 확인(구현 쿼리 직접 대조)**: `DbRepositories.cs` 의 실제 쿼리가 `OrderByDescending(a => a.AssignedAt)` — 테스트가 B 배정을 +1s 최신으로 만들었으므로 **dest 필터(`a.Cell.DestinationId == dest.Id`)가 빠지면 B 가 선택·해제되어 즉시 RED**. 바코드 공유로 barcode 필터 무력화. 회귀 잠금 실효.
- **`ReleaseEmptyAssignment` 본문/시그니처 무변경**: `git diff -- backend/src/Wcs.Api/Repositories/DbRepositories.cs` **0건**(계약 Completion #4 원문 그대로 충족).

## [Completion #5 / VS-3] 항목 3 — 인덱스 2종 + 양 provider 마이그레이션 — PASS

- **양 provider 존재 + 체이닝**: `AddHotPathIndexes` 가 SqlServer `20260713012244`·Sqlite `20260713012257` 로 존재, Designer `[Migration]` 애트리뷰트 정합, 타임스탬프가 `AddB2BArchivedAt`(20260708…) **뒤**. Up/Down 이 두 provider 문자 그대로 동일 델타(CreateIndex 2 / DropIndex 2 — 대칭). 스냅샷 diff 각 **+6/-0 순수 additive**(기존 엔트리 재정렬 0).
- **SQLite 실적용**: 스크래치 파일 DB에 `ef database update --connection` → 5개 체인 전부 적용. `sqlite_master` 조회:
  `CREATE INDEX "IX_piece_pid_active" ON "piece" ("PId", "IsActive")` · `CREATE INDEX "IX_order_item_barcode" ON "order_item" ("Barcode")` **실재** + 기존 `UQ_piece_pid_active_status`·`UQ_order_item_order_barcode` **보존**. 검증 후 스크래치 삭제.
- **SqlServer 실적용(스크래치, 사용자 DB 무접촉)**: localhost 일회용 `WcsMigCheck_eval7c31` 에 `ef database update --connection` → 5개 체인 전부 적용. `sys.indexes` 조회(fresh):
  `IX_piece_pid_active | piece | is_unique=0 | has_filter=0 | (PId,IsActive)` · `IX_order_item_barcode | order_item | is_unique=0 | has_filter=0 | (Barcode)` **실재** + `UQ_piece_pid_active_status`(unique·filtered)·`UQ_order_item_order_barcode`(unique) **공존·비파괴**. 검증 후 **DROP DATABASE 완료**(`sys.databases WHERE name LIKE 'WcsMigCheck%'` = 0 rows).
- **사용자 DB 무접촉 입증(읽기 전용 점검)**: `Rcs3dsInterlockingWcs.dbo.__EFMigrationHistory` 최신 = `20260708021450_AddB2BArchivedAt` — 신규 마이그레이션 **미적용 그대로**(이 스프린트가 사용자 DB를 건드리지 않았음의 직접 증거). Azure/현장/실 PLC/COM1 무접촉. 검증 포트 전부 동적 loopback(고정 :5205/:1502 미사용).
- 판독 노트: 신규 인덱스가 비필터라 기존 필터드 유니크의 "물리 컬럼명 207 함정"(SQLite 검증만으론 못 잡는 부류) 비해당 — 그래도 SQLite 만으로 판정하지 않고 실 SqlServer 적용으로 이중 확인(lessons 2026-06-30 준수).

## [Completion #6 / VS-7] 항목 4 — 라이드얼롱 2건 — PASS

- **사장 DI 제거**: `Program.cs` 에서 `AddSingleton<IDestinationChangeNotifier>(...)` 제거. 전수 grep(`backend/`): `IDestinationChangeNotifier` 잔존 = 인터페이스 정의 + `DestinationStatusPusher` 구현 선언 + 설명 주석 **3곳뿐 — DI resolve 소비처 0** 재확인(슈트 변화원은 `OnChuteStateChanged` 이벤트 직구독 경로가 실효). `AddHostedService(sp => sp.GetRequiredService<DestinationStatusPusher>())` 존치 — 부팅·push 경로(슈트 capacity 콜백·소터 관찰·운영자 전이) 전부 push 군 5× GREEN 으로 실증.
- **`_cts` 재배치**: `StartAsync` 에서 `_cts = CreateLinkedTokenSource(...)` 가 부트스트랩 발신 루프 **앞**(L154 < L158-163) — 부트스트랩 `PumpAsync` 의 `_cts?.Token ?? None` 이 이제 실 토큰 사용(취소 가능). teardown 경쟁 스위트(RcsPush teardown 포함 push 군 + full) 전부 GREEN.

## [Completion #7 / VS-4] 무접촉 — PASS

- `git status --porcelain` 전수 대조: 변경 파일 = 계약 4항목 파일 + 신규 마이그레이션 4 + 신규 테스트 1 + tasks/*.md **뿐**. `Wcs.PlcGateway`·`Wcs.Core`·`HandshakeOrchestrator`·`frontend/`·`Sim3ds`·`DbRepositories.cs` **diff 0**(grep 매치 0건). Modbus 레지스터 맵 불변.

---

## 평가 기준 판정 (Backend/API 4기준)

1. **API/컴포넌트 설계 정합성 (★★★) PASS** — 하트비트가 S-IF08 단일 발신 계약을 우회하지 않고 관찰 루프 내부에서만 확장(미동기 게이트 + 기존 Observe→PumpAsync). 인덱스는 기존 유니크 2종과 공존(양 provider 카탈로그로 실증).
2. **아키텍처 원본성 (★★★) PASS** — 별도 병렬 경로/이중 소스 0. 마이그레이션 양 provider 문자 동일 델타·순수 additive 스냅샷.
3. **Craft (★★) PASS** — 신규 타이밍 상수 0(계약 명령 그대로 `SorterObserveIntervalMs` 재사용), DORMANT/teardown 방어 보존, 스크래치 DB 절차 준수(생성→검증→DROP→사용자 DB 무접촉 입증), 예외 삼킴 0(관찰 루프 로깅 유지).
4. **Functionality (★★) PASS** — VS-1~VS-7 전부 실제 재현(가짜 수신 본문·DB 카운트·스키마 카탈로그·git diff). 회귀 0.

## Minor (비블로킹 — 차단 없음, 기록만)

- **[관찰·by-design] RCS 장기 다운 중 미동기 슈트의 지속 재시도 cadence**: 하트비트는 다운 중에도 매 관찰 주기 미동기 슈트를 재구동한다. 실제 부하는 `PushInFlight` 가 재시도 사이클(운영 1s/2s/4s 백오프 ≈7s) 동안 True 로 유지돼 슈트당 "재시도 사이클 연속 실행" 수준으로 자체 제한됨(주기 150ms 폭주 아님 — HB-1 이 복구 후 정지도 실증). 계약이 명시적으로 선택한 설계(신규 상수 금지·주기 재사용)라 결함 아님. 운영 로그 노이즈가 문제되면 후속에서 하트비트 전용 감쇠(예: N주기 스킵) 검토 가능 — todo 등재는 불요 수준.

## 판정

**APPROVED.** Completion Conditions 7/7 충족. 잔여 조치: orchestrator 커밋(Evaluator 는 커밋/푸시하지 않음 — working tree 상태 그대로 인계).

## Code Review Pass (Step 4.5 — 독립 리뷰, 2026-07-13)

**판정: Ready to merge = Yes. Critical 0 · Important 0 · Minor 2.**

강점: 하트비트가 단일 Observe→Pump 경로를 그대로 재사용(병렬 발신 경로 없음), 미동기 술어가
Acked==null(부트스트랩 실패)을 포함해 정확·완전, DORMANT 엄격 no-op, PushInFlight로 폭주 아닌
관찰주기당 1회의 유계 재시도, stale 덮어쓰기 창 없음(발신 직전 라이브 재계산), 마이그레이션 양
provider 대칭·비파괴, EC-14 민감화 실효(dest 필터 제거 시 양방향 RED), 테스트 결정성(bare sleep 0).

### Minor (비블로킹 — 다음 sprint 참고)
1. **슈트 복구 cadence가 SorterObserveIntervalMs에 결합** — 계약이 지시한 트레이드오프(신규 상수 금지).
   운영자가 이 값을 공격적으로 낮추면 RCS 장애 중 아웃바운드 재시도율도 같이 올라감 — appsettings
   주석/SPEC 한 줄 문서화 후보.
2. **IDestinationChangeNotifier가 마커 인터페이스화** — DI 제거 후 타입 참조 0(이벤트 직결). 무해 —
   후속 정리 때 인터페이스 자체 제거 후보.
3. (Generator 발견·선재) 마이그레이션 팩토리 헤더의 `--startup-project src/Wcs.Data` 안내 명령이
   실제론 실패(Wcs.Data가 마이그레이션 어셈블리 미참조) — 헤더 주석 정정 후보(감사 묶음 E 계열).
