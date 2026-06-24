# Sprint Contract — RCS↔WCS 인터페이스 재설계 Phase 2 (아웃바운드 목적지 상태 푸시)

> Branch: `feat/rcs-if-redesign-p2` (develop @ PR #12 머지에서 분기)
> Planner 작성. **WHAT만 정의 — HOW(라이브러리·클래스·시그니처·재시도 알고리즘)는 Generator 결정.**
> 스펙 단일 진실 = 커밋된 HTML 5건. 특히 `docs/wcs_rcs_interface_kr.html`(IF-08 destination-status 푸시 정의·페이로드·RCS 해석 표)·`docs/wcs_3ds_unified_sequence.html`(③ ready 대기 = IF-08 푸시 수신).
> 선행 = Phase 1(PR #12 머지). 이 계약은 Phase 1이 선확보한 **`DestinationStatusService.Compute` 단일 산출**을 소비하는 아웃바운드 푸시 계층만 신설한다.

---

## 0. 분할 평가 (단일 스프린트 적정성)

### 판정: **단일 스프린트로 진행한다** (분할하지 않음).

Phase 2의 세 구성요소 — ① 아웃바운드 HTTP 클라이언트(WCS→RCS) ② 복합 `ready` 전이 감지(변화원 둘) ③ 실패/재시도 정책 — 는 **하나의 응집된 관심사**다:

- 세 요소가 **같은 신규 컴포넌트 경계**(아웃바운드 푸시 서비스) 안에서 동작하고 **같은 파일 집합**(신규 `Wcs.Api` 푸시 클라이언트 + 기존 변화원 2곳에 hook 결선)을 만진다. 모듈/패키지 경계로 깨끗이 갈라지지 않아 병렬 Generator로 쪼개면 같은 파일을 두 Generator가 만지게 된다.
- ②(전이 감지)와 ③(재시도)은 ①(클라이언트)이 없으면 검증할 대상이 없고, ①은 ②의 트리거 없이는 호출되지 않는다 — **순환 의존**이라 중간 산출물이 "동작하지만 미완"이 되기 쉽다. 한 사이클에 묶어야 가짜 RCS 수신 엔드포인트로 "전이당 1회 푸시 + 재시도 + 폴마다 폭주 없음"을 한 번에 단언할 수 있다.
- 규모: Phase 1(인바운드 4종 + Controller 전환 + 시나리오 대거 재작성)보다 작다. 신규 표면이 아웃바운드 푸시 1개 컴포넌트 + 변화원 2곳 hook + 테스트로 한정된다.

따라서 **단일 모듈 / 단일 Generator**가 적정하다(Parallel Modules N/A). 동시성 표면(변화 감지가 폴 스레드/타이머/이벤트 콜백 — 동시 푸시 경쟁)이 존재하므로 검증 차원은 functional 단일이되 **동시성 결함을 functional 시나리오로 명시 강제**하고, 4-Tier 독립 코드리뷰를 APPROVED 후 별도 게이트로 둔다(메타교훈: 인메모리/단일프로세스 GREEN ≠ 결함 없음 — 5회 반복 적발 사례).

---

## [Sprint Contract] — Phase 2

### Goal
WCS가 목적지(슈트 + 3D 소터) 상태를 **상태 변경 시에만** RCS로 푸시하는 **아웃바운드 HTTP 클라이언트**를 신설한다. `POST {RCS base}/api/v1/destination-status`, 페이로드 `{chuteNo, ready, timeStamp}`(키=`chuteNo`). `ready`는 Phase 1의 `DestinationStatusService.Compute`가 산출하는 **복합 단일 bool**을 재사용한다 — 새로 판정 로직을 만들지 않는다. chuteNo별 `ready`가 전이(true↔false)할 때만 **정확히 1회** 푸시한다(주기 전송 아님). 변화원은 둘: ① 슈트 `ChuteCapacityService` 상태 변화 ② 소터 게이트웨이 폴링 스냅샷 변화. RCS base URL은 설정화하고, RCS 미도달 시 실패/재시도 정책을 적용한다. **인바운드(IF-05/09/10)·Modbus·핸드셰이크·Sim3ds 본문은 건드리지 않는다.**

### Implementation Scope (Generator가 해야 할 일 — WHAT)

**A. 아웃바운드 푸시 클라이언트 (신규 컴포넌트)**
1. WCS→RCS로 목적지 상태를 푸시하는 **신규 아웃바운드 클라이언트**를 `Wcs.Api`에 신설한다. 전송: `POST {RCS base}/api/v1/destination-status`. **페이로드 = `{chuteNo, ready, timeStamp}`** (camelCase 와이어, STJ 기본). `chuteNo`=정수(목적지 슈트 번호 = destination.chute_no, 소터도 동일 키), `ready`=bool, `timeStamp`=문자열(기존 와이어 시간 포맷과 일관 — `wcs_rcs_interface_kr.html` 예시 `"2026-06-22 14:30:00"` 형태). RCS는 `{result:"OK"}` 또는 2xx로 응답.
2. HTTP 클라이언트는 **`IHttpClientFactory` 경유**로 생성한다(소켓 고갈·DNS 갱신 방지 — 직접 `new HttpClient()` 금지). 타임아웃·기본 헤더 등은 설정/팩토리 구성.
3. **RCS base URL은 설정화**한다(`WcsOptions` 또는 동등 설정 섹션 — 하드코딩 금지, 절대규칙 #7·#7-URL). 미설정 시 동작은 §사용자 질문 4 확인 후 결정.

**B. `ready` 산출 = Phase 1 단일 산출 재사용 (새 판정 금지)**
4. 푸시할 `ready` 값은 **`IDestinationStatusService.Compute(destinationId, destType)`의 결과(`DestinationReadiness.Ready`)를 그대로 사용**한다. Phase 2는 ready 판정 로직을 새로 만들지 않는다 — Phase 1이 선확보한 단일 산출 경로를 소비할 뿐이다.
   - 슈트 `ready` = `!full && !paused`(비활성 포함) — `ComputeChute`가 이미 산출.
   - 소터 `ready` = Phase 1 `ComputeSorter` 산출(`online && CurFloor==운영층 && Ready==1`). **단, 소터 `ready`에 full/paused를 접을지는 §사용자 질문 1에서 확정** — Phase 1 `ComputeSorter`는 현재 full/paused를 소터 ready에 미반영(`Full:false,Paused:false` 하드코딩, [[m4p4-if08-cell-fullness]] 이연). 사용자 결정에 따라 (a) Phase 1 산출 그대로 푸시 / (b) `DestinationStatusService`에 소터 full/paused 접기를 추가(이 경우 Scope 확장 — §질문 1).
   - 개별 `full`/`paused`/`online` 플래그는 **푸시하지 않는다**(복합 `ready` 하나로 접힘 — 스펙 `ready`가 RCS로 보내는 유일한 플래그).

**C. 변화원 ① — 슈트 상태 전이 감지 (`ChuteCapacityService`)**
5. 슈트의 복합 `ready`가 전이(true↔false)할 때 푸시를 1회 발생시킨다. 변화원은 `ChuteCapacityService`의 상태 변화(`OnReserved`/`OnDeposited`/`OnReservationCancelled`/`OnCleared` 또는 그로 인한 full/paused 경계 통과). 현재 `ChuteCapacityService`는 전이 이벤트를 노출하지 않으므로, 전이 감지 hook을 결선한다(이벤트 노출 vs. 비교 방식은 Generator 결정).
   - **전이 의미론**: chuteNo별 직전 `ready` 상태를 보관하고, 새 산출이 직전과 다를 때만 푸시한다. 같으면 푸시하지 않는다(주기·반복 전송 금지).
   - `OnReserved`로 만재 경계를 막 넘으면 `ready: true→false` 1회, `OnCleared`/`OnReservationCancelled`로 다시 받을 수 있게 되면 `ready: false→true` 1회.

**D. 변화원 ② — 소터 스냅샷 전이 감지 (게이트웨이 폴링 스냅샷)**
6. 소터의 복합 `ready`가 전이(true↔false)할 때 푸시를 1회 발생시킨다. 변화원은 **소터별 폴링 스냅샷의 변화**(online·CurFloor·Ready 등 ready 구성요소 변화). 기존 폴링 스냅샷을 **관찰/구독**하여 전이를 감지한다(폴마다 스냅샷이 갱신되더라도 `ready` 전이가 없으면 푸시 0건 — 폴마다 폭주 금지).
   - **기존 폴링 본문 무변경**: `PlcGateway.cs`/`PlcPollingService` 본문은 건드리지 않는다. 스냅샷 관찰은 (a) `SorterBundleHandle.Latest`를 WCS측에서 주기 관찰·diff 하거나 (b) `OnOfflineTransition`처럼 **추가 이벤트 노출이 정말 필요하면** 그 노출만 명시적으로 추가한다(이벤트 추가 시 게이트웨이 본문 변경을 최소·정당화하고 §무변경 zone 위반 여부를 Evaluator가 판정). HOW는 Generator 결정이되, **레지스터맵/핸드셰이크/Sim3ds 프로토콜 본문은 절대 무변경**.
   - **운영층(OperationalFloor) 설정 경유**: 소터 ready 산출이 운영층 비교를 포함하므로 `DestinationStatusService`(이미 설정 주입)를 재사용. Phase 2가 운영층 리터럴을 새로 하드코딩하지 않는다.

**E. 실패 / 오프라인 재시도 정책**
7. RCS 미도달(연결 거부·타임아웃·5xx 등) 시 **재시도 정책**을 적용한다(고정 sleep 금지·하드코딩 금지 — 재시도 횟수·백오프·간격은 설정값, 절대규칙 #7). 재시도 소진 후 동작(드롭 vs. 큐잉/최신값 유지 후 다음 전이에 재시도)은 §사용자 질문 2·3 확인 후 결정.
   - **전이 정합성 보존**: 재시도/실패가 chuteNo별 "마지막으로 RCS에 성공적으로 알린 ready 상태" 추적을 오염시키지 않아야 한다(실패한 푸시를 성공으로 간주해 다음 동일 전이를 놓치면 안 됨 — §질문 3과 연동).
   - **예외 삼킴 금지**(절대규칙·Fail-Loud): 재시도 소진·최종 실패는 명시 로깅(ILogger). fire-and-forget 푸시라도 `.ContinueWith` IsFaulted 또는 동등 패턴으로 미관찰 예외 0.

**F. 동시성 (변화원 둘 + 재시도 타이머/이벤트가 동시 푸시 경쟁)**
8. 변화원 둘(슈트 이벤트 스레드 / 소터 폴 스레드·관찰 타이머) + 재시도 경로가 **동시에 같은 chuteNo의 ready 상태/푸시를 갱신**할 수 있다. chuteNo별 "직전 ready" 추적과 푸시 발생은 **경합에서도 전이당 정확히 1회**를 보장해야 한다(P3 OFFLINE 전이당-1건 멱등 교훈: 비원자 check-then-act가 동시 호출에서 2건 발화 — `Interlocked`/락 등으로 원자화). 동시 전이에서 중복 푸시 0·누락 0.

**G. 부트스트랩(시작 시 초기 푸시) — §사용자 질문 5 확인 후**
9. WCS 기동 시 각 목적지의 초기 ready 상태를 RCS에 1회 푸시할지(RCS가 부팅 직후 상태를 알도록) 여부는 §사용자 질문 5에서 확정. 기본 권고는 "기동 시 전 목적지 1회 스냅샷 푸시 + 이후 전이만" 이나 사용자 확정 전 미구현.

**H. 검증 테스트 (가짜 RCS 수신 엔드포인트)**
10. 아래 §Verification Scenarios에 따라 **가짜 RCS 수신 HTTP 서버**(테스트용 in-process HTTP 서버 또는 `WebApplicationFactory`/`TestServer` 기반 수신 엔드포인트)를 세워 푸시를 실제로 수신·단언한다. 코드 리뷰로 대체 금지(절대규칙·Evaluator 의무).

**I. HTML 스펙 동기화 확인**
11. Phase 2가 스펙 HTML을 추가 수정해야 할 부분(예: IF-08 푸시 페이로드·재시도·하트비트 문구가 Phase 1에서 이미 반영됐는지 확인)이 있으면 같은 작업에 포함해 커밋 대상으로 둔다. Phase 1에서 이미 커밋된 부분은 재수정 불요 — `git diff`로 추가 변경분만.

### 무변경 유지 (절대 건드리지 말 것)
- **Modbus 레지스터 맵**(D0~D6·FC03/06/16) — `Wcs.Core/RegisterMap`·`PlcSnapshot.FromRegisters` 본문.
- **C/R 핸드셰이크 프로토콜** — `Wcs.PlcGateway/HandshakeOrchestrator`·`PlcGateway.cs`/`PlcPollingService` **본문**. 스냅샷 변화 감지는 기존 폴링 스냅샷(`Latest`)의 **관찰/구독만** — 폴 루프·RMW·`_clientLock`·기존 OFFLINE 전이 로직을 바꾸지 않는다. (소터 ready 전이 감지를 위해 `OnOfflineTransition`과 동형의 **추가 이벤트 노출**이 정말 필요하면 그 노출만 최소 추가 — 폴 동작·레지스터 읽기·타이밍은 불변. 추가 여부·범위는 Generator가 정당화하고 Evaluator가 본문 무변경을 git diff로 판정.)
- **`Wcs.Sim3ds` 프로토콜 본문.**
- **Phase 1 인바운드(IF-05/IF-09/IF-10)·`RcsController`·`DepositDecider`·`DestinationStatusService.Compute`의 판정 로직** — 회귀 0. (단 §질문 1에서 소터 full/paused 접기가 승인되면 `DestinationStatusService.ComputeSorter`에 **추가**는 허용 — 기존 IF-05 소비 동작은 회귀 0 유지.)
- **DB 스키마 / 마이그레이션** — Phase 2는 신규 테이블/컬럼 불요(푸시는 인메모리 전이 추적 + HTTP). 새 영속화가 필요하다고 판단되면(예: 푸시 감사 로그) **scope 확장 요청** 후 사용자 확인(protected zone).
- **`Wcs.Core` 순수성** — `Wcs.Core.csproj` Reference/Package 0, `DepositDecider` static·무필드·I/O 0. 푸시 클라이언트·HttpClient는 `Wcs.Api`에만.

### Scope OUT (이번 Phase 아님)
- **하트비트(주기 keep-alive 전송)** — 스펙상 "선택적·협의". §질문 6에서 사용자가 명시 요구하지 않으면 OUT(전이 기반 푸시만). 요구 시 별도 결정.
- **RCS측 투입 판단 로직** — RCS 소관(WCS는 푸시만). 검증에서 가짜 RCS는 "수신·OK 응답"까지만.
- **소터 셀 만재(빈 셀 없음)를 ready에 접는 로직** — §질문 1에서 "Phase 1 산출 그대로"로 확정되면 OUT([[m4p4-if08-cell-fullness]] 별도 트랙 유지). "접기"로 확정되면 IN(Scope B-4 (b)).

---

## Evaluation Criteria (Evaluator 판정 기준 + 가중)
프로젝트 타입 = Backend/API. 4-기준 구조:

1. **API Design Quality (★★★)** — (a) 아웃바운드 푸시 페이로드가 정확히 `{chuteNo, ready, timeStamp}`(camelCase 와이어, 개별 full/paused/online 미포함). (b) 엔드포인트가 `POST {RCS base}/api/v1/destination-status`로 구성(base URL 설정 경유, 하드코딩 0). (c) `IHttpClientFactory` 경유(직접 `new HttpClient()` grep 0). (d) RCS 2xx/`{result:"OK"}` 정상 처리, 비2xx는 재시도 경로로.
2. **Architecture Originality (★★★)** — (a) `ready` 산출이 **Phase 1 `DestinationStatusService.Compute` 재사용**(푸시 클라이언트가 ready 판정을 새로 구현하지 않음 — Compute 호출 1지점). (b) 변화원 둘이 **공통 푸시 경로**로 수렴(슈트 이벤트·소터 스냅샷이 같은 "전이 감지→푸시" 파이프로). (c) 전이 추적이 chuteNo별 "직전 ready" 단일 상태로 깔끔(주기 전송 아님이 구조로 보장). (d) 게이트웨이 본문 무변경(스냅샷 관찰만) — 추가 이벤트 노출이 있으면 최소·정당.
3. **Craft (★★)** — (a) 재시도 정책이 설정값 경유(횟수·백오프·간격 하드코딩 0, 고정 sleep 0). (b) fire-and-forget 푸시 예외 삼킴 0(`.ContinueWith` IsFaulted/SafeLog 로깅). (c) 동시 전이에서 전이당 1회 멱등(원자화 — 비원자 check-then-act 0). (d) 빌드 경고 0·teardown 클린(신규 HttpClient/타이머/HostedService가 graceful shutdown — exit 0 유지).
4. **Functionality (★★)** — (a) 슈트 ready 전이 시 정확히 1회 푸시(가짜 RCS 수신 단언). (b) 소터 ready 전이 시 정확히 1회 푸시 + **폴마다 폭주 0**(전이 없는 폴에선 푸시 0건). (c) RCS 미도달 시 재시도 동작(가짜 RCS를 거부→재개로 토글해 단언). (d) Phase 1 인바운드(IF-05/09/10·핸드셰이크) 회귀 0(전체 테스트 GREEN).

**가중**: ★★★ 항목(1·2) 하나라도 FAIL이면 APPROVED 불가. ★★ 항목(3·4)은 BLOCKING 결함이면 FAIL.

## Completion Conditions (Evaluator PASS 최소 조건)
- 솔루션 빌드 경고 0·에러 0(`dotnet build Wcs.sln`).
- 전체 테스트 GREEN(`dotnet test Wcs.sln`) — Phase 2 신규 푸시 테스트 + Phase 1 회귀 포함. **full-suite exit 0**(teardown hang 0 — PR #12에서 해소됨; 신규 HttpClient/타이머/HostedService가 graceful shutdown으로 종료 클린 유지. exit 1·abort·hangdump 발생 시 FAIL).
- flaky 의심 테스트(가짜 RCS 수신 HTTP·실 Sim 소켓 기반)는 **단독 다회(≥5) 연속 GREEN·exit 0·hangdump 0**으로 비결정성 0 확인(feedback-archive 메타교훈).
- **전이당 정확히 1회 푸시**: 가짜 RCS 수신 엔드포인트가 수신한 푸시 건수를 직접 카운트해 슈트·소터 각각 ready 전이 1회당 수신 1건 단언(2건도 0건도 아님). 동시 전이(병렬 변화원) 시뮬레이션에서도 정확히 1건.
- **폴마다 폭주 없음**: ready 전이가 없는 N회 폴(또는 N회 무변화 이벤트) 후 가짜 RCS 수신 0건(P3 `WaitUntilExactAsync(expected, stableCount)`형 강한 가드 — count가 N회 연속 expected 유지).
- **RCS 미도달 재시도**: 가짜 RCS를 거부(연결 거부/5xx)로 설정 → 재시도 발생 → 가짜 RCS 재개 → 푸시 성공 도달을 단언. 재시도 소진 후 동작(§질문 3 확정값)대로.
- **base URL·재시도 설정 경유**: RCS base URL·재시도 파라미터 하드코딩 grep 0(설정 1지점). `new HttpClient(` 직접 생성 grep 0(`IHttpClientFactory` 경유).
- **푸시 페이로드 정합**: 가짜 RCS가 수신한 실제 JSON 본문이 `{chuteNo, ready, timeStamp}` 정확히(개별 full/paused/online 키 부재) — 수신 raw 본문으로 입증.
- **무변경 가드**: `git diff develop -- src/Wcs.PlcGateway/PlcGateway.cs src/Wcs.PlcGateway/HandshakeOrchestrator.cs src/Wcs.Sim3ds src/Wcs.Core` — 본문 0줄(소터 ready 전이용 추가 이벤트 노출이 있으면 그 한 줄만 정당화; 레지스터맵/핸드셰이크/Sim3ds/Core 판정 0). `RcsController.cs` 인바운드 액션 본문 회귀 0.
- **Phase 1 회귀 0**: IF-05/09/10·핸드셰이크·alarm·OFFLINE 전이 단언 전부 GREEN 유지.

## Parallel Modules
N/A (single module — 아웃바운드 푸시 클라이언트 + 변화원 2곳 hook + 재시도가 한 컴포넌트 경계 안에서 강결합. 파일 경계가 겹치고(같은 푸시 파이프) 순환 의존이라 병렬 분할 시 충돌. 기본 1 Generator.)

## Evaluation Dimensions
functional only (단일 차원). **단, 동시성 표면이 명시적으로 존재한다**(변화원 둘 + 재시도 타이머/이벤트가 동시 푸시 경쟁 — Scope F). 메타교훈(M2 off-lock·M3 IF-10 멱등·M4-P1 마이그레이션·M4-P2a FULL 영속화·M4-P3 OFFLINE 전이당-1건 — 5회 연속 기능 GREEN 후 독립 리뷰가 동시성 적발)에 따라: ① functional 시나리오에 **동시 전이→전이당 1회 멱등**을 명시 강제(아래 VS), ② 4-Tier 독립 코드리뷰(Step 4.5)를 APPROVED 후 별도 게이트로 수행(전이 추적 원자성·푸시 경합·재시도 상태 오염·타이머/HostedService 수명주기 disposal을 코드 직접 검사). 인메모리 GREEN을 PASS 근거로 삼지 않는다.

---

## Detected Project Type: Backend/API
프로젝트 신호: `src/Wcs.Api`에 서버 라우트/컨트롤러(`RcsController`)와 서버 엔트리포인트(`Program.cs`, ASP.NET Core, `Microsoft.NET.Sdk.Web`)가 존재하고, 같은 리포에 브라우저 대면 UI 트리 없음(docs/의 HTML은 정적 인터페이스 정의서이지 클라이언트 렌더 뷰가 아님). Phase 2 신규 표면도 서버측 아웃바운드 HTTP 클라이언트(WCS→RCS)로 Backend/API 영역. 따라서 Backend/API.

## Verification Scenarios (Backend/API — mandatory)

### 이번 Phase가 건드리는 "엔드포인트" (아웃바운드 — WCS가 호출하는 대상)
> Phase 2는 인바운드 라우트를 신설하지 않는다. WCS가 **호출하는 아웃바운드 엔드포인트** + 그 호출을 **트리거하는 내부 변화원**을 검증 표면으로 둔다.
- **아웃바운드(WCS→RCS)**: `POST {RCS base}/api/v1/destination-status` — 가짜 RCS 수신 엔드포인트로 수신·단언.
- **트리거 경로(검증 진입점)**: ① 슈트 `ChuteCapacityService` 상태 변화(IF-05 예약/IF-10 투입/비움이 만재·정지 경계를 넘김) ② 소터 폴링 스냅샷 변화(online/CurFloor/Ready 전이 — 실 Sim3ds 또는 Fake 게이트웨이로 유도).

### Happy path per "엔드포인트" (expected trigger → expected push shape)
- **슈트 ready 전이 → 푸시**
  - `false→true`: 만재였던 슈트가 비움(`OnCleared`)/예약 취소로 받을 수 있게 됨 → 가짜 RCS가 `{chuteNo:<chute>, ready:true, timeStamp}` **정확히 1건** 수신.
  - `true→false`: 예약(`OnReserved`)으로 만재 경계 통과 또는 정지(PAUSED) → 가짜 RCS가 `{chuteNo:<chute>, ready:false, timeStamp}` **정확히 1건** 수신.
- **소터 ready 전이 → 푸시**
  - `false→true`: 소터가 미정렬/이동중(CurFloor≠운영층 또는 Ready=0)에서 정렬·준비(online && CurFloor==운영층 && Ready==1)로 전이 → `{chuteNo:<sorter>, ready:true, timeStamp}` **정확히 1건** 수신.
  - `true→false`: 준비 상태에서 분류 시작(Ready 1→0) 또는 OFFLINE 전이 → `{chuteNo:<sorter>, ready:false, timeStamp}` **정확히 1건** 수신.
- **RCS OK 응답 경로**: 가짜 RCS가 2xx/`{result:"OK"}` 반환 → WCS가 정상 처리(재시도 미발생·로그 정상).

### Relevant error / edge cases per "엔드포인트" (Planner가 적용분만 선택 — pad 금지)
- **무변화 → 푸시 0건(폭주 방지·핵심)**: ready 전이가 없는 N회 폴 / N회 무경계 이벤트(OnReserved로 만재 미도달 등) → 가짜 RCS 수신 **0건**. (전이 없는 폴마다 푸시하면 FAIL.)
- **동일 전이 중복 억제**: 같은 `ready=false`가 연속 두 번 산출(예: 만재 상태에서 추가 예약) → 첫 전이만 1건, 이후 0건.
- **동시 전이 → 전이당 1회 멱등(동시성·핵심)**: 슈트와 소터가 동시에 전이하거나, 같은 소터의 스냅샷 전이를 폴 스레드와 관찰 타이머가 동시에 감지 → chuteNo별 정확히 1건(중복 0·누락 0). 병렬 변화 유도 후 가짜 RCS 수신 카운트 단언.
- **RCS 미도달 → 재시도**: 가짜 RCS 연결 거부/5xx/타임아웃 → 재시도 정책 발동(설정 횟수/백오프) → 가짜 RCS 재개 후 푸시 도달. 재시도 소진 후 동작은 §질문 3 확정값(드롭이면 다음 전이까지 미전송, 큐잉/최신값이면 재개 시 최신 ready 1건).
- **재시도 상태 오염 없음**: 푸시 실패 후 동일 chuteNo가 같은 ready로 재전이 시도 시, 실패를 "성공한 직전 상태"로 오인해 누락하지 않음(실패 → 다음 산출에서 재시도 가능).
- **base URL 미설정/오설정**: §질문 4 확정값대로(예: 미설정 시 기동 실패 fail-loud vs. 푸시 비활성 경고). 검증은 그 확정 동작 단언.

---

## 사용자 확인 필요 (모호점 — Phase 2 진행 전 답변 요청)

> 아래는 스펙 HTML·Phase 1 코드만으로 단정할 수 없어 사용자 확정이 필요한 항목이다. **1·2·3·4·5는 Generator 착수 전 답이 필요**(설계 분기). 6은 기본 OUT으로 두되 확인.

1. **소터 `ready`에 full/paused를 접는가?** 스펙(`wcs_rcs_interface_kr.html` L126)은 3D 소터 `ready = !full && !paused && online && 정렬`이라 명시하나, **Phase 1 `DestinationStatusService.ComputeSorter`는 현재 소터 full(빈 셀 없음)·paused를 ready에 미반영**(`Full:false, Paused:false` 하드코딩 — [[m4p4-if08-cell-fullness]] 의도적 이연). Phase 2 푸시 `ready`는 (a) **Phase 1 산출 그대로**(online·정렬만 — full/paused 미반영, 셀만재 트랙은 별도 유지) 인가, 아니면 (b) **이번에 소터 full/paused를 ready에 접기**(ComputeSorter에 셀가용성·소터 PAUSED 추가 — Scope 확장)인가? **권고: (b)** — 스펙 정합(`ready`가 RCS의 유일 플래그인데 셀 만재인데 ready=true면 RCS가 만재 소터에 투입). 단 셀 만재 산출 결선이 추가 작업이라 사용자 확정 요청.

2. **재시도 정책 상세**: 재시도 (a) 횟수(예: 3회) (b) 백오프 방식(고정 간격 / 지수 백오프) (c) 간격/상한을 어떻게 둘까? 전부 설정값으로 외부화하되 **기본값**을 확정해야 한다. (예: 3회·지수 백오프·초기 500ms·상한 5s — 협의.)

3. **재시도 소진 후: 드롭 vs. 큐잉/최신값 유지?** RCS가 끝내 미도달이면 (a) **드롭**(해당 전이 푸시 포기 — 단 "직전 성공 ready"는 갱신 안 함 → 다음 전이 시 자연 재시도, 또는 다음 동일상태 전이에서 다시 보냄) (b) **최신값 유지·재개 시 1회**(RCS 복구되면 그 chuteNo의 현재 ready를 1회 푸시 — 중간 전이는 합쳐짐) 중 무엇인가? **권고: (b) 최신값 유지** — RCS가 복구되면 최종 상태로 수렴(누락된 전이로 RCS가 영영 stale 상태에 머무는 것 방지). 단 "전이당 1회"와의 상호작용(복구 푸시는 전이가 아니라 복구 트리거)을 사용자 확인.

4. **RCS base URL 미설정 시 동작**: 설정에 RCS base URL이 없으면 (a) **기동 실패(fail-loud)** (b) **푸시 비활성 + 경고 로그**(WCS는 정상 기동, 푸시만 no-op) 중 무엇인가? (개발/Sim 환경에선 RCS가 없을 수 있어 (b)가 편하나, 운영 누락을 조용히 넘기면 위험.) **권고: 설정 키 존재 시 활성·미설정 시 (b) 경고 후 비활성**(개발 편의) — 단 운영 appsettings엔 필수 표기. 사용자 확정.

5. **기동 시 초기 푸시(부트스트랩) 유무**: WCS 기동 직후 전 목적지의 현재 ready를 1회씩 RCS에 푸시할까(RCS가 부팅 직후 전체 상태를 알도록), 아니면 **전이가 발생할 때까지 푸시 0**(RCS가 첫 전이까지 모름)인가? **권고: 기동 시 전 목적지 1회 스냅샷 푸시 후 이후 전이만** — RCS가 초기 stale 상태에 머무는 것 방지. 단 RCS 미기동 시 재시도 폭주 우려(§4와 연동).

6. **하트비트(주기 keep-alive) 필요?** 스펙은 "선택적·협의"(L241). 기본은 **OUT**(전이 기반 푸시만)으로 두려 한다. RCS측이 "일정 시간 푸시 없으면 WCS 다운으로 간주" 같은 요구가 있으면 IN으로 전환(주기·간격 설정값). 사용자 확인.

---

> **Planner self-check** — Detected project type: Backend/API. Required scenario slots: 3 (endpoints touched [아웃바운드 `POST {RCS}/destination-status` + 트리거 경로 2: 슈트 ChuteCapacity 변화·소터 스냅샷 변화], happy path per endpoint [슈트 ready 전이 푸시·소터 ready 전이 푸시·RCS OK 응답], error/edge cases per endpoint [무변화 0건·동일전이 중복억제·동시전이 멱등·RCS미도달 재시도·재시도 상태오염 없음·base URL 미설정]). All slots filled: yes.

---

## 사용자 확정 (2026-06-24 — 진행 승인)
1. **소터 full/paused = 이연**(질문1=a): Phase 2는 **푸시 메커니즘에 집중**. 소터 `ready`는 Phase 1 `ComputeSorter` 산출 그대로(`online && CurFloor==운영층 && Ready==1`) 푸시 — 소터 full/paused(빈셀없음=셀만재 판정, [[m4p4-if08-cell-fullness]])는 **다음 전용 스프린트로 이연**. Phase 2에서 DestinationStatusService 소터 산출 변경 없음(Scope 확장 안 함). 슈트 ready는 기존대로 `!full && !paused`.
2. **재시도 기본값**(질문2): 횟수·백오프·간격 전부 **WcsOptions 설정화**(하드코딩 0). 기본값 = 3회 지수 백오프(예 1s/2s/4s, Generator가 합리값 + 주석). 고정 sleep 금지.
3. **재시도 소진 후 = 최신값 유지·복구 시 재푸시**(질문3=최신값): chuteNo별 "마지막으로 RCS에 성공 알린 ready"는 **실패 시 오염 금지**(실패한 푸시를 성공으로 간주 안 함 → 미알림 상태 유지). RCS 복구되면 현재 ready를 재푸시(다음 전이 또는 복구 감지 시 1회). 드롭 아님.
4. **RCS base URL 미설정 시 = 경고 + 푸시 비활성**(질문4): 기동 시 ILogger 경고, 푸시 best-effort 비활성(크래시 X). 운영 필수 설정으로 표기. 인바운드(IF-05/09/10)는 정상 동작.
5. **기동 시 초기 푸시 = 전 목적지 1회 스냅샷 후 전이만**(질문5): RCS URL 설정 시, 기동(또는 URL 설정 시점)에 모든 목적지 현재 ready 1회 푸시 → 이후 전이만. URL 미설정이면 비활성.
6. **하트비트 = OUT**(질문6): 전이 + 초기 스냅샷만. 주기 하트비트는 RCS 요구 시 후속.
