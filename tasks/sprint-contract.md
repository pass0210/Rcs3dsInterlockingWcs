[Sprint Contract] — S-SORT-CYCLE-TIME-METRIC (재기획 · S-TRACE-AVG-CYCLE-LABEL iter1 FAIL 대체)

관측/기입 최소확장 · additive · Full-stack. /trace 그리드 위에 "평균 사이클 시간(분류시작~복귀)"을 표시하고 실시간 갱신한다. iter1(`ReturnedAt − CWrittenAt`)이 라이브에서 항상 ≈0(구조적 결함: CWrittenAt=핸드셰이크 후 저널 시각)이라 FAIL → 사용자 게이트 재확정으로 **물리 앵커 쌍(분류 시작 → 복귀)** 으로 재정의한다.

──────────────────────────────────────────────────────────────────────────────
- Goal
──────────────────────────────────────────────────────────────────────────────
현장 운영자가 /trace 상단에서 "한 사이클(**분류 이동 시작 → 복귀 완료**)에 평균 몇 초 걸리는가"를 한눈에 보고, 신규 사이클이 완료될 때마다(=신규 ReturnedAt 기입) 값이 스스로 갱신되게 한다.

정의(★재정의 2026-07-30 — 사용자 게이트 재확정):
  · 대상 = `sorter_command` 테이블.
  · **분류 시작(SortStartedAt) = Ready 워드 1→0 전이 시각**. 근거: SPEC상 분류 시작 시 Ready=0(절대규칙 #4 — 0=분류/이동 중). C 기입 후 소터가 분류를 시작하는 첫 물리 신호다. 이 시각을 그 사이클의 `sorter_command` 행에 기입한다.
  · **복귀 완료(ReturnedAt) = Ready 0→1 전이 시각**(기존 컬럼·기존 캡처 재사용 — 무변경).
  · 사이클 시간 = **ReturnedAt − SortStartedAt**, 초 단위(소수 1자리).
  · 계산 범위 = 전 행. ★ ArchivedAt 필터 없음(초기화/아카이브·재테스트 이전 행 전부 포함).
  · n = `ReturnedAt != null && SortStartedAt != null` 인 행 수.
  · 값 = Σ(ReturnedAt − SortStartedAt) / n, 소수 1자리. n=0 → null("—").
  · **음수 없음(단조 보장)**: DepositedAt ≤ SortStartedAt ≤ TiltedAt ≤ ReturnedAt 단조 불변식상 ReturnedAt − SortStartedAt ≥ 0. 따라서 iter1의 "음수 제외" 로직 불요 — 0 나눗셈만 방어. (Generator는 시계 비단조 방어 클램프를 기존 TiltedAt/ReturnedAt 클램프와 동형으로 둔다.)
  · 실시간 = 신규 사이클 완료(ReturnedAt 신규 기입 = Ready 0→1 = 트레이스 event 9) 시 값이 갱신된다.
  · ★ **재검증 게이트(iter1 교훈 — 필수)**: 자동 offset 테스트(손세팅 SortStartedAt/ReturnedAt)만으론 **불충분**. **실 Sim E2E로 실제 저널링 경로(Ready 1→0 실관측 → SortStartedAt 기입 → 틸트 → Ready 0→1 → ReturnedAt → 집계)를 태워 의미 있는 양수값(현장 감각상 초 단위)이 나오고, 표시값 = DB Σ/n 임을 실증**해야 PASS. 합성 손세팅으로 마스킹하는 것은 금지(lessons: "GREEN ≠ 사용자 여정").

──────────────────────────────────────────────────────────────────────────────
- ★ 핵심 설계 논점 (Planner 조사 결과 — HOW 최종은 Generator)
──────────────────────────────────────────────────────────────────────────────
A. **Ready 1→0(분류 시작) 캡처 지점 — 조사 완료.**
   현재 Ready 1→0은 두 곳에서 이미 관측되나 **둘 다 층-scope(피스/커맨드 상관 없음)**:
     ① `PlcGateway.EmitRegisterChanges`(:584) → `OnRegisterChange("Ready",1,0)` 발화.
     ② `TraceWiring.Wire`(:636)가 이를 구독해 트레이스 event 7(READY_1TO0) 발화 — PId/CSeq/CellNo=null.
   반면 `HandshakeOrchestrator.WaitRFlagAndProcessAsync`(:355)의 R 폴 루프는 C 기입 이후·R_Flag=1 이전 구간에서 **이미 `_gw.Latest`를 `RFlagPollMs` 주기로 폴링**하며, `PlcSnapshot.Ready`가 그 스냅샷에 포함돼 있다.
   · **권고안(候補 A1 — 채택 권고)**: 오케스트레이터 R 폴 루프 안에서 Ready 1→0 에지를 **관측만 추가**로 캡처 → `HandshakeResult`에 `SortStartedAt`(nullable) append(기존 TiltedAt/ReturnedAt 추가 패턴과 동형·기본값 null로 ~20개 기존 호출부 보존) → `RcsController` continuation의 `journal.Finalize`에서 그 사이클 행에 기입. **장점**: per-command 상관이 구조적으로 깔끔(각 핸드셰이크 인스턴스가 자기 폴 루프·자기 커맨드 소유), 소터 동시성 상관 문제 회피.
   · **대안(候補 A2 — 비권고)**: 폴 루프(PlcGateway/TraceWiring)가 관측한 Ready 1→0을 소터별 in-flight latch에 저장하고 Finalize가 읽어 상관. 직렬 dispatch 전제 필요·상태 결선이 더 침습적 → 비권고.
   · **에지 미관측 리스크(HOW·게이트)**: 한 사이클의 분류가 폴 주기(RFlagPollMs=100ms 기본)보다 빨라 Ready 1→0 에지를 샘플링에서 놓치면 SortStartedAt=null(그 행은 n에서 자연 제외). Generator는 에지 감지 실패 시 "C 기입 후 첫 Ready==0 레벨 관측"을 폴백으로 둘지 결정하되(권고: 폴백 둠), **어떤 HOW든 SortStartedAt ≤ TiltedAt 단조를 깨지 않아야** 한다. E2E 게이트가 실측으로 미관측률·양수성을 검증한다.

B. **스키마/마이그레이션.** `SorterCommand.SortStartedAt`(nullable DateTime) 추가(Entities.cs:374 부근·컬럼명=프로퍼티명 PascalCase·HasColumnName 미사용·B2C 관례). WcsDbContext(:548 부근)에 `.IsRequired(false)` 추가. **provider-split 마이그레이션 2개**(Wcs.Migrations.Sqlite TEXT / Wcs.Migrations.SqlServer datetime2, nullable:true) + 양 ModelSnapshot 갱신 — 기존 `AddSorterCommandProcessingTimes` 관례 그대로. 단조 불변식 주석(Entities.cs:373)을 **`DepositedAt ≤ SortStartedAt ≤ TiltedAt ≤ ReturnedAt`** 로 갱신. **현장 prod(SqlServer) 마이그레이션은 `MigrateOnStartup=true`(appsettings·DbInitializer.cs:82-87 기본)로 콜드스타트 자동 적용됨을 명시.**

C. **기존 행 처리(사용자 확인).** 이미 있는 `sorter_command` 행은 SortStartedAt=null → n에서 자연 제외. **평균은 신규 사이클부터 집계**(과거 행은 분류시작 앵커가 없어 빠짐). → Open Question OQ-2.

D. **provider-neutral 집계.** iter1과 동형 — (SortStartedAt, ReturnedAt) 2컬럼 materialize 후 C# TimeSpan 계산(SqlServer/Sqlite 동일 수치·provider 고유 date/datediff SQL 0). ArchivedAt 무필터.

E. **재검증 게이트(iter1 교훈).** 위 Goal 마지막 항목 — 실 Sim E2E 필수, 합성 마스킹 금지.

──────────────────────────────────────────────────────────────────────────────
- Implementation Scope (Generator 가 HOW 결정 · 아래 WHAT 전부 충족)
──────────────────────────────────────────────────────────────────────────────
BE-1. **스키마 + 마이그레이션** (설계 논점 B):
      · `SorterCommand.SortStartedAt`(nullable DateTime) 추가 + WcsDbContext 매핑 `.IsRequired(false)`.
      · 단조 불변식 주석(Entities.cs:373) 갱신: `DepositedAt ≤ SortStartedAt ≤ TiltedAt ≤ ReturnedAt`.
      · provider-split 마이그레이션 2개(Sqlite·SqlServer) + 양 ModelSnapshot 갱신. Up=AddColumn(nullable)·Down=DropColumn.

BE-2. **분류 시작(Ready 1→0) 캡처 + 기입** (설계 논점 A — 권고안 A1):
      · `HandshakeOrchestrator` R 폴 루프에서 Ready 1→0 에지를 **관측만 추가**(기존 폴/타이밍/pop/write-on-clear/ClearR 시점 불변). `HandshakeResult`에 `SortStartedAt`(nullable, 기본 null) append.
      · `EfSorterCommandJournal.Finalize`(또는 CreateSent — Generator 결정)가 `result.SortStartedAt`를 그 행에 기입.
      · ★ #8: **Wcs.Core zero-diff**(SortStartedAt은 Wcs.PlcGateway/Wcs.Api/Wcs.Data 계층에만 — Wcs.Core·DepositDecider·판정 로직 무접촉). 핸드셰이크 **제어 흐름·타이밍·pop·write-on-clear·PLC write 시퀀스 불변**(관측·EF 기입만 additive).
      · ★ #1: 신규 PLC write 0 — SortStartedAt 기입은 EF DB 저장이지 Modbus 아님. 쓰기 큐 무변경.

BE-3. **읽기 전용 집계 조회 1건** (Wcs.Api/Monitoring — 설계 논점 D):
      · `sorter_command` 전 행(ArchivedAt 무필터) 중 `SortStartedAt != null && ReturnedAt != null` 행에 대해 Σ(ReturnedAt − SortStartedAt)·n 산출 → 평균(초). materialize 후 C# 계산(provider-neutral).
      · `IMonitoringQueries`에 메서드 추가 + `MonitoringQueries` 구현 + 신규 DTO(예: `{ avgSeconds, n }`). 기존 조회/DTO/리포지토리 무변경(append-only). AsNoTracking.
      · n=0 방어: 0 나눗셈 없이 `avgSeconds=null`·n=0 신호로 200 반환(500 금지).
      · 핫패스 무접촉: 집계는 이 조회 요청 시에만 실행(폴/핸드셰이크/IF-10 응답에 삽입 금지).

BE-4. **REST 엔드포인트 1개** (MonitoringController, GET, 읽기 전용):
      · 파라미터 없음(전 행 집계). 기존 /api/monitor/* 관례(읽기 전용·부수효과 0·200 일관)를 따른다. 경로명 Generator 확정(iter1 관례 `cycle-time-avg` 재사용 가능).

FE-1. **/trace 그리드 위 "평균 사이클 시간" 레이블**:
      · 마운트 시 BE-4 조회해 표시. 주표기 "평균 사이클 시간(분류시작~복귀): X.X초 · n=N" + 수식 부기 "Σ(복귀−분류시작)/N"(툴팁/부제).
      · n=0/조회 실패 시 그리드를 깨지 않고 우아하게 degrade("—"/"측정 데이터 없음").
      · api.ts에 클라이언트 함수 추가. 기존 그리드/필터/연결배지 동작 무변경. 레이블은 앱 테마 토큰(text-ink/bg-panel/border-line/text-faint) 사용.

FE-2. **실시간 트리거 결선**(iter1 동형 재구현):
      · 기보유 `subscribeTrace` 구독에 얹어 사이클 완료 신호 수신 시 BE-4 재조회(디바운스). **트리거 = event 9(READY_0TO1 = 복귀 완료 = 신규 ReturnedAt)**. 신규 백엔드 push·신규 SignalR 메서드 0.
      · 재연결(onreconnected) 시에도 1회 재조회(갭 보정).

CFG(#7). **상수/설정 소스화·하드코딩 0**: 소수자리·디바운스 간격·트리거 event 번호·placeholder/카피 문구·포맷터를 프론트 단일 소스 모듈(iter1 `lib/cycleTime.ts` 관례)로. 캡처/타임아웃 관련 백엔드 값은 기존 appsettings 키(RFlagPollMs 등) 재사용 — 신규 타이밍 리터럴 산재 0.

SCOPE OUT(명시):
  · 소터별/셀별 분해 통계·다른 페이지·다른 이벤트(이번엔 전 행 단일 평균만).
  · Wcs.Core / DepositDecider / 판정 로직 (절대규칙 #8 zero-diff).
  · 핸드셰이크 제어 흐름·타이밍·pop·write-on-clear·PLC write 시퀀스 변경(관측·기입만 additive 허용).
  · 인덱스 추가(현 규모 전행 집계 허용 — 필요 시 별도 스프린트).

──────────────────────────────────────────────────────────────────────────────
- Open Questions (사용자 확인 완료)
──────────────────────────────────────────────────────────────────────────────
OQ-1 (스코프 확장) 캡처를 위해 HandshakeOrchestrator/HandshakeResult/DbRepositories 기입 경로(#8 인접·write-path)를 사용자 승인 하에 의도적으로 확장 — Wcs.Core zero-diff·핸드셰이크 제어흐름/타이밍/pop/write-on-clear/PLC write 불변·회귀 0 최대 보존. → **사용자 ① 선택으로 승인(2026-07-30)**.
OQ-2 (과거 행) 기존 `sorter_command` 행은 SortStartedAt=null → 평균 제외, **신규 사이클부터 집계**. → **확인: 예**.
OQ-3 (에지 미관측) 분류가 폴 주기보다 빠른 드문 경우 대비 "C 후 첫 Ready==0 레벨 폴백" 허용(단조 SortStartedAt ≤ TiltedAt 유지). → **확인: 허용**.
OQ-4 (표시/경로) 표시 카피·수식 부기 게이트 확정값. 엔드포인트 경로명 Generator 확정(iter1 `cycle-time-avg` 재사용 가능).

──────────────────────────────────────────────────────────────────────────────
- Evaluation Criteria (Full-stack — 가중치)
──────────────────────────────────────────────────────────────────────────────
1. Integration Quality (★★★) — BE 집계 계약(avgSeconds·n)과 FE 표시가 형상 일치. 실 사이클을 태워 Ready 1→0 실관측 → SortStartedAt 기입 → ReturnedAt → 집계 → trace event 9 → FE 재조회 → 레이블 갱신이 end-to-end 실제 동작(코드 존재 아님). **표시값이 의미 있는 양수**임을 실증.
2. Functionality / 정확성 (★★★) — 시드/실데이터로 Σ(ReturnedAt−SortStartedAt)/n 손계산 일치. ArchivedAt!=null 행 포함 양성 실증. SortStartedAt=null 또는 ReturnedAt=null 행은 n 제외. n=0 → 200·비크래시. 양 provider 동일 수치. 단조상 음수 미발생 확인.
3. Craft (★★) — 읽기 전용 집계·핫패스 무접촉·n=0/조회실패 우아 처리·#7(리터럴/타이밍 설정 소스화)·마이그레이션 관례 정합(2 provider + snapshot). 콘솔 BLOCKING(pageerror/React warning) 0.
4. 회귀 0 (★★) — **Wcs.Core git diff 0**. 기존 핸드셰이크(성공/불일치/타임아웃/OFFLINE/잔류/복귀)·write-on-clear·트레이스(1~10)·monitor(E1~E7·operation-log)·기존 /trace 그리드/필터/배지·전체 테스트 스위트 수치 불변(baseline+신규=합). 핸드셰이크 타이밍/pop/PLC write 시퀀스 불변 실증.

- Evaluation Dimensions: functional only (단일 표면·읽기 집계 + 최소 write-path 확장. 성능/회귀는 위 Craft·회귀 기준 내 검증).
- Parallel Modules: N/A (single feature — BE 스키마/캡처/집계 → FE 표시가 계약 의존, boundary-clean 분할 아님).

──────────────────────────────────────────────────────────────────────────────
- Detected Project Type: Full-stack
  (repo 신호: frontend/ React 클라이언트 렌더 트리(TraceLogPage.tsx 등) + backend/ ASP.NET Core 컨트롤러/허브(MonitoringController·WcsMonitorHub·HandshakeOrchestrator)가 동일 리포에 공존.)
──────────────────────────────────────────────────────────────────────────────

- Verification Scenarios (Full-stack, N=13):

  === Applicable Web/UI scenarios (frontend surface: /trace) ===
  W-1 (기본 상태) /trace 진입 시 그리드 위에 "평균 사이클 시간(분류시작~복귀)" 레이블이 값·n과 함께 렌더된다(주표기 + 수식 부기).
  W-2 (실시간 갱신 = 핵심 상호작용) 신규 사이클 완료 후(event 9 수신·디바운스 재조회) 레이블의 값·n이 새 수치로 갱신된다(before→after 수치 대조).
  W-3 (빈/에러 상태) n=0 → "—"/"측정 데이터 없음"; BE-4 조회 실패 주입 시 그리드는 정상 스트림 지속·레이블만 우아 degrade, 복원 후 다음 사이클에 자가 회복.
  W-4 (다크모드) 앱이 단일 라이트 테마(토글 컨트롤·data-theme·테마 localStorage 키 미관찰)이면 N/A — Evaluator가 실제 토글 유무로 확정. 존재 시 양 테마 대비 확인.
  W-5 (콘솔) W-1~W-3 클릭스루 중 pageerror 0·React dev-warning 0·의도치 않은 4xx/5xx 0(내 dev 포트로 분리 캡처).

  === Applicable Backend/API scenarios (backend surface) ===
  B-1 (엔드포인트) GET /api/monitor/<cycle-avg 경로>(최종 경로명 Generator 확정·파라미터 없음) → 200·{ avgSeconds, n }.
  B-2 (happy path) SortStartedAt·ReturnedAt 둘 다 있는 시드 행 → { avgSeconds: Σ/n(초·1자리 반올림 전 raw double), n } 손계산 일치. ArchivedAt!=null 행 포함 양성.
  B-3 (에러/경계) n=0(둘 다 non-null 행 전무) → { avgSeconds:null, n:0 }·200(500 아님). SortStartedAt=null 또는 ReturnedAt=null 행은 n 제외. 양 provider(SqlServer/Sqlite) 동일 수치(또는 provider-neutral 경로임을 코드로 입증).
  B-4 (마이그레이션 적용) Sqlite·SqlServer 마이그레이션 2개가 `sorter_command`에 SortStartedAt(nullable) 컬럼을 추가하고 콜드스타트 MigrateOnStartup 경로가 exit 0으로 적용됨(기존 데이터 보존·구 행 SortStartedAt=null). ModelSnapshot 정합(pending model changes 경고 0).
  B-5 (Ready 1→0 캡처 정확성) 핸드셰이크 단위 테스트/통합에서 Ready 1(idle)→0(분류)→…→R_Flag=1→Ready 0→1(복귀) 시퀀스를 태워 SortStartedAt = 1→0 관측 시각으로 기입됨을 실증. 미관측(폴백/에지) 경로 동작 명시.
  B-6 (단조 불변식) 성공 사이클 행에서 DepositedAt ≤ SortStartedAt ≤ TiltedAt ≤ ReturnedAt 이 성립(음수 사이클타임 미발생) — 시드·실데이터로 확인.
  B-7 (회귀) Wcs.Core git diff 0 실증. 기존 핸드셰이크/write-on-clear/trace(1~10)/monitor 테스트 스위트 GREEN(baseline+신규=합). 핸드셰이크 타이밍·pop·PLC write 시퀀스 불변(관련 테스트 수치 불변).

  === End-to-end data-flow scenario (≥2 계층 횡단) ===
  E2E-1 (★ 재검증 게이트) 실 Sim IF-05→IF-10 → C 기입 → **Ready 1→0(분류 시작) 실관측 → SortStartedAt 기입** → 틸트 → **Ready 0→1(복귀) → ReturnedAt 기입** → COMPLETED sorter_command 1행 → trace event 9가 프론트 trace 구독 도달 → 디바운스 재조회 → /trace 레이블 n이 +1·평균이 재계산된 **의미 있는 양수**(≈초 단위)로 갱신됨을 브라우저 수치로 실측. 표시값 = DB Σ(ReturnedAt−SortStartedAt)/n 일치(아카이브 행 포함 규칙 반영). ★ 합성 손세팅 아닌 실제 저널링 경로로 실증(iter1 FAIL 근본원인 재발 차단).

──────────────────────────────────────────────────────────────────────────────
- Completion Conditions (Evaluator PASS 최소 조건)
──────────────────────────────────────────────────────────────────────────────
C1. GET(BE-4)가 { avgSeconds, n } 형상으로 200 반환. 파라미터 없이 전 행 집계.
C2. 자동 테스트(백엔드): (i) 둘 다 non-null 여러 행 → 평균·n 손계산 일치, (ii) ArchivedAt!=null 행도 n·합 포함(양성), (iii) SortStartedAt=null 또는 ReturnedAt=null 행은 n 제외, (iv) n=0 → avgSeconds=null·200, (v) provider-neutral(양 provider 동일 수치 또는 코드 입증). ★ 단, 자동 테스트만으론 PASS 불가 — E2E(C3) 필수.
C3. **E2E(재검증 게이트)**: 실 Sim 핸드셰이크로 Ready 1→0 실관측→SortStartedAt 기입→ReturnedAt→집계→/trace 레이블 값·n 갱신을 브라우저로 실측. **표시값이 의미 있는 양수**이고 DB Σ/n과 일치. (offset 손세팅 마스킹 금지.)
C4. 마이그레이션: Sqlite·SqlServer 2개 + 양 ModelSnapshot. `dotnet ef` pending-model-changes 경고 0. MigrateOnStartup 적용 exit 0.
C5. 정적검사(독립 실행): dotnet build/test exit 0(신규 경고 0 — 선재 NU1903 제외), 프론트 tsc/lint/build exit 0.
C6. 회귀: 기존 스위트 GREEN(baseline+신규 산술 일치). **Wcs.Core git diff 0.** 핸드셰이크 제어흐름/타이밍/pop/write-on-clear/PLC write 시퀀스 불변 실증(관련 테스트 수치 불변·git diff 리뷰). 기존 /trace 그리드·필터·배지·monitor(E1~E7)·trace(1~10) 무변경.
C7. 절대규칙: #1(신규 write 경로·PLC write 0 — SortStartedAt 기입은 EF DB 저장), #7(소수자리/디바운스/트리거 event/문구·타이밍 상수·설정 소스화·하드코딩 0), #8(판정 로직 무접촉·Wcs.Core 순수·핸드셰이크 관측/기입만 additive).

> Planner self-check — Detected project type: Full-stack. Required scenario slots: 13 (W-1 기본상태, W-2 실시간갱신, W-3 빈/에러상태, W-4 다크모드, W-5 콘솔, B-1 엔드포인트, B-2 happy-path, B-3 에러/경계, B-4 마이그레이션적용, B-5 Ready1→0캡처정확성, B-6 단조불변식, B-7 회귀, E2E-1 횡단데이터흐름-재검증게이트). All slots filled: yes.
