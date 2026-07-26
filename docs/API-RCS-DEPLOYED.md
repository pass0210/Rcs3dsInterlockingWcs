# WCS ↔ RCS API Reference (As Deployed · Test Environment)

> A **hands-on API reference** for RCS integration developers. Every example/response below was
> actually called and verified against the deployed server (E2E, 2026-07-10). For the full design
> spec (decision tables, sequences), see [wcs_rcs_interface.html](wcs_rcs_interface.html).

## 0. Connection

| Item | Value |
|---|---|
| **Base URL** | `http://20.24.10.147:5205` |
| Protocol | HTTP / JSON (internal network assumed — no auth, single port) |
| Environment | Test server + Sim3ds (sorter simulator) |
| Direction | **This test environment is inbound-only** — RCS→WCS (IF-05/09/10). The WCS→RCS push (IF-08) is **inactive** (§6). |
| Generated docs | **None** (no Swagger/Scalar/OpenAPI exposed). This document is the API reference. |

> ⚠ Sim3ds stands in for the real 3DS PLC. Sorter behavior (floor moves, sorting) is simulated —
> timing may differ from the real PLC.

### Field conventions (all interfaces)
`pId, agvNo, barcode, inductionNo, chuteNo, qty, timeStamp` — JSON is **camelCase**.
`timeStamp` format: `"yyyy-MM-dd HH:mm:ss"` (local). `pId` range: **1–30000**.

---

## 1. Health check — `GET /health`

Process liveness is always 200; read `db` and `sorters[].online` in the body for status.
```bash
curl http://20.24.10.147:5205/health
```
```json
{"status":"ok","db":true,"sorters":[{"chuteNo":1,"online":true,"lastPollAt":"2026-07-10T04:16:21Z"}]}
```
- `db:true` = WCS internal DB connection OK. `sorters[].online:true` = sorter (Sim3ds) polling OK.

---

## 2. RCS inbound APIs (IF-05 / IF-09 / IF-10)

All three are `POST` with `Content-Type: application/json`. Business outcomes come back as
**200 + `result`**; only input-validation failures return **400**.
NG reasons (NORMAL·BUSY·FULL·PAUSED·NO_DEST, …) are recorded in **WCS internal logs only** —
RCS receives just `result`.

### 2-1. IF-05 Destination Query — `POST /api/v1/destination-query`
Called once per piece at the induction scan. WCS records the piece and decides the destination and quantity.

**Request**
| Field | Type | Required | Description |
|---|---|---|---|
| `pId` | int (1–30000) | ✔ | Piece ID (per AGV tray) |
| `agvNo` | int | ✔ | AGV number (for records/audit) |
| `barcode` | string (≤200) | ✔ | Product barcode → order matching key |
| `inductionNo` | int | ✔ | Induction number (used to **decide the target floor 1/2** for a 3D-sorter destination) |
| `qty` | int (≥1) | ✔ | Quantity (full-tilt basis — source of truth) |
| `timeStamp` | string (≤30) | ✔ | `"yyyy-MM-dd HH:mm:ss"` |

**Response** `200 {result, chuteNo}` — on OK, `chuteNo` (destination chute number); on NG, `chuteNo=null`.
```bash
curl -X POST http://20.24.10.147:5205/api/v1/destination-query \
  -H 'Content-Type: application/json' \
  -d '{"pId":5001,"agvNo":1,"barcode":"0701-CELL-01","inductionNo":1,"qty":1,"timeStamp":"2026-07-10 04:20:00"}'
```
```json
{"result":"OK","chuteNo":1}
```
- Unmatched barcode → `{"result":"NG","chuteNo":null}` (internal reason NO_DEST).
- Validation failure (e.g. `pId:0`) → `400` with an error body.
- Decision summary: a **plain chute** returns OK even when full/paused (send and wait). A **3D sorter**
  returns NG when `PAUSED`, NG (FULL) when it cannot receive by cell, and OK when BUSY (sorting/moving —
  still move the AGV). Full table: spec HTML §6.
