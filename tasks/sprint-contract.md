# Sprint Contract — S-M4-P1 (EF Core 영속화 + 리포지토리 DB 교체)
> M4 (phase 1 / 3). 사용자 확정 2026-06-16: 3단계 분할 / 테스트 in-memory SQLite·운영 SQL Server / 마이그레이션 생성만(자동적용 M5) / 소터 키=destination.id.

## Goal
ERD.md의 16테이블을 EF Core 엔티티 + `WcsDbContext`로 구현, Initial 마이그레이션·기준정보 시드 생성,
M3가 교체점으로 남긴 4개 인터페이스(`IOrderRepository`·`IDepositRecorder`·`ICellSelector`·`IAgvFloorResolver`)를
**EF Core 구현으로 교체**한다. **WCS 관찰 동작(IF-05/08/10 와이어 응답·핸드셰이크·판정)은 전혀 안 바뀐다** —
데이터가 인메모리 대신 DB에 영속화되는 것만 변화. **M3의 44 테스트가 단언·코드 변경 없이 GREEN 유지**(강한 회귀 가드).

## Scope (IN)
1. **Wcs.Data EF Core 패키지**(Wcs.Data.csproj): SQL Server provider + SQLite provider + EF Core Design/Tools. net10.0.
2. **16 엔티티**(ERD §테이블 그대로): destination·cell·cell_assignment·agv·printer·chute_detail·induction·work_batch·wcs_order·order_item·piece·piece_event·sorter_command·plc_event·alarm·destination_event.
   - 대리키 `id bigint identity` 전부 / 자연키 UNIQUE(ERD §인덱스) / 상태 enum `HasConversion<string>()` + CHECK(가능 범위) / 이력(piece_event·plc_event·destination_event) append-only(UPDATE 경로 안 만듦) / 공통 `created_at datetime2(UTC)` / 상태테이블 `updated_at`+`row_version` / piece 필터드 유니크 `(p_id) WHERE is_active=1`.
   - piece/piece_event에 `client_ts`·`created_at` 컬럼은 ERD대로 생성(백필 로직은 P2).
3. **WcsDbContext + provider 분기**: SQL Server(filtered index·rowversion) / SQLite(개발·테스트 — 일반 `UNIQUE(p_id,is_active)`·정수 동시성 토큰). 연결문자열·provider 선택 appsettings(하드코딩 금지).
4. **Initial 마이그레이션**(SQL Server·SQLite). **생성·테스트/개발 적용까지만 — 운영 기동 자동 Migrate()는 M5로 이연**(사용자 확정).
5. **기준정보 시드**: destination·chute_detail·cell·agv·printer·induction(+ M3 테스트가 의존하는 최소 오더 work_batch·wcs_order·order_item). M3 인메모리 시드(슈트·셀·agv 매핑·바코드 오더)와 **데이터 동등** — 회귀 0의 토대.
6. **4개 인터페이스 EF 구현**(교체점 1지점):
   - IOrderRepository.QueryDestination: 바코드→오더 + 상태판정(OVER/COMPLETED/PAUSED/NO_DEST) + OK 시 `reserved_qty+=qty` + **piece 삽입** + NULL 목적지면 AUTO 슈트 할당 — **한 DB 트랜잭션**.
   - IDepositRecorder: IF-05 OK/NG(NG=piece status=DENIED + piece_event, 예약 차감 0 — IF-16) / IF-10 멱등 보고(상태 전이 + piece_event) — 한 트랜잭션. **멱등성(중복 pId 무해) 보존**.
   - ICellSelector: 활성 cell_assignment 재사용 → 빈 셀 → 없으면 null. 점유=`released_at IS NULL`. 배정/해제 트랜잭션.
   - IAgvFloorResolver: **agv.floor 단일 진실**(DB 조회). appsettings `Floors:AgvNoToFloor`는 시드 전용 강등(런타임 조회 경로 제거). 매핑 없는 agvNo→명시적 null/400.
7. **DI 교체**(Program.cs): WcsDbContext 등록 + 4 인터페이스 EF 바인딩. **Wcs.Api→Wcs.Data ProjectReference 복원**. **`InMemory*` 프로덕션 경로 제거**(테스트도 SQLite 통일 — 사용자 확정).
8. **테스트 DB 배선**: ApiIntegration 16건이 의존하는 시드를 **테스트 in-memory SQLite**에 동등 제공. WebApplicationFactory가 테스트 provider/시드 주입.

## Scope (OUT) — P2/P3 이연
- IF-08 목적지 타입 분기(3D/CHUTE) → **P2**. P1은 IF-08 M3 동작 그대로(단일 게이트웨이 Decide, hold=None).
- FULL/PAUSED 계산 → P2. 멀티 소터(N대 레지스트리) → P2(P1은 단일 게이트웨이; 단 스키마는 소터=destination.id로 P2 라우팅 가능하게).
- timeStamp 백필 → P2(P1은 client_ts·created_at 컬럼만 생성).
- S1~S9 → P3. 죽은 코드 정리·핸드셰이크 앱 종료 토큰 → P2. 보존 퍼지 배치 → M5.
- **Wcs.Core 판정·전송 추상화·핸드셰이크·DTO 동작 변경 0**(결선/주입만).

## Detected Project Type: Backend/API (EF Core 영속화 확장)
검증 표면 = 기존 통합/단위 테스트 회귀 + DB 트랜잭션·provider 분기 검증. 신규 의존 EF Core(SQL Server·SQLite).

