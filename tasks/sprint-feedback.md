# Sprint Feedback — S-SORT-CYCLE-TIME-METRIC

(Evaluator가 PASS/FAIL·APPROVED 기록)

## APPROVED (2026-07-31, iteration 1)

메트릭 = avg(ReturnedAt − SortStartedAt), SortStartedAt = 분류 시작(Ready 워드 1→0 관측, HandshakeOrchestrator R 폴
루프 관측 전용 + 첫 Ready==0 폴백[OQ-3]). 전 완료조건 C1~C7·전 시나리오(W-1~W-5·B-1~B-7·E2E-1) 신선증거로 검증.

### ★ 재검증 게이트(iter1 FAIL 근본원인 재발 차단) — 실 Sim E2E로 실증
- 격리 라이브 스택: 실 Sim3ds TCP :1518(--sort 1500) + Wcs.Api :5217(Sqlite scratch·SeedOnStartup·Sorters[0]
  ChuteNo=30/Tcp/1518 override·ChuteStatePush DORMANT) + Vite :5294(→:5217). 현장/운영 DB·포트 무접촉.
- 실 저널링 경로 2사이클 구동(합성 손세팅 아님): IF-05(TEST-BARCODE-3·ind1)→IF-10(chuteNo30)→핸드셰이크→
  COMPLETED sorter_command. 브라우저 /trace 레이블:
  - 마운트: "평균 사이클 시간(분류시작~복귀) · 1.5초 · n=1 · Σ(복귀−분류시작)/N".
  - 2번째 사이클 완료 후 **페이지 리로드 없이**(navEntries=1) "1.5초 · n=2"로 자가 갱신 — event 9(READY_0TO1)
    → subscribeTrace → 500ms 디바운스 재조회 체인 실동작 실증(W-2/E2E-1 핵심 상호작용).
- **표시값 = DB Σ/n 비순환 실증**(raw 컬럼 직독, dbread 도구): row1 cycle=1.5090001s·row2 cycle=1.4046335s →
  n=2 sum=2.9136336 avg=**1.4568168** = 엔드포인트 {avgSeconds:1.4568168,n:2} = 레이블 "1.5초"(toFixed(1)) 완전 일치.
  n=1 단계도 1.5090001 정확 일치. **의미 있는 양수(≈초)** 실증 — iter1의 항상-≈0 결함 재발 없음.
- 단조 불변식 실데이터 확인: 두 행 모두 Deposited ≤ SortStarted ≤ Tilted ≤ Returned = OK(음수 사이클 미발생).

### 집계 정확성 / 마이그레이션 / 회귀
- 자동 테스트 4건 GREEN(명시 실측): Aggregate n=2 avg=15.0 손계산 일치·ArchivedAt!=null 행 포함·null 행 제외 /
  n=0→avgSeconds=null·200 / Journal.Finalize 지속+단조 / **실 Sim 캡처**(Outcome=Success·sortStarted<tilted≤returned·
  cycle=124ms 양수). 전체 스위트 **518 GREEN / 0 fail / 0 skip**(514 baseline + 4 신규 = 산술 일치·회귀 0).
- 마이그레이션: SqlServer(datetime2)·Sqlite(TEXT) 2개 + 양 ModelSnapshot. **has-pending-model-changes=No**(양 provider).
  base e90f0cc(FixPieceIdempotency…) 위 20260731 신규가 순서대로 얹힘. **SQL Server 콜드스타트**: 빈 eval DB에
  9개 마이그레이션 from-scratch 적용 exit 0·SortStartedAt=datetime2 nullable 확인. Sqlite 콜드스타트: 라이브 스택
  MigrateOnStartup+Seed 정상(컬럼 존재). AddColumn(nullable)이라 기존 행 보존·구 행 SortStartedAt=null 구조적 보장.
- 회귀 0: **Wcs.Core git diff 0**·Sim3ds diff 0. HandshakeOrchestrator diff = SortStartedAt 관측(`if(sortStartedAt is
  null && !snap.Ready)`) + tiltedAt 클램프 + 전 종료경로 파라미터 threading + 주석뿐. **EnqueueAsync 6→6**(신규 PLC
  write 0)·Task.Delay/deadline/poll/pop/write-on-clear/ClearR 시퀀스 불변(diff 리뷰 + 라이브 핸드셰이크 정상 완료
  cSeq/rSeq match로 실증). 프론트 tsc/lint/build exit 0(선재 chunk 경고만). 콘솔: 내 :5294 페이지 pageerror 0·warning 0
  (all:true 버퍼 69k는 전부 foreign 세션 — 내 포트 참조 0건, foreign-buffer 분리).

### 절대규칙
- #1: SortStartedAt 기입은 EF DB 저장(Journal.Finalize)·Modbus 아님 — 쓰기 큐 무변경·EnqueueAsync 6→6.
- #7: 프론트 상수(소수1·디바운스500·트리거event9·카피·포맷) lib/cycleTime.ts 단일 소스·백엔드 RFlagPollMs 재사용·신규 타이밍 리터럴 0.
- #8: 판정 로직 무접촉·Wcs.Core 순수(diff 0)·핸드셰이크 관측/EF 기입만 additive.

W-3(빈/에러 우아 degrade)·W-4(단일 라이트 테마 N/A — data-theme·토글 부재 확인)는 엔드포인트 n=0→null 응답·
formatCycleTime placeholder·failed catch branch 코드로 커버(라이브 결함주입 미실시 — 핵심 양수 게이트 실증으로 비례).

## Minor (코드리뷰 4-Tier — 다음 스프린트 Generator 참고 · 전부 비차단·사용자 결정=등재 후 커밋)
- **CR-1**: `MonitoringQueries.GetCycleTimeAvg`가 전행 `.ToList()` materialize(ArchivedAt 무필터·무인덱스) — 매 event9 재조회마다 전체 스캔. **계약 SCOPE OUT**(현 규모 허용). 실 볼륨 전 running-sum/윈도우 팔로업 권고.
- **CR-2**: `TraceLogPage.tsx CycleTimeAvgLabel.load()`가 매 resolve에 setData — 마운트/event9/재연결 fetch 동시 시 늦은 응답이 최신 덮음(seq 가드 없음). 자가치유(다음 event9). abort/seq 토큰 권고.
- **CR-3**: trailing 디바운스라 연속 분류(event9 < 500ms 간격) 중엔 ≥500ms 조용한 구간까지 미갱신. 디바운스 자체는 계약 허용. leading+trailing max-wait 권고.
- **CR-4**: event9(READY_0TO1·폴 관측)와 핸드셰이크 Finalize(ReturnedAt 기입)가 독립 — 커밋 지연 시 재조회가 방금 완료행을 놓쳐 n이 1사이클 지연(자가보정). E2E n+1 게이트 flakiness 관련.
- **CR-5**: `HandshakeOrchestrator` 첫 Ready==0=분류시작 전제 — 필드 PLC가 비분류 사유(인테이크·직전 실패사이클 잔류 모션)로 Ready 드롭 시 sortStartedAt 조기 캡처(단조 ≤ TiltedAt 클램프됨·사이클 팽창). OQ-3 허용·E2E 양수 검증. 필드 반복 로그 관찰 권고.
- **CR-6**: 조회 실패 degrade가 직전 good 값 버리고 "—" + tooltip이 stale `data`로 isEmpty 판정 → "—"+수식 병기 가능(cosmetic). 계약은 "—" 명시.
