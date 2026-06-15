# 하네스 교차 검증 결과 (SPEC ↔ HTML 확정원본 ↔ 스켈레톤 코드)

검증 방식: 6개 차원 병렬 검사 → 발견마다 독립 검증자가 적대적 반박(인용 정확성·모순 실재성).
원자료: 확정 35건 / 기각 8건. 중복(여러 차원이 같은 문제 발견)을 제거하면 **21개 고유 이슈**.

진실 우선순위(프로젝트 규칙): `docs/*.html 4종(확정 원본)` > `SPEC.md(응축본)` > 코드.
"충돌 시 HTML이 우선" → 아래 불일치의 기본 해소 방향은 모두 **HTML에 맞춤**.

---

## A. 즉시 수정 (HTML 권위에 따른 오류 정정 — 결정 불요)

| # | 심각도 | 이슈 | 위치 | 조치 |
|---|---|---|---|---|
| A1 | CRITICAL | IF-05 요청에 `agvNo` 누락 (HTML 3종·절대규칙 6·SPEC §1 공통필드엔 있음) | SPEC.md:39, Dtos.cs:6 | SPEC §3에 agvNo 추가 / Dtos는 M3 코드 수정 |
| A2 | MAJOR | IF-08에 원본에 없는 `timeStamp`, IF-10에 `qty·timeStamp` 추가 | SPEC.md:43,46, Dtos.cs:9,12 | SPEC §3 원본대로 정정 / DTO는 nullable 선택필드(M3) |
| A3 | MAJOR | IF-08 `allowed=true` 응답 reason — 원본은 `"READY"`, 코드는 null | wcs_rcs_interface_kr.html:171,199 vs Models.cs:58/테스트:102 | API 계층(M3)에서 allowed=true→"READY" 주입, ToWire(None)=null 유지 |
| A4 | MAJOR | CLAUDE.md "12테이블" vs 실제 16테이블 (ERD/TASKS/Entities 모두 16) | CLAUDE.md:27 | 16으로 정정 |
| A5 | MINOR | `wcs_erd.html` 동기 선언했으나 파일 부재(깨진 참조) | ERD.md:1 | 동기 문구 제거, ERD.md를 단일 진실로 명시 |
| A6 | MINOR | "처음엔 전부 RED" 부정확 — Wire_Strings_AreStable은 즉시 GREEN | CLAUDE.md:40 | "Decide 9케이스 RED / Wire 1건 GREEN"로 정정 |
| A7 | MINOR | Dtos IF-05 OK 응답 reason(NORMAL/BUSY/FULL/PAUSED) 주석 누락 | Dtos.cs:7 | 주석 정정(M3) |
| A8 | MINOR | 레지스터 주소 잠정성(현장 확정) 단서 누락 | SPEC.md §1/§7 | §7에 추가 |

## B. Sim3ds 동작 스펙 명확화 (M2 — HTML 타이밍차트와 정합)

| # | 심각도 | 이슈 | 조치 |
|---|---|---|---|
| B1 | MAJOR | 분류 종료 시 무조건 `Ready=1` → 복귀 이동 남으면 Ready=1 블립(차트③ 모순) | SPEC §6: 복귀 이동 남으면 Ready=0 유지하고 곧바로 이동 |
| B2 | MAJOR | 이동 규칙에 '분류 중 아님' 전제 누락 → 분류 중 병행 이동 해석 가능 | SPEC §6: 분류·이동 직렬(분류 종료 후 이동) 명시 |

## C. 판정 테스트 커버리지 갭 (M1 — 테스트=스펙)

| # | 심각도 | 이슈 | 추가할 테스트 |
|---|---|---|---|
| C1 | MAJOR | 행1 경계: 이동완료 후 `TgtFloor≠0`인데 층일치·Ready=1 → allowed=true 미검증 | `Snap(true,cur:1,tgt:1),agv:1 → Allowed, WriteTgtFloor=false` |
| C2 | MAJOR | Hold/Offline이 행4 선기입(쓰기) 경로를 차단하는지 미검증 (Row6·7 입력이 전부 ready=true) | `Snap(false,cur:2,tgt:0)+Full/Paused/Offline → 쓰기 없음` |
| C3 | MINOR | Hold 우선순위 강한 경계(층일치·Ready=1인데 Hold로 거부) 미검증 | `Snap(true,cur:1,tgt:0)+Full → false·Full·쓰기없음` |

## D. 스펙 누락 — RCS/3DS 협의 필요 (SPEC §7 등재 후 확정)

| # | 심각도 | 이슈 |
|---|---|---|
| D1 | MAJOR | R_Flag 타임아웃 **초과 시** 동작(알람·상태 재확인·sorter_command=TIMEOUT) 미정의 |
| D2 | MAJOR | IF-05 "NG여도 투입 기록 남김"(IF-16 통합) 규칙 누락 — piece status=DENIED 삽입 경로 명문화 |
| D3 | MINOR | agvFloor 출처 — 원본은 'agvNo의 층으로 판정' 확정인데 SPEC은 '요청필드 vs 매핑' 미확정으로 재개봉 |
| D4 | MINOR | TgtFloor 잔류(이동만 완료·투입 없이 이탈) 해소 경로 부재 → 타 층 영구 WRONG_FLOOR |
| D5 | MINOR | C_Flag=1 대기 타임아웃·알람 미정의(R쪽과 비대칭, 무한 대기 가능) |
| D6 | MINOR | IF-05 NG 시 chuteNo `null 포함` vs `생략` 직렬화 정책 모호 |
| D7 | MINOR | agvFloor 매핑 이중 진실(appsettings vs ERD agv.floor) — M4 전환 규칙 부재 |
| D8 | MINOR | 통합 시퀀스에 IF-12 라벨 미표기(무명 단계) |

## 기각된 8건 (적대적 검증에서 모순 아님으로 판정)
- 투입~분류시작 Ready 미정의 / Sim R_Flag==0 확인 누락 / IF-11 트리거 / 시퀀스 동기묘사 vs 캐시아키텍처
  / '예약 차감' 용어 / SetTgtFloor 재확인 소스 / ClearR 순서 / Directory.Build.props 부재
  (일부는 검증자가 세션 한도로 미완 — 추후 재확인 가능)
