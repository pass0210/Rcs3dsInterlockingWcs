# WCS 응축 스펙 (코드 기준 문서)

원본: docs/*.html 4종. 충돌 시 HTML(최신 확정본)이 우선.

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
| CurFloor | D5 | PLC write | 현재 층(1/2). 도착 시 기입 |
| TgtFloor | D6 | WCS write / **PLC clear** | 목표 층. 0=명령 없음 |

D4는 한 워드 — 한 비트만 바꿀 땐 D4 읽기→비트 수정→쓰기(RMW). 쓰기는 단일 큐에서만.

## 2. IF-08 투입 가부 판정 (Wcs.Core.DepositDecider — 순수 함수)
입력: PlcSnapshot(레지스터+Online), agvFloor(int), WcsHold(None/Full/Paused)
우선순위: Offline → Full/Paused → Ready/층 비교

| # | 조건 | allowed | reason | TgtFloor 쓰기 |
|---|---|---|---|---|
| 1 | Online && Hold=None && Ready=1 && CurFloor==agvFloor | true | None | 안 씀 |
| 2 | Ready=1 && CurFloor≠agvFloor && TgtFloor==0 | false | WRONG_FLOOR | **agvFloor 기입** |
| 3 | Ready=1 && CurFloor≠agvFloor && TgtFloor≠0 | false | WRONG_FLOOR | 안 씀(핑퐁 차단) |
| 4 | Ready=0 && TgtFloor==0 (층 무관) | false | BUSY | **agvFloor 기입**(분류 후 복귀 선기입) |
| 5 | Ready=0 && TgtFloor≠0 | false | BUSY | 안 씀 |
| 6 | Hold=Full / Paused | false | FULL / PAUSED | 안 씀(대기) |
| 7 | !Online | false | OFFLINE | 안 씀 |

TgtFloor 쓰기 조건 한 줄: `TgtFloor==0 && (CurFloor!=agvFloor || Ready==0)` — 단 Hold/Offline이면 항상 안 씀.
클리어: WCS는 절대 안 함. PLC가 분류 시작(Ready 1→0) 시 0으로(도착 시엔 CurFloor만 기입·TgtFloor 유지).

## 3. API (WCS=서버, RCS=클라이언트, 응답 3s 한계)
공통 필드: pId(int 1~30000, RCS 부여), agvNo, barcode, inductionNo, chuteNo, qty, timeStamp("yyyy-MM-dd HH:mm:ss" 로컬)
- `POST /api/v1/destination-query` (IF-05) req{pId,barcode,inductionNo,qty,timeStamp}
  → OK·chuteNo (목적지 NORMAL/BUSY/FULL/PAUSED — 일단 이동) / NG·reason(OVER/COMPLETED/NO_DEST/OFFLINE — 대기)
  OK 시 예약 차감(이동 중 물량 반영, 중복 배정 방지)
  · 오더의 destination이 NULL(송장/매장 단위 상위 등록)이면 **이 시점 WCS가 빈 슈트 자동 할당**(dest_assign_type=AUTO) 후 예약 — 같은 트랜잭션. 빈 슈트 없으면 NG·NO_DEST
- `POST /api/v1/deposit-permission` (IF-08) req{pId,chuteNo,agvNo,timeStamp}
  → {allowed, reason?} — 판정 표 그대로. RCS는 false면 500ms 후 재호출
  ※ agvFloor 산출 방법(요청 필드 vs agvNo/chuteNo→층 매핑)은 미확정 — 설정 매핑으로 시작
- `POST /api/v1/deposit-report` (IF-10) req{pId,barcode,chuteNo,agvNo,qty,timeStamp}
  → {result:"OK"} — 멱등(pId 중복 보고 무해). 3D 목적지면 이후 IF-11 셀 지정 트리거

## 4. C/R 핸드셰이크 (3D 목적지 한정, IF-11/12)
셀 선택: 오더의 활성 cell_assignment 있으면 그 셀 재사용, 없으면 그 destination 소속 빈 셀(enabled·미점유) 할당 — 빈 셀 없으면 해당 3DS는 FULL(WCS 판단 요소)
C(셀 지정): WCS가 C_Flag==0 확인 → C_CellNo·C_Seq 쓰기 → C_Flag=1
            → PLC가 C_Flag=1 감지 → C 읽기 → 읽은 직후 C_CellNo·C_Seq·C_Flag=0 클리어 → (틸트 낙하 N초는 PLC 지연) → 적재
R(적재 완료): PLC가 R_Flag==0 확인 → R_CellNo·R_Seq 쓰기 → R_Flag=1
            → WCS가 R_Flag 폴링(100ms, 타임아웃=분류 최대 소요+여유) → R 읽기 → R_Seq==C_Seq 대사(유실·중복 검출, 불일치=알람)
            → R_CellNo·R_Seq·R_Flag=0 클리어

## 5. 타이밍 기본값 (전부 appsettings — 현장 조정)
폴 주기 100~200ms · IF-08 재호출 500ms(RCS측) · R_Flag 폴 100ms · R_Flag 타임아웃 = 분류최대+여유(고정 5s 금지)
· WCS API 3s · OFFLINE = 연속 폴 실패 N회 또는 소켓 끊김

## 6. Sim3ds 동작 스펙 (시뮬레이터가 흉내낼 PLC)
- HR 7워드(D0~D6) 노출, FC03/06/16 응답
- C_Flag=1 감지 → C 읽고 즉시 C_*·C_Flag=0 → TiltDelay 후 적재 → **분류 시작: Ready=0 + TgtFloor=0 클리어**
  → SortDuration 후: R_CellNo=셀, R_Seq=받은 C_Seq, R_Flag=1, Ready=1
- TgtFloor≠0 && TgtFloor!=CurFloor → 이동 시작(Ready=0) → MoveDuration 후 CurFloor=TgtFloor 기입(TgtFloor는 유지!) → Ready=1
- 설정: TiltDelay, SortDuration, MoveDuration, 초기 CurFloor / 고장 주입: R_Seq 불일치, R_Flag 지연, 무응답(OFFLINE 유발)

## 7. 미확정 사항 (구현 중 추측 금지 — 기록·질문)
- agvFloor 출처(IF-08 요청 필드 추가 vs 매핑) — RCS 협의
- RCS Q1~Q7 회신 대기(HTTP 클라이언트 사양, pId 초기화 정책, 인증 등)
- PLC측: Ready=0에 이동 중 포함 / TgtFloor 분류 시작 클리어 — 3DS 담당 확정 대기
- R_Flag 타임아웃 실측값, TCP(502) vs RTU
