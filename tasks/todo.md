# TODO (sprint 간 추적 — Minor·이연 항목)

## S-TRACE-LOG-VIEWER 후속 (4-Tier 코드리뷰 Minor 6건 — 비차단·오케스트레이터 이관, Evaluator APPROVED 시 등재)
- [ ] [S-TRACE-LOG-VIEWER][reconnect] 뷰어 SignalR 재접속(onreconnected) 시 백로그 재시드 미수행 — 재연결 갭 동안 놓친 트레이스 이벤트가 테이블에 안 채워질 수 있음(하트비트 없음). 재접속 훅에서 REST 백로그 재조회 검토.
- [ ] [S-TRACE-LOG-VIEWER][config] `TraceLogOptions.Directory` 기본값이 코드 리터럴 `@"D:\Rcs3dsInterlockingWcsLogs"`(옵션 기본값·appsettings 오버라이드 가능하나, 절대규칙 #7 정신상 리터럴 0을 원하면 appsettings-only 로 이동 검토). — 현 상태 비차단(설정 바인딩됨).
- [ ] [S-TRACE-LOG-VIEWER][perf] `TraceLogService.TailLines`가 `need*4` 줄을 전 롤링파일에서 역순 수집 후 필터 — 큰 파일·좁은 필터에서 비효율(전량 스캔 근접). 인덱스/역방향 스트리밍 read 검토(현 clamp≤500·저빈도라 비차단).
- [ ] [S-TRACE-LOG-VIEWER][robustness] 이벤트 detail JSON을 문자열 보간(`$"{{\"barcode\":\"{req.Barcode}\"...}}"`)으로 생성 — barcode에 `"`/`\` 포함 시 깨진 JSON 가능(현 바코드 검증이 위생하나 방어적으로 JsonSerializer 사용 권장).
- [ ] [S-TRACE-LOG-VIEWER][robustness] 컨슈머 루프가 `WaitToReadAsync` false(채널 완료) 후 종료하는 one-shot 구조 — 정상 수명엔 문제없으나 예외로 루프 이탈 시 이후 레코드 드롭(로그만). 재기동/감시 검토.
- [ ] [S-TRACE-LOG-VIEWER][perf] 뷰어 필터 변경마다 subscribeTrace 해제/재구독(그룹 churn) — SubscribeTrace/UnsubscribeTrace 왕복 발생. 클라 필터는 로컬 유지하고 구독은 페이지 수명 1회로 분리 검토.
- [ ] [S-TRACE-LOG-VIEWER][concurrency·문서화됨·스코프밖] 한 소터 **동시** IF-10 시 pId↔cSeq 상관 교차 가능(순차 dispatch 전제·SPEC §6). 코드 주석 + 뷰어 note로 노출 완료. 동시 IF-10 직렬화는 별도 스프린트(lessons: single-sorter-concurrent-handshake-gap).

## S-IF10-CWRITE-SETTLE-DELAY 후속 (Evaluator Minor — 비차단)
- [ ] [일관성] `HandshakeOrchestrator.SettleDelayAsync`의 폴 스텝 폴백 `_opt.RFlagPollMs > 0 ? _opt.RFlagPollMs : 50`이 타 루프(arming·복귀 대기·C_Flag 대기)의 `_opt.RFlagPollMs` 직접 사용과 미세 비일관(그쪽은 폴백 없음). 지연량과 무관한 방어용 폴백이나(RFlagPollMs는 항상 >0), 스타일 통일 시 폴백 제거 또는 공용 상수화 검토. (APPROVED 시 Evaluator 지적 — 현 스프린트 비차단.)
- [ ] [S-BACKEND-FOLDER][기존부채·보안] **SQLitePCLRaw.lib.e_sqlite3 2.1.10 high-severity advisory (NU1903 / GHSA-2m69-gcr7-jv3q)** — 빌드 경고 10건(Wcs.Data/Migrations.SqlServer/Migrations.Sqlite/Api/Tests). 폴더 이동과 무관·base develop 선재. EF Core Sqlite provider 상향 또는 명시적 SQLitePCLRaw pin으로 해소. 계약의 "빌드 경고 0" 게이트를 다시 만족시키려면 필요.
- [ ] [S-BACKEND-FOLDER][config·스코프밖] `.claude/settings.json` 권한 allowlist가 구 `src/Wcs.Api/...` 경로 참조 — 이동 후 최악의 경우 권한 프롬프트 추가뿐(빌드/실행 실패 아님). 사용자 후속 결정(config·미승인 영역).

## S-DEV-SEED-GUARD 후속 (Minor — 스코프 OUT 이관·비차단)
- [ ] [S-DEV-SEED-GUARD][하드닝] **DbSeeder `SeedWorkBatchAndOrders`의 `First(ChuteNo==1 && CHUTE)`는 명시 `SeedOnStartup=true`를 현장-토폴로지 DB(chute 1이 CHUTE 아님)에 걸면 여전히 "Sequence contains no elements" 크래시** — 자동 시드 게이트 차단(본 스프린트)으로 사고 경로에선 도달 불가하나, 명시 true를 잘못된 DB에 걸면 재발 가능. `FirstOrDefault()`+null skip 하드닝 검토. (참고: 빈 스크래치 DB 대조 재현에서는 이 First 크래시 대신 시드된 SORTER_3D chuteNo=30 ↔ appsettings Sorters[ChuteNo=1] 미스매치로 SorterRegistry fail-loud가 먼저 발생 — 둘 다 명시 시드를 부적합 DB에 걸었을 때의 downstream 증상.)

## 2026-07-01 전체 감사(7차원×적대적 검증) — 신규 확정 23건 + minor/info 20건. **상세·근거·검증노트: `tasks/audit-20260701-full.md`** (사용자 결정: 기록만·착수는 후속)
스프린트 묶음(감사 보고서 §D — 착수 시 이 단위로 계약):
- [ ] **[묶음 A — 현장 quick-fix]** ①OFFLINE 지속 중 폴 루프가 매 150ms 스택 전문+거짓 '전이' ERROR 무억제 반복(`PlcGateway.cs:286` — failures 미리셋+isHardEx 매회, 전이 1회만 상세로) + Serilog File `rollOnFileSizeLimit` 미설정(1GB 도달 시 그날 로그 조용히 유실) ②`/health` 엔드포인트(생존·소터 Online·DB — AllBundles.Latest 읽기만) ③입력 상한: IF-05 qty(int 오버플로로 OVER 우회·ReservedQty 오염)·IF-10 음수 qty·barcode 200자/timeStamp 30자(초과 시 500+DENIED 감사행 롤백+operation_log 같은 배치 최대 255행 드롭).
- [ ] **[묶음 B — 운영(Windows Service) 배포 전 차단]** ①Serilog 상대경로 `logs/` → 서비스 CWD(System32)행/제한계정 조용한 유실(절대경로화 또는 `Directory.SetCurrentDirectory(AppContext.BaseDirectory)`) ②install-service.ps1: LocalSystem+SQL 권한 실패 시 5초 무한 크래시루프·password 미지원·`depend= MSSQLSERVER` 부재(사전 연결점검 추가) ③운영 README(M5-P4와 병합).
- [ ] **[묶음 C — 데이터 정합(운영 투입 전)]** ①동시 IF-05(같은 barcode) rowversion 충돌 미처리 500(재시도/원자 UPDATE — DENIED 기록 계약 보존) ②piece 활성 비활성화가 FirstOrDefault 1행만(전건 처리 — 다중 활성 잔존 시 정상 IF-10이 '멱등 OK'로 위장 유실+IF-11 미트리거) ③인덱스 2종: `piece(PId,IsActive)`(필터드 유니크는 status 조건 탓 이 조회에 못 씀·영구 테이블 풀스캔)·`order_item(Barcode)`(IF-05 매 호출 스캔) ④**ReleaseCell destination 스코프 누락**(CellNo만으로 전 소터 해제 — 멀티소터 2대째 투입 전 필수, ICellSelector 시그니처에 destId) ⑤SelectCell 오더 미매칭 시 배정 기록 없이 셀 반환(REPORTED_DIRECT 물리 적재가 DB상 빈 셀 → 혼적, fail-loud로).
- [ ] **[묶음 D — 핸드셰이크 견고화(운영 투입 전)]** ①**R_Flag '레벨' 읽기 → stale 스냅샷(≤150ms 창) 허위 RSEQ_MISMATCH + off-by-one 연쇄 자가지속**(`HandshakeOrchestrator.cs:184` — RFlag==0 선관찰 arming 또는 기존 RFlagRaised 에지 채널 소비. **IT4b flake의 유력 실기전·F1b per-소터 직렬화와 별개 근본원인, 같은 스프린트로 묶기**) ②CFlagTimeout 단독 단언 테스트(현 D5는 CFLAG-or-RFLAG 택일이라 C_Flag 무한대기 회귀 통과) ③재시작 reconciliation(기동 시 잔류 R_Flag ClearR+로그, journal.CreateSent를 투입 직후로) ④IF-10 RecordDeposit false 3원인(DENIED 재보고/미존재 chuteNo/중복)이 전부 '멱등 OK' 로그로 합류 — enum 분리+WARN+현동작 고정 테스트.
- [ ] **[묶음 E — 문서 일괄 정정]** ①CLAUDE.md drift(Minimal API→Controller·IF-09 누락·IF-08=푸시·"M5에서 Serilog 도입"→완료·16→17테이블·Migrations 2종 누락) ②README 전면 stale(폐지된 IF-08 폴링·TCP만·SQLite 개발 — RCS 개발사 오도 위험) ③master_spec §05 'FULL·PAUSED는 NG'가 확정4(슈트=OK) 미반영 — canonical HTML 2종 충돌 ④appsettings.Development.json "dotnet run 기본 환경" 주석 허위(launchSettings 부재로 실제 기본=Production) ⑤**Dev 시드(chuteNo=30)↔base Sorters(ChuteNo=1) 미스매치 — Development 기동 시 현장 DB 오염+fail-loud**(기존 'Dev에 Provider=Sqlite 오버라이드' 항목과 같은 스프린트) ⑥TASKS.md M3 구모델·WcsDbContext 주석 16→17.
- [ ] **[정책 확정 4건 — 사용자·3DS·RCS 협의(SPEC §7-B 등재됨)]** IF-09 Hold 예외 / 오더 완료 소유자 / 슈트 비움 IF / API 무인증 내부망 전제.
- 신규 minor/info 20건(IF-10 chuteNo 불일치 회계·Pusher stale 역전·SetTgtFloor 캐시 재확인·TeardownGuard InvalidOperationException 과포괄·마스터데이터 재시작 요구·AUTO 이중 배정·CHECK 미구현·SQLite 토큰 사문화 등)은 보고서 §C 참조 — 해당 영역 스프린트 착수 시 함께.

## IF-05 동일 바코드 다중 목적지 (2026-06-30 조사 — 사용자 결정: SPEC 미확정 등재·후속)
- [x] [SPEC·동작갭] ~~**동일 바코드가 여러 활성 목적지에 걸릴 때 IF-05 비결정적**~~ **✅ 해소(2026-07-27, S-B2C-BARCODE-MULTI-FIX Fix 2)**: `DbRepositories.QueryDestination`의 정렬없는 `FirstOrDefault()`를 순수 `Wcs.Core.BarcodeDestinationSelector`(배정-우선 결정적 선택 — 배정확정 최신 DestAssignedAt → 최소 OrderId → 최소 OrderItemId)로 교체. 단위(`BarcodeDestinationSelectorTests`)+통합(`B2cBarcodeMultiFixTests`) 커버리지 추가. SPEC §7-B 확정 규칙으로 갱신.
- [ ] [동작갭] IF-05 조회에 **work_batch 필터 부재** → 교차 배치(어제/오늘) 동일 바코드 매칭 가능. 활성/당일 배치로 좁힐지 정책 확정 필요.

## S-CHUTESTATE-PUSH 이연 (고객사 UpdateChuteState 아웃바운드 — dormant)
- [ ] [활성화 결정·중요] **재동기화(reconciliation) 여부** — 현재 best-effort(라이브 PAUSED→2/RESUMED→3 전이만 전송). 재시도 소진(고객사 다운)·WCS 재시작 시 고객사 상태 뷰 divergence 가능. RcsPush는 startup bootstrap 보유. **활성화(고객사 host 제공) 시 고객사와 협의**: startup/주기 재동기화 추가할지(단 이 API는 Pause/Open만·normal 없어 "현재 PAUSED만 재전송" 등 시맨틱 협의 필요).
- [ ] [코드리뷰 #2·정합] `ChuteStatePushClient` (및 RcsPushClient 공통) 결정적 4xx(400/401/403/404)를 재시도함 — 낭비. 4xx 종단 처리(즉시 false)·5xx/408/429·transport만 재시도로 정렬. WCS가 항상 정상 body라 자가발생 불가(비긴급).
- [ ] [코드리뷰 #3] `ChuteStatePushClient.cs:46-47` XML doc "예외 안 던짐"이 부정확(OperationCanceledException은 전파) — "취소 시에만 throw, 그 외 false 수렴"으로 정정.
- [ ] [코드리뷰 #4] `ChuteStatePusher.cs:146` 종료 중 in-flight push의 `_cts?.Token` ObjectDisposedException이 ERROR로 로깅(오해 소지) — 토큰 로컬 스냅샷 또는 teardown ODE benign 처리.
- [ ] [코드리뷰 #5·DRY] `ComputeBackoffDelay`/`CombineUrl`이 ChuteStatePushClient↔RcsPushClient 바이트 중복(~15줄) — 공용 헬퍼 추출.
- [ ] [코드리뷰 #6] `ChuteStatePusher.DisposeAsync`가 `StopAsync().GetAwaiter().GetResult()` — 형제(DestinationStatusPusher)처럼 `await`로 정렬(무해).
- [ ] [Evaluator minor] ChuteStatePushClient DetailJson 수기 문자열 보간 → System.Text.Json 직렬화.

## S-F3B-FOLLOWUP 코드리뷰 이연 (Minor — 비차단, 주석/a11y)
- [ ] [주석] `OpsControls.tsx:63` bare `// #2`가 절대규칙 #N 관례와 충돌(여기선 스프린트 항목번호) — `// S-F3B-FOLLOWUP #2:` 접두어로 오독 방지.
- [ ] [a11y] `OpsControls.tsx:335,386,401` `<label htmlFor>` 도입으로 `aria-label` 중복(접근명 override) — 3개 aria-label 제거하거나 가시 라벨과 일치시킴.
- [ ] [주석] `OpsController.cs:264` Ready 게이트가 순수 advisory·컨슈머 백스톱 없음(Q3, TgtFloor/C_Flag만 fresh-read 백스톱)임을 한 줄 명시.
- [ ] [주석] `PlcGateway.cs:512-526` CellAssign이 D4를 2회 읽음(가드+RMW) — PLC측 D4 변화 클로버 방지 위한 의도이므로 "optimize away 금지" 주석.

## S-F3b 코드리뷰 이연 (Minor — 비차단, 운영제어 UI)
- [ ] [DRY] `OpsControls.tsx:288-289,328-329,340-341` + desc 문자열이 bound 리터럴(1~20/1~1000/1~30000)을 하드코딩 — `OPS_LIMITS`에서 유도해 `ops.ts` 단일소스 유지.
- [ ] [DRY] `ConnBadge`가 `OpsPage.tsx:118-137`·`SortersPage.tsx:56-70`에 동일 복제 — 공용 컴포넌트 추출(2번째 사본).
- [ ] [문서] `OpsControls.tsx:61` `currentTgt`(SignalR word) 사전 핑퐁 힌트는 advisory·응답 `pingPongGuard`가 authoritative임을 한 줄 주석.
- [ ] [a11y] bound 검증·operatorBlank가 토스트로만(입력에 `aria-describedby`/`aria-invalid` 미연결). 토스트는 aria-live라 발화되나 폴리시.
- [ ] [문서] 재사용 `Dialog` 초기 포커스가 operator 입력(취소 아님) — 여기선 합당하나 ConfirmDialog 안전기본과 상이함을 문서화.
- [ ] [cosmetic·Evaluator] readiness "Busy" vs WordPanel Ready=1 이중소스 표기 — 표시 일관성.

## S-CELL-ACCUM 이연 (Minor — 비차단, 셀 누적 바인딩)
- [ ] [코드리뷰 #6][방어] `DbRepositories.Finalize`가 이미 COMPLETED된 오더에 초과 piece 도착 시 SortedQty를 PlannedQty 초과로 증가시키고 SelectCell②가 새 셀 배정 — benign(ReservedQty 게이트가 선차단)이나 무가드 경로. `order.Status==COMPLETED`면 증가 skip/로그 검토.
- [ ] [정합성 minor·후속][동시성] 동시 동일-오더 IF-10을 한 소터에 보내면 SelectCell② read-then-create race로 **중복 활성 배정** 가능. 직렬 dispatch 전제(SPEC §6 물리 직렬)에선 미발생 — 동시 IF-10 허용 시 원자 배정 필요. 관련: [[single-sorter-concurrent-handshake-gap]].
- [ ] [정합성 minor][선재] `SelectCell`② 비-RUNNING 오더에 무배정 셀 반환(이번 스프린트 무변경·선재 동작).
- [ ] [정합성 minor][테스트] E2E AB A2가 주석은 "동일 셀" 주장하나 단언은 총 qty만 검증 — 단언 강화 여지.

## S-F3a 코드리뷰 이연 (Minor — 비차단, 운영제어 백엔드)
- [ ] [일관성] `OpsController`가 ILogger/IOperationLogger는 생성자 주입인데 WcsDbContext/IChuteCapacityService/ISorterGatewayRegistry/IDestinationControlService는 `[FromServices]` 메서드 주입 — MonitoringController(순수 생성자 주입)와 불일치.
- [ ] [async] O1 `ClearChute`가 동기 `FirstOrDefault`(async 메서드 내) + `OnCleared`가 CancellationToken 없이 FindAsync/SaveChangesAsync — 전이 경로는 RequestAborted 전달하는데 불일치. `FirstOrDefaultAsync`+토큰 권장.
- [ ] [일관성] O6가 C_Flag==1 컨슈머 스킵 가능성을 신호 안 함(O4는 pingPongGuard로 정직 보고) — `cFlagGuard`류 병렬 필드 검토.
- [ ] [DRY] O4/O5/O6 404 문자열 중복 → 헬퍼 추출.
- [ ] [감사] 워드쓰기·AlreadyInState pause/resume의 운영자 귀속이 operation_log(비내구·드롭가능)에만 남고 destination_event엔 없음(§3.4 경량 설계상 허용) — 큐 드롭 시 귀속 유실 인지.
- [ ] [테스트] Conflict 분기(DbUpdateConcurrencyException→Conflict)가 SQLite에서 XminRowVersion 미증가라 사실상 미도달 — prod SQL Server rowversion에서만 동작("실 prod provider 검증" 교훈). 인지.
- [ ] [견고성] detail/DetailJson 수기 JSON 문자열 조립(Esc는 operatorName만 커버) — 필드 추가 시 취약. System.Text.Json 직렬화 검토.
- [ ] [기존동작 인지] `OnCleared`가 InFlightQty도 0으로(선재) — A-8로 프로덕션 호출자 생기며, 예약-미투하 piece 보유 슈트 clear 시 만재집계에서 일시 "망각". 스펙 커버·의식적 결정.

## S-S5-FLAKE 코드리뷰 이연 (Minor — 비차단·방어적·현재 미발현, Sim 하네스)
- [ ] [대칭성] `SimServer.cs:182` `RegistersChanged += OnServerRegistersChanged` 구독에 대응 `-=` 부재(StopAsync/DisposeAsync). 현재 누수 0(서버·SimServer 동시 폐기·StartAsync 1회)이나, 향후 이중 StartAsync=이중구독/서버 수명역전=누수 함정. StopAsync에 `-=` 한 줄로 대칭화 권장(리뷰어 명시).
- [ ] [Fail Loud] `SimServer.cs:353-369` 핸들러 try/catch 부재 + FluentModbus가 핸들러 예외를 조용히 삼켜(연결 드롭·무로그) CLAUDE.md "예외 삼키지 말 것"과 상충. 현재 provably non-throwing이라 방어용 — try/catch+`_log.LogError` 검토.
- [ ] [정확성] `SimServer.cs:346-347` docstring "클리어가 관측상 전혀 반영되지 않음"은 과장(FC16이 R_CellNo/R_Seq는 0으로, R_Flag만 복원). 무해하나 "R_Flag가 관측상 클리어되지 않음"으로 정정 권장.
- [ ] [업그레이드 체크리스트] fix는 FluentModbus 5.3.2 3동작(동기 RegistersChanged가 lock(Lock) 내·응답 전 / GetHoldingRegisters lock-free / 변화-게이트 이벤트)에 의존. 메이저 업그레이드 시 S5/S5b가 canary — 업그레이드 체크리스트에 명기.

## S-B2B-2c 코드리뷰 이연 (Minor — 비차단, 인쇄·선택·설정)
- [ ] [터치] `DetailGrid.tsx:58-64` 드래그가 `pointerup`만 해제 → `pointercancel` 시 draggingRef 잔류. 또 터치 포인터는 implicit capture로 `onPointerEnter` 미발화 → 터치 드래그선택 무동작. 데스크톱 마우스(실배포)는 정상. `pointercancel` 리스너 추가 검토.
- [ ] [cosmetic] `SettingsPage.tsx:42,77` `Select`에 `w-full max-w-[280px]`가 inner `<select>`에 붙는데 `ui/select.tsx:8` 래퍼가 inline-flex content-width라 full-width 미해석. 전폭 의도면 래퍼에 width 부여.
- [ ] [문서] `DataGeneratorPage.tsx:182-185` 인쇄가 컬럼필터로 숨은 체크행도 대상(단일 소스=삭제와 동일 동작). 의도임을 한 줄 주석.
- [ ] [cleanup] `PrintLabelPreview.tsx:150` `row.barcode2 ?? ''`가 `dual` 분기 내부라 중복(무해).
- [ ] [일관성] `PrintLabelPreview.tsx:49` 미리보기 backdrop 클릭 미닫힘(`ui/dialog.tsx`는 닫힘). 의도(오조작 방지)일 가능성 — 일관성만 참고.

## S-B2B-3b 코드리뷰 이연 (Minor — 비차단, 프론트 조회 3페이지)
- [ ] [DRY] `ArchiveSelect.tsx`가 `ARCHIVE_LABELS`+보관 아이콘/라벨 마크업을 `DataGeneratorPage.tsx:29-33,234-248`와 중복. DataGeneratorPage도 ArchiveSelect 소비하도록 통합(라벨 단일 소스화).
- [ ] [UX] `BoxesPage.tsx:42` 선택된 박스 상세가 통합검색으로 마스터에서 필터아웃돼도 잔존(`selected`를 `rows`가 아닌 `filtered`에서 해석하거나 필터아웃 시 선택 해제).
- [ ] [UX] `TestLogGrid.tsx:157` 통합검색 haystack에 한글 `equipmentLabel`("인덕션"/"슈트") 포함 → "슈트" 검색 시 분류탭 전행 매치. 컬럼 실값만 대상으로 좁힐지 검토.
- [ ] [cosmetic] `ComparisonPage.tsx:148-159` GROUP border-left 구분선이 헤더행엔 있고 필터입력행엔 없음(`FilterCell`가 className 미수용, `SummaryGrid.tsx:130`). 그룹 경계선 필터행에서 끊김.
- [ ] [cosmetic] `LogsPage.tsx:27` Tabs `gap-3` + TabsContent `mt-4` 이중 세로 간격.
- [ ] [성능·의식적선택] 입력/분류/비교 그리드 미가상화(하루치 바코드 전량 렌더) — 기존 DetailGrid/SummaryGrid와 일관이라 이번 PR 허용. 대량 시 TanStack virtualization 검토.

## S-B2B-3a 코드리뷰 이연 (Minor — 비차단, 읽기전용 조회 API)
- [ ] [#2 방어] E1/E2/E5(`LogService`)는 `bizDay` 생략 시 상한 없이 전체 materialization(E3만 500 캡). 소규모·내부망 전제라 저위험이나 E1/E2에 안전 `Take` 또는 bizDay 필수 가드 검토.
- [ ] [#3 성능] E5 3-way 매칭이 `foreach(test_data)` 안에서 `pool.Where().OrderBy().FirstOrDefault()` 매행 재스캔·재정렬 → O(T×(I+S+R)). 루프 전 `ILookup<(Batch,Barcode)>`·`Dictionary<TestDataId>` 프리빌드로 준선형화. #2와 함께 처리 권장.
- [ ] [#6 DRY] `LogService.FilterArchive`가 `TestLog`/`WorkResult` 두 near-identical 오버로드. 공용 `IArchivable{ DateTime? ArchivedAt }` 제네릭 검토(EF 식트리 번역 주의 — 현행 허용).
- [ ] [#7 문서] `GetResultComparisonAsync`는 test_data 기준 순회라 마스터가 하드삭제된 아카이브 로그/결과는 `archived=all`에도 미표출(설계 의도). 한 줄 주석으로 필터버그 오인 방지.

## S-OBSERVABILITY 후속 (코드리뷰 MINOR — 비차단)
- [ ] [운영 전 필요] **operation_log 보존 14일 정의만·퍼지 일배치 미구현** → 무한 증가. 장기 운영 전 퍼지 배치 추가.
- [ ] [정리] `OperationLogService` unbounded Channel → BoundedChannel(DropOldest)+appsettings capacity. Detail JSON 수기 보간 → System.Text.Json 직렬화. 5개 WebApplicationFactory `UseSetting` 중복 → 헬퍼 추출. `appsettings.Development.json`에 Provider=Sqlite/Transport=Tcp 오버라이드(dev/sim DX). 제품 SqlServer 부팅분기 dotnet test 사각 → SqlServer smoke 테스트.

## M5 / CI 이연
- [x] [S-RCS-IF-REDESIGN P1] ~~full `dotnet test` teardown hang(BLOCKING-for-CI)~~ **해소됨(PR #12 teardown-fix 커밋)** — SorterBundleHandle.StopPollingAsync가 쓰기 큐 Writer.TryComplete()로 컨슈머 결정적 종료(빈 채널 CTS-only 취소 경쟁 해소). evaluator 6회 연속 exit0·70/70·hangdump0 + 독립 코드리뷰 APPROVE. PlcGateway 본문 무변경(Wcs.Api disposal 결선만).
- [ ] [정리] 미등록 dead code `PlcPollingHostedAdapter.StopAsync`가 큐 완료 누락(production DI 미등록·P2a 레거시 — SorterRegistryFactory로 대체됨). 제거 또는 동일 complete 추가(라이브 위험 0, 함정 제거용). 독립 코드리뷰 MINOR.

## S-RCS-IF-REDESIGN P2 이연 (Minor — 비차단)
- [ ] [S-RCS-IF-REDESIGN P2] 슈트 실패 푸시의 자율 복구 없음 — 소터는 관찰 타이머가 매 주기 재구동하나, 슈트는 상태변화 이벤트가 없으면 실패 푸시가 다음 IF-05/10까지 stale(자율 RCS 복구 폴러 없음). 확정3 경계 내·상태 오염 0(Acked 불변)이나, RCS 장시간 다운 후 복구돼도 무변화 슈트는 미동기. RCS 복구 시 부트스트랩 재기동 또는 주기 복구 스윕 필요하면 후속 스프린트.
- [ ] [S-RCS-IF-REDESIGN P2] `RcsPushOptions.Path` 기본 리터럴 운영 문서화 — 외부화된 설정 기본값(절대규칙 #7 위반 아님)이나 스펙 경로 변경 시 appsettings 오버라이드 가능함을 운영 문서에 명기 권고.

## 선행 sprint 이연(기록 유지)
- [ ] [S-M4-P2b] SorterRegistryFactory 번들 Dispose 누수(종료 시 _master/_cts/_clientLock 미dispose, 포트는 해제) — M5 graceful shutdown. ↑ teardown hang과 같은 뿌리.
- [ ] [S-M4-P3] IF-10 ContinueWith GetRequiredService throw 시 ReleaseCell 스킵 셀누수(호스트종료/DI오설정 한정) — M5.

## S-소터셀수량full 후속(코드리뷰 재리뷰 MINOR — 비차단·다음 정리/M5)
- [ ] [정리] `SorterHasAssignedCellWithRoomForBarcode`(`DestinationStatusService.cs:74,121`) orphan — production 호출자 0(IF-05는 `SorterCanAcceptBarcode`만 사용), 테스트 5곳(HP-1·EC-1·EC-2·EC-6)에서만 호출. 둘 다 내부 `HasAssignedCellWithRoom` 공유라 현재 정합하나, 향후 갈라지면 테스트가 production 경로를 못 지킴. → 인터페이스에서 제거 + 테스트를 `SorterCanAcceptBarcode`로 통일하거나 "테스트 introspection 전용" 명시. (정확성 무해.)
- [ ] [정리] `Compute` 본문 주석(`DestinationStatusService.cs:253`)에 "단일 원자 쿼리로 평가" 잔존 — `ComputeSorterFull` 주석은 2-쿼리로 정정됐으나 이 1줄 미반영. 주석 불일치(정확성 무해). → "같은 스코프 순차 읽기"로 정정.
- [ ] [정보성] 동일 오더 **동시 IF-10 2건**이 같은 여유 셀을 둘 다 읽고 적재 시 일시 Capacity 초과(soft-threshold, 1건 바운드·자가수렴). m4p4에도 없던 용량 모델 본질 특성이며 이 fix가 악화 안 함(SelectCell tx는 배정 INSERT만 직렬화, sorter_command 적재는 별도 핸드셰이크 콜백). 계약 §88-89 "단일 응답 내부 불변식 + eventually-consistent" 범위 내(교차 IF-10 직렬 capacity 강제 미약속). 강제 직렬화 필요 시 후속(만재센서 도입과 함께 재검토).

## S-M5-P1 후속(코드리뷰 정보성 — 비차단)
- [ ] [정보성·테스트인프라] 콜드스타트 Migrate 게이트가 "in-memory SQLite 패턴"(`Mode=Memory`)에 의존 — 현재 5개 테스트 팩토리 전부 in-memory라 안전하나, 향후 **파일 기반 SQLite 테스트**가 생기면 게이트를 통과해 Migrate 실행→EnsureCreated 스키마 충돌 가능. 그때 게이트를 명시 "test 환경 플래그"로 보강 권고. (현재 0건·결함 아님.)
- [ ] [정보성] `Database:SeedOnStartup` null→`IsDevelopment()` fallback이 base appsettings.json `false` 명시로 사문화(Development.json 오버라이드가 실효 경로). 더 보수적이라 안전 — 주석은 fallback 설명하나 실제는 명시값 우선. 의도와 일치.

## S-E2E-MULTI-AGV 후속(코드리뷰 MINOR·finding — 비차단)
- [ ] [정보성·테스트] E2EGroupGHI I2 단언 `sw.ElapsedMilliseconds < 3000` 느슨 경계 — 동기 핸드셰이크 대기로의 회귀는 잡으나(비공허) 3s 경계가 헐거움. 더 결정적인 단언(핸드셰이크 미완료 상태 직접 관찰)으로 조일 수 있음.
- [ ] [FINDING·진성·후속] 한 소터 concurrent 핸드셰이크 직렬화 부재 — `HandshakeOrchestrator.ExecuteAsync` 인스턴스 락 0(공유 `_gw.Latest` RSeq 폴링) → 한 소터 동시 IF-10 시 R_Seq 교차 MISMATCH≥1. 순차 dispatch면 전부 COMPLETED(SPEC §6 물리 직렬 모델 정합·현 지원). **동시 IF-10 허용 명세 미정** → 허용하려면 orchestrator 직렬화(per-소터 락) 필요. RCS가 한 소터에 동시 다발 IF-10을 보낼 가능성 확인 후 결정. (E2E F1b가 현 동작으로 명시 단언.)
- [ ] [테스트 인프라·후속] 실-Sim I/O 통합 테스트(IT4b `IT4b_WritesDuringReconnect_NoCorruption`·S9·핸드셰이크 R_Seq류)가 **xUnit 기본 병렬 + 무거운 E2E 부하**에서 저빈도 flake(RSeqMismatch). 근본=실 Modbus TCP I/O 타이밍 테스트가 무거운 E2E와 병렬 실행 시 CPU/소켓 경합. **클린 단일 런은 결정적**(Evaluator fresh 25+회 0 발현·Generator 12/12), 미정리 누적·고부하 러너에서만 드물게 발현. S9는 본 스프린트서 stable-count로 견고화 완료. 옵션: **(B) 권장** — 실-Sim 통합+E2E 테스트만 `[CollectionDefinition]`+`DisableTestParallelization`로 직렬 컬렉션(나머지 unit은 병렬 유지·속도 보존) / (A) 어셈블리 전체 병렬 비활성(즉효·12s→42s blunt) / (C) IT4b 개별 WaitUntil 견고화(whack-a-mole). 실 CI에서 발현 시 (B) 착수.

## S-소터push운영상태 후속(코드리뷰 정보성 — 비차단)
- [ ] [문서부채·선재] `docs/SPEC.md` §2(line 20·60)가 **폐지된 구 IF-08(deposit-permission 폴링) 모델**을 기술 — RCS 재설계로 IF-08=WCS→RCS 상태 푸시로 대체됐으나 SPEC.md는 미반영(canonical 정의서 wcs_rcs_interface_kr.html은 최신). 이 스프린트 이전부터의 문서 부채. 별도 문서 정리에서 SPEC.md IF-08 섹션을 푸시 모델(+소터 push=운영상태 / IF-05=셀·관리 게이트 2단계)로 갱신 권고.
- [ ] [정리·정보성] `DestinationReadiness.Full`/`Paused`/`Reason` 필드가 현재 **production 미소비**(테스트 introspection·내부 산출 전용 — IF-05는 `SorterCanAcceptBarcode`·`r.Paused`만, push는 `r.Ready`만). dead-but-consistent. 향후 이 필드들의 소비처가 생기면 "ready=true && Full=true 공존" 의미를 재확인할 것(현재는 무해).

## S-RCS-IF-REDESIGN P2 후속(코드리뷰 MINOR — 비차단)
- [ ] [P2] 슈트(CHUTE) 복구 재푸시 비대칭 — 관찰 타이머가 SORTER_3D만 재평가. RCS 다운으로 슈트 푸시 실패 시 다음 슈트 이벤트(예약/투입/비움) 전까지 stale(상태오염 0·확정3 "다음 전이 시"는 충족, "복구 감지 시"는 미충족). 한산한 슈트 장시간 stale 가능. → 슈트도 주기 재평가 또는 RCS 헬스 복구 시 전 목적지 재펌프(하트비트 결정과 함께 후속).
- [ ] [P2] teardown 중 disposed-CTS 접근 spurious error 로그 — DisposeAsync의 _cts.Dispose 후 lingering PumpAsync가 _cts.Token 접근 시 ObjectDisposedException→generic catch LogError(크래시·hang·미관찰예외 0·종료 클린). token 취득을 _stopped 가드로 감싸 조용히 종료 분기 권고.

## S-B2B-1 후속(Evaluator 재검증 — 비차단)
- [ ] [S-B2B-1][pre-existing flake·별도 이관] 핸드셰이크 S5 타이밍 테스트군(`HandshakeResidueTests.S5_ResidueClearNotReflected_TerminalTimeout_NoCWritten`·`S5RSeqMismatchTests.S5_RSeqMismatch_*`)이 전체 스위트/핸드셰이크군 병렬 실행 시 저빈도(~1/8~1/10) flake. **B2B 무관 확정**: B2B 0개 로드한 핸드셰이크군 단독 필터에서도 재현·단일 테스트 격리 시 8/8 GREEN. 실 Sim 소켓/타이밍 경합(s9-flake-under-e2e-load·e2e-parallel-load-surfaces-integration-flakes 동류). S9(ScenarioTests) 안정화 패턴(WaitUntilStableCount / 안정-관찰)을 이 두 테스트에도 적용 검토. 수정 대상=테스트 전용·production 0.
- [ ] [S-B2B-1][#1 잔여 SQL Server 500 갭] `ResultItem.ChuteNo`(→`work_result.chute_no nvarchar(20)`)·`BoxRequest.EndTime`(→`box.end_time nvarchar(50)`)에 `[StringLength(20)]`/`[StringLength(50)]` 미부여 → 과길이 입력이 SQL Server truncation 500(SQLite 더블 은폐). FIX ITER 2가 닫은 barcode/batch/boxNo와 동일 클래스. StringLength 2건 + 과길이 400 테스트 2건 추가로 원천 완전 해소 권고.

## S-B2C-DATAGEN 후속(Evaluator minor — 비차단)
- [ ] [S-B2C-DATAGEN] 프론트 테스트 러너(vitest/jest) 미구성 — 다이얼로그 체이닝(초기화→force 재요청) 회귀 테스트를 자동화할 수단이 없음. iter-1 BLOCKING(React 배칭에 의한 force 다이얼로그 silent close)이 정적검사·xUnit 어디에도 안 잡혔음. 프레임워크 도입 시 B2cDataGenPage onConfirm→requestForceReset 체이닝 케이스를 최우선 등재.
- [ ] [S-B2C-DATAGEN] B2B 모드에서 /b2c/test-data 직접 URL 진입 시 배너 제목이 b2b NAV 항목("데이터 생성")으로 표시 — Layout 배너가 현재 uiMode NAV_SETS 에서 title 을 찾는 구조. 정상 nav 경로에선 미발생(cosmetic).

## S-TRACE-LOG-VIEWER 코드리뷰 Minor 추가(포커스 재리뷰 — 비차단·cosmetic)
- [ ] [S-TRACE-LOG-VIEWER] TraceLogService.cs cap-eviction WARN 로깅이 `sp.Lock` 보유 중 실행 — >32 degraded 영역(직렬 dispatch에선 도달 불가)만 도달. 정리: 축출 pId를 락 안에서 수집→락 해제 후 로그.
- [ ] [S-TRACE-LOG-VIEWER] TraceCorrelator.RegisterHandshake 반환 토큰이 opaque `object`(DiscardPending에서 `is PendingReg` 재확인) — 타입드 핸들(마커 인터페이스/readonly struct)로 바꾸면 무관 객체 전달 원천 차단. 순수 스타일.

## S-TWO-FLOOR-WRITE-ON-CLEAR 후속(Evaluator minor — 비차단)
- [ ] [S-TWO-FLOOR-WRITE-ON-CLEAR] `SorterFloorReturnService.FireStallWarning`의 Serilog WARN 메시지 문자열이 **구 스톨 조건**을 기술 — "유휴(Ready=1)·TgtFloor=0·머리 불변"이라고 찍지만, 재조정된 abandonment 조건은 CurFloor==머리(정렬)이고 이 발화 상태에선 WCS가 write-on-clear로 이미 F를 기입해 **TgtFloor==머리(비영)**이다(0 아님). 즉 사람이 읽는 WARN 라인이 실제 상태와 모순("TgtFloor=0"인데 실제 D6=1/2)되고 "정렬" 절도 누락. 구조화 operation_log detail(line ~409)은 실제 snap.TgtFloor를 정확히 기록하므로 기계판독 감사기록은 정합 — 결함은 콘솔/파일 WARN 문구 1줄뿐. 관측 전용(교정 동작 0)이라 오분류·pop·쓰기 영향 0이나, fail-loud 진단 정직성 차원에서 문구를 새 조건(정렬-유휴 abandonment·TgtFloor=머리)으로 정정 권고. 1줄 수정.
- [ ] [S-TWO-FLOOR-WRITE-ON-CLEAR] E2EGroupK_TwoFloorReturnTests.cs:159(및 :154 WriteLine) 주석이 구식("pop=분류사이클 Ready 1→0→1 단위 검증")을 기술 — 테스트 본문은 clear-edge pop 검증으로 정확·GREEN. 주석만 새 트리거(TgtFloor 1→0 클리어 에지)로 정정. 주석-only.

## S-TRACE-READY-PUSH-AND-DEFAULT 코드리뷰 Minor(비차단)
- [ ] [S-TRACE-READY-PUSH-AND-DEFAULT] TraceLogService.cs `BuildReadyEdgeRecord` docstring "순수·부수효과 0" 부정확 — 내부에서 DateTimeOffset.Now 읽어 시간 의존(에지→null 매핑만 결정적). 주석을 "매핑은 결정적(At은 관측시각)"으로 완화하거나 clock 주입.
- [ ] [S-TRACE-READY-PUSH-AND-DEFAULT] 프론트 "10개 이벤트" 카피가 Layout.tsx:46·TraceLogPage.tsx:113 문자열 하드코딩(2곳) — 이벤트 추가 시 수동 동기 누락 위험. `EVENT_FILTER_OPTIONS.length` 파생으로 드리프트 제거.
