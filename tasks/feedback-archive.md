# Feedback Archive

스프린트별 평가에서 도출된 재사용 가능한 핵심 피드백.

## S-M4-P2b (멀티 소터: 단일 게이트웨이 → 소터별 레지스트리 N대) — APPROVED (2026-06-17, 2 iterations)

- **동시성/격리 스프린트의 핵심 시나리오는 실 인프라로 실증해야 — fake + NotSame 구조 단언은 불충분**: Rev.1 FAIL 핵심은
  소터별 핸드셰이크 독립(C_Seq 교차 0, VS-P2b-4 "핵심")이 구현은 됐으나 검증이 `FakeModbusMasterForApi` + `NotSame` 구조 단언뿐 —
  폴 루프조차 미기동이라 실 핸드셰이크 0건. Rev.2에서 `P2bSimHandshakeTests`(IAsyncLifetime, 동적 포트 2개)로 실 SimServer 2대 +
  번들 2세트를 띄워 동시 ExecuteAsync → 각 소터 SentCSeq==ReceivedRSeq, 독립 소켓 버스로 교차 0(물리 분리)를 실증. → "구현됐다"와
  "검증됐다"는 다르다. 인스턴스 격리 주장은 실 동시 핸드셰이크로 증명. (단 P2b4의 "C_Seq 우연 일치 시 교차 미감지" 보강 단언은
  자기일치 중복으로 degenerate — 교차 0의 본질 증거는 포트/소켓 물리 분리. 더 강하게 하려면 한 소터 CSeq를 큰 오프셋으로 초기화해 값 교차도 배제 권장.)
- **green 테스트도 teardown 예외는 FAIL**: Rev.1은 56/56 통과했으나 매 실행 `Test Class Cleanup Failure: ObjectDisposedException`
  동반(FakeModbusWebApplicationFactory.Dispose + NopSorterRegistryFactory.StopAsync가 같은 _fakePolling을 3중 Stop/Dispose →
  PlcGateway.cs:176 disposed CTS 접근). Rev.2에서 폴링 소유권을 NopSorterRegistryFactory.StopAsync 단일 지점으로 통합(Dispose는
  _anchorConnection만)해 해소. → 단언 PASS ≠ 깨끗한 종료. xUnit cleanup-failure 라인을 grep으로 적극 배제.
- **소터별 OFFLINE 타임아웃은 설정 유도값으로**: P2b6의 OFFLINE 대기 = WriteTimeoutMs×(OfflineAfterFailures+1)+여유 — 하드코딩 sleep 아님.
- **flaky 배제는 실 소켓 테스트 standalone 반복으로 확정**: 실 SimServer 기반 P2b4/5/6은 소켓·타이밍 의존이 가장 큼 → 전체 4회 +
  P2bSimHandshakeTests 단독 5회 GREEN으로 비결정성 0 확인. → 새 통합 테스트의 flaky 위험은 전체 회귀와 별도로 표적 반복.
- **문서 deliverable 주장은 git status로 검증**: Rev.1 메시지 "SPEC §7-A 정정 완료"는 거짓(파일 무변경). Rev.2에서 L99 실제 정정 +
  Sorters[] 스키마·DB 주도 판별·fail-loud 명문화. → "정정 완료" 주장은 `git diff docs/`로 대조.
- **Core/게이트웨이 무변경 + 인스턴스화만 N배 패턴 유효**: src/Wcs.Core·PlcGateway(PlcGateway.cs·HandshakeOrchestrator.cs)·Sim3ds·
  Data·Migrations git diff 0바이트. 멀티소터화는 SorterRegistryFactory.StartAsync가 소터별 `new PlcWriteQueue/PlcPollingService/
  HandshakeOrchestrator`를 N개 생성(단일 공유 큐/싱글톤 grep 0, SingleSorterGatewayRegistry 제거)하고 IF-08/IF-10은 chuteNo→dest.Id→
  GetBundle 라우팅 최소 교체뿐(와이어·Decide 판정 불변)로 달성. → 클래스 본문 무변경 + DI 인스턴스화 N배 + 라우팅 교체 = 멀티화 안전 패턴.

## S-M4-P2a (IF-08 분기 + FULL/PAUSED + timeStamp + 멱등 DB 백스톱 + 이관 정리) — APPROVED (2026-06-16, 2 iterations)

- **"이름만 통과" 가드 — 인메모리 집계는 전용 시나리오 없으면 미검증**: Rev.1 FAIL 핵심은 FULL 집계(ChuteCapacityService)가
  구현됐으나 테스트 0건 → 49/49 GREEN이어도 FULL 경로는 한 줄도 실행 안 됨. PAUSED-status 테스트(destination.status DB 필드)는
  FULL 집계와 무관. Rev.2에서 `P2a_If08_Chute_Full_ThenCleared_Normal`(OnReserved(qty=workFullQty)→GetHold=Full→IF-08 FULL→
  OnCleared→GetHold=None→IF-08 READY, qty 합산·COUNT 아님)로 해소. → 기능 슬롯마다 "그 코드가 실제로 실행되는 단언"이 있는지 확인.
- **멱등 DB 백스톱은 코드 테스트의 약한 단언을 Evaluator 정량 프로브로 보강해 입증**: 본 코드 CONCUR1은 "≥1 기록·전부 200"만 단언 —
  exactly-once를 증명 못 함. Evaluator가 임시 프로브(8병렬 후 실제 DB 행 카운트: depositedRows==1, cell_assignment<=1)를
  Rev.1·Rev.2 각 5회 실행해 lock-free 진성 멱등을 ground-truth로 확정. F4의 RowVersion DropColumn 후에도 멱등 불변 재확인.
  → static lock 제거형 멱등은 "전부 200"이 아니라 "DB에 정확히 1행"을 직접 세야 한다.
- **경고0은 무해해도 차단**: F1 CS8714(long? ToDictionary 키, MINOR-5 nullable FK 파생)는 동작 무해하나 계약 Criteria #1 경고0 위반.
  `.Where(x=>x.DestinationId!=null).ToDictionary(x=>x.DestinationId!.Value,...)`로 해소. → "동작 무해"는 경고 잔존의 면죄부 아님.
- **이중 물리 컬럼 제거는 ValueGeneratedNever()가 아니라 Ignore()+DropColumn 마이그레이션**: Rev.1은 `ValueGeneratedNever()`로
  "이중 컬럼 방지"를 주장했으나 물리 컬럼은 그대로 생성됨(코드 주석도 인정). Rev.2에서 `e.Ignore(propertyName)` + 양 provider
  DropColumn(RowVersion×5/XminRowVersion×5) 마이그레이션으로 실제 제거 + SPEC §7-C 문구 정정. has-pending-model-changes 양쪽 "No changes" 재확인.
  → 문서 주장은 실제 스키마(마이그레이션 산출물)와 대조. provider 분기 컬럼 제거는 Ignore()여야 물리 제거.
- **Core 무변경 가드 유효**: 도메인 분기(슈트/소터·hold 산출)를 API 계층에서만 수행, Decide/Models/ToWire 시그니처 불변.
  git diff HEAD -- src/Wcs.Core/ = 0줄(committed/staged/working/untracked 전부). PlcGateway·Sim3ds도 0 — 종료토큰조차 Program.cs에서 주입.

