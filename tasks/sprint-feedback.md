# Sprint Feedback — S-RCS-DOCS-B2B

**APPROVED** — Evaluator, 2026-07-07 (1 iteration to pass).
WCS↔RCS 인터페이스 3문서 B2C/B2B 이분 기술. 검증 시나리오 D1~D6 전항 + Q1~Q4 확정 준수 전부 PASS, fresh evidence.
브랜치 `docs/rcs-interface-b2b-b2c`. 검증자는 코드 수정/커밋/브랜치 전환 없이 독립 재실행·문서↔원천 대조·코드 판독만 수행.
핸드오프 마커 확인: `tasks/sprint-log.md:2266` `## IMPLEMENTATION COMPLETE (Generator, 2026-07-07)`.
변경 파일: `docs/{wcs_rcs_3ds_master_spec,wcs_rcs_interface,wcs_rcs_interface_kr}.html` (+ tasks/sprint-log.md). 원천 = `docs/api-spec-ko.html` §1~6.

---

## D1. B2B 계약 대조 (문서 → 원천 api-spec-ko.html §1~5) — PASS

프로그램 추출로 3문서 B2B 인터페이스 섹션(`id="b2b-if"`)의 (필드,타입,길이,필수) 튜플 40행 파싱·대조.
- **KR=EN=MS 완전 동일** (byte-identical 튜플 40행, `KR==EN: True`, `KR==MS: True`).
- 40행 전부 api-spec-ko.html §1~5 값과 일치. Q2 확정대로 **batch만 1~10 통일**(api-spec 내부 §1~4=3 vs §5=1~10 모순 해소) — 5개 batch 행 전부 `1~10`(KR/EN/MS 각 5). 나머지 api-spec 값 채택 확인:
  - `barcode` §1~4 = **20**, `reason` = **255**, `inTime`/`sortTime` = **20** (PROGRAM_STRUCTURE의 느슨한 ≤100/≤500/≤30 아님).
  - `pId`·`inductionNo` = **Integer** (lessons E-4 회귀 교훈 준수).
  - 필수여부 미세 차이 정확: input `barcode`=N(선택)·results `qty`=N vs classification `barcode`=Y·unprocessed `qty`=Y — api-spec 그대로.
  - box §5 고유값: `bizDay` 8~10 · `boxNo` 1~50 · `chuteNo` 1~10 · `items[].barcode` 1~100 · `endTime` 1~50 N — api-spec §5 정확 미러링.
- raw: `KR: 40 field rows / EN: 40 / MS: 40 · KR==EN==MS True · batch len=1~10 ×15(3문서×5)`.

## D2. §6 실패 message verbatim 검증 — PASS

`grep -Fq`(fixed-string) 19개 실패 message 문자열을 3문서에 각각 대조:
- **3문서 각각 "ALL 19 PRESENT (verbatim)"** — 누락 0. 카탈로그: 공통400 7종·unprocessed 1·input 2·classification 4·results 3·box 4 + 시스템 429/400/500(중복 제거 후 고유 19문자열). `{Field}/{N}/{M}/{barcode}/{list}/{chuteNo}/{value}/{id}` 런타임 치환 자리 원문 보존.
- **`status:"F"`(서버 거부) vs 요청 `status:"NG"`(불량 물품·수락됨) 구분 설명** 3문서 전부 존재(KR:455 · EN:472 · MS:458 note). "HTTP 코드 아닌 status 필드로 분기" 명시도 3문서 존재.
- raw 출력: 3 docs 각각 `ALL 19 PRESENT (verbatim)`.

## D3. B2C 무훼손 검증 — PASS

`git diff HEAD`의 **제거(-) 라인 전수**를 문서별로 캡처해 B2C 변경이 라벨/재배치/Q4-동기화/div-fix에만 국한됨을 입증(제거 라인 = B2C 수정 전량 회계):
- **KR(정본)**: 제거 3줄 = 헤더 소개문 + TOC `#s1` + 푸터. **B2C 본문(§1~§8) 의미변경 0**.
- **EN**: 제거 7줄 = 헤더/TOC/푸터(3) + **Q4 동기화 4개소**(§3 `ready` 필드 · §5 IF-08 문단 · §6 IF-05 FULL/PAUSED 행 · §6 IF-08 ready=false 행). Q4 확정("의미 보존하며 최신화")이 명시 승인한 변경 — 낡은 EN을 KR 최신 내용에 맞춘 것(삭제·왜곡 아님).
- **MS**: 제거 4줄 = 헤더/TOC/푸터(3) + **§06 note div-fix 1**(제거=미완 note, 추가=동일 텍스트+`</div>` — 텍스트 100% 동일).
- **B2C 사유코드 전수 보존** 확인: NORMAL·BUSY·FULL/PAUSED·OVER·COMPLETED·NO_DEST·OFFLINE 3문서 전부 존재. IF-05/08/09/10 플로우 보존.
- **코드 정합(표본)**: 문서의 IF-05 FULL/PAUSED 슈트/소터 분기·OFFLINE 규칙이 `RcsController.cs`와 일치 — 슈트 full/paused→OK(L77·L88), 소터 `if (r.Paused) return DestinationBlock.Paused`(L93), 셀 못받음→`DestinationBlock.Full`(L96-98), OFFLINE은 IF-05 dispatch 비차단. IF-08 push ready에서 소터 셀 만재·정지 제외 규칙도 문서·코드 정합.

