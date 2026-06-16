# Sprint Feedback — S-M4-P1 (EF Core 영속화 + 리포지토리 DB 교체)

## 판정 (코드리뷰 BLOCKING 픽스 재검증): APPROVED (유지)

코드리뷰 BLOCKING(provider별 마이그레이션 베이스라인 분리) 수정 ground truth 재검증. 직전 APPROVED의 MINOR-1(스냅샷 위생)이 BLOCKING으로 승격되어 처리됨 — 결함 해소 확인.

### BLOCKING 해소 증거 (직접 재실행)
- **구조 분리**: `src/Wcs.Migrations.Sqlite/`·`src/Wcs.Migrations.SqlServer/` 신규 어셈블리 각각 독립 ModelSnapshot 보유.
  구 `src/Wcs.Data/Migrations/`·`WcsDbContextFactory.cs` **삭제 확인**(ls 부재). Program.cs MigrationsAssembly가 provider별("Wcs.Migrations.Sqlite"/"Wcs.Migrations.SqlServer")로 분기. Wcs.Api.csproj 두 ProjectReference 추가. Wcs.sln 2 프로젝트 추가.
- **스냅샷 provider 정합**: SQLite 스냅샷 = `UQ_piece_pid_is_active`(일반 unique), UseIdentityColumns 0, HasFilter 0. SQL Server 스냅샷 = `UseIdentityColumns` + `UQ_piece_pid_where_active` HasFilter("[is_active] = 1"). 더 이상 한 스냅샷이 양 provider를 점유하지 않음(직전 결함의 정확한 해소).
- **스냅샷=모델 일치(핵심)**: `dotnet ef migrations has-pending-model-changes` 양 provider 모두 **"No changes have been made to the model since the last migration"** — 스냅샷이 모델과 완전 동기. 향후 증분 마이그레이션 손상 위험 제거.
- **마이그레이션 적용 가능(DB update surrogate)**: `dotnet ef migrations script` 양 provider 정상 생성.
  - SQL Server(idempotent): 17 CREATE TABLE(16 도메인 + __EFMigrationHistory), 16 ERD 테이블 전부, `rowversion` 컬럼, `CREATE UNIQUE INDEX [UQ_piece_pid_where_active] ON [piece] ([PId]) WHERE [is_active] = 1`(필터드).
  - SQLite: 17 CREATE TABLE, `CREATE UNIQUE INDEX "UQ_piece_pid_is_active" ON "piece" ("PId","IsActive")`(일반), 필터 WHERE 0건. provider 분기가 생성 DDL에 실재.
  - 두 Initial 마이그레이션 AlterColumn 0(베이스라인 깨끗).
- **회귀 0**: build 경고0/오류0(신규 2 어셈블리 포함). `dotnet test` **4회 연속 44/44 GREEN**, split 불변(15/9/4/16).
- **보호 파일 무수정 유지**: Wcs.Core·PlcGateway.cs·HandshakeOrchestrator.cs·Dtos.cs git diff 0바이트.
- **문서**: docs/SPEC.md §7-C 신설 — 단일 인스턴스 가정 명문화 + MAJOR-1(다중 인스턴스 멱등=부분 유니크 인덱스로 static lock 대체)·MINOR P2 이관 목록 기록. 직전 MINOR-3(static lock 운영 병목) 추적 항목으로 정식 이관됨.

→ BLOCKING 해소, 회귀 0, 동작 무변경 유지. **APPROVED 유지.** 잔여 MINOR-2(양 provider RowVersion+XminRowVersion 잉여 컬럼)·MAJOR-1(static lock→부분 유니크)는 SPEC §7-C로 P2 추적 — 비차단.

---

## 판정: APPROVED

Evaluator GROUND TRUTH 재검증(소스 직접 검사 + dotnet 직접 재실행, 생성자 요약 불신).
build exit 0(경고 0/오류 0). SDK 10.0.300. 브랜치 `feat/m4-p1-persistence`(develop 직접 0).
전 시나리오 PASS — 동작 무변경 인프라 교체 + 회귀 0 강한 가드 충족.

### VS-P1-1 회귀(필수) — PASS
- expected: `dotnet test Wcs.sln` 4회 연속 44/44, split 불변(Decider 15/PlcGatewayIntegration 9/RtuTransport 4/ApiIntegration 16).
  보호 파일(Wcs.Core·PlcGateway.cs·HandshakeOrchestrator.cs·Dtos.cs·DepositDeciderTests·PlcGatewayIntegration·RtuTransport) git diff 무변경. ApiIntegration은 시드/배선만, 단언 불변.
- actual: **4회 연속 44/44 GREEN, 실패 0, ~3s**(flaky 0). split 직접 필터 확인 — Decider 15 / PlcGatewayIntegration 9 / RtuTransport 4 / ApiIntegration 16 = 44 정확 일치.
  `git diff --stat`로 보호 7개 surface(Wcs.Core, PlcGateway.cs, HandshakeOrchestrator.cs, Dtos.cs, DepositDeciderTests, PlcGatewayIntegrationTests, RtuTransportTests) **diff 0바이트** 확인.
  ApiIntegrationTests.cs diff = 57 insert/4 delete, 전부 `FakeModbusWebApplicationFactory`(앵커 SQLite 연결·EnsureCreated·DbSeeder 배선)에 한정. `git diff | grep Assert` → **단언 변경 0줄**. VS-1~7·CONCUR1·MINOR1 본문 무변경.

### VS-P1-2 ERD 16 대조 — PASS
- WcsDbContext DbSet 16개 ↔ ERD 16테이블 1:1(destination·cell·cell_assignment·agv·printer·chute_detail·induction·work_batch·wcs_order·order_item·piece·piece_event·sorter_command·plc_event·alarm·destination_event). 누락/추가 0.
- 대리키 `Id`(bigint identity, ValueGeneratedOnAdd) 전부. 자연키 UNIQUE(ChuteNo·AgvNo·PrinterNo·InductionNo·(WorkDate,BatchNo,WaveNo)·(WorkBatchId,OrderNo)·(OrderId,Barcode)·(DestinationId,CellNo)). chute_detail PK=FK(1:1, ValueGeneratedNever).
- enum 12종 전부 `HasConversion<string>()` + MaxLength. 이력 테이블(piece_event·plc_event·destination_event) 네비게이션 단방향·UPDATE 경로 없음(append-only). 상태 테이블 row_version/updated_at, created_at UTC(시드·리포 전부 DateTime.UtcNow).
- piece 필터드 유니크: SQLite=UNIQUE(PId,IsActive) / SQL Server=HasFilter("[is_active] = 1") provider 분기(소스 L361-376).

