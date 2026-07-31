# Sprint Feedback — S-AUDIT-C-DATA-INTEGRITY

## APPROVED (2026-07-31, iteration 1 · 2차원 Evaluator pool aggregate)

전 차원 PASS (AND). 상세 partitioned 증거: tasks/sprint-feedback/functional.md · tasks/sprint-feedback/concurrency.md.

### 차원 1 — Functional / Data-integrity: **PASS**
- **② 전건 비활성화**: QueryDestination OK(:226-231)·RecordDenied NG(:311-318) 둘 다 `FirstOrDefault` 1행 → `Where().ToList()`+foreach 전건. 부분 유니크 백스톱 유지·단일활성 불변식 미강제(OQ-3). RcsController 무정렬 조회 2곳 `OrderByDescending(Id)`. S2×2 GREEN.
- **⑤ SelectCell fail-loud**: 매칭 오더 없음 → null 반환·배정 0·tx 밖 WARN+alarm `CELL_ORDER_UNMATCHED`(pieceId=null). 빈셀없음(FULL)은 alarm 없이 null(구분). RcsController null-path 로그 중립화. S5a/S5b GREEN.
- **① 기능경로**: 원자 조건부 ExecuteUpdate·affected 0=OVER→RecordDenied(DENIED 보존). S1 happy + S1b(SQLite 8-way: 500 0·정확히 1 OK·7 DENIED/OVER) GREEN.
- 회귀: 524 GREEN(518+6)·11/12 run GREEN(1 RED 재발 0·선재 병렬부하 flake 귀속·sprint 6 테스트 격리 안정)·teardown hang 0. Wcs.Core diff 0(#8)·PlcGateway diff 0(#1)·재시도상수 0(#7)·마이그레이션 0(구조증거: Wcs.Data+양 스냅샷 develop 대비 byte-identical). R-③ 인덱스 존재·R-④ 전역 ReleaseCell 미재도입.

### 차원 2 — Concurrency & Provider-fidelity: **PASS** (실 SQL Server 실증)
- **실 SQL Server 2025**(MSSQLSERVER·Developer) 스크래치 DB `WcsAuditCConcur` from-scratch `ef database update` 9마이그레이션 exit 0. `order_item.RowVersion`=실 timestamp(rowversion) 컬럼 확인(SQLite 재현 불가 토큰).
- 실 백엔드(Provider=SqlServer·에페메랄 5399·/health db=True·현장 5205/운영DB 무접촉) 기동, 같은 barcode **8-way 동시 IF-05** planned=1/3/5로 **총 10회**.
- **미처리 500 = 0**(전 run 200:8) · **초과예약 0**(reserved==planned 정확) · **DENIED 계약 보존**(비수용마다 piece(DENIED)+IF05_REQ/RES OVER) · **flake 0**(planned=1 단독 8회 동일, ≥5 요건 초과).
- 코드경로: 추적 RMW 삭제→원자 조건부 ExecuteUpdate(WHERE reserved_qty+qty<=planned_qty·affected 0=OVER·tx 최초 write)→rollback+tx밖 RecordDenied. TOCTOU 폐쇄·RMW 잔재 0. 정리: 앱 종료·스크래치 DB DROP·운영 DB(piece=1/order_item=572/destination=1) 무접촉 확인.

### 재triage 반영
③(인덱스 2종)·④(ReleaseCell destination 스코프)는 S-HARDENING-1에서 이미 해소 → SCOPE OUT(존재 재확인 R-③/R-④ PASS). 남은 유효 ①②⑤ 전부 해소.

**APPROVED** — Step 4.5 코드리뷰 진행 가능. 커밋 스코프: DbRepositories.cs·RcsController.cs·DataIntegrityAuditTests.cs(신규) + 묶음 A reconcile(ffe179c) + 프로세스 파일. foreign 없음(RESUME/.bak 이미 정리).

## Step 4.5 코드리뷰 (2026-07-31) — Critical 0 · Major 0 · Minor 4 (전부 정보성·무액션 권고 · BLOCKING 없음)
고위험 질문 전부 clean(①tracked staleness/이중write 0·RecordDenied tx 안전·TOCTOU 폐쇄 / ②필터드 유니크 밖 insert 안전·OrderByDescending 일관 / ⑤alarm tx 후·DI 정합·null IF-11 skip·FULL vs unmatched 구분). #8 Wcs.Core diff 0. RED-first 비공허.
### Minor (다음 스프린트 참고 — 비차단)
- **CR-M1**: S1b SQLite 8-way 동시성 테스트가 shared-cache in-memory SQLite — SQLITE_BUSY 시 500 가능(선재 flake class). **실 SQL Server rowversion-500 실증은 Concurrency Evaluator 차원이 이미 discharge(10회 PASS)** — SQLite 단독으론 prod-provider 미증명(계약이 명시 위임). 무액션.
- **CR-M2**: NG-response `destType`가 pre-OVER 경로(:97 null)와 atomic-OVER 경로(:291 destApiType) 불일치. 내부 튜플 필드·RCS 미전송({result,chuteNo}만)·NG는 floor enqueue 0 → 무해. AUTO배정+동시OVER 희소 엣지. (원하면 두 경로 destType 통일.)
- **CR-M3**(무액션): 전건 비활성화가 per-row foreach(tracker) — active/pId 통상 0~2라 N tiny·tracker가 동일 tx 커밋 위해 정답. bulk ExecuteUpdate 불요.
- **CR-M4**(정보성·선재): SelectCell alarm append throw 시 IF-10 500 후 멱등 재시도로 IF-11 미발화 — unmatched는 원래 IF-11 skip이라 alarm 행만 유실·fail-loud·오분류 0. SelectCell은 원래도 throw 가능(선재 노출).