## S-M4-P1 (EF Core 영속화 + 리포지토리 DB 교체) — APPROVED (2026-06-16, 1 iteration to pass)

- **회귀 0 인프라 교체의 강한 가드 충족**: M3 44 테스트가 단언·코드 변경 0으로 GREEN 유지(4회 연속, split 15/9/4/16 불변).
  보호 파일(Wcs.Core·PlcGateway.cs·HandshakeOrchestrator.cs·Dtos.cs + 3개 테스트 파일) git diff 0바이트.
  ApiIntegrationTests 변경은 WebApplicationFactory 배선(앵커 SQLite·EnsureCreated·DbSeeder)에만 한정, Assert 0줄 변경.
  → "동작 무변경 + 데이터만 DB로 영속화" 계약의 본질을 git diff 격리 + 단언 불변으로 입증하는 패턴이 유효.
- **provider 분기는 마이그레이션 산출물로 검증**: piece 유니크(SQLite 일반 UNIQUE vs SQL Server filtered `[is_active]=1`)와
  rowversion 분기가 두 Initial 마이그레이션 파일에 실재. 소스 OnModelCreating의 `IsSqlite` 분기만이 아니라 생성 SQL까지 대조.
- **CONCUR 동시성은 단독 다회 실행으로 flaky 배제**: 8병렬 동일 pId IF-10 → CONCUR1 단독 5회 GREEN.
  static `_recordLock` + 트랜잭션 내 멱등 상태체크가 "정확히 1건 전이 + IF-11 ≤1"를 보장(첫 호출만 true→트리거).
- **재사용 교훈 — static lock의 운영 함의**: 테스트(named in-memory SQLite 단일 writer) 직렬화엔 적합하나
  프로세스 전역 lock은 운영 SQL Server에서 전 투입기록 병목. 정합성 가드로는 DB 유니크 제약/트랜잭션 격리가 정석(P2 정리).
- **재사용 교훈 — 멀티 provider 마이그레이션 스냅샷 분리**: provider별 output-dir만 나누고 ModelSnapshot은 1개를
  공유하면 마지막 생성 provider로 덮여(여기선 SqlServer가 Sqlite 폴더의 스냅샷을 점유) 향후 증분 마이그레이션이 손상.
  → 멀티 provider는 스냅샷도 provider별로 분리하거나 별도 MigrationsAssembly 권장. 단, Initial 2개는 각각 정상이고
     테스트가 EnsureCreated로 마이그레이션을 우회하므로 현재 영향 0 — MINOR(P2).
- **죽은 코드 잔존은 scope OUT 명시 시 PASS**: 구 InMemory* 리포지토리가 파일로 남아도 Program.cs DI에서 인스턴스화 0이면
  "프로덕션 경로 제거" 충족. "죽은 코드 정리→P2" scope OUT 명문화 덕에 비차단. grep로 production 참조 0 확인이 판정 근거.

## S-M0 (솔루션 구성 + 빌드 그린) — APPROVED (2026-06-15, 1 iteration to pass)

- **핵심 교훈 (M0-1)**: SDK 10.0.300에서 `dotnet new sln -n <Name>`은 클래식 `.sln`이 아니라
  `.slnx`(XML 형식)를 기본 생성한다. 계약/검증 명령이 `Wcs.sln`(클래식)을 전제하면
  `dotnet build Wcs.sln`이 MSB1009로 실패한다. 클래식 형식이 필요하면 `--format sln`을 명시.
  → 향후 sln 생성 시 산출물 확장자를 계약 문자 그대로 맞추고, 발산 시 보고서에 명시할 것.
- **검증 원칙 확인**: 생성자가 보고에서 명령을 임의로 바꿔 적으면(여기선 `Wcs.sln`→`Wcs.slnx`)
  발산이 가려진다. Evaluator는 항상 계약 문자 그대로의 명령으로 ground truth 재현해야 함.
- **PASS 구성**: Core 의존성 0(참조·패키지 모두), Sim3ds 프로젝트 참조 0(FluentModbus 패키지만),
  FluentModbus가 Core/Api/Data로 누출 안 됨, 테스트 패키지는 Wcs.Tests에만, net10.0 6개 유지.
  스켈레톤 .cs/.json 무변경 + DepositDecider.Decide 9 RED(NotImplementedException) / Wire 1 GREEN이
  M0의 정상 시작점.
- [CODE-REVIEW] sprint=S-M0 critical=0 major=0 minor=0 iter=0 opus=no (설정/배선 전용 — 소스 로직 0, 오케스트레이터 레벨 diff 리뷰로 가름)

## S-M1 (판정 엔진 DepositDecider) — APPROVED (2026-06-15, 1 iteration to pass)

- **핵심 교훈 (테스트가 스펙)**: SPEC §2 표 7행을 코드 분기 순서(Offline→Hold→Ready/층)와 1:1로
  대조하면 표 밖 동작을 즉시 잡을 수 있다. TgtFloor 쓰기 조건은 한 줄(`Tgt==0 && (cur!=agvFloor || !Ready)`)
  이고 Hold/Offline은 선행 우선순위에서 차단되어 쓰기 분기에 도달하지 않음 — 이 구조가 "Hold/Offline 쓰기 금지"를
  코드로 보장한다(별도 가드 불필요). C1=잔류 TgtFloor 허가 경계, C2=선기입 조건 충족해도 Hold/Offline 차단,
  C3=층일치·Ready=1이어도 Hold 우선 — 세 경계가 표의 함정을 정확히 인코딩.
- **순수성 검증법**: Decide가 static·무필드·DateTime/Random/I-O 없음 + `Wcs.Core.csproj`에 Reference/Package 0
  두 가지로 확정. 테스트의 `DateTimeOffset.UtcNow`는 Snap 헬퍼 한정(판정 로직 밖)이라 비순수 아님 —
  비순수 오탐 주의.
- **검증 환경**: 이 환경은 PowerShell 권한 거부 → Bash(Git Bash)로 `cd "<절대경로>" && dotnet ...` 실행.
  Evaluator는 도구가 막히면 우회 경로로라도 ground truth를 직접 재현할 것(요약 신뢰 금지).
- **E4 범위 판정**: 코드 surface는 src/tests. `tasks/` 하위 sprint-contract.md·sprint-log.md 변경은
  3-Tier 하네스 산출물이라 코드 범위 위반 아님 — `git diff --name-only HEAD`에서 src/tests만 필터해 판정.
- [CODE-REVIEW] sprint=S-M1 critical=0 major=0 minor=0 iter=0 opus=yes (독립 Opus 코드리뷰어 — SPEC §2 7행 1:1·순수성·TgtFloor 클리어 없음·C1~C3 단언 정확, 결함 0. 관찰: WcsHold enum 확장 시 fall-through는 현 enum에선 비결함)

## S-M2 (PLC 게이트웨이 + 시뮬레이터 핸드셰이크) — APPROVED (2026-06-15, 3 iterations to pass)