### VS-P1-3 provider 분기 — PASS
- SQLite로 실제 테스트 구동(EnsureCreated 기반 in-memory SQLite, 44 GREEN). 마이그레이션 양쪽 생성:
  - Migrations/Sqlite/20260616065821_Initial.cs: `UQ_piece_pid_is_active`(일반 UNIQUE), `Sqlite:Autoincrement`, XminRowVersion=INTEGER.
  - Migrations/SqlServer/20260616065853_InitialSqlServer.cs: `UQ_piece_pid_where_active` filter:"[is_active] = 1", RowVersion type:rowversion, UseIdentityColumns.
- piece 유니크/rowversion provider 분기가 마이그레이션 산출물에 실재 확인.

### VS-P1-4 IF-05 트랜잭션 — PASS
- EfOrderRepository.QueryDestination: OK 경로가 `BeginTransaction`(L126) 안에서 reserved_qty+=qty + 기존 활성 piece 비활성(p_id 순환) + piece(RESERVED) 삽입 + piece_event(IF05_RES) → Commit, catch Rollback+throw. 원자.
- AUTO: destination NULL 오더에서 빈 슈트(CHUTE·NORMAL·IsActive·RUNNING 미점유) 할당 + dest_assign_type=AUTO + WAITING→RUNNING 전이 + 예약을 동일 트랜잭션(L132-143). 빈 슈트 없으면 NG·NO_DEST.

### VS-P1-5 IF-05 NG(IF-16) — PASS
- RecordDenied: 미존재/COMPLETED/PAUSED/OVER/비활성목적지 → piece(status=DENIED) + piece_event(IF05_RES,reason) 단일 트랜잭션, **예약 차감 0**(ReservedQty 미변경). 와이어는 200 NG·chuteNo=null(VS-2·VS2_Paused GREEN) — M3 동일.

### VS-P1-6 IF-10 멱등 DB + CONCUR — PASS
- EfDepositRecorder.RecordDeposit: 단일 트랜잭션 내 활성 piece 조회 → 이미 DEPOSITED/CELL_ASSIGNED/LOADED면 Rollback+false(멱등) / DENIED면 Rollback+false / RESERVED·QUERIED·PERMITTED → DEPOSITED 전이 + piece_event(IF10_RES) → Commit+true.
- 동시성: `static readonly object _recordLock` + `lock(_recordLock)`로 프로세스 전역 직렬화(테스트 named in-memory SQLite Mode=Memory;Cache=Shared 단일 writer 정합). 첫 호출만 true → Program.cs는 isNewRecord==true에서만 IF-11 트리거 → **IF-11 ≤1 보장**(논리 입증).
- 실증: 전체 4회 + **CONCUR1 단독 5회 연속 GREEN, flaky 0**. 8병렬 동일 pId 전부 200 OK + HasDepositRecord==true.

### VS-P1-7 셀 배정 DB — PASS
- EfCellSelector.SelectCell: 트랜잭션 내 ①같은 오더(바코드) 활성 assignment(ReleasedAt==null) 재사용 → ②빈 셀(미점유·Enabled) 할당+cell_assignment 삽입 → ③없으면 null. ReleaseCell: ReleasedAt=now. 빈 셀 없으면 Program.cs가 IF-11 트리거 생략(VS6_Chute 대조 GREEN) — M3 동일.

### VS-P1-8 agv.floor 단일진실 — PASS
- EfAgvFloorResolver.Resolve: `_db.Agvs.FirstOrDefault(AgvNo==agvNo && Enabled)?.Floor`. appsettings Floors:AgvNoToFloor 런타임 조회 경로 0(grep — DbSeeder 시드 전용·주석만). 매핑 없으면 null→IF-08 400(VS4_UnknownAgvNo GREEN).

### VS-P1-9 동작 무변경 종합 — PASS
- IF-08 라이브: WcsHold.None 고정(L160) 단일 게이트웨이 Decide. allowed/READY/WRONG_FLOOR·BUSY·TgtFloor 기입(VS-3a/3b/VS-4) M3 동일. 핸드셰이크 C/R 무변경(보호 파일 diff 0).

### Error cases (적극 배제) — 전부 통과
- **E1 동작 변경 0**: git status로 Wcs.Core·PlcGateway.cs·HandshakeOrchestrator.cs·Dtos.cs **무수정**(diff 0). P1=인프라 교체뿐.
- **E2 범위 침범 0**: IF-08 목적지 분기 없음(WcsHold.None), FULL/PAUSED 계산 없음, 멀티소터 레지스트리 없음, timeStamp 백필 없음(client_ts·created_at 컬럼만 생성=허용), S1~S9 없음. appsettings 변경=Database/ConnectionStrings 추가만.
- **E3 교체점**: Program.cs DI에 InMemory*/ConfigAgvFloorResolver 0 — Ef* 4종만 바인딩. Wcs.Api→Wcs.Data ProjectReference 복원 확인. (구 Repositories.cs는 잔존하나 어디서도 인스턴스화 0 = 죽은 코드, "죽은 코드 정리→P2" scope OUT대로 허용.)
- **E4 ERD 위반 0**: 16테이블 정합, 대리키 전부, 이력 UPDATE 경로 없음, enum string 변환 누락 0.
- **E5 트랜잭션 누락 0**: IF-05 OK/NG·IF-10·셀 배정/해제 전부 BeginTransaction~Commit/Rollback 원자. CONCUR1 동시성 회귀 테스트 실재·5회 GREEN.
- **E6 하드코딩 0**: provider·연결문자열 appsettings(Program.cs L57-59), 시간값 Timing 섹션. (design-time factory의 localdb 문자열은 마이그레이션 생성 전용·런타임 비경로 — 허용.)
- **E7 자동 Migrate 0**: Program.cs에 .Migrate()/.MigrateAsync() 없음(주석만). 테스트는 EnsureCreated. M5 이연 준수.

