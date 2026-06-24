# Sprint Feedback — S-M4-P4 (소터 셀 만재 판정 m4p4) — APPROVED

## Phase 3 Evaluate 결과 (Evaluator fresh evidence, 미커밋 working tree `feat/sorter-cell-fullness`, 2026-06-24)

**최종 판정: APPROVED** — 계약 Verification Scenarios(HP-1~3·EC-1~6)·Completion Conditions·Evaluation Criteria(40/25/15/10/10)·사용자 확정 4건(Q1~Q4) 전부 fresh 직접 실행 증거로 충족. 보호 zone diff 0·DB 스키마 무변경·teardown 회귀 0. 동시성 원자성은 SQL 단일 쿼리 구조 + EC-5 내부 불변식 + quiesce 등가성으로 입증.

### Fresh evidence (직접 실행 — generator 주장 신뢰 아님)
- **build**: `dotnet build Wcs.sln` → **경고 0개 / 오류 0개, BUILD_EXIT=0**.
- **full test**: `dotnet test Wcs.sln --blame-hang-timeout 120s` → **통과! 실패:0 통과:83 건너뜀:0 전체:83, TEST_EXIT=0**. Blame 수집기 "시퀀스 파일이 생성되지 않습니다"(teardown 전용 행 0). hangdump 생성 0건. **기존 76 회귀 0 + 신규 7 = 83.** (76 = Phase 2 baseline.)
- **flaky 0(타이밍/동시성 표적 ≥5회)**: `SorterCellFullnessTests | RcsPushTests` 필터 **5/5회 연속 13/13 GREEN·exit 0**. 비결정성 0.
  ```
  RUN 1: exit=0 통과:13 실패:0 (5s)
  RUN 2: exit=0 통과:13 실패:0 (5s)
  RUN 3: exit=0 통과:13 실패:0 (5s)
  RUN 4: exit=0 통과:13 실패:0 (5s)
  RUN 5: exit=0 통과:13 실패:0 (5s)
  ```

### 사용자 확정 4건(Q1~Q4) — 코드 직접 검증
- **[PASS] 확정1 (Q1 — IF-05 piece-aware 오더 재사용 예외)**: `RcsController.cs:65-74` availability 콜백 — `r.Paused`면 무조건 차단(우선), `r.Full && SORTER_3D && SorterHasActiveAssignmentForBarcode(id, barcode)`면 `DestinationBlock.None`(OK), 아니면 `Full`(NG). 슈트는 예외 미적용. `SorterHasActiveAssignmentForBarcode`(`DestinationStatusService.cs:107-110`)는 `EfCellSelector.SelectCell` ①분기(`DbRepositories.cs:578-585`)와 **predicate 완전 동형**(`ReleasedAt==null && Cell.DestinationId==id && Order.Items.Any(Barcode==bc)`), `.Any()` 읽기 전용 — 배정 부수효과 0. IF-05 OK→IF-10 SelectCell ①분기 재사용 일관(같은 셀 누적). 행동 입증: HP-2(OK·reason=NORMAL), EC-1(NG·reason=FULL).
- **[PASS] 확정2 (Q2 — 기존 소터 관찰 타이머 재사용, 별도 변화원 0)**: `git diff develop -- src/Wcs.Api/DestinationStatusPusher.cs` = **0줄**. `git diff develop -- src/Wcs.Api/ChuteCapacityService.cs` = **0줄**. `grep NotifyCellChanged|CellChanged|OnCellChanged src/ tests/` = **0건**(새 cell-change 변화원 없음). 기존 `RunSorterObserveLoopAsync`(`DestinationStatusPusher.cs:198`)가 매 주기 `_status.Compute`(line 233) 호출 → DB-aware ComputeSorter가 cell_assignment 변화를 자동 포착. 행동 입증: EC-3/HP-3(셀 점유/해제 전이가 타이머에 포착돼 푸시).
- **[PASS] 확정3 (Q3 — IServiceScopeFactory 주입, captive 회피)**: `DestinationStatusService` 생성자(`:76-86`)가 `IServiceScopeFactory`(싱글톤) 주입. `ComputeSorter`(`:156`)·`SorterHasActiveAssignmentForBarcode`(`:102`)가 `_scopeFactory.CreateScope()`로 scoped `WcsDbContext` 취득. `Program.cs` DI 등록은 `AddSingleton<IDestinationStatusService, DestinationStatusService>()` 불변(주석만 변경) — `IServiceScopeFactory` 자동 해석. 싱글톤이 scoped DbContext를 직접 주입하지 않음(안티패턴 회피).
- **[PASS] 확정4 (Q4 — 마이그레이션 0·DB 스키마 무변경)**: `git diff develop -- src/Wcs.Migrations.Sqlite src/Wcs.Migrations.SqlServer src/Wcs.Data` = **0줄**. 신규/untracked 마이그레이션 파일 0. cell/cell_assignment/`(cell_id) WHERE released_at IS NULL` 부분유니크 전부 기존. 읽기 전용 조회만 추가.

