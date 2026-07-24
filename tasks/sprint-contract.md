[Sprint Contract] — S-TWO-FLOOR-CONTROL 서브 스프린트 C1 (R clear는 Ready==1일 때만 + 처리 3시각 + sorter_command 양-provider 마이그레이션)

> 선행: A(층 판정 코어, PR #76)·B(IF-08 층별 호스트·I-2, PR #77)는 develop 병합 완료. 본 계약은 B 계약을 덮어쓴다(B는 병합됨).
> 설계 권위(SOURCE OF TRUTH): docs/SPEC.md(§4 C/R 핸드셰이크 — R clear@Ready·3시각, §4-A arming 잔류대사, §6 Sim3ds), docs/ERD.md(`sorter_command` 3시각 신규 컬럼·이력 분리 원칙·대리키), docs/wcs_3ds_interface.html(타이밍 차트 ①②③·R/C 핸드셰이크), docs/wcs_3ds_unified_sequence.html. 레지스터 맵·핸드셰이크 정의는 이 문서들이 정답 — 추측 금지.
> ⚠ SPEC §4/§4-B는 이미 "R clear는 Ready==1 시점" + "3시각" + "재시작 클리어"를 확정 기술해 둔 상태다(2026-07-21 설계). 본 스프린트는 문서에 앞서 있는 코드를 문서 확정 동작으로 끌어올리는 구현 스프린트다.

---

## 서브 분할 (범위 크기 대응 — 6항목 + 핸드셰이크 변경 + 양-provider 마이그레이션은 한 스프린트에 과대)

USER REQUEST의 6항목을 응집도 기준으로 **3개 서브 스프린트**로 나눈다. 경계 근거는 아래와 같다(코드 직독 판단 — 예시가 아님):

- **C1 (본 계약 — 상세) · "핸드셰이크 타이밍 + 계측 + 스키마"**: ① R clear@Ready==1 + ② 처리 3시각(depositedAt/tiltedAt/returnedAt) + sorter_command 양-provider 마이그레이션. 세 항목이 한 덩어리인 이유: ①의 새 타이밍(복귀 완료까지 대기)이 **returnedAt을 채우는 유일 경로**이고, tiltedAt은 R_Flag==1 관측(핸드셰이크 R단계), 3시각 영속화는 `HandshakeResult`→`EfSorterCommandJournal`→`sorter_command` 스키마까지 관통한다. 셋을 쪼개면 절반 구현이 나온다. **최고 위험군**(현장 R-clear off-by-one 교훈 + 운영 SQL Server 콜드스타트 마이그레이션 교훈이 여기 집중).
- **C2 (로드맵) · "콜드스타트 복구"**: ③ 재시작 시 WCS 기입 레지스터(C/R/TgtFloor) 클리어(D4는 RMW로 Ready 보존) + ④ [A 이연] I-3 재시작 시 in-memory 소터별 pending-floor 큐 복원. 둘 다 "기동 시 1회 복구"라는 동일 트리거·동일 수명(§4-B).
- **C3 (로드맵) · "루프 경량화·관측성"**: ⑤ [A 이연] 샘플링 스톨 fail-loud 감지기(SorterFloorReturnService) + ⑥ [B 이연] pusher 경량화(DestinationStatusPusher observe 루프 ComputeSorterFull 스킵). 둘 다 관측/주기 루프의 비용·침묵 결함 정리 — 서로 다른 파일이라 독립.

각 서브 스프린트는 이전 것 병합 후 순차 재계약한다(A/B 방식). **본 계약은 C1만 상세 정의**하고 C2·C3은 아래 "로드맵" 절에 1줄씩 둔다.

---

## ❗ 질문 (사용자 확인 필요 — 확정 전 Phase 2 착수 금지)

추측 금지(절대규칙·Planner 규범). 아래 (b)(d)(e)는 **C1 착수 전 필수 확정**, (a)(c)는 C2 착수 전 확정.

- **(b) [C1 필수] `RFlagAt` ↔ 신규 `tiltedAt` 개념 중복 처리.** ERD는 `tilted_at`을 "셀 틸트(R_Flag==1 관측; **기존 `r_flag_at`과 동일 시점**)"으로 정의한다 — 즉 같은 타임포인트다. 현재 `r_flag_at`은 **Success에서만** 기입(DbRepositories.cs:854)되고 MonitoringQueries `SorterCommandDto`가 노출한다. 선택지: (i) **`r_flag_at`을 `tilted_at`으로 개명**(단일 컬럼·중복 0 — 권장), (ii) `deposited_at`+`returned_at` 2개만 신설하고 기존 `r_flag_at`을 tiltedAt으로 재활용(개명 없이 의미만 확장), (iii) 3개 모두 신설 + `r_flag_at` 존치(명시적 중복). **Planner 권장 = (i) 또는 (ii)**(중복 컬럼 금지 — ERD 원칙 "단일 진실"). 단 tiltedAt 의미가 "Success만"에서 "R_Flag==1 관측 시 항상(불일치 포함)"으로 넓어지는 점 확정 필요. → **어느 방식? 개명 시 MonitoringDto/프론트 표기 변경 동반됨.**
- **(d) [C1 필수] R-clear를 Ready==1까지 지연할 때 (i) 불일치(RSeqMismatch)·(ii) 타임아웃 경로의 clear 시점, (iii) 복귀 대기 타임아웃 구조.**
  - (i) 현재 코드는 불일치·성공 **둘 다 R_Flag==1 즉시 ClearR**한다(HandshakeOrchestrator.cs:296·306). 새 규칙 "Ready==1에 clear"를 불일치에도 적용할지, 아니면 불일치는 알람이므로 즉시 clear 유지할지. **Planner 권장 = 성공만 Ready==1 지연(returnedAt 계측 대상), 불일치·타임아웃은 현행 즉시/미클리어 유지**(불일치는 복귀를 기다릴 정상 사이클이 아님). 확정 필요.
  - (iii) R_Flag==1 관측 후 Ready==1 복귀를 기다리는 **새 대기 구간**의 상한. 기존 `RFlagTimeoutMs`(=분류최대+여유) 단일 데드라인을 전체(투입~복귀)에 재사용할지, 복귀 이동분을 위한 **별도 설정값(예: `ReturnReadyTimeoutMs`)**을 신설할지. 복귀 이동(MoveDuration)이 추가되므로 단일 데드라인 재사용은 촉박할 수 있음. **어느 쪽이든 하드코딩 금지(절대규칙 #7·appsettings Timing)**. 복귀 대기 타임아웃 초과 시 outcome/상태(returnedAt=NULL 유지 + 알람?) 확정 필요.
- **(e) [C1 필수] 3시각의 정확한 소스 이벤트 + NULL 허용 규칙.** Planner 매핑안(확정 요청):
  - `depositedAt` = **IF-10 deposit-report 시점**(= piece.DepositedAt). sorter_command는 IF-10→셀지정 이후 생성되므로 행 생성 시 항상 non-NULL. (핸드셰이크 C-write 시각인 기존 `CWrittenAt`과 구별 — depositedAt은 투입 보고 사실 시각.)
  - `tiltedAt` = **R_Flag==1 관측 시점**. Success·Mismatch에서 non-NULL, 타임아웃/OFFLINE(R 미수신)에서 NULL.
  - `returnedAt` = **Ready 0→1 복귀 완료(= R 영역 클리어) 시점**. Success에서 non-NULL(복귀 대기 성공 시), 그 외 NULL. R_Flag==1 관측 시 이미 Ready==1이면 즉시 클리어 → returnedAt≈tiltedAt.
  - 이 매핑 확정 요청. 특히 depositedAt 소스(IF-10 report 시각 vs C-write 시각)와 tiltedAt/returnedAt의 outcome별 NULL 규칙.
- **(a) [C2] 재시작 시 TgtFloor(D6) 클리어 vs 절대규칙 #3("WCS는 TgtFloor를 절대 클리어하지 않는다").** SPEC §4-B는 "**에러로 인한 재시작 등 기동 시** WCS가 자신이 쓰는 레지스터(C/R/TgtFloor)만 클리어(Ready·CurFloor는 미접촉)"로 **콜드스타트 예외를 이미 명문화**했다. 즉 설계 권위(docs/)는 "정상 운영 중 클리어 금지(#3) vs 콜드스타트 1회 복구 리셋(§4-B)"을 구분한다. 그러나 **CLAUDE.md 절대규칙 #3 문언은 예외 없는 절대형**이다 — B에서 #2를 정정한 것과 동형의 문언 괴리. **→ 확정 요청: 콜드스타트 TgtFloor 클리어를 #3의 정당한 예외로 승인하고, CLAUDE.md #3 문언을 "정상 운영 중 금지 / 콜드스타트 1회 복구 리셋 허용"으로 오케스트레이터가 정정(B의 #2 정정 선례 — 보호파일이라 에이전트 아닌 오케스트레이터가 별도 커밋)할지.** (C1엔 영향 없음 — C1은 R 영역만 조기 clear 지연, TgtFloor 미접촉.)
- **(c) [C2] I-3 큐 복원 방식.** 재시작 시 유실되는 in-memory pending-floor 큐를 (i) 큐 자체 DB 영속화 vs (ii) 재시작 시 미완료 sorter_command/piece(수용 확정·미분류 상태)에서 **재파생**. **Planner 권장 = (ii) 재파생**(큐는 파생 상태이므로 별도 영속화는 이중 진실 — ERD "단일 진실=piece" 원칙 정합). C2에서 확정.

---

## Goal

C/R 핸드셰이크의 **R 영역 클리어 시점을 "R_Flag==1 즉시"에서 "Ready==1(복귀 완료) 시점"으로 지연**하고, 그 과정에서 관측되는 **처리 3시각(depositedAt=투입 / tiltedAt=틸트 / returnedAt=복귀 완료)을 sorter_command에 기록**해 "투입→틸트→복귀" 구간 소요를 계측할 수 있게 한다. 이를 위해 `sorter_command`에 3시각 컬럼을 신설하고 **양 provider(SqlServer 운영 / Sqlite 개발·테스트) 마이그레이션을 분리 프로젝트(Wcs.Migrations.SqlServer / Wcs.Migrations.Sqlite)에 각각 추가**한다.

목적(현장 근거): 현재 성공/불일치 모두 R_Flag==1 즉시 ClearR하는데, 이는 "분류 시작~복귀 완료" 전체 소요를 계측할 수 없고(returnedAt 부재), 2층 복귀 이동이 있는 사이클에서 clear 시점이 복귀 전이라 계측·감사 정합이 어긋난다. R clear를 복귀 완료(Ready==1)에 맞추면 전체 소요를 정확히 계측하고 SPEC §4 확정 동작에 코드를 일치시킨다.

핵심 불변(위반 금지):
- **절대규칙 #1**: 모든 PLC 쓰기(ClearR 포함)는 소터별 단일 쓰기 큐 경유. HandshakeOrchestrator는 직접 Modbus 호출 금지 — 기존 `_gw.EnqueueAsync(new PlcWrite.ClearR())` 경로 유지.
- **절대규칙 #3**: WCS는 TgtFloor를 클리어하지 않는다 — C1은 R 영역(D2/D3/R_Flag)만 다룬다. TgtFloor 미접촉.
- **절대규칙 #4**: Ready(D4.2) 의미 = 1(수용가능·비분류·정지) / 0(분류 중 **또는 이동 중**). "복귀 완료"의 판정 기준은 Ready 0→1 상승(이동 중엔 Ready=0 유지되므로 복귀 이동 완료 후에만 1).
- **절대규칙 #7**: 복귀 대기 타임아웃·폴 주기 등 모든 타이밍은 appsettings(Timing 섹션) — 하드코딩 금지. "분류 최대 소요+여유"의 R_Flag 타임아웃도 고정값 금지.
- **절대규칙 #8**: 판정은 Wcs.Core 순수 함수 유지 — 핸드셰이크 타이밍은 I/O 계층(Wcs.PlcGateway/Wcs.Api)에만.
- **Modbus 레지스터 맵 불변.** ClearR가 쓰는 레지스터(D2/D3=0, D4 RMW로 R_Flag clear·다른 비트 보존)는 기존 `ProcessWriteAsync` ClearR 케이스 그대로.
- **arming/잔류 대사(§4-A) 불변**: 시작 시 R_Flag==1 잔류 reconcile(ClearR 선행) 로직·기동 첫 폴 reconcile은 무변경. C1은 **핸드셰이크 후반(R 수신 후 clear 시점)**만 바꾼다.
- **Sim3ds 프로덕션 충실도 무변경**: 현장 실 PLC는 ClearR을 ack로 R 영역 유지(자체 클리어 안 함)·복귀 이동 남으면 Ready=0 유지 후 도착 시 Ready=1(§6). Sim이 이미 이 동작이면 Sim 코드 변경 0. Sim에 창(window)을 만드는 마스킹 금지(S-S5-FLAKE 교훈).

---

## Implementation Scope (Generator가 구현할 것 — HOW는 Generator 재량)

1. **핸드셰이크 R-clear 지연 (HandshakeOrchestrator.cs · Wcs.PlcGateway)**
   - `WaitRFlagAndProcessAsync`에서 R_Flag==1 수신·R 읽기·R_Seq 대사(현행 유지) 후, **성공 경로**의 ClearR을 "R_Flag==1 즉시"에서 **"Ready==1 관측 후"**로 지연.
   - 새 대기 구간: R_Flag==1 & 대사 성공 후 → Ready==1을 폴링 대기(`RFlagPollMs` 주기) → Ready==1 관측 시 ClearR 큐 투입 + returnedAt 기록. **R_Flag==1 관측 시 이미 Ready==1이면 즉시 clear**(추가 지연 0 — SPEC §4).
   - 복귀 대기 상한 = appsettings 설정값(질문 (d)-iii 확정 후 결정 — 기존 `RFlagTimeoutMs` 재사용 또는 신규 키). 초과 시 종결 outcome/상태 (d)-iii 확정대로. 대기 중 OFFLINE 감지 시 기존 패턴대로 명확 종결.
   - 불일치·타임아웃·OFFLINE·CFlagTimeout 경로의 clear 시점은 (d)-i 확정대로(권장: 현행 유지).
   - `EmitStage` 관측 훅: 새 단계(예: 복귀 대기·returnedAt 확정)를 operation_log(HANDSHAKE)에 흘리도록 스테이지 추가(관측 훅 예외 격리 유지). 기존 스테이지 의미 불변.

2. **HandshakeResult 확장 (Wcs.PlcGateway)**
   - `HandshakeResult` record에 계측 시각 필드 추가(예: `TiltedAt`, `ReturnedAt` — nullable). depositedAt은 IF-10에서 유입되므로 result에 안 담아도 됨(질문 (e) 확정대로). 기존 필드(Outcome/SentCSeq/ReceivedRSeq/ReceivedRCellNo/Detail)·`IsSuccess` 불변.
   - 이 시각들은 핸드셰이크가 관측한 순간(tilted=R_Flag==1 관측, returned=Ready==1 확정)을 담는다.

3. **sorter_command 3시각 영속화 (EfSorterCommandJournal · Wcs.Api/Repositories + RcsController IF-10)**
   - `CreateSent`/`Finalize`가 `depositedAt`(IF-10 report 시각 — RcsController에서 유입)·`tiltedAt`·`returnedAt`(HandshakeResult에서)을 sorter_command에 기입. outcome별 NULL 규칙은 (e) 확정대로.
   - RcsController IF-10 핸들러(deposit-report ContinueWith 블록, RcsController.cs:308~)에서 depositedAt(투입 보고 시각)을 journal에 전달. 기존 CreateSent→Finalize 순서·teardown 방어(ApplicationStopping 스킵·SafeLog)·alarm 경로 무변경.
   - `RFlagAt` 중복 처리는 (b) 확정대로(개명/재활용/존치).

4. **sorter_command 엔티티 + 마이그레이션 (Wcs.Data + Wcs.Migrations.SqlServer + Wcs.Migrations.Sqlite)**
   - `SorterCommand` 엔티티(Entities.cs)에 3시각 컬럼(nullable DateTime) 추가. WcsDbContext 매핑(컬럼명 snake_case·nullable — ERD `deposited_at`/`tilted_at`/`returned_at` NULL).
   - **양 provider 마이그레이션 각각 추가**: `dotnet ef migrations add`를 SqlServer·Sqlite 두 마이그레이션 어셈블리에 각각(`--project`·`--startup-project` 둘 다 해당 마이그레이션 어셈블리 — 교훈). 각자 독립 ModelSnapshot 갱신. 기존 컬럼·인덱스·FK 거동(Restrict 10 + nullable 7 = NO_ACTION) 무변경 — **1785/207 재발 금지**.
   - (b)에서 `r_flag_at` 개명 선택 시 RenameColumn 마이그레이션 포함(데이터 보존).

5. **모니터링 노출 (MonitoringQueries/MonitoringDtos · Wcs.Api/Monitoring)**
   - `sorter-commands` 엔드포인트(GET /api/monitor/sorter-commands)의 `SorterCommandDto`가 3시각을 노출(계측 가시화). (b) 개명 시 DTO 필드명 동반 변경.
   - 프론트 표기는 **본 스프린트 스코프 아님**(frontend diff 0). DTO 필드 추가로 인한 기존 소비 화면 파손이 없도록 append-only(기존 필드 제거 금지, 개명 시만 대응).

**스코프 밖(건드리지 말 것)**: TgtFloor 클리어/재시작 클리어(C2)·I-3 큐 복원(C2)·스톨 감지기(C3)·pusher 경량화(C3)·arming 로직(§4-A)·Sim3ds 프로덕션 동작·프론트엔드·PLC 쓰기 큐 컨슈머(ProcessWriteAsync)의 ClearR 케이스 내부 로직·IF-05 dispatch 게이트.

---

## Evaluation Criteria (Backend/API 기준 — Evaluator 판정 가중)

1. **Functionality — R-clear 타이밍 정확성 (★★★)**: R 영역이 **Ready==1(복귀 완료) 시점에만** 클리어되는가(성공 경로). 복귀 이동이 있는 사이클(Ready 0 구간 존재)에서 clear가 복귀 전에 새지 않는가. R_Flag==1 관측 시 이미 Ready==1이면 즉시 clear(추가 지연 0)인가. 실 Sim3ds 핸드셰이크 왕복 + 레지스터/타임라인으로 실증. arming(§4-A)·불일치·타임아웃 경로 회귀 0.
2. **Data integrity — 3시각 + 마이그레이션 (★★★)**: sorter_command에 depositedAt≤tiltedAt≤returnedAt 순서로 3시각이 기록되는가(성공). outcome별 NULL 규칙((e))이 정확한가. **양 provider 마이그레이션이 실제 적용되는가 — 특히 빈 SQL Server 인스턴스에 `dotnet ef database update`가 fresh로 성공(1785/207 없음)**. SQLite GREEN만으로 PASS 금지(교훈 sqlserver-migration-prod-provider). 기존 컬럼/FK/인덱스 거동 불변.
3. **Craft (★★)**: 절대규칙 #1(ClearR 큐 경유)·#7(복귀 대기 타임아웃 appsettings·하드코딩 0)·#8(Core 순수) 준수. 예외 삼킴 0(복귀 대기 중 OFFLINE 명확 종결). 관측 훅 예외 격리 유지. teardown 채널 경쟁 방어(교훈 testhost-teardown — 새 대기 루프가 취소·채널 완료에 결정적 종료).
4. **Architecture originality / API design (★★)**: HandshakeResult 확장이 append-only(기존 소비 파손 0). 3시각 영속화가 기존 CreateSent/Finalize 트랜잭션 경계·teardown 방어를 존중. RFlagAt 중복 정리((b))가 이중 진실을 남기지 않음.

---

## Completion Conditions (Evaluator PASS 최소 조건 — 모두 충족해야 APPROVED)

- [ ] **전체 테스트 GREEN**: `dotnet test backend/Wcs.sln` 전량 통과(현행 408 기준 + 신규 테스트). Evaluator 독립 재실행. 실-Sim/핸드셰이크 타이밍군은 단일 run 불신 — **`--filter`로 신규+핸드셰이크군 ≥5회 반복 GREEN(flake 0)**(교훈 s9-flake·e2e-parallel-load). baseline 대조는 `총 - 신규 = baseline` 산술.
- [ ] **R-clear@Ready 실증(E2E, 실 Sim3ds)**: (a) 복귀 이동 사이클 — R_Flag==1 후 Ready==1이 될 때까지 R 영역(R_Flag/D2/D3)이 **유지**되고(clear 미발생), Ready==1 관측 직후에만 ClearR 발생을 레지스터 타임라인(operation_log REG_CHANGE / Sim 상태)으로 확인. (b) 무-이동 사이클(도착 시 즉시 Ready=1) — clear가 즉시 발생·추가 지연 0.
- [ ] **3시각 DB 실증**: 성공 핸드셰이크 후 sorter_command 행에 depositedAt·tiltedAt·returnedAt이 채워지고 `depositedAt ≤ tiltedAt ≤ returnedAt`(단조), 복귀 이동 사이클에서 `returnedAt > tiltedAt`(측정 가능한 간격). 불일치/타임아웃 경로의 NULL 규칙((e)) 정확. DB 직접 조회(SQLite scratch)로 확인.
- [ ] **양 provider 마이그레이션 실적용**: (a) SQLite scratch DB에 `ef database update` 적용 + `sqlite_master`로 3컬럼 실재 확인. (b) **빈 SQL Server 인스턴스(localhost 일회용 `WcsMigCheck_*`)에 `ef database update`를 fresh로 적용 성공 + `sys.columns`/`sys.foreign_keys`/`sys.indexes`로 3컬럼·FK 거동(NO_ACTION 불변)·인덱스 확인 후 DROP**(교훈). **사용자 운영 DB(Rcs3dsInterlockingWcs)는 무접촉** — `__EFMigrationHistory` 최신이 여전히 직전(AddPieceArchivedAt)임을 읽기전용 확인으로 증거화. `has-pending No changes` 양 provider 확인.
- [ ] **정적 검사**: `dotnet build` 경고 0(신규) — 선재 NU1903만 허용. 무접촉존 diff 0: Wcs.Core·SorterFloorReturnService·DestinationStatusPusher·frontend·ProcessWriteAsync ClearR 케이스 내부·arming(ArmRFlagZeroAsync)·PLC 쓰기 큐 컨슈머 로직.
- [ ] **하드코딩 스캔(#7)**: 복귀 대기 타임아웃·주기가 appsettings에서 읽히는가 — 코드 리터럴 grep 0(고정 ms 상수 금지).
- [ ] **격리 안전(현장 DB 오염 0)**: 검증 스택은 Sim3ds Tcp(NOT COM1/RTU) + Wcs.Api `--urls` 명시 + scratch SQLite(Provider=Sqlite) — 기동 로그 문자열로 실증(교훈 field-3ds-plc·2026-07-03 시드 사고). 실 SQL Server 검증은 일회용 DB에서만.

---

## Parallel Modules (Generator fan-out)

N/A (단일 응집 모듈). C1의 세 항목(핸드셰이크 타이밍·3시각 영속화·마이그레이션)은 `HandshakeResult`→저널→스키마로 관통 의존해 파일 경계로 병렬 분할이 불가능하다(마이그레이션은 엔티티에, 저널은 result 타입에 의존). 단일 Generator가 순차 구현.

## Evaluation Dimensions (Evaluator expert pool)

functional only (기본 1 Evaluator). 단 마이그레이션 무결성(실 SQL Server 콜드스타트)은 별도 Evaluator로 병렬화하지 않고 **Completion Conditions의 명시 게이트로 강제**한다(핸드셰이크와 마이그레이션이 독립 병렬 검증할 만큼 분리되지 않고, 소규모 스프린트라 pool 오버헤드가 이득을 초과). 한 Evaluator가 functional + data-integrity(마이그레이션 포함)를 순차 검증.

---

## Detected Project Type: Full-stack

프로젝트 신호: 리포에 브라우저 진입점(frontend/ — React/Vite)과 서버 라우트/컨트롤러(backend/src/Wcs.Api/Controllers/*)가 **동시** 존재 → Full-stack. 단 **본 C1 스프린트는 백엔드 전용**(frontend diff 0) — 아래 Full-stack 슬롯에서 Web/UI 프런트 표면은 N/A(사유 명기)로 채우고 Backend/API·E2E 크로스레이어를 전수 채운다.

## Verification Scenarios (Full-stack — mandatory)

### Applicable Web/UI scenarios (frontend surface this sprint touches)
- **N/A — frontend diff 0.** C1은 백엔드(핸드셰이크·저널·마이그레이션·모니터링 DTO)만 변경한다. MonitoringDto에 3시각 필드가 추가되나 프론트 소비 화면 결선은 본 스프린트 스코프 아님(append-only라 기존 화면 무파손). Evaluator는 `git diff --stat develop -- frontend/`가 빈 출력임을 무회귀 증거로 확인(Web/UI 브라우저 검증 면제 근거).

### Applicable Backend/API scenarios (backend surface this sprint touches)
- **엔드포인트(method+path) 목록**:
  - `POST /api/v1/deposit-report` (IF-10) — 3D 목적지 투입 보고 → 셀 지정 → C/R 핸드셰이크 트리거. C1이 바꾸는 R-clear 타이밍·3시각의 상류 진입점.
  - `GET /api/monitor/sorter-commands` — sorter_command(3시각 포함) 관측 노출.
- **Happy path (입력→출력 shape)**:
  - IF-10: `{pId,barcode,chuteNo,agvNo}` → `{result:"OK"}`(멱등) → 백그라운드 핸드셰이크 성공 → sorter_command 1행에 depositedAt/tiltedAt/returnedAt 단조 기입 + Status=COMPLETED + piece.status=LOADED.
  - GET sorter-commands → 최근 명령 목록에 3시각 필드 포함(비-NULL, 단조), (b) 개명 시 새 필드명.
- **에러/경계 케이스 (택 — 패딩 금지)**:
  - **복귀 대기 타임아웃**(실 Sim에 복귀 이동을 무한 지연/Ready 미복귀 주입) → 설정 상한 초과 시 (d)-iii 확정 outcome/상태 + returnedAt NULL + 알람. R 영역 처리 정합.
  - **R_Seq 불일치**(Sim R_Seq 오프셋 주입) → clear 시점 (d)-i 확정대로 + tiltedAt 기입/returnedAt NULL + Status=MISMATCH.
  - **OFFLINE 중 복귀 대기**(폴 실패 주입) → 명확 종결(더티 진행 0)·returnedAt NULL.
  - IF-10 재보고(멱등) → 이중 기록 0.

### End-to-end data-flow scenario (2+ 레이어 관통)
- **투입→틸트→복귀 전체 소요 계측 왕복(핵심 시나리오)**: 실 Sim3ds(Tcp) 소터에 2층 복귀가 필요한 시나리오를 구성 → IF-10 투입 → 핸드셰이크가 C 기입 → Sim이 R_Flag=1 세팅(tiltedAt 관측) → **복귀 이동(Ready=0 구간) 동안 WCS가 R 영역을 유지**(clear 미발생) → Sim이 목적지 층 도착·Ready=1(returnedAt 관측) → WCS가 ClearR 큐 투입 → 레지스터가 R_Flag=0·D2/D3=0으로 전이. 그 후 DB(sorter_command)에서 `depositedAt(IF-10) ≤ tiltedAt(R_Flag=1) < returnedAt(Ready=1)`을 조회로 확정하고, operation_log(HANDSHAKE/REG_CHANGE) 타임라인이 "clear가 Ready=1 이후"임을 시계열로 뒷받침. → API(IF-10) · PLC게이트웨이(핸드셰이크·레지스터) · DB(sorter_command 3시각) 세 레이어를 관통해 "R-clear@Ready + 3시각 계측"을 단일 왕복으로 실증.
- **마이그레이션 크로스-provider 데이터 흐름**: 동일 3시각 스키마가 SQLite(scratch) 왕복과 SQL Server(일회용) 콜드스타트 양쪽에서 성립 — 엔티티→양 ModelSnapshot→양 DDL이 일관(직렬화/컬럼 정합). 사용자 운영 DB 무접촉.

---

## 로드맵 (C2·C3 — 병합 후 순차 재계약, 본 계약 스코프 아님)

- **C2 — 콜드스타트 복구**: ③ 재시작 시 WCS 기입 레지스터(C_CellNo/C_Seq/C_Flag/R_CellNo/R_Seq/R_Flag/TgtFloor) 기동 클리어(D4는 RMW로 Ready 비트 보존·CurFloor 미접촉, IF-08 부트스트랩 push보다 먼저 — SPEC §4-B) + ④ [A 이연] I-3 재시작 시 in-memory 소터별 pending-floor 큐 복원(권장 (c)-ii 재파생). 선결: 질문 (a)(CLAUDE.md #3 문언 정정)·(c) 확정.
- **C3 — 루프 경량화·관측성**: ⑤ [A 이연] 샘플링 스톨 fail-loud 감지기 — (a) `ObserveIntervalMs ≪ 최소 분류시간` 불변식 문서화, (b) head 불변 && 유휴 && TgtFloor==0 N틱 지속 시 WARN(현재 silent) — SorterFloorReturnService. ⑥ [B 이연] pusher 경량화 — DestinationStatusPusher observe 루프가 매 틱 `_status.Compute`(→ ComputeSorterFull 셀/배정/명령/piece 다중 집계) 호출하는 것을, I-2처럼 경량 readiness(snapshot 기반 Ready + `IsPaused`, ComputeSorterFull 스킵)로 대체(accept = Ready && !Paused에 Full 미사용이므로 무기능 변경·비용만 절감).

---

> Planner self-check — Detected project type: Full-stack. Required scenario slots: 3 (Applicable Web/UI scenarios [N/A·frontend diff 0], Applicable Backend/API scenarios [endpoints + happy path + error/boundary cases], End-to-end data-flow scenario [투입→틸트→복귀 계측 왕복 + 마이그레이션 크로스-provider]). All slots filled: yes.

---

## ✅ 확정 결정 (사용자 게이트 — 2026-07-24, C1)

C1 착수 질문 4건 확정(전부 Planner 권장):
- **(b) RFlagAt 중복** → **`r_flag_at` → `tilted_at` 개명**. deposited_at/returned_at 2개 신설 + r_flag_at 개명(RenameColumn 마이그레이션, 데이터 보존). MonitoringDto/필드명 동반 변경(프론트 결선은 스코프 밖·append-only). tiltedAt 의미는 "R_Flag==1 관측 시 항상(성공·불일치 포함)"으로 확장.
- **(d-i) clear 범위** → **성공 경로만 Ready==1까지 지연**. 불일치(MISMATCH)·타임아웃은 현행(즉시/미클리어) 유지.
- **(d-iii) 복귀 대기 타임아웃** → **복귀용 별도 설정키 신설**(예 `Timing:ReturnReadyTimeoutMs`, appsettings·하드코딩 0). 기존 RFlagTimeoutMs와 분리. 초과 시 outcome: returnedAt=NULL 유지 + 알람(명확 종결).
- **(e) 3시각 매핑 승인** → depositedAt=IF-10 투입보고 시각(항상 non-NULL) / tiltedAt=R_Flag==1 관측(성공·불일치 non-NULL, 타임아웃·OFFLINE NULL) / returnedAt=Ready 0→1 복귀완료(성공만 non-NULL). depositedAt≤tiltedAt≤returnedAt 단조.

(질문 (a) CLAUDE.md #3 문언·(c) I-3 재파생은 C2 게이트에서 확정 — C1 무관.)