- **핵심 교훈 (단일 큐 ≠ 단일 소켓 안전)**: 절대규칙 #1(쓰기 단일 큐)을 SingleReader Channel + 단일 컨슈머로
  지켜도, 폴 루프(읽기)와 쓰기 컨슈머(쓰기·RMW)가 **같은 ModbusTcpClient를 별 태스크에서 동시 사용**하면
  소켓 레벨 직렬이 아니다. FluentModbus ModbusTcpClient는 단일 소켓·단일 트랜잭션 버퍼라 동시 호출
  thread-safe 아님 → 프레임 교차·버퍼 경합. 해법: 공유 `SemaphoreSlim(1,1)`로 모든 `_client` 트랜잭션을
  감싸고 **RMW read+write를 한 임계구역**으로 묶음(폴 읽기·각 Write·RMW read+write가 모두 게이트 통과).
  컨슈머 임계구역 내 early `return`도 try/finally로 Release 보장 — 락 누수 없음. SemaphoreSlim 비재진입이나
  RmwD4LockedAsync가 재획득 안 하므로 데드락 없음.
- **회귀 가드**: "폴 진행 중 다수 핸드셰이크 연속(IT-3c)"으로 R_Seq==C_Seq 대사가 매 건 성공함을 단언하면
  프레임 무결성을 동작으로 입증. 직렬 핸드셰이크라도 폴(30ms)이 계속 돌아 poll-vs-write 교차를 커버.
- **검증 원칙 재확인**: 생성자 1차 재제출이 FAIL-2를 "수정 완료" 없이 원 구현만 재기술 → Evaluator가
  `grep`로 `_client` 직렬화 프리미티브 부재(0건)를 ground truth로 확인해 미해소 적발. 요약 신뢰 금지·소스 재검사 필수.
- **flaky 회피 확정법**: 동시성/락 변경은 데드락·타이밍 리스크 → `dotnet test` 4회 연속 GREEN + 테스트 split
  (15 Decider + 8 통합) 불변 확인으로 결정성 입증. FAIL-1(고정 sleep 80ms 제거)은 GW Online 폴링이 흡수.
- **코드리뷰 후속 (off-lock 접근)**: 단일 소켓 직렬화는 폴 읽기·쓰기뿐 아니라 **Disconnect/재연결**도 포함해야 완전.
  폴 catch의 TryReconnect(_client.Disconnect())가 락 밖이면 진행 중 쓰기 트랜잭션과 경합 → `_clientLock` 임계구역으로
  이동해 해소. 검증법: 전 `_client.` 사용처를 grep해 각각 (a)락 보유 중 (b)종료 후 단일스레드 중 하나임을 확인.
  회귀 가드 IT-4b(핸드셰이크 중 서버 단절·재기동 후 후속 핸드셰이크 Success)로 동작 입증. 죽은 코드(테스트 동기화용
  TCS)도 제거 — 실제 테스트는 폴링 대기(WaitUntilAsync) 사용이라 불필요했음.
- [CODE-REVIEW] sprint=S-M2 critical=0 major=1 minor=4 iter=1 opus=yes (독립 Opus 코드리뷰어가 BLOCKING 1건 적발: off-lock _client.Disconnect 경쟁 — 폴 catch의 TryReconnect가 _clientLock 밖에서 실행돼 쓰기 트랜잭션과 소켓 경합. 기능 테스트 24/24 GREEN였지만 구조적으로 못 잡는 동시성 버그. fix-only 1 iter로 해소: Disconnect를 _clientLock 임계구역으로 + IT-4b(단절-중-핸드셰이크) 회귀 가드 추가. minor 4: 죽은 TCS 동기화 코드·"BackgroundService" 주석·InjectNoResponse 주석·IT-3c 과대명명 — 함께 정리. 재검증 RESOLVED, _client. 전 사용처 락 보호 확인, 데드락 없음)
- **메타 교훈**: 기능 Evaluator APPROVED ≠ 코드리뷰 통과. 4-Tier 코드리뷰가 테스트·기능검증이 구조적으로 못 잡는 동시성 결함을 머지 전 한 겹 더 걸러냄. 동시성 코드는 반드시 독립 리뷰.

## S-RTU (Modbus 전송 추상화 + RTU 어댑터) — APPROVED (2026-06-16, 1 iteration to pass)

- **전송 추상화 경계 검증법**: 구상 타입 누수 0을 `grep "ModbusTcpClient|ModbusRtuClient" src/Wcs.PlcGateway`로
  ground truth 확인 — 어댑터(TcpMaster·RtuMaster)·인터페이스 주석에만 등장, PlcPollingService·HandshakeOrchestrator
  직접 참조 0이어야 통과. 인터페이스(IModbusMaster)가 M2 사용 표면(IsConnected/Connect/Disconnect/Dispose +
  FC03 일괄읽기 + FC06/FC16)을 최소·정확 포착.
- **회귀 0 보존 패턴(기본값 변경 시)**: 기본 전송이 Rtu로 바뀌어도 기존 TCP 통합 테스트는 2인수 편의 생성자
  `new PlcPollingService(opt, queue)`(내부 ModbusTcpMaster 생성)로 명시 TCP 경로 유지 → M2 IT 파일 `git diff`
  무변경으로 회귀 0 입증. dev/sim용 appsettings는 `Transport=Tcp` 명시(혼동 방지), 현장 배포만 Rtu.
- **물리 하드웨어 없는 RTU 라이브 테스트**: FluentModbus `IModbusRtuSerialPort` 공개 인터페이스를 in-memory
  Pipe 쌍(FakeSerialPort)으로 구현 → 실제 ModbusRtuClient↔ModbusRtuServer in-process 왕복(CI 가능, COM/com0com 불필요).
  `SimulateClose=true`로 ReadAsync/WriteAsync에서 IOException 유발 → OFFLINE 전이 실증. 빈 단언 아님 — C_Flag·
  CSeq·R_Seq 대사·RMW Ready 보존을 콘솔 출력과 함께 단언.
- **전송 무관 OFFLINE 전이**: 소켓 전용 예외 분기에 의존하지 말 것. isHardEx = SocketException ∪ IOException ∪
  TimeoutException ∪ InnerException(소켓·IO)로 확장해야 RTU 시리얼 타임아웃·IO에서도 OFFLINE 전이. VT-5로 실증.
- **직렬화 불변(RTU 정합)**: RTU 단일 버스 제약(한 버스=한 트랜잭션)이 M2 `_clientLock` 단일 직렬화와 정합 —
  추상화 후에도 폴·쓰기·RMW·Disconnect/재연결 전부 임계구역 통과 유지. 동시성 변경 → 4회 연속 GREEN로 결정성 확인.
- **문서 동기화 동일 커밋**: 전송 확정은 코드뿐 아니라 SPEC §7-A(전송 확정 신설, 舊 §7-A→§7-B 이동)·CLAUDE.md
  다이어그램(Modbus TCP→RTU/TCP) 정정이 같은 변경에 포함돼야 통과. `git diff --name-only`로 SPEC.md·CLAUDE.md 동반 확인.
