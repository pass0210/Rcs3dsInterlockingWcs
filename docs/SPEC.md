# WCS 응축 스펙 (코드 기준 문서)

원본: docs/*.html 4종. 충돌 시 HTML(최신 확정본)이 우선.

> **[2026-07-21 설계 변경 — 인덕션 기반 2층(1·2층) 제어]** 이 결정은 **2026-06-22 "운영 2층 고정(TgtFloor 항상 2)" 결정을 대체(supersede)한다.**
> 이제 목표 층 **F는 인덕션 번호에서 파생**한다(설정 `InductionFloorMap`: inductionNo→floor, 예 `{"1":1,"2":1,"3":2}`). TgtFloor(D6)에는 상수 2가 아니라 **인덕션 파생 층 F(1 또는 2)**를 쓴다.
> IF-05 목적지 조회는 소터 목적지 피스를 **소터별 pending-floor FIFO 큐에 enqueue**하고 OK를 응답한다(층은 인덕션에서 확정). TgtFloor 쓰기는 "피스마다 자기 IF-05 순간에 1회"가 아니라, WCS가 **`TgtFloor==0`을 관측할 때 큐 머리(head) 피스의 층 F를 게이트**(`TgtFloor==0 && (CurFloor!=F || Ready==0)`)로 기입한다 — PLC가 분류 시작(Ready 1→0) 시 TgtFloor=0으로 클리어하면 WCS가 그 순간 큐의 다음 층을 즉시 기입하고 소터가 그 층으로 **복귀**한다("타겟층을 순서대로 써준다"). 쓰기는 **`TgtFloor==0` 게이트 관측으로 트리거**되고 층은 **큐가 공급**한다(sort-completion 이벤트가 아님). 핑퐁 차단·"WCS는 클리어 안 함, PLC가 분류 시작 시 클리어" 규칙은 **불변** — 값만 상수 2에서 인덕션 층 F로 바뀐다. (§2-C.)
> 소터 readiness·IF-08 push는 **층 파라미터화**(`CurFloor==F`)되며, IF-08 push는 **층별 호스트**로 라우팅한다(1층 `http://192.168.0.151:3000` · 2층 `http://192.168.0.152:3000`). IF-09 도착 보고에는 **층 필드가 없다**(층은 IF-05에서 이미 확정).
> 2층 고정의 과거 근거는 삭제하지 않고 "폐지됨"으로 표기한다. → 상세: §2·§3·§4·§5.

## 1. 레지스터 맵 (Holding Register, FC03 읽기 / FC06·FC16 쓰기 — Coil 미사용)
| 이름 | 주소 | 방향 | 설명 |
|---|---|---|---|
| C_CellNo | D0 | WCS write | 지정할 셀 번호 (IF-11) |
| C_Seq    | D1 | WCS write | 명령 순번(매 건 증가) |
| R_CellNo | D2 | PLC write | 실제 적재한 셀 (IF-12) |
| R_Seq    | D3 | PLC write | 처리한 순번 (= 받은 C_Seq) |
| D4.0 C_Flag | D4 bit0 | WCS set / PLC clear | C 영역 유효 |
| D4.1 R_Flag | D4 bit1 | PLC set / WCS clear | R 영역 유효 |
| D4.2 Ready  | D4 bit2 | PLC set | 1=수용 가능(정지·비분류) / 0=분류 중 **또는 이동 중** |
| CurFloor | D5 | PLC write | **현재 정렬된 층(1/2)** — 소터 한 대가 정렬로 두 층을 겸하며, CurFloor 층에서만 수용 가능. 도착 시 기입 |
| TgtFloor | D6 | WCS write / **PLC clear** | 목표 층 **F(1/2, 인덕션 파생)**. 0=명령 없음. IF-05에 enqueue → WCS가 **`TgtFloor==0` 관측 시 큐 머리 층 기입·소터 복귀**(§2-C · 과거 "항상 2" 폐지 — 2026-07-21) |

D4는 한 워드 — 한 비트만 바꿀 땐 D4 읽기→비트 수정→쓰기(RMW). 쓰기는 단일 큐에서만.

## 2. IF-08 투입 가부 판정

> **IF-08 모델 변경(재설계 Phase 1)**: RCS 폴링 `deposit-permission` 엔드포인트는 **폐지**되고 **WCS→RCS 상태 푸시**(키=chuteNo·단일 `ready`)로 대체됐다 — `docs/wcs_rcs_interface_kr.html` §6·`master_spec §06` 참조. 아래 판정표(2-A/2-B)는 **삭제된 게 아니라 내부 `DepositDecider` 판정 스펙으로 유효**하다: 소터 push `ready` 산출·**IF-05 인덕션 파생 층 정렬** 판정의 근거로 쓰인다(투입 가부의 상류 차단은 IF-05 dispatch로 이동 — §3 IF-05·`RcsController.QueryDestination`).

### 2-A. SORTER_3D 경로 (Wcs.Core.DepositDecider — 순수 함수)
입력: PlcSnapshot(레지스터+Online), **floor F(int, 인덕션 파생 — IF-05 요청 inductionNo→`InductionFloorMap`)**, WcsHold(None/Full/Paused)
우선순위: Offline → Full/Paused → Ready/층 비교. **F는 상수 2가 아니라 인덕션 층(1 또는 2)**(2026-06-22 "항상 2" 폐지).

| # | 조건 | allowed | reason | TgtFloor 쓰기 |
|---|---|---|---|---|
| 1 | Online && Hold=None && Ready=1 && CurFloor==F (TgtFloor 무관 — 이동완료 후 잔류 ≠0 포함) | true | None(와이어 reason="READY") | 안 씀 |
| 2 | Ready=1 && CurFloor≠F && TgtFloor==0 | false | WRONG_FLOOR | **F 기입** |
| 3 | Ready=1 && CurFloor≠F && TgtFloor≠0 | false | WRONG_FLOOR | 안 씀(핑퐁 차단) |
| 4 | Ready=0 && TgtFloor==0 (층 무관) | false | BUSY | **F 기입**(분류 후 복귀 선기입) |
| 5 | Ready=0 && TgtFloor≠0 | false | BUSY | 안 씀 |
| 6 | Hold=Full / Paused | false | FULL / PAUSED | 안 씀(대기) |
| 7 | !Online | false | OFFLINE | 안 씀 |

TgtFloor 쓰기 조건 한 줄: `TgtFloor==0 && (CurFloor!=F || Ready==0)` → **F 기입** — 단 Hold/Offline이면 항상 안 씀. 이 판정의 `F`는 **소터별 pending-floor 큐의 머리 피스 층**이다(§2-C).
**쓰기 트리거**: IF-05는 소터 목적지 피스를 큐에 **enqueue**만 한다(OK 응답). 실제 TgtFloor 쓰기는 **소터가 free(TgtFloor==0)일 때 큐 머리 층 F**를 위 게이트로 기입 — 피스마다 자기 IF-05 순간에 쓰는 게 아니다(과거 IF-09 도착 트리거 폐지). 쓰는 값만 상수 2→인덕션 층 F로 바뀌고, 게이트·순서·핑퐁 차단은 동일.
클리어: WCS는 절대 안 함. PLC가 분류 시작(Ready 1→0) 시 0으로(도착 시엔 CurFloor만 기입·TgtFloor 유지).

**소터 물리 모델(2층 겸용)**: 소터 한 대가 **정렬로 두 층(1·2)을 겸한다**. CurFloor = 현재 정렬된 층이고 그 층에서만 수용하므로, **어느 순간이든 최대 한 층에만 ready**. 층 F에 대한 readiness = `online && CurFloor==F && Ready==1 && !paused`(셀 만재는 push에 반영 안 함 — IF-05 dispatch에서만 차단).

**IF-08 push 층별 호스트 라우팅**: 상태가 바뀐 목적지의 **층**으로 라우팅한다 — 1층 호스트 `http://192.168.0.151:3000` · 2층 호스트 `http://192.168.0.152:3000`(경로 `PUT /api/UpdateChuteState`·페이로드 snake_case `{chute_numbers[], next_states[]}`·`next_state` 3=수용/2=불가 불변). 3DS 소터(두 층 겸용)는 **현재 CurFloor의 층 호스트**로 push — 층 F로 정렬·ready면 **F층 호스트에 `next_state=3`**, **다른 층 호스트엔 사실상 `2`**(그 층엔 지금 서비스 안 함). 고정(비3D) 슈트는 **자기 층 호스트**로 push. (호스트 값·매핑은 §5-A 설정.)

**기동 시 부트스트랩(전 목적지 1회)**: 프로그램 시작 시(층별 호스트 설정 시) WCS는 **전 목적지의 현재 수용 상태를 목적지별로 1회 푸시**한다(`DestinationStatusPusher.StartAsync`). **소터는 두 층 호스트 모두에 발신**(현재 CurFloor 호스트=`3` 수용 시(online·!full·!paused·Ready=1) / 다른 층 호스트=`2`; CurFloor에서도 미수용이면 두 호스트 모두 `2`), **고정 슈트는 자기 층 호스트 1곳**(`!full && !paused`면 `3`, 아니면 `2`). 이후로는 **상태 전이 시에만** 푸시(같은 dual-host 규칙 — 부트스트랩·전이 동일). 2층이므로 부트스트랩도 per-floor 라우팅이 필요하다(현재 단일 BaseUrl 전제 → 층별 라우팅 결선은 코드 스프린트).

### 2-B. CHUTE 경로 (M4-P2a 신설)
입력: WcsHold(IChuteCapacityService.GetHold) — PLC 스냅샷·층(F) 미사용, TgtFloor 쓰기 없음.

| 조건 | allowed | reason |
|---|---|---|
| destination 미존재 또는 비활성 | false | PAUSED |
| destination.Status == PAUSED | false | PAUSED |
| hold == Full | false | FULL |
| hold == Paused (용량 집계) | false | PAUSED |
| hold == None (정상) | true | READY |

FULL 판정: `SUM(piece.qty WHERE deposited_at > last_cleared_at) + in-flight(RESERVED/PERMITTED)qty >= work_full_qty`
(cur_qty 컬럼 없음 — 집계는 piece 테이블이 단일 진실. 인메모리 캐시: ChuteCapacityService 싱글톤)

### 2-C. 소터별 pending-floor 큐 (타겟층을 순서대로 — 2026-07-21 확정)
소터로의 dispatch는 물리적으로 직렬(§6)이므로, 소터마다 **pending-floor FIFO 큐** 하나를 둔다.
- **enqueue**: IF-05 목적지 조회가 소터 목적지 피스를 판정해 OK면, 그 피스(층 F=인덕션 파생)를 해당 소터 큐에 **IF-05 순서대로** 넣는다. (OK/BUSY→진행 철학 동일.)
- **write-in-order**: WCS는 **큐 머리 피스의 층 F**만 TgtFloor에 쓴다 — 게이트(`TgtFloor==0 && (CurFloor!=F || Ready==0)`) 허용 시에만. TgtFloor≠0(머리 피스 정렬/분류 중)이면 쓰지 않고 **큐 머리는 대기**.
- **gate 관측 → 복귀**: WCS는 **`TgtFloor==0`을 계속 관측**한다(기존 `DepositDecider` 게이트 `write = TgtFloor==0 ? floor : null`). 분류 시작(Ready 1→0) 시 PLC가 TgtFloor=0으로 클리어하는 순간 WCS가 **큐에 대기 중인 다음 층을 즉시 TgtFloor에 기입**하고, 소터는 free해지면 그 층으로 **복귀**(이동)해 도착 시 PLC가 CurFloor를 기입한다. 즉 쓰기는 **`TgtFloor==0` 게이트 관측으로 트리거**되고 층은 **큐가 공급**한다(sort-completion 이벤트가 아님). "정렬해 둔 F로 자동 복귀"(옛 2층 고정)가 아니라 **큐가 주는 층으로의 복귀**다. 큐가 비면 미기입·현 CurFloor에서 idle. 이렇게 층을 **한 번에 하나씩 FIFO 순서**로 수용한다. IF-08 층별 호스트 push는 소터가 각 큐 피스의 층으로 복귀해 감에 따라 그 CurFloor를 따라간다.
- **AGV 측 동작(RCS · 참고)**: 이 큐는 **WCS가 소터를 어느 층으로 정렬·open push할지(순서)**를 지배할 뿐, AGV가 소터 앞에 물리적으로 줄 서는 게 아니다. IF-05 OK 후 AGV는 목적지로 출발하되 **미수용(BUSY·타 층 정렬·FULL·PAUSED·미개방)이면 목적지에서 대기하지 않고 파킹존으로 우회**한다. WCS가 그 층 목적지에 **IF-08 `next_state=3`(열림)**을 푸시하면 RCS가 주차된 AGV를 목적지로 보낸다 → **IF-09 도착 보고 → IF-10 투입**. 따라서 **미개방 목적지에선 IF-08 열림 푸시가 IF-09 도착보다 먼저**다(열림 푸시=출발 신호). 도착 시점에 이미 열려 있으면 파킹 없이 직행.
- **미확정(코드 스프린트에서 확정)**: 쓰기 트리거는 **`TgtFloor==0` 게이트 관측**으로 확정(위). 다만 소터 큐에서 **머리 피스를 정확히 언제 pop**하는지(예: 분류 시작 관측 시)와 큐 자료구조·동시성 세부는 코드 스프린트에서 결정.
- **현행 코드 갭**: 현재 코드는 **상태 없는 게이트만**(스냅샷 기반 `DepositDecider`) 있고 **큐가 없다** — 이 pending-floor 큐는 코드 스프린트에서 신설할 컴포넌트다.

## 3. API (WCS=서버, RCS=클라이언트, 응답 3s 한계)
공통 필드: pId(int 1~30000, RCS 부여), agvNo, barcode, inductionNo, chuteNo, qty, timeStamp("yyyy-MM-dd HH:mm:ss" 로컬)
- `POST /api/v1/destination-query` (IF-05) req{pId,agvNo,barcode,inductionNo,qty,timeStamp}  ← agvNo 포함(원본 HTML·절대규칙 6)
  → OK·chuteNo·reason(NORMAL/BUSY/FULL/PAUSED — 일단 이동) / NG·reason(OVER/COMPLETED/NO_DEST/OFFLINE — 대기)
  OK 시 예약 차감(이동 중 물량 반영, 중복 배정 방지)
  · **NG여도 투입 기록은 남긴다**(IF-16 통합) — piece를 status=DENIED로 삽입 + piece_event 기록(ERD §order_item·piece 참조)
  · 오더의 destination이 NULL(송장/매장 단위 상위 등록)이면 **이 시점 WCS가 빈 슈트 자동 할당**(dest_assign_type=AUTO) 후 예약 — 같은 트랜잭션. 빈 슈트 없으면 NG·NO_DEST
  · **목표 층 F 파생·큐 enqueue(2026-07-21)**: 요청 `inductionNo`로 `InductionFloorMap`에서 F(1/2)를 구한다. 소터 목적지면 그 피스(층 F)를 **소터별 pending-floor 큐에 IF-05 순서대로 enqueue**한다(§2-C). TgtFloor 쓰기는 IF-05 순간이 아니라 WCS가 **`TgtFloor==0`을 관측할 때 큐 머리 층을 즉시 기입**(게이트) → 소터 복귀 · "타겟층을 순서대로". FULL/PAUSED/OFFLINE이면 enqueue/쓰기 안 함. (과거 "IF-09 도착 트리거 + 항상 2" 폐지.)
- ~~`POST /api/v1/deposit-permission` (IF-08)~~ **[폐지 — RCS 폴링 엔드포인트 제거]** — 현행: WCS→RCS 상태 푸시로 대체(§2 상단 노트·`interface_kr` §6·`master_spec §06`). 옛 응답 형상 `{allowed, reason}`(판정 표 그대로, allowed=true→reason="READY")은 이제 **RCS로 나가지 않고** 내부 `DepositDecider` 판정 결과일 뿐이다 — 소터 push `ready` 산출과 **IF-05 인덕션 파생 층 정렬**의 근거.
  · 층 F는 요청 `inductionNo`→`InductionFloorMap`으로 산출(과거 "agvNo→agvFloor 매핑"·"운영 2층 고정"은 **폐지 — 2026-07-21**). 매핑 값만 현장 확정 — 설정 결선은 코드 스프린트. ※ AGV 도착 보고는 IF-09(`POST /api/v1/arrival-report`)이며 **층 필드 없음**(층은 IF-05 인덕션에서 이미 확정). 3DS 정렬은 **IF-05 시점**에 수행(도착 IF-09가 아니라) — IF-09는 도착 기록만.
- `POST /api/v1/deposit-report` (IF-10) req{pId,barcode,chuteNo,agvNo}  ← qty·timeStamp 없음(원본 HTML — qty는 IF-05 등록값 사용, 전량 틸트)
  → {result:"OK"} — 멱등(pId 중복 보고 무해). 3D 목적지면 이후 IF-11 셀 지정 트리거

## 4. C/R 핸드셰이크 (3D 목적지 한정, IF-11/12)
셀 선택: 오더의 활성 cell_assignment 있으면 그 셀 재사용, 없으면 그 destination 소속 빈 셀(enabled·미점유) 할당 — 빈 셀 없으면 해당 3DS는 FULL(WCS 판단 요소)
C(셀 지정): WCS가 C_Flag==0 확인 → C_CellNo·C_Seq 쓰기 → C_Flag=1
            → PLC가 C_Flag=1 감지 → C 읽기 → 읽은 직후 C_CellNo·C_Seq·C_Flag=0 클리어 → (틸트 낙하 N초는 PLC 지연) → 적재
R(적재 완료): PLC가 R_Flag==0 확인 → R_CellNo·R_Seq 쓰기 → R_Flag=1
            → WCS가 R_Flag 폴링(100ms, 타임아웃=분류 최대 소요+여유) → R 읽기 → R_Seq==C_Seq 대사(유실·중복 검출, 불일치=알람) + **틸트 시각(tiltedAt) 기록**
            → **R은 R_Flag==1 즉시 클리어하지 않는다.** **Ready==1(복귀 완료) 시점에** R_CellNo·R_Seq·R_Flag=0 클리어 + **복귀 시각(returnedAt) 기록**. (R_Flag==1 관측 시 이미 Ready==1이면 즉시 클리어.) 목적: **분류 시작~복귀 완료 전체 소요 측정**.

**sorter_command 처리시각 3종(신규 컬럼 — 마이그레이션은 코드 스프린트):** `depositedAt`(3DS 투입 = IF-10 투입 보고 시점) · `tiltedAt`(셀 틸트 = R_Flag==1 관측 시점) · `returnedAt`(복귀 완료 = Ready 0→1, R 영역 클리어 시점). 세 시각으로 투입→틸트→복귀 구간 소요를 계측한다. → ERD.md `sorter_command`.

### 4-A. R단계 잔류 대사 (arming) — S-HANDSHAKE-RESIDUE (감사 A-1 해소)
R단계는 **레벨 읽기가 아니라 arming(=C 기입 전 R_Flag==0을 1회 관찰 보장) 기반**이다. 0을 관찰한 이후의 R_Flag==1 상승만 자기 응답으로 수용 → 에지 감지와 등가.
- **핸드셰이크 시작 시**: C(CellAssign)를 큐에 투입하기 **전** 스냅샷 R_Flag를 확인한다.
  - R_Flag==0 → 그대로 진행(깨끗한 경로는 추가 지연 0).
  - R_Flag==1 → 직전 건·PLC 기동 잔류로 **대사**: WARN 로그 + operation_log(HANDSHAKE, 잔류값 `rCellNo`/`rSeq` 포함) + **ClearR 선행 큐 투입**(단일 큐 경유) + 폴링 스냅샷에서 **R_Flag==0 확인** 후에만 C 기입.
  - R_Flag==0 확인 대기 상한 = `Timing:RFlagClearConfirmTimeoutMs`(appsettings — 고정 금지). 초과 시(ClearR 미반영 — PLC 무ack 등) **C를 기입하지 않고** `RFlagResidueTimeout`으로 종결(더티 진행 금지). 대기 중 OFFLINE 감지도 명확 종결.
- **기동 시(첫 유효 폴)**: 게이트웨이가 첫 Online 폴에서 R_Flag==1이면 PLC 기동 잔류로 간주 → **ClearR 큐 투입**(단일 큐) + WARN 로그 + operation_log 기록. 기동 잔류는 그 응답의 대기자가 없고 C_Seq도 리셋 상태이므로, 유지하면 후속 전 건이 "직전 응답"을 오소비하는 off-by-one 연쇄를 낳는다 → 클리어가 정당한 복구.
- 배경: 레벨 읽기는 직전 건/PLC 기동 잔류 R_Flag=1을 새 건의 응답으로 오소비 → 허위 RSEQ_MISMATCH가 매 건 한 칸씩 밀리며 자가지속(현장 2026-07-06 5연쇄 실측). arming + 기동 reconcile로 근본 차단.

### 4-B. 기동/재시작 레지스터 클리어 (에러 복구 — 2026-07-21)
에러로 인한 재시작 등 **기동 시 WCS는 자신이 쓰는 레지스터만 0으로 클리어**한다 — `C_CellNo`(D0)·`C_Seq`(D1)·`C_Flag`(D4.0)·`R_CellNo`(D2)·`R_Seq`(D3)·`R_Flag`(D4.1)·`TgtFloor`(D6). **`Ready`(D4.2)·`CurFloor`(D5)는 건드리지 않는다**(PLC 소유·읽기 전용). D4는 한 워드라 **비트 0·1만 0으로 지우는 read-modify-write**(비트 2 `Ready` 보존)로 수행한다. 이 클리어는 **IF-08 부트스트랩 전체 상태 푸시(§2-A·§5-A)보다 먼저** 실행한다. 목적: 재시작 후 잔류 핸드셰이크·목표층 없이 깨끗한 상태로 시작(에러 복구). (§4-A의 R_Flag 잔류 reconcile와 정합 — 기동 클리어가 R_Flag 잔류도 함께 0으로 지운다.)

## 5. 타이밍 기본값 (전부 appsettings — 현장 조정)
폴 주기 100~200ms · IF-08 재호출 500ms(RCS측) · R_Flag 폴 100ms · R_Flag 타임아웃 = 분류최대+여유(고정 5s 금지)
· WCS API 3s · OFFLINE = 연속 폴 실패 N회 또는 소켓 끊김

### 5-A. 인덕션 층 매핑 · 층별 IF-08 push 호스트 (설정 — 하드코딩 금지, 절대규칙 #7)
- `InductionFloorMap` — inductionNo→floor 맵. 예 `{"1":1,"2":1,"3":2}`. IF-05에서 요청의 `inductionNo`로 목표 층 F(1/2)를 파생(§2-A·§3). 값은 현장 확정, appsettings 결선은 코드 스프린트.
- IF-08 층별 push 호스트 — **1층 `http://192.168.0.151:3000` · 2층 `http://192.168.0.152:3000`**. 상태가 바뀐 목적지의 **층**으로 라우팅(§2-A 라우팅 규칙: 3DS 소터=현재 CurFloor 층 호스트, 고정 슈트=자기 층 호스트). 경로 `PUT /api/UpdateChuteState`·페이로드·`next_state` 의미는 층 무관 동일.
- 기동 부트스트랩도 per-floor — 시작 시 전 목적지 현재 상태 1회 푸시(§2-A). **소터는 두 층 호스트 모두**(현재 CurFloor 호스트=`3` 수용 시 / 다른 층 호스트=`2`), **고정 슈트는 자기 층 호스트 1곳**. 두 층 라우팅이므로 부트스트랩 경로도 per-floor로 결선(코드 스프린트).
- ※ 두 값 모두 appsettings — 하드코딩 금지. 본 문서는 설계 확정값을 기록하고, 실제 파일 결선은 별도 코드 스프린트.

## 6. Sim3ds 동작 스펙 (시뮬레이터가 흉내낼 PLC)
- HR 7워드(D0~D6) 노출, FC03/06/16 응답
- 분류와 이동은 **직렬**(분류 진행 중엔 이동 시작 안 함 — 차트③: 분류를 마친 뒤 복귀). Ready=1 블립 금지.
- C_Flag=1 감지 → C 읽고 즉시 C_*·C_Flag=0 → TiltDelay 후 적재 → **분류 시작: Ready=0 + TgtFloor=0 클리어**
  → SortDuration 후: R_CellNo=셀, R_Seq=받은 C_Seq, R_Flag=1 세팅.
  이때 **복귀 이동이 남았으면(TgtFloor≠0 && TgtFloor≠CurFloor) Ready=0을 유지한 채 곧바로 이동 시작**, 그 외에만 Ready=1.
- **분류 중이 아닐 때** && TgtFloor≠0 && TgtFloor!=CurFloor → 이동 시작(Ready=0) → MoveDuration 후 CurFloor=TgtFloor 기입(TgtFloor는 유지!) → Ready=1
- 설정: TiltDelay, SortDuration, MoveDuration, 초기 CurFloor / 고장 주입: R_Seq 불일치, R_Flag 지연, 무응답(OFFLINE 유발)
- **전송(S-SIM3DS-RTU)**: Sim3ds는 `Transport=Tcp`(기본·현행 :1502 보존) 또는 `Rtu`(현장 리허설·RS-485)로 기동. 레지스터 맵·에코 지연·C_Flag 자체 클리어·ClearR까지 R 유지·잔류 프리셋 의미는 전송 무관 동일. 설정은 `appsettings.Sim3ds.json`(기본값) + 환경변수(`SIM3DS_*`) + CLI(`--transport rtu --port COMx …`). → **실 PLC 없이 WCS↔Sim RTU 리허설 절차·시리얼 페어 준비법: [docs/RTU-REHEARSAL.md](RTU-REHEARSAL.md)**

## 7. 미확정 사항 (구현 중 추측 금지 — 기록·질문)
- 목표 층 F **산출 방법 확정(2026-07-21)**: 요청 `inductionNo`→`InductionFloorMap`(inductionNo→floor). 과거 "agvNo→agvFloor 매핑"·"운영 2층 고정(항상 2)"은 **폐지**. **매핑 값**만 현장 확정 — 설정 결선은 코드 스프린트.
- RCS Q1~Q7 회신 대기(HTTP 클라이언트 사양, pId 초기화 정책, 인증 등)
- PLC측: Ready=0에 이동 중 포함 / TgtFloor 분류 시작 클리어 — 3DS 담당 확정 대기
- R_Flag 타임아웃 실측값은 현장 실측 후 appsettings 조정

### 7-A. 전송 방식 확정 (S-RTU 스프린트 2026-06 반영)

**전송 확정**: 현장 1차 타깃 = **Modbus RTU(RS-485)**. TCP는 시뮬레이터·SAT·일부 장비 병행 유지.

- **RTU 우선 + TCP 병행**: `Plc:Transport` = `Rtu`(기본, 미지정 시) | `Tcp`. 설정 1줄로 교체.
- **전송 추상화 완료**: `IModbusMaster` 인터페이스(backend/src/Wcs.PlcGateway/IModbusMaster.cs) — 판정 엔진·핸드셰이크·단일 쓰기 큐·RMW·OFFLINE은 전송 무관하게 재사용. TCP 어댑터(`ModbusTcpMaster`), RTU 어댑터(`ModbusRtuMaster`), 팩토리(`ModbusMasterFactory`) 구현 완료.
- **소터 전송 토폴로지(소터별 독립 버스 기본 + 공유 버스 가능 — S-MULTISORTER-SHARED-BUS)**: 소터별 독립 버스/포트가 기본이되, **같은 버스 키(RTU=PortName 대소문자무시 / TCP=Host:Port 입력그대로)로 여러 소터를 unitId로 구분해 한 물리 버스에 공유** 가능하다(Phase 1 메커니즘 `ModbusBus`+`SharedModbusConnection`+`BusSlaveMaster` + Phase 2 DI/레지스트리/설정 결선 — 버스당 클라이언트/포트 1 Open). 서로 다른 버스 키는 각자 독립 `ModbusBus`로 병렬 유지(멀티 포트 보존). **버스 단위 단일 쓰기 큐·버스 락으로 프레임 무교차**(절대규칙 #1 의미 불변·입도만 버스 단위), **슬레이브별 OFFLINE 독립**(한 슬레이브의 무응답/타임아웃이 공유 포트를 끊지 않아 형제 무영향). 소터 목록은 기동 시 DB(destination WHERE dest_type=SORTER_3D AND is_active) 주도로 확정하고 ChuteNo로 appsettings `Sorters[]`(전송·버스 키 파라미터·**UnitId**·공통/소터별 Timing 포함)와 매칭한다. **fail-loud(기동 거부)**: SORTER_3D인데 Sorters[] 항목 없음 / 같은 버스 멤버의 시리얼 파라미터(BaudRate·Parity·StopBits) 불일치 / 같은 버스 PollIntervalMs 불일치 / 같은 버스 중복 UnitId. per-member 핸드셰이크 Timing(RFlagTimeoutMs 등)·OfflineAfterFailures는 멤버별 상이 허용. (DataBits·엔디안은 설정 표면에 없어 검사 제외 — RTU 엔디안 고정 BigEndian.) 단일 멤버 버스(N=1)는 현행 단일 소터와 동작 동치.
- **WCS = Modbus 마스터 / 3DS PLC = 슬레이브**: RTU·TCP 모두 동일.
- **RTU 시리얼 파라미터**: PortName·BaudRate·Parity·StopBits·ReadTimeoutMs·WriteTimeoutMs·UnitId — 전부 appsettings(하드코딩 금지). 기본값은 현장 실측 전 TCP 동작 보존(BigEndian·UnitId=1).
- **OFFLINE 전이**: RTU 예외(IOException·TimeoutException)에서도 TCP와 동일하게 OFFLINE 전이(소켓 전용 분기 제거).
- **RTU 자동 테스트**: in-memory fake `IModbusRtuSerialPort` 쌍(`FakeSerialPort`)으로 CI 자동화(물리 COM 불필요 확인).

### 7-B. 하네스 검증(2026-06)에서 도출 — RCS/3DS 확정 대기
- **API 필드 정렬(HTML 우선 적용함)**: IF-08은 timeStamp 없음 / IF-10은 qty·timeStamp 없음(qty=IF-05 등록값). WCS 감사용 timeStamp가 필요하면 DTO에 **nullable 선택필드**로 두고 RCS 미전송 허용 — RCS 확정.
- **IF-08 allowed=true reason="READY"**: 원본 §6 사유코드는 READY 명시. API 계층에서 주입(Core ToWire(None)=null 유지). RCS가 reason 파싱 여부 확인.
- **IF-05 NG 시 chuteNo**: null 포함 vs 키 생략 직렬화 정책(원본이 혼용). 권장=null 포함(STJ 기본). RCS 파서 전제 확인.
- **R_Flag 타임아웃 초과 시 동작**: RFLAG_TIMEOUT 알람 + PLC 상태 재확인(Ready·Online) + sorter_command.status=TIMEOUT. **재시도 vs 포기** 정책 미정(재시도=새 행, ERD). (※ 별개 결함이던 감사 A-1 "R_Flag 레벨 읽기 → off-by-one 연쇄"는 §4-A arming + 기동 reconcile로 해소됨. 본 타임아웃은 진짜 무응답 경로로 회귀 보존.)
- **C_Flag=1 대기 타임아웃**: R쪽과 달리 상한·알람 미정의(무한 대기 위험). appsettings 설정값 + 초과 시 알람/상태 재확인 — 3DS 협의.
- **TgtFloor 잔류 해소**: 이동만 완료·투입 없이 AGV 이탈 시 TgtFloor≠0 영구 잔류 → 타 층 영구 WRONG_FLOOR. 해소책(PLC 무투입 N분 자체 클리어 / WCS 운영자 수동 리셋=절대규칙 3 예외 명문화) — 3DS 협의. S4 시나리오에 기대동작 정의.
- **레지스터 시작 주소**: D0~D4는 3DS 제공 맵 기반, D5·D6은 본 협의 신설. D영역↔Modbus 주소 오프셋 포함 현장 확정 — 변경 시 RegisterMap 상수만 수정.
- **IF-05 동일 바코드 다중 목적지 선택 규칙 미정 (2026-06-30 조사 도출)**: `order_item` UQ가 `(order_id, barcode)`라 **같은 바코드가 여러 활성 오더(다른 destination)에 존재 가능**. 현재 `DbRepositories.QueryDestination`은 `.Where(barcode 일치 && Order.Status∉{COMPLETED,CANCELLED}).FirstOrDefault()`로 **정렬 기준 없이 첫 매치** 반환 → 다중 매치 시 **어느 목적지가 반환될지 비결정적**(EF 기본 순서 의존). 운영 규칙 미정: (a) "1바코드=1활성목적지" 불변식으로 보고 모호성=에러/방어 / (b) 우선순위 규칙(예: 가장 오래된 오더·특정 배치·여유 셀)으로 결정적 선택. **단일 소터/1:1 바코드 환경에선 미발생**(내일 현장 16셀 데이터도 전부 1:1). 다중 슈트·동일 바코드 운영 도입 시 규칙 확정 후 결정적 처리 + 테스트 필요. 현재 테스트 커버리지 없음(갭).
- **IF-05 work_batch 필터 부재 (위와 연결)**: 위 조회에 `work_batch` 상태/일자 필터가 없어 **다른 배치(어제/오늘)의 활성 오더에 같은 바코드**가 있으면 교차 매칭 가능. 활성 배치만 조회 대상으로 좁힐지(예: WorkBatch.Status=RUNNING·당일) 정책 확정 필요. 단일 배치 운영에선 미발생.
- **정렬의 Hold(FULL/PAUSED) 무관 진행 — 절대규칙 #2 예외 여부 (2026-07-01 감사 A-확정)** — **(2026-07-21 갱신: 정렬 트리거가 IF-09 도착 → IF-05로 이동.)** IF-05 소터 dispatch는 FULL/PAUSED를 이미 NG로 차단하므로, 정렬(TgtFloor=F 쓰기)이 IF-05 게이트를 통과한 경우에만 수행되어 **Hold 무관 쓰기 문제는 IF-05 게이트에서 자연 해소**된다. 도착 IF-09는 정렬을 트리거하지 않는다(도착 기록만). 아래는 옛 IF-09 트리거 기준의 기록: ~~현재 IF-09 정렬은 `DepositDecider.Decide(snap, floor, WcsHold.None)` 고정으로 FULL/PAUSED 소터에도 TgtFloor를 쓴다~~. PAUSED 런타임 설정 기능 자체가 아직 없어 현 단계 실질 영향은 좁음.
- **오더 완료 전이·sorted_qty의 소유자 미정 (2026-07-01 감사 A-확정)**: WCS 코드에 `sorted_qty += qty`·`OrderStatus.COMPLETED` 전이가 전무 — ERD("IF-10·12 확정: reserved→sorted 이동")·본 문서 §3(NG reason COMPLETED)이 사문화 상태고, AUTO 슈트 배정은 RUNNING 오더 점유가 영영 안 풀려 슈트 풀이 단조 고갈된다. **결정 필요**: WCS가 IF-10/12 확정 시 가산+완료 전이를 구현할지, 상위(WMS/배치 마감)가 수행할지. 주의: 나이브하게 reserved→sorted '이동'으로 구현하면 유일한 마감 장치인 OVER 가드(ReservedQty 누적)가 무력화됨.
- **슈트 비움(clear)·PAUSED/RESUMED 운영 조작 인바운드 표면 부재 (2026-07-01 감사 A-확정)** → **✅ 확정(2026-07-03)**: **관리 API(웹 프론트 콘솔)로 결정** — `docs/FRONTEND.md` F3 페이즈에서 `/api/ops/chutes/{destId}/clear`·pause/resume으로 구현 예정(destination_event 감사 포함). RCS IF 신설 아님. (구현 전까지 갭 자체는 잔존 — 슈트 라인 운영 투입 전 F3 필요.)
- **RCS 인바운드 API 무인증 + 0.0.0.0:5205 전 인터페이스 바인딩 (2026-07-01 감사)** → **✅ 확정(2026-07-03)**: **인증 도입하지 않음(사내망/폐쇄망 신뢰 — 사용자 결정)**. 대신 ①내부망 전제를 운영 문서에 명문화 ②바인딩·방화벽 제한 권고 유지 ③웹 콘솔 운영 조작은 확인 다이얼로그의 작업자 이름 입력으로 감사 흔적 유지(`docs/FRONTEND.md` §4.5). 외부망 노출 요구가 생기면 재론.

### 7-C. M4-P1 코드리뷰 P2 이관 항목 (2026-06-16)

**P1 가정 명문화 — 단일 인스턴스 배포**
- IF-10 멱등은 in-process `static readonly object _recordLock`에 의존. 단일 프로세스 내에서만 유효.
  다중 인스턴스(로드밸런서) 배포 시 이중 기록·IF-11 이중 트리거 가능. P1 범위 밖 — P2에서 DB 레벨 진성 멱등으로 전환.

**P2a 완료 항목 (M4-P2a, 2026-06-16)**
- ✅ [MAJOR-1] `piece` 부분 유니크 `(p_id) WHERE is_active=1 AND status IN ('DEPOSITED','CELL_ASSIGNED','LOADED')` + UniqueConstraintException catch → false 반환. `static _recordLock` 제거.
- ✅ [MINOR-2] `Ignore(propertyName)` 적용 — 이중 물리 컬럼 실제 제거. SQLite: `RowVersion(byte[]?)` Ignore, SQL Server: `XminRowVersion(int)` Ignore. 마이그레이션 DropColumn 포함.
- ✅ [MINOR-4] `cell_assignment` `(cell_id) WHERE released_at IS NULL` 부분 유니크 인덱스 추가.
- ✅ [MINOR-5] IF-05 NG(DENIED) piece `destination_id`: nullable FK — 미매칭 시 null(0 fallback 제거).
- ✅ [MINOR-6] `IF05_REQ` + `IF05_RES` 이벤트를 `QueryDestination` 단일 트랜잭션에서 삽입. `RecordDestinationQuery` 인터페이스 메서드 제거.
- ✅ [Scope-1] IF-08 SORTER_3D / CHUTE 분기 — ISorterGatewayRegistry 단일 진입점, CHUTE 경로 hold만 판정.
- ✅ [Scope-2] ChuteCapacityService 싱글톤 — FULL/PAUSED 인메모리 집계, IHostedService 기동 시 DB 복원.
- ✅ [Scope-3] timeStamp 백필 `"yyyy-MM-dd HH:mm:ss"` 파싱, UtcNow 폴백. ClientTs 원문 보존.
- ✅ [Scope-9] `CancellationToken.None` → `IHostApplicationLifetime.ApplicationStopping`. GetDestType 다운캐스트 제거. InMemory* 구현체+POCO 제거(인터페이스 유지).
- ✅ [Migration] P2a_PieceNullableDestId_UniqueIndexes_RowVersionIgnore 마이그레이션 (SQLite·SqlServer) 추가·적용. DropColumn(RowVersion×5·SQLite / XminRowVersion×5·SqlServer) 포함.

**P2b 이관 대상 (미완)**
- 다중 소터(N대) 라우팅: ISorterGatewayRegistry P2b에서 실제 destination.id→gateway 맵으로 교체.
