[Sprint Contract] — S-TWO-FLOOR-CONTROL Sub-Sprint C3 (루프 경량화·관측성)

## 배경 / 위치
S-TWO-FLOOR-CONTROL 로드맵의 C-계열 마지막 경량 서브-스프린트. A(#76)·B(#77)·C1(#78)·C2(#79)는 develop 병합 완료.
C3는 **두 건의 이연 항목**을 처리한다 — 둘 다 관측 루프 계층(트리거)에 국한된 경량 작업이며 판정/재파생(순수 코어)·
PLC 쓰기 경로·Modbus 맵·DB 스키마를 건드리지 않는다. **규칙 개정 없음·마이그레이션 0** → 사용자 게이트 불요
(단 아래 §질문의 기본값 4건은 계약 확인 시 한 줄 승인 권장 — 블로킹 아님).

- 항목 1 [A 이연]: 샘플링 스톨 fail-loud 감지기 (`SorterFloorReturnService`).
- 항목 2 [B 이연]: pusher 경량화 (`DestinationStatusPusher` 관측 루프의 매-틱 `ComputeSorterFull` 제거).

---

## Goal
1. **관측 루프의 under-pop 스톨을 조용히 방치하지 않는다(fail-loud).** 현재 `SorterFloorReturnService`의 분류
   사이클 감지는 관측 주기(`ObserveIntervalMs`) 샘플링 기반이라, Ready=0 창이 주기보다 짧으면 사이클 에지를
   놓쳐 큐 머리가 pop되지 않고 정체(under-pop→stall)될 수 있다. 이는 **liveness 위협일 뿐**이며 over-pop/안전속성
   회귀는 구조상 불가(현장 분류=초 단위 ≫ 주기 150ms라 실무상 안전). C3는 (a) 이 안전 가정을 **불변식으로
   문서화**하고, (b) 실제로 정체가 의심되는 상태를 **WARN 로그 + operation_log로 발화**해 운영자가 감지·진단할 수
   있게 한다(현재는 완전 silent).
2. **pusher 관측 루프의 낭비 비용을 제거한다(무기능 변경).** `DestinationStatusPusher`의 소터 관찰 루프가 매 틱
   호출하는 `_status.Compute`(소터→`ComputeSorterFull`: cell/cell_assignment/sorter_command/piece 다중 집계 쿼리)의
   결과 중 발신 판정에 실제로 쓰이는 것은 `Ready ∧ !Paused`뿐이고 `Full`은 쓰이지 않는다. I-2가
   `SorterFloorReturnService`에서 한 것과 **동형**으로 경량 readiness(스냅샷 기반 Ready + `IsPaused`, `ComputeSorterFull`
   스킵)로 대체한다. **발신 결과는 완전히 동일**하고 비용만 절감된다(선재/후속으로 B 코드리뷰가 `important=1`로 명시).

---

## Implementation Scope
Generator가 **무엇을** 만들지의 목록. **어떻게**(정확한 메서드 시그니처·자료구조·주입 방식)는 Generator 재량.

### 항목 1 — 샘플링 스톨 fail-loud 감지기
1a. **불변식 문서화**: `ObserveIntervalMs ≪ 최소 분류 소요(Ready=0 창)` 안전 가정을 (i) `SorterFloorReturnOptions`
    XML doc 주석과 (ii) `docs/SPEC.md` §2-C(관측 루프) 에 명문화한다. "이 가정이 깨지면 사이클 에지 유실 →
    under-pop 가능"과 "그 경우 아래 스톨 감지기가 fail-loud로 발화"를 함께 기술.
    - 정적 설정 검증(IValidateOptions 등)은 **현재 리포에 인프라가 없다**(grep 0건). 분류 소요는 런타임/현장
      값이라 기동 시 정적 검증으로 알 수 없으므로, **런타임 스톨 감지기가 곧 이 불변식의 fail-loud 안전망**이다.
      (별도 정적 검증기 신설은 스코프 아님 — 문서화 + 런타임 감지로 닫는다.)
1b. **fail-loud 스톨 감지기**(관측 루프 계층 — 순수 코어 무접촉): `ObserveSorter`(또는 그 관측 상태)에 per-소터
    스톨 의심 카운터를 얹어, 다음 **AND 조건이 연속 N틱 지속**되면 WARN 발화:
    - 소터 Online(스냅샷 신뢰) **그리고** 정지 아님(`IsPaused==false`) — 즉 오프라인/PAUSED는 **정당한 미기입
      상태이므로 발화 안 함**.
    - 큐 머리 존재(비어 있지 않음).
    - 소터 유휴(`Ready==1`).
    - `TgtFloor==0`.
    - 큐 머리 층 값이 **직전 틱과 동일**(pop으로 머리가 바뀌지 않음).
    발화 시: (i) Serilog **WARN** 로그(소터 destId/chuteNo·큐 머리 층·지속 틱수 포함), (ii) `IOperationLogger`로
    **operation_log 1건**(구조화 detail) 기록. **에피소드당 1회만**(임계 도달 시 1회, 조건 유지 중 매 틱 반복
    발화 금지 — 로그 스팸 0). 조건이 하나라도 깨지면(머리 변경·busy(Ready==0)·`TgtFloor≠0`·큐 빔·오프라인·
    PAUSED) 카운터/발화 상태 **리셋**(다음 에피소드 재감지 가능).
1c. **설정화**(절대규칙 #7 — 하드코딩 0): 임계 틱수 N(및 필요 시 판정 주기)을 `SorterFloorReturnOptions`에 신규
    필드로 추가하고 `appsettings.json`(+`appsettings.Development.json` 필요 시)에 결선. 스톨 감지 자체를 끌 수 있는
    비활성 값(예: N≤0 = 감지 비활성) 허용 여부는 Generator 재량(단 기본값은 활성).

### 항목 2 — pusher 경량화
2a. **경량 readiness 대체**: `DestinationStatusPusher`의 관찰 경로(현재 `ComputeAccept`→`_status.Compute`)를,
    소터에 대해 **`ComputeSorterFull`을 호출하지 않는** 경량 readiness로 대체한다. 소터 accept = 스냅샷 기반
    Ready(게이트웨이 `bundle.Latest` + `DepositDecider`(순수, CurFloor 목표) — DB 무접촉) `∧ !IsPaused(destId)`
    (destination Status/IsActive 단일 조회). I-2가 `SorterFloorReturnService`에서 쓴 `IDestinationStatusService.IsPaused`
    경로와 **동형**. 신규 경량 산출 메서드를 `IDestinationStatusService`/`DestinationStatusService`에 추가할지,
    pusher 내부에서 조립할지는 Generator 재량(단 **소터 accept 값이 기존 `Compute().Ready ∧ !Compute().Paused`와
    비트 동일**해야 함 — `Full`은 accept에 미사용이므로 스킵해도 결과 불변).
2b. **슈트(비소터) 경로**: 현행 슈트 accept는 `ChuteCapacityService.GetHold`(인메모리 hold — DB 집계 없음)라 이미
    경량이다. 슈트 경로를 굳이 재작성하지 않아도 되나(§질문 3), 어느 선택이든 **슈트 accept 값은 불변**이어야 한다.
2c. **와이어·멱등 불변**: 발신 페이로드(`{chute_numbers:[ChuteNo], next_states:[3|2]}` snake_case), 층별 호스트
    라우팅(B), route별 전이당 1회 멱등(`PumpAsync`), 부트스트랩 순서(C2: 클리어 before push)는 **전부 무변경**.
    이번 변경은 **오직 accept 산출의 데이터 소스**(heavy Compute → light readiness)만 바꾼다.

### 스코프 경계 (무접촉·명시)
- **D(파킹존)·D4.3(PLC 이연) 미착수.** 스톨 감지기는 **관측-전용**(WARN + operation_log)이며, 미투하
  abandonment 복구·파킹/재dispatch 같은 **교정 동작은 하지 않는다**(그건 D 스코프). (§질문 4)
- **C1/C2 병합 로직 무접촉**: R-clear@Ready, 처리 3시각, 콜드스타트 StartupClear/I-3 큐 재파생 로직 diff 0.
- **절대규칙 #1**: PLC 단일 쓰기 큐 경로 무변경(항목 1·2 모두 쓰기 경로 미접촉 — 감지기는 읽기/관측만, pusher는
  아웃바운드 HTTP만). `bundle.EnqueueSetTgtFloorAsync`·`PlcGateway` diff 0.
- **절대규칙 #8**: `Wcs.Core`(DepositDecider·InductionFloorMap 등) **diff 0**. 판정/재파생은 순수 유지 —
  스톨 감지기는 서비스(트리거) 계층에만 존재.
- **Modbus 레지스터 맵·EF 마이그레이션·DB 스키마 변경 0.** `docs/ERD.md` 무접촉.
- **frontend diff 0**(UI 표면 없음 — 아래 Detected Type 참조).

---

## Evaluation Criteria (Backend/API — 4기준 구조, ★=가중치)
1. **API/서비스 설계 품질 (★★★)**: 스톨 감지기가 관측 루프 계층에 깔끔히 얹혀 순수 코어를 오염시키지 않는가.
   pusher 경량화가 기존 단일 발신 소스(Observe→PumpAsync) 구조를 우회·중복시키지 않고 데이터 소스만 교체하는가.
   신규 설정 필드가 기존 `SorterFloorReturnOptions`/appsettings 관행(주석·기본값)과 일관되는가.
2. **아키텍처 원본성 (★★★)**: I-2 경량화 패턴(IsPaused 분리)을 pusher에 **동형 재사용**했는가(중복 재발명 아님).
   스톨 감지 상태가 기존 `ObserveState`(단일 스레드 관측 루프 전용·락 불요)에 자연스럽게 통합됐는가.
3. **Craft (★★)**: 스톨 감지 오탐 0 경계가 정확한가(리셋 조건 누락 없음). 에피소드당 1회 발화(스팸 억제).
   operation_log detail 구조화. 스톨 감지기 예외가 관측 루프/형제 소터를 죽이지 않게 격리(기존 try/catch 패턴 유지).
   teardown 경쟁 방어(신규 상태/의존이 취소·dispose 경로를 깨지 않음).
4. **Functionality (★★)**: (1) 실제 스톨 상태에서만 WARN 발화·정상 동작에서 오탐 0. (2) pusher 발신 결과가
   경량화 전후 **완전 동일**(accept/next_state 값·전이당 1회 멱등). (3) `ComputeSorterFull`이 pusher 관찰 경로에서
   더 이상 매 틱 호출되지 않음(비용 절감 실증). (4) 절대규칙 #1/#7/#8 준수.

---

## Completion Conditions (Evaluator PASS 최소 조건)

### 항목 1 — 스톨 감지기
- **CC1.1 (오탐 0)**: 정상 분류 사이클(enqueue→정렬→분류 Ready 1→0→1→pop)이 도는 동안, 그리고 다음 정상 상태에서
  스톨 WARN이 **0건** 발화됨을 자동 테스트로 실증:
  - 큐 빈 소터(머리 없음),
  - 유휴이나 정상 사이클로 머리가 진행 중(pop마다 머리 변경 → 카운터 리셋),
  - 오프라인 소터, PAUSED 소터(정당한 미기입 — 발화 제외).
- **CC1.2 (실제 스톨에서만 발화)**: "큐 머리 존재 + 유휴(Ready==1) + `TgtFloor==0` + 머리 불변"이 임계 N틱을
  넘겨 지속하는 상태(Sim이 분류/틸트를 하지 않아 pop이 안 일어나는 상황)를 조성하면 **정확히 1회** WARN + operation_log
  발화됨을 실증(에피소드당 1회 — 지속 중 반복 발화 0). 이후 조건이 깨지면(예: 큐 머리를 소비시키거나 busy 전이)
  카운터가 리셋돼 새 에피소드에서 재발화 가능함을 실증.
- **CC1.3 (관측-전용 무부작용)**: 스톨 발화가 **PLC 쓰기/pop/재dispatch/파킹 같은 교정 동작을 유발하지 않음**을
  실증(D6 쓰기 0·큐 상태 불변·발화는 WARN 로그 + operation_log 기록뿐). `Wcs.Core` diff 0(grep/`git diff --stat`).
- **CC1.4 (설정화)**: 임계 N틱(및 주기)이 appsettings에서 읽힘(하드코딩 리터럴 0). XML doc + SPEC §2-C에 불변식
  문서 반영.

### 항목 2 — pusher 경량화
- **CC2.1 (발신 동일성)**: 경량화 후 아웃바운드 push 결과가 기존과 동일 — 가짜 RCS 수신 서버(FakeChuteState류)
  기준으로 (i) accept↔next_state 매핑 3(수용)/2(불가) 동일, (ii) 전이당 정확히 1회 멱등, (iii) 만재(SorterFull)
  전이가 push를 유발하지 않음(만재는 발신 accept에 미반영 — 기존 동작) 이 유지됨을 실증.
- **CC2.2 (기존 push 테스트군 회귀 0)**: `ChuteStatePushTests`·`SorterPushOperationalTests`·`RcsPushTests`·
  `ChuteRecoveryPushHeartbeatTests`·`B2cChutePushTests`(B2C) 전부 GREEN(카운트 불변). 특히 SorterPushOperational의
  `Ready ∧ !Paused` 합성·paused 재접힘·SorterFull 발신 제외 단언이 그대로 통과.
- **CC2.3 (비용 절감 실증)**: pusher 관찰 경로에서 `ComputeSorterFull`(또는 그를 포함하는 heavy `Compute`)이
  **매 틱 호출되지 않음**을 spy/카운트로 실증 — 기존 `TwoFloorHostRoutingTests`의 `CountingStatusService`
  (ComputeCount vs IsPausedCount) 패턴(VSE2a/b)과 동형으로: 소터 관찰이 N틱 도는 동안 heavy `Compute`
  호출 카운트가 (부트스트랩 등 정당 호출을 제외하고) 매 틱 증가하지 **않음**을 단언.

### 공통
- **CC3.1 (전체 GREEN)**: `dotnet test backend/Wcs.sln` 전량 GREEN. 신규 테스트 = baseline + 신규분 산술 일치로 확인.
- **CC3.2 (결정성·flake 0)**: 무거운 실-Sim 스위트 특성상 **clean-env에서 ≥5회 반복** GREEN·flake 0. 반복 전
  dotnet/testhost/vstest 전수 kill로 클린 슬레이트, 각 run 자연 완료(mid-run kill 금지), **TCP TIME_WAIT 드레인**
  후 측정(C2 교훈 — 소켓 고갈로 인한 testhost teardown abort는 기능 실패 아님·`netstat | grep -c TIME_WAIT` 선확인).
- **CC3.3 (정적 검사)**: `dotnet build` 0 오류, 신규 경고 0(기존 선재 NU1903류만 허용).
- **CC3.4 (평가 하네스 안전 — C2 유실 교훈)**: baseline 대조가 필요하면 `git stash`로 미커밋 코드를 제거하지 말 것.
  부득이 stash 시 **`-u`(untracked 포함) + 즉시 pop 보장**, 대조 후 `git diff`로 Generator 산출물 전량 보존 확인
  (C2 INCIDENT: baseline stash 드롭으로 19파일 미커밋 유실 → 복구). 미커밋 파일 revert 대조는 `git checkout` 금지
  (HEAD 소거) — Copy-Item 백업+SHA256 방식(S5-FLAKE 교훈).

---

## Parallel Modules (optional)
N/A (single module 권장). 두 항목은 파일 분리(항목1 = `SorterFloorReturnService.cs`·`WcsOptions.cs`·`appsettings*.json`·
`docs/SPEC.md` / 항목2 = `DestinationStatusPusher.cs`·`DestinationStatusService.cs`)라 경계-청정 fan-out이 **가능은**
하나, C3의 경량 스코프와 병렬 worktree의 stale-base/미커밋 유실 리스크(교훈 agent-worktree-stale-base·C2 INCIDENT)를
고려해 **단일 Generator 순차 구현을 권장**한다. 오케스트레이터가 fan-out을 택할 경우 위 파일 파티션을 경계로 사용.

## Evaluation Dimensions (optional)
functional only. (항목2에 성능 측면이 있으나 별도 성능 Evaluator 불요 — 기능 회귀 테스트군 + spy 카운트로 충분히 커버.
보안/권한 표면 무접촉.)

---

## Detected Project Type: Full-stack
(리포 신호: `frontend/`(브라우저 진입점) + `backend/src/Wcs.Api`(서버 라우트/컨트롤러) 공존 → Full-stack.
단 **이번 스프린트의 변경 표면은 백엔드 백그라운드 서비스 전용**이며 프론트엔드 diff는 0이다.)

## Verification Scenarios (per-type, mandatory)

=== Full-stack ===

- **Applicable Web/UI scenarios (frontend surface this sprint touches)**:
  **N/A — 이 스프린트는 UI 표면을 건드리지 않는다**(frontend diff 0, 신규 페이지/컴포넌트/네비게이션 0).
  스톨 WARN·operation_log는 기존 F2 실시간 관측(operation_log tail)로 자연히 노출되나, 그 뷰 코드는 변경하지
  않으므로 브라우저 검증 대상 아님. 프론트 무접촉은 `git diff develop --stat -- frontend/` 빈 출력로 실증.

- **Applicable Backend/API scenarios (backend surface this sprint touches)**:
  이 스프린트는 인바운드 HTTP 라우트를 **신규/변경하지 않는다**(엔드포인트 method+path 변경 0). 변경 표면은
  (i) 백그라운드 관측 루프 2종, (ii) 아웃바운드 IF-08 push 와이어, (iii) operation_log 싱크. 각 표면의 정상/에지
  시나리오를 자동 테스트로 검증:
  1. **관측 루프 — 스톨 감지기(`SorterFloorReturnService`)**:
     - 정상(happy): 정상 사이클 관측 중 스톨 WARN 0건(CC1.1).
     - 에지(스톨): 머리 존재+유휴+TgtFloor==0+머리 불변 N틱 지속 → WARN + operation_log 정확히 1회(CC1.2).
     - 에지(정당 미기입): 오프라인/PAUSED에서 발화 0(CC1.1).
     - 에지(리셋): 조건 해소 후 재에피소드 재발화 가능(CC1.2).
  2. **아웃바운드 IF-08 push 와이어(`DestinationStatusPusher` → PUT `/api/UpdateChuteState`)**:
     - 정상(happy): 수용상태 전이 시 가짜 RCS 서버가 `{chute_numbers:[N], next_states:[3|2]}`를 전이당 1회 수신
       (경량화 전후 **동일** — CC2.1).
     - 에지: 만재(SorterFull) 전이는 push 유발 안 함(발신 accept에 Full 미반영); paused 전이는 next_state=2 1회.
     - 에지(비용): 소터 관찰 N틱 동안 heavy `Compute`/`ComputeSorterFull` 매-틱 호출 0(CC2.3).
  3. **operation_log 싱크**: 스톨 WARN 1건이 operation_log에 Level=WARN·구조화 detail로 영속(비동기 싱크 —
     테스트에서 기록 출현 대기 후 조회. 교훈 sim-timeline-log-vs-snapshot-race: 비동기 append는 스냅샷 전이와
     경합하므로 로그 출현을 조건 대기 후 캡처).

- **At least one end-to-end data-flow scenario crossing two or more layers**:
  1. **스톨 감지 크로스레이어(관측 루프 → operation_log → DB)**: 실 Sim 소터를 유휴·큐 머리 존재·TgtFloor==0
     상태로 붙잡아(틸트 미발생) 관측 루프가 임계 N틱 관측 → Serilog WARN 발화 + `IOperationLogger` enqueue →
     백그라운드 컨슈머가 operation_log 테이블에 영속 → 조회로 1건 확인. "관측 루프 신호가 실제 DB 감사 기록까지
     도달"을 계층 관통으로 실증.
  2. **pusher 경량화 발신 동일성 크로스레이어(스냅샷 관찰 → accept 합성 → 아웃바운드 HTTP)**: 소터 CurFloor/Ready
     전이를 실 Sim로 발생시켜 관찰 루프가 경량 readiness로 accept 산출 → 가짜 RCS 서버가 수신한 실제 JSON 본문이
     경량화 전 동작과 동일(next_state·전이당 1회)함을 실증. 동시에 spy로 `ComputeSorterFull` 매-틱 미호출 확인 —
     "발신은 동일, 비용만 절감"을 한 시나리오에서 계층 관통으로 닫는다.

---

## 질문 (사용자 확인 필요 — 기본값 채택, 계약 확인 시 한 줄 승인 권장 · 블로킹 아님)
C3는 규칙 개정·마이그레이션이 없어 사용자 게이트는 불요하나, 아래 4건은 기본값을 제안하며 이견 시에만 회신.

1. **스톨 감지 임계 N틱 기본값**: 정상 대기(정렬 후 AGV 틸트 대기)를 오탐하지 않도록 N×`ObserveIntervalMs`가
   "현장 최대 분류 소요 + AGV 도착 cadence"를 충분히 상회해야 한다. **권장 기본값 = N틱을 지속시간으로 환산해
   약 6~10초 상당**(예: `ObserveIntervalMs`=150ms 기준 N≈40~66). Generator가 틱수 또는 지속시간(ms) 중 어느
   형태로 설정화할지는 재량이되 기본값은 이 범위. (이견 없으면 이대로.)
2. **발화 수준 — WARN만 vs 알람**: **권장 = Serilog WARN + operation_log Level=WARN**(진단 신호). ERROR/알람
   에스컬레이션은 하지 않는다(스톨은 liveness 의심일 뿐 — 실제 abandonment 복구/알람은 D 스코프). (이견 없으면 이대로.)
3. **pusher 경량 readiness 적용 범위 — 슈트에도 vs 소터 한정**: **권장 = 소터 한정**(슈트 accept는 이미
   인메모리 `GetHold` 기반이라 `ComputeSorterFull`을 타지 않음 — 최적화 대상 아님). 단 어느 선택이든 슈트 accept
   값은 불변이어야 함. Generator가 코드 단순화를 위해 공통 경량 경로로 통합해도 무방(값 불변 전제). (이견 없으면 이대로.)
4. **스톨 감지기 = 관측-전용 확인**: **권장 = 관측-전용**(WARN + operation_log만, 교정 동작 0). 파킹/재dispatch/
   미투하 복구는 D 스코프로 이연 유지. (이견 없으면 이대로.)

---

> Planner self-check — Detected project type: Full-stack. Required scenario slots: 3 (Applicable Web/UI scenarios [N/A·근거명시], Applicable Backend/API scenarios [관측루프 스톨감지·아웃바운드 IF-08 push·operation_log 싱크], At least one end-to-end cross-layer data-flow scenario [스톨감지 관측→operation_log→DB · pusher 경량화 발신동일성 스냅샷→accept→HTTP]). All slots filled: yes.

---

## ✅ 확정 결정 (오케스트레이터, 2026-07-24, C3 — 사용자 "C3 진행" 위임 + Planner 권장 채택)

게이트 불필요(규칙/마이그레이션 없음). 4개 기본값 = Planner 권장 확정(전부 appsettings·사후 조정 가능):
- **스톨 감지 N틱**: 기본 ≈6~10초 상당(ObserveIntervalMs 배수, appsettings·하드코딩 0).
- **레벨**: WARN + operation_log 1회/에피소드. 에스컬레이션/알람 없음(관측 전용).
- **경량 readiness 적용 범위**: 소터 한정(슈트는 이미 인메모리 GetHold — ComputeSorterFull 미사용이라 이득 없음).
- **스톨 감지기 = 관측 전용**: 파킹/복구/자동조치 없음(그건 Sub-Sprint D). 순수 fail-loud 가시화.
