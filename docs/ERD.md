# DB 스키마 스펙 (M4 구현 기준) — 이 문서가 DB 스키마의 단일 진실(원본 HTML 없음)

## 설계 원칙 (위반 금지)
1. PK는 전부 대리키 `id bigint identity`. 자연키(p_id, chute_no, order_no…)는 UNIQUE 인덱스.
2. **p_id는 1~30000 순환** → `piece`에 필터드 유니크 `(p_id) WHERE is_active=1`.
   같은 p_id로 새 IF-05 수신 시: 기존 활성 행 `is_active=0` → 새 행 삽입(한 트랜잭션).
3. 현재 상태(`piece`) vs append-only 이력(`piece_event`, `plc_event`) 분리 — 이력 UPDATE 금지.
4. 상태값은 문자열 + CHECK 제약(EF: enum `HasConversion<string>()`).
5. 이력 테이블엔 FK + 당시 값 스냅샷 컬럼(cell_no, c_seq…) 병행 — 마스터 변경에도 이력 불변.
6. 공통: `created_at datetime2(UTC)`. 상태 테이블 추가: `updated_at`, `row_version rowversion`.
   RCS의 로컬 `timeStamp` 원문은 `piece_event.client_ts`에 보존(파싱 실패해도 저장).
7. FULL은 저장하지 않는다 — **계산**: `SUM(piece.qty WHERE deposited_at > chute_detail.last_cleared_at) + 이동 중 예약 qty 합 >= work_full_qty` (COUNT 아님 — 피스 1건 qty>1 가능).
   현재 투입 수량 컬럼(cur_qty) 금지. 노출=뷰 v_destination_status(cur_qty·in_flight·full), IF-08 핫패스=서비스 인메모리 집계(기동 시 뷰로 재구성, IF-05/IF-10/비움 이벤트로 증감).
   비움 확인 = last_cleared_at 갱신 + destination_event(CLEARED) — 카운터 컬럼 금지(드리프트 원천 차단, 단일 진실=piece).
   오더 완료(OVER/COMPLETED)는 order_item reserved+sorted vs planned 계산 — 동일 원칙. 필요 시 뷰 v_destination_status.

## 테이블 (16)
### 기준정보
- `destination` — id, chute_no UQ, dest_type CHECK('CHUTE','SORTER_3D'), floor int NULL(3D=NULL), status CHECK('NORMAL','PAUSED'), is_active bit
- `cell` — id, destination_id FK(**SORTER_3D 목적지 소속** — dest_type='SORTER_3D' 검증은 앱 레벨), cell_no, **UQ(destination_id,cell_no)**, capacity int NULL, enabled bit
  · IF-11 셀 선택: ① 오더의 활성 cell_assignment 재사용 → ② 없으면 그 소터 소속 빈 셀(enabled·미점유) 할당 → ③ 빈 셀 없음 = 해당 3DS FULL 판정 요소(WCS 판단)
- `cell_assignment` — id, cell_id FK, order_id FK, assigned_at, released_at NULL(=점유 중)
- `agv` — id, agv_no UQ, floor int, enabled  ← agvFloor 미확정 Q를 데이터로 흡수
- `printer` — id, printer_no UQ, name, conn_info NULL(IP:PORT 등), enabled
- `chute_detail` — destination_id **PK=FK(1:1, CHUTE 전용)**, default_full_qty(기본 풀: 마감 시 적용),
  work_full_qty(작업 풀: 현재 적용), printer_id FK NULL, last_cleared_at NULL(마지막 비움), zone NULL
  · **마감 규칙**: 마감 시 한 트랜잭션으로 `work_full_qty := default_full_qty` + destination_event(CLOSED·FULL_QTY_CHANGED)
  · 새 슈트 속성은 이 테이블에만 추가(타 테이블 무영향)
- `induction` — id, induction_no UQ, floor int, enabled

### 운영 축 · 오더
- `work_batch` — id, work_date date(작업일자), batch_no(배치), wave_no int(차수), **UQ(work_date,batch_no,wave_no)**,
  status CHECK('WAITING','RUNNING','CLOSED'), opened_at, closed_at
  · 배치↔차수 관계는 현장마다 달라 계층 강제 없이 3컬럼 흡수(1:N 확정 시 분리)
  · **마감 앵커**: CLOSED 전이와 한 트랜잭션으로 슈트 work_full_qty:=default_full_qty + destination_event(CLOSED·FULL_QTY_CHANGED)
