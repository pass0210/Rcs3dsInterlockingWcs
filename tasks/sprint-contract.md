# Sprint Contract — S-F3a (B2C 운영 제어 · **백엔드 Ops API + 런타임 전이**, Sim 검증)

> Planner Subagent · 2026-07-09
> 설계 원본(고정): `docs/FRONTEND.md` §3(API 표면)·§3.3(clear/pause/resume + PAUSED/RESUMED 전이 신규 백엔드)·§3.4(operation_log 경량 vs 명시)·§4.5(인증=없음·사내망 신뢰, 조작 다이얼로그의 작업자 이름 입력→`destination_event.operator_id`)·§6(F3 범위·Done)·§8(설계 Q 전건 해소 2026-07-03). **모든 핵심 결정은 그 문서에 LOCK — 재론 금지.**
> 감사 근거: `tasks/audit-20260701-full.md`(A-8 = `OnCleared` production 호출자 0), `docs/SPEC.md` §2(판정표)·§7-B(슈트 비움/PAUSED = 관리 API 확정 2026-07-03).
>
> **이 프로젝트 최고위험 스프린트.** 실 PLC 워드 쓰기 경로(보호구역)와 런타임 상태 전이를 신설한다. 아래 **SAFETY BOUNDARY**가 이 계약의 최우선 절(節)이며, 위반은 즉시 FAIL이다.
>
> **분할 결정(권고 채택 전제):** 원래 F3(=OpsController + PAUSED/RESUMED 전이 + OnCleared 결선 + 워드쓰기 enqueue + Ops UI + 작업자 감사)를 **F3a(백엔드 Ops API·전이 — Sim3ds 전수검증)** → **F3b(프론트 Ops/편집 UI)**로 분할한다. 검증된 a/b 패턴(F1a/F1b·B2B 1/2/3)과 동형. 위험한 PLC-write 백엔드를 UI 착수 전에 완전 격리·Sim 검증한다. **이 계약은 F3a만 스코프한다.** F3b는 하단 "Remaining phases"에 명시.

---

## ⚠ Questions for user (착수 전 확인 — 전부 권장 기본값 있음. 답 없으면 기본값으로 진행)

설계 Q(§8)는 전건 LOCK이므로 재론하지 않는다. 아래는 **이 스프린트 경계에서만 발생하는 실제 갈림길** 3건이다.

- **Q-a — F3를 F3a(백엔드)/F3b(프론트)로 분할?** **권장 = YES(분할).** 근거: PLC 워드 쓰기 = 보호구역이라 UI 얹기 전 백엔드를 Sim3ds로 전수 검증해 격리해야 한다. 프로젝트 검증된 a/b 패턴과 동형. 이 계약은 F3a 스코프.
- **Q-b — operation_log 운영자 조작 기록: 경량(재사용) vs `OPS` 카테고리 신설?** **권장 = 경량(`docs/FRONTEND.md` §3.4 기본).** **결정적 근거(코드 확인):** clear/pause/resume은 이미 존재하는 `STATE` 훅 + `destination_event`(OperatorId 컬럼·`CLEARED/PAUSED/RESUMED` EventType·`DestStatus.PAUSED` **전부 이미 존재**)로 기록 → **마이그레이션 0건**. `OPS` 카테고리는 `operation_log.category` CHECK 제약 변경 = **SqlServer+Sqlite 2종 마이그레이션 + ModelSnapshot** 비용 발생(운영자 액션 단일 필터라는 편의 하나 위해). 정규 감사 단일 진실은 `destination_event`이므로 경량으로 충분. 후속에 필터 요구 확인되면 `OPS` 신설.
- **Q-c — "안전 워드 쓰기" 3종(SetTgtFloor·ClearR·CellAssign)을 F3a API에 포함? 아니면 clear/pause/resume + OnCleared만 F3a, 워드쓰기는 F3b로?** **권장 = 워드쓰기 API 3종도 F3a에 포함(Sim 전수 검증)·UI는 F3b.** 근거: 워드 쓰기가 바로 최고위험 경로 → 반드시 백엔드에서 Sim 검증하고, 프론트는 검증된 API를 소비만. API를 F3b로 미루면 UI와 위험 경로가 동시 착수돼 격리 목적이 무너진다.

