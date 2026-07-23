[Sprint Contract] — S-TWO-FLOOR-CONTROL 서브 스프린트 B (IF-08 층별 호스트 라우팅 · 소터 dual-host push · 부트스트랩 per-floor · IF-09 문서 정리 · A 이연 I-2/문서 sync)

> 선행: 서브 스프린트 A(층 판정 코어 — 층맵·소터별 FIFO 큐·TgtFloor==0 관측 기입·CurFloor 기준 readiness)는 develop 병합 완료(PR #76). 설계는 문서·병합 완료(PR #70·#71). 본 계약은 A 계약을 덮어쓴다(A는 이미 병합됨).
> 설계 권위(SOURCE OF TRUTH): docs/SPEC.md(§2-A·§2-C·§4-B·§5-A), docs/UpdateChuteState_API_EN.md("Per-floor host routing"), docs/API-RCS-DEPLOYED(-KR).md, docs/wcs_rcs_interface_kr.html, docs/wcs_rcs_3ds_master_spec.html. UpdateChuteState 요청/응답 계약(payload snake_case `{chute_numbers[], next_states[]}`·`next_state` 3=수용/2=불가·성공 `flag==1`·실패 `{result:"Failed"}`/비2xx)은 이 문서들이 정답 — 추측 금지.

---

## Goal

IF-08 아웃바운드 수용상태 푸시(WCS → RCS `PUT /api/UpdateChuteState`)를 **층별 호스트로 라우팅**한다. 현재는 단일 `BaseUrl` 하나로만 발신하나(S-IF08-READY-PUSH), 2층 제어(A)에서 목적지가 층을 갖게 됐으므로 목적지의 층에 해당하는 RCS 호스트로 push를 보낸다. 3D 소터는 정렬로 두 층을 겸하므로 **두 층 호스트 모두**에 발신한다(현재 정렬층=수용가능, 다른 층=수용불가). 기동 부트스트랩도 동일 규칙으로 per-floor 발신한다. 더불어 A에서 이연된 관측 루프 비용 정리(I-2)와 SPEC 문서 sync, IF-09 층 흔적 문서 정리를 흡수한다.

핵심 불변:
- 절대규칙 #1(PLC 쓰기 단일 큐)·#3(WCS TgtFloor 클리어 금지)·#5(OFFLINE은 WCS 판단)·#7(모든 호스트·주기·타이밍은 appsettings — 하드코딩 금지 · 특히 `192.168.0.151/152:3000`을 코드에 박지 말 것)·#8(판정은 Wcs.Core 순수 함수) 준수.
- **Modbus 레지스터 맵 불변.** PLC 쓰기 경로(단일 쓰기 큐) 로직 무변경 — I-2는 관측 루프가 게이트에 넘기는 `hold` 산출만 건드린다.
- **UpdateChuteState wire 계약 불변**: 경로 `PUT /api/UpdateChuteState`·payload snake_case·`next_state` 3/2·성공 판정(2xx && flag==1)은 층 무관 동일. 층은 **오직 어느 호스트가 수신하느냐**로만 전달된다.
- 목적지당 "전이당 1회 멱등"(중복 0·누락 0)·DORMANT no-op·Fail-Loud(실패 명시 로깅) 성질은 dual-host 확장 후에도 보존.

## Detected Project Type: Full-stack

리포 신호: 브라우저 진입점(`frontend/` — Vite/React B2C·모니터링 UI) + 서버 라우트/컨트롤러(`backend/src/Wcs.Api/Controllers/*`)가 한 리포에 공존 → Full-stack. **단 본 스프린트는 backend outbound-push 전용**(프론트 표면 무접촉). 따라서 Web/UI 시나리오 슬롯은 N/A(사유 명시), Backend/API 슬롯 전수, E2E는 크로스레이어(WCS 상태 전이 → 층별 호스트 HTTP push)로 채운다.

## Implementation Scope (Generator가 구현할 WHAT — HOW는 Generator 재량)

1. **층별 호스트 설정화 (하드코딩 금지)**
   - `ChuteStatePushOptions`의 단일 `BaseUrl`을 **층→호스트 맵**으로 대체·확장한다(예: `Wcs:ChuteStatePush:FloorHosts` 형태 — 정확한 키/구조는 Generator 재량, 단 appsettings 바인딩 가능·문서화 필수). 1층 `http://192.168.0.151:3000` · 2층 `http://192.168.0.152:3000`은 **appsettings 값**으로만 존재(코드 리터럴 0).
   - 경로(`Path` = `/api/UpdateChuteState`)·재시도/백오프/타임아웃/관찰 주기 옵션은 층 공통(현행 유지). 값은 층별로 다르지 않다(호스트만 다름).
   - **DORMANT 성질을 층별로 보존**: 어떤 층의 호스트가 미설정(null/공백)이면 그 층으로의 push는 no-op(HTTP 시도 0·크래시 0). 모든 층이 미설정이면 현행과 동일하게 서브시스템 전체 DORMANT(경고 후 관찰/구독 미기동). (질문 Q4 확정 대기 — 아래.)
   - 현재 `BaseUrl`은 출하 시 null(DORMANT)이므로 실 운영 파괴 없이 교체 가능. 구 단일 키의 처리(제거 or 하위호환 alias)는 Generator가 결정하되 문서에 명기.

2. **층별 라우팅 발신 (전이·부트스트랩 공통 규칙)**
   - **고정(비3D) 슈트**(`Destination.Floor` 비-NULL): 자기 층(`Floor`) 호스트 **한 곳**에만 push. `next_state` = 수용가능(`!full && !paused`)이면 3, 아니면 2. (현행 accept 합성 = `Ready && !Paused` 재사용.)
   - **3D 소터**(`Destination.Floor == NULL`, 정렬로 두 층 겸용): **두 층 호스트 모두**에 push.
     - 현재 `CurFloor` 층 호스트 = 운영 수용가능(`DestinationStatusService.Compute().Ready` — 이미 CurFloor 기준·A)이면 `next_state=3`, 아니면 2.
     - **다른 층 호스트 = 항상 `next_state=2`**(그 층엔 지금 정렬돼 있지 않아 수용 불가).
     - 소터 OFFLINE(스냅샷/번들 없음)·CurFloor 불명이면 **두 층 호스트 모두 2**(SPEC §2-A 부트스트랩 규칙과 동형).
   - 층은 `{1, 2}` 두 값(설정된 호스트 키 집합). "다른 층"은 CurFloor가 아닌 나머지 설정 층.

3. **전이 추적을 층별 호스트 단위로 확장 (멱등 보존)**
   - 소터가 CurFloor를 1→2로 바꾸면 **정확히 두 전이**가 발생해야 한다: 1층 호스트에 `2`(더 이상 서비스 안 함) 1회 + 2층 호스트에 `3`(새로 서비스) 1회. 각 (목적지, 층 호스트) 조합의 `Acked`를 분리 추적해 "전이당 1회"(중복 0·누락 0)를 dual-host에서도 보장한다.
   - 성공만 Acked 갱신·실패 시 stale 유지→다음 관찰 재푸시(현행 복구 하트비트 성질) — 이를 층별 호스트마다 독립 적용.
   - per-destination 락·PushInFlight·Observe→Pump 단일 경로 등 S-IF08-READY-PUSH의 동시성 멱등 구조는 유지(단일 발신 소스 원칙 불변 — 같은 (dest,floor)에 모순값 동시 발신 불가).

4. **부트스트랩(기동) 전체 상태 dual-host 발신**
   - 기동 시(층 호스트 설정된 경우) 전 활성 목적지의 현재 수용상태를 목적지당 1회 발신한다.
   - 고정 슈트 = 자기 층 호스트 1곳. 소터 = 두 층 호스트 모두(현재 CurFloor 호스트=수용 시 3, 다른 층=2; CurFloor에서도 미수용/오프라인이면 둘 다 2).
   - 부트스트랩도 §2번 라우팅 규칙을 그대로 탄다(전이·부트스트랩 동일 경로).

5. **[A 이연 흡수] I-2 — 관측 루프 write-gate 비용 정리**
   - `SorterFloorReturnService.ObserveSorter`의 정렬 기입 판단이 **매 유휴 틱마다 `DestinationStatusService.Compute()`(→ `ComputeSorterFull` DB 집계: cell/cell_assignment/sorter_command/piece 다중 쿼리)를 호출**하는 비용을 제거한다. 쓰기 게이트에 넘길 `hold`는 **Paused(그리고 Online — 이미 `snap.Online`으로 선판정)**만 필요하도록 정리한다(무거운 셀 만재 집계를 매 틱 돌리지 않음).
   - **⚠ 절대규칙 #2 정합 필수**: 현행 게이트는 `hold==Full`이면 기입을 차단한다(SPEC §2-A 표 6행·절대규칙 #2 "FULL이면 안 씀"). Full 산출을 게이트에서 빼면 "만재 소터의 정렬 기입"이 허용되도록 **동작이 바뀐다**. 이는 A의 2단계 게이트 분리(수용 판정=IF-05 dispatch / 물리 정렬=관측 루프)와 정합하나 **명문 규칙 변경**이므로, 질문 Q5(아래) 확정 + SPEC §2-A/절대규칙 문언 sync로 닫는다. (구현 정합≠사용자 재확인 면제 — feedback-archive A 교훈.)
   - Wcs.Core(`DepositDecider`) 순수성·시그니처 불변. PLC 단일 쓰기 큐 경로 불변(#1). 변경은 관측 루프의 hold 산출부 한정.

6. **[A 이연 흡수] SPEC §2-A/§2-C 문서 sync**
   - A에서 관측 루프가 **유휴(Ready=1)에서만 기입**(분류 중 Ready=0 선기입 폐지)으로 바뀌어, SPEC §2-A 판정표 **4행("Ready=0 && TgtFloor==0 → F 기입(분류 후 복귀 선기입)")이 관측 루프에선 dead output**이다(`DepositDecider`는 여전히 그 write를 산출하지만 `SorterFloorReturnService`가 Ready=0에선 게이트를 호출하지 않음). 이 괴리를 §2-A/§2-C에 명시(판정 함수의 순수 산출 vs 트리거의 실제 호출 조건 구분).
   - I-2 결정(FULL 게이트 제거)이 확정되면 §2-A 표 6행·절대규칙 #2도 함께 sync.
   - 문서는 코드 확정 후 코드와 일치하도록 갱신(문서 먼저 쓰고 코드가 어긋나는 일 금지).

7. **[문서 위주] IF-09 DTO/문서 층 표기 정리**
   - 현행 `ArrivalReportRequest(PId, ChuteNo, AgvNo, TimeStamp)`에 **층 필드는 이미 없음**(확인 완료). DTO 변경은 원칙적으로 불요.
   - 남은 IF-09 관련 문서(docs/API-RCS-DEPLOYED(-KR).md, wcs_rcs_interface(_kr).html, SPEC §3)에 "층은 IF-05 인덕션에서 확정·IF-09엔 층 없음"이 일관 반영됐는지 확인하고, A에서 제거된 정렬 트리거의 잔여 층 흔적(주석·설명)을 정리한다.
   - `appsettings.json`의 stale 주석(`agvFloor 산출 — 미확정 … IF-09 도착 기록`)이 현 설계와 어긋나면 정리(코드 동작 무변경 — 주석/문서만).

**Scope OUT (건드리지 말 것 — 후속 스프린트):**
- Sub-Sprint C: R-clear 재설계·처리시각 3종·마이그레이션·재시작 레지스터 클리어(§4-B)·I-3(인메모리 큐 재시작 복원)·스톨 감지기.
- Sub-Sprint D: 파킹존.
- Modbus 레지스터 맵·PLC 단일 쓰기 큐 로직·핸드셰이크(IF-11/12)·IF-05/IF-10 판정 로직.
- 프론트엔드(frontend/) 일체 — 본 스프린트 무접촉.

## Parallel Modules
N/A (single module). 설정·라우팅 클라이언트·pusher·문서 sync가 상호의존적인 단일 push 서브시스템 — 파일 경계 분리가 깔끔하지 않다. 단일 Generator.

## Evaluation Dimensions
functional only. 단, functional 검증에 **동시성/멱등**(dual-host 전이당 1회, CurFloor 변화 시 정확히 두 전이)과 **DORMANT/부분실패 격리**를 필수 포함(별도 병렬 Evaluator로 분리할 만큼 직교하지 않음 — 한 Evaluator가 Backend/API 4기준으로 커버).

## Evaluation Criteria (Backend/API — 4기준, Evaluator가 회의적으로 판정)

1. **API Design Quality (★★★)** — 층별 라우팅이 wire 계약을 오염시키지 않는가: 두 호스트 모두 `PUT /api/UpdateChuteState`·동일 snake_case payload·`next_state` 3/2 동일. 층은 호스트 선택으로만 전달(payload에 층 필드 유입 0). 설정 스키마가 명료하고 DORMANT를 층별로 표현 가능.
2. **Architecture Originality (★★★)** — 단일 발신 소스·전이당 1회 멱등 구조를 dual-host로 확장하되 이중/모순 발신 경로를 새로 만들지 않음. (dest, floor-host) 단위 전이 추적이 기존 Observe→Pump 단일 경로에 자연스럽게 얹힘. I-2가 Core 순수성·단일 쓰기 큐를 보존.
3. **Craft (★★)** — Fail-Loud(호스트별 실패 명시 로깅·operation_log), 층별 독립 재시도, 한쪽 호스트 다운이 다른 층 발신을 막지 않음, 취소/teardown 경쟁 방어, 입력 위생(설정 누락·미매핑 층). appsettings 하드코딩 0.
4. **Functionality (★★)** — 아래 Verification Scenarios가 실제 fake 수신 서버 왕복으로 전부 통과. 회귀 0(기존 push 테스트군·E2E·전체 스위트 GREEN). SPEC/문서가 코드와 일치.

## Completion Conditions (Evaluator PASS 최소 조건)

- Verification Scenarios(아래) 전 항목이 **fake HTTP 수신 서버(층당 1대, 총 2대)의 실제 수신 payload**로 실증(코드 리뷰 대체 금지). 실제 `192.168.0.151/152` 미접속 — fake 수신 서버 기동으로 검증("인프라 미실행≠스킵": fake 서버를 직접 띄운다).
- 자동화 테스트(xUnit)로 재현 가능(1회성 명령 아님). 신규 실-Kestrel/타이밍 민감 테스트군은 결정성 확인차 `--filter` ≥5회 반복 GREEN(S9/testhost teardown 이력 대비 — 계약 관행).
- 전체 스위트 독립 재실행 GREEN(Generator 보고 불신 재실행). 기존 push 테스트군(`ChuteStatePushTests`·`SorterPushOperationalTests`·`RcsPushTests`·`ChuteRecoveryPushHeartbeatTests`·`B2cChutePushTests`) 회귀 0.
- 정적 검사(build 0/0 신규 경고, 마이그레이션 0 — 스키마 무변경 확인) 독립 실행 기록.
- 절대규칙 #1/#3/#7/#8 위반 0 코드 직독 확인: PLC 단일 쓰기 큐 diff 0(I-2는 hold 산출만), 호스트 리터럴 grep 0(`192.168` 코드 매치 0 — appsettings만), Wcs.Core diff 0.
- SPEC §2-A/§2-C·UpdateChuteState 문서·IF-09 문서가 최종 코드와 일치.
- 질문 Q1~Q5가 사용자 확정으로 닫힘(미확정 상태로 구현 진입 금지 — 특히 Q5는 절대규칙 문언 변경).

---

## Verification Scenarios (Full-stack — 필수, 빈 슬롯=위반)

### === Web/UI 시나리오 (프론트 표면) ===
**N/A (사유 명시)** — 본 스프린트는 backend outbound-push 전용으로 `frontend/` 표면을 일절 건드리지 않는다. 무접촉 증거로 `git diff develop --stat -- frontend/` 빈 출력을 Evaluator가 확인(브라우저 검증 불요). 프론트 회귀 검증은 코드 diff 0 실증으로 갈음.

### === Backend/API 시나리오 (outbound push — 층당 fake 수신 서버 2대로 검증) ===

**대상 "엔드포인트"(아웃바운드 — WCS가 호출하는 RCS 계약):**
- `PUT http://<1층호스트>/api/UpdateChuteState`  (설정값, 테스트=fake 서버 A)
- `PUT http://<2층호스트>/api/UpdateChuteState`  (설정값, 테스트=fake 서버 B)
- (인바운드 WCS 엔드포인트 변경 없음.)

**Happy path (전이별 라우팅 — 수신 서버 payload로 단언):**
- VS-B1 **고정 슈트 1층 전이**: 1층 배정 고정 슈트를 PAUSE 전이 → fake 서버 A(1층)만 `{chute_numbers:[n], next_states:[2]}` 정확히 1건 수신, fake 서버 B(2층) 수신 0. RESUME 시 A만 `[3]` 1건.
- VS-B2 **고정 슈트 2층 전이**: 2층 배정 고정 슈트 전이 → fake 서버 B만 수신, A 수신 0(층 라우팅 대칭 실증).
- VS-B3 **소터 dual-host(정렬 1층·ready)**: 소터 CurFloor=1·운영 수용가능 → 서버 A(1층) `next_state=3` 1건 + 서버 B(2층) `next_state=2` 1건(둘 다 정확히 1건). payload는 두 호스트 동일 형식·층 필드 없음.
- VS-B4 **소터 CurFloor 전이(1→2)**: 소터가 1층에서 2층으로 재정렬 → 서버 A에 `2`(서비스 중단) 1건 + 서버 B에 `3`(새 서비스) 1건. 각 (dest,floor) 전이당 정확히 1건(중복 0). 무변화 관찰 폴에서 추가 발신 0(stableCount 유지).
- VS-B5 **부트스트랩 dual-host**: 기동 시 전 목적지 1회 발신 — 소터는 A·B 모두(CurFloor 호스트=ready면 3/아니면 2, 다른 층=2), 고정 슈트는 자기 층 호스트 1곳. 각 목적지·호스트 조합 정확히 1건.

**Error / 경계 케이스 (Planner가 적용 대상만 선정 — 패딩 금지):**
- VS-B6 **한쪽 층 호스트 다운(부분 실패 격리)**: 1층 fake 서버 연결 거부 + 2층 정상. 소터 dual-host 전이 시 → 2층은 정상 수신(3 또는 2), 1층은 재시도 소진 후 실패 명시 로깅(operation_log FAIL)·Acked stale 유지, **2층 발신은 1층 실패에 영향 없음**. 이후 1층 서버 복구 시 다음 관찰에서 재푸시 도달(복구 하트비트 성질 dual-host 보존).
- VS-B7 **층별 DORMANT 보존**: 1층 호스트만 설정·2층 미설정 → 1층 목적지 push 정상, 2층 목적지(및 소터의 2층 호스트분) push no-op(HTTP 시도 0·크래시 0), 인바운드(IF-05/09/10) 정상 회귀 0. 전 층 미설정 → 서브시스템 전체 DORMANT(수신 서버 총 수신 0·기동 크래시 0).
- VS-B8 **소터 오프라인/미수용 → 두 층 모두 2**: 소터 번들 없음(OFFLINE) 또는 CurFloor에서도 미수용(paused/ready=0) → 두 층 호스트 모두 `next_state=2` 수신(단일 층 3 누출 0).
- VS-B9 **wire 계약 불변(회귀 가드)**: 두 호스트 수신 RawBody가 snake_case 키(`chute_numbers`/`next_states`) Contains ∧ camelCase(`chuteNumbers`/`nextStates`) DoesNotContain, HTTP 메서드=PUT, 성공 판정=2xx&&flag==1. 층 라우팅이 payload를 오염시키지 않음(층 필드 유입 0).

### === End-to-End 크로스레이어 시나리오 (2+ 레이어 데이터 흐름) ===
- VS-E1 **PLC 스냅샷 전이 → 층별 호스트 HTTP push(실 Sim3ds + fake RCS 2대)**: Sim3ds를 구동해 소터를 1층 정렬(CurFloor=1, Ready=1)→분류/재정렬로 CurFloor=2로 물리 전이시킨다. 흐름: 게이트웨이 폴링 스냅샷 → `DestinationStatusService.Compute`(CurFloor 기준 readiness) → `DestinationStatusPusher` 층별 라우팅 → 층별 HTTP 클라이언트 → fake RCS 서버 A/B. 단언: CurFloor=1 구간엔 A=3·B=2, 재정렬 후 A=2·B=3가 각 호스트 수신 이력에 정확히 순서대로 나타남. (레이어: Modbus 스냅샷 → 판정 서비스 → push 라우팅 → HTTP.)
- VS-E2 **I-2 동작 — 만재 소터 정렬 기입(Q5 확정 시)**: 소터를 SorterFull=true(전 셀 만재)·유휴(Ready=1)·미정렬(CurFloor!=큐 머리 F) 상태로 만들고 관측 루프 1주기 경과. Q5 확정이 "FULL은 정렬 기입 차단 안 함"이면 → TgtFloor에 F 기입이 단일 쓰기 큐로 발생(만재여도 물리 정렬 진행). 동시에 관측 루프가 매 틱 `ComputeSorterFull`(셀 집계 쿼리)를 호출하지 않음을 증거(쿼리 카운트/로그 또는 코드 경로)로 확인. Paused 소터는 여전히 미기입(#2 유지). (레이어: 관측 루프 트리거 → Core 게이트 → 단일 쓰기 큐 → Sim 레지스터.)

> Planner self-check — Detected project type: Full-stack. Required scenario slots: 3 (Web/UI-frontend[N/A-사유], Backend/API-outbound-push, E2E-cross-layer). All slots filled: yes.

---

## 질문 (사용자 확인 필요 — 추측 금지 · Planner 권고 포함)

- **Q1. 부트스트랩 시 고정(비소터) 슈트 발신 범위** — SPEC §2-A/§5-A는 "전 활성 목적지 1회, 고정 슈트=자기 층 호스트 1곳"으로 이미 확정. **권고: 문서대로 전 활성 목적지(슈트+소터) 부트스트랩, 고정 슈트는 `Destination.Floor` 호스트 1곳.** 확인만 요청.
- **Q2. dual-host push 실패 시 처리** — push 실패는 소터/슈트를 OFFLINE으로 전이시키지 않는다(OFFLINE은 PLC 통신 판단 전용, 절대규칙 #5). **권고: 현행 client 성질 유지 — 층별 독립 지수 백오프 재시도(설정값) → 소진 시 Fail-Loud 로깅·Acked stale·다음 관찰 재푸시. push 실패가 상태 전이를 유발하지 않음.** 확인 요청.
- **Q3. 층별 호스트 중 한쪽만 살아있을 때** — **권고: 각 층 호스트 완전 독립 — 도달 가능한 호스트는 정상 발신, 불가 호스트는 재시도/stale, 교차 영향 0(VS-B6).** 한쪽 다운이 다른 층 발신을 지연/차단하지 않음. 확인 요청.
- **Q4. DORMANT를 층별 맵에서 어떻게 보존** — **권고: 층별 DORMANT — 층 호스트 미설정이면 그 층만 no-op, 나머지 층 정상; 전 층 미설정이면 서브시스템 전체 DORMANT(현행과 동일 — 관찰/구독 미기동·크래시 0)(VS-B7).** 부분 설정(1층만 설정) 허용 여부 포함 확인 요청.
- **Q5. [중요 — 절대규칙 문언 변경] I-2가 FULL 게이트를 제거하는가** — 현재 관측 루프 정렬 기입 게이트는 `hold==Full`이면 기입 차단(SPEC §2-A 표 6행·절대규칙 #2 "FULL/PAUSED/OFFLINE이면 안 씀"). I-2("쓰기 판단엔 Paused/Online만 필요")를 문자 그대로 적용하면 **만재 소터도 정렬 기입이 허용**된다(동작 변경). **권고: 승인** — 근거: (a) 수용 판정은 IF-05 dispatch가 이미 per-piece로 게이트(A의 2단계 게이트 분리), 큐에 든 피스는 이미 수용 확정분; (b) 목적지-단위 SorterFull은 "이 피스의 셀"이 아니라 "아무 피스도 못 받음"이라 정렬(물리 이동)까지 막으면 확정 피스를 고립; (c) 비용(매 유휴 틱 다중 DB 집계 쿼리) 제거가 실익. **단 승인 시 절대규칙 #2 문언·SPEC §2-A 표 6행을 "FULL은 IF-05 dispatch만 차단, 관측 루프 정렬 기입은 Paused/Offline만 차단"으로 sync**(scope 항목 6). 거부(FULL 유지) 시 I-2는 "동작 불변 + 집계 호출만 저비용화"로 축소 — Generator에 재지시 필요. **사용자 결정 필수.**

---

## 참고 — Generator가 반드시 읽을 현행 코드·문서
- `backend/src/Wcs.Api/Services/ChuteStatePushClient.cs` (단일 BaseUrl·named client·재시도·DORMANT no-op).
- `backend/src/Wcs.Api/Services/DestinationStatusPusher.cs` (전이 감지·DestState Computed/Acked/PushInFlight·부트스트랩·복구 하트비트·Observe→Pump 단일 경로).
- `backend/src/Wcs.Api/Services/DestinationStatusService.cs` (`Compute`/`ComputeSorter` — A에서 CurFloor 기준; `ComputeSorterFull` = I-2 대상 집계).
- `backend/src/Wcs.Api/Services/SorterFloorReturnService.cs` (I-2 대상 — `ObserveSorter`의 `_status.Compute` 호출부).
- `backend/src/Wcs.Api/Infrastructure/WcsOptions.cs` (`ChuteStatePushOptions` — 단일 BaseUrl·IsEnabled·DORMANT).
- `backend/src/Wcs.Api/Program.cs` (AddHttpClient "ChuteStatePush" 결선·pusher 등록).
- `backend/src/Wcs.Data/Entities.cs` (`Destination.Floor int?` — 3D=NULL, 라우팅 키).
- `backend/src/Wcs.Api/Dtos/Dtos.cs` (`ArrivalReportRequest` — 층 필드 이미 없음).
- docs/SPEC.md §2-A·§2-C·§4-B·§5-A, docs/UpdateChuteState_API_EN.md(Per-floor host routing).
- 테스트 패턴: `FakeChuteStateServer`(S-CHUTESTATE-PUSH/S-IF08-READY-PUSH — Kestrel 동적 포트·RawBody 기록·app.Map 전 메서드) 를 **층당 2대**로 확장.
- lessons.md(worktree·SqlServer provider override·delta baseline·arming·RFlag), feedback-archive.md(A 서브스프린트·S-IF08-READY-PUSH·S-CHUTESTATE-PUSH 항목) 필독.

---

## ✅ 확정 결정 (사용자 게이트 — 2026-07-23)

계약·분할 **승인**(B 진행). 미확정 5건 확정:
- **Q1** 부트스트랩 범위 → **전 활성 목적지 1회**(고정 슈트=자기 층 호스트 1곳, 소터=dual-host). SPEC 권고대로.
- **Q2** dual-host push 실패 → **OFFLINE 전이 안 함**(절대규칙 #5). 층별 독립 지수 백오프 재시도 → 소진 시 Fail-Loud 로깅·Acked stale·다음 관찰 재푸시.
- **Q3** 한쪽 층 호스트만 살아있을 때 → **각 층 호스트 완전 독립**, 교차 영향 0(VS-B6).
- **Q4** DORMANT → **층별 DORMANT**(층 미설정=그 층만 no-op, 부분설정 허용; 전 층 미설정=서브시스템 전체 DORMANT).
- **Q5 [절대규칙 문언 변경] 승인** → 관측 루프 정렬 기입 게이트는 **Paused/Offline만 차단, FULL은 차단하지 않음**. FULL(만재)은 IF-05 dispatch(수용 판정)에서만 차단. 근거: 큐 피스는 이미 수용 확정분 — 만재로 물리 정렬까지 막으면 확정 피스 고립.
  - **절대규칙 #2 문언(CLAUDE.md)은 오케스트레이터가 사용자 승인 하에 직접 정정**(에이전트 보호 파일). Generator는 **CLAUDE.md 미변경**, 대신 **SPEC §2-A 표 6행·§2-C**를 "FULL은 IF-05 dispatch만 차단, 관측 루프 정렬 기입은 Paused/Offline만 차단"으로 sync.
