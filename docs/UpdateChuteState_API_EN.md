# API: Update Chute State

## Endpoint

```
PUT /api/UpdateChuteState
```

Updates the state of one or more chutes to **Pause** or **Manual Open**.

---

## Per-floor host routing

WCS runs one push channel **per floor** and sends each `UpdateChuteState` call to the RCS host of the
destination's floor:

| Floor | RCS receiving host |
|---|---|
| Floor 1 | `http://192.168.0.151:3000` |
| Floor 2 | `http://192.168.0.152:3000` |

- The path (`PUT /api/UpdateChuteState`) and the payload (`{chute_numbers[], next_states[]}`) are
  **identical on both hosts** — the floor is conveyed only by which host receives the call.
- A **3D sorter serves both floors by aligning**. While it is aligned and ready for floor *F*, the
  **floor-*F* host** receives `next_state=3` (can receive) for that chute and the **other floor's host**
  receives `next_state=2` (cannot — the sorter is not serving that floor now).
- A **fixed (non-3D) chute** is pushed only to **its own floor's host**.

---

## Request

### Headers

```
Content-Type: application/json
```

### Body

```json
{
  "chute_numbers": [1001, 1002],
  "next_states": [2, 3]
}
```

| Field | Type | Required | Description |
|---|---|---|---|
| `chute_numbers` | array[int] | Yes | List of chute IDs to update (external/display IDs) |
| `next_states` | array[int] | Yes | List of target states, matching `chute_numbers` in order and length |

> `chute_numbers[i]` corresponds to `next_states[i]` by index.

---

## Supported States (`next_states`)

| Value | Name | Description |
|---|---|---|
| `2` | **Pause chute** | Temporarily closes the chute, without clearing data or calling external systems |
| `3` | **Manual open** | Manually opens the chute, without clearing data or calling external systems |

### State `2` — Pause chute

- The chute is temporarily closed (`status` set to `0`).
- **No** external webhook is called, and the current session data is **not** cleared.
- The chute's current session ID is preserved.

### State `3` — Manual open

- The chute is opened (`status` set to `1`).
- **No** external webhook is called, and the current session data is **not** cleared.
- A new session ID may be generated depending on the system's operating mode.

---

## Request Examples

### Example 1: Pause a single chute

```json
PUT /api/UpdateChuteState
{
  "chute_numbers": [1001],
  "next_states": [2]
}
```

### Example 2: Manually open a single chute

```json
PUT /api/UpdateChuteState
{
  "chute_numbers": [1001],
  "next_states": [3]
}
```

### Example 3: Update multiple chutes at once

```json
PUT /api/UpdateChuteState
{
  "chute_numbers": [1001, 1002, 1003],
  "next_states": [2, 3, 2]
}
```

---

## Response

### Success — `200 OK`

```json
{
  "flag": 1,
  "result": [
    {
      "status": 0,
      "msg": "",
      "chute_id": 1001,
      "last_changed": 1719999999000
    },
    {
      "status": 1,
      "msg": "",
      "chute_id": 1002,
      "last_changed": 1719999999500
    }
  ]
}
```

| Field | Type | Description |
|---|---|---|
| `flag` | int | `1` indicates the request was processed successfully |
| `result` | array | List of update results per chute |
| `result[].status` | int | New status of the chute (`0`: closed, `1`: open) |
| `result[].msg` | string | Message field (currently always empty) |
| `result[].chute_id` | int | Chute ID (matches the ID sent in `chute_numbers`) |
| `result[].last_changed` | int | Timestamp (milliseconds) of the update |

### Error — missing parameters

```
400
```

Returned when `chute_numbers` or `next_states` is missing from the request body.

### Error — processing failure

```json
{
  "result": "Failed"
}
```

Status code: `400`

Returned when an error occurs during processing (e.g. invalid chute ID, internal system error, etc.).

---

## Integration Notes

- `chute_numbers` and `next_states` must have the **same length** and be in the **same corresponding order**.