### MINOR (비차단 — P2/후속 권고)
1. **마이그레이션 스냅샷 위생**: ModelSnapshot 파일이 단 1개(`Migrations/Sqlite/WcsDbContextModelSnapshot.cs`)인데 내용은 **SQL Server**(UseIdentityColumns + filtered index `UQ_piece_pid_where_active`). 두 provider가 한 스냅샷을 공유해 SQL Server 생성분으로 덮인 상태. 현재 영향 없음(커밋된 두 Initial 마이그레이션은 각각 정상, 테스트는 EnsureCreated로 마이그레이션 우회). 그러나 향후 SQLite 증분 마이그레이션은 잘못된 스냅샷과 diff → 손상 위험. provider별 스냅샷 분리 권장.
2. **컬럼 이중화**: row_version 분기에서 SQLite도 RowVersion(BLOB) + XminRowVersion(INT) 둘 다, SQL Server도 둘 다 물리 컬럼 생성("한 쪽만 매핑"과 미세 불일치). 미사용 잉여 컬럼이라 기능 무해. 한 쪽 Ignore 권장.
3. **static lock 운영 함의**: `_recordLock`이 프로세스 전역이라 운영 SQL Server에서도 모든 IF-10 투입 기록이 단일 모니터로 직렬화 → 처리량 병목. 정합성/테스트 결정성엔 무해하나(소스 주석도 인지), 운영 경로는 DB 유니크 제약/트랜잭션 격리로 대체 권장(P2).

→ Completion Conditions(회귀 0·16엔티티·provider 분기·교체점·트랜잭션·무변경·agv.floor·하드코딩 0·feature 브랜치) 전부 충족. **FULL PASS.** MINOR 3건은 P2 정리 대상.

---

# Sprint Feedback — S-RTU (Modbus 전송 추상화 + RTU 어댑터)

## 판정: APPROVED

GROUND TRUTH 재검증(소스 직접 검사 + dotnet 직접 재실행, 요약 불신). 전 시나리오 PASS.

### VT-1 TCP 회귀 (필수) — PASS
- expected: 기존 M1 15 + M2 통합 9(IT-1·2a·2b·3a·3b·3c·4·4b·5)이 단언·코드 변경 없이 GREEN, split 감소 없음.
- actual: `dotnet test Wcs.sln` **4회 연속 28/28 GREEN, 실패 0, ~2s**(flaky/데드락 없음).
  `--list-tests` 카운트 = Decider 15(Row1~7 Theory 전개 + C1·C2×3·C3 + Wire) + 통합 9 + RTU 4 = 28.
  M2 IT 9건 전부 메서드명·split 유지(IT3c·IT4b 포함). `PlcGatewayIntegrationTests.cs`는 `git diff` 무변경 —
  2인수 편의 생성자 `new PlcPollingService(_gwOpt, _queue)`(→내부 ModbusTcpMaster)로 TCP 경로 회귀 0.

### VT-2 RTU 라이브 왕복 — PASS
- expected: in-memory fake `IModbusRtuSerialPort` 쌍으로 실제 ModbusRtuClient↔ModbusRtuServer 왕복, C/R 성공·R_Seq==C_Seq·RMW 비트 보존·단일 큐.
- actual: `VT2_RtuFakeSerial_LiveRoundtrip` 245ms 실 왕복. 콘솔 증거: `RTU GW Online=True` /
  `C_Flag=1 CCellNo=5 CSeq=1`(CellAssign FC16+RMW 왕복) / `R_Flag=1 RSeq=1==CSeq=1`(대사) / ClearR 후 R_Flag=0.
  RMW 보존: `Assert.True(snapC.Ready)` — C_Flag set 후 Ready 비트 보존. 빈 단언 아님(소스 L88-116 확인).
  단일 큐: 모든 쓰기 `gw.EnqueueAsync` 경유, PlcGateway RunWriteConsumer 단일 컨슈머.

### VT-3 전송 선택 팩토리 — PASS
- expected: Tcp→TcpMaster, Rtu→RtuMaster, 미지정→Rtu(기본), 시리얼 파라미터 전달.
- actual: `Assert.IsType<ModbusTcpMaster>`(Tcp) / `<ModbusRtuMaster>`(Rtu) / `new PlcTransportOptions()` 기본→RtuMaster /
  bad value→`InvalidOperationException`. 4분기 전부 통과. 시리얼 파라미터는 팩토리 `CreateRtu`가 PortName·Baud·
  Parity·Stop·timeouts·UnitId 전부 전달(ModbusMasterFactory.cs L94-103)→생성자가 client에 세팅(ModbusRtuMaster.cs L45-52),
  빌드+VT-2 실통신(UnitId=1)으로 구조 검증. 기본값=Rtu 정합 확인(E4).

### VT-4 추상화 단위 테스트 — PASS
- FakeModbusMaster 주입으로 PlcGateway 로직 전송 무관 검증: CellAssign→C_Flag=1·CCellNo=7·CSeq=42·Ready 보존(RMW),
  R 세팅→RSeq=42, ClearR→R_Flag=0. 실 단언.

### VT-5 RTU OFFLINE 전이 — PASS
- FakeSerialPort `SimulateClose=true`→ReadAsync/WriteAsync에서 IOException→연속 실패→Online=false, 복구→true.
  콘솔: 초기 Online→OFFLINE→복구 Online. 예외 안 삼킴.

### Error cases 배제
- **E1 추상화 누수 0**: `grep "ModbusTcpClient|ModbusRtuClient" src/Wcs.PlcGateway` → 어댑터(TcpMaster·RtuMaster)와
  IModbusMaster 주석에만 등장. PlcGateway.cs·HandshakeOrchestrator.cs 구상 타입 직접 참조 0건.
- **E2 직렬화 회귀 0**: PlcGateway 모든 Modbus 트랜잭션(폴 읽기 L208-222 / 쓰기·RMW L309-362 / Disconnect·재연결
  L257-259)이 `_clientLock` 임계구역 통과. off-lock `_master` 접근은 StopAsync·DisposeAsync(태스크 await 완료 후
  단일스레드) 뿐. 데드락 없음(finally Release, RMW 재획득 없음). 4회 연속 GREEN로 결정성 입증.
- **E3/E4 회귀**: M2 IT 9건 GREEN, TCP 어댑터가 BigEndian·ReadTimeout·재연결 의미 보존. 미지정=Rtu, 기존 TCP는 명시 Tcp 구성으로 회귀 0.
- **E5 하드코딩/범위**: 시리얼/시간 매직넘버 0(전부 PlcTransportOptions·PlcGatewayOptions 설정). `git diff` 코드 변경 =
  PlcGateway.cs·appsettings.json 뿐. Wcs.Core·Wcs.Api(*.cs)·Wcs.Data·HandshakeOrchestrator·DepositDeciderTests·
  M2 IT 파일 무변경. appsettings는 키 추가만(WriteTimeoutMs 값·존재 유지).