- **Per-sorter ordering · AGV parking (RCS side)**: sorter-destination pieces are received in
  **induction order (FIFO)** — WCS aligns the sorter to each piece's floor (1/2) in turn, and once the
  aligned floor is open (IF-08 `3`) that piece is deposited. **If not yet receivable, the AGV waits in a
  parking zone (not at the destination)** and moves to the destination on the IF-08 `3` (open) push
  (directly if already open) — so for a not-yet-open destination the open push precedes arrival.

### 2-2. IF-09 Arrival Report — `POST /api/v1/arrival-report`
Called once when the AGV reaches the destination chute, right before depositing. WCS records the
arrival. **No floor field** — a 3D sorter's floor alignment was already done at IF-05 (the floor is
derived from `inductionNo`).

**Request**
| Field | Type | Required | Description |
|---|---|---|---|
| `pId` | int (1–30000) | ✔ | Piece ID |
| `chuteNo` | int (>0) | ✔ | Arrived destination chute number |
| `agvNo` | int | ✔ | AGV number |
| `timeStamp` | string (≤30) | – | Optional |

**Response** `200 {result:"OK"}`
```bash
curl -X POST http://20.24.10.147:5205/api/v1/arrival-report \
  -H 'Content-Type: application/json' \
  -d '{"pId":5001,"chuteNo":1,"agvNo":1,"timeStamp":"2026-07-10 04:20:00"}'
```
```json
{"result":"OK"}
```
- A sorter's floor alignment starts at IF-05; once aligned, `ready` in `/api/monitor/sorters`
  transitions `false→true` (observable).
- A non-existent/inactive `chuteNo` still returns 200 (arrival recorded, alignment skipped) — no 500.

### 2-3. IF-10 Deposit Report — `POST /api/v1/deposit-report`
Sent after the tilt. For a 3D destination, WCS internally triggers the **Modbus handshake
(cell assignment → C_Flag → R_Flag)**. Idempotent (duplicate reports are harmless).

**Request**
| Field | Type | Required | Description |
|---|---|---|---|
| `pId` | int (1–30000) | ✔ | Piece ID |
| `barcode` | string (≤200) | ✔ | Barcode |
| `chuteNo` | int (>0) | ✔ | Destination chute number |
| `agvNo` | int | ✔ | AGV number |
| `qty` | int (≥0) | – | Optional (when omitted, the IF-05 registered qty is used — full tilt) |
| `timeStamp` | string (≤30) | – | Optional |

**Response** `200 {result:"OK"}`
```bash
curl -X POST http://20.24.10.147:5205/api/v1/deposit-report \
  -H 'Content-Type: application/json' \
  -d '{"pId":5001,"barcode":"0701-CELL-01","chuteNo":1,"agvNo":1,"qty":1,"timeStamp":"2026-07-10 04:20:05"}'
```
```json
{"result":"OK"}
```
- After the handshake completes, the cell's `currentQty` and the order's `sortedQty` increase
  (observable via the monitor — §4).

---

## 3. Deposit flow (one piece cycle)

```
[IF-05] destination-query  → {result:OK, chuteNo}       (destination/qty decision, piece recorded · induction→floor F, sorter pre-aligned to F)
   ↓ AGV travels to the chute (sorter ready once aligned to floor F)
[IF-09] arrival-report      → {result:OK}                (arrival recorded · no floor field)
   ↓
[IF-10] deposit-report      → {result:OK}                (tilt report + Modbus handshake)
   ↓ inside WCS: cell assignment → C_Flag → R_Flag → command COMPLETED
   result: cell currentQty +qty, order sortedQty +qty
```

---

## 4. Monitoring endpoints (read-only — for test observation)

| Endpoint | Returns |
|---|---|
| `GET /api/monitor/sorters` | `[{destId,chuteNo,online,ready,full,paused}]` |
| `GET /api/monitor/sorters/{destId}/cells` | `[{cellNo,capacity,currentQty,occupied,enabled,assignedOrderNo}]` |
| `GET /api/monitor/orders?take=` | `[{id,orderNo,orderType,destinationChuteNo,status,plannedQty,reservedQty,sortedQty}]` |
| `GET /api/monitor/orders/{id}/items` | `[{id,barcode,plannedQty,reservedQty,sortedQty}]` |
| `GET /api/monitor/sorter-commands?destId=&take=` | `{items:[{id,pId,barcode,cellNo,cSeq,rSeq,status,cWrittenAt,rFlagAt}],nextCursor}` |
| `GET /api/monitor/operation-log?take=&category=` | `{items:[{id,at,category,action,level,barcode,pId,detail}],nextCursor}` — every API/PLC-write/handshake step recorded |

