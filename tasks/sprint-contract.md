# Sprint Contract — RCS↔WCS 인터페이스 재설계 (Phase 1 / 2)

> Branch: `feat/rcs-if-redesign` (develop @ PR #11 머지에서 분기)
> Planner 작성. **WHAT만 정의 — HOW(라이브러리·클래스·시그니처)는 Generator 결정.**
> 스펙 단일 진실 = working tree 미커밋 HTML 5건(`docs/wcs_rcs_interface_kr.html`·`wcs_3ds_interface.html`·`wcs_3ds_unified_sequence.html`·`wcs_rcs_3ds_master_spec.html`·신규 `wcs_rcs_interface.html`).

---

## 0. 분할 평가 + Phasing 개요 (가장 중요)

### 분할 판단
이 변경은 크다(인터페이스 4종 재설계 + 방향 역전 푸시 신설 + Controller 전환 + 시나리오 대거 재작성). **단일 스프린트로 묶으면** ① 인바운드(IF-05/09/10)와 아웃바운드(IF-08 푸시)가 한 사이클에 섞여 Evaluator가 회귀/신규를 분리 검증하기 어렵고, ② 아웃바운드 HTTP 클라이언트(신규 컴포넌트 + 가짜 RCS 수신 엔드포인트 + 재시도)는 인바운드와 파일·관심사 경계가 깨끗이 분리되므로, **2개 서브 스프린트로 쪼갠다**.

권고 시작점(프롬프트 제시)을 검토한 결과 **2-Phase 분할이 최적**으로 판단한다. 3-Phase(생존 Controller화 / IF-09+full / IF-08 푸시)는 Phase 1·2가 같은 컨트롤러·DTO·DepositDecider를 동시에 건드려 경계가 겹치고 중간 산출물이 "동작하지만 미완"인 어정쩡한 상태를 만든다. 반대로 IF-09 도착(2층 정렬)과 IF-05 reason 제거·FULL/PAUSED→NG는 **같은 DepositDecider 재용도 + 같은 인바운드 컨트롤러 표면**을 공유하므로 한 Phase로 묶는 게 응집도가 높다. 아웃바운드 푸시만 독립 Phase로 분리.

> 대안 고려: "Phase 1을 다시 1a(Controller 골격만)/1b(판정 변경)로" — 불필요한 과분할. Controller 전환과 판정 변경은 같은 파일을 만지므로 한 번에. (1줄 메모)

### Phasing 개요 (Phase별 한 줄)
- **Phase 1 (인바운드 + 구조 전환) ← 이번 계약 상세**: Minimal API → Controller 전환(IF-05/09/10), `deposit-permission`(IF-08 폴링) 엔드포인트 제거, IF-05 응답 reason 제거 + **FULL/PAUSED→NG**·BUSY→OK, IF-09 도착 보고 신설(2층 정렬 `TgtFloor=2`), `full`/`ready` 내부 산출 함수화(IF-05 NG 필터 + Phase 2 푸시 공용), DepositDecider 재용도(2층 고정·WRONG_FLOOR 개념 소멸), S1~S9·VS 시나리오 재작성.
- **Phase 2 (아웃바운드 푸시) ← 개요만**: WCS→RCS 목적지 상태 푸시 클라이언트(`POST {RCS base}/api/v1/destination-status`, 페이로드 `{chuteNo, ready, timeStamp}`), 복합 단일 `ready` 전이 감지(변화원 둘: 슈트 ChuteCapacityService + 소터 게이트웨이 스냅샷), RCS base URL 설정·실패 재시도, 가짜 RCS 수신 엔드포인트로 전이당 1회 푸시 검증(슈트+소터 둘).

---

## [Sprint Contract] — Phase 1

### Goal
RCS↔WCS 인터페이스를 새 스펙(HTML 5건)에 맞게 **인바운드 계층 + 구조를 재설계**한다. 투입가부 폴링(IF-08 `deposit-permission`)을 **폐지**하고, FULL/PAUSED 차단을 **도착 시점(폐지된 IF-08)에서 배정 시점(IF-05)으로 상류 이동**시킨다. AGV 도착 보고(IF-09)를 신설해 WCS가 3DS를 **2층 고정 정렬**하도록 한다. Minimal API 엔드포인트를 **Controller 구조**로 전환한다. Phase 2 아웃바운드 푸시가 소비할 **복합 `ready`/`full` 내부 산출**을 함수로 선확보한다. **아웃바운드 푸시 자체는 Phase 2** — 이번 Phase는 인바운드 계약과 내부 산출까지만.

### Implementation Scope (Generator가 해야 할 일 — WHAT)

**A. 구조 전환 (MVC 흡수)**
1. `src/Wcs.Api`의 Minimal API 엔드포인트(`/api/v1/destination-query`·`/api/v1/deposit-report`)를 **Controller 구조로 이관**한다(동작 보존). IF-09는 신규 Controller 액션. 엔드포인트를 두 번 만들지 않는다(이관이지 병행 아님).
2. `/api/v1/deposit-permission`(IF-08 폴링) 엔드포인트·그 요청/응답 DTO(`DepositPermissionRequest`/`DepositPermissionResponse`)·전용 핸들러 분기를 **완전 제거**한다. 잔존 참조 0(grep 0).

**B. IF-05 목적지 조회 — 응답 reason 제거 + FULL/PAUSED→NG**
3. IF-05 응답을 `{result, chuteNo}`로 축소한다(reason 필드 제거 — RCS로 전송하지 않음). 사유는 기존대로 piece_event(IF05_REQ/RES)에 **내부 기록**으로 유지한다.
4. IF-05 배정 판정에 **FULL·PAUSED 상류 필터**를 추가한다: 대상 목적지가 FULL(만재) 또는 PAUSED(정지)이면 **NG**(chuteNo=null, 배정 안 함). BUSY(분류 중·이동 중)는 **OK**(이동시키되 도착 후 투입은 Phase 2 푸시가 ready일 때). result→사유 매핑: NORMAL→OK / BUSY→OK / FULL·PAUSED→NG / OVER·COMPLETED·NO_DEST·OFFLINE→NG. 내부 사유는 piece_event에 기록.
   - FULL/PAUSED 판정의 산출원은 **슈트 산출(ChuteCapacityService.GetHold)**과 **소터 산출**을 같은 함수로 공유(아래 E).

**C. IF-09 도착 보고 신설 + 2층 고정 정렬**
5. `POST /api/v1/arrival-report` 신설. 요청 `{pId, chuteNo, agvNo, timeStamp}`, 응답 `{result:"OK"}`. AGV가 슈트에 도착 시 1회 호출.
6. 도착 보고 수신 시 WCS가 **도착을 기록**한다(DB — piece_event 또는 동등 이력. 정확한 테이블/이벤트 타입은 Generator가 ERD에 맞게 결정 — 단 §사용자 질문 1·2 확인 후).
7. 목적지가 **3D 소터 슈트**면 WCS가 3DS를 **2층(고정)으로 정렬**한다 — `TgtFloor=2` 쓰기. **층 정보는 RCS와 주고받지 않는다**(WCS 내부). TgtFloor 쓰기 조건은 절대규칙 #2를 2층 운영으로 갱신: `TgtFloor==0 && (CurFloor≠2 || 분류 중)` → `TgtFloor=2`. 진행 중(≠0)엔 덮어쓰지 않음(핑퐁 차단). FULL/PAUSED/OFFLINE이면 쓰지 않음. WCS는 TgtFloor를 클리어하지 않음(절대규칙 #3 불변 — PLC가 분류 시작 시 클리어).
8. 목적지가 **슈트 전용(비3D)**이면 도착만 기록(층 정렬 없음).
9. **운영 2층 고정값은 설정화**: 하드코딩 `2` 금지. 설정 키(권고: `OperationalFloor`, 기본 2)에서 읽는다. 절대규칙 #7(시간·층값 하드코딩 금지) 준수.

**D. DepositDecider 재용도 (Wcs.Core)**
10. DepositDecider를 **2층 고정 운영 + WRONG_FLOOR 개념 소멸**에 맞게 재용도한다. 구 IF-08 가부 응답(allowed/reason)을 산출하던 용도는 사라진다. 새 용도는 **(a) IF-09 TgtFloor=2 쓰기 판단**(쓸지/말지 + 쓸 값) **(b) Phase 2가 소비할 소터 `ready` 산출의 순수 로직 재료**. `ready` 기준 = `online && CurFloor==2 && Ready==1`. 구 `agvFloor` 비교는 정렬에 불요(AGV는 항상 2층 수령) — `IAgvFloorResolver`는 정렬 용도 소멸(기록용으로만 잔존 허용).
    - 판정 로직은 Wcs.Core에 **순수 함수**로 유지(I/O·DI 의존 금지 — 절대규칙 #8). 시그니처 변경은 Generator 결정이되, 순수성·I/O 무의존은 불변.

**E. `full`/`ready` 내부 산출 함수화 (Phase 2 공용 선확보)**
11. 목적지별 상태를 내부에서 산출하는 경로를 함수로 정리한다:
    - **일반 슈트**: `full = ChuteCapacityService 만재` / `paused = destination.status==PAUSED 또는 비활성`. IF-05 NG 필터(B-4)가 이를 소비.
    - **3D 소터 슈트**: `full`(빈 셀 없음 포함) / `paused` / `online` + DepositDecider의 `ready`(`online && CurFloor==2 && Ready==1`). IF-05 NG 필터가 소비.
    - 이 산출은 **Phase 2 아웃바운드 푸시가 복합 단일 `ready`로 접을 재료**다. Phase 1에서는 IF-05 NG 필터로만 소비하고, **푸시 클라이언트는 구현하지 않는다**(Phase 2). 개별 `full`/`paused` 필드를 외부로 내보내지 않는다.

**F. 테스트 재작성 (회귀가 아니라 명시적 전환)**
12. 아래 §"회귀·전환 명세"에 따라 테스트를 재작성/삭제/유지한다. 새 검증은 IF-05(reason 없음·FULL/PAUSED→NG·BUSY→OK)·IF-09(도착 기록·2층 정렬 TgtFloor=2·핑퐁 차단·슈트 전용 무정렬)·구조(deposit-permission 부재)를 자동화 테스트로 입증.

**G. HTML 5건 커밋 포함**
13. working tree 미커밋 HTML 5건(4 modified + 1 new `wcs_rcs_interface.html`)을 이번 작업에 포함해 커밋 대상으로 둔다(스펙 원본 동기화).

### 무변경 유지 (절대 건드리지 말 것)
- **Modbus 레지스터 맵**(D0~D6·FC03/06/16) — `Wcs.Core/RegisterMap`·`PlcSnapshot.FromRegisters` 본문.
- **C/R 핸드셰이크 프로토콜** — `Wcs.PlcGateway/HandshakeOrchestrator`·`PlcGateway` 본문(단일 쓰기 큐·RMW·`_clientLock` 직렬화·OFFLINE 전이 이벤트). IF-09 2층 정렬은 **기존 쓰기 큐(`SetTgtFloor`)를 경유**할 뿐 게이트웨이 본문을 바꾸지 않는다.
- `Wcs.Sim3ds` 프로토콜 본문.
- alarm·sorter_command 영속화(`IAlarmSink`/`ISorterCommandJournal` + EF 구현)·OFFLINE 전이당 1건 멱등 — **재활용**(IF-10→IF-11 핸드셰이크 경로는 유지).
- RTU 레지스터 base 주소 설정화 — **OUT(M5 배포 이슈)**.

### Scope OUT (이번 Phase 아님)
- **IF-08 아웃바운드 푸시 클라이언트 전체**(`POST {RCS base}/api/v1/destination-status`, 복합 ready 전이 감지, RCS base URL·재시도, 가짜 RCS 수신 검증) → **Phase 2**.
- Phase 2 전용 테스트(전이당 1회 푸시·슈트/소터 둘) → Phase 2.

---

## Evaluation Criteria (Evaluator 판정 기준 + 가중)
프로젝트 타입 = Backend/API. 4-기준 구조:

1. **API Design Quality (★★★)** — (a) Controller 전환이 RESTful·일관 네이밍·버전(`/api/v1`) 유지. (b) IF-05 응답이 정확히 `{result, chuteNo}`(reason 부재, NG면 chuteNo=null). (c) IF-09 요청/응답이 스펙 페이로드와 정합(camelCase 와이어). (d) `deposit-permission` 엔드포인트·DTO 완전 부재(grep 0, 호출 시 404/405). 검증실패만 400, 가부는 200+result.
2. **Architecture Originality (★★★)** — (a) DepositDecider 순수성·Wcs.Core I/O 무의존 유지(`Wcs.Core.csproj` Reference/Package 0, static·무필드·DateTime/Random/I-O 0). (b) `full`/`ready` 산출이 **단일 산출 경로**로 함수화돼 IF-05 소비 + Phase 2 푸시 재사용 가능한 확장점. (c) 2층 고정값 설정화(하드코딩 `2` grep 0, 산출 1지점). (d) Controller 흡수가 엔드포인트 이중 생성 0.
3. **Craft (★★)** — (a) IF-09 TgtFloor=2 쓰기가 절대규칙 #1(단일 큐 경유, 핸들러 직접 Modbus 호출 0)·#2(TgtFloor==0 && (CurFloor≠2||분류중) 조건·핑퐁 차단)·#3(WCS 클리어 0) 준수. (b) 입력 검증(pId 범위·필수 필드)·에러 응답·트랜잭션 경계 유지. (c) fire-and-forget(TgtFloor 큐 투입)은 예외 삼킴 금지(`.ContinueWith` IsFaulted 로깅). (d) 빌드 경고 0.
4. **Functionality (★★)** — (a) IF-05 FULL/PAUSED→NG·BUSY→OK·NORMAL→OK가 실제 동작. (b) IF-09 도착이 DB에 기록되고 3D 소터면 2층 정렬, 슈트 전용이면 무정렬. (c) IF-10 투입 보고·IF-11 핸드셰이크 경로 회귀 0(유지 시나리오 GREEN). (d) HTML 5건이 커밋 대상에 포함.

**가중**: ★★★ 항목(1·2) 하나라도 FAIL이면 APPROVED 불가. ★★ 항목(3·4)은 BLOCKING 결함이면 FAIL.

## Completion Conditions (Evaluator PASS 최소 조건)
- 솔루션 빌드 경고 0·에러 0(`dotnet build`).
- 전체 테스트 GREEN(`dotnet test`) — 재작성된 시나리오 포함. flaky 의심 테스트(실 Sim 소켓 기반)는 단독 다회 연속 GREEN으로 비결정성 0 확인(feedback-archive 메타교훈).
- `deposit-permission` 잔존 참조 0(grep: 엔드포인트·DTO·핸들러).
- IF-05 응답 JSON에 reason 키 부재 — 실 HTTP 응답 본문으로 입증.
- IF-09 도착→2층 정렬: 실 Sim(또는 Fake) 통합 테스트로 `TgtFloor=2` 기입 관찰 + 핑퐁 차단(≠0 구간 추가 쓰기 0) + 슈트 전용 무정렬을 자동화 단언.
- `Wcs.Core`(DepositDecider·Models·RegisterMap) 순수성 불변 + `Wcs.PlcGateway`/`Wcs.Sim3ds` 프로토콜 본문 `git diff` 0바이트 입증. (단 Phase 1은 Core 시그니처 변경 허용 — Core diff는 0이 아닐 수 있음; 순수성·I/O 무의존만 불변.)
- 하드코딩 층값 `2` grep 0(설정 경유 확인).
- HTML 5건 working tree 포함 확인(`git status`).

## Parallel Modules
N/A (single module — Wcs.Api 인바운드 계층 중심, Wcs.Core 판정 재용도가 강결합. 파일 경계가 겹쳐 병렬 분할 시 충돌. 기본 1 Generator.)

## Evaluation Dimensions
functional only (단일 차원 — 동시성 표면은 기존 게이트웨이/핸드셰이크 무변경이라 신규 동시성 위험이 낮음. 단, 4-Tier 독립 코드리뷰는 APPROVED 후 별도 게이트로 수행 — feedback-archive 메타교훈: 인메모리/단일프로세스 테스트가 못 보는 결함을 거름).

---

## Detected Project Type: Backend/API
프로젝트 신호: `src/Wcs.Api`에 서버 라우트/핸들러(Minimal API → Controller 전환)와 서버 엔트리포인트(`Program.cs`, ASP.NET Core, `Microsoft.NET.Sdk.Web`)가 존재하고, 같은 리포에 브라우저 대면 UI 트리 없음(docs/의 HTML은 정적 인터페이스 정의서이지 클라이언트 렌더 뷰가 아님). 따라서 Backend/API.

## Verification Scenarios (Backend/API — mandatory)

### 이번 Phase가 건드리는 엔드포인트 (method + path)
- `POST /api/v1/destination-query` (IF-05 — 응답 reason 제거·FULL/PAUSED→NG 추가, Controller 이관)
- `POST /api/v1/arrival-report` (IF-09 — **신설**, 2층 정렬)
- `POST /api/v1/deposit-report` (IF-10 — Controller 이관, 동작 보존)
- `POST /api/v1/deposit-permission` (IF-08 폴링 — **제거**: 호출 시 404/405 검증)

### Happy path per endpoint (expected input → expected output shape)
- **IF-05 / destination-query**
  - NORMAL: `{pId, agvNo, barcode, inductionNo, qty, timeStamp}`(시드 매칭, 목적지 NORMAL) → `200 {result:"OK", chuteNo:<int>}` (reason 키 부재).
  - BUSY: 목적지가 분류 중/이동 중(소터 Ready=0 또는 CurFloor≠2) → `200 {result:"OK", chuteNo:<int>}` (BUSY여도 OK·이동).
- **IF-09 / arrival-report**
  - 3D 소터 슈트 도착: `{pId, chuteNo:<sorter>, agvNo, timeStamp}` → `200 {result:"OK"}` + 도착 DB 기록 + (TgtFloor==0·미정렬·분류중 조건 충족 시) `TgtFloor=2` 쓰기 큐 투입 관찰.
  - 슈트 전용 도착: `{pId, chuteNo:<chute>, agvNo, timeStamp}` → `200 {result:"OK"}` + 도착 DB 기록, TgtFloor 쓰기 0건.
- **IF-10 / deposit-report**
  - 슈트 보고: `{pId, barcode, chuteNo, agvNo}` → `200 {result:"OK"}` (멱등 — 중복 재보고도 OK). 3D 목적지면 IF-11 핸드셰이크 트리거(회귀 유지).

### Relevant error cases per endpoint (Planner가 적용분만 선택 — pad 금지)
- **IF-05**: pId 범위 밖(0 또는 >30000)·barcode 누락·qty≤0 → `400`. 미매칭 바코드 → `200 {result:"NG", chuteNo:null}`. 목적지 FULL → `200 {result:"NG", chuteNo:null}`. 목적지 PAUSED → `200 {result:"NG", chuteNo:null}`.
- **IF-09**: pId 범위 밖·chuteNo≤0 → `400`. 미존재/비활성 chuteNo → 도착 기록은 남기되 정렬 없음(500 금지). 검증은 500 부재 + 200/400 정합.
- **IF-10**: pId 범위 밖·barcode 누락·chuteNo≤0 → `400` (기존 동작 보존).
- **IF-08 deposit-permission (제거 확인)**: `POST /api/v1/deposit-permission` 호출 → `404`(또는 405) — 엔드포인트 부재 입증.

---

## 회귀·전환 명세 (어떤 테스트가 바뀌고 어떤 게 유지되는가)
**이건 동작 보존 리팩터가 아니다.** "0 회귀"가 아니라 명시적 전환:

### 삭제/대체 (구 IF-08 폴링 전제 — 폐지)
- `ApiIntegrationTests`의 **VS-3/VS-4 IF-08 라이브 분기**(`deposit-permission` 호출, allowed/reason·WRONG_FLOOR·BUSY·OFFLINE 단언) → **삭제**(엔드포인트 폐지). 일부는 IF-09 2층 정렬 테스트로 **재타겟**(WRONG_FLOOR 개념 소멸 → 2층 미정렬→TgtFloor=2 정렬로).
- `ScenarioTests`의 **S1/S5/S6**가 IF-08 `deposit-permission`을 폴링하던 `SendIf05AndIf10Async` 헬퍼 → **IF-08 폴링 단계 제거**(IF-05→(IF-09 도착)→IF-10 흐름으로 재작성). 핸드셰이크 DB 단언(COMPLETED/MISMATCH/TIMEOUT·alarm)은 **유지**(IF-10→IF-11 경로 불변).
- **S8 FULL/PAUSED**: 구 IF-08 FULL/PAUSED 응답 단언 → **IF-05 FULL/PAUSED→NG**로 재타겟(FULL/PAUSED 차단이 도착→배정으로 상류 이동).
- **S2/S3/S4/S9**(PlcGateway 직접 — WRONG_FLOOR/BUSY 선기입·핑퐁·경합): DepositDecider 재용도로 **2층 고정 기준 재작성**(agvFloor 비교 → CurFloor==2 비교, WRONG_FLOOR→미정렬). 게이트웨이 동작(D6 쓰기·핑퐁·클리어) 입증 골격은 유지.

### 유지 (재활용)
- IF-05 happy(NORMAL→OK·chuteNo)·검증(pId 범위·qty≤0·미매칭 NG) — 단 응답에서 reason 단언 제거.
- IF-10 happy·멱등(CONCUR-1 8병렬 동일 pId)·IF-11 트리거(3D)·슈트 무트리거(대조) — Controller 이관 후에도 동작 보존.
- 핸드셰이크 alarm/sorter_command 영속화(S5/S6)·OFFLINE 전이당 1건(S7) — 게이트웨이 무변경이므로 그대로.

> Generator는 시나리오 번호를 보존할 필요 없다. 새 인터페이스를 덮는 검증이면 재구성·재명명 가능. 단 **"어떤 구 단언이 왜 삭제/유지되는지"를 sprint-log.md에 명시**해야 Evaluator가 전환을 추적할 수 있다.

---

## 사용자 확정 (2026-06-23 — 진행 승인)
1. **IF-09 도착 DB 기록 = `piece_event`에 신규 이벤트 타입 `IF09_ARRIVAL` 추가** (사용자 승인). DB protected zone 변경 허용됨. **양 provider 마이그레이션 동반**(Wcs.Migrations.Sqlite + Wcs.Migrations.SqlServer 둘 다 — P1 교훈: provider별 별도 마이그레이션 어셈블리·스냅샷, `has-pending-model-changes` 양쪽 "No changes" 확인). 테스트는 EnsureCreated라 모델만으로 동작하나, 마이그레이션도 반드시 추가. piece_event.event_type CHECK 제약에 `IF09_ARRIVAL` 반영.
2. **IF-09 시 piece 상태 전이 없음 — 기록만** (사용자 확정). piece.status는 그대로(RESERVED/PERMITTED 유지, IF-10에서 DEPOSITED). `ARRIVED` 신규 status 미추가 → piece status CHECK 마이그레이션 불요. 도착은 piece_event(IF09_ARRIVAL)로만 남김.
3. **미존재/비활성 chuteNo IF-09**: 200 OK + 도착 기록만, 정렬 스킵(500 금지). (기본값 채택.)

---

> **Planner self-check** — Detected project type: Backend/API. Required scenario slots: 3 (endpoints touched [4 endpoints: IF-05/IF-09/IF-10/deposit-permission 제거], happy path per endpoint [IF-05/IF-09/IF-10], error cases per endpoint [IF-05/IF-09/IF-10 + deposit-permission 부재 확인]). All slots filled: yes.