- **E6 문서 동기화**: SPEC §7-A 신설(RTU 우선+TCP·추상화 완료·소터별 독립 포트·마스터/슬레이브·RTU 예외 OFFLINE·
  fake serial CI), 舊 §7-A→§7-B 이동. CLAUDE.md 다이어그램 `Modbus TCP`→`Modbus RTU/TCP` 정정. 코드와 함께 커밋 가능 상태.
- **E7 RTU 예외→OFFLINE**: PlcGateway L243-247 isHardEx에 IOException·TimeoutException + InnerException 포함(소켓 전용 분기 비의존). VT-5로 실증.

→ build exit 0(경고 0/오류 0), test 4회 28/28 GREEN, 추상화 경계·회귀 안전·RTU 정합·장인성/설정 4기준 충족. FULL PASS.

---

# Sprint Feedback — S-M2 (PLC 게이트웨이 + 시뮬레이터 핸드셰이크)

## 판정 (재검증 #4 — 코드리뷰 수정 반영): APPROVED (유지)

코드리뷰 후속 수정 ground truth 재검증. 핵심: 락 밖 `_client` 접근(off-lock Disconnect) 제거 확인 + 회귀/데드락 없음.

### 재검증 #4 증거
- **build**: exit 0, 경고 0 / 오류 0.
- **test**: `dotnet test Wcs.sln` → **5회 연속 24/24 GREEN, 실패 0**(IT-4b 서버 단절·재기동 타이밍 flaky/데드락 배제).
  split: Decider 15(M1 회귀 0) + 통합 9(IT-4b 신규). 총 24.
- **off-lock 접근 해소**: 폴 catch의 `TryReconnect()`(=`_client.Disconnect()`)를 `_clientLock` 임계구역으로 이동
  (PlcGateway.cs L233-235 `WaitAsync`→`try TryReconnect()`→`finally Release`). 전 `_client.` 11개소 감사 결과
  모두 락 보호 또는 종료 후 단일스레드 경로(StopAsync L163·DisposeAsync L169 — 두 태스크 await 완료 후). 락 밖 접근 0.
  데드락 없음(finally Release 보장, 재연결은 보유 중 재획득 안 함). catch 내 WaitAsync는 ct 취소 시 OCE로 루프 종료 — 정상.
- **죽은 코드 제거**: `_writeCompletionTcs`/`_tcsDoor`/`WaitNextWriteCompletionAsync` 및 컨슈머 finally 삭제(grep 0건).
- **주석 정정**: 클래스 "BackgroundService→수동 StartAsync/StopAsync(M3 IHostedService)" / SimServer `InjectNoResponse`
  "OFFLINE 유발→RFlagTimeout 유발, 폴 응답 계속→Online 유지"(실제 동작과 일치하도록 정정 — 적절).
- **IT-4b 회귀 가드**: `IT4b_WritesDuringReconnect_NoCorruption` — 핸드셰이크 진행 중 서버 단절·재기동 후
  후속 핸드셰이크 Success + R_Seq==C_Seq 단언(첫 건은 Success/Offline/Timeout 허용 — 타이밍 비결정 합리적). 빈 단언 아님.
- 범위 클린: Core·Api.cs·Data·DepositDeciderTests 무변경.

→ 이전 APPROVED 유지 + 코드리뷰 결함 0 확인.

---

## 판정 (재검증 #3): APPROVED

3회차 재제출에서 FAIL-1·FAIL-2 모두 해소 확인. Evaluator ground truth 재검증(요약 불신, 명령 직접 재실행 + 소스 검사).

### 최종 증거
- **build**: `dotnet build Wcs.sln` → exit 0, 경고 0 / 오류 0.
- **test**: `dotnet test Wcs.sln` → **4회 연속 23/23 GREEN, 실패 0**(동시성·락 변경의 데드락/flaky 배제).
  split: `--filter Decider` 15(M1 회귀 0) + `~PlcGatewayIntegrationTests` 8(IT-3c 신규 추가). 총 23.
- **FAIL-1 해소**: `SimServer.StartAsync`가 동기(`return Task.CompletedTask`), `Task.Delay(80)` 제거.
  src 전체 하드코딩 numeric `Task.Delay` 0건(grep). GW `WaitUntilAsync(Online)` 폴링이 기동 대기 흡수.
- **FAIL-2 해소**: `PlcPollingService._clientLock = SemaphoreSlim(1,1)`(L107) 도입.
  - 폴 읽기: `WaitAsync`(L190)→`EnsureConnected`+`ReadHoldingRegistersUInt16Async`→`finally Release`(L202).
  - 쓰기 컨슈머: `WaitAsync`(L307)→`EnsureConnected`+`switch`(전 Write*)→`finally Release`(L359).
    early `return`(SetTgtFloor/CellAssign skip)도 finally로 Release 보장 — 락 누수 없음.
  - `RmwD4LockedAsync`(개명): read+write가 컨슈머 임계구역 안에서 원자 실행, 재획득 없음 → 데드락 없음.
  - `DisposeAsync`에서 `_clientLock.Dispose()`(L173). 폴 예외 시 finally Release 후 OFFLINE 전이 — E7 보존.
- **IT-3c 회귀 가드**: `IT3c_ConcurrentPollAndWrite_NoFrameCorruption` — 폴 진행 중 3건 연속 핸드셰이크
  각 Success + R_Seq==C_Seq 단언(빈 단언 아님). poll-vs-write 프레임 무결성 동작 입증.

### Error case 재확인 (전부 배제 유지)
E1 단일 컨슈머 쓰기 / E2 RMW `(current|set)&~clear` 비트 보존 / E3 §6 분류·이동 직렬·Ready 블립 없음 /
E4 SetTgtFloor TgtFloor≠0 스킵·WCS TgtFloor 클리어 없음 / E5 Core·Api.cs·Data·DepositDeciderTests 무변경·하드코딩 시간값 0 /
E6 P1/P2 보수(알람+상태재확인까지만, 재시도/리셋 미구현) / E7 OFFLINE 명시 전이·예외 비삼킴. 모두 PASS.

### 잔여 minor (차단 아님)
- `src/Wcs.Sim3ds/Program.cs` 옵션 리터럴이 appsettings 미바인딩 — 독립 실행 entrypoint(테스트 표면 밖).
  M3 배선 시 IConfiguration 바인딩으로 정리 권장.