- `sorters[].ready` = sorter receiving readiness = `online && CurFloor==target floor F && Ready==1`
  (F = induction-derived floor). It is `false` while not aligned to the target floor; `true` once aligned.
- Monitoring UI (web): open `http://20.24.10.147:5205/` in a browser (served by WCS on the same port).

---

## 5. Test data (currently registered)

- **Orders/barcodes**: `0701-CELL-01` … `0701-CELL-20` (GENERAL, RUNNING, plannedQty=9999). Barcode = orderNo.
- **Sorter**: destId=1, chuteNo=1 (SORTER_3D).
- **Cells**: cellNo 1–20, capacity 3 each, pre-assigned to orders `0701-CELL-NN` (enabled).
- Example: barcode `0701-CELL-03` → loaded into cell 3.

> To reset, ask the WCS operator (re-seeded by script). All data is **test-only**.

---

## 6. Outbound push — IF-08 UpdateChuteState (inactive in this environment)

The WCS→RCS outbound is a **single channel: UpdateChuteState** (fixed contract based on the
RCS-provided document [UpdateChuteState_API_EN.md](UpdateChuteState_API_EN.md)).

| Item | Value |
|---|---|
| Wire | `PUT {per-floor RCS host}/api/UpdateChuteState` + `{chute_numbers[], next_states[]}` (**snake_case**) |
| Per-floor host | Routed to the changed destination's **floor** — **floor 1** `http://192.168.0.151:3000` · **floor 2** `http://192.168.0.152:3000`. A sorter gets `3` on the host of its currently-aligned floor and `2` on the other floor's host |
| Meaning | `next_state` = receivability flag: **3** (Manual open) = can receive · **2** (Pause) = cannot (sorting / moving / not aligned / offline / paused) |
| Trigger | Only on an actual receivability transition (not periodic — a sorter may toggle 2↔3 every sorting cycle). **On startup, every destination's current state is sent once** (a sorter to both floor hosts — current floor=`3` when receivable / other floor=`2`; a plain chute to its single host), then only on transition |
| Activation | Activated once **RCS configures its per-floor receiving hosts with WCS** (not configured yet → **inactive**) |

---

## 7. Verification results (2026-07-10, deployed-server E2E)

**Part I · B2C (IF-05/09/10)**
| Scenario | Result |
|---|---|
| IF-05 normal | `{result:OK, chuteNo:1}` |
| IF-09 arrival report | `{result:OK}` |
| IF-10 → handshake | `{result:OK}` + command **COMPLETED** (cSeq1→rSeq1), cell 1 `currentQty 0→1`, order 1 `sortedQty 0→1` |
| IF-05 validation failure (pId=0) | `400` |
| IF-05 unknown barcode | `{result:NG, chuteNo:null}` (NO_DEST) |
| Health / monitor / structured logs / frontend | All OK |

**Part II · B2B (`/api/v1/works/*`, 5 endpoints)** — spec: [api-spec-ko.html](api-spec-ko.html)
| Scenario | Result |
|---|---|
| `GET unprocessed` | Returns generated data grouped (empty `[]` with 200 when none) |
| `POST input` / `classification` / `results` / `box` | All `{status:"S","message":"Success"}` |
| Duplicate box resend | `F "Box already exists…"` |
| Classification chute mismatch | `F "Chute mismatch: … expected chute(s) [002], received 001."` |
| Malformed bizDay | `400 F "BizDay must be in YYYYMMDD or YYYY-MM-DD format."` |
| Unknown barcode | `F "Barcode not found, or bizDay/batch does not match…"` |
| Query APIs (`/api/logs/input·sort`, `/api/boxes`, `/api/test-data/comparison`) | All OK (`isMatch:true`) |

> Failure `message` strings match the spec document's failure tables **exactly**. Test data is
> provided in a clean, initialized state.

> For the full spec (decision tables, sequences, B2B `/api/v1/works/*`), see
> [wcs_rcs_interface.html](wcs_rcs_interface.html).
