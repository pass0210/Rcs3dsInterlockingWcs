# Sprint Feedback — S-RCS-DOCS-B2B-POLISH

**APPROVED** — Evaluator, 2026-07-07 (1 iteration to pass).
B2B Part II를 B2C Part I "정리 문법"으로 재구성(B1~B8 확장·공통필드표 신설·5종 균질틀·순서 일원화·note 정돈). 검증 D1~D6 전항 PASS, fresh evidence. 계약 정보 손실 0·B2C 무접촉·210 GREEN.
브랜치 `docs/rcs-interface-b2b-b2c`(읽기 전용 — 커밋/수정/브랜치전환 없음). 핸드오프 마커: `tasks/sprint-log.md:2344` `## IMPLEMENTATION COMPLETE (B2B-POLISH)`.
변경 파일: `docs/{wcs_rcs_interface_kr, wcs_rcs_interface, wcs_rcs_3ds_master_spec}.html` (+ `tasks/sprint-log.md`). 원천 = `docs/api-spec-ko.html` §1~6. 무변경 가드 대상(backend/frontend/scripts/api-spec-ko.html) diff 0.

---

## D2. 계약 정보 보존 (문서 → 원천 api-spec-ko.html §1~5) — PASS  ★핵심

공통 필드표(B3) + 엔드포인트별 고유표(B5) + 각주 5개를 **합쳐** 엔드포인트별로 원천과 대조. 원래 필드 누락 0.

- **공통 필드표(B3) 9필드** = `bizDay`(String 8~10 Y)·`batch`(String 1~10 Y)·`chuteNo`(String 3¹ Y, zero-pad)·`barcode`(String 20¹ Y²)·`qty`(Integer N³ 1~9999)·`status`(String 2 Y⁴ OK/NG)·`reason`(String 255 N⁴)·`pId`(Integer Y⁴)·`inductionNo`(Integer Y⁵). 3문서 byte-identical(KR 324~344 / EN 341~362 / MS 326~347).
- **각주 5개(변형 무손실 보존)**: ¹box `chuteNo` **1~10**·`items[].barcode` **1~100**(그 외 3·20) · ²`barcode` 필수여부 = input **N(선택)** / classification·unprocessed·results·box **Y** · ³`unprocessed` 응답 `items[].qty` = **Y "1 이상"** / 그 외 N·1~9999 · ⁴`status`·`reason`·`pId` = input·classification 한정 · ⁵`inductionNo` = input 한정. → **box chuteNo 1~10·barcode 1~100 보존 ✓, input barcode=N 보존 ✓, unprocessed qty=Y 보존 ✓.**
- **엔드포인트별 재구성 대조(공통표+고유표+각주 합산)**:
  - ① unprocessed(§1): 요청 bizDay + 응답 items[].barcode 20 Y·chuteNo 3 Y·qty **Integer Y "1 이상"** — 전부 present.
  - ② input(§2): bizDay·batch·inductionNo·chuteNo·pId·barcode(**N**)·status·reason·**inTime(고유 String 20 Y)**·qty + 응답 status/message — 10 요청필드 present.
  - ③ classification(§3): bizDay·batch·chuteNo·pId·barcode(Y)·status·reason·**sortTime(고유 String 20 Y)**·qty — present. inductionNo 부재(원천 정합) ✓.
  - ④ box(§5): boxNo 1~50·chuteNo **1~10**·items(최소1)·barcode **1~100**·qty·endTime 1~50 N — present.
  - ⑤ results(§4): items·barcode 20 Y·chuteNo 3 Y·qty Integer N — present.