> 위 3건에 대한 override가 없으면 **YES(분할) / 경량 / 워드쓰기 API 포함**으로 확정 진행한다.

---

## ★ SAFETY BOUNDARY (이 계약의 최우선 절 — 위반 = 즉시 FAIL)

### S-1. 워드 쓰기는 **안전 3종만**, 임의 레지스터/D4 비트 편집 도입 금지 (Q2 LOCK)
- 허용 워드 쓰기 = `PlcWrite.SetTgtFloor(int)` · `PlcWrite.ClearR` · `PlcWrite.CellAssign(int,int)` **오직 이 3개**. 이 유니온과 컨슈머 `ProcessWriteAsync`의 3 case는 **이미 존재**(`backend/src/Wcs.PlcGateway/PlcGateway.cs:30-36, 486-527`).
- **`WriteRawRegister`/`SetD4Bit` 등 신규 PlcWrite 레코드 추가 금지.** 이를 추가하면 `Wcs.PlcGateway` 코어(보호구역) 변경이 된다 — Q2가 "도입하지 않음(PlcGateway 코어 무변경)"으로 LOCK.

### S-2. 모든 PLC 쓰기는 **기존 소터별 단일 쓰기 큐 경유** (절대규칙 #1)
- 컨트롤러/서비스가 Modbus·`IModbusMaster`를 **직접 호출 금지.** Ops 워드 쓰기는 `ISorterGatewayRegistry.GetBundle(destId)` → `SorterBundleHandle.Enqueue*Async` → 소터별 `PlcWriteQueue` → 단일 컨슈머 경로로만 **enqueue**한다.
- **최소 영향 확인(코드):** `SorterBundleHandle`(위치 = `backend/src/Wcs.Api/Infrastructure/SorterGatewayRegistry.cs`, **namespace `Wcs.Api`**)에는 `EnqueueSetTgtFloorAsync`만 있고 ClearR/CellAssign enqueue 래퍼가 없다. F3a는 여기에 `EnqueueClearRAsync`·`EnqueueCellAssignAsync` **박막 래퍼 2개**(`_polling.EnqueueAsync(new PlcWrite.ClearR()/CellAssign(...))`)를 추가한다 — **이것은 Wcs.Api 변경이지 `Wcs.PlcGateway` 코어 변경이 아니다**(레코드·컨슈머 case는 이미 존재). PlcGateway 코어는 **한 줄도 바꾸지 않는 것이 목표.**

