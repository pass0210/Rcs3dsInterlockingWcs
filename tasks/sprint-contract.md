# Sprint Contract — S-M4-P2b (멀티 소터: 단일 게이트웨이 → 소터별 레지스트리 N대)
> M4 (phase 2b / 3). 전제: P2a APPROVED(51 GREEN). 사용자 확정 2026-06-17: 소터 판별 **DB 주도**(destination.dest_type=SORTER_3D 기동 쿼리) + 설정 키 **ChuteNo**(소터별 전송 파라미터) / 테스트 실 Sim3ds 2대 + fake / Timing 공통+소터별 오버라이드 / 단일 구성은 N=1로 흡수. 소터 라우팅 키 = destination.id.
> 도메인: SORTER_3D destination 1개 = 3D 소터 1대(여러 cell·층 이동·별도 버스/포트).

## Goal
P2a가 단일 반환으로 둔 `ISorterGatewayRegistry` 진입점 **뒤를 소터별 게이트웨이 번들 N대로 교체**. **기동 시 DB에서 `dest_type=SORTER_3D` destination을 조회해 소터 목록 확정**(단일 진실=DB), 각 소터의 전송 파라미터(RTU 포트/Baud 또는 TCP Host/Port + Timing)는 **appsettings 소터 배열에서 ChuteNo로 매칭**. destination.id별 번들(IModbusMaster + 소터별 PlcWriteQueue + PlcPollingService + HandshakeOrchestrator) 인스턴스화. IF-08/IF-10 라우팅을 chuteNo→destination.id→레지스트리로 일원화하되 **IF-08 핸들러 본문 무변경 목표**. 인스턴스별 `_clientLock`·쓰기 큐·RFlag 채널 보존(off-lock 0·소터 간 경합 0). 소터 0·1·N대 IHostedService Start/Stop. **Wcs.Core 판정 무변경, PlcGateway/HandshakeOrchestrator/Sim3ds 클래스 본문 무변경(인스턴스화만 N배).** 기존 51 회귀 0(소터 1개=P2a 동일).

