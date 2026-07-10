# WCS ↔ RCS API 레퍼런스 (배포 기준 · 테스트 환경)

> RCS 연동 개발자를 위한 **실전 API 레퍼런스**. 아래 예제·응답은 전부 배포 VM에서 실제 호출·검증한
> 값이다(2026-07-10 E2E). 설계 배경·판정표 전체는 [wcs_rcs_interface_kr.html](wcs_rcs_interface_kr.html) 참조.

## 0. 접속 정보

| 항목 | 값 |
|---|---|
| **Base URL** | `http://20.24.10.147:5205` |
| 프로토콜 | HTTP / JSON (사내망 전제 — 인증 없음, 단일 포트) |
| 환경 | 테스트 서버 + Sim3ds(소터 시뮬레이터) |
| 방향 | **이 테스트 환경은 인바운드 전용** — RCS→WCS(IF-05/09/10)만. WCS→RCS 푸시(IF-08)는 **비활성**(§6). |
| 자동생성 문서 | **없음**(Swagger/Scalar/OpenAPI 미노출). 이 문서가 API 레퍼런스다. |

> ⚠ Sim3ds는 실 3DS PLC 대역이다. 소터 동작(층 이동·분류)은 시뮬레이션이며 타이밍은 실 PLC와 다를 수 있다.

### 필드 규약 (전 인터페이스 공통)
`pId, agvNo, barcode, inductionNo, chuteNo, qty, timeStamp` — JSON은 **camelCase**.
`timeStamp` 형식 `"yyyy-MM-dd HH:mm:ss"`(로컬). `pId`는 **1~30000**.

---

## 1. 헬스 체크 — `GET /health`

프로세스 생존은 항상 200. 본문의 `db`·`sorters[].online`으로 상태 판독.
```bash
curl http://20.24.10.147:5205/health
```
```json
{"status":"ok","db":true,"sorters":[{"chuteNo":1,"online":true,"lastPollAt":"2026-07-10T04:16:21Z"}]}
```
- `db:true` = WCS 내부 DB 연결 정상. `sorters[].online:true` = 소터(Sim3ds) 폴링 정상.

---

## 2. RCS 인바운드 API (IF-05 / IF-09 / IF-10)

3개 모두 `POST`, `Content-Type: application/json`. 정상/가부는 **200 + `result`**, 입력 검증 실패만 **400**.
NG 사유(NORMAL·BUSY·FULL·PAUSED·NO_DEST 등)는 **WCS 내부 로그(piece_event)에만** 기록되고 RCS로는 `result`만 반환한다.

### 2-1. IF-05 목적지 조회 — `POST /api/v1/destination-query`
인덕션 스캔 시 피스 단위로 1회 호출. WCS가 피스를 기록하고 목적지·수량을 판정한다.

**요청**
| 필드 | 타입 | 필수 | 설명 |
|---|---|---|---|
| `pId` | int(1~30000) | ✔ | 피스 ID(AGV 트레이 단위) |
| `agvNo` | int | ✔ | AGV 번호(기록·감사용) |
| `barcode` | string(≤200) | ✔ | 상품 바코드 → 오더 매칭 키 |
| `inductionNo` | int | ✔ | 인덕션 번호 |
| `qty` | int(≥1) | ✔ | 수량(전량 틸트 기준값 — 진실의 원천) |
| `timeStamp` | string(≤30) | ✔ | `"yyyy-MM-dd HH:mm:ss"` |

**응답** `200 {result, chuteNo}` — OK면 `chuteNo`(목적지 슈트번호), NG면 `chuteNo=null`.
```bash
curl -X POST http://20.24.10.147:5205/api/v1/destination-query \
  -H 'Content-Type: application/json' \
  -d '{"pId":5001,"agvNo":1,"barcode":"0701-CELL-01","inductionNo":1,"qty":1,"timeStamp":"2026-07-10 04:20:00"}'
```
```json
{"result":"OK","chuteNo":1}
```
- 미매칭 바코드 → `{"result":"NG","chuteNo":null}` (내부사유 NO_DEST).
- 검증 실패(예 `pId:0`) → `400 {"error":"pId는 1~30000 범위여야 합니다."}`.
- 가부 판정(요약): **슈트**는 full/paused여도 OK(보내고 대기). **3D 소터**는 `PAUSED`면 NG, 셀 수용 불가면 NG(FULL), BUSY(분류·이동 중)는 OK(이동시킴). 전체 표는 스펙 HTML §6.

