# Sprint Contract — S-M4-P2a (도메인 분기 + FULL/PAUSED + timeStamp + 멱등 DB 백스톱 + 이관 정리)
> M4 (phase 2a / 3). 사용자 확정 2026-06-16: P2→P2a(도메인·데이터, 단일 게이트웨이)/P2b(멀티 소터) 분할 / 슈트 allowed=hold==None→READY·비활성→PAUSED 매핑(Core enum 변경 금지) / 멱등 DB 백스톱으로 static lock 제거 / NG DENIED FK=**nullable** / 게이트웨이 조회는 destination.id 단일 진입점(P2b 확장점) / v_destination_status 뷰 제외(인메모리만) / SQL Server filtered index 물리 컬럼명 교정.
> 도메인 모델 확인: destination(chuteNo 주소, dest_type CHUTE|SORTER_3D). SORTER_3D=3D 소터 1대(여러 cell 보유, 층 이동). 일반 CHUTE=소터·셀·층 없음.

## Goal
P1이 None으로 미룬 IF-08 결선을 완성한다. **chuteNo→목적지 타입 분기**(SORTER_3D=DepositDecider 경로 / CHUTE=내부 FULL/PAUSED/NORMAL 경로), **ERD §7 FULL/PAUSED 인메모리 집계**(cur_qty 컬럼 금지, piece 단일진실)를 WcsHold로 변환해 IF-08 주입, **timeStamp 백필**(RCS 파싱/실패 시 UtcNow), **MAJOR-1 진성 멱등 DB 백스톱**(piece 부분 유니크 + 위반 catch → static `_recordLock` 제거), **MINOR/이관 정리**. **단일 게이트웨이 전제(멀티 소터 P2b)**. **Wcs.Core(Decide·Models·ToWire) 동작/시그니처 무변경 — 분기는 API 계층, hold만 산출·주입.** 기존 44 회귀 0.

## Scope (IN)
1. **IF-08 목적지 타입 분기(API 계층)**: chuteNo→destination 조회 후 dest_type 분기.
   - **SORTER_3D**: 게이트웨이 스냅샷(P2a 단일 `IPlcGateway.Latest`) + 산출 WcsHold를 `DepositDecider.Decide(snap, agvFloor, hold)`에 주입. **Decide 호출부 시그니처·반환 그대로.**
   - **CHUTE(소터·층·Ready 없음)**: hold만 판정 — `hold==None`→allowed=true·reason="READY" / Full→FULL / Paused→PAUSED. **비활성(enabled=false/is_active=false) 슈트→PAUSED 매핑**(기존 DenyReason 내, Core enum 변경 금지). agvFloor·TgtFloor 쓰기 경로 없음(쓰기 큐 미투입).
   - 게이트웨이 조회 = **destination.id 키 단일 진입점**(`ISorterGatewayRegistry` 가칭, P2a는 단일 반환 — P2b 확장점).
   - **SPEC §2/§3에 슈트(비소터) IF-08 경로 명문화**(문서 동반).