- `wcs_order` — id, work_batch_id FK, order_no, **UQ(work_batch_id,order_no)**(재작업 재등장 허용),
  order_type CHECK('GENERAL','INVOICE','STORE') default 'GENERAL', ref_no NULL(송장번호/매장코드), ref_name NULL,
  destination_id FK **NULL허용(미할당)**, dest_assign_type CHECK('UPSTREAM','AUTO','MANUAL') NULL, dest_assigned_at NULL,
  status CHECK('WAITING','RUNNING','COMPLETED','CANCELLED'), started_at, closed_at
  · **지연 할당**: destination NULL 오더는 첫 피스 IF-05에서 빈 슈트 자동 할당(AUTO)+예약을 한 트랜잭션으로. 빈 슈트 없으면 NG·NO_DEST
  · 빈 슈트 판정 = RUNNING 오더의 dest_assigned_at~closed_at 점유 윈도우(별도 테이블 불필요). 할당 우선순위(층/구역/라운드로빈)는 Core 전략+설정
- `order_item` — id, order_id FK, barcode, planned_qty, reserved_qty, sorted_qty, UQ(order_id,barcode)
  · IF-05 OK: reserved_qty+=qty + piece 삽입 = 한 트랜잭션 / IF-10·12 확정: reserved→sorted 이동
  · **IF-05 NG**(IF-16 통합 — 응답이 NG여도 투입 기록은 남긴다): piece를 status=DENIED로 삽입(예약 차감 없음) + piece_event(IF05_REQ/RES) 기록 = 한 트랜잭션

### 실행·이력
- `piece` — id, p_id, is_active, barcode, qty, deposited_at datetime2 NULL(IF-10 시점·사실), destination_id FK, order_item_id FK NULL(예약 라인 — SUM(piece.qty)==order_item 정합성 검증용), agv_id FK NULL, induction_id FK NULL,
  status CHECK('QUERIED','RESERVED','DENIED','PERMITTED','DEPOSITED','CELL_ASSIGNED','LOADED','MISMATCH','TIMEOUT','CANCELLED'),
  updated_at, row_version
  · 슈트 목적지는 DEPOSITED 종료, 3D만 CELL_ASSIGNED→LOADED 진행
- `piece_event` — id, piece_id FK, event_type('IF05_REQ','IF05_RES','IF08_REQ','IF08_RES','IF10_REQ','IF10_RES','DECISION'), reason NULL, payload_json nvarchar(max), client_ts varchar NULL, at(UTC)
- `sorter_command` — id, piece_id FK, cell_id FK, c_seq, cell_no(스냅샷), c_written_at, r_seq NULL, r_cell_no NULL, r_flag_at NULL, status CHECK('SENT','COMPLETED','MISMATCH','TIMEOUT') · 재시도=새 행
- `plc_event` — id, kind CHECK('REG_CHANGE','WRITE','ONLINE','OFFLINE'), register varchar('D0'~'D6','D4.0'…), old_val, new_val, at(UTC)
- `alarm` — id, code('R_SEQ_MISMATCH','RFLAG_TIMEOUT','OFFLINE',…), severity('INFO','WARN','ERROR'), piece_id FK NULL, message, raised_at, acked_at NULL
- `destination_event` — id, destination_id FK, event_type CHECK('CLEARED','FULL_QTY_CHANGED','CLOSED','PAUSED','RESUMED'),
  detail_json NULL(old/new 값), operator_id NULL, at(UTC) · append-only — 운영 조작(비움/풀수량/마감/일시정지) 감사 단일 이력

## 인덱스
- piece: 필터드 UQ(p_id) WHERE is_active=1 · (status) · (destination_id,status) · (destination_id,deposited_at)
- piece_event/plc_event: (at) 선두, piece_event 보조 (piece_id,at)
- alarm: (acked_at) WHERE acked_at IS NULL
- destination_event: (destination_id, at)
- wcs_order: (work_batch_id, status) · piece 보조: (order_item_id)
- SQLite(개발)엔 filtered index/rowversion 없음 → provider 분기: 일반 UNIQUE(p_id,is_active) + int 버전 컬럼

## 보존
plc_event 7~14일 · piece_event 30~90일 · 나머지 영구. 일배치 퍼지(시간 인덱스 사용).