### 2-2. IF-09 도착 보고 — `POST /api/v1/arrival-report`
AGV가 목적지 슈트에 도착해 투입 직전 1회 호출. WCS가 도착을 기록하고, **3D 소터면 운영층(2층)으로 정렬**(TgtFloor 쓰기)한다.

**요청**
| 필드 | 타입 | 필수 | 설명 |
|---|---|---|---|
| `pId` | int(1~30000) | ✔ | 피스 ID |
| `chuteNo` | int(>0) | ✔ | 도착 목적지 슈트번호 |
| `agvNo` | int | ✔ | AGV 번호 |
| `timeStamp` | string(≤30) | – | 선택 |

**응답** `200 {result:"OK"}`
```bash
curl -X POST http://20.24.10.147:5205/api/v1/arrival-report \
  -H 'Content-Type: application/json' \
  -d '{"pId":5001,"chuteNo":1,"agvNo":1,"timeStamp":"2026-07-10 04:20:00"}'
```
```json
{"result":"OK"}
```
- 소터의 경우 이 호출 뒤 소터가 2층으로 이동 → `/api/monitor/sorters`의 `ready`가 `false→true`로 전이(관측 가능).
- 미존재/비활성 `chuteNo`도 500 없이 200(도착만 기록, 정렬 스킵).

### 2-3. IF-10 투입 보고 — `POST /api/v1/deposit-report`
틸트 후 전송. 3D 목적지면 WCS가 내부적으로 **Modbus 핸드셰이크(셀 배정→C_Flag→R_Flag)** 를 트리거한다. 멱등(중복 보고 무해).

**요청**
| 필드 | 타입 | 필수 | 설명 |
|---|---|---|---|
| `pId` | int(1~30000) | ✔ | 피스 ID |
| `barcode` | string(≤200) | ✔ | 바코드 |
| `chuteNo` | int(>0) | ✔ | 목적지 슈트번호 |
| `agvNo` | int | ✔ | AGV 번호 |
| `qty` | int(≥0) | – | 선택(미전송 시 IF-05 등록값 사용 — 전량 틸트) |
| `timeStamp` | string(≤30) | – | 선택 |

**응답** `200 {result:"OK"}`
```bash
curl -X POST http://20.24.10.147:5205/api/v1/deposit-report \
  -H 'Content-Type: application/json' \
  -d '{"pId":5001,"barcode":"0701-CELL-01","chuteNo":1,"agvNo":1,"qty":1,"timeStamp":"2026-07-10 04:20:05"}'
```
```json
{"result":"OK"}
```
- 핸드셰이크 완료 후 해당 셀 `currentQty`·오더 `sortedQty`가 증가(모니터로 확인 — §4).

---

## 3. 투입 플로우 (한 피스 사이클)

```
[IF-05] destination-query  → {result:OK, chuteNo}       (목적지·수량 판정, 피스 기록)
   ↓ AGV가 슈트로 이동
[IF-09] arrival-report      → {result:OK}                (도착 기록 + 3D 소터 2층 정렬)
   ↓ 소터 ready 전이(운영층 도달)
[IF-10] deposit-report      → {result:OK}                (틸트 보고 + Modbus 핸드셰이크)
   ↓ WCS 내부: 셀 배정 → C_Flag → R_Flag → sorter_command COMPLETED
   결과: 셀 currentQty +qty, 오더 sortedQty +qty
```

---

## 4. 모니터링 엔드포인트 (읽기 전용 — 테스트 관측용)

| 엔드포인트 | 반환 |
|---|---|
| `GET /api/monitor/sorters` | `[{destId,chuteNo,online,ready,full,paused}]` |
| `GET /api/monitor/sorters/{destId}/cells` | `[{cellNo,capacity,currentQty,occupied,enabled,assignedOrderNo}]` |
| `GET /api/monitor/orders?take=` | `[{id,orderNo,orderType,destinationChuteNo,status,plannedQty,reservedQty,sortedQty}]` |
| `GET /api/monitor/orders/{id}/items` | `[{id,barcode,plannedQty,reservedQty,sortedQty}]` |
| `GET /api/monitor/sorter-commands?destId=&take=` | `{items:[{id,pId,barcode,cellNo,cSeq,rSeq,status,cWrittenAt,rFlagAt}],nextCursor}` |
| `GET /api/monitor/operation-log?take=&category=` | `{items:[{id,at,category,action,level,barcode,pId,detail}],nextCursor}` — API·PLC_WRITE·HANDSHAKE 전 단계 기록 |

