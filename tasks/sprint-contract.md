[Sprint Contract]

# S-TWO-FLOOR-CONTROL — 인덕션 기반 2층(1·2층) 제어 (전체 9항목)

> 설계는 이미 문서화·병합됨(PR #70·#71). **SPEC.md·docs/*가 SOURCE OF TRUTH**이며 이 계약은 "무엇을
> 만들고 어떻게 검증하는가"만 정의한다(기술 세부 결정은 Generator 몫). 범위가 커서 **4개 서브 스프린트로
> 분할**하고, 본 계약은 **첫 서브 스프린트(A)를 전체 상세**로 정의한다. B·C·D는 말미 로드맵 1줄씩.

---

## 0. 현행 코드 실측(추측 아님 — 소스 직접 확인)

Planner가 변경 대상 코드를 직접 읽어 확인한 "지금 상태":

| 항목 | 실측 현행 상태 |
|---|---|
| InductionFloorMap | **없음.** `Wcs:OperationalFloor=2`(단일 층)만 있음. 레거시 `Floors:AgvNoToFloor`는 "정렬 미사용" 주석. IF-05는 inductionNo로 층을 파생하지 않음. |
| 소터별 pending-floor 큐 | **없음.** `DepositDecider`는 무상태 순수 함수. SPEC §2-C가 "큐가 없다 — 코드 스프린트에서 신설" 명시. |
| TgtFloor 기입 | `DepositDecider.Decide(snap, **operationalFloor**, hold)` — 단일 층(기본 2)을 씀. **IF-09 도착**이 트리거(`RcsController.AlignSorterToOperationalFloor`). TgtFloor==0 관측 기반 아님. |
| ready 판정 | `DestinationStatusService.ComputeSorter`가 `CurFloor==operationalFloor`(단일 층)로 산출. |
| IF-08 push | 단일 `Wcs:ChuteStatePush:BaseUrl`(1개 호스트, 현재 null=DORMANT). 전이당 chuteNo 1건 push. 층별 호스트·소터 dual-host 없음. |
| IF-09 DTO | `ArrivalReportRequest`(pId,chuteNo,agvNo,timeStamp) — **층 필드 이미 없음**. 단 IF-09가 정렬을 트리거함(제거 필요). |
| R clear 시점 | 핸드셰이크가 **R_Flag==1 즉시** ClearR(성공·불일치 모두). Ready==1 대기 없음. |
| sorter_command 컬럼 | Id,PieceId,CellId,CSeq,CellNo,**CWrittenAt**,RSeq,RCellNo,**RFlagAt**,Status,CreatedAt,ArchivedAt. **depositedAt/tiltedAt/returnedAt 없음**(RFlagAt≈tiltedAt 개념 중복 소지 — C에서 판단). |
| 재시작 클리어 | 기동 첫 폴 R_Flag==1일 때만 ClearR(`PollCycleAsync`). C/R/TgtFloor 전체 클리어 없음. |
| 마이그레이션 | provider 분리: `Wcs.Migrations.SqlServer` / `Wcs.Migrations.Sqlite`(각자 Migrations 폴더). 최신 = `AddPieceArchivedAt`(2026-07-13). |
| Sim3ds | SPEC §6대로 `TgtFloor≠0 && TgtFloor≠CurFloor`면 이동→CurFloor 기입(2층 이동 이미 지원). `InitialCurFloor=1`. |

---

## 서브 스프린트 A — 층 판정 코어 (인덕션 층맵 + 소터별 FIFO 큐 + TgtFloor==0 관측 기반 순서 기입 + ready 층별)

**사용자 요청 대응 항목: #1(InductionFloorMap) · #2(소터별 pending-floor FIFO 큐) · #3(TgtFloor 층-파라미터화) · #4(ready 층별 판정). + #6의 "IF-09 정렬 트리거 제거"(큐 구동으로 대체되므로 A에 동반, DTO/문서 층 표기 정리는 B).**

### Goal
WCS가 IF-05 요청 `inductionNo`로 목표 층 F(1/2)를 파생하고, **소터별 pending-floor FIFO 큐**에 IF-05
순서대로 F를 enqueue한다. 각 소터의 **`TgtFloor==0`을 관측**할 때 큐 머리(head) 층 F를 게이트
(`TgtFloor==0 && (CurFloor!=F || Ready==0)`, FULL/PAUSED/OFFLINE이면 미기입) 통과 시 **소터별 단일
쓰기 큐**로 기입해 소터를 그 층으로 복귀시킨다. 층 파생·게이트·ready는 **Wcs.Core 순수 함수**(테스트가
스펙)로 유지한다. 기존 "단일 OperationalFloor 고정 정렬(IF-09 트리거)"을 **큐 구동 다층 제어**로 대체.

### Implementation Scope (Generator가 만들 것 — HOW는 Generator 결정)
1. **InductionFloorMap 설정** — `WcsOptions`에 `InductionFloorMap`(inductionNo→floor, 예 `{"1":1,"2":1,"3":2}`)
   추가 + appsettings `Wcs:InductionFloorMap` 결선. 하드코딩 금지(절대규칙 #7).
2. **층 파생 순수 함수** — inductionNo→F를 Wcs.Core 순수 함수로(맵은 호출자 주입, I/O·DI 의존 0).
3. **DepositDecider 층-파라미터화** — 현재 단일 `operationalFloor` 파라미터를 임의 목표 층 F로 일반화한다.
   게이트(`TgtFloor==0 && (CurFloor!=F || Ready==0)`→F 기입 / FULL·PAUSED·OFFLINE 미기입 / 진행중(≠0)
   핑퐁 차단)와 "WCS는 TgtFloor 클리어 안 함"(절대규칙 #3)은 **불변** — 쓰는 값만 상수→F. 순수 함수 유지.
4. **소터별 pending-floor FIFO 큐** — destination.id 키로 소터마다 FIFO 큐 1개(스테이트풀 컴포넌트).
   동시성 안전(다중 AGV IF-05 동시 enqueue · 관측 루프 dequeue).
5. **IF-05 enqueue** — 소터 목적지 OK 피스를 그 소터 큐에 **층 F로 IF-05 순서대로** enqueue. FULL/PAUSED/
   OFFLINE이면 enqueue 안 함. TgtFloor를 IF-05 순간에 쓰지 않음(과거 IF-09 트리거·상수 2 폐지).
6. **TgtFloor==0 관측 기반 순서 기입** — 각 소터 스냅샷의 `TgtFloor==0`을 관측하면 큐 머리 F를
   DepositDecider 게이트로 **소터별 단일 쓰기 큐(SetTgtFloor)** 경유 기입(절대규칙 #1 — 핸들러 직접
   Modbus 호출 금지). 머리 피스 pop 시점(SPEC §2-C "미확정")은 Generator가 결정하되 근거 기록. 큐 비면
   미기입·현 CurFloor idle.
7. **IF-09 정렬 트리거 제거** — `RcsController.ArrivalReport`의 `AlignSorterToOperationalFloor`(단일층
   정렬) 제거. IF-09는 **도착 기록만**(정렬은 IF-05 enqueue + 큐 구동 기입으로 이동). ⚠ 미제거 시
   이중 기입(경합) 발생 — A의 핵심 전환.
8. **ready 층별 판정** — `DestinationStatusService.ComputeSorter`를 단일 `OperationalFloor`가 아니라
   소터 **CurFloor 기준**으로 산출(현재 정렬된 층에서만 ready). dual-host 발신은 B — 여기선 산출만.
9. **테스트** — DepositDeciderTests를 F 파라미터로 갱신(층 다름·핑퐁 차단·FULL/PAUSED/OFFLINE 미기입
   전 케이스) + 층맵 파생 단위 테스트 + 큐 FIFO·순서 기입 테스트 + Sim3ds 상대 1↔2층 복귀 통합 테스트.

### Evaluation Criteria (Backend/API 4기준 — ★★★ 최우선)
1. **API Design Quality ★★★** — 층맵·큐·게이트 계약이 SPEC §2-A/§2-C·§3(IF-05)와 정합. Wcs.Core 순수성
   보존(의존성 0 — I/O·DI·EF 유입 0). DepositDecider 시그니처 변경이 명확·일관.
2. **Architecture Originality ★★★** — "큐=상태 / Core=순수 판정 / 관측 루프=트리거"의 관심사 분리.
   절대규칙 #1(소터별 단일 쓰기 큐)·#2(TgtFloor 게이트)·#3(WCS 클리어 금지)·#8(판정=순수 함수) 위반 0.
3. **Craft ★★** — 동시성(큐 enqueue/dequeue·관측 루프 경합), 미매핑 inductionNo fail-loud, 핑퐁 차단
   (컨슈머 fresh-read 재확인 보존), 무변화 폴에서 write 폭주 0, 예외 삼킴 0(OFFLINE 전이 명시).
4. **Functionality ★★** — 인덕션 F 파생→enqueue→TgtFloor==0 관측→F 기입→CurFloor=F 복귀 폐루프가
   Sim3ds 상대로 **1층·2층 둘 다** 동작. FIFO 순서(F=1,2,1)대로 한 번에 하나씩 복귀.

### Completion Conditions (Evaluator 통과 최소 조건)
- `dotnet build backend/Wcs.sln` 성공 · `dotnet test backend/Wcs.sln` 전체 GREEN(신규 테스트 포함, ≥5회 반복 안정 — flake 없음).
- `dotnet test --filter Decider` GREEN. **Wcs.Core 의존성 0 유지**(csproj에 I/O·EF·DI 패키지 추가 0).
- TgtFloor는 **오직 소터별 단일 쓰기 큐(SetTgtFloor)**로만 기입 — API 핸들러/서비스의 직접 Modbus 호출 0.
- **WCS가 TgtFloor를 클리어하는 코드 0**(절대규칙 #3). FULL/PAUSED/OFFLINE 소터엔 TgtFloor 미기입.
  TgtFloor≠0(진행 중)엔 미기입(핑퐁 차단).
- IF-09 경로에 정렬(TgtFloor 쓰기) 트리거 0 — 도착 기록만.
- 미매핑 inductionNo는 조용히 통과하지 않음(fail-loud — 정책은 질문 확정 후 반영).

### Parallel Modules
N/A (단일 응집 모듈 — 큐→Decider→쓰기 트리거→IF-05/09 결선이 상호의존이라 파티션 부적합).

### Evaluation Dimensions
functional only (제어 로직 스프린트 — 동시성·절대규칙 준수는 단일 Evaluator의 Craft/Architecture 기준으로 커버. 별도 security/perf 풀로 패딩하지 않음).

### Detected Project Type: Full-stack
(repo 신호: `frontend/`(Vite/React) 브라우저 진입점 + `Wcs.Api` 서버 컨트롤러가 같은 repo에 공존 → 하네스
정의상 Full-stack. **단 이 스프린트(및 S-TWO-FLOOR-CONTROL 전 A~D)는 frontend/ 파일을 0개 변경하는
백엔드 전용 수직**이다. 따라서 아래 Full-stack 슬롯에서 Web/UI는 근거와 함께 N/A, Backend/API는 전수
열거, 크로스레이어 E2E는 RCS(HTTP)↔WCS↔Modbus PLC(Sim3ds)↔DB 데이터플로로 채운다.)

### Verification Scenarios (Full-stack — 필수)

=== Full-stack ===

- **Applicable Web/UI scenarios (프론트 표면):**
  **N/A — 근거**: 이 스프린트는 `frontend/` 파일을 0개 변경한다(브라우저 표면 변화 없음). Web/UI E2E
  해당 없음. Evaluator는 브라우저 검증 대신 Backend/API 자동 테스트 + Sim3ds 크로스레이어 통합으로 검증
  (하네스 "인프라 미실행≠스킵" 준수 — 서버·Sim·in-memory DB를 실제 기동해 검증).

- **Applicable Backend/API scenarios (백엔드 표면 — 전수):**
  - **엔드포인트(method+path):**
    - `POST /api/v1/destination-query` (IF-05) — 소터 OK 시 inductionNo→F 파생 + 소터 큐 enqueue.
    - `POST /api/v1/arrival-report` (IF-09) — 정렬 트리거 **제거**(도착 기록만).
    - (비-HTTP, 검증 대상) 소터 **TgtFloor 기입** = 큐 관측 트리거(SetTgtFloor via 단일 쓰기 큐).
  - **Happy path(입력→출력 형상):**
    - IF-05 `inductionNo=1`(→F=1) 소터 목적지 → `200 {result:"OK", chuteNo}` + 그 소터 큐에 F=1 enqueue + operation_log(IF05_REQ/RES).
    - IF-05 `inductionNo=3`(→F=2) → `200 {result:"OK", chuteNo}` + 큐에 F=2 enqueue.
    - 소터 `TgtFloor==0` 관측 → 큐 머리 F 기입(SetTgtFloor) → Sim3ds가 `CurFloor=F`로 이동·복귀.
    - IF-09 → `200 {result:"OK"}` + 도착만 기록(TgtFloor 쓰기 로그·SET_TGTFLOOR 발화 0).
  - **Error/edge cases(적용되는 것만 — 패딩 금지):**
    - 미매핑 inductionNo → fail-loud(정책 확정 후: NG/폴백/거부 중 하나 — 질문 참조). 조용한 통과 0.
    - `TgtFloor≠0`(진행 중) 관측 → 미기입(핑퐁 차단, 컨슈머 fresh-read 재확인).
    - FULL/PAUSED/OFFLINE 소터 → enqueue·기입 안 함.
    - 다층 FIFO 순서: F=1,F=2,F=1 순 enqueue → CurFloor가 1→2→1 순으로 **한 번에 하나씩** 복귀.
    - 큐 빔 → 미기입·현 CurFloor idle(불필요 폴 write 0).
    - IF-05 검증 실패(pId 범위·barcode·qty 상한) → `400`(기존 위생 회귀 보존).

- **E2E 크로스레이어 시나리오(≥1, 2+ 레이어 횡단):**
  RCS가 IF-05(HTTP)로 소터 목적지 조회 → WCS가 `inductionNo→InductionFloorMap`으로 F 파생(Api) →
  소터별 pending-floor 큐 enqueue(상태) → 관측 루프가 `TgtFloor==0`을 보고 DepositDecider(Core 순수)
  게이트 통과 시 소터별 단일 쓰기 큐로 SetTgtFloor(PlcGateway) → **Sim3ds(Modbus 슬레이브)**가 이동해
  `CurFloor=F` 기입 → WCS 폴링 스냅샷 반영 → operation_log/piece_event(DB) 기록. **HTTP API ↔ 인메모리
  큐 ↔ Modbus 게이트웨이 ↔ Sim3ds ↔ 영속화**를 횡단하는 폐루프를 xUnit 통합 테스트로 재현(수동 curl 금지).

> Planner self-check — Detected project type: Full-stack. Required scenario slots: 3 (Web/UI[N/A·근거], Backend/API, E2E cross-layer). All slots filled: yes.

---

## 질문 (사용자 확인 필요 — 추측 금지, SPEC §2-C·§7 미확정)

1. **미매핑 inductionNo 정책** — `InductionFloorMap`에 없는 inductionNo가 IF-05로 오면? (a) NG 응답+기록,
   (b) 기본층 폴백(예 1층), (c) fail-loud 거부. SPEC 미확정 — A 구현 전 확정 필요.
2. **큐 머리 pop 시점** — `TgtFloor==0` 관측 즉시 pop vs **분류 시작(Ready 1→0) 관측 시** pop. SPEC §2-C가
   "미확정"으로 명시. 폐루프 정합(재기입 왕복 방지)에 직접 영향.
3. **CurFloor 피드백으로 폐루프 확인** — 소터가 F 도착(`CurFloor=F`)한 뒤 큐 머리를 pop하는가, 아니면
   PLC의 TgtFloor 클리어(분류 시작) 시 pop하는가? 큐 소비와 물리 도착/분류 사이 정합 규칙.
4. **두 층이 한 소터를 물리 공유하는가** — SPEC은 "소터 한 대가 정렬로 두 층 겸용, 어느 순간 최대 한 층만
   ready"로 명시. 이 전제 최종 확인(1층·2층이 물리적으로 별개 소터라면 큐/라우팅 설계가 달라짐).
5. **같은 층 연속 enqueue dedupe 여부** — 같은 소터에 F=1 피스가 연속으로 들어오면 큐에 F=1을 매 피스 1개씩
   쌓는가(그래도 기입값은 같음), 아니면 연속 동일층은 병합하는가? SPEC "IF-05 순서대로 넣는다"는 순서만 규정.

---

## ✅ 확정 결정 (사용자 게이트 — 2026-07-22)

계약·분할 **승인**(A부터 구현, B·C·D는 A 완료·승인 후 순차 재계약). 미확정 5건 확정:

1. **미매핑 inductionNo** → **NG 응답 + 경고 로그**(Fail Loud). `InductionFloorMap`에 없는 inductionNo는
   IF-05에서 거부(NG) 응답 + operation_log 명시 기록, **소터로 보내지 않음**. 조용한 통과·기본층 폴백 금지.
2. **큐 머리 pop 시점** → **CurFloor==F 도착 확인 시**(폐루프). WCS가 TgtFloor=F 기입 후 소터가 실제 F
   도착(`CurFloor==F`)을 관측하면 그때 큐 머리 pop. 물리 도착 미확인 상태로 큐 전진 금지.
   (엣지: 큐 머리 F가 **이미 현재 CurFloor와 같으면** 이동·기입 없이 즉시 소비(pop) — 스톨 방지.)
3. **CurFloor 피드백 폐루프**(Q3) → 위 2와 동일: **CurFloor==F 도착으로 확인**(PLC의 TgtFloor 클리어
   시점이 아님).
4. **두 층 물리 공유**(Q4) → **확정: 소터 1대가 정렬로 양층 겸용**, 어느 순간 최대 한 층만 ready(앞서 확인).
5. **연속 동일층 enqueue** → **피스마다 1건씩 stack**(dedupe 안 함). FIFO 순서 유지. 기입값이 같으면
   게이트(`CurFloor!=F`)가 재기입을 자동 차단하므로 무변화 폴 write 폭주 없음.

> Generator는 이 결정을 코드에 반영하고, SPEC.md §2-C·§7의 해당 "미확정" 항목도 확정 내용으로 갱신할 것.

## 나머지 서브 스프린트 로드맵 (A 승인·완료 후 순차 재계약)

- **B — IF-08 층별 호스트 라우팅 + 부트스트랩 dual-host + IF-09 DTO/문서 층 표기 정리 [Api/outbound]**
  (항목 #5·#6): 층별 push 호스트(1F `http://192.168.0.151:3000` · 2F `http://192.168.0.152:3000`) 설정화,
  전이 push를 목적지 층 호스트로 라우팅, **소터는 두 층 호스트 모두**(CurFloor 호스트=3 / 다른 층=2),
  기동 부트스트랩 dual-host(소터=현재층만 open·다른층 close), IF-09 층 필드/문서 정리. `ChuteStatePush:BaseUrl`
  단일 호스트 → 층별 호스트 맵으로 확장(DORMANT 성질 보존).
- **C — R-clear@Ready==1 + 3시각 + sorter_command 마이그레이션 + 재시작 클리어 [Data/Gateway]**
  (항목 #7·#8): 핸드셰이크 ClearR를 **Ready==1(복귀 완료) 시점**으로 지연(즉시 클리어 폐지), `depositedAt`
  (IF-10)·`tiltedAt`(R_Flag==1)·`returnedAt`(Ready 0→1) 3시각 기록, sorter_command 컬럼 확장 →
  EF 마이그레이션(SqlServer+Sqlite 양 provider 분리 프로젝트), 재시작 시 C/R/TgtFloor만 클리어(D4는 RMW로
  Ready 비트 보존)를 IF-08 부트스트랩보다 **먼저** 실행. (RFlagAt↔tiltedAt 컬럼 중복 여부는 이때 판단.)
- **D — AGV 파킹존 동작 [semantics/outbound]** (항목 #9): 목적지 수용 불가 시 파킹존 우회, IF-08 open push가
  IF-09 도착에 **선행**(열림 push=출발 신호)임을 WCS 동작·문서로 보장. RCS가 파킹 자체를 소유하므로 WCS
  코드 표면은 최소(push 순서·타이밍 계약 검증 중심).

> Planner self-check — Detected project type: Full-stack. Required scenario slots: 3 (Web/UI[N/A·근거], Backend/API, E2E cross-layer). All slots filled: yes.