## D4. EN↔KR(및 MS) 정합 — PASS

- **신규 B2B 섹션 완전 일치**: D1의 40행 튜플 `KR==EN==MS True` + D2의 19 message가 3문서 동일. 엔드포인트·응답 래퍼·two-axis 설명 구조 동일.
- **Q4 B2C 동기화**: EN의 낡은 4개소를 KR 최신과 일치시킴(IF-08 push ready에서 소터 셀 만재·정지 제외 · IF-05 FULL/PAUSED 슈트 OK/소터 NG 분기 · §6 IF-08 표에 소터 운영상태 BUSY/OFFLINE 행 추가). EN §6 IF-08 표 3행 = KR 3행 대응.

## D5. master_spec B2B 3DS 무관성 명시 — PASS

`wcs_rcs_3ds_master_spec.html`에 B2B의 3DS 무관성이 **구조로** 드러남:
- **2모드 개요 note**(L134, `.note.b2b`): "Part II(B2B)는 §07 PLC 레지스터·§08 핸드셰이크·IF-06/11/12·층 정렬의 대상이 아니다 … Modbus·핸드셰이크·목적지 판정·IF-08 상태 푸시가 없다".
- **B1 선두 note**(L294): "B2B는 3DS Modbus/핸드셰이크와 무관 — 본 파트는 REST API만 다룬다 … §07 PLC 레지스터(C/R/Ready·D0~D6)·§08 핸드셰이크·IF-06(상태 조회)·IF-11(셀 지정)·IF-12(적재 완료)·층 정렬(2층 고정)·IF-08 상태 푸시·목적지 판정의 대상이 전혀 아니다".
- §00~§10(B2C·3DS)은 Part I 배너로 감싸 라벨링만, B2B(Part II)와 물리적으로 분리. 개요 표 "3DS 연동" 행이 B2C=있음 / B2B=없음 대조.

## D6. 유효 렌더 + 무변경 가드 + 회귀 — PASS

- **HTML 유효성**(브라우저 미가용 → 계약 sanction된 파서 단정, "문서 파일이라 서버 불요"): `html.parser` 태그 밸런스 3문서 전부 **errors=0 · leftover_open=[]**. 테이블 th/td 셀 밸런스 imbalanced=0(KR 26·EN 26·MS 24 테이블). 문서에 `<script>` 0 → JS 콘솔/pageerror 발생 불가. 외부 CDN 폰트(pretendard)는 `--sans` 시스템 폰트 폴백으로 graceful degradation. master_spec §06 선재 미완 `</div>`는 **HEAD에 실재**(`git show HEAD:…` L191-192에서 `</section>` 앞 note 미닫힘 확인) → 텍스트 무변경으로 닫음(well-formedness만 개선).
- **무변경 가드**: `git diff --stat HEAD -- backend/ frontend/ scripts/` = **빈 출력**. 전체 diff = `docs/*.html` 3개(+tasks/sprint-log.md)뿐. 코드·스키마·스크립트·appsettings 0줄.
- **회귀**: `dotnet test backend/Wcs.sln` → **통과! 실패:0 통과:210 건너뜀:0 전체:210** (1회 실행, exit 0). 문서 변경이 코드에 무영향 실증. (NU1903 SQLite transitive 경고는 선재 부채·본 스프린트 무관.)

---

## Q1~Q4 사용자 확정 준수 — PASS

- **Q1 대섹션 이분**: 3문서 전부 상단 `section#modes`(2모드 7행 비교표) + `div.part.b2c`(Part I 배너) + `div.part.b2b`(Part II 배너) + B2B 3섹션(B1 개요·통신 / B2 인터페이스 5종 / B3 실패 응답). B2B 5개 인터페이스(unprocessed GET+부수효과 명시 · input · classification · results 최상위 배열 · box 선택) 존재. 요청 status OK/NG vs 응답 status S/F 두 축 구분 서술 3문서 존재.
- **Q2 정본**: api-spec-ko.html 값 채택 + batch만 1~10 통일 + §6 verbatim (D1·D2에서 입증).
- **Q3 서버 주소**: Base URL `http://<WCS_HOST>:5080`(배포 환경별 플레이스홀더) + 레거시 `192.168.0.150:5205` 각주 — 3문서 각 1개소.
- **Q4 EN B2C 동기화**: D3·D4에서 입증.