- `sorters[].ready` = 소터 운영 준비도 = `online && CurFloor==운영층(2) && Ready(D4.2)==1`. 부팅 직후엔 1층이라 `false`, IF-09 정렬 후 `true`.
- 모니터링 UI(웹): 브라우저로 `http://20.24.10.147:5205/` (WCS가 SPA 동일 포트 서빙).

---

## 5. 테스트 데이터 (현재 등록분)

- **오더/바코드**: `0701-CELL-01` ~ `0701-CELL-20` (GENERAL, RUNNING, plannedQty=9999). 바코드 = orderNo.
- **소터**: destId=1, chuteNo=1 (SORTER_3D).
- **셀**: cellNo 1~20, 각 capacity=3, 각 오더 `0701-CELL-NN`에 사전 배정(enabled).
- 예: 바코드 `0701-CELL-03` → 셀3에 적재.

> 초기화가 필요하면 WCS 운영자에게 요청(시드/리셋 스크립트로 재설정). 데이터는 **테스트 전용**.

---

## 6. 아웃바운드 푸시 — IF-08 UpdateChuteState (이 환경에선 비활성)

WCS→RCS 아웃바운드는 **UpdateChuteState 한 채널**이다(RCS 제공 문서
[UpdateChuteState_API_EN.md](UpdateChuteState_API_EN.md) 기반 확정 계약).

| 항목 | 값 |
|---|---|
| 와이어 | `PUT {RCS base}/api/UpdateChuteState` + `{chute_numbers[], next_states[]}` (**snake_case**) |
| 의미 | `next_state` = 수용 상태 플래그: **3**(Manual open)=받을 수 있음 · **2**(Pause)=받을 수 없음(분류중·이동중·미정렬·오프라인·정지) |
| 트리거 | 목적지 수용 상태 실제 전이 시(주기 아님 — 소터는 분류 사이클마다 2↔3 반복 가능) |
| 활성화 | **RCS가 수신 base URL을 WCS 측에 전달**하면 활성화(현재 미전달 → **비활성**) |

> ⚠ 현재는 운영자 일시정지/재개 전이만 발신되며, **수용(ready) 전이의 2/3 발신은 다음 업데이트에서
> 반영 예정**이다 — 반영 전까지는 채널을 활성화해도 분류중/오프라인 전이는 푸시되지 않는다.

---

## 7. 검증 결과 (2026-07-10, 배포 VM E2E)

**Part I · B2C (IF-05/09/10)**
| 시나리오 | 결과 |
|---|---|
| IF-05 정상 | `{result:OK, chuteNo:1}` |
| IF-09 → 2층 정렬 | `{result:OK}` + 소터 `ready:false→true` |
| IF-10 → 핸드셰이크 | `{result:OK}` + sorter_command **COMPLETED**(cSeq1→rSeq1), 셀1 `currentQty 0→1`, 오더1 `sortedQty 0→1` |
| IF-05 검증실패(pId=0) | `400` |
| IF-05 미존재 바코드 | `{result:NG, chuteNo:null}`(NO_DEST) |
| 헬스/모니터/구조화로그/프론트 | 전부 정상 |

**Part II · B2B (`/api/v1/works/*` 5종)** — 스펙 [api-spec-ko.html](api-spec-ko.html)
| 시나리오 | 결과 |
|---|---|
| `GET unprocessed` | 생성 데이터 그룹화 반환(0건이면 `[]` 200) |
| `POST input` / `classification` / `results` / `box` | 전부 `{status:"S","message":"Success"}` |
| box 중복 재전송 | `F "Box already exists…"` |
| classification 슈트 불일치 | `F "Chute mismatch: … expected chute(s) [002], received 001."` |
| bizDay 형식 오류 | `400 F "BizDay must be in YYYYMMDD or YYYY-MM-DD format."` |
| 미등록 바코드 | `F "Barcode not found, or bizDay/batch does not match…"` |
| 조회 API(`/api/logs/input·sort`, `/api/boxes`, `/api/test-data/comparison`) | 전부 정상(comparison `isMatch:true`) |

> 실패 `message` 문구가 스펙 문서 §6 실패표와 **정확히 일치**함을 확인. 테스트 잔여 데이터는 검증 후 전량 초기화됨(셀·오더·B2B 로그 클린 상태).

> 전체 스펙(판정표·시퀀스·B2B `/api/v1/works/*`)은 [wcs_rcs_interface_kr.html](wcs_rcs_interface_kr.html) 참조.