→ Completion Conditions 전부 충족. **APPROVED.**

---

## (재검증 #2 기록 — 보존)

## 판정 (재검증 #2): FAIL — FAIL-1 해소, FAIL-2 미해소

재제출 검증(ground truth): build exit 0(경고/오류 0), `dotnet test Wcs.sln` **3회 연속 22/22 GREEN**(flaky 아님).
- **FAIL-1 (매직넘버 고정 sleep) — 해소됨.** `SimServer.cs:95-96`에서 `Task.Delay(80)` 제거,
  GW `WaitUntilAsync(Online)` 폴링이 흡수. 회귀 없음(3회 GREEN로 입증).
- **FAIL-2 (단일 소켓 read/write 경합) — 미해소(필수 차단 유지).**
  `PlcGateway.cs` 재검사: `_client` 직렬화 프리미티브(`SemaphoreSlim`/`Mutex`/`lock`/`_ioGate`) 전무
  (`lock`은 `_tcsDoor` 2곳뿐 — 테스트 TCS용, 소켓 무관). L145-146 `_pollTask`/`_writeTask` 두 태스크가
  여전히 같은 `_client`를 동시 사용: 폴 읽기 L224 ↔ 쓰기 L301/315/326 ↔ RMW read+write L346/355. 결함 그대로.
  재제출 메시지에도 FAIL-2 언급 없음. 아래 FAIL-2 수정 지침대로 처리 후 재제출 요망.

---

## (1차 검증 기록 — 보존)

Evaluator가 GROUND TRUTH로 검증함 — Generator 요약 불신, 모든 명령 직접 재실행 + 소스 검사.
**핵심 메커니즘·절대규칙·통합 시나리오는 모두 통과**했으나, 하드코딩 고정 sleep 1건(매직넘버)과
동시 트랜잭션 안전성 결함 1건으로 FAIL. 둘 다 국소적·실행가능한 수정.

---

## Verification Scenarios (PASS)

### V1 (build) — PASS
`cd "<proj>" && dotnet build Wcs.sln` (Bash 경유):
```
빌드했습니다.
    경고 0개
    오류 0개
경과 시간: 00:00:04.54
BUILD_EXIT=0
```
exit 0, 경고 0 / 오류 0. PASS.

### V2 (full test GREEN, flaky 아님) — PASS
`dotnet test Wcs.sln --no-build` **3회 연속 실행** 전부 동일:
```
RUN 1: 통과!  - 실패: 0, 통과: 22, 전체: 22, 기간: 2 s
RUN 2: 통과!  - 실패: 0, 통과: 22, 전체: 22, 기간: 2 s
RUN 3: 통과!  - 실패: 0, 통과: 22, 전체: 22, 기간: 2 s
```
구성 검증(필터):
- `--filter Decider` → 15 통과 (M1 회귀 0)
- `--filter ~PlcGatewayIntegrationTests` → 7 통과 (IT1·IT2a·IT2b·IT3a·IT3b·IT4·IT5)
3회 모두 GREEN, 시간 일정 → 현재 규모에서 flaky 아님. PASS.

### V3 (시나리오 충실) — PASS
소스 검사(PlcGatewayIntegrationTests.cs) — 빈 단언/이름만 통과 아님 확인:
- IT-1: 결과=Success 단언 + SentCSeq==ReceivedRSeq + WaitUntil로 종료 시 C_Flag·R_Flag=0 + 타임라인 NotEmpty. 실제 왕복.
- IT-2a 일치=Success / IT-2b `InjectRSeqOverride=999`→RSeqMismatch·ReceivedRSeq==999·Detail "mismatch". 실제 고장주입.
- IT-3a `InjectRSeqOverride` 없이 CellAssign→R_Flag=1 시 Ready=1 단언으로 RMW 비트 보존 입증. IT-3b SetTgtFloor(2) 후 SetTgtFloor(99) 스킵→TgtFloor==2 유지(PollForDuration 후).
- IT-4 서버 Stop→`!Online` 대기(timeout=WriteTimeoutMs*(N+1)+여유, 설정 유도값)→재기동→Online 복구.
- IT-5 RFlagTimeoutMs=300 + InjectRFlagDelayMs=5000→RFlagTimeout·Detail "RFLAG_TIMEOUT". P1까지만 단언(재시도 단언 없음 — 적절).
PASS.

---

## Error cases (배제 결과)

- **E1 절대규칙 #1 (단일 큐)** — PASS(배제). 솔루션 전체 Modbus 쓰기 호출부 4건 전부 PlcGateway.cs 내부:
  L301/L315/L326(ProcessWriteAsync) + L355(RmwD4Async). ProcessWriteAsync는 RunWriteConsumerAsync에서만,
  RmwD4Async는 ProcessWriteAsync에서만 호출. Channel `SingleReader=true`, 단일 리더는 RunWriteConsumerAsync뿐.
  HandshakeOrchestrator는 EnqueueAsync만 호출(L101·L185·L193). 단일 쓰기 컨슈머 구조적 강제됨.
- **E2 RMW** — PASS(배제). `RmwD4Async`: `(ushort)((current | set) & ~clear)` (PlcGateway.cs L352).
  RegisterMap D4 비트 C_Flag=1<<0·R_Flag=1<<1·Ready=1<<2 확인. set/clear 외 비트 보존. IT-3a가 Ready 보존 동작 입증.
- **E3 §6 정정(분류·이동 직렬, Ready 블립 금지)** — PASS(배제). SimServer `_isSorting`/`_isMoving` 플래그로 직렬화.
  C_Flag 감지 조건 `cFlag && !sorting && !moving`(L159). 분류 종료 시 복귀 있으면 Ready=0 유지한 채 이동(L255-257),
  없으면 Ready=1 1회 set(L264) — 중간 1→0→1 블립 없음.
- **E4 핑퐁/클리어** — PASS(배제). SetTgtFloor는 `_latest.TgtFloor != 0`이면 return(L296-300). WCS 코드에 TgtFloor=0 쓰기 없음.
  TgtFloor=0 클리어는 Sim의 분류 시작 시점에서만(SimServer L209).