### Verification Scenarios — 항목별 PASS/FAIL (실 DB·가짜 RCS ground-truth)
- **[PASS] HP-1 (IF-05/Compute 빈셀 있음 + 정렬→ready)**: `HP1_EC6_...` — 빈셀 3개 시드(`FreeCellCount==3` DB 단언) → 미정렬 Compute `Full=false·Ready=false`(decision.Reason) → 정렬(CurFloor=2·Ready=1) Compute `Full=false·Ready=true`. 가짜 RCS·실 cell DB 기준.
- **[PASS] HP-2 (IF-05 오더 재사용 예외)**: `HP2_...` — ORD-003 3셀 전부 점유(`FreeCellCount==0`) + `SorterHasActiveAssignmentForBarcode(sorterId,"TEST-BARCODE-3")==true` → IF-05 실 HTTP `{result:"OK", chuteNo:30}`, piece_event reason=NORMAL·PieceStatus.RESERVED(DB 단언). 목적지 Compute는 여전히 `Full=true`(새 오더 수용 불가) — IF-05만 예외.
- **[PASS] HP-3 (푸시 full→!full 재푸시)**: `EC3_HP3_...` — 셀 1개 해제(`FreeCellCount==1` DB) → 관찰 타이머가 !full 전이 감지 → 가짜 RCS `LastFor(chute).Ready==true` + `CountFor` 전이당 정확히 1건(WaitUntilExact stableCount:6 폭주 0).
- **[PASS] EC-1 (소터 FULL→NG)**: `EC1_...` — 3셀 전부 점유 + 새 오더(ORD-SORTER-OTHER, barcode "SORTER-OTHER-BC", 활성 assignment 없음=재사용 불가) → Compute `Full=true·Ready=false·Reason=Full` + IF-05 실 HTTP `{result:"NG", chuteNo:null}`(200, 도메인 거부) + piece_event reason=FULL(DB 단언).
- **[PASS] EC-2 (소터 PAUSED/비활성→NG)**: `EC2_...` — 케이스A Status=PAUSED → Compute `Paused=true·Reason=Paused` + IF-05 실 HTTP NG·chuteNo=null. 케이스B IsActive=false → Compute `Paused=true·Reason=Paused`(직접 호출 단언). 비활성은 IF-05 와이어상 NO_DEST 경로(`DbRepositories.cs:120`가 availability 이전 차단 — 코드 확인)로 NG, ComputeSorter 비활성→paused 매핑 산출원 정확성은 Compute 직접 단언. 테스트 주석이 이 경로를 정확히 명시(허위 주장 없음).
- **[PASS] EC-3 (푸시 !full→full ready=false)**: `EC3_HP3_...` — 정렬 ready=true 안정 후 3셀 전부 점유(`FreeCellCount==0`) → 관찰 타이머 full 전이 → 가짜 RCS `Ready==false` + 전이당 정확히 1건(중복 0·무변화 폴 폭주 0, stableCount:6).
- **[PASS] EC-4 (paused 단독 전이)**: `EC4_...` — NORMAL ready=true → Status=PAUSED(셀 무변) → 가짜 RCS `Ready==false` 1건. full과 독립적 paused 단독 전이 입증.
- **[PASS] EC-5 (동시성 원자성)**: `EC5_...` — 6스레드 동시 배정/해제(각 40회 토글) + 관찰자가 Compute 반복 호출. **내부 불변식**(`full ⟹ !ready`, `ready ⟹ !full && !paused && online`) 위반 **0건**(observations>0). quiesce 후 full⟺빈셀0 등가성 확정(전부점유→full=true·ready=false / 전부해제→full=false). **원자성 구조 근거**: `ComputeSorter` full 쿼리(`:174-178`)는 `db.Cells.Any(c=> enabled && !db.CellAssignments.Any(active))` — EF가 단일 SQL(상관 서브쿼리)로 번역, 중간 materialize·check-then-act 분리 없음. `ready = !full && !paused && decision.Ready`는 두 read 완료 후 합성 → `full && ready` 구조적 불가. paused/full 두 쿼리는 동일 스코프 별도 round-trip이나, 불변식이 합성 후 자기일관 → torn-read 위험 없음.

