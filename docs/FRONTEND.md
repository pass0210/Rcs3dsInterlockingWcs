# WCS 프론트엔드 설계 문서 (다중 스프린트 관통)

> 상태: **설계(코드 구현 전)**. 이 문서는 단일 스프린트 계약이 아니라 F1~F3 여러 스프린트를 관통하는 설계 기준이다.
> 각 페이즈의 실제 착수는 이 문서를 근거로 **페이즈별 Sprint Contract**를 별도 작성·사용자 확인 후 진행한다(3-Tier).
> 스펙 소스 우선순위: `docs/SPEC.md`(§7-B 미확정 포함) → `docs/ERD.md` → `docs/*.html`. 코드 사실은 본 문서 작성 시점(develop, PR #23 병합 직후 + 감사 `tasks/audit-20260701-full.md`) 기준.

## 0. 목적·범위·전제

물류 테스트라인 WCS의 운영·엔지니어링용 웹 콘솔. 세 축:

1. **모니터링** — 작업데이터(배치/오더/오더아이템 진행), 로봇 이동중 데이터(in-flight piece), 분류 데이터(sorter_command 적재 결과·셀 현황).
2. **3DS 워드값 확인/수정** — 실시간 D0~D6 레지스터 관찰 + 제한적 쓰기.
3. **운영자 제어** — 슈트 비움(clear), destination PAUSED/RESUMED, 워드 조작.

**사용자 확정 전제(재론 없음)**: 스택 = React + TypeScript + Vite SPA. 사용 환경 = 사내망 소수 사용자(엔지니어·관리자), 데스크톱 위주.

### ★ 확정 결정 (2026-07-03 사용자 확인 — 본문 권고와 다른 경우 이 블록이 우선)

| # | 질문(§8) | 결정 | 본문 영향 |
|---|---|---|---|
| Q1 | 슈트 비움·PAUSED/RESUMED 경로 | **관리 API(이 프론트)로 확정** | §3.3·F3 그대로. SPEC §7-B 해당 항목 확정 처리 |
| Q2 | 워드 수정 범위 | **안전 3종만**(SetTgtFloor·ClearR·CellAssign) | §3.2의 "임의 레지스터/D4 비트 편집(Q2-b)"은 **도입하지 않음** — PlcGateway 코어 무변경 확정 |
| Q4 | 인증 | **인증 없음(사내망 신뢰)** — 사용자 명시 선택 | §4.5의 로그인/Windows 인증 미도입. F3에서 인증 구현 제거. **트레이드오프(명시)**: `destination_event.operator_id` 자동 귀속 불가 → 완화책으로 조작 확인 다이얼로그에 **작업자 이름 입력 필드**(자유 입력, detail/operator_id에 기록 — 인증은 아니나 감사 흔적 유지). 방어층 (a) 바인딩·방화벽 제한 + 내부망 전제 운영 문서 명문화는 **유지**(감사 C-26 해소 방식 = 명문화) |
| Q5 | UI 라이브러리 | **shadcn/ui + Tailwind** — 사용자 명시 선택 | §1.3 개정: antd 제외, **shadcn/ui + Tailwind CSS + TanStack Table**(headless 테이블 필요) 채택. §4·§5의 antd 컴포넌트 언급(Modal.confirm·Descriptions·Layout 등)은 shadcn 대응물(Dialog·Table·자체 레이아웃)로 대체. 구현량 증가 수용. 프론트 페이지 구현 시 `frontend-design` 스킬 참조 |
| Q3 | 소터 PAUSE 의미 | 기본안 채택: **순수 WCS-측 dispatch 게이트(PLC 쓰기 없음)** — F3 계약에서 재확인 | §3.3 그대로 |
| Q6 | 배치·빌드 자동화 | 기본안 채택: `frontend/` 루트 + Wcs.Api 정적 서빙. wwwroot 복사 자동화 여부는 F1 계약에서 결정 | §1.1~1.2 그대로 |

**이 프론트가 해소하는 감사 확정 갭**:
- A-8 / SPEC §7-B: `ChuteCapacityService.OnCleared`의 production 호출자 0 → 슈트 FULL 비가역. PAUSED/RESUMED 런타임 전이 표면 자체가 없음(현재 `IsPaused`는 기동 시 1회만 세팅).
- C-26 / SPEC §7-B: RCS 인바운드 API 전면 무인증 + `Urls=http://0.0.0.0:5080` 전 인터페이스 바인딩.

**절대규칙 준수(설계 불변식)**: 이 프론트의 모든 PLC 쓰기는 예외 없이 **기존 소터별 단일 쓰기 큐(`PlcWriteQueue`)**를 경유한다(#1). 컨트롤러가 Modbus를 직접 호출하지 않는다. TgtFloor 쓰기는 컨슈머의 `TgtFloor==0` 재확인 가드를 그대로 탄다(#2). WCS는 TgtFloor를 클리어하지 않는다(#3) — 단, 운영자 수동 리셋은 SPEC §7-B에 "절대규칙 3 예외 명문화" 대상으로 이미 등재된 협의 항목이며, 노출 시 반드시 경고+확인을 건다. 모든 타이밍/설정은 appsettings 외부화(#7).

---

## 1. 아키텍처 결정

### 1.1 프로젝트 배치 — 권고: 리포 루트 `frontend/` (별도 .NET 프로젝트 아님)

현 솔루션(`backend/Wcs.sln`)은 순수 .NET 8개 프로젝트로 구성되고 전부 `backend/src/`·`backend/tests/` 아래에 있다. React 앱은 .NET 프로젝트(.csproj)가 아니므로 `backend/src/Wcs.*` 네이밍/`.sln` 등록 대상이 아니다.

**권고: 리포 루트에 `frontend/` 디렉터리(Node 툴체인 격리).**
- 근거: (a) `.sln`을 순수 .NET으로 유지 — `dotnet build`/`dotnet test`/pre-commit hook 무영향. (b) npm/pnpm/node_modules를 .NET 빌드 그래프 밖에 둔다. (c) Windows Service 단일 배포 모델 유지를 위해 **빌드 산출물(`frontend/dist`)만 `backend/src/Wcs.Api/wwwroot`로 복사**해 Wcs.Api가 정적 서빙(1.2 참조).
- 대안 각하: `backend/src/Wcs.Web/`(SpaProxy + SPA 템플릿) — MSBuild가 npm을 구동해 결합도가 높아지고, 현 단일-서비스·수동 배포 흐름과 마찰. `backend/src/` 규칙(=.NET 프로젝트)과도 어긋남. 채택하지 않음.
- `.gitignore` 추가 필요: `frontend/node_modules/`, `frontend/dist/`, `backend/src/Wcs.Api/wwwroot/`(빌드 산출물 — 소스 아님). `frontend/`의 소스만 커밋.

```
frontend/                 Vite + React + TS 소스 (npm 프로젝트, .sln 무등록)
  src/                    페이지·컴포넌트·API client·SignalR client
  index.html
  vite.config.ts          dev proxy → http://localhost:5080
  package.json
backend/src/Wcs.Api/wwwroot/  ← frontend/dist 복사본(정적 서빙 대상, .gitignore)
```

### 1.2 배포·서빙 — 권고: Wcs.Api가 정적 서빙(단일 서비스 유지)

- **운영**: `frontend`를 `npm run build` → `dist/`를 `backend/src/Wcs.Api/wwwroot/`에 배치 → **Wcs.Api가 `UseStaticFiles()` + SPA fallback(`MapFallbackToFile("index.html")`)로 서빙**. Windows Service 하나로 API + UI를 함께 제공(기존 배포 모델·`install-service.ps1` 무변경).
  - Program.cs 현재 상태: `app.MapControllers(); app.Run();`뿐 — **정적 서빙 미들웨어 없음**. F1에서 추가한다. 순서 주의: `UseStaticFiles()` → (인증 미들웨어, F3) → `MapControllers()` → `MapFallbackToFile("index.html")`. fallback은 `/api/**`를 삼키지 않도록 API 라우트가 우선.
  - **A-12 정합**: Serilog 파일 경로가 상대경로(`logs/`)여서 서비스 CWD(System32) 문제가 있듯, `wwwroot`도 `ContentRootPath`(=`AppContext.BaseDirectory`, `UseWindowsService`가 설정) 기준으로 해석되는지 확인. `UseStaticFiles` 기본은 `IWebHostEnvironment.WebRootPath`(ContentRoot/wwwroot)라 서비스에서도 정상. 배포 README에 명기.
- **개발**: Vite dev server(`npm run dev`, 기본 :5173) + `vite.config.ts` proxy로 `/api`·`/hubs`(SignalR) → `http://localhost:5080`. 프론트/백엔드 동시 기동(`dotnet run --project backend/src/Wcs.Api` + `npm run dev`). 개발 중에만 cross-origin이므로 CORS는 dev 한정(1.4·4.5).

### 1.3 라이브러리 선정 (최소 셋 — 과의존 금지)

| 관심사 | 권고 | 근거 |
|---|---|---|
| 라우팅 | **React Router** (`react-router-dom`) | 표준. 페이지 2~3개 + 탭. 학습비용 0. |
| 서버 상태 | **TanStack Query** (`@tanstack/react-query`) | 모니터링 데이터 폴링·캐시·무효화·로딩/에러 상태 일원화. SignalR 이벤트로 `invalidateQueries` 연동(2.3). |
| 실시간 | **@microsoft/signalr** | 백엔드가 ASP.NET Core — SignalR이 1급 시민. 재연결·백프레셔·그룹 내장. |
| UI 컴포넌트 | **shadcn/ui + Tailwind CSS** (★확정 결정 Q5 — antd 권고를 사용자 선택으로 대체) | 컴포넌트 코드 직접 소유(벤더 종속 없음)·디자인 원본성. Dialog(확인 다이얼로그)·Badge·Card·Tabs 등 조립. 구현량 증가 수용. |
| 테이블 | **TanStack Table** (`@tanstack/react-table`) | shadcn/ui는 headless라 데이터 테이블 엔진 필요 — shadcn DataTable 패턴(TanStack Table 기반)이 표준 경로. 서버 페이징·정렬·필터 조립. |
| 차트 | (F1~F3 미도입) | 초기 범위는 테이블·상태 뷰로 충분. 추세/KPI 차트가 필요해지면 `@ant-design/charts` 또는 Recharts를 후속 페이즈에서 추가(미확정 §8-Q5). |

**UI 라이브러리 트레이드오프(정직하게)**: CLAUDE.md "Design Quality First / 원본성"은 antd의 기성 룩과 다소 상충한다. 그러나 대상이 **소수 내부 사용자용 밀집 데이터 운영툴**이고 속도·일관성·한국어·테이블/폼/모달 완비가 우선이라 antd를 권고한다. 원본성 높은 bespoke 디자인이 요구되면 shadcn/ui(Tailwind + Radix + TanStack Table 조립)로 전환 가능하나 F1~F3 구현량이 늘어난다 — 사용자 판단 필요 시 §8-Q5.

**최소 의존 셋(★확정)**: `react` `react-dom` `react-router-dom` `@tanstack/react-query` `@tanstack/react-table` `@microsoft/signalr` + `tailwindcss`(+shadcn/ui 컴포넌트 — 코드 소유 방식이라 런타임 의존 아님, radix-ui 프리미티브 수반). 빌드: `vite` `typescript` `@vitejs/plugin-react`. 테스트: `vitest` `@testing-library/react` + Playwright(7장).

---

## 2. 실시간 설계 (SignalR)

**허브 위치**: `Wcs.Api`에 SignalR 허브 신설. Program.cs에 `AddSignalR()` + `MapHub<WcsMonitorHub>("/hubs/monitor")`. 인증(F3)은 허브에도 적용.

백엔드에는 이미 실시간 결선에 재사용할 훅이 있다 — **새 폴 루프를 만들지 않는다**:
- `PlcPollingService` 이벤트: `OnRegisterChange(reg, old, new)`(변화분만·무변화 0), `OnOfflineTransition`/`OnOnlineTransition`, `OnWrite(action, detail)`.
- `HandshakeOrchestrator.OnStage(action, detail)`.
- `IOperationLogger`(단일 싱크 `OperationLogService`) — **모든 동작이 통과하는 단일 초크포인트**. operation_log 테일의 최적 소스.
- `ChuteCapacityService.OnChuteStateChanged(destId)` — 슈트 hold 전이.
- `SorterBundleHandle.Subscribe*`(위 훅을 번들 단위로 노출) + `Latest`(현재 스냅샷).

### 2.1 소터 워드 스냅샷 스트림 (①)

- **전략: 변화분 push + 스냅샷 부트스트랩 + 저빈도 하트비트.**
  - **부트스트랩**: 클라이언트 접속(`OnConnectedAsync`) 시 서버가 `ISorterGatewayRegistry.AllBundles`의 각 `Latest`(전체 D0~D6 + Online)를 1회 전송 → 늦게 접속한 클라이언트도 즉시 완전 상태 확보.
  - **델타**: `SorterBundleHandle.SubscribeRegisterChange`를 relay 서비스가 구독해 변화분(reg, old, new, chuteNo)만 push. 이는 이미 operation_log `POLL_CHANGE` 훅과 **동일 소스**이므로 관측 훅 하나에 SignalR 브로드캐스트를 나란히 얹는다(POLL_CHANGE 기록 로직 재사용 — 신규 폴 없음).
  - **하트비트**: 저빈도(예: 1s, appsettings — 절대규칙 #7) 전체 스냅샷 1회 push로 델타 유실·재연결 갭 보정. Online/Offline 전이는 `SubscribeOffline/Online`으로 즉시 push.
- **주의**: relay는 폴/쓰기 스레드에서 직접 호출되므로 콜백은 **논블로킹·예외 격리**(기존 훅 계약과 동일). SignalR 전송은 `IHubContext`로 fire-and-forget, 예외 흡수(fail-safe) — 관측이 본 동작(150ms 폴·핸드셰이크)을 지연시키지 않는다.

### 2.2 operation_log 실시간 테일 (②)

- **소스**: `OperationLogService`(단일 컨슈머). 여기서 배치 flush 직전/직후 각 엔트리를 `IHubContext`로 브로드캐스트(그룹: `oplog`). DB 영속화와 별개 경로 — 기록 실패가 스트림을, 스트림 실패가 기록을 막지 않음.
- **필터**: 클라이언트가 category/level/sorterChuteNo로 구독 그룹 선택 가능(초기엔 전량 push + 클라이언트 측 필터로 단순화 가능). 고빈도 `POLL_CHANGE`는 기본 스트림에서 제외하거나 옵트인(콘솔 폭주 방지 — DB 정책과 동형).
- **백로그**: 접속 시 최근 N행은 REST(`GET /api/monitor/operation-log?take=N`)로 로드하고, 이후는 SignalR로 append(무한 스크롤/테일 UX).

### 2.3 모니터링 데이터 갱신 전략 (③) — 데이터별 권고

| 데이터 | 갱신 | 근거 |
|---|---|---|
| 소터 워드 D0~D6 | **SignalR push**(2.1) | 초당 다수 변화·저지연 필요. |
| operation_log 테일 | **SignalR push**(2.2) | 이벤트 스트림 본질. |
| 배치/오더/오더아이템 진행 | **TanStack Query 폴링(2~5s)** + 이벤트 무효화 | 상태가 IF-05/09/10·핸드셰이크 완료에만 바뀜. 폴링이 단순·복원력↑. SignalR API/HANDSHAKE 이벤트 수신 시 `invalidateQueries`로 근실시간 보정(행별 push 없이). |
| in-flight piece | **폴링(2~3s)** + 이벤트 무효화 | 동. |
| 셀 현황·sorter_command | **폴링(2~5s)** + 핸드셰이크 이벤트 무효화 | 동. |
| destination readiness(full/paused/online) | **폴링(2~5s)** + STATE 이벤트 무효화 | `DestinationStatusService.Compute` 재사용. |

원칙: **고빈도·저지연 = push, 집계·목록 = 폴링(+이벤트 무효화)**. 행 단위 push 남발 금지.

---

## 3. API 표면 (신규 컨트롤러)

기존 `RcsController`(`/api/v1/*`, RCS↔WCS 계약)는 **불변**. 신규 표면은 별도 컨트롤러·별도 라우트로 분리하고 F3에서 인증을 건다.

- **`MonitoringController`** (`/api/monitor/*`, 읽기 전용)
- **`OpsController`** (`/api/ops/*`, 쓰기/제어 — 인증·확인 필수)

읽기 쿼리는 기존 `DbRepositories`에 없는 조회가 많으므로 **읽기 전용 쿼리 서비스**(`IMonitoringQueries`, EF `AsNoTracking`)를 신설 — 기존 리포지토리·핫패스 무영향.

### 3.1 MonitoringController (읽기 전용)

| 엔드포인트 | 반환 | 원천 |
|---|---|---|
| `GET /api/monitor/batches` | work_batch 목록(상태·일자·차수) | `work_batch` |
| `GET /api/monitor/orders?batchId=&status=` | 오더 진행(order_no·type·destination·status·planned/reserved/sorted 합) | `wcs_order` + `order_item` 집계 |
| `GET /api/monitor/orders/{id}/items` | 오더아이템(barcode·planned/reserved/sorted) | `order_item` |
| `GET /api/monitor/pieces/in-flight` | 이동중 piece(status∈QUERIED/RESERVED/PERMITTED) | `piece` |
| `GET /api/monitor/sorters` | 소터 목록 + 현재 스냅샷(Latest) + readiness | `ISorterGatewayRegistry` + `DestinationStatusService` |
| `GET /api/monitor/sorters/{destId}/cells` | 셀 현황(cell_no·capacity·현재 적재 qty·배정 오더·점유여부) | `cell`·`cell_assignment`·`SorterCellQty`(재사용) |
| `GET /api/monitor/sorter-commands?destId=&take=` | 적재 결과 이력(c_seq/r_seq·status) | `sorter_command` |
| `GET /api/monitor/destinations` | 슈트/소터 상태(full/paused/online·work_full_qty·last_cleared_at) | `destination`·`chute_detail`·`DestinationStatusService` |
| `GET /api/monitor/operation-log?category=&level=&from=&to=&sorterChuteNo=&take=&cursor=` | operation_log 조회(시계열·커서 페이징) | `operation_log`(선두 인덱스 `at` 활용) |
| `GET /api/monitor/alarms?acked=` | 알람 목록 | `alarm` |

주의: operation_log·piece는 대량 → **커서/키셋 페이징**(`at` 또는 `id` 기준), `take` 상한 강제. A-3 정합 — piece 조회는 인덱스 없는 풀스캔을 피하도록 destination/status/시간 범위로 좁힌다.

### 3.2 OpsController — 워드 쓰기 (전부 소터별 단일 쓰기 큐 enqueue)

**PlcWrite 레코드 종류(현재)**: `SetTgtFloor(int Floor)`, `CellAssign(int CellNo, int Seq)`, `ClearR`. 이 세 개가 큐 컨슈머(`ProcessWriteAsync`)가 처리하는 전부다. 워드 쓰기는 이들을 `SorterBundleHandle` 경유로 enqueue한다.

| 엔드포인트 | 매핑 | 위험도·가드 |
|---|---|---|
| `POST /api/ops/sorters/{destId}/tgtfloor {floor}` | `bundle.EnqueueSetTgtFloorAsync`(기존) → `PlcWrite.SetTgtFloor` | **⚠ 절대규칙 #2/#3 위반 가능.** 컨슈머가 `TgtFloor==0` 재확인 후에만 씀(진행 중 덮어쓰기 자동 스킵·로그). UI는 현재 TgtFloor를 보여주고 **≠0이면 "핑퐁 차단됨"·"진행 중 쓰기 무시" 경고**. `floor==0` 요청(수동 클리어)은 절대규칙 #3 예외 조작 → **강한 경고 + 재확인 + operator_id 필수**(SPEC §7-B "TgtFloor 잔류 해소·운영자 수동 리셋" 항목 결선). |
| `POST /api/ops/sorters/{destId}/clear-r` | 신규 `bundle.EnqueueClearRAsync` → `PlcWrite.ClearR` | 진단용. R_Flag/R영역 강제 클리어. 핸드셰이크 상태 오염 가능 → 경고+확인. `SorterBundleHandle`에 enqueue 메서드 1개 추가(번들 표면 확장 — **코어 무변경**). |
| `POST /api/ops/sorters/{destId}/cell-assign {cellNo, seq}` | 신규 `bundle.EnqueueCellAssignAsync` → `PlcWrite.CellAssign` | **고위험 진단용.** 정상 셀 지정은 IF-10 핸드셰이크가 수행 — 수동 CellAssign은 핸드셰이크·셀 회계와 경합 가능 → **관리자 전용·강경고·확인**. 번들 표면 확장(코어 무변경). |

**임의 레지스터 편집(D0~D6 raw / D4 비트)**: 현재 PlcWrite 유니온에 "임의 워드 쓰기"가 **없다**. "워드값 수정"을 위 3개 안전 쓰기를 넘어 진짜 임의 편집까지 열려면 **새 PlcWrite 레코드**(예: `WriteRawRegister(ushort addr, short value)` / `SetD4Bit`)를 유니온에 추가하고 `ProcessWriteAsync`에 case를 넣어야 한다 → **`Wcs.PlcGateway` 코어 변경 = 보호구역(절대규칙 #1 경로) 수정**. 권고: 기본 범위는 위 3개(안전·의미 명확)로 한정하고, 임의 편집은 **관리자 전용·기본 비활성 기능 플래그** 뒤에 두며 도입 여부를 §8-Q2로 사용자 확정. 도입 시에도 반드시 단일 큐 경유 + 전수 operation_log + 강경고.

**자동 감사(무료)**: 위 큐 쓰기는 컨슈머 `EmitWrite`가 `OnWrite`를 발화 → 이미 operation_log `PLC_WRITE`(`SET_TGTFLOOR`/`CELL_ASSIGN`/`CLEAR_R`/`RMW_D4`)로 **자동 기록**된다(SorterRegistryFactory 구독). 즉 워드 쓰기 감사는 기존 결선을 그대로 탄다. operator_id 귀속은 OpsController가 요청 시 별도 `API`/`OPS` 로그 1행을 남겨 보완(3.4).

### 3.3 OpsController — 운영자 조작

| 엔드포인트 | 결선 | 감사 |
|---|---|---|
| `POST /api/ops/chutes/{destId}/clear` | **`IChuteCapacityService.OnCleared(destId)` 호출** — **감사 확정 갭 A-8 'OnCleared production 호출자 0' 해소.** last_cleared_at 갱신 + 인메모리 리셋 + `OnChuteStateChanged` 발화(이미 구현됨, 호출자만 없음). | `destination_event(CLEARED, operator_id)`는 `OnCleared` 내부에서 이미 append(단 현재 operator_id 미기입 → operator_id 전달 인자 추가). |
| `POST /api/ops/destinations/{destId}/pause` | **신규 백엔드 작업 필요** — 런타임 PAUSED 전이 메서드가 없다. `destination.Status=PAUSED` DB 갱신 + `destination_event(PAUSED, operator_id)` + (슈트면 `ChuteCapacityService` 인메모리 `IsPaused=true` + `OnChuteStateChanged`; 소터는 `DestinationStatusService.ComputeSorter`가 DB의 Status를 직접 읽으므로 인메모리 불요). | `destination_event(PAUSED)` |
| `POST /api/ops/destinations/{destId}/resume` | 위의 역 — `Status=NORMAL` + `destination_event(RESUMED)` + 인메모리 해제. | `destination_event(RESUMED)` |

**PAUSED/RESUMED 신규 백엔드 상세**: `ChuteCapacityService`는 `IsPaused`를 **기동 시 `InitializeFromDbAsync`에서만** 세팅한다(런타임 전이 없음). 따라서 `OnPaused(destId, operatorId)`/`OnResumed(...)`를 신설해 DB Status 전이 + destination_event + 인메모리 반영 + 이벤트 발화를 한 트랜잭션 단위로 수행하는 서비스(예: `IDestinationControlService` 또는 `ChuteCapacityService` 확장)를 F3에서 구현한다. 소터 PAUSE는 순수 WCS-측 dispatch 게이트(IF-05·push)만 바꾸고 **PLC 쓰기 없음**(§8-Q3 확인).

### 3.4 감사 카테고리 — operation_log

`OperationLogCategory` = `API/PLC_WRITE/POLL_CHANGE/HANDSHAKE/STATE`(CHECK 제약 enum). 운영자 조작 기록 옵션:
- **경량(권고 기본)**: 신규 카테고리 없이 재사용 — clear/pause/resume은 `STATE`(action `CLEARED`/`PAUSED`/`RESUMED`, 이미 STATE 훅이 FULL/PAUSED/NORMAL을 씀) + 워드 쓰기는 `PLC_WRITE`(자동). operator 귀속은 `detail` JSON(`operatorId`)과 **`destination_event.operator_id`(정규 감사)**로.
- **명시(옵션)**: `OPS` 카테고리 신설(`OPS_CLEAR`/`OPS_PAUSE`/`OPS_RESUME`/`OPS_SET_TGTFLOOR`/`OPS_WRITE_REG`) — 운영자 발원 조작을 한 필터로 분리. **CHECK 제약 변경 마이그레이션 필요**(SqlServer·Sqlite 2종). operation_log엔 operator_id 컬럼이 없으므로 귀속은 detail JSON. 정규 감사 이력은 여전히 `destination_event`(operator_id 컬럼 보유).

권고: F3는 경량으로 시작하고, 운영자 액션 분리 필터 요구가 확인되면 `OPS` 카테고리를 후속. **정규 감사 단일 진실은 `destination_event`**(append-only·operator_id 보유).

---

## 4. 안전 설계 (핵심)

모든 쓰기/제어는 아래 5중 방어를 통과한다.

1. **단일 쓰기 큐 경유(불변)**: 모든 PLC 쓰기는 `SorterBundleHandle`→소터별 `PlcWriteQueue`→단일 컨슈머(`ProcessWriteAsync`)로만. 컨트롤러는 Modbus를 직접 만지지 않는다. clear/pause/resume은 PLC 쓰기가 아니라 서비스 메서드(DB+인메모리) 경유.
2. **확인 다이얼로그(UI)**: 모든 쓰기/제어는 antd `Modal.confirm`으로 "대상·현재값·요청값·위험" 재확인. 파괴적 조작(cell-assign·raw write·TgtFloor=0)은 문구 강조 + 필요 시 대상 식별자 재입력.
3. **operation_log 전수 기록**: 워드 쓰기는 `PLC_WRITE` 자동. clear/pause/resume은 `STATE`(또는 `OPS`) + `destination_event`. 실패/거부도 기록. 결과(큐 스킵 등)는 후속 `POLL_CHANGE`/`PLC_WRITE`로 추적 가능.
4. **규칙 위반 가능 조작 경고**: `TgtFloor≠0`인데 SetTgtFloor 요청(핑퐁 차단 대상), `TgtFloor=0` 강제 쓰기(절대규칙 #3 예외), FULL/PAUSED/OFFLINE 소터에 쓰기, 진행 중(Ready==0) 조작 → UI가 현재 스냅샷을 근거로 사전 경고. 서버는 컨슈머 가드로 재검증(예: TgtFloor 재확인 스킵은 이미 존재).
5. **간단 인증 + 바인딩 제한(§4.5)**.

### 4.5 인증 — 옵션 비교 및 최소안 권고

> **★확정(Q4, 2026-07-03)**: **인증 없음(사내망 신뢰)** — 아래 옵션 비교는 기록용. 유지되는 것: (a) 바인딩·방화벽 제한 + 내부망 전제 운영 문서 명문화(감사 C-26 해소 = 명문화 방식), 조작 확인 다이얼로그의 **작업자 이름 입력**(자유 입력 → destination_event.operator_id/detail 기록 — 인증 아님·감사 흔적용). F3 범위에서 로그인 구현 제거.

현재: 무인증 + `0.0.0.0:5080`(감사 C-26/SPEC §7-B). 프론트는 이 갭과 함께 해소한다.

| 옵션 | 장점 | 단점 | operator_id 귀속 |
|---|---|---|---|
| API 키 헤더 | 가장 단순, 설정 1줄 | 사용자 구분 없음, 공유 비밀 | 불가(단일 키) |
| 간단 로그인(쿠키 세션) | 사용자별 감사, 소수 계정 config/DB, 구현 단순 | 계정 관리 최소 필요 | **가능** |
| Windows 인증(Negotiate/AD) | 도메인 PC 무암호 SSO, AD 연동 | 도메인 조인 전제, 서비스 계정 구성 | 가능(AD 계정) |

**권고(2층 방어)**:
- (a) **바인딩·방화벽**: `Urls`를 실제 운영 NIC IP로 좁히거나 내부망 전제 + 방화벽(5080 인바운드를 RCS/운영 PC로 제한)을 `install-service.ps1`/운영 README에 명문화(C-26 권고 그대로).
- (b) **인증 대상 분리**: `/api/ops`·`/api/monitor`·`/hubs/*`에 인증 적용. `/api/v1`(RCS)은 계약 주체가 RCS라 별도(SPEC §7-B "RCS 인증 Q" 대기) — 최소 API 키 1개라도 옵션(설정 외부화)으로 두고 문서화.
- (c) **최소안 권고 = 간단 로그인(쿠키 기반)**. 이유: **운영자 제어(clear/pause/resume)는 `destination_event.operator_id` 감사가 계약(ERD)**이라 사용자 식별이 필요하다. 소수 계정을 config(해시)·소형 테이블로 관리. 도메인 조인 환경이 확인되면 **Windows 인증(Negotiate)을 선호안으로 승격**(무암호 SSO + AD 귀속) — §8-Q4로 사용자 확정.
- 프론트는 prod에서 Wcs.Api가 **동일 출처**로 서빙하므로 CORS 불요. dev만 Vite proxy(동일 출처처럼 동작) → CORS는 개발 편의로 최소 허용 or proxy로 회피.

---

## 5. 페이지/화면 설계 (데스크톱 위주)

전역: 좌측 내비 + 상단 상태바(소터 Online/Offline·알람 배지·연결 상태). antd `Layout`. `ConfigProvider locale=ko_KR`. 넓은 다열 밀집 레이아웃.

### 페이지 ① 모니터링 대시보드 (`/monitor`)

- **A. 작업 데이터**: work_batch 선택 → 오더 테이블. 컬럼: order_no·type·destination(chuteNo)·status·planned/reserved/sorted(진행 바). 행 확장 → order_item(barcode·planned/reserved/sorted). 갱신 2~5s 폴링 + API/HANDSHAKE 이벤트 무효화. 필터: 배치·상태.
- **B. 로봇 이동중 데이터(in-flight piece)**: piece(status∈QUERIED/RESERVED/PERMITTED). 컬럼: pId·barcode·qty·destination(chuteNo)·agvNo·inductionNo·status·시각. 갱신 2~3s. 정렬: 최신순.
- **C. 분류 데이터**:
  - 셀 현황(소터별): cell_no·capacity·현재 적재 qty·배정 오더·점유/여유·enabled. `SorterCellQty` 재사용. 색상 태그(여유/만재/비활성).
  - sorter_command 이력: piece·cell·c_seq·r_seq·status(SENT/COMPLETED/MISMATCH/TIMEOUT)·시각. 핸드셰이크 이벤트 무효화.
- 상호작용: 행 클릭 → 상세 drawer(연관 piece_event·operation_log 발췌).

### 페이지 ② 3DS 워드 + 제어 (`/sorters/:destId` 또는 탭)

- **레지스터 패널(실시간)**: D0 C_CellNo·D1 C_Seq·D2 R_CellNo·D3 R_Seq·D4 비트(C_Flag·R_Flag·Ready)·D5 CurFloor·D6 TgtFloor·Online. SignalR 스트림(2.1). **변경값 하이라이트(깜빡임)**, 각 값에 마지막 변경 시각. Descriptions/Statistic로 표현.
- **워드 편집(3.2)**: SetTgtFloor(현재 TgtFloor·≠0 경고 표시), Clear-R(진단), Cell-Assign(관리자), (옵션)Raw write. 각 컨트롤은 확인 모달 + 현재값 + 규칙 위반 경고.
- **운영자 제어(3.3)**: 이 목적지 Pause/Resume 토글, 슈트면 Clear 버튼. 확인 + operator 표시.
- **operation_log 라이브 테일(2.2)**: 하단 패널. category/level 필터. 자동 스크롤 토글. `POLL_CHANGE` 기본 접힘(옵트인).

### (옵션) 관제 요약 (`/`)
소터별 상태 카드(Online·CurFloor·TgtFloor·Ready·full/paused) + 미확인 알람 카운트 + 최근 operation_log. F2 이후 여유 시.

---

## 6. 페이즈 분할 (각 페이즈 = 3-Tier 스프린트 1개)

의존: **F1 → F2 → F3** 순차. 각 페이즈는 자체 Sprint Contract로 착수.

### F1 — 스캐폴드 + 정적 서빙 + 모니터링 읽기
- 범위: `frontend/` Vite+React+TS 스캐폴드, antd/router/query 셋업, dev proxy. Wcs.Api `UseStaticFiles`+`MapFallbackToFile` + wwwroot 배치. `MonitoringController`(읽기 전용) + `IMonitoringQueries`(AsNoTracking). 페이지 ① 모니터링(A/B/C, 폴링). `.gitignore`·빌드 산출물 경로.
- **Done**: `dotnet run --project backend/src/Wcs.Api` 후 `:5080`에서 SPA가 서빙되고, 배치/오더/in-flight/셀/sorter_command가 폴링으로 표시. 기존 146 스위트 GREEN + 신규 MonitoringController 통합 테스트. Playwright로 페이지 로드·데이터 표시 검증(`.mcp.json` 필요·7장).
- 선행 결선: 없음(읽기만). 정적 서빙 미들웨어 순서 확정.

### F2 — SignalR 실시간 + 워드 뷰(읽기 전용)
- 범위: `AddSignalR`+`WcsMonitorHub`(`/hubs/monitor`). relay 서비스(번들 Subscribe*·OperationLogService 브로드캐스트·부트스트랩·하트비트). 페이지 ② 레지스터 패널(실시간·읽기) + operation_log 테일. TanStack Query ↔ SignalR 이벤트 무효화 연동.
- **Done**: D0~D6 값이 SignalR로 실시간 갱신(변경 하이라이트), operation_log 테일 스트리밍, 재연결 시 부트스트랩 스냅샷 복구. relay가 폴/핸드셰이크 타이밍에 영향 0(관측 훅 계약 유지). 통합 테스트(허브 접속→스냅샷 수신) + Playwright(값 변화 관찰). 기존 스위트 GREEN.
- 의존: F1(서빙·모니터링 골격).

### F3 — 워드 쓰기 + 운영자 제어 + 인증
- 범위: `OpsController`(워드 쓰기 큐 enqueue + clear/pause/resume). **`OnCleared` 결선(A-8 해소)**. **PAUSED/RESUMED 런타임 전이 신규 백엔드**(`destination_event` 감사). 인증(§4.5) + 바인딩 제한(C-26 해소). 페이지 ② 편집·제어 UI(확인 모달·규칙 경고). (옵션)`OPS` 카테고리 + 마이그레이션.
- **정책 확정 포함(중요)**: 이 페이즈는 **SPEC §7-B "슈트 비움/PAUSED 운영 조작 = 관리 API vs RCS IF" 미확정을 '관리 API(이 프론트)'로 확정**하는 결정을 담는다. 착수 Sprint Contract에 이 결정을 명시하고 사용자 사인오프를 받는다(§8-Q1). RCS IF 신설이 아니라 이 관리 콘솔이 운영 조작의 인바운드 표면이 된다.
- **Done**: 운영자가 UI에서 clear/pause/resume + 워드 편집을 **단일 쓰기 큐 경유·확인·전수 감사(operation_log + destination_event.operator_id)**로 수행. FULL 슈트가 clear로 복구됨(A-8 갭 실증). 인증 미적용 접근 차단·바인딩 제한. **Sim3ds로 워드 쓰기 검증**(SetTgtFloor→D6 반영 등, 7장). 기존 스위트 GREEN + 신규 OpsController/전이 통합 테스트.
- 의존: F2(실시간 뷰 위에 제어 얹음).

---

## 7. 테스트/검증 전략

- **프론트 단위/컴포넌트**: Vitest + @testing-library/react — API client·상태 변환·경고 로직(TgtFloor≠0 등).
- **프론트 E2E**: **Playwright**. Evaluator의 브라우저 검증 의무(fresh evidence) 대비. **`.mcp.json` 부재 → 신설 필요**(Playwright MCP, Web/UI 프로젝트 규칙: `{"mcpServers":{"playwright":{"command":"cmd","args":["/c","npx","@playwright/mcp@latest","--headless"],...}}}`). F1 스프린트에서 `.mcp.json` 생성 포함.
- **백엔드 신규 API**: 기존 146 xUnit 스위트에 `WebApplicationFactory` 통합 테스트 추가 — MonitoringController(조회 형상·페이징), OpsController(clear→OnCleared 호출·pause/resume→destination_event, 워드 쓰기→큐 enqueue), 인증(미인증 401·인증 통과). 기존 스위트 회귀 0.
- **워드 쓰기 Sim3ds 검증 경로**: OpsController → `bundle.EnqueueSetTgtFloorAsync` → 단일 큐 컨슈머 → `Sim3ds`(FluentModbus TcpServer)가 D6 쓰기 수신 → 폴 스냅샷/시뮬레이터 상태에서 값 변화 단언. 기존 Sim3ds 통합 테스트 패턴 재사용(TgtFloor==0 가드·핑퐁 차단 케이스 포함). ClearR/CellAssign 노출 시 각 레지스터 반영 단언.
- **실시간**: 허브 접속→부트스트랩 스냅샷 수신, 레지스터 변경→델타 push, operation_log append→테일 수신 통합 테스트. E2E 병렬 부하 flake 교훈(todo S-E2E) 준수 — 무거운 실-Sim 테스트는 직렬 컬렉션 고려.

---

## 8. 미확정/질문 — **전건 해소됨 (★확정 결정 블록 참조, 2026-07-03)**

- **Q1**: ✅ **관리 API(이 프론트)로 확정** — SPEC §7-B 해당 항목 확정 처리, F3에서 구현.
- **Q2**: ✅ **안전 3종만** — 임의 레지스터/D4 비트 편집 도입 안 함(PlcGateway 코어 무변경).
- **Q3**: ✅ 기본안 — 소터 PAUSE = 순수 WCS-측 dispatch 게이트(PLC 쓰기 없음). F3 계약에서 재확인.
- **Q4**: ✅ **인증 없음(사내망 신뢰)** — §4.5 확정 블록 참조(바인딩 제한·내부망 명문화·작업자 이름 입력은 유지).
- **Q5**: ✅ **shadcn/ui + Tailwind + TanStack Table** — §1.3 개정. 차트는 F1~F3 미도입(필요 시 후속).
- **Q6**: ✅ 기본안 — `frontend/` 루트 + Wcs.Api 정적 서빙. 복사 자동화 여부는 F1 계약에서 결정.

---

## Planner Self-Check

- **설계 범위**: 사용자 확정 3항(스택 React+TS+Vite / 모니터링+워드 확인·수정+운영자 제어 / 사내망 소수·최소 보호)을 모두 반영. 코드 미작성(설계 문서만). 요청된 8개 필수 항목(아키텍처·실시간·API·안전·화면·페이즈·테스트·미확정) 전부 포함.
- **절대규칙 점검**: #1 — 모든 PLC 쓰기가 기존 소터별 단일 큐(`PlcWriteQueue`) 경유로 고정, 컨트롤러 직접 Modbus 금지 명시. #2 — SetTgtFloor는 컨슈머 `TgtFloor==0` 재확인 가드를 그대로 타고 UI가 ≠0 경고. #3 — WCS 비클리어 원칙 유지, TgtFloor=0 수동 리셋은 SPEC §7-B 예외 후보로 강경고+operator 귀속. #7 — 하트비트/폴 주기 등 신규 타이밍도 appsettings 외부화 명시. #8 — 판정 로직(DepositDecider·DestinationStatusService) 재사용, 신규 순수 판정 없음.
- **감사 갭 결선 확인**: A-8(OnCleared 호출자 0) → F3 `/api/ops/chutes/{destId}/clear`로 해소. PAUSED/RESUMED 런타임 전이 부재 → F3 신규 백엔드. C-26(무인증·0.0.0.0) → F3 인증+바인딩 제한. 각 갭을 페이즈에 명시 귀속.
- **페이즈 분할 근거**: 위험·의존 오름차순 — F1은 읽기만(결선 0·회귀 위험 최소)로 인프라 확립, F2는 관측 훅 재사용(본 동작 무변경)으로 실시간, F3만 쓰기/제어·인증·신규 백엔드·정책 확정(고위험)을 격리. 각 페이즈가 독립 Done 조건 + 기존 146 스위트 GREEN 회귀 게이트를 갖는 3-Tier 단위.
- **미검증·전제**: (a) `UseStaticFiles`가 Windows Service ContentRoot 기준 정상 서빙(A-12 상대경로 이슈와 유사 — 배포 검증 필요). (b) 임의 워드 편집(Q2-b) 도입 시 코어 변경 = 보호구역 → 사용자 승인 선행. (c) SignalR relay가 폴/핸드셰이크 핫패스에 무영향임은 F2 부하 테스트로 실증 필요. 모두 문서에 표시.