2. **FULL/PAUSED 계산(ERD §7)**: 인메모리 집계 서비스(싱글톤) — 기동 시 DB 쿼리로 재구성. `SUM(piece.qty WHERE deposited_at>chute_detail.last_cleared_at) + 이동중 예약 qty ≥ work_full_qty`→Full(피스 qty>1 가능, COUNT 아님). IF-05 예약(+)·IF-10 투입·비움(리셋) 이벤트로 증감. **cur_qty 컬럼 금지**. PAUSED=destination.status. WcsHold 변환해 IF-08·IF-05 주입. (Full+Paused 동시→Full 우선, SPEC §2 Hold 순서.)
3. **timeStamp 백필**: IF-05/08/10 수신 시 RCS timeStamp 파싱(`"yyyy-MM-dd HH:mm:ss"`), 없거나 실패→**서버 UtcNow**. client_ts엔 원문 보존(파싱 실패해도, ERD §6), effective=파싱값 또는 UtcNow. 로컬 `DateTimeOffset.Now`→`UtcNow` 통일. created_at(UTC) 별도 유지.
4. **MAJOR-1 멱등 DB 백스톱**: piece 부분 유니크 `(p_id) WHERE is_active=1 AND status IN('DEPOSITED','CELL_ASSIGNED','LOADED')`(SQL Server filtered/SQLite expression). **물리 컬럼명 정확**(현 PascalCase — 아래 ※). `EfDepositRecorder.RecordDeposit`에서 유니크 위반 catch→진성 멱등(중복 false). **static `_recordLock` 제거**. **양 provider 증분 마이그레이션 add**(Wcs.Migrations.Sqlite·SqlServer, ModelSnapshot 갱신, pending 0).
5. **MINOR-4 셀 배정 유니크**: cell_assignment `(cell_id) WHERE released_at IS NULL` 부분 유니크(동시 이중 점유 차단). 양 provider 마이그레이션.
6. **MINOR-2 RowVersion 정리**: 비활성 provider 분기 RowVersion `Ignore()`(이중 물리 컬럼 제거).
7. **MINOR-5 NG DENIED FK = nullable(사용자 확정)**: IF-05 NG piece의 destination_id를 **nullable FK**로(임의 fallback 제거). 스키마·마이그레이션·집계 쿼리 NULL 처리 동반.
8. **MINOR-6 IF05_REQ 통합**: IF05_REQ piece_event를 QueryDestination 트랜잭션에 통합, RecordDestinationQuery 메서드 제거(IF-05 호출 정리).
9. **죽은 코드·종료 토큰**: 죽은 GetDestType·다운캐스트→전용 인터페이스, InMemory* 죽은 클래스 제거. 핸드셰이크 `CancellationToken.None`→`IHostApplicationLifetime.ApplicationStopping` 주입.

※ **잠재 결함 교정**: 현 SQL Server Initial의 piece 필터드 유니크가 `[is_active]`인데 물리 컬럼은 PascalCase `IsActive` → 실 SQL Server 인덱스 생성 실패(SQLite EnsureCreated라 미검출). P2a에서 물리 컬럼명 일치(또는 snake_case 매핑)로 교정.

## Scope (OUT) — P2b/P3 이연
- **멀티 소터 N대 레지스트리**(소터별 IModbusMaster/PlcPollingService/HandshakeOrchestrator·소터별 버스/포트·appsettings 소터 배열·destination.id 라우팅)→**P2b**. P2a는 단일 게이트웨이, IF-08은 destination.id 단일 진입점으로 호출하되 단일 반환.
- S1~S9→P3. 보존 퍼지·운영 자동 Migrate()→M5. v_destination_status 물리 뷰 제외(인메모리).
- **Wcs.Core 판정·전송 추상화·핸드셰이크 동작/시그니처 변경 0**(hold 산출·주입, 종료 토큰 전달뿐).

## Detected Project Type: Backend/API (도메인 결선 + 동시성 멱등 + 스키마 증분)
검증 = 44 회귀 + IF-08 분기(슈트/소터) + FULL 계산 + timeStamp 백필 + 멱등 DB 백스톱(lock 없이 8병렬 1건) + 이관 정리.

## Evaluation Criteria
1. 빌드/테스트: build exit0(경고0/오류0). `dotnet test` 기존 44 회귀 0 + 신규 GREEN, 4회 연속. 기존 split(Decider15/PlcGatewayIntegration9/RtuTransport4) 불변, ApiIntegration 신규 추가만.
2. **Core 무변경**: git diff src/Wcs.Core 0바이트. PlcGateway/HandshakeOrchestrator는 종료 토큰 외 동작 변경 0.
3. IF-08 분기: 소터=Decide(층·Ready), 슈트=hold만(NORMAL→READY/Full→FULL/Paused→PAUSED, 비활성→PAUSED, 층 무관·쓰기 큐 0). SPEC §2/§3 문서 동반.
4. FULL 계산: 누적qty(deposited_at>last_cleared_at)+예약 ≥ work_full_qty→Full, 비움 리셋, qty>1 케이스. cur_qty 컬럼 0(grep).
5. timeStamp: 파싱→client_ts 원문+effective / 실패·누락→UtcNow. 로컬 Now 잔존 0(grep).
6. 멱등 DB 백스톱: piece 부분 유니크 양 provider(물리 컬럼명 정확), _recordLock 제거(grep 0), **CONCUR 8병렬 동일 pId lock-free→1건 전이·IF-11 ≤1** 단독 5회 GREEN.
7. MINOR 정리: cell_assignment 유니크·RowVersion Ignore·NG nullable FK·IF05_REQ 통합(RecordDestinationQuery 제거)·죽은코드·종료토큰 — git diff + 동작 무변경.
8. 마이그레이션 동기: `has-pending-model-changes` 양 provider "No changes", 증분이 piece·cell_assignment 부분 유니크 CreateIndex 포함.
9. 하드코딩 0(work_full_qty·시간 appsettings/DB) / 커밋 전 HEAD=feat/m4-p2-domain 확인.