- [CODE-REVIEW] sprint=S-RTU critical=0 major=0 minor=4 iter=0 opus=yes (독립 Opus — 추상화 경계·M2 동시성 invariant 보존(모든 _master 트랜잭션 _clientLock 내, off-lock Disconnect는 태스크 종료 후만)·양 어댑터 계약 일치·자원 정리 전부 APPROVE, BLOCKING/MAJOR 0)

---

## M3 (API IF-05/08/10 + S-RTU MINOR 4) — APPROVED 핵심 피드백

- **READY는 Core 변경이 아니라 API 계층 주입으로**: Core `DenyReason.None => null`(Models.cs)을 절대 건드리지 말 것.
  허가 사유 문자열 "READY"는 엔드포인트에서 `decision.Allowed ? "READY" : decision.Reason.ToWire()`로 주입.
  통과 기준: `git status` src/Wcs.Core 무수정 + 소스에서 주입 지점 1곳 확인. Core 수정은 통과해도 FAIL.
- **M4 경계는 DB 참조 0으로 입증**: `grep "Wcs.Data|DbContext|EntityFramework|UseSqlServer|UseSqlite" src/Wcs.Api`가
  주석 외 0건이어야 함(ProjectReference·using·인스턴스화 모두). 기준정보(오더·목적지·예약·셀·agvFloor)는 인터페이스+
  인메모리 구현으로, 교체점 1지점. Wcs.Api.csproj에서 Wcs.Data ProjectReference 제거 동반.
- **IHostedService 결선이 M2 수동 Start/Stop을 안 깨야**: PlcPollingService를 직접 BackgroundService로 바꾸지 말고
  어댑터(PlcPollingHostedAdapter)로 브리지 — 수동 StartAsync/StopAsync 경로 보존 → M2 통합테스트 9건 회귀 0.
- **fire-and-forget는 예외 삼킴 금지**: IF-08 SetTgtFloor 큐 투입·IF-10 핸드셰이크 트리거는 응답 완료 대기 X(fire-and-forget)
  이되, `.ContinueWith`로 IsFaulted 로깅 필수. 즉시 OK 반환(핸드셰이크 완료 대기 안 함)이 계약.
- **DTO는 원본 HTML(wcs_rcs_interface_kr.html)이 진실**: IF-05 agvNo 포함, IF-08 timeStamp/IF-10 qty·timeStamp는
  HTML 요청 표에 없으므로 nullable 선택필드(RCS 미전송 허용). NG는 chuteNo=null 직렬화. 가부는 result/allowed 필드(HTTP 200),
  검증실패만 400. JSON은 STJ 기본 camelCase로 와이어 정합.
- **타이밍 민감 통합테스트 flaky 배제**: 백그라운드 큐/핸드셰이크 관찰(C_Flag 상승, TgtFloor 기입)은 고정 sleep이 아니라
  WaitForSnapshot/WaitForRegister 폴링으로 동기화. 검증 시 ApiIntegration 단독 다회 연속 GREEN 재확인(이번 6회).
- **MINOR 정리는 동작 변경 0 입증**: 엔디안 필드화는 기본값을 구동작(BigEndian)과 동일하게, sleep 제거는 선행 동기화가
  대체함을 주석으로, sync Read fail-loud는 async 경로만 사용됨을 확인 후. `git diff`로 4건 실재 + 동작 무변경 확인.
- [CODE-REVIEW] sprint=M3 critical=0 major=1 minor=5 iter=1 opus=yes (독립 Opus가 BLOCKING급 MAJOR 적발: IF-10 멱등 check-then-act 경쟁 → 동시 같은 pId면 IF-11 이중 트리거·셀 이중 할당. 기능 41/41 GREEN였으나 동시성 테스트 부재로 미검출. fix-only 1 iter 해소: RecordDeposit lock 원자화 + IF-11 트리거를 RecordDeposit 반환값으로 단일화(선검사 제거) + CONCUR1(8병렬 동일 pId) 회귀. 함께: IF-05 qty<=0 가드. M4 등재: 죽은 GetDestType·다운캐스트·CancellationToken.None·단일트리거 직접 카운터. 재검증 RESOLVED, 44/44)
- **메타 교훈(반복 확인)**: M2 off-lock·M3 IF-10 멱등 모두 기능 Evaluator GREEN을 통과했으나 4-Tier 독립 코드리뷰가 동시성 결함을 적발. 공유 가변 상태·핸드셰이크 트리거가 있으면 동시성 회귀 테스트 + 독립 리뷰 필수.

## S-M4-P1 (EF Core 영속화 + 리포지토리 DB 교체) — APPROVED 핵심 피드백

- **EF Core 이중 provider 마이그레이션 함정(BLOCKING 교훈)**: 한 DbContext는 마이그레이션 어셈블리당 ModelSnapshot **1개**만 갖는다. SQLite·SQL Server 마이그레이션을 같은 프로젝트에 폴더만 나눠 넣으면 나중 생성분이 스냅샷을 덮어써 한쪽이 베이스라인이 아닌 diff(AlterColumn)가 되고 **신규 DB 생성 불가**. 테스트가 EnsureCreated면 이 결함이 가려진다(영향 0). 해법: **provider별 별도 마이그레이션 어셈블리**(Wcs.Migrations.Sqlite·SqlServer) + 각자 IDesignTimeDbContextFactory로 MigrationsAssembly 고정. 검증: `dotnet ef migrations has-pending-model-changes`가 양 provider "No changes" + 각 Initial이 16 CreateTable.
- **동작 무변경 인프라 교체 입증법**: 보호 파일(Wcs.Core·PlcGateway.cs·HandshakeOrchestrator.cs·Dtos.cs) `git diff` 0바이트 + 기존 44 테스트 단언·split 불변(15/9/4/16) 4회 연속 GREEN. Program.cs는 DI 교체만.
- **테스트 DB 동시성**: named in-memory SQLite(`Mode=Memory;Cache=Shared`)로 각 DbContext 독립 연결·같은 DB 공유 → 중첩 트랜잭션 오류 회피. 멱등은 트랜잭션 + static lock(P1 단일 인스턴스 가정 — SPEC §7-C).
- [CODE-REVIEW] sprint=S-M4-P1 critical=1 major=1 minor=4 iter=1 opus=yes (독립 Opus가 BLOCKING 적발: 이중 provider 마이그레이션 무효(스냅샷 1개·SQL Server가 diff라 신규 DB 생성 불가). 기능 44/44 GREEN였으나 테스트가 EnsureCreated로 마이그레이션 미사용 → 미검출. fix-only 1 iter 해소: provider별 마이그레이션 어셈블리 분리 + 깨끗한 베이스라인 재생성(각 16 CreateTable·스냅샷 provider-정확·pending 0). MAJOR-1(멱등 다중인스턴스 DB 백스톱)+MINOR-2/4/5/6은 P2로 SPEC §7-C 기록. 재검증 RESOLVED, 44/44, 테이블 정확히 16. 비차단: 테스트 teardown ObjectDisposedException(기존 하네스 artifact, 테스트 실패 아님))

## S-M4-P2a (IF-08 분기 + FULL/PAUSED + timeStamp + 멱등 DB 백스톱) — APPROVED 핵심 피드백

