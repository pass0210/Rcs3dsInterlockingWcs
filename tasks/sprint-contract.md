[Sprint Contract] — S-CLEANUP-FIELD

Branch: fix/cleanup-field (base: develop @ PR #34 병합 완료)
작성자: Planner Subagent · 2026-07-07
성격: 수정 스프린트(누적 코드리뷰 Minor + 2026-07-01 감사 잔여 정리). 신규 기능 0.
우선순위 원칙: **목요일(7/9) 현장 테스트 전 — 현장 관측성·운영 가치 항목이 최우선.**
후속 맥락: 이 스프린트 직후 B2B 이식 3단계가 이어짐 → **과확장 금지**(최소 침습, 재사용 가능한 최소 형태).

────────────────────────────────────────────────────────────────────────
## Goal

직전 3개 스프린트(S-HANDSHAKE-RESIDUE / S-FIELD-20CELLS / S-SIM3DS-RTU)의 코드리뷰 Minor와
2026-07-01 전체 감사의 잔여 quick-fix(묶음 A)·문서 표류(묶음 E)를 한 번에 정리한다.
행동 의미 변경은 최소화하고(핸드셰이크·판정 의미 변경 0), **관측성·입력 위생·문서 현행화**에 집중해
현장 테스트 중 진단·운영이 실제로 가능한 상태로 만든다.

────────────────────────────────────────────────────────────────────────
## Detected Project Type: Full-stack

근거(레포 신호 — 사용자 표현·기억 아님):
- 브라우저 진입 트리 존재: `frontend/src/pages/sections/*.tsx`(클라이언트 렌더 컴포넌트).
- 서버 라우트/컨트롤러 존재: `backend/src/Wcs.Api/Controllers/RcsController.cs` + 서버 진입점 `Program.cs`.
- 둘이 같은 레포에 공존 → Full-stack.

**단, 이번 스프린트의 변경 표면은 전부 서버측(backend/Wcs.Api·PlcGateway·Sim3ds)·문서·테스트다.
프론트엔드는 0 변경(B-5 OUT).** 따라서 Full-stack 슬롯 중 Web/UI(브라우저 E2E) 파트는 N/A로 두되,
Evaluator는 브라우저 E2E 대신 **`git diff`로 `frontend/`가 전혀 수정되지 않았음을 증거로 확인**한다(대체 검증).

────────────────────────────────────────────────────────────────────────
## IN / OUT 선별 (Planner 판단 — 현장가치·리스크 근거)

각 후보의 실제 코드 위치를 열어 현 유효성을 확인함. **이미 해소된 항목은 OUT(사유 명기).**

### IN — 백엔드 관측성 (Module 1)  ※ 현장가치 최상위 군
| ID | 항목 | 위치(확인) | 근거 |
|----|------|-----------|------|
| D-1 | **OFFLINE 지속 중 로그 스팸 억제** [현장↑] | PlcGateway.cs:319·330-333 | 폴 실패 시 `LogWarning(ex,…)`가 매 폴 스택 전문 + `isHardEx` 매회 true라 "OFFLINE 전이" `LogError`가 매 폴 반복(거짓 전이 라벨). 진단 로그 매몰 → 관측성 훼손. 전이 1회만 상세, 지속은 강등/주기요약, ONLINE 복구 1줄. |
| D-2 | **Serilog rollOnFileSizeLimit** [현장] | backend/src/Wcs.Api/appsettings.json Serilog File Args | 현재 미설정 → 기본 1GB 도달 시 그날 잔여 로그 침묵 유실. `rollOnFileSizeLimit=true`(+`fileSizeLimitBytes`·`retainedFileCountLimit`) 추가. |
| D-3 | **/health 엔드포인트** [현장] | Program.cs:204(MapControllers 유일) | 프로세스 생존/소터 Online/DB 연결을 외부에서 확인할 HTTP 표면 0. 최소 liveness 신설(과설계 금지 — B2B-1 재사용 예정). |
| D-4 | **입력 상한(input caps)** [현장] | RcsController.cs:49·51-52 / DbRepositories.cs:82 | barcode 길이 무검증(스키마 nvarchar(200))·timeStamp 무검증(ClientTs 30)·IF-05 qty 상한 부재(int 오버플로로 OVER 우회·ReservedQty 오염)·IF-10 음수 qty 무검증. Postman·스캐너·RCS 버그 1건으로 500 또는 조용한 데이터 오염. |
| A-1 | **HS_R_RESIDUE 로그레벨 승격** [현장↑] | Program.cs:410-411 (레벨 분류기) | 분류기가 `MISMATCH/TIMEOUT/OFFLINE` 키워드만 ERROR, 나머지 INFO. "HS_R_RESIDUE"(잔류 감지 — 현장 추적 핵심)가 INFO로 묻힘. `OperationLogLevel.WARN` 존재 확인(Entities.cs:89) → RESIDUE 키워드 WARN 승격. |
| A-2 | 기동 reconcile spurious RFlagRaised 에지 억제 + 주석 | PlcGateway.cs:286-310 | 기동 첫 폴 잔류 처리(ClearR 큐 투입) 직후 `if(!prevRFlag && snap.RFlag)`가 RFlagRaised 에지도 발화. 소비자 부재라 무해하나, reconcile가 지울 값을 에지로도 흘림. reconcile 발동 시 에지 억제 + RFlagRaised 채널의 소비자 부재 상태를 주석 명시(F-3 흡수). 저비용·방어. |

### IN — Sim/RTU + 테스트·시드 위생 (Module 2)
| ID | 항목 | 위치(확인) | 근거 |
|----|------|-----------|------|
| C-2 | SimServer UnitId 무음 절단 fail-loud | SimServer.cs:40·118·134 | `int UnitId` → `(byte)opt.UnitId` 무음 절단(300→44). RTU 유효 1~247 범위 검증 fail-loud(형제 ParsedParity/ParsedStopBits와 동형 — Consistency). |
| C-3 | SetRResidue/Flush/Pull StartAsync 이전 호출 NRE | SimServer.cs (`_transport` nullable, StartAsync:143 세팅) | 기동 전 호출 시 `_transport` null → NRE. 명확한 InvalidOperationException("StartAsync 먼저 호출") 가드. |
| A-3 | InjectStickyRResidue 등 volatile 일관성 | SimServer.cs:86(및 :65·:68) vs :108 `volatile _noResponse` | 형제 `_noResponse`는 volatile인데 InjectStickyRResidue/InjectRSeqOverride/InjectRFlagDelayMs는 plain auto-property. 크로스 스레드 읽힘 → volatile 정렬(테스트 결정성). |
| B-1 | **seed-field-20cells.sql 매핑확장 주석 정정** [현장↑] | scripts/seed-field-20cells.sql:22-28 | 주석이 확장을 "수동 UPDATE cell SET Enabled=1"로 안내하나, 스크립트는 `@availMax` 리팩터됨 — 실제 정확한 확장 = **`@availMax=15→20`(§2 cells·§5 order_item·§6 배정 자동 연동) + §4 오더 VALUES 리스트 1~15→1~20 수동 확장 + §7 셀16 CANCELLED 블록 제거.** 현장 매핑 확장 시 오독 방지(1순위). 주석만 정정. |
| B-2 | Field20CellsGateTests PlannedQty=100 주석 | Field20CellsGateTests.cs:87·124 | 시드는 PlannedQty=3인데 테스트 픽스처는 100. "OVER 격리용 상향(게이트 검증 시 OVER 간섭 배제)" 1줄 주석. |
| B-4 | LoadCellQty pId 41000대 주석 | Field20CellsGateTests.cs:286 | SPEC pId 1~30000인데 41000대 사용. "실 IF-05 pId 아님 — LOADED 직적재 합성 pId(20000대 IF-05 pId와 비충돌)" 우회 명시 주석. |
| B-6 | (선택) cells_enabled 검증 컬럼 술어 분리 | seed sql:224-225 | `Enabled=1 AND Capacity=@cellCap` 혼입 → enabled/capacity 진단 conflate. B-1과 같은 파일이라 저비용 — 술어 분리(진단 명료). |

### IN — 문서 현행화 (Module 3)
| ID | 항목 | 위치(확인·현 stale) | 근거 |
|----|------|-----------|------|
| A-5 | **master_spec §05 FULL/PAUSED 타입별 분기** [현장] | wcs_rcs_3ds_master_spec.html:170·175·179 | "FULL·PAUSED는 NG"(타입 구분 없음)이 확정4(슈트=OK·소터만 NG)·실코드(DbRepositories.cs:68-70)·interface_kr과 충돌. canonical HTML 2종이 정반대 → 목요일 문서 사용 전 정정. 표 FULL/PAUSED 행에 "슈트=OK(보내고 대기)/소터=NG" 분기 반영. |
| A-20 | **README.md 전면 현행화** [현장 — RCS 오도 방지] | README.md:8·16·18·35·43·45·64·66·70 (전부 stale 확인) | 폐지된 IF-08 폴링('allowed=true까지 폴링')·Minimal API·"Modbus TCP"만·"개발은 SQLite 분기"·16테이블·IF-09 부재·HTML 4종·로드맵 미래형. RCS 개발사에 공유되면 폐지 API 구현 유도. 현행 재작성. |
| E-SPEC | SPEC.md §2/§3 IF-08 푸시모델 명료화(최소) | SPEC.md §2·§3 deposit-permission | IF-08 엔드포인트가 폴링→푸시로 대체됐으나 §2/§3은 구 폴링 서술. **판정표(§2-A/2-B DepositDecider)는 내부 판정 스펙으로 유효 — 삭제 금지.** "IF-08 deposit-permission 엔드포인트 폐지·WCS→RCS 푸시로 대체(interface_kr 참조), 아래 판정표는 내부 DepositDecider 스펙" 최소 주석. |
| A-6 | **CLAUDE.md drift** ※ ORCHESTRATOR-APPLIED | CLAUDE.md:29·37·60 등 (still stale 확인) | "Minimal API: IF-05/08/10"(실제 MVC·IF-05/09/10+IF-08 푸시)·"M5에서 Serilog 도입"(완료)·"§6 투입 가부 표"(§06=푸시)·"16테이블"(17)·Migrations 2종 누락. **CLAUDE.md는 Team 보호파일(workflow-agents.md — Generator/Evaluator 수정 금지). 오케스트레이터/사용자가 적용**(Q1 참조). |

### OUT — 사유 명기
| ID | 항목 | OUT 사유 | 처리 |
|----|------|----------|------|
| A-4 | TimingOptions 레코드 중복 필드 통합 | dual-record(공통 TimingOptions + 소터별 nullable SorterTimingOverride)는 **의도된 정상 패턴**(ToXxx 병합 로직 보유). 통합은 병합 경로를 건드리는 횡단 리팩터 — 리스크>가치, 과확장 금지. | todo 등재 |
| B-3 | Field20CellsGateTests:196-200 데드 브랜치 | 5회 반복 루프의 `p.Value <= AvailMax` 가드는 "16~20 미반환" 불변식을 문서화하는 무해한 방어 단언. 제거는 회귀 의도 약화 리스크. | 유지(무변경) |
| B-5 | grid-cols-5 전역 고정(SortingSection.tsx:76) | 요건 부합·수용됨(물리 4×5 미러링). frontend 0 변경 원칙. | todo(다중소터 셀수기반 열도출=미래) |
| C-1 | RTU-REHEARSAL.md:8 SPEC 참조 라벨 | **이미 정확** — 현재 "[§6 Sim3ds 동작]·[§7-A 전송 방식 확정]" 두 참조 모두 SPEC 구조와 일치. 해소됨. | OUT(해소) |
| C-4 | ISimTransport.Server 노출 폭 | 수용·리뷰 종결(internal 봉인, 실용적 선택). | OUT |
| C-5 | fire-and-forget 시퀀스 vs Dispose 경합 | 선재·방어 처리 확인됨. | OUT |
| C+ | 물리 RTU 실선 미검증 | 코드 결함 아님 — 목요일 리허설 몫(RTU-REHEARSAL 체크리스트 커버). | OUT |
| A-19 | appsettings.Development.json 주석 | **이미 수정됨** — 현재 주석이 "launchSettings 부재로 dotnet run 기본=Production"·필드안전 경고(현장 DB 오염 방지)·SeedOnStartup=false 전부 반영. 해소됨. | OUT(해소) |
| A-12/A-13 | Serilog 상대경로·install-service.ps1 | 감사 **묶음 B(운영 배포 전)** — 본 스프린트(묶음 A·E) 범위 밖. | todo(기추적) |
| E-⑤code | DbSeeder chuteNo=30↔Sorters=1 정렬 + Development.json Provider 오버라이드 | **config/code 변경**(현장 DB 오염 회귀) — E는 "문서만" 범위. 잠정 완화(Development.json 필드안전 경고)는 **이미 존재**, 필드 기본=Production. 신중한 전용 처리 필요. | todo(기추적 :8·:16) · Q3 |
| F-1 | DbSeeder First() 하드닝 | 명시 dev 플래그일 때만 시드 도달·현장=Production. 저현장가치. | todo |
| F-2 | DateTimeOffset→Stopwatch monotonic | 큰 횡단 변경 — 본 스프린트 OUT. | todo |

────────────────────────────────────────────────────────────────────────
## Implementation Scope (Generator)

기술 세부(정확한 메서드·시그니처·테스트 배치)는 Generator 재량. 아래는 **무엇을** 만들지의 계약.

### Module 1 — 백엔드 관측성 (파일: PlcGateway.cs · Program.cs · RcsController.cs · appsettings.json + 신규 백엔드 테스트)
1. **D-1**: 폴 실패 로깅을 전이/지속 분리 — (a) OFFLINE **전이 시 1회만** 상세(스택 포함), (b) 지속 실패는 스택 없는 강등(Debug) 또는 N폴/주기마다 1줄 요약, (c) ONLINE 복구 1회 로그. **요약 주기·백오프 등 신규 타이밍은 appsettings**(절대규칙 #7 — 하드코딩 금지). alarm/operation_log 전이당 1회 가드는 현 동작 보존.
2. **D-2**: appsettings.json Serilog File Args에 `rollOnFileSizeLimit=true`(+`fileSizeLimitBytes`·`retainedFileCountLimit`) 추가. Development.json도 정합 검토.
3. **D-3**: `/health` GET 1개 — 부수효과 0(ISorterGatewayRegistry.AllBundles의 Latest.Online/At + DB 연결여부 스냅샷 읽기만). 응답 예: `{status, db:bool, sorters:[{chuteNo, online, lastPollAt}]}`. **liveness 최소**(전용 HealthChecks 프레임워크 도입 금지 — 단일 엔드포인트). 상태코드 정책은 Q2.
4. **D-4**: RcsController 입력 검증 추가(DB 도달 전) — barcode 길이 ≤ 스키마 상수(200, 단일 진실 공유)·timeStamp(ClientTs) ≤30·IF-05 qty 상한(설정값·appsettings, OVER 검사는 long 산술로 오버플로 방어)·IF-10 qty 음수 거부. 응답 의미(400 vs NG)는 Q2. **정상 입력 경로 동작 불변**(무변경 가드).
5. **A-1**: Program.cs:410 레벨 분류기에 RESIDUE 키워드 → `OperationLogLevel.WARN` 승격(MISMATCH/TIMEOUT/OFFLINE은 ERROR 유지).
6. **A-2**: reconcile 발동 폴에서 spurious RFlagRaised 에지 억제(또는 억제 불가 시 주석으로 무해성 명시) + RFlagRaised 채널 소비자 부재 상태 주석(F-3).

### Module 2 — Sim/RTU + 테스트·시드 위생 (파일: SimServer.cs · Field20CellsGateTests.cs · scripts/seed-field-20cells.sql)
7. **C-2**: SimServer UnitId 1~247 범위 검증 fail-loud(범위 밖 명확한 예외).
8. **C-3**: SetRResidue/Flush/Pull(StartAsync 前) 명확한 InvalidOperationException 가드.
9. **A-3**: InjectStickyRResidue·InjectRSeqOverride·InjectRFlagDelayMs volatile 정렬.
10. **B-1**: seed sql:22-28 매핑확장 주석 정정(위 근거대로 @availMax·§4·§7 경로 정확화).
11. **B-2/B-4**: 테스트 주석 1줄씩.
12. **B-6(선택)**: cells_enabled 검증 술어 분리.

### Module 3 — 문서 (파일: README.md · wcs_rcs_3ds_master_spec.html · SPEC.md ; CLAUDE.md = 오케스트레이터 적용)
13. **A-5**: master_spec §05 FULL/PAUSED 표 행 목적지 타입별 분기.
14. **A-20**: README 현행 재작성.
15. **E-SPEC**: SPEC.md §2/§3 IF-08 푸시 명료화 최소 주석(판정표 보존).
16. **A-6**: CLAUDE.md 정정안을 계약에 명시하되 **적용은 오케스트레이터**(Team 보호파일).

────────────────────────────────────────────────────────────────────────
## Parallel Modules (Generator fan-out 가능 — 경계 확인 완료)

세 모듈은 **쓰기 파일이 겹치지 않음**(확인함):
- **Module 1**(백엔드 관측성): `PlcGateway.cs`, `Program.cs`, `RcsController.cs`, `appsettings.json`(+`appsettings.Development.json` Serilog), 신규 백엔드 테스트 파일.
- **Module 2**(Sim·테스트·시드): `SimServer.cs`, `Field20CellsGateTests.cs`, `scripts/seed-field-20cells.sql`.
- **Module 3**(문서): `README.md`, `wcs_rcs_3ds_master_spec.html`, `SPEC.md`.

Fan-out 규칙(택하면):
- Module 1·2 모두 `Wcs.Tests` 프로젝트에 쓰지만 **서로 다른 파일**(Module 1=신규 테스트 파일, Module 2=Field20CellsGateTests.cs) → 파일 충돌 0. 신규 테스트는 기존 파일에 추가하지 말고 별도 파일로.
- **CLAUDE.md는 어느 모듈에도 속하지 않음** — 오케스트레이터 적용(Q1).
- Fan-in: 병합 후 **전체 스위트 1회 통합 실행**(189 + 신규). worktree 격리 권장(파일 경계는 깨끗하나 csproj 동시성 안전).
- 단일 Generator가 순차 처리해도 무방(모듈은 순서 독립). 규모가 작아 fan-out은 선택.

## Evaluation Dimensions: functional only
(관측성·입력위생·문서 — 단일 기능 차원. 보안/성능 전용 병렬 검증 불요. 입력 캡의 오버플로/음수는 functional 케이스로 커버.)

────────────────────────────────────────────────────────────────────────
## Evaluation Criteria (Backend/API 4기준 — Evaluator)
1. **API Design Quality (★★★)**: /health 응답 형태 일관·부수효과 0; 입력 검증 응답(에러 구조)이 기존 IF-05/10 규약과 정합; 로그 레벨 분류 의미 정확(RESIDUE=WARN).
2. **Architecture Originality (★★★)**: 최소 침습·재사용 가능한 최소 형태(B2B 대비 과설계 0); dual-record 등 기존 패턴 존중; 신규 타이밍 전부 appsettings.
3. **Craft (★★)**: 오버플로/음수/과길이 엣지 처리; fail-loud(UnitId·pre-StartAsync); volatile 정렬; 예외 삼킴 0; 로그 스팸 실제 억제 확인.
4. **Functionality (★★)**: **무변경 가드** — 핸드셰이크·판정(Decide)·기존 GREEN 189 전부 보존; 문서가 실코드와 일치.

────────────────────────────────────────────────────────────────────────
## Completion Conditions (Evaluator PASS 최소 조건)
- [ ] 기존 전체 스위트 GREEN + **카운트 189 재확인**(기동 시 `dotnet test backend/Wcs.sln`로 baseline 재측정) + IN 항목별 신규 테스트 GREEN.
- [ ] **D-1 실효 검증**: Sim으로 OFFLINE 유도(StopAsync 등) → 지속 OFFLINE 동안 **로그 라인 수가 폴 주기마다 스택 반복이 아님**을 단정(전이 1회 상세 + 지속 억제). 라인 수/레벨을 fresh 로그 캡처로 인용.
- [ ] **D-2**: File 싱크에 rollOnFileSizeLimit 반영 확인(설정 파싱·기동 로그 or 단위 확인).
- [ ] **D-3**: 실제 `GET /health` 왕복(HTTP 응답 본문) — status·db·sorters 필드 존재·부수효과 0.
- [ ] **D-4**: 실HTTP로 (a) 과길이 barcode(>200)·(b) IF-05 qty=int.MaxValue·(c) IF-10 음수 qty → **500 아님·데이터 오염 0**(정상 입력은 불변) 단정. SQLite 테스트 더블의 provider-gap 유의(길이 미강제 → 컨트롤러 검증으로 잡아야 함).
- [ ] **A-1**: operation_log에 HS_R_RESIDUE가 WARN으로 기록됨을 단정.
- [ ] **C-2/C-3**: UnitId 범위밖·pre-StartAsync 호출 → 명확한 예외(fail-loud) 단정.
- [ ] **문서(A-5/A-20/E-SPEC)**: 정정 내용이 실코드·interface_kr과 일치(리뷰). **frontend/ 무변경**을 `git diff`로 확인(Web/UI 브라우저 E2E 대체).
- [ ] 정적검사(빌드 경고 회귀 0 — 단, 선재 NU1903 SQLitePCLRaw advisory는 기존 부채로 별건 todo:4, 본 스프린트 신규 경고 0).
- [ ] A-6 CLAUDE.md 정정은 오케스트레이터 적용 확인(Team 미수정).

────────────────────────────────────────────────────────────────────────
## Verification Scenarios (Full-stack — 슬롯 채움)

### Web/UI 슬롯 — 전부 N/A (사유: 이번 스프린트 frontend 표면 0 변경)
- 각 surface 기본상태 / 대체상태 / 빈·에러 상태 / 다크모드 / 핵심 상호작용:
  **N/A** — 프론트엔드 파일 무수정. Evaluator는 브라우저 E2E 대신 `git diff --stat`로 `frontend/`에 변경이 없음을 증거로 확인(대체 검증). B-5는 OUT.

### Backend/API 슬롯 — 채움
- **이번 스프린트가 건드리는 엔드포인트(method + path)**:
  1. `GET /health` (신규 — D-3)
  2. `POST /api/v1/destination-query` (IF-05 — D-4 입력검증 추가; 판정 의미 불변)
  3. `POST /api/v1/deposit-report` (IF-10 — D-4 음수 qty 거부; 멱등 의미 불변)
- **엔드포인트별 happy path(입력→출력 형태)**:
  1. `/health` → 200, `{status, db:true, sorters:[{chuteNo,online,lastPollAt}]}`(현장 소터 chuteNo=1 반영).
  2. IF-05 정상 barcode·qty → 기존과 동일 OK·chuteNo·reason(NORMAL/BUSY/…) — **회귀 없음**.
  3. IF-10 정상 → `{result:"OK"}` 멱등 — **회귀 없음**.
- **엔드포인트별 관련 에러 케이스(Planner 선별 — 패딩 없음)**:
  1. IF-05 barcode 길이 >200 → 검증 거부(500 아님; Q2 결정에 따라 400 또는 NG), DENIED 감사행/operation_log 배치 유실 없음.
  2. IF-05 qty ≤0(기존) 및 qty=2147483647(오버플로) → 거부, ReservedQty 오염 0.
  3. IF-10 qty 음수 → 거부, ChuteCapacity DepositedQty 왜곡 0.
  4. timeStamp 원문 >30자 → 거부/절단(ClientTs truncation 500 방지).
  5. `/health` DB 다운/소터 OFFLINE 시 응답(Q2 상태코드 정책대로) — 부수효과 0 유지.

### 계층 교차(E2E) 슬롯 — 프론트↔백엔드 흐름은 N/A(이번 변경 없음)
대신 **백엔드↔Sim3ds 통합**(이 스프린트의 실제 통합면)을 실 Sim으로 검증:
- Sim OFFLINE 유도 → (a) 폴 루프 로그 스팸 억제 실측(D-1) + (b) 그 상태에서 `/health`가 sorter online=false·lastPollAt 반영(D-3)을 한 시나리오로 관측.

> Planner self-check — Detected project type: Full-stack. Required scenario slots: 6 (Web/UI=N/A(git-diff 대체), Backend/API endpoints, happy path, error cases, cross-layer=N/A(backend↔Sim 대체), integration-observed). All slots filled: yes.

────────────────────────────────────────────────────────────────────────
## 제약 재확인 (절대규칙 — 위반 금지)
- **#7 하드코딩 금지**: OFFLINE 요약 주기·백오프·qty 상한 등 신규 임계값은 전부 appsettings.
- **#1 PLC 쓰기 단일 큐**: A-2 reconcile·ClearR은 큐 경유 유지(직접 Modbus 호출 금지).
- **무변경 가드**: 핸드셰이크(HandshakeOrchestrator)·판정(DepositDecider)·TgtFloor 규칙 의미 변경 0.
- **예외 삼킴 금지**: 입력검증·fail-loud는 명시 예외/응답으로.
- **frontend 0 변경**(B-5 OUT).
- **CLAUDE.md·workflow-*.md·.git/hooks/는 Team 미수정**(오케스트레이터/사용자 전담).

────────────────────────────────────────────────────────────────────────
## Questions (novel 결정 — 선택지 + 권장안)

**Q1. CLAUDE.md drift(A-6) 적용 주체.**
CLAUDE.md는 Team(Generator/Evaluator) 보호파일이라 팀이 수정 불가.
- (a) **[권장]** 오케스트레이터가 Team 완료 후 docs-only 정정을 직접 적용(코드 리스크 0·순수 문서). 계약에 정정안 명시됨.
- (b) 사용자가 직접 정정.
- (c) 본 스프린트에서 제외하고 별도 처리.
→ 권장 (a).

**Q2. 입력 상한(D-4) 거부 의미 + /health 상태코드.**
- 입력 캡: (a) **[권장]** 구조적 위반(과길이 barcode/timeStamp·비양수/오버플로 qty)은 **400 Bad Request**로 DB 도달 전 거부(입력 검증 ≠ 업무 NG) / (b) 기존 NG 규약에 흡수(reason 추가). RCS 계약 영향 있어 확인 필요.
- /health: (a) **[권장]** liveness=프로세스 생존이면 **항상 200**+본문에 db/sorter 저하 플래그(단순·프로브 친화, readiness 503은 B2B-1 이연) / (b) 저하 시 503.
→ 권장 (a)/(a). RCS가 400을 어떻게 처리할지 미확정(SPEC §7 Q1~Q7 대기)이면, 현장 Postman 단계에선 400+명확 메시지가 안전.

**Q3. 현장 DB 오염 회귀(E-⑤ code) 이연 확인 — FYI.**
DbSeeder 소터 chuteNo=30 ↔ base Sorters ChuteNo=1 미스매치는 명시 `SeedOnStartup=true`를 dev Provider 오버라이드 없이 켜면 현장 SqlServer DB 오염 경로. **잠정 완화(Development.json 필드안전 경고)는 이미 존재**하고 필드 기본 환경=Production이라 즉시 위험은 낮음. 본 스프린트(docs+관측성)에서는 코드 정렬 OUT·todo 유지.
→ 권장: **이연 확인**. 목요일 전 현장 머신에서 ASPNETCORE_ENVIRONMENT=Development 기동만 하지 않으면 안전. 정렬이 필요하면 별도 소규모 스프린트.

────────────────────────────────────────────────────────────────────────
## 참고 — 이번 스프린트로 신규 todo 등재 예정(OUT 항목)
A-4(TimingOptions 통합) · B-5(다중소터 열도출) · F-1(DbSeeder First 하드닝) · F-2(Stopwatch monotonic).
기추적 유지: 묶음 B(A-12/A-13) · E-⑤ code(todo:8·16) · NU1903(todo:4).

── ★ 오케스트레이터 확정 (2026-07-07, Questions 처리 — 기존 관례 준수 권장안 채택) ──
Q1: CLAUDE.md(A-6) 정정은 **오케스트레이터가 커밋 단계에서 직접 적용** (Team 보호파일)
Q2: 입력 상한 위반 = **400**(기존 IF-05 검증 관행 동형) / /health = **항상 200 liveness**(상태 상세는 본문 JSON)
Q3: 현장 DB 오염 회귀 이연 확인 — 잠정 완화 존재(Production 기본) 전제 수용
실행 방식: Parallel Modules 3기 fan-out(워크트리 격리) → 오케스트레이터 fan-in(패치 수확→통합 빌드·전체 테스트) → 단일 Evaluator