## Completion Conditions (회귀 0)
- build exit0 / `dotnet test` 44 회귀 0 + 신규 GREEN 4회, split 불변.
- IF-08 슈트/소터 분기 + SPEC §2/§3 갱신. FULL/PAUSED 인메모리→WcsHold(cur_qty 0). timeStamp 백필+UtcNow 통일.
- piece·cell_assignment 부분 유니크 양 provider(pending 0), static lock 제거, CONCUR lock-free GREEN.
- MINOR-2/5/6 + 죽은코드 + 종료토큰. Wcs.Core 무수정. feature 브랜치 커밋.

## Verification Scenarios
- **VS-P2a-1 회귀(필수)**: 44 GREEN 4회, Decider/PlcGatewayIntegration/RtuTransport diff 0, ApiIntegration 기존 16 단언 불변.
- **VS-P2a-2 IF-08 소터**: SORTER_3D→Decide(Ready=1·층일치→READY/층불일치→WRONG_FLOOR+TgtFloor 기입/Ready=0→BUSY). hold=None시 P1 와이어 동일.
- **VS-P2a-3 IF-08 슈트**: CHUTE→hold만. NORMAL→READY/PAUSED status→PAUSED/FULL 조건→FULL/비활성→PAUSED. **TgtFloor 쓰기 큐 0**.
- **VS-P2a-4 FULL**: work_full_qty=N → 누적 qty 도달 시 FULL, 비움 후 NORMAL 복귀, 피스 qty>1.
- **VS-P2a-5 timeStamp**: 정상→client_ts 원문·effective / 누락·"bad"→client_ts 보존+effective=UtcNow. created_at UTC.
- **VS-P2a-6 멱등 DB 백스톱(핵심)**: 8병렬 동일 pId IF-10 **lock 제거 상태**→1건 DEPOSITED, 나머지 멱등 200, IF-11 ≤1. 단독 5회. _recordLock grep 0.
- **VS-P2a-7 셀 유니크**: 동시 같은 소터 셀 배정→`(cell_id) WHERE released_at IS NULL` 위반으로 1건만.
- **VS-P2a-8 NG nullable FK**: IF-05 NG(NO_DEST)→piece DENIED destination_id=NULL. 와이어 P1 동일(NG·chuteNo=null).
- **VS-P2a-9 마이그레이션 동기**: 양 provider pending 0, 증분에 piece·cell_assignment 부분 유니크.
- **VS-P2a-10 종료토큰·정리**: 핸드셰이크 ApplicationStopping(CancellationToken.None grep 0), IF05_REQ 트랜잭션 내 1건·RecordDestinationQuery 부재·죽은코드 부재.

## 미확정 (추측 금지)
- 이동중 예약 qty = RESERVED/PERMITTED(미투입) 활성 piece qty 합(DENIED/CANCELLED 제외).
- SQLite 부분 유니크 expression이 EF HasFilter로 정확히 내려가는지 + 물리 컬럼명 검증.

> Planner self-check — Backend/API. Scenario slots VS-P2a-1~10(회귀·소터/슈트 분기·FULL·timeStamp·멱등DB·셀유니크·NG FK·마이그레이션·정리). All filled: yes. Core 무변경 + 회귀 0 가드. 멱등 DB 백스톱은 lock-free CONCUR 5회 + 독립 코드리뷰 필수. 사용자 확정(P2 분할·슈트 PAUSED·lock 제거·nullable FK·destination.id 진입점) 반영.