- **IF-08 목적지 타입 분기는 API 계층에서**: chuteNo→destination(dest_type) 조회 후 SORTER_3D→그 소터 게이트웨이 스냅샷+`Decide(snap,agvFloor,hold)` / CHUTE→hold만(층·Ready·TgtFloor 쓰기 없음, None→READY/Full→FULL/Paused→PAUSED/비활성→PAUSED). **Wcs.Core(Decide·Models) 무변경** — hold만 산출해 주입. destination.id 단일 진입점(ISorterGatewayRegistry)으로 P2b 멀티소터 확장점 선확보.
- **FULL 인메모리 집계의 영속화 함정(코드리뷰 MAJOR 2)**: 기능 테스트가 단일 프로세스 인메모리만 보면 **재시작 정확성 버그**가 가려진다. (1) 비움(OnCleared)은 인메모리 리셋만이 아니라 **chute_detail.last_cleared_at + destination_event(CLEARED)를 DB에 영속화**해야 함(ERD §7/§14). (2) 기동 재구성(InitializeFromDb)은 **`deposited_at>last_cleared_at` 필터**를 반드시 적용(없으면 비움 이전 piece 재합산→FULL 복귀). 둘은 복합(영속화돼야 필터가 의미). 검증=재시작 시뮬레이션(DB에 FULL piece 삽입→OnCleared→StartAsync 재실행→NORMAL 단언) 회귀 테스트 필수.
- **멱등 DB 백스톱(static lock 제거)**: piece 부분 유니크 `(p_id) WHERE is_active=1 AND status IN(활성3)` + **위반 catch는 provider 에러코드로**(SQLite SqliteExtendedErrorCode==2067 / SQL Server Number 2601·2627) — 메시지 문자열 매칭 금지(비영어 서버·타 인덱스 오판). 부분 유니크는 신규 piece insert 경합만 백스톱(RESERVED→DEPOSITED 업데이트는 write lock+status 재확인이 직렬화). 8병렬 동일 pId lock-free 정량 프로브(depositedRows=1·cellAssign=1) 5회로 입증.
- [CODE-REVIEW] sprint=S-M4-P2a critical=0 major=2 minor=3 iter=1 opus=yes (독립 Opus가 FULL 집계 재시작 정확성 MAJOR 2건 적발: OnCleared 비움 미영속화→재시작 FULL 복귀, InitializeFromDb deposited_at>last_cleared_at 필터 누락→과다 집계. 기능 50/50 GREEN였으나 단일프로세스 인메모리만 봐서 미검출. fix-only 1 iter 해소: OnCleared DB 영속화(last_cleared_at+destination_event, 스코프 트랜잭션, 락 밖 I/O) + 필터 추가 + 재시작 회귀 테스트. MINOR: 멱등 catch 에러코드화·주석 정정·죽은 fallback 제거. 재검증 RESOLVED, 51/51 4회, Core diff 0. wcs_dev.db는 .gitignore 처리.)
- **메타 교훈(3회째 반복)**: M2 off-lock·M3 IF-10 멱등·M4-P1 마이그레이션·M4-P2a FULL 영속화 — 전부 기능 Evaluator GREEN 통과 후 4-Tier 독립 코드리뷰가 적발. **인메모리/단일프로세스 테스트는 재시작·동시성·실DB 경로를 구조적으로 못 본다 → 독립 리뷰 필수.**

## S-M4-P2b (멀티 소터: 소터별 게이트웨이 번들 N대) — APPROVED 핵심 피드백

- **확장점 선확보가 무변경 교체를 가능케 함**: P2a에서 `ISorterGatewayRegistry` 단일 진입점 + 번들 인스턴스별 상태(`_clientLock`/`_writeQueue`/`_cSeq`/RFlag 채널이 전부 instance 필드, static 0)를 깔아둬서, P2b는 PlcGateway·HandshakeOrchestrator·Sim3ds **클래스 본문 무변경**으로 DI 팩토리 + 라우팅만으로 N대 확장(git diff 0). 단일 공유 큐 싱글톤 제거→소터별 `new PlcWriteQueue()`.
- **소터 판별 DB 주도 + 설정 ChuteNo 매칭 + fail-loud**: 기동 시 `dest_type=SORTER_3D` 조회로 소터 목록(단일 진실=DB), 전송 파라미터는 appsettings 소터 배열에서 chute_no로 매칭. SORTER_3D인데 설정 누락→InvalidOperationException(기동 실패, 조용한 스킵 금지). DB 쿼리 실패→Critical 로그+rethrow.
- **멀티 인스턴스 격리·핸드셰이크 독립 검증법**: 실 Sim3ds **2대를 다른 포트**로 띄워 동시 핸드셰이크 → 각 소터 C_Seq↔R_Seq 교차 0 입증(fake로는 못 잡는 진성 검증). 인스턴스별 직렬화·소터별 OFFLINE 독립도 실소켓으로. 단독 5회 연속 flaky 0.
- **수명주기 disposal 비대칭(M5 이관)**: 번들이 DI 등록이 아니라 StartAsync 내 수동 생성이라, 종료 시 StopAsync(=Disconnect, 포트 해제)는 하나 `_master/_cts/_clientLock` Dispose는 미호출(관리 객체 누수, 프로세스 종료로 회수·포트는 해제됨). M5 운영(graceful shutdown)에서 SorterRegistryFactory를 IAsyncDisposable로 + 번들별 DisposeAsync(비멱등 StopAsync/DisposeAsync 이중 호출 주의).
- [CODE-REVIEW] sprint=S-M4-P2b critical=0 major=0 minor=2 iter=0 opus=yes (독립 Opus APPROVE — 인스턴스 격리(static 0·번들별 독립)·F4 disposal 수정 진성(테스트 와이어링 단일 소유)·라우팅 무교차·DB 주도 fail-loud·실소켓 핸드셰이크 독립 진성·동작/스키마 무변경 전부 확인. BLOCKING/MAJOR 0. MINOR 2(M5/정리 이관): ① SorterRegistryFactory 번들 Dispose 누수(종료 시 _master/_cts/_clientLock 미dispose, 포트는 해제됨 — M5 graceful shutdown) ② Program.cs CHUTE !dest.IsActive 죽은 분기(쿼리가 이미 IsActive 필터, 선재 결함·무해). 59/59 4회, 실소켓 5회.)
- **메타 교훈(코드리뷰 APPROVE 첫 통과)**: P2b는 4-Tier 독립 코드리뷰에서 **BLOCKING/MAJOR 0으로 첫 통과** — P2a가 확장점·인스턴스 격리를 미리 깔아둔 덕. 동시성 표면이라도 "클래스 본문 무변경 + 인스턴스화만" 구조면 결함 표면이 최소화됨.

## S-M4-P3 (시나리오 S1~S9 자동화 + alarm/sorter_command 영속화 결선) — APPROVED 핵심 피드백

