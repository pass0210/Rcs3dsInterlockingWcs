# Sprint Contract — S-RCS-DOCS-B2B

> Branch: `docs/rcs-interface-b2b-b2c` · Base: `develop` (@ PR #35 병합 완료)
> 작성: Planner Subagent · 2026-07-07

## Goal

RCS(로봇 제어 시스템) 담당자에게 제공하는 **WCS↔RCS 인터페이스 정의 문서 3종**에
**B2C(실시간 3D 소터 연동)와 B2B(배치 테스트데이터 방식)** 두 운영 방식을 **명확히 구분**해
기술한다. 현재 세 문서는 B2C만 담고 있다. B2B(3DS 핸드셰이크 없음, 배치 등록 데이터 기반)를
추가하되 B2C와 섞이지 않게 나눠 쓰고, **B2B 와이어 계약은 RCS 합의 원천(`docs/api-spec-ko.html`)을
한 글자도 틀리지 않게** 반영한다.

- **문서 전용 스프린트다.** 코드/스키마/테스트/설정은 0 변경. 산출물은 `docs/*.html` 3개 파일뿐이다.

## 배경 · 두 방식의 본질 차이 (Generator가 문서에 드러내야 할 핵심)

| 구분 | **B2C (실시간 · 현행)** | **B2B (배치 · 신규 기술)** |
|---|---|---|
| 트리거 | 인덕션 스캔 즉시, 피스 단위 실시간 | `bizDay`/`batch` 단위 배치 등록 데이터 |
| 인터페이스 | IF-05/08/09/10 (`destination-query` 등) | `unprocessed`·`input`·`classification`·`results`·`box` (`/api/v1/works/*`) |
| 목적지 판정 | **WCS가 판정**(오더 매칭·예약 차감·소터 셀) | **없음** — 배치에 등록된 `chuteNo`를 그대로 사용 |
| 3DS 연동 | **있음** — Modbus 핸드셰이크(IF-06/11/12), 층 정렬, PLC 레지스터 | **없음** — 소터 핸드셰이크/IF-08 푸시/층 개념 전부 무관 |
| 방향 | IF-05/09/10 RCS→WCS, IF-08 WCS→RCS 푸시 | 전부 RCS→WCS (unprocessed는 GET, 나머지 POST) |
| 응답 형태 | `{result, chuteNo}` / `{result:"OK"}` | `ApiResponse {status:"S"|"F", message}` + unprocessed·results는 원시 배열/배열 |
| 상태 축 | `ready`(bool) | 요청 `status` OK/NG(작업 양·불), 응답 `status` S/F(처리 성부) — **두 축 혼동 금지** |

이 차이(특히 **B2B에는 3DS·목적지 판정·IF-08 푸시가 없다**)가 문서 구조로 명확히 드러나야 한다.

## Implementation Scope (Generator가 할 일)

1. **세 문서에 B2B/B2C 구분 기술** — `docs/wcs_rcs_3ds_master_spec.html`(통합 정의서),
   `docs/wcs_rcs_interface.html`(영문), `docs/wcs_rcs_interface_kr.html`(한글).
   문서 내 구분 구조는 아래 **Questions Q1의 사용자 확정 결과**를 따른다(권장안 = Option A).

2. **B2B 인터페이스 5종 기술** — `docs/api-spec-ko.html` §1~6을 **원천(source of truth)**으로 삼아
   필드명·타입·길이·필수여부·검증 규칙·**실패 `message` 문자열**을 정확히 옮긴다:
   - `GET /api/v1/works/unprocessed?bizDay=` — 미작업 차수 조회. **부수효과 명시**(조회 행 ReceiveTime 마킹 = "가져갔다", 미처리 0건 시 자동생성 트리거). **응답은 원시 배열**(`[{bizDay,batch,items:[{barcode,chuteNo,qty}]}]`), 데이터 없으면 `[]`+200(실패 아님).
   - `POST /api/v1/works/input` — 투입 데이터. `pId·inductionNo`=Integer, `status`=OK/NG, `qty` 묶음(생략 시 1), 부족 시 전체 거부.
   - `POST /api/v1/works/classification` — 분류 데이터. 슈트 불일치·이미 분류됨·부족 실패 사유 포함.
   - `POST /api/v1/works/results` — 전체 결과. **요청 바디가 최상위 JSON 배열**임을 명시. 미등록 barcode 하나라도 있으면 전체 거부.
   - `POST /api/v1/works/box` — 박스 마감 데이터. RCS 측 구현 선택적(미구현 시 나머지 4개에 영향 없음)임을 원문대로.
   - **§6 실패 응답 전문**: 모든 실패 `message`(공통 400 검증, 엔드포인트별, 공통 시스템 오류 429/400/500)를 문자열 그대로. `status:"F"`(서버 거부) vs 요청 `status:"NG"`(불량 물품, 수락됨) 구분 설명 유지.

3. **B2C 현행 유지** — IF-05/08/09/10 기존 내용 **훼손 0**. 필요 시 "B2C" 라벨/모드 표기만 부여.
   (B2C 내용을 삭제·재작성·의미변경 금지 — 재배치/라벨링만 허용.)

4. **영문↔한글 정합** — `wcs_rcs_interface.html`(영문)은 `wcs_rcs_interface_kr.html`(한글)과
   **동일 내용의 영문 번역**. 최소한 **신규 B2B 섹션은 EN↔KR 완전 일치**해야 한다.
   (⚠️ 발견된 선행 드리프트 — Questions Q4 참조: 현재 EN 문서의 **B2C** 서술이 KR보다 낡음.)

5. **master_spec에서 B2B의 3DS 무관성 명시** — 통합 정의서(3DS 포함)에 B2B를 넣되,
   **B2B는 §07 PLC 레지스터·§08 핸드셰이크·IF-06/11/12·층 정렬의 대상이 아님**을 문서 구조로 드러낸다
   (예: B2B 파트 선두에 "B2B는 3DS Modbus/핸드셰이크와 무관 — 본 파트는 REST API만 다룸" 명시 박스).

6. **B2B 서버 주소 표기** — `api-spec-ko.html`의 기존값 `192.168.0.150:5205`는 **레거시 원 시스템** 값.
   본 3문서에서는 **배포 환경별(WCS 호스트/포트)** 로 표기하고, B2C 문서의 "Base URL: WCS가 제공"과
   일관되게 둔다. (구체 포트 하드코딩 여부 = Questions Q3.)

## Questions — 사용자 확정 필요 (Phase 1 gate)

> Q1은 **문서 구조 novel 결정**으로 반드시 사용자 확정을 받는다. Q2~Q4는 정본/정합 확인.

**Q1. 문서 내 B2C/B2B 구분 구조 (핵심 gate)**
- **Option A — 대섹션 이분 + 상단 모드 개요 (★ 권장)**: 문서 상단에 "2가지 운영 방식(B2C/B2B)" 개요 표를
  두고, 이후 본문을 **Part I. B2C(현행 전체)** / **Part II. B2B(신규)** 두 대섹션으로 나눈다.
- Option B — 인터페이스별 `[B2C]`/`[B2B]` 모드 태그: 각 API에 모드 배지를 단다.
- Option C — 상단 2모드 개요 + 각 API 태그(A와 B 혼합).
- **권장 = A.** 근거: B2B와 B2C는 **엔드포인트 집합이 완전히 분리**(`/works/*` vs `/destination-query` 등)되고
  공유 인터페이스가 없다. 따라서 "인터페이스별 모드 태그"(B/C)는 한 API에 두 모드가 없어 부자연스럽다.
  대섹션 이분(A)이 B2C 원본을 **손대지 않고 통째로 감싸** "훼손 0"을 지키기에 가장 안전하고,
  상단 모드 개요가 RCS 담당자에게 "어느 방식을 볼지"를 첫 화면에서 안내한다.

**Q2. api-spec ↔ PROGRAM_STRUCTURE 미세 불일치의 정본**
발견된 불일치:

| 항목 | `api-spec-ko.html` (RCS 합의) | `PROGRAM_STRUCTURE.md` (구현) |
|---|---|---|
| barcode 길이 | **20** | ≤100 |
| batch 길이 | **3**(§1~4) / **1~10**(§5 box) — 내부 불일치 | 1~10 |
| reason | "status NG시 필수", 길이 **255** | 항상 선택(optional), ≤500 |
| InTime / SortTime 길이 | **20** | ≤30 |
| qty 상한 | 1~9999 | 9999 (공통) |

- **권장 = `api-spec-ko.html` 정본(변경 금지 원칙).** 이 문서가 RCS에게 이미 전달된 합의 계약이며,
  PROGRAM_STRUCTURE §2.0/§12도 "계약 변경 금지, 구현이 계약을 따른다"를 명시한다. 신규 3문서는
  `api-spec-ko.html`을 그대로 미러링한다(RCS가 보유한 문서와 어긋나지 않게).
- 단, **batch 길이는 api-spec 내부에서 3(§1~4) vs 1~10(§5)로 모순** → **`1~10`으로 통일** 권장(박스 §5 + 구현 일치). 사용자 확정 요청.
- **§6 실패 message 문자열은 무조건 verbatim**(정본 논쟁 대상 아님 — 연동 파손 방지).

**Q3. B2B 서버 주소** — 3문서 B2B 파트의 Base URL을 (a) "배포 환경별(WCS 호스트/포트)" 플레이스홀더로
표기[권장, B2C와 일관], (b) WCS 포트 `5080` 명기, (c) 레거시 `192.168.0.150:5205` 병기 중 무엇으로 할지.
권장 = (a) + 레거시값을 "원 시스템 참고" 각주로.

**Q4. 선행 EN↔KR B2C 드리프트 처리** — 현재 **영문 `wcs_rcs_interface.html`의 B2C 서술이 한글보다 낡음**:
KR은 IF-08 푸시에서 "소터 셀 만재·정지는 push `ready`에 미포함"과 IF-05 FULL/PAUSED의 슈트/소터 분기를
반영했으나 EN은 옛 단순 서술. 이번 스프린트에서 (a) **B2B만 EN↔KR 일치** 보장하고 B2C 드리프트는 그대로 둘지,
(b) 이 김에 **B2C EN도 KR에 맞춰 동기화**할지. 권장 = (b)(항목 4 "EN=KR 번역" 요건 충족 + 저비용).
단 "B2C 훼손 0"의 의미가 "의미 보존하며 최신화"임을 확인.

## Evaluation Criteria (Evaluator 판정 기준 · 정확성이 최우선)

1. **(★★★) B2B 계약 정확성** — 세 문서의 B2B 필드명·타입·길이·필수여부·검증 규칙·**모든 실패 `message`
   문자열**이 `docs/api-spec-ko.html` §1~6과 **일치**. 임의 개변·오탈자·누락 0. (Q2 확정값 기준.)
2. **(★★★) 구조적 명료성** — B2C/B2B가 Q1 확정 구조대로 분리되어 RCS 담당자가 혼동 없이 각 방식을 읽을 수 있음.
   master_spec에서 **B2B의 3DS 무관성**이 구조로 드러남.
3. **(★★) B2C 정합·무훼손** — B2C 내용이 현행 코드(`RcsController.cs` IF-05/09/10, IF-08 푸시 의미)와
   기존 문서 대비 의미 보존(삭제·왜곡 0). B2C diff는 라벨/재배치(+Q4 동기화 시 KR→EN 반영)에 국한.
4. **(★★) 영/한 정합 + 유효 렌더** — 신규 B2B 섹션 EN↔KR 내용 일치. 세 HTML이 브라우저에서 유효하게
   렌더(구조 깨짐·미완 태그·깨진 표 0).

## Completion Conditions (PASS 최소 조건)

- [ ] 세 문서 모두 B2B 5개 인터페이스(unprocessed/input/classification/results/box) + §6 실패 응답이 기술됨.
- [ ] B2B 필드·타입·실패 message가 `api-spec-ko.html`과 정확히 일치(Evaluator가 문서↔원천 대조로 확인).
- [ ] B2C IF-05/08/09/10 내용이 기존 대비 의미 보존(무훼손) — B2C 관련 diff가 라벨링/재배치/Q4-동기화 외 의미변경 없음.
- [ ] master_spec에 "B2B는 3DS 레지스터·핸드셰이크 무관" 명시 존재.
- [ ] 영문·한글 B2B 섹션 내용 일치.
- [ ] 세 `.html` 파일이 유효하게 렌더(브라우저 스크린샷 또는 구조 파싱 확인).
- [ ] **무변경 가드**: `git diff --stat`에서 `backend/`·`frontend/`·`scripts/` 변경 라인 = 0. 변경 파일은 `docs/*.html` 3개(+ tasks/ 산출물)로 한정.
- [ ] **회귀 확인**: `dotnet build backend/Wcs.sln` 성공 + `dotnet test backend/Wcs.sln` 기존 210 테스트 GREEN 유지(문서 변경이 코드에 영향 없음을 확인 — 문서 스프린트라 코드 미변경이 정상).

## 제약 (CLAUDE.md 절대규칙 정합)

- **api-spec-ko.html 변경 금지** — B2B 계약 원천. 삭제·수정 금지, 참조만. (필드명 `pId·agvNo·barcode·inductionNo·chuteNo·qty·timeStamp`는 개명 완료값 — CLAUDE.md 규칙 #6, `loadQty` 아님. B2B의 `pId·inductionNo`=Integer 고정은 lessons "E-4 회귀" 교훈.)
- 코드·스키마·테스트·appsettings 0 변경. Modbus 레지스터 맵 서술 불변.
- 스펙 모호 시 추측 금지 — `docs/SPEC.md` "미확정 사항" 기록 + 사용자 질문(위 Questions).

## Parallel Modules

N/A (single module — 문서 3개는 상호 의존(EN↔KR 정합, master는 두 문서 참조)하며 동일 계약 원천을 공유하므로 순차 단일 Generator가 정합성 유지에 유리. 병렬 분할 시 EN/KR 불일치·구조 편차 위험).

## Evaluation Dimensions

functional only (문서 정확성·정합 단일 차원. 보안/성능 표면 없음).

## Detected Project Type: Full-stack

레포 신호로 판별: `backend/src/Wcs.Api`(서버측 route/controller — `RcsController.cs`) + `frontend/`(브라우저 진입점, 무변경 가드가 참조) 가 한 레포에 공존 → Full-stack. (사용자 요청 어법이 아닌 레포 구조 기준.)

> **표면 투명성 노트 (S-SIM3DS-RTU 선례 준용):** 레포 타입은 Full-stack이나, **이번 스프린트의 실제 변경 표면은 `docs/*.html` 정적 참조 문서 3개뿐**이다. 실행되는 프론트/백엔드 런타임 표면을 건드리지 않으므로, 아래 Full-stack의 **런타임 E2E 슬롯 3종은 N/A(사유 명시)** 로 두고, 이 docs 표면에 맞는 **문서 검증 시나리오**로 대체·구체화한다(사용자 지시 "검증 시나리오를 이 표면에 맞게 채울 것" 준수). 문서 전용 변경이므로 pre-commit 훅의 docs-only 경로가 코드 검증 없이 통과함이 정상.

## Verification Scenarios (per-type — Full-stack)

- **Applicable Web/UI scenarios (frontend surface this sprint touches):**
    N/A — 이번 스프린트는 어떤 프론트엔드 런타임/컴포넌트 표면도 건드리지 않는다(변경은 `docs/*.html` 정적 문서). 프론트 UI 검증 대상 없음.
- **Applicable Backend/API scenarios (backend surface this sprint touches):**
    N/A — 어떤 백엔드 엔드포인트/컨트롤러도 변경하지 않는다. `RcsController.cs` 등 코드는 문서 정합 대조의 **참조 대상**일 뿐 수정 대상 아님.
- **At least one end-to-end data-flow scenario crossing two or more layers:**
    N/A — 런타임 계층 간 데이터 흐름 변경 없음(문서 전용). 대신 아래 문서 검증 시나리오 D6(무변경 가드 + 회귀)가 "코드 계층이 문서 변경에 영향받지 않음"을 실증한다.

### Document Verification Scenarios (이 표면의 필수 검증 — Evaluator 수행, N=6)

- **D1. B2B 계약 대조 (문서 → 원천)**: 세 문서의 B2B 5개 인터페이스 각각에 대해, `api-spec-ko.html` §1~6의
  필드명·타입·길이·필수여부·검증 규칙·실패 message 문자열을 나란히 대조. 불일치 1건이라도 = FAIL.
  증거: 문서별·엔드포인트별 대조 결과(값 인용)를 `sprint-feedback.md`에 기록.
- **D2. §6 실패 message verbatim 검증**: `api-spec-ko.html` §6의 모든 실패 message(공통 400 검증 7종,
  unprocessed 1종, input 2종, classification 4종, results 3종, box 4종, 공통 시스템 429/400/500)가
  세 문서에 문자열 그대로 존재하는지 grep 대조. `status:"F"` vs 요청 `status:"NG"` 구분 설명 존재 확인.
- **D3. B2C 무훼손 검증**: `git diff`로 B2C(IF-05/08/09/10) 관련 변경이 라벨링/재배치/Q4-동기화에
  국한되고 **의미변경·삭제가 없음**을 확인. 표본: IF-08 push `ready` 산출 규칙, IF-05 FULL/PAUSED 슈트/소터
  분기가 `RcsController.QueryDestination` 코드 및 기존 KR 문서와 정합 유지.
- **D4. EN↔KR 정합**: 신규 B2B 섹션이 영문·한글 문서에서 동일 내용(필드 표·엔드포인트·실패 message 구조)임을
  대조. (Q4 확정 시 B2C 동기화 여부도 확인.)
- **D5. master_spec 3DS 무관성**: `wcs_rcs_3ds_master_spec.html`에 B2B가 §07 PLC 레지스터·§08 핸드셰이크·
  IF-06/11/12·층 정렬의 대상이 아님을 명시하는 구조/문구가 존재. B2B 파트가 3DS 섹션과 섞이지 않음.
- **D6. 유효 렌더 + 무변경 가드 + 회귀**: 세 HTML을 브라우저(Playwright)로 열어 스크린샷 — 구조 깨짐/미완
  태그/깨진 표 없음 확인(스크린샷 판독 증거). `git diff --stat`으로 `backend/`·`frontend/`·`scripts/`
  변경 0 확인. `dotnet build` + `dotnet test`(210 GREEN) 회귀 무영향 확인.

> Planner self-check — Detected project type: Full-stack. Required scenario slots: 6 (D1 B2B계약대조, D2 실패message verbatim, D3 B2C무훼손, D4 EN↔KR정합, D5 master 3DS무관성, D6 렌더+무변경가드+회귀). Full-stack 런타임 E2E 3슬롯은 docs-only 표면 사유로 N/A 처리(투명성 노트 명시). All slots filled: yes.

── ★ 사용자 확정 (2026-07-07, Phase 1→2 게이트 — 4문항 전부 권장안) ─────────
Q1 구분 구조: **대섹션 이분** — 상단 "2모드 개요" + [B2C 파트]·[B2B 파트] 대섹션. 기존 B2C 훼손 0.
Q2 스펙 정본: **api-spec-ko.html(RCS 합의) 우선** — barcode≤100·reason≤500 등 느슨한 쪽이 아니라
   api-spec 값 채택. 단 batch는 api-spec 내부 모순(3 vs 1~10)이므로 **1~10으로 통일**.
   §6 실패 message는 **무조건 verbatim**(원문 문자열 그대로).
Q3 서버 주소: **배포 환경별 플레이스홀더** + 레거시(192.168.0.150:5205) 각주.
Q4 영문 B2C: **이번에 동기화** — interface.html의 B2C 서술을 interface_kr.html 최신 내용
   (IF-08 push ready 제외 규칙·IF-05 FULL/PAUSED 슈트/소터 분기)과 일치시킴. "영문=한글" 정합.
실행: 단일 Generator(문서 3종 순차·상호 정합 필요 — 병렬 부적합). Evaluator는 계약 대조·영한 정합·렌더 검증.
