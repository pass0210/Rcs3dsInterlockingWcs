# Sprint Log — S-SORT-CYCLE-TIME-METRIC

(Generator가 `## IMPLEMENTATION COMPLETE` + 변경 요약 + 테스트 결과 기록)

## IMPLEMENTATION COMPLETE

메트릭 = **avg(ReturnedAt − SortStartedAt)**, SortStartedAt = 분류 시작 = **Ready 워드 1→0 전이 시각**(신규 컬럼).
캡처 = HandshakeOrchestrator R 폴 루프에서 Ready 1→0 에지를 **관측만 추가**(권고안 A1). 브랜치 base = e90f0cc
(feat/sort-cycle-time-metric). 커밋/푸시 없음.

### 변경 요약 (파일별)

**BE-1 스키마 + 마이그레이션**
- `backend/src/Wcs.Data/Entities.cs` — `SorterCommand.SortStartedAt`(nullable DateTime) 추가(DepositedAt 다음).
  단조 불변식 주석을 `DepositedAt ≤ SortStartedAt ≤ TiltedAt ≤ ReturnedAt`로 갱신.
- `backend/src/Wcs.Data/WcsDbContext.cs` — ConfigureSorterCommand에 `e.Property(x => x.SortStartedAt).IsRequired(false)` 추가(additive). Piece 인덱스 무접촉.
- 신규 마이그레이션 2개(`FixPieceIdempotencyIndexExcludeArchived` 위에 얹음):
  - `Wcs.Migrations.Sqlite/Migrations/20260731021217_AddSorterCommandSortStartedAt.cs` — AddColumn TEXT nullable / Down=DropColumn.
  - `Wcs.Migrations.SqlServer/Migrations/20260731021228_AddSorterCommandSortStartedAt.cs` — AddColumn datetime2 nullable / Down=DropColumn.
  - 양 ModelSnapshot 갱신(SortStartedAt property만 추가 — Piece 인덱스 무변경).
  - 생성 명령: `dotnet ef migrations add … --project <MigProj> --startup-project <MigProj>`(양 provider). MigrateOnStartup=true로 콜드스타트 자동 적용(현장 prod SqlServer).

**BE-2 분류 시작(Ready 1→0) 캡처 + 기입 (캡처 지점 = HandshakeOrchestrator R 폴 루프)**
- `backend/src/Wcs.PlcGateway/HandshakeOrchestrator.cs`
  - `HandshakeResult` 레코드에 `SortStartedAt`(nullable, 기본 null) append(TiltedAt/ReturnedAt와 동형 — 기존 ~20 호출부 보존).
  - `WaitRFlagAndProcessAsync` R 폴 루프에서 **최초 Ready==0 관측 1회**를 sortStartedAt에 기입(정상=1→0 에지, 폴 주기보다 빠른 분류/진입 시 이미 0이면 "첫 Ready==0 레벨" 폴백 — OQ-3 허용). 관측 전용.
  - 단조 클램프: R_Flag==1 관측 시 `sortStartedAt > tiltedAt`이면 tiltedAt로 클램프(기존 returnedAt<tiltedAt 클램프와 동형).
  - 모든 종료 경로(성공/불일치/타임아웃/OFFLINE)로 sortStartedAt 전달(`WaitReadyThenClearRAsync`·`ClearRAndReturnSuccessAsync`에 파라미터 추가).
  - ★ **제어 흐름·타이밍·pop·write-on-clear·PLC write 시퀀스 불변** — EnqueueAsync 호출 수 6→6(신규 PLC write 0), Task.Delay/deadline/poll 무변경. diff는 관측 캡처 + 파라미터 threading + 주석뿐.
- `backend/src/Wcs.Api/Repositories/DbRepositories.cs` — `EfSorterCommandJournal.Finalize`가 `cmd.SortStartedAt = result.SortStartedAt` 기입(TiltedAt/ReturnedAt 옆에 additive).