## Scope (IN)
1. **소터 판별 = DB 주도**: 기동 시 `destination WHERE dest_type='SORTER_3D' AND is_active` 조회 → 소터 목록(destination.id·chute_no). 각 소터의 전송 파라미터는 appsettings 소터 배열에서 **chute_no로 매칭**. SORTER_3D인데 설정 전송 항목 없으면 **fail-loud**(기동 로그 에러 — 추측 금지).
2. **소터별 번들 N대(DI 팩토리)**: destination.id별 IModbusMaster(ModbusMasterFactory.Create, 매칭된 전송 설정) + **번들 전용 PlcWriteQueue**(단일 공유 큐 제거 — 절대규칙 #1 소터별 보존) + PlcPollingService + HandshakeOrchestrator. 불변 컬렉션(키=destination.id).
3. **appsettings: 단일 Plc → 소터 배열** (ChuteNo 키):
   - 소터별: `ChuteNo` + Transport(Rtu/Tcp) + 전송 파라미터(PortName/BaudRate/… 또는 Host/Port). **Timing은 공통 + 소터별 오버라이드**(공통 Timing 상속, 항목별 덮어쓰기). 하드코딩 0.
   - 단일 소터 구성도 배열(N=1)로 표현 — 기존 단일 Plc 섹션은 배열로 흡수(별도 레거시 경로 없음).
4. **`ISorterGatewayRegistry` N대 라우팅**: destination.id→번들. GetLatest(destinationId)는 해당 소터 스냅샷(미존재 시 null→OFFLINE 경로 유지). IF-08 SetTgtFloor 큐 투입·IF-10 핸드셰이크 트리거가 destination.id로 번들 경유(레지스트리가 번들 핸들 제공 — 인터페이스 확장). SingleSorterGatewayRegistry 제거 또는 N=1 흡수.
5. **라우팅 일원화**: IF-08 chuteNo→destination(조회됨)→dest.Id→번들(스냅샷·SetTgtFloor 큐). **핸들러 본문 무변경 목표**(단일 writeQueue 의존을 번들 경유로 최소 교체만). IF-10 3D 보고→dest.Id 번들 핸드셰이크 트리거. 셀 선택·멱등·FULL 집계 무변경.
6. **인스턴스별 동시성 불변**: 각 PlcPollingService `_clientLock`이 폴·쓰기·RMW·Disconnect/재연결을 인스턴스별 단일 임계구역 직렬화(M2 off-lock 0 보존). `_cSeq`·RFlag 채널 per-instance. 별도 버스→인스턴스 간 소켓/시리얼 경합 0.
7. **IHostedService 소터별 Start/Stop**: 번들 N개 PlcPollingService를 기동/종료(ApplicationStopping) 연결. 소터 0(빈)·1·N 전부 정상.
8. **죽은 코드·문서**: 단일 IPlcGateway/PlcWriteQueue/HandshakeOrchestrator 싱글톤 직접 등록 제거(번들 흡수). SPEC §7-A "런타임 단일 소터" 정정 + 소터 배열 스키마·DB 주도 판별 명문화.

## Scope (OUT) — P3/M5
- S1~S9 → P3. 보존 퍼지·운영 자동 Migrate() → M5.
- **스키마 무변경**: destination/cell/piece ERD·마이그레이션 변경 0 → pending 0(증분 add 0). 매핑은 기존 destination 행 사용.
- **Wcs.Core(Decide·Models·ToWire) 동작/시그니처 변경 0. PlcPollingService·HandshakeOrchestrator·Sim3ds 클래스 본문 변경 0**(인스턴스화·DI·옵션 바인딩만). IF-08/IF-10 와이어 변경 0(라우팅만).
- CHUTE 경로(ChuteCapacityService·FULL/PAUSED) 무변경.

## Detected Project Type: Backend/API (DI 멀티 인스턴스 + 동시성 인스턴스 격리 + 설정 스키마 + 기동 DB 판별)
검증 = 51 회귀(소터 1대) + 2+ 소터 라우팅 독립 + 소터별 핸드셰이크 독립(C_Seq) + 인스턴스별 직렬화 + 소터별 OFFLINE 독립 + 소터 0/1/N 기동.

## Evaluation Criteria
1. 빌드/테스트: build exit0(경고0/오류0). `dotnet test` 51 회귀 0 + 신규 GREEN, 4회 연속. 기존 split 불변, ApiIntegration 멀티소터 신규만.
2. **Core·게이트웨이 클래스 무변경**: git diff src/Wcs.Core 0. src/Wcs.PlcGateway/{PlcGateway.cs,HandshakeOrchestrator.cs} 클래스 본문 동작 변경 0(옵션/생성자 시그니처 외). Sim3ds 본문 0.
3. DB 주도 판별: 기동 시 SORTER_3D destination 조회로 소터 목록. SORTER_3D인데 설정 전송 없으면 fail-loud(기동 에러).
4. 소터별 번들 N대: destination.id별 IModbusMaster·PlcWriteQueue·PlcPollingService·HandshakeOrchestrator N개 실재. 단일 공유 큐 부재(grep).
5. 라우팅: chuteNo→destination.id→번들. IF-08 본문 무변경(또는 레지스트리 경유 최소 교체, git diff 입증). 2소터 다른 스냅샷→독립 판정(교차 0).
6. 인스턴스별 직렬화·off-lock 0: 전 `_clientLock`/`_master` 인스턴스별 임계구역. `_cSeq` 소터별 독립(공유 0).
7. 소터별 핸드셰이크 독립(핵심): 2 Sim3ds(다른 포트) 동시 핸드셰이크→각 소터 C_Seq↔R_Seq 자기 소터 내 일치, 교차 0. 다회 GREEN.
8. 소터별 OFFLINE 독립: 한 소터 단절→그 소터 IF-08만 OFFLINE, 타 소터 정상. 재기동 후 후속 핸드셰이크 Success.
9. 소터 0·1·N 기동 + 스키마 pending 0(양 provider) + 하드코딩 0(포트·Timing 설정) + 커밋 전 HEAD=feat/m4-p2b-multisorter 확인.

## Completion Conditions (회귀 0)
- build exit0 / `dotnet test` 51 회귀 0 + 신규 GREEN 4회, split 불변.
- DB 주도 소터 판별 + ChuteNo 키 설정 매칭(미스매치 fail-loud). destination.id별 번들 N대 + 소터별 큐. 단일 공유 큐·핸드셰이크 싱글톤 제거.
- IF-08/IF-10 라우팅 + 본문 무변경. 와이어 무변경. 2+ 소터 라우팅·핸드셰이크 독립(C_Seq)·인스턴스별 직렬화·소터별 OFFLINE GREEN. 소터 0/1/N 기동.
- Wcs.Core·게이트웨이 클래스 본문 무변경. pending 0. SPEC §7-A 정정. feature 브랜치 커밋(HEAD 확인).
- **독립 코드리뷰 통과**(동시성 표면 — 인스턴스 격리·off-lock 0·소터 간 경합 0).

## Verification Scenarios
- **VS-P2b-1 회귀(필수)**: 51 GREEN 4회. Decider/PlcGatewayIntegration/RtuTransport diff 0. 소터 1대 구성=P2a 동작 동일.
- **VS-P2b-2 N대 인스턴스화**: SORTER_3D 2개 + 설정 2개→번들 2세트(각 IModbusMaster·PlcWriteQueue·PlcPollingService·HandshakeOrchestrator). 공유 큐 부재.
- **VS-P2b-3 라우팅 독립(fake)**: 소터 A·B 다른 fake 스냅샷(A Ready=1·층일치/B Ready=0)→IF-08(destA) READY / IF-08(destB) BUSY. 교차 0.
- **VS-P2b-4 핸드셰이크 독립(핵심, 실 Sim3ds 2대)**: 다른 포트 Sim3ds 2대 동시 IF-10 3D 보고→각 소터 C_Seq↔R_Seq 자기 소터 내 일치, 교차 0. 다회 GREEN(flaky 배제).
- **VS-P2b-5 인스턴스별 직렬화**: 소터 A 폴 중 다수 핸드셰이크→A R_Seq==C_Seq 매 건 성공, B 무영향. 4회 연속.
- **VS-P2b-6 소터별 OFFLINE 독립**: A 단절→A IF-08만 OFFLINE·B 정상. A 재기동 후 후속 핸드셰이크 Success(off-lock 인스턴스별 보존).
- **VS-P2b-7 소터 0·1·N 기동**: 빈(0대) 기동/종료 정상(SORTER_3D IF-08은 OFFLINE). 1대(P2a 동등). 2+대 StartAsync/StopAsync 전부 호출. SORTER_3D 설정 누락→fail-loud.
- **VS-P2b-8 스키마·하드코딩**: pending 0(양 provider). 소터별 포트·Timing 설정 주입(하드코딩 grep 0).

## 미확정 (추측 금지)
- 레지스트리 인터페이스 확장 형태: IF-08 본문 무변경 위해 스냅샷+SetTgtFloor enqueue+핸드셰이크 트리거를 destination.id로 제공하는 번들 핸들 반환(권고) vs 별도 메서드 분리 — 구현 시 본문 무변경도 우선해 확정.
- 기동 DB 판별 타이밍: IHostedService StartAsync에서 SORTER_3D 조회→번들 구성. DB 미가용 시 정책(재시도 vs fail) — 단일 인스턴스·기동 1회라 fail-loud 권고.

> Planner self-check — Backend/API. Scenario slots VS-P2b-1~8. All filled: yes. 회귀 0(소터 1대=P2a) + Core/게이트웨이 클래스 무변경 + pending 0. 동시성 표면→독립 코드리뷰 필수. 사용자 확정(DB 주도 판별·ChuteNo 키·실 Sim3ds 2대 테스트·Timing 공통+오버라이드·단일 N=1 흡수) 반영.