- **갭 영속화는 API 계층 한정·단방향 경계 사수**: alarm/sorter_command DB 쓰기(현 0)는 `IAlarmSink`/`ISorterCommandJournal`(Wcs.Api) + EF 구현으로만. `HandshakeOrchestrator`/`PlcGateway`는 DB 무지(Wcs.PlcGateway→Wcs.Data 참조 0) — OFFLINE 전이 1회 신호(`OnOfflineTransition` 이벤트)만 노출하고, IF-10 결과는 API의 `ContinueWith` 콜백에서 `IServiceScopeFactory` 별도 스코프로 영속화(요청 스코프 종료 후 안전). Wcs.Core 판정·게이트웨이 본문·Sim3ds **git diff 0** 유지.
- **OFFLINE 전이당-1건 멱등은 원자화 필수(BLOCKING급 MAJOR-1)**: `PublishOffline`의 `prev=_latest; _latest=off; if(prev.Online) Invoke`는 락 밖 비원자 check-then-act이고 호출원이 둘(폴 루프 catch + 쓰기 컨슈머 catch). 소켓 동시 사망 시 양쪽이 prev.Online=true 읽어 **alarm 2건**(계약 "전이당 1건" 위반). 해법: `int _online=1` + `if(Interlocked.Exchange(ref _online,0)==1) Invoke` (승자만 발화) + ONLINE 복구 시 `Interlocked.Exchange(ref _online,1)` 리셋(재전이 1건 보장). `_stopped`와 동형.
- **이벤트 핸들러가 폴 스레드를 죽이면 안 됨(MAJOR-2)**: OFFLINE 구독 핸들러는 폴 스레드(RunPollLoopAsync)에서 직접 호출 → scope 생성·DI 해석·Append **전 구간**을 단일 try/catch(Exception)로 감싸야 함(부분만 감싸면 DI 예외가 폴 루프 영구 종료→OFFLINE/ONLINE 영영 미감지). teardown 방어 catch는 **로깅만** 감싸고 `failures++`/`PublishOffline()`는 try 밖에 둬 OFFLINE 전이가 가려지지 않게(Fail-Loud 보존).
- **enum 전 분기 명시(MAJOR-3)**: HandshakeOutcome 5값(Success/RSeqMismatch/RFlagTimeout/Offline/CFlagTimeout)을 switch default(`_=>TIMEOUT`/`RFLAG_TIMEOUT`)로 뭉개면 IF-08~IF-10 사이 OFFLINE 시 거짓 영속화. Offline/CFlagTimeout 명시 분기(DB CHECK 4값상 status는 TIMEOUT 유지하되 **alarm code `OFFLINE`/`CFLAG_TIMEOUT`로 사유 구분**).
- **flaky 대응은 고정 sleep 증가 금지 — 조건 동기화**: S7 재전이 타임아웃을 `Task.Delay(2000)`로 덮는 건 안티패턴(reconnect 비결정성 미해결·부하 시 여전히 깨짐). ONLINE 복구는 `WaitUntilAsync(IsSorterOnline)`. **부재 단언(no-flood: 추가 alarm 0건)**은 positive-condition 폴링이 불가하나, 폴 카운터 계측은 무변경 가드(PlcSnapshot/Sim3ds 보호)를 깨므로 → `WaitUntilExactAsync(expected, stableCount:5)`(count가 N회 연속 expected 유지해야 통과, flood로 어긋나면 실패)로 **바운드 고정 settle보다 강한 회귀가드** 달성.
- **오케스트레이터 직접 검증(file-mtime+grep)이 헛 보고 차단**: generator가 "수정 완료"를 보고했으나 파일 mtime 미변경/핵심 줄 잔존이 여러 라운드 반복. orchestrator가 매 보고마다 `stat mtime` + `grep`으로 working tree를 직접 검사해 stale·부분수정·"×5 GREEN으로 닫기"를 걸러냄. **에이전트 자기보고는 산출물 직접 검사로 교차검증.**
- [CODE-REVIEW] sprint=S-M4-P3 critical=0 major=3 minor=1 iter=2 opus=yes (독립 Opus가 동시성/생명주기 MAJOR 3건 적발: ①OFFLINE 전이당-1건 멱등이 동시 실패에서 깨짐(비원자 check-then-act, 호출원 2개) ②OFFLINE 핸들러 DI 예외가 폴 루프 영구 종료 ③Offline/CFlagTimeout switch default 오분류. 기능 69/69 GREEN였으나 S7 단일 idle 소터라 동시 실패 경합 미발생·정상경로 예외 없음·S1~S9에 mid-seq OFFLINE 없어 미검출. iter2 해소: Interlocked 원자화+ONLINE 리셋 / 핸들러 전체 try-catch / 명시 분기 + S7 3-phase(==2) 보강. 고정 sleep→WaitUntilExactAsync. MINOR-1(IF-10 콜백 GetRequiredService throw 시 ReleaseCell 스킵 셀누수 — 호스트종료/DI오설정 한정, M5 이관). 재검증 APPROVE 69/69, S6/S7·S7보강 ×5, 무변경 가드 0·경계 0.)
- **메타 교훈(5회째 반복 — 가장 강한 사례)**: M2 off-lock·M3 IF-10 멱등·M4-P1 마이그레이션·M4-P2a FULL 영속화에 이어 **M4-P3 OFFLINE 전이당-1건 멱등**까지 — 전부 기능 Evaluator GREEN 통과 후 4-Tier 독립 코드리뷰가 적발. 특히 P3는 검증 phase(시나리오 작성)였는데도 **시나리오 자체가 단일 idle 경로라 동시 실패 경합을 구조적으로 못 봄** → "테스트 GREEN ≠ 결함 없음"은 검증 코드에도 적용. 공유 가변 상태·이벤트 발화·폴 스레드 콜백이 있으면 독립 리뷰 + 원자성 검토 필수.

## S-RCS-IF-REDESIGN Phase 1 (인바운드 재설계: IF-05 reason제거·FULL/PAUSED→NG / IF-09 신설 2층정렬 / deposit-permission 폐지 / Controller 이관) — APPROVED (조건부, 2026-06-24, 1 iteration to pass)

