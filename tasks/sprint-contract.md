# Sprint Contract — S-RCS-DOCS-B2B-POLISH

> Branch: `docs/rcs-interface-b2b-b2c` · Base: `develop` · PR #36 미병합 — **같은 브랜치에 이어 작업**
> 작성: Planner Subagent · 2026-07-07 · (이전 계약 S-RCS-DOCS-B2B는 커밋 완료되어 본 계약이 덮어씀)

## Goal

RCS 담당자에게 제공하는 **WCS↔RCS 인터페이스 정의 문서 3종**에서, 앞선 스프린트(PR #36)로
추가된 **B2B 파트(Part II)를 B2C 파트(Part I)와 같은 "정리 문법"으로 재구성**해 한 문서로서
일관되게 읽히게 한다. 사용자 요청(2026-07-07): *"기존 B2C 인터페이스 문서는 정리가 잘 되어
있는데 추가된 B2B는 뭔가 통일도 안 되고 잘 안 읽힌다. B2C랑 비슷하게 정리해줘."*

- **표현·구조·레이아웃·넘버링만 개선하는 스프린트다.** B2B **와이어 계약(엔드포인트·필드명·타입·
  길이·검증규칙·실패 message·골든패스 순서 등) 정보 손실 0**. B2C 파트(Part I)는 **무접촉**.
  코드/스키마/테스트/설정은 0 변경. 산출물은 `docs/*.html` 3개 파일뿐이다.

## 배경 — 현재 B2B가 "정리가 안 된" 지점 (실제 문서 정독으로 식별)

세 문서(`wcs_rcs_interface_kr.html` 정본 · `wcs_rcs_interface.html` 영문 · `wcs_rcs_3ds_master_spec.html`
통합)는 **모두 동일한 B2B 구조**를 공유하며, 아래 6개 결함도 **세 문서에 공통**으로 존재한다.

**B2C 파트(Part I)의 "정리가 잘 된" 문법 = 재구성의 기준틀:**

| B2C 섹션 | 역할 | 특징 |
|---|---|---|
| `§1 개요 & 역할` | 산문 + 원칙 bullet | 방식의 본질을 먼저 서술 |
| `§2 통신` | 2열 표(항목/내용) | 프로토콜·방향·메서드·인코딩·타임아웃·인증·Base URL |
| `§3 데이터 모델/필드` | **공통 필드 표**(필드/타입/설명) | 모든 IF가 공유하는 필드를 **한 번만** 정의 |
| `§4 인터페이스 목록` | 표(IF/이름/방향/요약) | 전체 조망을 **독립 섹션**으로 |
| `§5 API 정의` | IF별 h3(.ifid) + **일관된 틀** | 제안 엔드포인트 → 요청/응답 표 → 예시 JSON → note |
| `§6 사유 코드` | IF별 사유 표 + note | |
| `§7 전체 플로우` | .flow 다이어그램 + ol 단계 | 운영 순서를 **독립 섹션**으로 |
| `§8 타이밍 & 재시도` | 표 | 타이밍/재시도를 **한 곳에 모음** |

> master_spec의 B2C는 `§00~§10`(두 자리 chip)로 넘버링되며 3DS 섹션(§07 레지스터·§08 핸드셰이크)을
> 포함한다. 즉 "B2C 문법"의 **넘버링 형식이 문서마다 다르다**(interface=§1~§8, master=§00~§10).
> B2B 재구성은 **각 문서 자신의 B2C 형식**에 맞춰야 한다.

**B2B 파트(Part II) 현재 구조와 6개 정리 결함:**

| # | 결함 | 현재 상태 | B2C 대비 |
|---|---|---|---|
| **F1** | **넘버링 이질감** | B2B = `B1/B2/B3` 3개뿐 | B2C의 연속 §1~§8과 이질적 (Q1 게이트) |
| **F2** | **①~⑤ 내부 모순** | B1 개요표·골든패스는 `unprocessed①→input②→classification③→box④→results⑤`, 그러나 **B2 정의부·B3 실패부는 `…→results④→box⑤`** (물리 순서·배지 숫자 상충) | 골든패스 순서는 **불변(locked)**인데 정의부가 어긋남 (Q2 게이트) |
| **F3** | **공통 필드 표 부재** | `bizDay/batch/chuteNo/status/reason/qty` 정의가 **엔드포인트마다 반복**(5개 표에 산재) | B2C §3처럼 **한 번만** 정의하는 공통 표가 없음 |
| **F4** | **엔드포인트별 틀 불일치** | `unprocessed`는 요청/응답 2표, 나머지 4개 POST는 요청 표 + 산문 "응답:" 줄 | B2C §5는 IF마다 **동일한 요청/응답 틀** |
| **F5** | **B1 과적재** | B1이 개요+통신+데이터모델note+인터페이스목록표+골든패스를 **한 섹션에** 담음. 타이밍/재시도는 B1 통신표·골든패스표·B3 말미 3곳에 흩어짐 | B2C는 이를 §2·§3·§4·§7·§8로 **분리** |
| **F6** | **note 스타일 난립** | B1에 `.note`/`.note.b2b` 박스 4개(3DS없음·상태2축·batch override·chuteNo정규화)가 연속 적재 + 회색 레거시주소 문단 | B2C는 note를 절제해 배치 |

목표: 위 6개를 B2C 문법으로 정돈해 **B2B Part II가 B2C Part I과 같은 리듬으로 읽히게** 한다.

## Implementation Scope (Generator가 할 일)

> **원칙**: 아래는 전부 **재배치·재라벨·넘버링·표틀 통일**이다. 필드·타입·길이·검증규칙·실패 message·
> 골든패스 순서 등 **계약 내용은 한 글자도 바꾸지 않는다**. 이동만 하고, 삭제·의미변경·신규 계약 0.

1. **[F1 · Q1] B2B 넘버링을 B2C 섹션 문법에 1:1 대응** — Q1 사용자 확정 결과를 따른다.
   권장안(=Q1 Option A): **B-prefix 유지하되 B2C §1~§8을 그대로 반영하는 `B1~B8` 연속 서브넘버링**으로 확장.
   (Part I/Part II 경계 보존 + B2C 무접촉 + master_spec까지 세 문서 통일 — 근거는 Q1 참조.)

2. **[F3] 공통 데이터 모델/필드 섹션 신설**(B2C §3 대응, Q3 확정 시) — 반복되는 공통 필드
   (`bizDay` 8~10 · `batch` 1~10 · `chuteNo` String·zero-pad · `status`/`reason` · `qty` 1~9999 ·
   `pId`/`inductionNo`=RCS 부여 Integer)를 **공통 표로 1회 정의**. 엔드포인트별 변형(예: `box`의
   `chuteNo` 1~10 · `barcode` 1~100, unprocessed/input/classification/results의 `barcode` 20)은
   공통 표 아래 각주 또는 해당 엔드포인트 표에 **명시적으로 보존**(정보 손실 0). B2C §5처럼, 개별
   엔드포인트 표는 공통 필드를 참조하고 **엔드포인트 고유·변형 필드만** 강조하는 방향.

3. **[F5] B1 분해 + 인터페이스 목록·골든패스 독립 섹션화** — 현재 B1에 뭉쳐 있는 것을 B2C 대응
   위치로 분리: 통신 표(→ §2 대응), 데이터모델 note(→ 신설 데이터모델 섹션), 엔드포인트 목록 표
   (→ §4 대응 독립 섹션), 골든 패스 flow+표(→ §7 대응 독립 섹션). B1은 B2C §1처럼 **개요/본질 차이**에 집중.

4. **[F4] 엔드포인트별 요청/응답 틀 통일**(B2C §5 대응) — 5개 엔드포인트 모두 **동일한 시각 틀**:
   제안 엔드포인트 → 요청 표 → 응답 표(또는 일관된 응답 스펙) → 예시 JSON → note. 특히 POST 4종의
   산문 "응답:" 줄을 `unprocessed`와 균질한 형태로 정리(응답 `{status:"S"|"F", message}` 스펙을
   표/일관 블록으로). 예시 JSON·필드 값은 api-spec-ko.html과 동일하게 유지.

5. **[F2 · Q2] ①~⑤ 넘버링 일원화** — 골든패스 순서가 **불변**이고 "results는 항상 마지막"이 최근
   정정된 운영 진실이므로, **B2 정의부·B3 실패부의 물리 순서와 배지 숫자를 골든패스와 일치**시킨다:
   `①unprocessed ②input ③classification ④box ⑤results`. (개요표·골든패스·정의부·실패부가 **한 넘버링**으로 정합.)
   ⚠️ **표시 순서·배지 숫자만 변경**이며 각 엔드포인트의 필드·message 내용은 불변. api-spec-ko.html은
   `results(4)→box(5)` 순이나 이는 참조 원천의 섹션 순서일 뿐 계약값이 아니다(Q2 참조).

6. **[F5] 타이밍·재시도 통합 섹션**(B2C §8 대응) — 레이트리밋(429)·POST 재시도 규칙·`unprocessed`
   비멱등 경고·골든패스 실패시행동을 **한 섹션으로 통합**. 흩어진 3곳의 중복을 정리하되 각 지침 내용 보존.

7. **[F6] note 스타일 통일** — `.note.b2b`(teal)는 Part II 시각 정체성으로 **유지**하되, B1 연속 적재를
   해소(데이터모델 note는 데이터모델 섹션으로, 상태 2축·chuteNo 정규화·batch override note도 해당
   섹션으로 이동). B2C의 절제된 note 배치 리듬에 맞춘다.

8. **세 문서 동시 적용 + EN=KR=master 정합** — B2B 재구성은 **한 번 설계해 세 문서에 동일 적용**.
   정본=`wcs_rcs_interface_kr.html`(한글) → `wcs_rcs_interface.html`(동일 내용 영문 번역) →
   `wcs_rcs_3ds_master_spec.html`(각 문서 B2C 넘버링 형식에 맞춰 반영, B2B의 **3DS 무관성** 문구 유지)
   순으로 전파. B2B 파트 내용은 세 문서에서 (언어·문서별 상호참조 문구 제외) **동일**해야 한다.

## Questions — 사용자 확정 필요 (Phase 1 gate)

> Q1·Q2는 **문서 정체성에 영향을 주는 novel 결정**으로 사용자 확정을 받는다. Q3는 재량 확인.

**Q1. B2B 섹션 넘버링 체계 (핵심 gate)**
- **Option A — B-prefix 유지 + B2C 문법으로 확장 (★ 권장)**: `B1/B2/B3`를 B2C §1~§8과 1:1
  대응하는 `B1~B8`(개요 / 통신 / 데이터모델 / 인터페이스목록 / API정의 / 실패응답·사유 / 골든패스 /
  타이밍·재시도)로 확장. **Part I/Part II 경계 보존**, **B2C 완전 무접촉**(§1~§8·§00~§10 그대로),
  세 문서(interface §1~§8 · master §00~§10)에서 **B-prefix가 유일하게 형식 통일을 이룸**.
- Option B — B2C와 연속 번호(interface `§9~`, master `§11~`): Part 경계가 흐려지고, **문서마다 시작
  번호가 달라져** 오히려 통일성이 낮아진다.
- Option C — Part별 독립 번호이되 동일 서식(예: `II-1`~`II-8`): A와 유사하나 B-prefix가 더 짧고 기존
  앵커(`#b2b-*`)와 자연스럽다.
- **권장 = A.** 근거: B2B 엔드포인트는 B2C의 `IF-05` 같은 고정 ID가 없어 연속 §번호(B)가 Part 경계를
  지운다. B-prefix는 (1) B2C를 한 글자도 안 건드리고 (2) 세 문서의 서로 다른 B2C 넘버링 위에서도
  일관되며 (3) "Part II 전용 번호대"임을 시각적으로 즉시 전달한다.

**Q2. ①~⑤ 엔드포인트 순서/배지 일원화**
현재 **문서 내부 모순**: 개요표·골든패스는 `box④→results⑤`, 정의부(B2)·실패부(B3)는 `results④→box⑤`.
- **권장 = 골든패스 기준으로 통일** — 정의부·실패부를 `①unprocessed ②input ③classification ④box ⑤results`로
  재배치(배지 숫자 포함). 근거: (1) 골든패스 순서는 **불변 가드**, (2) "results 항상 마지막"은 최근
  정정된 운영 진실(커밋 48884c4), (3) 현 모순 제거. **표시 순서·숫자만 바뀌며 필드·message 내용 불변.**
- 대안 = 배지 숫자를 떼고 엔드포인트명만 사용(B2C의 고정 IF-ID 방식과 유사). 순서 모순은 남으므로 비권장.
- ⚠️ api-spec-ko.html은 `results(§4)→box(§5)` 순이나 **참조 원천의 섹션 순서일 뿐 계약값이 아님** —
  필드·타입·length·message는 그대로 보존되므로 이 재배치는 계약 무변경이다.

**Q3. 공통 데이터 모델/필드 표 신설 (재량)**
- **권장 = 신설**(Implementation Scope 2). B2C §3와 대칭을 이뤄 "산재"(F3) 결함을 가장 크게 해소.
- 단, `box`의 `chuteNo`(1~10)·`barcode`(1~100)와 다른 엔드포인트(`chuteNo` 3 · `barcode` 20)의 **길이
  차이**, `status`/`reason`이 input/classification에만 존재하는 점 때문에 공통 표는 "진짜 공통 필드"만
  담고 **변형은 각 엔드포인트 표/각주로 명시**해야 한다(무손실 조건). 이 세분화 정도는 Generator 재량.

## Evaluation Criteria (Evaluator 판정 기준 · 구조 일관성 + 무손실이 최우선)

1. **(★★★) 구조 일관성** — B2B Part II가 B2C Part I과 **같은 정리 문법**으로 재구성됨: Q1 넘버링 체계
   준수, 엔드포인트별 요청/응답 틀 균질(F4), 공통 필드 표 존재(F3), 인터페이스 목록·골든패스·타이밍이
   독립 섹션(F5), ①~⑤ 넘버링 무모순(F2), note 스타일 정돈(F6). **정성 체크리스트(D1)로 판정**.
2. **(★★★) 계약 정보 보존(무손실)** — 재구성 후에도 B2B 5개 엔드포인트의 필드명·타입·길이·필수여부·
   검증규칙·**실패 message 전량(약 19종)**·골든패스 순서·`pId`/`inductionNo` 출처·`chuteNo` zero-pad·
   `bizDay` 8~10·`batch` 1~10이 `docs/api-spec-ko.html` §1~6과 **일치**(이동은 됐어도 손실·개변 0).
3. **(★★) B2C 무접촉** — Part I(§1~§8 / §00~§10) 관련 `git diff` = **0**(단 Q1이 B2C 넘버링에 영향을
   준다면 그 최소 범위는 계약 명시대로). IF-05/08/09/10 서술 훼손 0.
4. **(★★) EN=KR=master 정합 + 유효 렌더** — 세 문서의 B2B 파트가 **동일 재구성**을 반영(언어/상호참조
   문구 제외 내용 일치). 세 HTML이 브라우저에서 유효 렌더(구조 깨짐·미완 태그·깨진 표 0). 210 테스트 GREEN 불변.

## Completion Conditions (PASS 최소 조건)

- [ ] 세 문서의 B2B Part II가 Q1 확정 넘버링 체계로 B2C 섹션 문법에 대응(개요/통신/데이터모델/인터페이스목록/API정의/실패응답/골든패스/타이밍).
- [ ] 공통 필드 표 존재(Q3 확정 시) — 반복 필드가 1회 정의로 통합되고 엔드포인트별 변형은 무손실 보존.
- [ ] 5개 엔드포인트가 동일한 요청/응답 표 틀로 기술(F4 해소).
- [ ] ①~⑤ 넘버링이 개요표·골든패스·정의부·실패부에서 **무모순**(F2 해소), 골든패스 순서 불변.
- [ ] B2B 필드·타입·길이·검증규칙·실패 message 전량이 `api-spec-ko.html`과 정확히 일치(Evaluator 대조).
- [ ] B2C Part I diff = 0(또는 Q1 확정 최소 범위). master_spec의 "B2B는 3DS 무관" 명시 유지.
- [ ] 영문·한글·master B2B 파트 재구성 내용 일치.
- [ ] 세 `.html`이 유효하게 렌더(브라우저 스크린샷 또는 구조 파싱 확인).
- [ ] **무변경 가드**: `git diff --stat`에서 `backend/`·`frontend/`·`scripts/` 변경 라인 = 0. 변경 파일은 `docs/*.html` 3개(+ `tasks/` 산출물)로 한정. `api-spec-ko.html` 변경 0.
- [ ] **회귀 확인**: `dotnet build backend/Wcs.sln` 성공 + `dotnet test backend/Wcs.sln` 기존 210 테스트 GREEN 유지(문서 변경이 코드에 영향 없음).

## 제약 (CLAUDE.md 절대규칙 정합)

- **api-spec-ko.html 변경 금지** — B2B 계약 원천. 참조만. 필드명 `pId·agvNo·barcode·inductionNo·chuteNo·qty·timeStamp`는 개명 완료값(규칙 #6, `loadQty` 아님). `pId`·`inductionNo`=RCS 부여 Integer 고정(lessons E-4 회귀 교훈).
- **계약 내용 0 변경** — 재구성은 표현·구조·레이아웃·넘버링 개선에 국한. 필드·타입·길이·검증규칙·실패 message·골든패스 순서 손실/개변 0.
- **B2C 파트 무접촉** — 이미 정리된 Part I은 구조 참조만, 변경 0(Q1 확정 최소 범위 예외).
- 코드·스키마·테스트·appsettings 0 변경. Modbus 레지스터 맵 서술 불변. 3DS 무관성(master) 유지.
- 스펙 모호 시 추측 금지 — `docs/SPEC.md` "미확정 사항" 기록 + 사용자 질문(위 Questions).

## Parallel Modules

N/A (single module — B2B 재구성은 **한 번 설계해 세 문서에 동일 적용**해야 하며(EN=KR=master 정합),
정본 KR → EN → master 순차 전파가 정합성 유지에 유리. 병렬 분할 시 세 문서 구조 편차·EN/KR 불일치 위험).

## Evaluation Dimensions

functional only (문서 구조 일관성·계약 무손실 단일 차원. 보안/성능 표면 없음).

## Detected Project Type: Full-stack

레포 신호로 판별: `backend/src/Wcs.Api`(서버측 route/controller — `RcsController.cs`) + `frontend/`
(브라우저 진입점, 무변경 가드가 참조)가 한 레포에 공존 → Full-stack. (사용자 요청 어법이 아닌 레포 구조 기준.)

> **표면 투명성 노트 (S-RCS-DOCS-B2B / S-SIM3DS-RTU 선례 준용):** 레포 타입은 Full-stack이나,
> **이번 스프린트의 실제 변경 표면은 `docs/*.html` 정적 참조 문서 3개뿐**이며 그마저도 **B2B 파트의
> 재배치·재라벨(계약 무변경)**이다. 실행되는 프론트/백엔드 런타임 표면을 건드리지 않으므로 Full-stack의
> **런타임 E2E 슬롯 3종은 N/A(사유 명시)**로 두고, 이 docs 표면에 맞는 **문서 검증 시나리오(D1~D6)**로
> 대체·구체화한다. 문서 전용 변경이므로 pre-commit 훅의 docs-only 경로가 코드 검증 없이 통과함이 정상.

## Verification Scenarios (per-type — Full-stack)

- **Applicable Web/UI scenarios (frontend surface this sprint touches):**
    N/A — 이번 스프린트는 어떤 프론트엔드 런타임/컴포넌트 표면도 건드리지 않는다(변경은 `docs/*.html` 정적 문서). 프론트 UI 검증 대상 없음.
- **Applicable Backend/API scenarios (backend surface this sprint touches):**
    N/A — 어떤 백엔드 엔드포인트/컨트롤러도 변경하지 않는다. `RcsController.cs` 등 코드는 계약 무손실 대조의 **참조 대상**일 뿐 수정 대상 아님.
- **At least one end-to-end data-flow scenario crossing two or more layers:**
    N/A — 런타임 계층 간 데이터 흐름 변경 없음(문서 전용). 대신 D6(무변경 가드 + 회귀)가 "코드 계층이 문서 변경에 영향받지 않음"을 실증한다.

### Document Verification Scenarios (이 표면의 필수 검증 — Evaluator 수행, N=6)

- **D1. 구조 일관성 체크리스트 (B2B Part II ↔ B2C Part I)**: 세 문서 각각에 대해 아래를 **B2C 대비**로
  판정하고 결과를 `sprint-feedback.md`에 기록. 항목: ①Q1 넘버링 체계 준수 ②공통 필드 표 존재(F3)
  ③5개 엔드포인트의 요청/응답 표 틀 균질(F4) ④인터페이스 목록·골든패스·타이밍이 독립 섹션(F5)
  ⑤①~⑤ 넘버링 무모순(F2) ⑥note 스타일 정돈(F6). 한 항목이라도 미충족 = FAIL.
- **D2. 계약 무손실 대조 (문서 → 원천 api-spec-ko.html §1~6)**: 재구성 후 세 문서의 B2B 5개
  엔드포인트 필드명·타입·길이·필수여부·검증규칙을 원천과 **나란히 대조**. 공통 표로 이동한 필드가
  값 손실·개변 없이 보존됐는지, 엔드포인트별 변형(box chuteNo 1~10 등)이 유지됐는지 확인. 불일치 1건 = FAIL.
  증거: 문서별·엔드포인트별 대조 결과(값 인용) 기록.
- **D3. 실패 message verbatim (약 19종) grep 대조**: `api-spec-ko.html` §6의 모든 실패 message
  (공통 검증 7 · unprocessed 1 · input 2 · classification 4 · results 3 · box 4 · 시스템 429/400/500)가
  재구성 후에도 세 문서에 **문자열 그대로** 존재하는지 grep. `status:"F"` vs 요청 `status:"NG"` 구분 설명 유지 확인.
- **D4. ①~⑤ 넘버링·골든패스 정합 (문서 내부)**: 각 문서에서 개요 엔드포인트 표·골든패스 flow·B2
  정의부 배지·B3 실패부 배지의 순서/숫자가 **모두 일치**함을 확인(F2 해소). 골든패스 순서
  `unprocessed→input→classification→box→results` 및 "box 선택 · results 항상 마지막" 보존.
- **D5. EN=KR=master 정합**: 세 문서의 B2B 파트가 **동일 재구성**(섹션 구성·표 틀·공통필드·넘버링)을
  반영하는지 대조(언어·상호참조 문구 차이 제외). master_spec에 B2B가 §07 PLC 레지스터·§08 핸드셰이크·
  IF-06/11/12·층 정렬의 대상이 아님을 명시하는 구조/문구 **유지** 확인.
- **D6. B2C 무접촉 + 유효 렌더 + 무변경 가드 + 회귀**: `git diff`로 B2C(Part I §1~§8 / §00~§10 및
  IF-05/08/09/10) 변경 = 0 확인(Q1 확정 최소 범위 예외 시 그 범위 명시). 세 HTML을 브라우저(Playwright)로
  열어 스크린샷 — 구조 깨짐/미완 태그/깨진 표 없음(스크린샷 판독 증거). `git diff --stat`으로
  `backend/`·`frontend/`·`scripts/`·`api-spec-ko.html` 변경 0 확인. `dotnet build` + `dotnet test`(210 GREEN) 무영향 확인.

> Planner self-check — Detected project type: Full-stack. Required scenario slots: 6 (D1 구조일관성체크리스트, D2 계약무손실대조, D3 실패message verbatim, D4 ①~⑤넘버링·골든패스정합, D5 EN=KR=master정합, D6 B2C무접촉+렌더+무변경가드+회귀). Full-stack 런타임 E2E 3슬롯은 docs-only(재배치·무계약변경) 표면 사유로 N/A 처리(투명성 노트 명시). All slots filled: yes.

── ★ 사용자 확정 (2026-07-07, Phase 1→2 게이트 — 3문항 전부 권장안) ─────────
Q1 넘버링: **B1~B8로 확장** — B 접두사 유지, B2C §1~§8 구조 미러(개요/통신/공통필드/인터페이스목록/API정의/사유·실패/플로우/타이밍 — B2C 항목에 맞춰 매핑). 3문서 각각 자기 B2C 서식 따름(master는 §00~10 계열이나 B2B는 B체계로 일관).
Q2 ①~⑤ 정합: **골든패스 순서(unprocessed→input→classification→box→results)로 B2 정의·B3 실패 섹션 재배치**. 배치 순서·뱃지 숫자만, 필드·message 내용 불변. 문서 내 순서 모순(F2) 완전 해소.
Q3 공통 필드표: **신설** — B2C §3 대응 공통 필드표(bizDay/batch/chuteNo/status/reason/qty). box 길이 변형(chuteNo 1~10·barcode 1~100)은 각주 보존(정보 손실 0).
실행: 단일 Generator(KR 정본→EN→master 순차·상호 정합). Evaluator functional + 문서리뷰(수신자 관점) 재활용 가능.