## Minor (APPROVED 비차단 — 다음 스프린트 Generator 참고)

- 없음. 계약 IN 전항·확정 4건·무변경 가드·회귀 전부 충족. 문서 정확성 결함 0.

## 검증 방법 메모

- fresh evidence 전량 자체 생성: 필드 튜플 프로그램 추출·대조, `grep -F` verbatim 19종×3문서, `git diff HEAD` 제거라인 전수, `git show HEAD` div-fix 선재성, `html.parser` 밸런스, `dotnet test` 210 GREEN 1회 실행, 컨트롤러 코드 판독.
- COM1 실 PLC 물리 제약 준수 — API/Sim 미기동(문서 작업). dotnet test는 인메모리 더블. 검증 후 잔류 프로세스 0(Wcs.Api/Sim3ds none).

---

## FIX ITER 2 재검증 (Evaluator, 2026-07-07 — 최상단 APPROVED 유지)

문서 리뷰 C1·I1~I7·Minor 보완(fix iteration 2) 후 delta 재검증. **회귀 0 + fix 항목 사실·계약 정합** 전항 PASS → **위 APPROVED 판정 유지**. 브랜치 `docs/rcs-interface-b2b-b2c` 읽기 전용(커밋/수정 없음). 전량 fresh evidence 자체 생성.

### (A) 회귀 0 — 이전 통과분 유지 (fresh 재확인)

- **실패 message 19종 verbatim**: `grep -Fq` 3문서 각 **19/19 present, MISSING 없음**. 카탈로그 = api-spec-ko.html §6 원천(공통400 7·unprocessed 1·input 2·classification 4·results 3·box 4·시스템 429/400/500, 중복 제거 후 고유 19). 치환 자리 `{Field}/{N}/{M}/{barcode}/{list}/{chuteNo}/{value}/{id}` 원문 보존.
- **B2B 필드 튜플 3문서 동일 + api-spec 정합**: b2b-if 섹션 (field,type,length,req) **40행 파서 추출 → KR==EN==MS True**. 40행 전부 api-spec-ko.html §1~5 값 일치(chuteNo 3자리·barcode 20·reason 255·inTime/sortTime 20·box boxNo 1~50/chuteNo 1~10/items barcode 1~100). 필수여부 미세차 보존(input barcode=N vs classification barcode=Y, unprocessed items[].qty=Y vs input/classification/results qty=N). bizDay 8~10·batch 1~10은 I3/I4 승인된 의도적 확장(회귀 아님).
- **B2C 사유코드 8종·플로우 보존**: NORMAL·BUSY·FULL·PAUSED·OVER·COMPLETED·NO_DEST·OFFLINE **3문서 전부 존재**. IF-05/08/09/10 서술 유지.
- **EN=KR=MS 정합**: 40행 튜플 == True + 19 message 3문서 공통. fix 항목(C1·I1~I7)이 EN(영문)·MS(국문)에서 KR과 동일 내용으로 반영.
- **HTML 유효**: `html.parser` 태그 밸런스 3문서 전부 **errors=0 · leftover_open=[] · stray=[]**. `<script>` 0(콘솔/pageerror 발생 불가).
- **무변경 가드**: `git diff --stat -- backend/ frontend/ scripts/` = **빈 출력**. 변경 = `docs/*.html` 3개(+tasks 산출물)뿐. numstat: KR +310/-2 · EN +315/-6 · MS +311/-3.
- **회귀 테스트**: `dotnet test backend/Wcs.sln` → **통과! 실패:0 통과:210 건너뜀:0 전체:210**(1회, exit 0). 문서 변경 코드 무영향 실증.
- **B2C 무훼손(제거라인 전수 회계)**: 3문서 총 제거 11줄 = 헤더 소개문 재배치(3) + 푸터 재배치(3) + **EN Q4 동기화 4개소**(§3 ready 필드·§5 IF-08 문단·§6 IF-05 FULL/PAUSED 행·§6 IF-08 ready=false 행 — Q4 승인) + **MS IF-09 도착 note div-close fix 1**(제거=미완 `</div>` note, 추가=**텍스트 byte-identical + `</div>`**만 — 기존 sanction된 well-formedness 수정 클래스, 의미변경·삭제 0). B2C 본문 의미 손실 0.

### (B) fix 항목 사실·계약 정합