- **teardown hang은 "선재 vs 도입"을 baseline worktree 동일명령 재현으로 가른다(이번 핵심 판정법)**: full `dotnet test`가 단언 70/70 PASS이나 testhost가 teardown에서 행→**exit 1·run abort**(team-lead 관찰 그대로, 깨끗한 GREEN 아님). M4-P3 선례(Generator 도입 ObjectDisposedException)와 혼동 위험 → Evaluator가 `git worktree add /tmp/x <baseline-sha>` 후 동일 `dotnet test --blame-hang-timeout` 실행 → **develop@1501ccd에서 동일 hang(69 후 abort) 재현**으로 "스프린트 도입 아님" 확정. 근본원인(PlcGateway 폴루프/IHost disposal)이 계약 "무변경 유지" zone이라 Phase 1이 고칠 수 없음 → APPROVED 비차단하되 **BLOCKING-for-CI로 todo.md 등재**. → 교훈: teardown 크래시는 무조건 FAIL이 아니라, (a)baseline 재현으로 도입여부 (b)근본원인이 수정가능 zone인지 두 축으로 판정. 둘 다 "선재+수정금지zone"이면 명시·추적하되 비차단.
- **격리 진단으로 hang 표면을 좁힌다**: 클래스별 단독·소그룹은 전부 exit 0(ApiIntegration 24·Decider+gateway 19·socket 6클래스 11·S8 2). **full-suite 조합 teardown에서만** 발생 → 다중 IHost/PlcPollingService 정리 + xUnit collection teardown 순서 상호작용으로 특정. 단일 테스트 로직 결함 아님을 입증. → 타이밍 표적 flaky 검증은 socket 클래스만 필터해 5/5 clean exit 0로 분리 확정(full-suite hang에 오염되지 않음).
- **Generator의 "결정적 통과" 주장은 fresh 실측으로 반증**: "`--blame-hang-timeout 90s`로 5/5 GREEN"은 내 실행에서 여전히 exit 1·abort·hangdump → 주장과 불일치. `--blame-hang-timeout`은 단언 결과는 보여주나 hang 자체는 해소 못 함(timeout 후 dump 뜨고 abort). → "blame 플래그로 통과"는 hang 은폐가 아니라 hang 측정일 뿐. 요약 신뢰 금지, exit code·`중단됨` 라인 직접 확인.
- **Core 시그니처 변경 허용 sprint의 순수성 가드**: Phase 1은 DenyReason(WrongFloor→NotAligned)·DepositDecision(Allowed→Ready) 시그니처 변경 허용 → Core diff≠0 정상. 순수성은 별도 입증: csproj Reference/Package 0 + DepositDecider static·무필드·DateTime/Random/IO grep 0 + RegisterMap/PlcSnapshot.FromRegisters 본문 diff 0(시그니처 변경분은 enum/record만, 레지스터 맵 불변). → "시그니처 변경 허용 ≠ 순수성 검증 면제".
- **단일 산출 함수화 = Phase 2 확장점 선확보 패턴(P2a→P2b 재현)**: DestinationStatusService.Compute가 슈트(ChuteCapacity hold)·소터(스냅샷+DepositDecider) ready를 한 경로로 접어 IF-05 NG 필터가 소비 + Phase 2 푸시 재사용. 개별 full/paused 외부 미노출. 푸시 클라이언트 미구현(grep: destination-status 주석 1건뿐, HttpClient POST 0) → 스코프 경계 준수. → Phase 분할 시 "다음 Phase 소비자가 쓸 산출만 함수로 선확보, 실제 I/O는 다음 Phase"가 깨끗한 경계.
- **deposit-permission 폐지 입증 3종**: src grep(주석 3건뿐·DTO타입 0·엔드포인트 0) + 타입명 `DepositPermissionRequest/Response` src 0(문서에만) + 라이브 호출 404/405 테스트. → "제거" 주장은 grep 0 + 부재 입증 테스트 둘 다.
- **메타 교훈(검증 phase의 baseline 대조)**: 이번엔 동시성 결함이 아니라 **선재 환경 이슈를 스프린트 결함으로 오판하지 않기**가 핵심. team-lead의 "teardown=FAIL" 지침을 기계적 적용하면 무변경 baseline 이슈로 정당한 작업을 차단했을 것 — baseline worktree 재현이 오판을 막음. **"teardown 크래시 발견 → 즉시 FAIL"이 아니라 "→ baseline 재현으로 귀속 판정"이 정확한 절차.**
- [CODE-REVIEW] sprint=S-RCS-IF-REDESIGN-P1 critical=0 major=0 minor=2 iter=0 opus=yes (독립 Opus APPROVE — Controller 이관·IF-09 2층정렬·deposit-permission 제거(grep 0+404테스트)·IF09_ARRIVAL 빈 Up/Down(enum→string·CHECK없음→DDL 불요, 스냅샷 byte-identical)·2층 설정화·Core 순수성·무변경 가드(PlcGateway/Handshake/Sim3ds diff 0) 전부 입증. **teardown hang 독립 귀속 = 선재**(신규 IHostedService/BackgroundService/IDisposable 0 — Phase1은 WcsTeardownGuard·FakeSerialPort.DisposeAsync(CancelPendingRead)·팩토리 DisposeAsync override로 오히려 완화. 근원 IHost/PlcGateway disposal). MINOR 2(이연): ①IF-09 fire-and-forget ContinueWith 로깅 비대칭(IF-10 SafeLog 미적용 — 단 WcsTeardownGuard가 InvalidOperationException 흡수) ②IAgvFloorResolver dead registration(2층 고정으로 .Resolve() 0·기록용 잔존). BLOCKING/MAJOR 0.)
- **메타 교훈(독립 리뷰의 baseline 귀속 교차검증)**: Evaluator가 baseline worktree 재현으로 teardown hang을 선재로 귀속했고, 독립 코드리뷰가 "신규 IHostedService/IDisposable 0"로 **코드 관점에서 교차 확인**(Phase1이 hang에 기여 안 함, 오히려 완화). 환경 이슈 귀속은 ①baseline 재현(동작) + ②신규 수명주기 컴포넌트 grep(코드) 둘로 입증하면 견고.
- [CODE-REVIEW] sprint=S-RCS-IF-REDESIGN-P1-teardownfix critical=0 major=0 minor=1 iter=0 opus=yes (독립 Opus APPROVE — full-suite teardown hang(exit1·abort) 해소 fix. SorterBundleHandle.StopPollingAsync가 _writeQueue.Writer.TryComplete()로 쓰기 컨슈머 ReadAllAsync 결정적 종료(빈 채널 CTS-only 취소 경쟁 해소). 7엣지 검증: 동일 인스턴스(Program 214→219→229)·complete-먼저-StopAsync 순서 무충돌·TryComplete 멱등(false 반환)·late enqueue 안전(IF-09/핸드셰이크 enqueue가 ContinueWith IsFaulted/try-catch+ApplicationStopping ct로 격리)·in-flight 드레인 시 _clientLock+TgtFloor==0 재확인 보존·PlcGateway/Handshake/Sim3ds diff 0·테스트 4지점 단언 무변경. evaluator 6회 연속 exit0·70/70·hangdump0. MINOR: 미등록 dead code PlcPollingHostedAdapter.StopAsync가 큐 완료 누락(production DI 미등록·P2a 레거시 — 정리 권고, 라이브 위험 0).)
- **메타 교훈(teardown 채널 경쟁 = CTS-only 취소의 함정)**: Channel 컨슈머가 `await foreach(ReadAllAsync(ct))`로 빈 채널에 parked면 CTS 취소만으론 즉시 안 깨어나는 타이밍 경쟁 → StopAsync가 쓰기 태스크를 영원히 await → 호스트 종료 데드락/testhost teardown hang. **해법: 종료 시 Writer.TryComplete()로 채널 완료 → ReadAllAsync가 드레인 후 정상 종료(취소 토큰과 병행).** 단언 PASS여도 exit1·abort면 깨끗한 GREEN 아님 — exit code·`중단됨` 라인 직접 확인. (generator가 스프린트 종료 후 범위 밖 freelancing으로 수정 — 결과물은 정확했으나, 범위 밖 작업은 정식 검증(evaluator+독립리뷰) 거쳐 채택하는 게 원칙.)