**BE-3/4 읽기 전용 집계 + 엔드포인트**
- `backend/src/Wcs.Api/Monitoring/MonitoringDtos.cs` — `CycleTimeAvgDto(double? AvgSeconds, int N)` 신설.
- `backend/src/Wcs.Api/Monitoring/MonitoringQueries.cs` — `IMonitoringQueries.GetCycleTimeAvg()` + 구현.
  전 행(★ **ArchivedAt 무필터**) 중 SortStartedAt·ReturnedAt 둘 다 non-null → (2컬럼 materialize 후 C# TimeSpan 계산·provider-neutral) Σ(ReturnedAt−SortStartedAt)/n. n=0 → AvgSeconds=null·N=0. 음수 방어 클램프. AsNoTracking·핫패스 무접촉.
- `backend/src/Wcs.Api/Controllers/MonitoringController.cs` — `GET /api/monitor/cycle-time-avg`(파라미터 없음·읽기 전용·200). raw double(초) 반환(소수 표기는 프론트).

**FE 표시 + 실시간 트리거**
- `frontend/src/lib/cycleTime.ts`(신규) — 단일 소스(#7): 소수자리(1)·디바운스(500ms)·트리거 event(9)·카피 문구·`formatCycleTime`.
- `frontend/src/lib/api.ts` — `CycleTimeAvg` 인터페이스 + `api.cycleTimeAvg()` 클라이언트.
- `frontend/src/pages/TraceLogPage.tsx` — 그리드 위 `CycleTimeAvgLabel` 컴포넌트: 마운트 조회 + event 9(READY_0TO1) 디바운스 재조회(기보유 subscribeTrace에 얹음·신규 SignalR 0) + 재연결(status→connected) 1회 재조회 + n=0/조회실패 우아 degrade("—"/"측정 데이터 없음"). 앱 테마 토큰(text-ink/bg-panel/border-line/text-faint) 사용. 기존 그리드/필터/배지 무변경.

### 설정 / 캡처 지점 / 상수
- 백엔드 타이밍: 신규 키 0 — 기존 `RFlagPollMs`(R 폴 주기) 재사용(캡처가 그 폴 루프에 얹힘).
- 프론트 상수: `lib/cycleTime.ts` 단일 소스(하드코딩 산재 0).
- 캡처 지점: `HandshakeOrchestrator.WaitRFlagAndProcessAsync` R 폴 루프(관측만 추가).

### 테스트 결과
- 신규 백엔드 테스트 4건(`backend/tests/Wcs.Tests/CycleTimeMetricTests.cs`):
  1. `Aggregate_BothNonNull_IncludesArchived_ExcludesNulls_MatchesHandCalc` — n=2·avg=15.0초 손계산 일치, ArchivedAt!=null 포함 양성, null 제외.
  2. `Aggregate_ZeroN_ReturnsNullNotError` — n=0 → avgSeconds=null·200(500 아님).
  3. `Journal_Finalize_PersistsSortStartedAt_AndMonotone` — Finalize가 result.SortStartedAt 지속·단조(DepositedAt ≤ SortStartedAt ≤ TiltedAt ≤ ReturnedAt).
  4. `Capture_ReadyOneToZero_SetsSortStartedAt_BeforeTiltAndReturn` — 실 Sim(TCP) 핸드셰이크로 Ready 1→0 실관측 → SortStartedAt 캡처·SortStartedAt < TiltedAt ≤ ReturnedAt·사이클 양수([Collection("RealSimSerial")] 직렬).
- **전체 스위트: 518 GREEN / 0 실패**(baseline 514 + 신규 4 = 518, 산술 일치·회귀 0). 1m31s.
- 프론트: `tsc --noEmit` exit 0 · `eslint .` exit 0 · `vite build` exit 0(chunk>500kB 경고는 선재).
- 마이그레이션: 양 provider `has-pending-model-changes` = **No**.
- 회귀 게이트: **Wcs.Core git diff 0** · Wcs.Sim3ds diff 0 · HandshakeOrchestrator EnqueueAsync 6→6(신규 PLC write 0) · 핸드셰이크 diff = 관측/파라미터/주석뿐(타이밍·pop·write-on-clear·PLC 시퀀스 불변).
- 빌드 경고: 선재 NU1903(SQLitePCLRaw)·CS8604(B2cFacilityService)·xUnit2013(기존 테스트)뿐 — 신규 0.

### 재검증 게이트(Evaluator 필수 — iter1 교훈)
자동 테스트만으론 PASS 불가. 실 Sim IF-05→IF-10 → C 기입 → Ready 1→0 실관측 → SortStartedAt 기입 → 틸트 → Ready 0→1 → ReturnedAt → COMPLETED sorter_command → trace event 9 → 디바운스 재조회 → /trace 레이블 n +1·평균이 **의미 있는 양수**로 갱신됨을 브라우저 수치로 실측. 표시값 = DB Σ(ReturnedAt−SortStartedAt)/n 일치(아카이브 행 포함 규칙). 합성 손세팅 마스킹 금지.
