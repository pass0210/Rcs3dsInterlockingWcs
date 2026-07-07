# Sprint Feedback — S-FIELD-20CELLS

**APPROVED** (2026-07-07, 1 iteration) — Evaluator 독립 검증, fresh evidence.

브랜치 `feat/field-20cells`. A안(기존 `cell.Enabled` 게이트 재사용 — 백엔드 프로덕션 코드 변경 0, 회귀 테스트만 추가) 확정 계약. 모든 Verification Scenario·Completion Condition을 직접 생성한 신선 증거로 통과. 커밋/push 없음(팀리드 권한). 핸드오프 마커 확인: `tasks/sprint-log.md:2149` `## IMPLEMENTATION COMPLETE — S-FIELD-20CELLS`.

---

## Scenario 1 — 전체 테스트 ×2 (독립 재실행) : PASS

`dotnet test backend/Wcs.sln` 2회 독립 실행 — Generator 보고(178) 재확인:
- RUN 1 (raw): `통과!  - 실패: 0, 통과: 178, 건너뜀: 0, 전체: 178, 기간: 17 s - Wcs.Tests.dll (net10.0)`
- RUN 2 (raw): `통과!  - 실패: 0, 통과: 178, 건너뜀: 0, 전체: 178, 기간: 15 s - Wcs.Tests.dll (net10.0)`
- 기준 카운트: 계약 175(=#31 병합 후) + 신규 회귀 3건 = **178** 정합. 하드코딩 아님(실측 재확인).
- 빌드 경고 = NU1903(SQLitePCLRaw 2.1.10, GHSA-2m69) 5건 — base 선재 부채·todo 등재분(이 스프린트 무관, 백엔드 코드 diff 0으로 도입 불가). 오류 0.

## Scenario 2 — 실 DB 검증 (Rcs3dsInterlockingWcs @ localhost, sqlcmd -E) : PASS

읽기 전용 검증 쿼리 + 멱등 재확인 목적 시드 1회 재실행(계약 허용). 원문 인용:
- **cell**: total=20, Enabled=1 = `1,2,3,4,5,6,7,8,9,10,11,12,13,14,15`(정확히 15), Enabled=0 = `16,17,18,19,20`(정확히 5). Capacity=3 × 20행.
- **활성 cell_assignment(ReleasedAt IS NULL)**: 15건 = 셀 `1~15`. 셀 16 배정 해제됨(active에 미포함).
- **released 이력 보존**: released 배정 10건(어제 현장 셀 1~9 해제분 9 + 셀16 해제 1), assign_total=25(원본 16 + 시드 1~9 활성 재수립 9). Generator 특기사항("적용 전 활성 7건[셀10~16]→시드가 1~9 활성 재삽입으로 15건 재수립") 정합, released 이력 소실 0.
- **wcs_order**: `0701-CELL-16 = CANCELLED`, `01~15 = RUNNING`(15건) 무손상.
- **order_item 16 보존**: barcode `0701-CELL-16`, PlannedQty=3, ReservedQty=0, SortedQty=0 (1행 잔존, 이력 보존). 배치 order_item 총 16.
- **현장 실 데이터 무접촉**: piece=2 / piece_event=2 / sorter_command=2 (piece pId `908,909` = 어제 현장 셀8·9 분류 이력). 시드 SQL은 이 테이블을 참조조차 하지 않음(SQL 전문 판독 확인).
- **사전 백업**: `cell_bak_field20`·`cell_assignment_bak_field20`·`wcs_order_bak_field20`·`order_item_bak_field20` 4개 잔존(롤백 안전망).
- **멱등**: 시드 1회 재실행 → 요약 출력 `20 15 5 15 1` 불변. 재확인 쿼리 전량 불변(cells20/en15/dis5, active15, assign_total 25→25 중복삽입 0, order_item 16, order16 CANCELLED, piece/event/cmd 2/2/2). NOT EXISTS/MERGE/조건부 UPDATE로 재실행 안정.

## Scenario 3 — 백엔드 게이트 회귀 (코드 판독 + 실행) : PASS

`backend/tests/Wcs.Tests/Field20CellsGateTests.cs` 직접 판독 — 느슨한 단정·mock 과다 아님:
- **T1** `SelectCell_NeverReturnsDisabledCells_16to20`: 15가용 전부 점유·16~20 Enabled=0 미배정 구성 후 `UnoccupiedCellCount(enabledOnly:true)==0` & `(false)==5`로 "16~20은 물리적으로 비었지만 게이트 밖"을 명시 단정 → `SelectCell` null + 5회 반복 ≤15. **음성 대조**(셀16 Enabled=1 승격 시 SelectCell이 16 반환)로 "게이트=Enabled"를 역증명 — 공허 단정 배제의 정석.
- **T2** `If05_Cells01And15_Ok_Cell16_Ng`: 실 HTTP `POST /api/v1/destination-query`(RcsPushWebApplicationFactory). CELL-01/-15 → OK+chuteNo, CELL-16 → NG+null, 그리고 piece_event `Reason=NO_DEST` 단정(CANCELLED 오더 → 1~15 점유 무관 결정적 NG).
- **T3** `All15EnabledCellsFull_SorterFull_And_If05_Ng`: 15가용 전부 Cap=3 도달(COMPLETED sorter_command) 후 `Compute().Full==true`(비활성 16~20을 빈 셀로 세지 않음 — 세었다면 false) + 신규 바코드 IF-05 NG, piece_event `Reason=FULL`.
- 실행(fresh, isolated): `dotnet test --filter Field20CellsGateTests` → `실패: 0, 통과: 3, 전체: 3, 기간: 4 s`. 프로그래매틱 SQLite 더블 + 실 DI(ICellSelector/IDestinationStatusService)·실 HTTP 사용, 단정은 정확한 셀번호·사유 문자열(NO_DEST/FULL)·카운트로 특정.

## Scenario 4 — 프론트 브라우저 검증 (Playwright, 실 브라우저) : PASS

백엔드(:5080, Production·SqlServer 실 DB) + frontend dev(:5173) 기동 후 Chromium(rev1228, playwright-core) 구동. 스크린샷 `screenshots/S-FIELD-20CELLS_20260707-100852/`(gitignored) 01~04 + console.log 보존. 육안 판독 완료:
- `/monitor` → "분류 현황" 탭 클릭 → 소터 드롭다운 자동 `3DS #01 (오프라인)` 선택(소터 OFFLINE이어도 셀 그리드는 DB 데이터라 렌더).
- **20타일 5열×4행**: `grid-cols-5` 존재(1개), computed `grid-template-columns = 190px×5`(=5열), 타일 20개, distinct row top-offset 4개(`249,346,442,539`)=4행.
- **16~20 회색 "비활성"**: 비활성 배지 타일 정확히 5개 = 셀 `16,17,18,19,20`, 전부 opacity 0.6(`opacity-60`). 셀 1~15는 비활성 아님(점유, op 1.0). 스크린샷 02에서 하단 행 16~20 회색·"미배정" 육안 확인. 셀8·9는 1/3(현장 piece 908/909, 하단 sorter_command COMPLETED 2건과 정합).
- **좁은 폭(<640px) 가로 스크롤**: viewport 500px에서 `overflow-x-auto` 컨테이너 scrollWidth=552 > clientWidth=234 → 5열 유지·가로 스크롤. 우측 스크롤 스크린샷 04로 5열 도달성 확인(타일 뭉개짐 0).
- **콘솔 캡처(BLOCKING)**: console.log 3줄 전부 무해 — `[vite] connecting/connected`(debug) + React DevTools 안내(info). `pageerror` 0, `console.error` 0, React dev-mode warning(key/validateDOMNesting 등) 0, network 4xx/5xx 0.
- **다크 모드**: 테마/다크 토글 부재(0개) → N/A(단일 테마 디자인 토큰). Evaluator 실제 확인 후 N/A 판정.

## Scenario 5 — tsc·eslint (frontend, 독립 실행) : PASS

- `npm run typecheck`(tsc --noEmit) → exit 0, 0 error.
- `npm run lint`(eslint .) → exit 0, 0 error.
- 포매터(prettier 등)는 미구성(package.json scripts에 없음) → `not configured` 기록. tsc+eslint가 구성된 정적 검사이며 둘 다 clean.

## Scenario 6 — 무변경 가드 (git diff 판독) : PASS

- `git diff HEAD -- backend/src` → **빈 출력**(A안 프로덕션 코드 diff 0). Wcs.Core·PlcGateway·Api 무변경.
- 변경 범위(`git status --short`): `scripts/seed-field-16cells.sql → seed-field-20cells.sql`(rename+rewrite, staged R + working M) · `frontend/src/pages/sections/SortingSection.tsx`(그리드 1블록) · 신규 `backend/tests/Wcs.Tests/Field20CellsGateTests.cs` · `tasks/*`. 계약 "scripts/·frontend SortingSection·신규 테스트·tasks 국한"과 정확히 일치.
- SortingSection diff = `grid-cols-2 sm/lg/xl…` → `overflow-x-auto` 컨테이너 안 `grid-cols-5 gap-2 min-w-[600px]`(8줄, DTO/타입 무변경). 검증이 tracked 트리를 변경하지 않음(검증 후 git status 동일, 로그/wwwroot는 gitignored).

## Scenario 7 — 동시성/사각 교훈 (시드 SQL 직접 판독) : PASS

`scripts/seed-field-20cells.sql`(234줄) 전문 판독:
- **트랜잭션 안전**: `SET XACT_ABORT ON` + 단일 `BEGIN TRANSACTION`/`COMMIT`. 셀 MERGE·오더/아이템/배정 INSERT·셀16 전이가 원자 적용.
- **멱등 로직**: 셀=MERGE(MATCHED 조건부 UPDATE + NOT MATCHED INSERT), 오더/아이템/배정=`INSERT … WHERE NOT EXISTS`(부분유니크 ReleasedAt IS NULL 준수), 셀16 배정 해제=`UPDATE … WHERE ReleasedAt IS NULL`, 오더16 CANCELLED=`UPDATE … WHERE Status<>'CANCELLED'`. 결함 0(재실행 카운트 불변으로 실증).
- **실적/예약 무조작**: order_item은 INSERT(NOT EXISTS)만, ReservedQty/SortedQty 미변경. piece/piece_event/sorter_command 미참조. fail-loud(`THROW 50001` — chuteNo=1이 SORTER_3D 아니면 중단). filtered index용 `QUOTED_IDENTIFIER/ANSI_NULLS ON`.
- **백업 테이블 존재**: 4개 확인(위 Scenario 2). 앱 기동 시드 벡터 없음(base·Development 둘 다 `SeedOnStartup=false`, launchSettings 부재로 `dotnet run` 기본 Production — 실 DB 자동 시드 차단, 2026-07-03 사고 교훈 준수).

---

## Minor (비차단 — 다음 스프린트 Generator 참고, todo 아님)
- 좁은 폭(500px)에서 페이지 body가 13px 가로 오버플로(scrollWidth 513 vs clientWidth 500). 셀 그리드는 `overflow-x-auto`로 완전 격리돼 원인 아님(원인은 하단 sorter_command DataGrid 8열 테이블 또는 레이아웃 패딩 — 선재·스코프 밖). 이 스프린트의 5열 그리드 요건은 컨테이너 내부 스크롤로 충족. 극단 좁은 폭에서 상단 타이틀 세로 래핑은 앱 기존 반응형 동작.
- 프론트 포매터 미구성 — 향후 prettier 도입 고려 가능(선택).

## 정리 (검증 후)
- 기동 백엔드(:5080)·프론트(:5173) 종료 완료. 포트 5080/5173/1502 **ALL FREE**. 고아 프로세스 0(chrome-headless/Wcs.Api/Wcs.Sim3ds 부재).
- DB 최종 상태 클린(20/15/5/15/1, piece/event/cmd 2/2/2 — 라이브 IF-05 미발동으로 런타임 산물 0). 백업 테이블은 롤백 안전망으로 잔존(팀리드 확인 후 DROP 가능).
- 스크린샷·console.log는 `screenshots/`(gitignored)에 보존. tracked 트리 무변경.

## Code Review Minor (4-Tier Step 4.5 — S-FIELD-20CELLS, 병합 비차단·다음 스프린트 Generator 참조)

1. **매핑 확장 주석 부정확** — seed-field-20cells.sql:27 "§4~6의 nums 범위" → 실제는 §4 하드코딩 VALUES 수동 확장 + @availMax=20(§5·6 자동 연동). 주석 정정 권장(확장 시 오독 방지, 1순위).
2. **테스트 PlannedQty=100 vs 시드 3 불일치** — Field20CellsGateTests.cs:87,124. OVER 격리 의도로 보이나 주석 없음·헤더 "동형" 표현과 충돌. 사유 주석 or 3으로 정렬.
3. **T1 5회 반복 루프 데드 브랜치** — :196-200. 첫 null 후 상태 불변이라 반복 무의미(무해). 정리 여지.
4. **LoadCellQty pId 41000대가 같은 파일 "1~30000" 주석과 표면 불일치** — :286. 직접 삽입이라 동작엔 무관 — 범위 하향 or 우회 명시 주석.
5. **grid-cols-5 전역 고정 트레이드오프** — SortingSection.tsx:76. 현장 소터(4×5)에 정확하나 셀 수가 5의 배수 아닌 소터엔 오도 가능. 요건 부합으로 수용 — 다중 소터 지형 시 셀 수 기반 열 도출 고려.

리뷰어 권고: cells_enabled_15 검증 컬럼 술어에 Capacity 혼입 — 진단 의미 분리 여지(선택). stale 파일명 참조는 역사 기록이라 조치 불요.