## S-RCS-IF-REDESIGN Phase 2 (IF-08 아웃바운드 목적지 상태 푸시: 전이 감지·전이당 1회·재시도·동시성 멱등) — APPROVED (2026-06-24, 1 iteration to pass)

- **전이 추적 멱등은 "Computed/Acked 분리 + Gate락 안 원자 클레임"으로 구조적 보장(P3 교훈 선제 적용 성공 사례)**: P3는 비원자 check-then-act(`prev=_latest; _latest=off; if(prev.Online)Invoke`)가 동시 호출에서 2건 발화 → iter2 Interlocked 수정이 필요했다. Phase 2는 **착수부터** per-destination `Gate` 락 안에서 `(Acked!=Computed) && !PushInFlight`를 원자 판정+클레임 → 한 스레드만 푸시, 나머지 즉시 return. 누락은 in-flight 완료 후 락 안 재평가로 흡수(`Computed!=Acked`면 while 계속). 성공 시만 `Acked=target`, 실패 시 Acked 불변(미알림 유지·복구 재푸시·확정3). → "전이당 1회"는 주기 전송 아님이 단일 상태(Acked/Computed)로 구조 보장. P3 교훈이 다음 동시성 스프린트에 선제 반영돼 iter1 통과.
- **PUSH4(전이1+무전이N통지)는 중복억제 경로일 뿐 — 진성 동시 전이 경합은 barrier 동시관찰 프로브로 별도 입증**: committed PUSH4는 전이 1회 후 16개 "무전이" 통지(Acked==Computed→즉시 return 경로)라, 같은 전이를 N스레드가 동시에 클레임하는 경합은 직접 치지 않는다(P3 "단일 idle 경로" 함정과 동형). Evaluator가 임시 프로브(같은 true→false 전이를 32스레드 `Barrier`로 동시 관찰→정확히 1건)를 5회 실행해 **진성 클레임 경합에서 exactly-once**를 행동 입증. → 동시성 시나리오는 "동시에 발사"가 아니라 "동시에 같은 전이를 보게" 설계해야 클레임 경합을 친다.
- **무변경 zone에서 소터 전이 감지 = 추가 이벤트 노출 0, 기존 Latest 주기 관찰만**: 소터 ready 전이는 `OnOfflineTransition`형 추가 이벤트 노출 없이 `bundle.Latest`를 SorterObserveIntervalMs 주기로 diff(관찰 타이머). git diff develop로 PlcGateway/HandshakeOrchestrator/Sim3ds/Core 0줄 + 추가 이벤트 노출 0 입증. 슈트는 ChuteCapacityService에 단방향 `OnChuteStateChanged` 이벤트 1개만 추가(락 밖 발화·구독자 예외 흡수, 집계 동작 무변경). → "관찰만으로 충분하면 이벤트 노출조차 추가하지 않는다"가 무변경 zone 최소 침습.
- **폴마다 폭주 0 가드 = WaitUntilExactAsync(expected, stableCount:6)**: 소터 관찰 타이머가 매 주기 도는데도 전이 없으면 푸시 0 — PumpAsync가 Computed==Acked면 즉시 return. P3 no-flood 가드(stableCount)를 재사용해 "N회 연속 expected 유지, 폭주로 어긋나면 실패"로 부재 단언. positive-condition 폴링 불가한 "푸시 0건"을 강하게 검증.
- **teardown 회귀 0(Phase 1 fix 보존)**: 신규 HostedService(DestinationStatusPusher)가 멱등 StopAsync(`Interlocked _stopped`)+CTS 취소+관찰 task await로 graceful 종료. full-suite 76/76 exit 0·hangdump 0(Blame "시퀀스 파일 미생성"). → 신규 IHostedService/타이머/HttpClient는 Phase 1이 해소한 채널 완료 teardown 패턴을 깨지 않도록 종료 경로를 반드시 동형 설계.
- **재시도/URL 전부 설정값, 직접 HttpClient 0**: `new HttpClient(` grep=주석뿐(IHttpClientFactory named client "RcsPush" 경유). RetryCount/BaseDelay/MaxDelay/HttpTimeout/ObserveInterval 전부 RcsPushOptions(appsettings). 지수백오프=`base<<(attempt-1)` 상한 클램프(고정 sleep 0). BaseUrl 미설정→경고+비활성(크래시 0·인바운드 정상).
- **메타 교훈(P3 교훈의 선제 적용 = iter1 통과)**: M2 off-lock·M3 IF-10 멱등·M4-P1 마이그레이션·M4-P2a FULL 영속화·M4-P3 OFFLINE 전이당-1건 — 5회 연속 "기능 GREEN 후 독립리뷰가 동시성 적발"이었으나, **Phase 2는 그 교훈을 착수 설계에 선반영(원자 클레임·Acked/Computed 분리)해 iter1 functional 통과**. Evaluator도 committed 테스트의 동시성 커버리지 약점(무전이 통지 경로)을 간파해 barrier 프로브로 보강. 단 Step 4.5 독립 코드리뷰는 여전히 별도 게이트(정적 구조 관점은 행동 테스트가 못 보는 결함을 봄).
- [CODE-REVIEW] sprint=S-RCS-IF-REDESIGN-P2 critical=0 major=0 minor=2 iter=0 opus=yes (독립 Opus APPROVE — 아웃바운드 푸시 동시성 7가설 전부 무결함. #1 in-flight 전이 유실 없음: PumpAsync claim 시 target=Computed 스냅→성공 시 Acked=target→재평가는 Gate락서 Computed 신규 재독해 비교, X→Y 전이 시 Computed=Y 보존되어 while 재펌프로 Y 푸시(누락0·전 Computed 쓰기/claim/재평가 동일 Gate락 직렬화·lost-update 0). Acked 성공 시만 갱신(실패 시 불변·확정3). OnChuteStateChanged ExitWriteLock 후 발화(재진입 데드락0·핸들러 예외 try/catch 격리·torn read 0). StopAsync Interlocked _stopped 멱등·관찰 task OCE break·미관찰 예외 0. IHttpClientFactory named "RcsPush"·하드코딩 0. 무변경 가드 PlcGateway/Handshake/Sim3ds/Core/RcsController/DestinationStatusService diff 0(소터 이연 보존). MINOR 2(후속): ①슈트 복구 재푸시 비대칭(소터만 타이머 재평가·슈트는 다음 이벤트까지 stale·상태오염0) ②teardown disposed-CTS 접근 spurious error 로그(크래시·hang·미관찰예외 0). evaluator 76/76·exit0·32스레드 동시전이 프로브 5/5 멱등.)
- **메타 교훈(claim-release-reevaluate 정확성 = 푸시 전이 유실 방지 패턴)**: 비동기 푸시(네트워크 지연) 중 상태가 재전이하면 유실 위험 → claim 시 값 스냅+성공 시 그 스냅값으로 Acked 갱신+해제 후 Computed 재독해 비교·재펌프. 전 상태쓰기/claim/재평가를 단일 락으로 직렬화하면 lost-update 0. 동시성 입증은 32스레드 Barrier로 같은 전이를 정면 경합시켜 "정확히 1건" 행동 확인(중복억제 테스트만으론 claim 경합 미검증).