### 무변경 가드 (git diff develop — 직접 실행)
- **보호 zone 0줄**: `git diff develop -- src/Wcs.PlcGateway src/Wcs.Sim3ds src/Wcs.Core/DepositDecider.cs src/Wcs.Data src/Wcs.Migrations.Sqlite src/Wcs.Migrations.SqlServer | wc -l` = **0**. `src/Wcs.Core` 전체 디렉터리 diff = **0줄**(DepositDecider 순수성·레지스터맵 불변).
- **RcsController 인바운드 무변경**: diff는 IF-05 `availability` 람다 + 주석 3줄에만 국한. IF-09(arrival)·IF-10(deposit) 핸들러 본문 0줄 변경 — 인바운드 회귀 0.
- **DenyReason 우선순위 변경(Full→Paused 우선)**: 와이어 결과 동일(둘 다 block!=None→NG). 소터가 full&&paused 동시일 때 내부 reason 라벨만 PAUSED(우선) — 계약 명시 우선순위(Offline>Paused>Full)와 일치, 와이어 회귀 0.

### Evaluation Criteria 충족 (가중치)
- **(40%) full/paused 산출 정확성**: 빈셀0→Full·ready=false / 빈셀≥1→!Full / PAUSED·비활성→Paused·ready=false. 단일 원자 쿼리. 오더 재사용 예외(Q1) 정확. HP-1/2·EC-1/2/5로 입증. **충족**.
- **(25%) 두 소비자 결선**: IF-05 소터 full/paused→NG(chuteNo=null), piece-aware 예외 정확. 푸시 ready 전이 반영(false/true 재푸시) 전이당 1회. EC-1/2/3/4·HP-2/3로 입증. **충족**.
- **(15%) 회귀 0**: 기존 76 전부 GREEN. 보호 zone diff 0. **충족**.
- **(10%) DI·경계 정합**: IServiceScopeFactory 경유(captive 0). DepositDecider 순수·Wcs.Core 의존성 0 불변. **충족**.
- **(10%) 동시성·예외 격리**: 빈셀 평가 SQL 원자. Observe/RunSorterObserveLoop 이중 try-catch(`:203-220`,`:231-241`)로 Compute 예외가 관찰 루프를 죽이지 않음. teardown exit 0. **충족**.

### Completion Conditions
- [x] dotnet build 경고 0, dotnet test 전체 GREEN(신규 VS 포함) — 83/83 exit 0.
- [x] VS 전부 자동화 테스트 통과 — 가짜 RCS 수신 본문·실 cell_assignment DB 상태 ground-truth(인메모리 카운터 단독 PASS 아님).
- [x] 무변경 영역 diff 0(Modbus·Sim3ds·DepositDecider·핸드셰이크·스키마).
- [x] Evaluator 독립 재실행으로 generator 주장 검증(fresh evidence).

### 메타 관찰(비차단)
- **EC-5 프로브 강도**: 내부 불변식 + quiesce 등가성만 검사(별도 free-count 재조회 비교는 읽기시점차 위양성이라 배제 — 정당). 진성 동시 클레임 경합(barrier 동시관찰)은 아님. **단 이번 경합 표면은 P2(아웃바운드 클레임)와 달리 "단일 SQL 원자 read"이므로 합성 모순 자체가 구조적 불가** — barrier 프로브로 추가 입증할 app-level claim 상태가 없음(공유 가변 상태는 DB 1테이블, 부분유니크가 일관성 보장). 따라서 P2 barrier 프로브 교훈은 이 스프린트엔 비적용(검토 후 판단). full⟹!ready 불변식이 SQL 원자성을 정확히 반영.
- **신규 테스트 파일 untracked**: `tests/Wcs.Tests/SorterCellFullnessTests.cs`는 아직 `git add` 전(working tree에 존재·컴파일·실행됨). 커밋은 team-lead — staging 포함 확인 권고.

### 결론
계약 전 항목 충족. 4-Tier 독립 코드리뷰(orchestrator Step 4.5)를 거쳐 commit 진행 권고. 커밋·push는 team-lead.
