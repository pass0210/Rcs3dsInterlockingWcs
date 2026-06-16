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

## 2. IF-08 투입 가부 판정

### 2-A. SORTER_3D 경로 (Wcs.Core.DepositDecider — 순수 함수)
입력: PlcSnapshot(레지스터+Online), agvFloor(int), WcsHold(None/Full/Paused)
우선순위: Offline → Full/Paused → Ready/층 비교

| # | 조건 | allowed | reason | TgtFloor 쓰기 |
|---|---|---|---|---|
| 1 | Online && Hold=None && Ready=1 && CurFloor==agvFloor (TgtFloor 무관 — 이동완료 후 잔류 ≠0 포함) | true | None(와이어 reason="READY") | 안 씀 |
| 2 | Ready=1 && CurFloor≠agvFloor && TgtFloor==0 | false | WRONG_FLOOR | **agvFloor 기입** |
| 3 | Ready=1 && CurFloor≠agvFloor && TgtFloor≠0 | false | WRONG_FLOOR | 안 씀(핑퐁 차단) |
| 4 | Ready=0 && TgtFloor==0 (층 무관) | false | BUSY | **agvFloor 기입**(분류 후 복귀 선기입) |
| 5 | Ready=0 && TgtFloor≠0 | false | BUSY | 안 씀 |
| 6 | Hold=Full / Paused | false | FULL / PAUSED | 안 씀(대기) |
| 7 | !Online | false | OFFLINE | 안 씀 |

TgtFloor 쓰기 조건 한 줄: `TgtFloor==0 && (CurFloor!=agvFloor || Ready==0)` — 단 Hold/Offline이면 항상 안 씀.
클리어: WCS는 절대 안 함. PLC가 분류 시작(Ready 1→0) 시 0으로(도착 시엔 CurFloor만 기입·TgtFloor 유지).

### 2-B. CHUTE 경로 (M4-P2a 신설)
입력: WcsHold(IChuteCapacityService.GetHold) — PLC 스냅샷·agvFloor 미사용, TgtFloor 쓰기 없음.

| 조건 | allowed | reason |
|---|---|---|
| destination 미존재 또는 비활성 | false | PAUSED |
| destination.Status == PAUSED | false | PAUSED |
| hold == Full | false | FULL |
| hold == Paused (용량 집계) | false | PAUSED |
| hold == None (정상) | true | READY |

FULL 판정: `SUM(piece.qty WHERE deposited_at > last_cleared_at) + in-flight(RESERVED/PERMITTED)qty >= work_full_qty`
(cur_qty 컬럼 없음 — 집계는 piece 테이블이 단일 진실. 인메모리 캐시: ChuteCapacityService 싱글톤)

## 3. API (WCS=서버, RCS=클라이언트, 응답 3s 한계)
공통 필드: pId(int 1~30000, RCS 부여), agvNo, barcode, inductionNo, chuteNo, qty, timeStamp("yyyy-MM-dd HH:mm:ss" 로컬)
- `POST /api/v1/destination-query` (IF-05) req{pId,agvNo,barcode,inductionNo,qty,timeStamp}  ← agvNo 포함(원본 HTML·절대규칙 6)
  → OK·chuteNo·reason(NORMAL/BUSY/FULL/PAUSED — 일단 이동) / NG·reason(OVER/COMPLETED/NO_DEST/OFFLINE — 대기)
  OK 시 예약 차감(이동 중 물량 반영, 중복 배정 방지)
  · **NG여도 투입 기록은 남긴다**(IF-16 통합) — piece를 status=DENIED로 삽입 + piece_event 기록(ERD §order_item·piece 참조)
  · 오더의 destination이 NULL(송장/매장 단위 상위 등록)이면 **이 시점 WCS가 빈 슈트 자동 할당**(dest_assign_type=AUTO) 후 예약 — 같은 트랜잭션. 빈 슈트 없으면 NG·NO_DEST
- `POST /api/v1/deposit-permission` (IF-08) req{pId,chuteNo,agvNo}  ← timeStamp 없음(원본 HTML). WCS 감사용 필요시 DTO nullable 선택필드(§7 확정 대기)
  → {allowed, reason} — 판정 표 그대로. **allowed=true → reason="READY"**(원본 §6 사유코드). RCS는 false면 500ms 후 재호출
  · agvFloor는 agvNo→층 매핑으로 산출(원본 §4에서 agvFloor 필드 제거 확정). 매핑 테이블 값만 현장 확정 — 설정(M3)→agv.floor(M4)
- `POST /api/v1/deposit-report` (IF-10) req{pId,barcode,chuteNo,agvNo}  ← qty·timeStamp 없음(원본 HTML — qty는 IF-05 등록값 사용, 전량 틸트)
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
- 분류와 이동은 **직렬**(분류 진행 중엔 이동 시작 안 함 — 차트③: 분류를 마친 뒤 복귀). Ready=1 블립 금지.
- C_Flag=1 감지 → C 읽고 즉시 C_*·C_Flag=0 → TiltDelay 후 적재 → **분류 시작: Ready=0 + TgtFloor=0 클리어**
  → SortDuration 후: R_CellNo=셀, R_Seq=받은 C_Seq, R_Flag=1 세팅.
  이때 **복귀 이동이 남았으면(TgtFloor≠0 && TgtFloor≠CurFloor) Ready=0을 유지한 채 곧바로 이동 시작**, 그 외에만 Ready=1.