## Evaluation Criteria
1. **빌드/테스트 그린**: build exit 0(경고0/오류0). `dotnet test` **44/44 GREEN, 4회 연속**, split 불변(Decider 15/PlcGatewayIntegration 9/RtuTransport 4/ApiIntegration 16).
2. **ERD 충실도**: 16 엔티티 ↔ ERD 1:1(대리키·UNIQUE·enum string·append-only·row_version·created_at UTC·piece 필터드 유니크). 누락/추가 0.
3. **provider 분기 입증**: SQLite로 실제 테스트 구동 + SQL Server·SQLite 마이그레이션 양쪽 생성. filtered index/rowversion 분기 소스 실재.
4. **교체점 1지점**: 4 인터페이스 EF 교체 + DI EF 바인딩. `InMemory*` 프로덕션 경로 0(grep). Wcs.Data ProjectReference 복원.
5. **DB 트랜잭션**: IF-05 예약차감+piece삽입(+AUTO), IF-05 NG DENIED 기록, IF-10 멱등, 셀 배정이 각각 단일 트랜잭션(원자 경계) + 동시성 회귀 테스트.
6. **동작 무변경**: Wcs.Core·PlcGateway.cs·HandshakeOrchestrator.cs·Dtos.cs **무수정**(git status). IF-05/08/10 와이어·핸드셰이크 M3와 동일.
7. **agv.floor 단일진실**: IAgvFloorResolver DB 조회. appsettings 매핑 런타임 조회 0(grep, 시드 전용). 매핑 없으면 400.
8. **하드코딩 0**: 연결문자열·provider·시간값 전부 appsettings.
9. **HEAD 브랜치**: 커밋 전 feature 브랜치 확인(develop 직접 0 — lessons.md 2026-06-16).

## Completion Conditions (회귀 0)
- build exit 0 / `dotnet test` 44/44 GREEN 4회 연속, split 불변.
- 16 EF 엔티티 + WcsDbContext + provider 분기. Initial 마이그레이션(SQL Server·SQLite) 생성·SQLite 적용.
- 기준정보 시드 = M3 인메모리 데이터 동등.
- 4 인터페이스 EF 교체 + DI + Wcs.Data 참조 복원. `InMemory*` 프로덕션 경로 제거.
- IF-05/IF-10/셀배정/NG기록 단일 트랜잭션 + 동시성 회귀 테스트 1건↑ GREEN.
- Wcs.Core·PlcGateway.cs·HandshakeOrchestrator.cs·Dtos.cs 무수정.
- 테스트가 in-memory SQLite로 도는 결정적 통합 테스트.
- feature 브랜치 커밋(develop 직접 0).

## Verification Scenarios
- **VS-P1-1 회귀(필수)**: M3 44 테스트 단언·코드 무변경 GREEN(4회). DepositDeciderTests·PlcGatewayIntegration·RtuTransport git diff 무변경. ApiIntegration은 시드 배선만, 단언 불변.
- **VS-P1-2 ERD 16 대조**: WcsDbContext DbSet ↔ ERD 16테이블 1:1 + 매핑 원칙 소스 확인.
- **VS-P1-3 provider 분기**: SQLite 마이그레이션 적용→통합 GREEN. piece 유니크 SQLite=`UNIQUE(p_id,is_active)`/SQL Server=filtered. rowversion 분기.
- **VS-P1-4 IF-05 트랜잭션**: OK→reserved_qty↑+piece 삽입 원자. AUTO→빈 슈트 할당+dest_assign_type=AUTO+예약 원자. 실패 주입 시 전체 롤백.
- **VS-P1-5 IF-05 NG(IF-16)**: 미존재/PAUSED/OVER→200 NG+piece DENIED+piece_event, 예약 차감 0. 와이어 M3 동일.
- **VS-P1-6 IF-10 멱등 DB**: 같은 pId 중복→200 OK·1건 전이. **CONCUR 8병렬 동일 pId→정확히 1건 전이, IF-11 트리거 ≤1**(DB 동시성·필터드 유니크/트랜잭션 격리로 입증).
- **VS-P1-7 셀 배정 DB**: 3D IF-10→cell_assignment(released_at NULL)→핸드셰이크 후 해제. 빈 셀 없으면 트리거 생략 — M3 동일.
- **VS-P1-8 agv.floor**: IF-08 agvFloor가 agv 테이블 조회. appsettings 매핑 제거해도 DB로 동작. 없으면 400.
- **VS-P1-9 동작 무변경 종합**: IF-08 라이브 allowed/READY/WRONG_FLOOR·TgtFloor 기입, 핸드셰이크 C/R 결과 M3 동일.

## 미확정 (구현 중 추측 금지 — 기록·질문)
- U5 row_version 충돌 정책: 단순 트랜잭션으로 충분한지 vs 낙관적 동시성 예외 재시도/fail-loud — 충돌 발생 케이스 한정. P1은 단일 트랜잭션 우선, 충돌 처리 필요 시 fail-loud + 알람.
- 보존/퍼지(plc_event 7~14일 등)는 M5.

> Planner self-check — Detected project type: Backend/API(EF Core 영속화). Required scenario slots: VS-P1-1~9(회귀·ERD대조·provider·트랜잭션·NG·멱등·셀·agvFloor·동작무변경). All filled: yes. 동작 무변경 인프라 교체 + 회귀 0 강한 가드. 사용자 확정(3단계/SQLite test·SQL Server 운영/마이그레이션 생성만/소터키 destination.id) 반영.
