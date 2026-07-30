# Sprint Log — S-TRACE-READY-PUSH-AND-DEFAULT

## IMPLEMENTATION COMPLETE (Generator, 2026-07-30)

관측/로깅 전용·additive 로 신규 트레이스 이벤트 4개(7·8·9·10)와 B2C 기본화면 /trace 랜딩을 구현. 회귀 0.

### 변경 요약 (코드 7파일 — 스코프 정확 일치)
**백엔드 (2)**
- `backend/src/Wcs.Api/Services/TraceLogService.cs`
  - S3: 헤더 주석 이벤트 목록 1~6 → 1~10 갱신 + `TraceRecord` docstring/필드 주석 "1~6"→"1~10"(문서 정합).
  - S1: `TraceWiring.Wire` 에 `bundle.SubscribeRegisterChange` 로 **reg=="Ready" 추가 구독**(기존 이벤트 6 C_Flag 구독 무변경) → 1→0=EventNo7(READY_1TO0)·0→1=EventNo9(READY_0TO1). Floor=전이 관측 시점 `bundle.Latest.CurFloor`(EmitRegisterChanges 는 `_latest` 갱신 후 발화 — cur 스냅샷). PId/CSeq/CellNo=null(소터 scope). 핸들러 예외 격리(try/catch)·trace.Log=Channel.TryWrite(논블로킹).
  - 발화 로직을 순수 헬퍼 `public static TraceRecord? BuildReadyEdgeRecord(chuteNo, destId, oldV, newV, curFloor)` 로 분리 → I/O 무의존 결정적 단위 테스트 가능(1→0=7·0→1=9·그 외 null).
- `backend/src/Wcs.Api/Services/ChuteStatePushClient.cs`
  - S2: 생성자에 **optional `ITraceLogger? trace = null`** 추가(역호환 — 직접 생성 단위 테스트 무영향, 전체 호스트에선 DI 주입). `PushAsync(payload, baseUrl, ct)` **단일 전송 chokepoint**(두 오버로드 funnel)에서 계측.
  - DORMANT(baseUrl null) 가드 **이후** `sentAt=DateTimeOffset.Now` 캡처(전송 시각 anchor) → 성공/소진 return 지점에서 `EmitPushTrace(result, attempts)` 호출. next_state==2→EventNo8(CHUTESTATE_PUSH_BUSY)·==3→EventNo10(CHUTESTATE_PUSH_READY)·그 외/빈 payload→안전 스킵. Detail={next_state,result,attempts,host} JsonSerializer 직렬화. ChuteNo=payload.ChuteNumbers[0], DestId=null(best-effort — 계약 허용). 전체 예외 격리(fail-safe — 트레이스 실패가 push 비차단). **판정로직(성공/재시도) zero-diff — 부수 훅만.**
  - 기존 operation_log CHUTESTATE_PUSH(OK/FAIL) **유지**(대체 아님 — 나란히 additive).

**프론트 (3)**
- `frontend/src/pages/TraceLogPage.tsx` (S5): EVENT_META 에 7("Ready 1→0")·8("슈트상태 push(busy)")·9("Ready 0→1")·10("슈트상태 push(ready)") 라벨/색조 추가 + 이벤트 필터 드롭다운 `EVENT_FILTER_OPTIONS=[1..10]` 로 확장 + "6개 이벤트"→"10개 이벤트…" 문구 갱신. 기존 1~6 렌더·TraceLine 폴백 무변경. 신규는 chuteNo/floor/detail 컬럼, pId/cSeq/cellNo="—"(제너릭 렌더).
- `frontend/src/lib/uiMode.ts` (S6): `homePathFor('b2c')` = **'/trace'**(단일 소스 — ModeHome `/`·`*`·Layout ModeToggle 공용). b2b='/data-generator' 불변.
- `frontend/src/components/Layout.tsx`: /trace NAV subtitle "6개 이벤트"→"…10개 이벤트" 문구 정합(cosmetic·개수 정합).