### S-3. TgtFloor 게이트·비클리어 불변식 보존 (절대규칙 #2·#3)
- SetTgtFloor는 컨슈머의 `TgtFloor==0` 재확인 가드(`PlcGateway.cs:486-496`, ≠0이면 "핑퐁 차단" 스킵+WARN)를 **그대로 탄다**. Ops API가 이 가드를 우회/삭제 금지.
- WCS는 TgtFloor를 클리어하지 않는다(#3). `floor==0` 수동 리셋(절대규칙 #3 예외 후보, SPEC §7-B)은 **F3a 범위에서 API가 값 검증만 하되, 예외 조작으로 명시 로깅**하고 그 정책 노출(강경고 UI)은 F3b로 미룬다 — 기본은 `floor>=1` 유효값만 받는다.

### S-4. **검증·실행은 Sim 전용 — 실 3DS PLC(COM1/RTU)에 절대 접근 금지 (하드 제약)**
- Generator/Evaluator는 백엔드를 **`Transport=rtu`·`COM1`으로 절대 기동하지 않는다.** 오직 **Sim3ds(FluentModbus TCP, 기본 :1502)** 상대역으로만 워드 쓰기를 검증한다.
- **실 PLC로 워드 1개라도 쓰면 즉시 FAIL.** 검증은 `dotnet test`(WebApplicationFactory + Sim3ds TCP) 또는 라이브 `dotnet run --project backend/src/Wcs.Sim3ds` + `Wcs.Api`(TCP) 조합으로만.
- **현장 DB 오염 가드:** 검증은 스크래치/시드 DB로만. `Environment=Production`(launchSettings 부재로 실제 기본) + `Database:SeedOnStartup=false`를 강제하고, 임의 시드가 현장 토폴로지 DB에 닿지 않게 한다(교훈: dev 시드 chuteNo=30 ↔ appsettings `Sorters[ChuteNo=1]` 미스매치 → fail-loud/현장 오염). 통합 테스트의 in-memory/파일 SQLite 또는 별도 스크래치 SQL Server만 사용.

### S-5. 보호구역 call-out (CLAUDE.md Protected zones)
- 이 스프린트는 설계상 **보호구역(PLC 쓰기 경로)**을 건드린다. 따라서 (a) 최대한 additive(신규 OpsController + 신규 전이 서비스 + 기존 큐 API로 enqueue), (b) `Wcs.PlcGateway` 코어(특히 `HandshakeOrchestrator`·`PlcPollingService`·`ProcessWriteAsync`) **무변경이 목표**. 만약 코어 변경이 불가피하다고 판단되면 **Generator는 구현 전 중단·Evaluator 경유로 사용자에게 보고**하고, 승인 없이는 진행하지 않는다(추가 검증·회귀 입증 요구).

---

## Goal

B2C "운영 제어"의 **백엔드 표면(F3a)**을 신설한다: `/api/ops/*` OpsController(clear/pause/resume + 안전 워드 쓰기 3종) + **PAUSED/RESUMED 런타임 전이 백엔드(신규)** + **`OnCleared` production 결선(감사 A-8 해소)** + 작업자 이름 → `destination_event.operator_id` 감사. 모든 PLC 쓰기는 단일 큐 경유·Sim3ds로 전수 검증하고, 절대규칙 #1/#2/#3/#7/#8과 위 SAFETY BOUNDARY를 전부 보존한다. **프론트 UI는 F3b(별도 계약).**

---

## Implementation Scope (Generator가 할 일 — F3a 백엔드 한정)

### 1. OpsController 신설 (`backend/src/Wcs.Api/Controllers/OpsController.cs`, `[Route("api/ops")]`)
현재 부재 확인됨(컨트롤러는 `MonitoringController`·`RcsController` 뿐). RcsController(`/api/v1/*`)·MonitoringController(`/api/monitor/*`)와 완전 분리된 신규 라우트. 인증은 없음(§4.5 LOCK) — 단 **모든 조작 body에 작업자 이름(`operatorName`, 자유 입력) 필수**로 받아 감사에 귀속.

엔드포인트(전부 POST, JSON body):

| # | 엔드포인트 | 결선 | 감사 |
|---|---|---|---|
| O1 | `POST /api/ops/chutes/{destId}/clear` `{operatorName}` | `IChuteCapacityService.OnCleared(destId, operatorName)` 호출 — **A-8 해소(production 호출자 신설).** | `destination_event(CLEARED, operatorId)` + `STATE`/CLEARED |
| O2 | `POST /api/ops/destinations/{destId}/pause` `{operatorName}` | 신규 전이: `destination.Status=PAUSED` + 슈트면 인메모리 `IsPaused=true`+`RaiseChuteStateChanged`, 소터면 DB만(소터는 `DestinationStatusService.ComputeSorter`가 DB Status 직접 read). | `destination_event(PAUSED, operatorId)` + `STATE`/PAUSED |
| O3 | `POST /api/ops/destinations/{destId}/resume` `{operatorName}` | O2 역전이: `Status=NORMAL` + 인메모리 해제. | `destination_event(RESUMED, operatorId)` |
| O4 | `POST /api/ops/sorters/{destId}/tgtfloor` `{floor, operatorName}` | `bundle.EnqueueSetTgtFloorAsync(floor)`(기존). 컨슈머 `TgtFloor==0` 가드 그대로. | `PLC_WRITE`/SET_TGTFLOOR 자동(`OnWrite`) + Ops 발원 `STATE` 1행(operatorId, detail) |
| O5 | `POST /api/ops/sorters/{destId}/clear-r` `{operatorName}` | **신규** `bundle.EnqueueClearRAsync()` → `PlcWrite.ClearR`. 진단용. | `PLC_WRITE`/CLEAR_R 자동 + Ops `STATE` 1행 |
| O6 | `POST /api/ops/sorters/{destId}/cell-assign` `{cellNo, seq, operatorName}` | **신규** `bundle.EnqueueCellAssignAsync(cellNo, seq)` → `PlcWrite.CellAssign`. 고위험 진단용. | `PLC_WRITE`/CELL_ASSIGN 자동 + Ops `STATE` 1행 |

- **라우팅:** 워드 쓰기(O4~O6)는 `ISorterGatewayRegistry.GetBundle(destId)`로 소터 번들 조회. `null`(미등록/비-SORTER_3D) → 404. clear(O1)는 CHUTE 대상, pause/resume(O2/O3)은 CHUTE·SORTER_3D 공용.
- **DI:** OpsController는 `IChuteCapacityService`·`ISorterGatewayRegistry`·신규 전이 서비스·`WcsDbContext`(또는 리포)를 생성자/`[FromServices]` 주입. MonitoringController의 DI 패턴 준수.

### 2. `SorterBundleHandle` enqueue 래퍼 2개 추가 (Wcs.Api — 코어 무변경)
`EnqueueClearRAsync(ct)` · `EnqueueCellAssignAsync(cellNo, seq, ct)` — 각각 `_polling.EnqueueAsync(new PlcWrite.ClearR()/CellAssign(cellNo,seq), ct)` 위임. `EnqueueSetTgtFloorAsync`와 동형(`SorterGatewayRegistry.cs:57-58`). **`Wcs.PlcGateway` 파일은 건드리지 않는다.**

### 3. PAUSED/RESUMED 런타임 전이 신규 백엔드
- 현재 `ChuteCapacityService.IsPaused`는 **기동 시 `InitializeFromDbAsync`에서만** 세팅되고 런타임 전이 메서드가 없다(`ChuteCapacityService.cs:185`). `OnCleared`(`:286`)는 있으나 `operatorId` 인자·기입이 없다.
- 신설: `IChuteCapacityService.OnPaused(destId, operatorId)` / `OnResumed(destId, operatorId)`(또는 신규 `IDestinationControlService`). **한 트랜잭션 단위**로 (a) `destination.Status` DB 전이, (b) `destination_event(PAUSED|RESUMED, OperatorId, DetailJson=old/new)` append, (c) 슈트면 인메모리 `IsPaused` 반영 + `RaiseChuteStateChanged`. 소터는 인메모리 불요(DB read).
- `OnCleared` 시그니처에 `operatorId` 추가 → `destination_event(CLEARED)`에 `OperatorId` 기입(현재 미기입, `:303-308`). 기존 호출부(있다면)·테스트 동반 수정.
- **동시성·전이 원자성:** 전이는 원자적으로(락 순서·DB tx). `Destination.RowVersion`/`XminRowVersion` 동시성 토큰 존중. 예외는 삼키지 말고 상태 전이/로깅으로 명시(CLAUDE.md).

### 4. 감사 결선 (경량 — Q-b 권장)
- clear/pause/resume → `destination_event`(정규 감사, OperatorId 보유) + 기존 `STATE` 훅 재사용. 워드 쓰기 → 기존 `OnWrite`→`PLC_WRITE` 자동 기록 + Ops 발원 사실을 `STATE`(또는 `API`) 1행으로 operator 귀속(detail JSON에 `operatorName`).
- **마이그레이션: 원칙적으로 0건.** `destination_event.OperatorId`·`DestinationEventType.{CLEARED,PAUSED,RESUMED}`·`DestStatus.PAUSED` 전부 이미 존재(확인: `Entities.cs:65-67,102,403`). **만약** Generator가 스키마 변경이 필요하다고 판단하면(예: Q-b에서 사용자가 `OPS` 카테고리 채택) → **SqlServer+Sqlite 2종 마이그레이션 + ModelSnapshot 동시 갱신**(교훈: 운영=SQL Server, SQLite만 검증 시 1785/207 은폐). 경량 기본에서는 스키마 무변경을 유지.

### 5. Program.cs 배선
- OpsController는 `MapControllers`로 자동 등록. 신규 전이 서비스가 신설되면 DI 등록. 정적 서빙/인증 미들웨어는 F3a 무관(F1에서 정적 서빙 추가됨, 인증은 §4.5 LOCK으로 미도입).
- 신규 타이밍/설정이 필요하면 appsettings 외부화(#7) — F3a는 폴링을 추가하지 않으므로 새 타이밍 키는 기대되지 않음.

### 스코프 OUT (F3a에 흡수 금지)
- **프론트 UI 전부**(Ops/편집 페이지·확인 다이얼로그·작업자 이름 입력 위젯) → **F3b.**
- 인증/로그인 구현 → §4.5 LOCK(미도입).
- 임의 레지스터/D4 비트 편집 → Q2 LOCK(미도입).
- **묶음 D 핸드셰이크 견고화**(R_Flag 레벨읽기·재시작 reconciliation 등) → **별도 스프린트. 절대 흡수 금지.**
- C-26 바인딩/방화벽 명문화는 문서 작업 — F3a 코드 범위 아님(운영 README는 후속). 단 S-4의 never-COM1·현장 DB 가드는 검증 규율로 준수.

---

## Absolute Rules Compliance (각 규칙 → F3a 준수 방식)

- **#1 (단일 쓰기 큐):** Ops 워드 쓰기(O4~O6)는 `GetBundle`→`Enqueue*Async`→소터별 `PlcWriteQueue`→단일 컨슈머로만 enqueue. 컨트롤러/서비스의 직접 Modbus 호출 0. clear/pause/resume은 PLC 쓰기가 아니라 DB+인메모리 서비스 경유.
- **#2 (TgtFloor 게이트):** O4는 컨슈머 `TgtFloor==0 && (…)` 재확인 가드를 그대로 탐. ≠0이면 컨슈머가 핑퐁 차단 스킵+WARN(기존 동작). API가 가드 우회 금지.
- **#3 (WCS 비클리어):** Ops는 TgtFloor를 클리어하지 않음. `floor==0` 수동 리셋은 F3a 기본 미노출(유효 `floor>=1`); 노출은 F3b + 강경고(§7-B 예외).
- **#7 (하드코딩 타이밍 금지):** 신규 타이밍 키 없음(폴링 미추가). 필요 시 appsettings. `dotnet test` 반복 검증에 고정 sleep 금지.
- **#8 (순수 판정 함수):** 신규 순수 판정 없음. 기존 `DepositDecider`·`DestinationStatusService.Compute*`·`ChuteCapacityService.GetHold`(순수/인메모리) 재사용. Ops는 이 판정을 소비만.
- **Protected zone:** 위 S-5. `Wcs.PlcGateway` 코어 무변경 목표, 불가피 시 사전 보고.

---

## Evaluation Criteria (Evaluator 판정 기준 + 가중치)

- **[30%] 안전 경계 준수(하드):** (i) 워드 쓰기 3종만·임의 레지스터 미도입, (ii) 모든 PLC 쓰기가 단일 큐 enqueue(컨트롤러 직접 Modbus 0 — 코드 검사), (iii) `Wcs.PlcGateway` 코어 무변경(diff로 확인; 변경 시 사전 보고 여부), (iv) 검증이 **Sim3ds TCP 전용**·실 COM1/RTU 접근 0·현장 DB 오염 0. **하나라도 위반 시 전체 FAIL(가중치 무관).**
- **[25%] Sim 워드 쓰기 실증:** O4 `SetTgtFloor`→(TgtFloor==0 전제) Sim D6 반영 관찰 + O4 핑퐁 차단(TgtFloor≠0 시 스킵) 관찰, O5 `ClearR`→D2·D3·R_Flag=0 반영, O6 `CellAssign`→D0·D1·C_Flag=1 반영. **전부 단일 큐 컨슈머 경유**임을 로그/스냅샷으로 확인. Evaluator 독립 재실행 fresh 증거.
- **[20%] 런타임 전이·A-8 실증:** pause → 해당 목적지 IF-05/dispatch 게이트가 `PAUSED` 반환(슈트=`GetHold`→Paused, 소터=DB Status), resume → 복원. clear → FULL 슈트가 실제 복구(A-8 "OnCleared 호출자 0" 해소 실증). `destination_event`에 `operator_id`(작업자 이름) 기록 확인.
- **[15%] 회귀 0:** 기존 스위트 전건 GREEN(baseline — 아래 Completion #1; 정확 카운트는 착수 시 clean run으로 확정) + 신규 OpsController/전이 통합 테스트 GREEN. 실패/스킵 은폐 없음. flake는 s9 교훈대로 ≥반복 확인(1회 GREEN 신뢰 금지).
- **[10%] 감사·계약 정합:** 경량 기록(STATE/PLC_WRITE 재사용 + destination_event 정규 감사) 정확. 마이그레이션 필요 시 SqlServer+Sqlite 2종+Snapshot 동반. 워드쓰기 자동 감사(`OnWrite`) 결선 유지.

---

## Completion Conditions (Evaluator PASS 최소 조건 — 전부 충족)

1. **회귀 0:** `dotnet test backend/Wcs.sln` 전건 GREEN. 착수 시 Generator가 clean run으로 baseline green 카운트를 기록(현재 `[Fact]/[Theory]` 272 메서드; theory 확장 실행 카운트 ≈289 — 정확값은 착수 clean run으로 확정, 단일 run 신뢰 금지). 신규 테스트 추가 후에도 전건 GREEN.
2. **OpsController 6 엔드포인트 존재·동작**(O1~O6), 라우트 `/api/ops/*`가 `/api/v1`·`/api/monitor`와 충돌 없음.
3. **Sim3ds 워드 쓰기 검증(단일 큐 경유):** O4/O5/O6 각각 Sim에서 해당 레지스터 반영 단언 + O4 핑퐁 차단 케이스. 실 COM1/RTU 미사용 증거(설정·로그).
4. **런타임 전이:** pause→게이트 PAUSED, resume→복원, clear→FULL 슈트 복구를 통합 테스트로 실증(A-8). `destination_event`에 CLEARED/PAUSED/RESUMED + operator_id 기입 확인.
5. **`Wcs.PlcGateway` 코어 무변경**(diff 확인). 변경했다면 S-5대로 사전 보고 + 회귀 입증(핸드셰이크 시나리오 유지).
6. **경량 감사 + 마이그레이션 정합:** 스키마 변경 0(경량) 또는 변경 시 2 provider + Snapshot 동반. 워드쓰기 자동 `PLC_WRITE` 감사 유지.
7. **위생:** 빌드 클린, 고아 `Wcs.Sim3ds.exe` 없음(교훈: MSB3021/파일잠금), 스코프 밖 파일(프론트·묶음 D·PlcGateway 코어) 미변경.
8. **프론트 미변경:** F3a는 `frontend/` 무접촉(UI는 F3b).

---

## Remaining phases (이 계약 이후)

- **F3b — B2C 운영 제어 프론트 UI (별도 Sprint Contract):** `frontend/src/components/Layout.tsx` b2c NAV_SET의 비활성 "운영 제어" 항목 활성화(현 `enabled:false, phase:'F3'`, `Layout.tsx:40`). 페이지 ②(`SortersPage`/`WordPanel`)에 편집·제어 UI 추가 — SetTgtFloor(현재값·≠0 경고)·Clear-R·Cell-Assign 컨트롤 + Pause/Resume 토글 + 슈트 Clear 버튼, 각각 **확인 다이얼로그(`ui/dialog.tsx`)** + **작업자 이름 입력 필드**(→ Ops API `operatorName`) + 규칙 위반 경고. shadcn/ui + Tailwind(Q5). Playwright E2E(사용자 상호작용 재현). Detected type = Full-stack.
- 착수 조건: F3a PASS·병합 후. F3b는 F3a의 검증된 API를 소비만 하므로 위험 표면이 UI로 한정.

---

- **Parallel Modules:** N/A (single module). F3a는 OpsController·전이 서비스·bundle 래퍼가 서로 결선·공유 파일(Program.cs·SorterGatewayRegistry.cs·ChuteCapacityService.cs)을 함께 수정하므로 경계-클린 병렬 분할 불가. 순차 단일 Generator.
- **Evaluation Dimensions:** functional + safety(2차원). **safety = 위 [30%] 안전 경계 하드 게이트**(단일 큐 불변식·코어 무변경·never-COM1·현장 DB 가드)를 functional과 **병렬 전문 검토**로 둔다. 최고위험 스프린트라 안전 차원을 독립 Evaluator 관점으로 격리(둘 다 PASS해야 APPROVED). 성능 차원은 표면 없음(제외).

- **Detected Project Type:** **Backend/API**
  (프로젝트 전체는 Full-stack이나, **F3a의 변경 표면은 백엔드 전용**이다 — 신규 `OpsController` + 전이 서비스 + `SorterBundleHandle` 래퍼 + `ChuteCapacityService`. `frontend/` 파일 변경 0. 프론트 UI는 F3b로 분리. S-S5-FLAKE 선례와 동형: 프로젝트가 Full-stack이라도 스프린트 변경 표면 기준으로 타입을 잡는다.)

- **Verification Scenarios (Backend/API — 이 스프린트 변경 표면 기준):**

  - **이 스프린트가 건드리는 엔드포인트(method + path) — 6개:**
    - `POST /api/ops/chutes/{destId}/clear` (O1)
    - `POST /api/ops/destinations/{destId}/pause` (O2)
    - `POST /api/ops/destinations/{destId}/resume` (O3)
    - `POST /api/ops/sorters/{destId}/tgtfloor` (O4)
    - `POST /api/ops/sorters/{destId}/clear-r` (O5)
    - `POST /api/ops/sorters/{destId}/cell-assign` (O6)

  - **Happy path per endpoint (기대 입력 → 기대 출력 shape):**
    - **O1 clear:** FULL 상태 슈트 destId + `{operatorName:"홍길동"}` → 200. `OnCleared` 호출로 `chute_detail.last_cleared_at` 갱신·인메모리 카운터 0·`GetHold`가 이후 None. `destination_event(CLEARED, operator_id="홍길동")` 1행 append.
    - **O2 pause:** NORMAL destId + operatorName → 200. `destination.Status=PAUSED`. 슈트면 `GetHold`→Paused, 소터면 `DestinationStatusService.ComputeSorter`가 PAUSED. `destination_event(PAUSED, operator_id)` append.
    - **O3 resume:** PAUSED destId → 200. `Status=NORMAL`, 게이트 복원. `destination_event(RESUMED, operator_id)`.
    - **O4 tgtfloor:** 소터 destId + `{floor:3, operatorName}`, 현재 TgtFloor==0 → 200(enqueue accepted). 단일 큐 컨슈머가 Sim D6=3 반영. `PLC_WRITE`/SET_TGTFLOOR 자동 감사 + Ops 귀속 1행.
    - **O5 clear-r:** 소터 destId + operatorName → 200. 컨슈머가 Sim D2·D3=0·R_Flag=0 반영. `PLC_WRITE`/CLEAR_R.
    - **O6 cell-assign:** 소터 destId + `{cellNo, seq, operatorName}`, C_Flag==0 → 200. 컨슈머가 Sim D0=cellNo·D1=seq·C_Flag=1 반영. `PLC_WRITE`/CELL_ASSIGN.

  - **Relevant error/edge cases per endpoint (해당되는 것만 — 패딩 금지):**
    - **404:** O4~O6에 비-SORTER_3D 또는 미등록 destId(`GetBundle`→null) → 404. O1에 비-CHUTE destId → 404/422(정책 택1·명시).
    - **400/422:** O4 `floor<1`(또는 음수)·O6 `cellNo/seq` 무효·전 엔드포인트 `operatorName` 누락/공백 → 400(감사 귀속 불가 방지). `[ApiController]` 모델 바인딩 400 정합.
    - **핑퐁 차단(정상 스킵):** O4에 TgtFloor≠0(진행 중)로 요청 → API는 enqueue 수락하나 컨슈머가 스킵+WARN. **API가 "성공적으로 썼다"고 거짓 응답하지 않을 것**(수락/스킵 가능성을 응답·후속 상태로 정직히 반영, 절대규칙 #2 위반 방지).
    - **멱등/전이:** 이미 PAUSED에 O2 재요청·이미 NORMAL에 O3 → 결정적(멱등 또는 명시 no-op, event 중복 방지 정책 명시). 동시 pause/resume 시 RowVersion 동시성 처리.
    - **A-8 실증(핵심 edge):** work_full_qty 도달로 FULL이 된 슈트가 O1 clear로 실제 복구되어 이후 IF-05가 다시 OK 받는 전 과정(현재 production 호출자 0으로 비가역이던 갭이 닫힘).
    - **회귀 보존:** 기존 IF-05/09/10·핸드셰이크·소터 push 경로 무변경(전건 GREEN).

  - **Web/UI 시나리오:** **N/A** — F3a는 `frontend/` 표면을 전혀 건드리지 않는다(UI는 F3b 별도 계약). 백엔드 API·전이·Sim 통합 검증 전용.

> Planner self-check — Detected project type: Backend/API. Required scenario slots: 3 (endpoints touched, happy path per endpoint, error/edge cases per endpoint). All slots filled: yes.