- **E5 하드코딩/범위(범위)** — PASS(배제). `git diff` 코드 surface: Wcs.PlcGateway/*, Wcs.Sim3ds/*, appsettings.json(키 추가만),
  Wcs.Tests/*. Wcs.Core / Wcs.Api/*.cs / Wcs.Data **무변경**(`git diff --stat HEAD` 확인). DepositDeciderTests.cs 무변경.
  appsettings에 CFlagTimeoutMs·Sim3ds.* 키 추가만. → 범위는 PASS. (단, 하드코딩 시간값은 아래 FAIL 참조)
- **E6 §7-A 과구현** — PASS(배제). P1(RFlagTimeout): 알람 로그 + Online·Ready 재확인 + 결과 반환까지만(HandshakeOrchestrator L201-211,
  재시도/포기 미구현). P2(CFlagTimeout): 알람 + 상태 재확인까지만(L129-137). 추측 동작 없음.
- **E7 OFFLINE 예외삼킴** — PASS(배제). 폴 실패 시 LogWarning/LogError 후 `PublishOffline()`로 Online=false 명시 전이(L211-216).
  소켓 예외 즉시 OFFLINE. 쓰기 컨슈머 예외도 LogError + PublishOffline(L270-274). 조용한 무시 없음.

---

## FAIL 항목 (수정 필요 — 둘 다 국소)

### FAIL-1 (E5 + NOTE "매직넘버 시간값 통과해도 FAIL") — 하드코딩 고정 sleep
- **위치**: `src/Wcs.Sim3ds/SimServer.cs:97`
  ```csharp
  // 서버가 포트를 수신 준비할 때까지 짧게 대기
  await Task.Delay(80, outerCt).ConfigureAwait(false);
  ```
- **Expected**: 절대규칙 #7 "모든 시간값 appsettings — 하드코딩 금지" + 계약 장인성 기준 #3
  "고정 sleep 대신 폴링/대기 — flaky 회피". 시간값은 설정 주입.
- **Actual**: `80`이 소스에 직접 박힌 매직넘버 고정 sleep. 서버 수신 준비 대기를 임의 80ms로 가정.
  CI/부하 환경에서 80ms 안에 리스닝 안 되면 클라이언트 첫 연결 실패 → 잠재 flaky(현재 통과는 운).
- **수정 지침**:
  - 최소: `SimServer.Options`에 키 추가(예 `StartupSettleMs`, 기본 80) — 매직넘버 제거. appsettings `Sim3ds`에도 대응 키.
  - 권장: 고정 sleep 제거하고 `_server.Start(ep)` 후 실제 수신 가능 여부를 짧은 폴링/재시도로 확인.
    (GW측은 이미 `WaitUntilAsync(() => _gw.Latest.Online)`로 폴링 대기 중이므로, Sim의 고정 sleep은
     테스트에 사실상 불필요 — StartAsync에서 제거해도 GW Online 폴링이 흡수 가능한지 확인 후 제거 권장.)

### FAIL-2 (장인성 ★★ / 아키텍처 ★★★ — 동시 트랜잭션 안전성) — 단일 소켓 read/write 경합
- **위치**: `src/Wcs.PlcGateway/PlcGateway.cs:145-146`
  ```csharp
  _pollTask  = Task.Run(() => RunPollLoopAsync(_cts.Token));      // _client 읽기 (L224)
  _writeTask = Task.Run(() => RunWriteConsumerAsync(_cts.Token)); // _client 쓰기 (L301/315/326) + RMW read+write (L346/355)
  ```
- **Expected**: 절대규칙 주석 "동시 쓰기는 경합을 일으킨다" 의 취지 — 단일 `ModbusTcpClient`(단일 TCP 소켓·
  단일 트랜잭션 버퍼)에서 트랜잭션이 직렬화되어야 프레임 손상/응답 교차가 없다. FluentModbus `ModbusTcpClient`는
  **단일 트랜잭션 클라이언트로 동시 호출에 thread-safe 하지 않음**.
- **Actual**: 폴 루프(읽기)와 쓰기 컨슈머(읽기+쓰기 RMW 포함)가 **같은 `_client`를 동시에** 사용하며
  둘 사이 직렬화(lock/SemaphoreSlim) 없음. 폴 주기(30~150ms)와 쓰기/RMW가 겹치는 순간 같은 소켓에서
  요청/응답 프레임이 교차할 수 있음(트랜잭션 ID 혼선·버퍼 경합). 현재 통과는 로컬 sim·짧은 윈도우로 인한 운이며,
  부하/지연 시 간헐 예외 또는 잘못된 스냅샷 → 이후 OFFLINE 오판/RMW 손상으로 번질 수 있음.
  ※ 절대규칙 #1(쓰기 단일 큐)·E2(RMW 비트 보존)는 *쓰기끼리는* 단일 컨슈머라 직렬이지만,
    **읽기(폴)와 쓰기가 별 태스크라 소켓 레벨에서 직렬이 아님** — 이 부분이 결함.
- **수정 지침** (택1):
  - (A) 폴 읽기와 쓰기 컨슈머가 공유하는 `SemaphoreSlim _ioGate = new(1,1)`로 모든 `_client` 트랜잭션
    (ReadHoldingRegisters / Write* / RMW read+write 묶음)을 감싸 직렬화. RMW는 read+write 한 쌍을 한 임계구역으로.
  - (B) 또는 폴링도 쓰기 큐 컨슈머와 동일 단일 루프/단일 태스크로 통합해 소켓 접근점을 하나로.
    (단일 컨슈머가 폴+쓰기를 번갈아 수행) — 절대규칙 #1 정신과 더 일치.
  - 회귀 방지: 동시 부하(폴 진행 중 다수 CellAssign/ClearR 연속 투입) 하에서도 스냅샷·RMW 무결성 유지되는
    통합 테스트 1건 추가 권장(IT-3 확장).

---

## Completion Conditions 대비
- build exit 0 / test 22 GREEN(3회) — 충족
- 통합 시나리오 IT-1~5 자동화 GREEN — 충족
- 절대규칙(단일 큐 쓰기·RMW·TgtFloor≠0 스킵·WCS 클리어 안 함) — 충족
- **하드코딩 시간값 0 — 미충족(FAIL-1: SimServer.cs:97 `Task.Delay(80)`)**
- Wcs.Core·Wcs.Api·Wcs.Data 무변경 — 충족
- 레지스터 타임라인 로그 출력 — 충족(SimServer.LogTimeline, 테스트 DisposeAsync에서 출력)

→ FAIL-1(필수: 매직넘버 제거) + FAIL-2(소켓 트랜잭션 직렬화) 수정 후 재제출.

## 참고 (minor, 차단 아님 — 여력 시)
- `src/Wcs.Sim3ds/Program.cs:13-16` 옵션값(TiltDelay/SortDuration/MoveDuration)이 appsettings 바인딩 없이
  소스 리터럴. 독립 실행 entrypoint라 테스트 표면 밖이나, "설정 주입" 일관성 위해 IConfiguration 바인딩 권장.

---

## Code Review (4-Tier Step 4.5) — APPROVE, MINOR 4건 (비차단 — 다음 스프린트/M3에서 정리)
독립 Opus 코드리뷰: BLOCKING/MAJOR 0, M2 동시성 invariant 보존 확인. 아래 MINOR는 머지 차단 아님:
1. `ModbusRtuMaster.Connect/Disconnect` fake-serial 모드 no-op이 TCP 계약과 분기 — 명명(`_externallyOwnedPort`)·XML 주석으로 의도 명시 권장.
2. `tests/Wcs.Tests/RtuTransportTests.cs:98` `await Task.Delay(50)` 고정 sleep이 "고정 sleep 없음" 주석과 모순 — 선행 `WaitUntilAsync(CFlag)`가 이미 동기화하므로 삭제 가능(무해한 dead weight).
3. `FakeSerialPort.Read`(sync) 버퍼 없을 때 0 반환 → 동기 경로 사용 시 busy-spin 위험. 현재 async만 사용되나 `NotSupportedException`으로 fail-loud 또는 문서화 권장.
4. `ModbusRtuMaster.Connect`가 물리 COM 모드에서 BigEndian 하드코딩 — fake ctor는 endianness 파라미터 받음. VEICHI는 BigEndian이라 기능상 정상이나, 엔디안을 필드로 통일 권장(향후 LittleEndian 장비 대비).

---

# Sprint Feedback — M3 (API 3종 IF-05/08/10 + S-RTU MINOR 4건)

## 판정: APPROVED

GROUND TRUTH 재검증(소스 직접 검사 + dotnet 직접 재실행, 요약 불신). 전 시나리오 PASS. 빌드 경고 0/오류 0.

### VS-7 회귀 (필수) — PASS
- expected: 기존 28(Decider 15 + PlcGateway 9 + RTU 4)이 단언·코드 변경 없이 GREEN, split 감소 없음.
- actual: `dotnet test Wcs.sln` **3회 연속 41/41 GREEN, 실패 0, ~2s**. 카운트 분해 직접 확인 —
  Decider 15 / PlcGatewayIntegration 9 / RtuTransport 4 = 28 회귀 0, + ApiIntegration(신규) 13 = 41.
- 비결정 요소 배제: 타이밍 민감 ApiIntegration(VS-3 WrongFloor 큐 관찰, VS-6 C_Flag 핸드셰이크 관찰)을
  **추가 3회 연속 13/13 GREEN**로 재확인(총 6회). flaky 없음.
- `git status`: src/Wcs.Core, src/Wcs.PlcGateway/PlcGateway.cs, HandshakeOrchestrator.cs 전부 **변경 없음**(무수정 확인).

### VS-1/2 IF-05 — PASS
- happy(VS1): 시드 TEST-BARCODE-1 매칭 → 200 OK·chuteNo=1·reason=NORMAL. 예약차감(`order.ReservedQty += qty`,
  Repositories.cs:219)·투입기록(`recorder.RecordDestinationQuery`, Program.cs:135) 소스 실재 확인.
- error(VS2): 미존재 바코드 → 200 NG·chuteNo=null·NO_DEST(Repositories.cs:186). PAUSED 시드 → NG·PAUSED(:194).
  NG여도 기록 — `recorder.RecordDestinationQuery`가 검증 후 OK/NG 무관 호출(Program.cs:132~137). pId=0 → 400(:123).
- 필드누락(barcode 공백) → 400(Program.cs:125).

### VS-3 IF-08 라이브 — PASS (핵심)
- WrongFloor(VS3b): agvNo=2→agvFloor=2(설정 Floors:AgvNoToFloor "2":2), CurFloor=1 불일치 → allowed=false·WRONG_FLOOR,
  TgtFloor=0이라 WriteTgtFloor=true → 큐 SetTgtFloor(2) fire-and-forget(Program.cs:183-194) → FakeMaster.TgtFloor=2 폴링 관찰.
- 층일치(VS3a): agvNo=1→floor1=CurFloor1, Ready=1 → allowed=true·**reason="READY"**.
- READY 주입 검증: `decision.Allowed ? "READY" : decision.Reason.ToWire()`(Program.cs:199) — API 계층 주입.
  Core `Models.cs:58 DenyReason.None => null` 무변경(`git status` Core 무수정) — Core ToWire(None)=null 유지 확인.

### VS-4 IF-08 분기 — PASS
- Ready=0 → allowed=false·BUSY(Decider 행4/5, DepositDecider.cs:45). OFFLINE 스냅샷 → OFFLINE(Decider 우선순위1).
- pId=-1 → 400(:161). agvNo=99(매핑없음) → 400(floorResolver.Resolve→null, Program.cs:168). 검증실패만 400.
- WriteTgtFloor 분기: Allow 경로(VS3a happy)는 WriteTgtFloor=false → 큐 투입 없음(Decider Allow()=false, Models.cs:71).
  큐 투입은 decision.WriteTgtFloor일 때만(Program.cs:183) — fire-and-forget, API 응답 완료 대기 X 확인.

### VS-5 IF-10 happy+멱등 — PASS
- 슈트 보고 → 200 OK. 같은 pId 재보고 → `HasDepositRecord`(Program.cs:229) true → 즉시 OK·상태무변경(멱등).
  `RecordDeposit`이 IsReported 플래그로 중복 무해 처리(Repositories.cs:270-278).

### VS-6 IF-10→IF-11 트리거 — PASS (핵심)
- 3D 목적지(TEST-BARCODE-3, Sorter3D): IF-05에서 DestType 저장(Program.cs:140) → IF-10 보고 시 GetDestType==Sorter3D →
  CellSelector.SelectCell → HandshakeOrchestrator.ExecuteAsync 백그라운드 트리거(Program.cs:243-266) → C_Flag=1 폴링 관찰.
- 슈트 목적지(TEST-BARCODE-2, Chute): 트리거 분기 미진입 → C_Flag 변동 없음(대조 확인).
- IF-10 즉시 OK: 핸드셰이크는 `_ = handshake.ExecuteAsync(...)` fire-and-forget, 응답은 즉시 Results.Ok(:280) — 완료 대기 X.

### Error cases (적극 배제) — 전부 통과
- E1 M4 경계: `grep "Wcs.Data|DbContext|EntityFramework|UseSqlServer|UseSqlite"` src/Wcs.Api → **주석 3건만, using/ProjectReference/인스턴스화 0**.
  오더·목적지·예약·셀·agvFloor 전부 인터페이스+인메모리(Repositories.cs). Wcs.Api.csproj에서 Wcs.Data ProjectReference 제거 확인.
- E2 Core 변경: `git status` src/Wcs.Core **무수정**. READY는 API 계층 주입(Program.cs:199), Core 판정/ToWire 불변.
- E3 DTO 정합: 원본 HTML(wcs_rcs_interface_kr.html) 대조 — IF-05 agvNo 있음(:119,145), IF-08/10 timeStamp·qty nullable,
  NG chuteNo null(:155), allowed=true reason="READY"(:171). DTO(Dtos.cs) 전 필드 일치. JSON camelCase(STJ 기본)로 와이어 정합.
- E4 전송/핸드셰이크 무변경: PlcGateway.cs·HandshakeOrchestrator.cs `git status` 무수정. IHostedService 결선은
  PlcPollingHostedAdapter(Program.cs:296) 신규 어댑터로 — PlcPollingService.StartAsync/StopAsync 수동 경로 보존(M2 IT 9건 회귀 0).
- E5 MINOR 4건: `git diff` 실재 확인 — (1)ModbusRtuMaster `_externallyOwnedPort` 명명+XML주석 (2)RtuTransportTests VT-2
  Task.Delay(50) 제거+주석 (3)FakeSerialPort sync Read→NotSupportedException fail-loud+문서 (4)ModbusRtuMaster `_endianness`
  필드 통일(기본 BigEndian=구동작 동일). 4건 모두 동작 변경 0.
- E6 하드코딩/스레드안전: appsettings.json에 시간(Timing)·포트(Plc)·매핑(Floors:AgvNoToFloor "1":1,"2":2) 전부 설정.
  소스 리터럴 시간값 0. 인메모리 상태 thread-safe(InMemoryOrderRepository/CellSelector=lock, DepositRecorder=ConcurrentDictionary).
  예외 안 삼킴(fire-and-forget는 ContinueWith로 IsFaulted 로깅).
- E7 응답 계약: IF-05 가부=result, IF-08 가부=allowed (HTTP 200). 검증실패만 400. allowed=true→reason="READY".

## 비차단 관찰 (M4 권고, 차단 아님)
- IF-08의 WcsHold가 Program.cs:179에서 `WcsHold.None` 고정 — 계약 §70(FULL=M4, PAUSED 기준정보만)대로 의도된 M3 범위.
  IF-05는 IsPaused 시드로 PAUSED 반환하나 IF-08은 항상 None 적용. M4에서 IOrderRepository/3DS 점유 기반 hold 산출 결선 필요.
- `IOrderRepository.GetDestType`(Repositories.cs:228)이 항상 null 반환 — 실 DestType은 DepositRecorder.GetDestType로 우회.
  인터페이스에 죽은 메서드가 남음(주석으로 명시됨). M4 EF 교체 시 정리 권장.

---

# Sprint Feedback — M3 코드리뷰 픽스 재검증 (IF-10 멱등 원자성 + IF-05 qty 가드)

## 판정: APPROVED (재확인)

GROUND TRUTH 재검증(소스 직접 검사 + dotnet 직접 재실행). 빌드 경고 0/오류 0. 전 시나리오 PASS.

### MAJOR — IF-10 멱등 원자성 — PASS
- expected: check-then-act 경쟁 제거. 같은 새 pId로 IF-10 동시 다수 → 기록 1건·IF-11 트리거 ≤1회·전부 200 OK.
- actual(소스): `InMemoryDepositRecorder.RecordDeposit`(Repositories.cs:272-318)이 TryGetValue→IsReported RMW→TryAdd→
  rare-path 재확인 전 구간을 `lock (_lock)`로 원자화. IF-10 핸들러(Program.cs:235)는 `HasDepositRecord` 선검사를
  **제거**하고 `RecordDeposit` 반환값(`isNewRecord`)만으로 IF-11 트리거 결정 — 선검사~기록 사이 삽입 경쟁 창 소멸.
- actual(테스트): CONCUR1(8건 병렬 IF-10, Barrier 동기화)이 전부 200 OK + HasDepositRecord==true. 단일 스레드만
  `true` 반환 → IF-11 블록 1회 진입 보장(RecordDeposit lock+TryAdd 구조). **CONCUR1 단독 5회 연속 GREEN — flaky 0.**
- 비차단 관찰: CONCUR1은 "기록 ≥1·전부 OK"까지만 단언(정확히 1건 카운트·트리거 1회 직접 단언 아님). 단일-트리거
  보장은 RecordDeposit 반환 계약에 있어 회귀 가드로 충분. RecordDestinationQuery(IF-05)가 lock 밖 `_records[pId]=`
  write라 IF-05⇄IF-10 진성 동시 시 미세 창이 남으나, 정상 흐름(IF-05 선행)+rare-path 재확인으로 완화됨. M4 EF 트랜잭션 시 정리.

### MINOR — IF-05 qty<=0 가드 — PASS
- expected: qty<=0 → 400 즉시(예약 차감 음수로 ReservedQty 손상 방지).
- actual: Program.cs:128 `if (req.Qty <= 0) return Results.BadRequest(...)` — 오더 매칭·예약차감 전 fail-loud.
  MINOR1_ZeroQty_400(qty=0)·MINOR1_NegativeQty_400(qty=-5) 둘 다 400 단언 GREEN.

### 회귀/flaky — PASS
- `dotnet test Wcs.sln` **3회 연속 44/44 GREEN, 실패 0**. 카운트 분해: Decider 15 / PlcGatewayIntegration 9 /
  RtuTransport 4 = 기존 28 회귀 0, + ApiIntegration 16(기존 13 + CONCUR1·MINOR1×2 = 3 신규) = 44.
- 변경 범위 한정: `git status` — 픽스로 변경된 소스는 Program.cs·Repositories.cs·ApiIntegrationTests.cs뿐.
  src/Wcs.Core·PlcGateway.cs·HandshakeOrchestrator.cs **무수정 유지**(E2/E4 회귀 0). DTO·전송·핸드셰이크 무변경.
- 진성 동시성 테스트(CONCUR1) 단독 5회 + 전체 3회 = 비결정 요소 안정성 확인.