- **분류 중이 아닐 때** && TgtFloor≠0 && TgtFloor!=CurFloor → 이동 시작(Ready=0) → MoveDuration 후 CurFloor=TgtFloor 기입(TgtFloor는 유지!) → Ready=1
- 설정: TiltDelay, SortDuration, MoveDuration, 초기 CurFloor / 고장 주입: R_Seq 불일치, R_Flag 지연, 무응답(OFFLINE 유발)

## 7. 미확정 사항 (구현 중 추측 금지 — 기록·질문)
- agvFloor **산출 방법은 확정**(agvNo→층 매핑; 원본 §4 "agvFloor 필드 제거"). **매핑 테이블 값**만 현장 확정 — M3 설정→M4 agv.floor 단일 진실 전환
- RCS Q1~Q7 회신 대기(HTTP 클라이언트 사양, pId 초기화 정책, 인증 등)
- PLC측: Ready=0에 이동 중 포함 / TgtFloor 분류 시작 클리어 — 3DS 담당 확정 대기
- R_Flag 타임아웃 실측값은 현장 실측 후 appsettings 조정

### 7-A. 전송 방식 확정 (S-RTU 스프린트 2026-06 반영)

**전송 확정**: 현장 1차 타깃 = **Modbus RTU(RS-485)**. TCP는 시뮬레이터·SAT·일부 장비 병행 유지.

- **RTU 우선 + TCP 병행**: `Plc:Transport` = `Rtu`(기본, 미지정 시) | `Tcp`. 설정 1줄로 교체.
- **전송 추상화 완료**: `IModbusMaster` 인터페이스(src/Wcs.PlcGateway/IModbusMaster.cs) — 판정 엔진·핸드셰이크·단일 쓰기 큐·RMW·OFFLINE은 전송 무관하게 재사용. TCP 어댑터(`ModbusTcpMaster`), RTU 어댑터(`ModbusRtuMaster`), 팩토리(`ModbusMasterFactory`) 구현 완료.
- **소터별 독립 포트(토폴로지 확정)**: 소터마다 독립 버스/포트(포트당 소터 1대, 다중 슬레이브 경합 없음). 설정 스키마는 소터별 독립 전송 N 확장 표현 가능 — 런타임은 단일 소터(M3/M4에서 N대 라우팅 추가 예정).
- **WCS = Modbus 마스터 / 3DS PLC = 슬레이브**: RTU·TCP 모두 동일.
- **RTU 시리얼 파라미터**: PortName·BaudRate·Parity·StopBits·ReadTimeoutMs·WriteTimeoutMs·UnitId — 전부 appsettings(하드코딩 금지). 기본값은 현장 실측 전 TCP 동작 보존(BigEndian·UnitId=1).
- **OFFLINE 전이**: RTU 예외(IOException·TimeoutException)에서도 TCP와 동일하게 OFFLINE 전이(소켓 전용 분기 제거).
- **RTU 자동 테스트**: in-memory fake `IModbusRtuSerialPort` 쌍(`FakeSerialPort`)으로 CI 자동화(물리 COM 불필요 확인).

### 7-B. 하네스 검증(2026-06)에서 도출 — RCS/3DS 확정 대기
- **API 필드 정렬(HTML 우선 적용함)**: IF-08은 timeStamp 없음 / IF-10은 qty·timeStamp 없음(qty=IF-05 등록값). WCS 감사용 timeStamp가 필요하면 DTO에 **nullable 선택필드**로 두고 RCS 미전송 허용 — RCS 확정.
- **IF-08 allowed=true reason="READY"**: 원본 §6 사유코드는 READY 명시. API 계층에서 주입(Core ToWire(None)=null 유지). RCS가 reason 파싱 여부 확인.
- **IF-05 NG 시 chuteNo**: null 포함 vs 키 생략 직렬화 정책(원본이 혼용). 권장=null 포함(STJ 기본). RCS 파서 전제 확인.
- **R_Flag 타임아웃 초과 시 동작**: RFLAG_TIMEOUT 알람 + PLC 상태 재확인(Ready·Online) + sorter_command.status=TIMEOUT. **재시도 vs 포기** 정책 미정(재시도=새 행, ERD).
- **C_Flag=1 대기 타임아웃**: R쪽과 달리 상한·알람 미정의(무한 대기 위험). appsettings 설정값 + 초과 시 알람/상태 재확인 — 3DS 협의.
- **TgtFloor 잔류 해소**: 이동만 완료·투입 없이 AGV 이탈 시 TgtFloor≠0 영구 잔류 → 타 층 영구 WRONG_FLOOR. 해소책(PLC 무투입 N분 자체 클리어 / WCS 운영자 수동 리셋=절대규칙 3 예외 명문화) — 3DS 협의. S4 시나리오에 기대동작 정의.
- **레지스터 시작 주소**: D0~D4는 3DS 제공 맵 기반, D5·D6은 본 협의 신설. D영역↔Modbus 주소 오프셋 포함 현장 확정 — 변경 시 RegisterMap 상수만 수정.

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