- **pId·inductionNo = RCS 부여 Integer 보존**(lessons E-4 회귀 교훈): B3 비고에 "RCS가 부여하는 정수·unprocessed 응답에 없어 RCS가 채워 보냄·서버 검증 없이 저장" 명시(3문서).
- **이번 스프린트가 값을 개변했는가? 아니오 — 이동만.** `git show HEAD:` 대조로 `bizDay 8~10`·`batch 1~10`·override note는 **HEAD(PR #36)에 이미 엔드포인트마다 반복 존재**했고, 본 스프린트는 이를 공통표로 **통합**(F3)했을 뿐. 제거(-) 라인 전수 검토 = 구 per-endpoint 필드표 값(String 20·3·255·Integer·box 1~10/1~100 등)이 모두 신 B3/B5/각주에 재출현 → **불일치 0**. `batch 1~10`/`bizDay 8~10` 확장은 계약(Impl Scope 2)이 명시한 공통값이며 `.note.b2b "상충 시 본 문서가 우선"`이 의도 문서화(선재).

## D3. 실패 message verbatim (19종) grep 대조 — PASS

`grep -Fq`(fixed-string — `{}()[]'.` 메타문자 회피) 19개 고유 문자열을 4파일 대조:
- **KR·EN·MS 각 found=19 missing=0**, api-spec-ko.html 원천도 19/19. 카탈로그 = 공통400 7 · unprocessed 1 · input 2 · classification 4 · results 3 · box 4 · 시스템 429/400/500(중복 제거 후 고유 19). 치환 자리 `{Field}/{N}/{M}/{barcode}/{list}/{chuteNo}/{value}/{id}` 원문 보존.
- **`status:"F"`(서버 거부) vs 요청 `status:"NG"`(불량·수락됨) 구분 note** 3문서 B6 선두 존재(KR 481 등). "HTTP 코드 아닌 status 필드로 분기" 명시 유지.

## D4. B1~B8 넘버링 + ①~⑤ 순서·골든패스 정합 — PASS (F1·F2 해소)

- **F1 — B1~B8 연속 넘버링**: 개요/통신/데이터모델/인터페이스목록/API정의/실패응답/플로우/타이밍 = B1~B8이 3문서에 존재(KR 294~581 / EN 311~589 / MS 296~583). B 접두사 유지·Part 경계 보존(Q1 확정 Option A).
- **F2 — ①~⑤ 무모순(핵심)**: **B4·B5·B6·B7 전부 `①unprocessed ②input ③classification ④box ⑤results`**(3문서 각 4개 지점 일치). 재구성 전 정의부·실패부가 `results④/box⑤`였던 **문서 내 모순 소멸**(HEAD 대조로 확인). 골든패스 flow 다이어그램(B7)도 `unprocessed→input→classification→box→results` — "box 선택 · results 항상 마지막" 보존.
  - ⚠ api-spec-ko.html 물리 섹션순은 `results(§4)→box(§5)`이나 계약(Q2)이 "참조 원천 섹션순일 뿐 계약값 아님"으로 명시 — 재배치는 표시순·배지숫자만, 필드·message 내용 불변. 계약 정합.

## D1. 구조 일관성 체크리스트 (B2B Part II ↔ B2C Part I) — PASS (3문서 전부 6/6)

- **①Q1 넘버링 준수**: B1~B8 = B2C §1~§8 미러(위 D4).
- **②공통 필드표 존재(F3)**: B3에 9필드 1회 정의 + 각주로 변형 보존(D2). B2C §3 대칭.
- **③5 엔드포인트 요청/응답 틀 균질(F4)**: B5 5종 모두 "공통필드 참조줄 → 고유·변형 필드표 → 예시 JSON → 응답 스펙" 동일 틀. POST 4종 응답을 `{status:"S"|"F", message}`(String 1 / String 100) 일관 블록으로 통일(구 산문 "응답:" 줄 균질화). unprocessed는 원시 배열 특성상 응답표 유지 — GET/POST 차이는 필연적·틀은 일관.
- **④인터페이스목록·골든패스·타이밍 독립 섹션(F5)**: B4(목록)·B7(골든패스 flow+표)·B8(타이밍·재시도) = 구 B1 뭉침에서 분리·독립 섹션화. B2C §4·§7·§8 대응.
- **⑤①~⑤ 무모순(F2)**: 위 D4.
- **⑥note 정돈(F6)**: chuteNo정규화·상태2축·batch override note → B3, 3DS무관 note → B1로 배치. `.note.b2b`(teal) Part II 정체성 유지. 구 B1 연속 4박스 적재 해소.

## D5. EN=KR=master 정합 — PASS

- **동일 재구성**: 3문서 diff `+214` 라인 동일(numstat 대칭). B1~B8 구성·B3 공통표(9필드)·각주 5개·B5 5종 틀·①~⑤ 순서 모두 일치. D2 필드값·D3 19 message 3문서 공통.
- **EN 번역 정합**: B3 common table(Field/Type/Length/Req./Description) + footnotes 1~5 + "This document wins on conflict" note가 KR과 1:1 대응(EN 341~362). B5 endpoint 틀·JSON 예시·응답 스펙 동일. box④ results⑤.
- **master 고유 보존**: B2B **3DS 무관성** 명시 다중 유지 — 2모드 개요 note(MS 139 `.note.b2b`)·B1 선두 note(MS 305: "§07 PLC 레지스터·§08 핸드셰이크·IF-06/11/12·층 정렬·IF-08 푸시·목적지 판정의 대상이 전혀 아니다")·Part II 배너 sub(MS 293)·비교표 "3DS 연동 없음" 행(MS 134). §00~§10(§07 레지스터·§08 핸드셰이크 포함) B2C 섹션은 무접촉(D6). master는 IF-06/11/12·층 정렬을 B2C 소관으로 명확 귀속.

## D6. B2C 무접촉 + 유효 렌더 + 무변경 가드 + 회귀 — PASS

- **B2C 무접촉(제거라인 전수 회계)**: Part II 배너 앞 영역을 HEAD vs 작업본 diff → **3문서 모두 유일 변경 = TOC B2B 링크(3개 → 8개, `#b2b-ovw/#b2b-if/#b2b-fail` → `#b2b-1~#b2b-8`)**. 계약이 명시 허용한 예외("TOC B2B 링크 갱신 제외"). §1~§8 / §00~§10 / IF-05·08·09·10 / Part I 배너 **byte-identical**(diff 0).
- **유효 렌더**: 독립 `html.parser` 태그밸런스 3문서 전부 **leftover_open=[] errors=[]**, table/tr/td/th/section/div/pre/h2/h3/ul/li **imbalance 0**. `<script>` 0 → JS 콘솔/pageerror 발생 불가. (COM1 실 PLC 물리 제약으로 서버 미기동 — 정적 docs라 파서 단정이 계약 sanction 경로. S-RCS-DOCS-B2B 선례 준용.)
- **무변경 가드**: `git diff --stat HEAD -- backend/ frontend/ scripts/ docs/api-spec-ko.html` = **빈 출력**. 전체 diff = `docs/*.html` 3개(+ tasks/sprint-log.md).
- **회귀**: `dotnet test backend/Wcs.sln` → **통과! 실패:0 통과:210 건너뜀:0 전체:210** (1회, exit 0, 16s). 문서 변경 코드 무영향 실증. (NU1903 SQLite transitive 경고는 선재 부채·본 스프린트 무관.)

---

## Minor (APPROVED 비차단 — 다음 스프린트 Generator 참고)

- **master B5 엔드포인트 배지 스타일 불일치(순수 cosmetic)**: KR/EN B5는 `<span class="ifid">①</span>`(칩 배경/보더)인데 master B5는 `<span style="font-family:var(--mono);font-weight:700">①</span>` 인라인 스타일(칩 없음). master B2C의 IF 배지(`.ifid` 칩)와도 시각 편차. 넘버링·계약·구조엔 무영향(mono bold로 동일 렌더). sprint-log가 "master 고유 inline 배지 style"로 의도 기록. 통일하면 3문서 배지 리듬 일치. **비차단.**

## 검증 방법 메모

- fresh evidence 전량 자체 생성: `grep -F` verbatim 19종×4파일, `git diff HEAD` B2C영역·무변경가드, `git show HEAD:` 값 선재성 대조, `html.parser` 밸런스 3문서, `dotnet test` 210 GREEN 1회, KR/EN/master B2B 섹션 전문 판독.
- COM1 실 PLC 물리 제약 준수 — API/Sim 미기동(정적 문서 작업). dotnet test는 인메모리 SQLite 더블. 잔류 프로세스 0(임시 파서/메시지 파일은 scratchpad 한정·리포 산출물 0).

## Code Review / 문서리뷰 Minor (S-RCS-DOCS-B2B-POLISH, 병합 비차단·todo)
- [해소·fix iter2] 공통 필드표 "필수" 열 엔드포인트별 상이 오해 → △ 마커 + ※각주로 3문서 통일. chuteNo 정규화 표 행/note 중복 정리.
- [잔여·todo] 레이트리밋(IP당 분당 300) B2/B6/B8 3중 하드 기재 — 값 동기화 위험. B8 단일 출처+참조화(verbatim message 훼손 없이) 후속.
- [잔여·todo] B2 통신 표에 요청 타임아웃 행 부재(B2C §2엔 3s) — pre-existing·비회귀. 값 협의 시 B2에 1줄 추가로 B2C 대칭.
- [잔여·cosmetic] master B5 엔드포인트 배지 인라인 mono-bold(KR/EN은 .ifid 칩) — 계약·구조 무영향.