**테스트 (2)**
- `backend/tests/Wcs.Tests/TraceReadyPushTests.cs` (신규·결정적 단위): push 8/10 계측(FakeChuteStateServer + 캡처 ITraceLogger), DORMANT no-op, next_state 2/3 외 안전 스킵, 실패 전송 result=FAIL 계측, Ready 헬퍼 7/9/비-에지 null, 파일 sink [7][8][9][10] raw 태그 + eventNo 필터. 10 케이스.
- `backend/tests/Wcs.Tests/E2E/E2EGroupN_TraceLogTests.cs`:
  - **N3(신규·라이브 E1/E2)**: 실 Sim `SetReady(false/true)` 로 Ready 1→0·0→1 유도 → 이벤트 7·9(폴 관측) + 실 PushAsync PUT→fake RCS 이벤트 8·10(next_state 2/3) 이 전용 파일·REST 로 관통·같은 chuteNo(30) 상관 실증 + operation_log CHUTESTATE_PUSH additive. (시드가 CHUTE 1~6 도 부트스트랩 push 하므로 소터 chuteNo==30 로 좁혀 선택. Ready 되돌리기 전 소터 이벤트 8 확인으로 3→2→3 coalesce 방지.)
  - **N1 갱신(불가피·회귀 아님)**: 같은 흐름에 이제 additive 이벤트 7~10 이 공존하므로 "정확히 {1..6}" 단언을 "1~6 모두 포함(superset)"으로 완화. 이벤트 1~6 발화·상관·additive 회귀 0 검증은 불변.

### 재량 결정 기록
- **전송 계측 지점**: OQ2 "모든 IF-08 PUT 단일 훅" → `ChuteStatePushClient.PushAsync(payload,baseUrl,ct)`(레거시 오버로드도 여기 위임 = 유일 전송 chokepoint)에서 계측. 전송당 1회(성공/소진 시점) 발화 — operation_log 와 동일 시맨틱. At=첫 전송 시도 시각(sentAt) 으로 지연 지표(같은 chuteNo 이벤트7/9→8/10 시각차) 정합.
- **ITraceLogger 주입 방식**: optional 파라미터(null 허용) — 직접 생성 단위 테스트(ChuteStatePushClientTests) 무수정 컴파일 보장 + 판정로직 무접촉. 전체 호스트에선 등록된 싱글톤이 DI 주입.
- **Ready curFloor 취득**: 콜백 (reg,old,new) 만 제공 → `bundle.Latest.CurFloor`(EmitRegisterChanges 가 `_latest=cur` 이후 발화하므로 전이 시점 스냅샷) 사용.
- **Layout subtitle 갱신**: 계약 S5 는 TraceLogPage 만 명시하나 Layout NAV subtitle 도 "6개 이벤트" stale 문구 보유 → 개수 정합 위해 함께 갱신(cosmetic·trace 표면 내).

### 테스트 결과 (baseline 대조)
- **전체 `dotnet test backend/Wcs.sln`: 504 통과 / 0 실패 / 0 건너뜀**. baseline 493 + 신규 11(TraceReadyPushTests 10 + N3 1) = 504(산술 일치·회귀 0).
- 신규 결정성: TraceReadyPushTests 10 + N3 1 = 11/11 반복 GREEN(N3 는 혼합 실행 전 회차 전부 통과).
- **N1/N2 flake 는 pre-existing(내 변경 무관) — 귀속 완료**: E2EGroupN 격리 6회 반복에서 내 버전 1~2/6 실패 vs **baseline(stash) 4/6 실패**(더 심함). 실패 모드=이벤트 6(C_Flag 1→0) 관측 타임아웃·pId↔cSeq 상관 — 문서화된 RealSim 핸드셰이크 flake(lessons: s9-flake·e2e-parallel-load·single-sorter-concurrent-handshake-gap). 내 추가 트레이스 write 는 flake 율을 높이지 않음(오히려 baseline 이 더 자주 실패). (검증 절차: `git stash push -u -- <4 code files>` → baseline 6회 → `git stash pop` 복원, 전 파일 무손실 확인.)
- 빌드 경고: 신규 0. 전부 선재(NU1903×10·xUnit2013×2[ChuteStatePushTests·TwoFloorHostRoutingTests]·CS8604×1[B2cFacilityService]) — 내 4파일에서 warning 0.
- 프론트: lint exit 0 · typecheck(tsc --noEmit) exit 0 · build(vite) exit 0(chunk>500kB 경고=선재). wwwroot gitignored·무추적(빌드 산출 tracked diff 0).

### 절대규칙 게이트 (코드 확인)
- #1: 신규 코드에 EnqueueSet*/WriteRegister/Modbus/write-queue 호출 0(grep NONE). trace sink=Channel.TryWrite.
- #7: 리터럴 경로/호스트 0(host=baseUrl 파라미터·TraceLog dir=옵션값·next_state/result 는 런타임 데이터). grep(D:\\·http://) NONE.
- #8: PlcGateway/Wcs.Core/Sim3ds/HandshakeOrchestrator **zero-diff**(git diff --stat 공란). ChuteStatePushClient 는 판정로직 무접촉·부수 훅만(계약 명시 허용). 로깅은 Wcs.Api 계층.
- 논블로킹·fail-safe: 모든 신규 발화 예외 격리 + Channel.TryWrite.

BEFORE HANDOFF 전량 GREEN 확인 완료.