- **C1 (PASS·원천 대조)**: 3문서 input(inductionNo·pId)·classification(pId) 필드표 비고에 "**RCS가 부여하는 정수 식별자·unprocessed 응답에 없어 RCS가 채워 보냄·서버는 검증 없이 저장**" 명시(KR 375·377·405 / EN 392·394·422 / MS 378·380·408) + 2모드 축 note 브리지 문장(KR 306/EN 323/MS 309). **원천 대조**: PROGRAM_STRUCTURE.md §2.2.1(`InductionNo` int "검증 없음(암묵 필수, 기본 0)"·`PId` int "검증 없음"·step3 `Pid=PId.ToString()`, `EquipmentNo=InductionNo.ToString()`)·§2.2.2(`PId` int "검증 없음(RCS 계약 유지)"·`Pid=PId.ToString()`) — Generator 주장 정확. unprocessed 응답 §2.2.4 = `[{bizDay,batch,items:[{barcode,chuteNo,qty}]}]`(pId/inductionNo 부재) 확인 → "RCS가 채움" 정합. **원천에 없는 유일성 규칙 창작 0**(B2B pId 비고에 uniqueness 없음). ※ B2C §데이터모델 pId "고유 ID·범위 1-30000·초기화 협의" 라인은 **HEAD 선재 B2C 콘텐츠**(git show HEAD: EN 136·KR 119, EN=KR)로 이번 fix 미접촉 — C1(B2B) 범위 밖·결함 아님.
- **I1 (PASS)**: 2모드 개요에 (1)라벨 정의("연동 방식(통신 계약) 라벨") (2)선택기준("라이브 3D 소터 실시간 틸트=B2C / 사전 등록 배치 데이터 소비=B2B") (3)운영경로("B2B도 정식 운영 경로 — 테스트 전용 아님") — KR 108/EN 125/MS 126.
- **I2 (PASS)**: B1에 B2B 골든 패스(unprocessed→input→classification→results→box) flow + 단계별 목적·실패 시 재시도 가부 표 — KR 318~334/EN 335~350/MS 존재.
- **I3 (PASS)**: bizDay 8→**8~10** 통일 — b2b-if 내 6행 전부 8~10(3문서). 구현 정규식 `^\d{8}$ 또는 ^\d{4}-\d{2}-\d{2}$`와 정합(오히려 원천 §1~4의 단일 8보다 정확).
- **I4 (PASS)**: "상충 시 본 문서 우선" note — batch 1~10·bizDay 8~10은 의도적 확장, api-spec-ko.html 해당 길이는 역사적 참조로 격하(KR 307/EN 324/MS 310). 원천값(batch §1~4=3, bizDay §1~4=8) 실측 대조로 "넓힘" 서술 정확.
- **I5 (PASS)**: chuteNo 3자리 zero-pad 전 엔드포인트 통일 + classification "Chute mismatch"는 패딩 후 비교 명확화(KR 308/EN 325/MS 311).
- **I6 (PASS)**: unprocessed GET **⚠ 비멱등 경고**(굵게) — 응답 유실 시 단순 재시도 금지·각 호출이 행 소비(KR 342 부수효과 note + 골든패스 329 / EN 346·564 / MS 345).
- **I7 (PASS)**: B3에 타임아웃·재시도 지침(unprocessed 재시도 금지 / POST별 이중소비 주의 / 429 지수 백오프 / 500 불확실) — KR 545~550/EN 562~567/MS 548.
- **Minor (PASS·전부 반영)**: M1 TOC PART I·B2C / PART II·B2B 구분자 span(3문서 각 2회) · M2 results 다중 (bizDay,batch) 그룹 허용(KR 426) · M3 box 송신 주체="박스 분류 설비·RCS 선택 구현"(KR 449) · M4 자동생성을 RCS 관측("빈 배열/생성 데이터 수신 가능")으로 재기술(KR 342) · M5 톤 분화 — master_spec 강한 3DS 무관 note 유지(MS 104·137, D5)/interface(KR·EN) note는 "PLC 상세는 마스터 소관"으로 완화 · M6 TOC 앵커 #b2b→**#b2b-ovw**(3문서 각 1) + 구 `href="#b2b"` **0건**.
- **신규 결함 유입 0**: fix 서술이 api-spec/코드와 모순되거나 B2C를 훼손하지 않음. C1의 B2B pId 무검증 서술과 B2C pId 고유 서술은 별개 운영모드의 별개 계약(모순 아님).

### 판정
회귀 0(19 message·40 튜플·8 사유코드·EN=KR=MS·HTML 유효·210 GREEN·무변경 가드·B2C 무훼손) + fix 항목(C1·I1~I7·M1~M6) 사실·계약 정합 전항 충족, 신규 결함 0. **최상단 APPROVED 판정 유지.**

### 검증 산물 정리
- 임시 파서 스크립트(`/tmp/htmlcheck.py`·`/tmp/tuples.py`) 리포 외 위치·삭제. API/Sim 미기동(COM1 실 PLC 준수). 잔류 프로세스 0.
