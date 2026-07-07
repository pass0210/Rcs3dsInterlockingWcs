# BowooTestBatchSystem_v2 — 프로그램 구조 청사진 (이식용)

## 개요 & 이식 목적

본 문서는 **BowooTestBatchSystem_v2**(BOWOO 분류 작업 테스트 데이터 생성/검증 배치 시스템)의 프론트엔드·백엔드 **구성/기능 청사진**이다.

**목적**: 신규 프로그램 **Rcs3dsInterlockingWcs**(자체 신규 DB 구조를 별도로 보유)로 현재 시스템의 프론트·백을 이식(재개발)하기 위한 **DB-중립 설계 참조 문서**로 사용한다.

**범위와 의도적 제외 사항**:
- 현재 시스템의 **DB 컬럼 스키마(테이블 정의, 인덱스 목록 등)는 의도적으로 다루지 않는다.** 신규 DB 구조가 이미 별도로 정해져 있기 때문에, 이 문서는 대신 각 기능이 다루는 **'데이터 개념'**(예: 테스트데이터, 작업로그, 작업결과, 박스+내품 등)을 정리하여 신규 DB 스키마에 매핑할 수 있도록 돕는다.
- HTTP API 계약(요청/응답 필드, 검증 규칙, 성공/실패 판정 기준), 서비스 계층의 비즈니스 알고리즘, 인프라/미들웨어 구성, 프론트엔드 페이지·컴포넌트·상태관리 구조는 **그대로 재현해야 할 대상**으로 상세히 기술한다.
- 특히 **RCS(분류 설비) 연동 4개 API(input/classification/results/unprocessed)의 계약(필드명·타입·도메인 값·qty 상한)은 RCS 측과 사전 합의된 외부 인터페이스**이므로, DB를 교체하는 이식 작업이라도 **절대 변경하면 안 되는 고정 계약**임을 문서 전반에서 강조한다(Sprint 2 `E-4` 회귀 전례: `PId`/`InductionNo`를 `int`→`string`으로 임의 변경했다가 운영 호출이 깨져 원복한 사례가 `CLAUDE.md`에 교훈으로 기록되어 있음).

---

## 목차

1. [전체 아키텍처 & 데이터 흐름](#1-전체-아키텍처--데이터-흐름)
2. [백엔드 — API 계약](#2-백엔드--api-계약)
3. [백엔드 — 서비스·비즈니스 로직](#3-백엔드--서비스비즈니스-로직)
4. [백엔드 — 인프라·크로스커팅](#4-백엔드--인프라크로스커팅)
5. [프론트엔드 — 구조](#5-프론트엔드--구조)
6. [프론트엔드 — 페이지별 기능](#6-프론트엔드--페이지별-기능)
7. [프론트엔드 — 공용 컴포넌트·API 통합·빌드/보안](#7-프론트엔드--공용-컴포넌트api-통합빌드보안)
8. [기능 인벤토리](#8-기능-인벤토리)
9. [데이터 의존 지점 (신규 DB 매핑 가이드)](#9-데이터-의존-지점-신규-db-매핑-가이드)
10. [빌드·실행·배포 요약](#10-빌드실행배포-요약)
11. [이식 체크리스트](#11-이식-체크리스트)

---

## 1. 전체 아키텍처 & 데이터 흐름

### 1.1 기술 스택

| 계층 | 스택 |
|---|---|
| 백엔드 | ASP.NET Core (.NET 10) Web API (C#), Entity Framework Core, SQL Server, Serilog |
| 프론트엔드 | React 19 + Vite, axios, react-router-dom 7, date-fns, jsbarcode |
| 배포 형태 | **Single-Port(SPA+API)** — 백엔드 Kestrel 프로세스 하나가 정적 파일(SPA 빌드 산출물)과 API를 동시 서빙 |

### 1.2 Single-Port SPA+API 서빙 구조

- 프론트 빌드 산출물(`npm run build`)을 `backend/wwwroot/`로 복사해 배포.
- `UseDefaultFiles()` + `UseStaticFiles()`로 정적 자원 서빙, `MapFallbackToFile("index.html")`로 API/health/openapi/scalar에 매칭되지 않는 모든 경로를 `index.html`로 폴백 → React Router 클라이언트 라우팅이 새로고침에도 깨지지 않음.
- **주의(이식 시 재현 포인트)**: `UseStaticFiles()`가 `UseCors()`/`UseRateLimiter()`/RCS 로깅 미들웨어보다 먼저 등록되어 있어, 정적 자원 요청은 CORS/RateLimit 검사를 거치지 않고 API 요청과 다른 파이프라인 경로를 탄다.

### 1.3 데이터 흐름 다이어그램 (텍스트 표기)

```
[RCS 분류 설비]
   │  GET  /api/v1/works/unprocessed?bizDay=   (미처리 조회 + 부수효과: ReceiveTime 마킹, 필요 시 자동생성)
   │  POST /api/v1/works/input                  (투입 데이터 수신, qty 묶음)
   │  POST /api/v1/works/classification          (분류 데이터 수신, qty 묶음)
   │  POST /api/v1/works/results                 (작업 결과 전송, 최상위 배열)
   │  POST /api/v1/works/box                     (박스 마감 데이터 수신)
   ▼
[RcsApiLoggingMiddleware] → 요청/응답 마스킹 후 ApiCallLogQueue(비동기 큐) → ApiCallLogBackgroundWriter → DB(API호출로그)
   │
   ▼
[Controllers] → [Services: WorkService / BoxService] → [테스트데이터 / 작업로그 / 작업결과 / 박스+내품]
   │
   ▼
[프론트엔드 SPA] ← GET /api/logs/*, /api/test-data/*, /api/boxes  (조회 전용, RCS API와 별개 경로)
   - DataGenerator 페이지: 테스트데이터 생성/관리
   - Logs 페이지: 투입/분류 로그 조회 + Excel 내보내기
   - ResultComparison 페이지: 투입·분류·결과 3-way 비교
   - Boxes 페이지: 박스+내품 조회
   - Settings 페이지: 자동생성 규칙 + 인쇄 설정
```

**핵심 관찰**: RCS 연동 API(§2)와 프론트 전용 조회 API(§2-3,4)는 서로 다른 소비 주체를 갖는 완전히 분리된 경로다. 전자는 실제 설비가 호출하는 계약(변경 금지), 후자는 사람이 보는 화면을 위한 조회/집계 API(신규 DB에서 자유롭게 재구성 가능하나 화면 요구사항은 그대로 충족해야 함).

---

## 2. 백엔드 — API 계약

> **이식 관점**: 이 섹션의 계약(HTTP 메서드/경로/요청 필드/검증 규칙/응답 포맷/성공·실패 사유)은 DB 스키마와 완전히 독립적으로 유지되어야 하는 **외부 인터페이스**다. 특히 RCS 4개 API는 필드명·타입·도메인 값을 임의 변경하면 실제 운영 호출이 깨진다(전례 있음). DB를 교체하더라도 요청/응답 형태는 그대로 보존해야 한다.

### 2.0 공통 규칙

| 항목 | 내용 |
|---|---|
| 응답 래퍼 | `ApiResponse { Status: "S"\|"F", Message: string }` (`backend/Dtos/ApiResponse.cs:3-10`) |
| DTO 검증 실패 | `[ApiController]` 자동 ModelState 검증 + `ApiBehaviorOptions.InvalidModelStateResponseFactory`가 첫 번째 오류 메시지를 `ApiResponse.Fail(msg)`로 감싸 `400`으로 반환 — 전 컨트롤러 공통 (`Program.cs:92-102`) |
| 미처리 예외 | `GlobalExceptionMiddleware`: `ArgumentException`(예: 존재하지 않는 날짜) → `400`+Fail, 그 외 → `500`+Fail(TraceId 포함), 클라이언트 abort → 응답 없이 로깅만 (`Middleware/GlobalExceptionMiddleware.cs:31-45`) |
| Rate Limiting | IP당 분당 300건(대기열 10건), 초과 시 `429`+Fail (`Program.cs:104-125`) |
| 인증 | 없음(전 엔드포인트 익명 접근) — 의도적 보류 |
| BizDay 정규화 | `AppUtils.NormalizeBizDay`가 `YYYYMMDD`/`YYYY-MM-DD` → `YYYY-MM-DD`로 정규화. 형식은 맞지만 실존하지 않는 날짜는 `ArgumentException`→400 (`Utils/AppUtils.cs:17-43`) |
| qty 상한 | `AppConstants.QtyMaxPerRequest = 9999` — input/classification/results/box 4곳 공통 |

**Status 필드 두 도메인 (혼동 금지)**

| 위치 | 도메인 | 의미 |
|---|---|---|
| RCS 요청 `Status` (`InputRequest.Status`, `ClassificationRequest.Status`) | `"OK"` \| `"NG"` (정규식 `^(OK\|NG)$`) | 실제 작업 결과(양품/불량). 값 자체에 대한 업무 로직 분기 없음(그대로 저장) |
| `ApiResponse.Status` (모든 API 공통 응답 래퍼) | `"S"`(Success) \| `"F"`(Fail) | HTTP 처리 성공/실패 |

두 값은 서로 다른 축이다. 예: `Status="NG"`(불량)로 온 투입 요청도 로그가 정상 INSERT되면 `ApiResponse.Status="S"`가 반환된다.

### 2.1 엔드포인트 카탈로그

| 구분 | 메서드 | 경로 | 용도 | 응답 형태 |
|---|---|---|---|---|
| RCS 연동 | GET | `/api/v1/works/unprocessed?bizDay=` | 미작업 데이터 조회(부수효과 있음) | **원시 배열** (예외) |
| RCS 연동 | POST | `/api/v1/works/input` | 투입 데이터 수신 | `ApiResponse` |
| RCS 연동 | POST | `/api/v1/works/classification` | 분류 데이터 수신 | `ApiResponse` |
| RCS 연동 | POST | `/api/v1/works/results` | 작업 결과 전송(**요청이 최상위 배열**) | `ApiResponse` |
| RCS 연동(박스) | POST | `/api/v1/works/box` | 박스 마감 데이터 수신 | `ApiResponse` |
| 프론트 전용 | GET | `/api/boxes?bizDay=&batch=` | 박스 목록 조회 | 원시 배열 |
| 프론트 전용 | GET | `/api/logs/input`, `/api/logs/sort` | 투입/분류 로그 조회 | 원시 배열 |
| 프론트 전용 | GET | `/api/logs/api-calls?date=` | API 호출 이력 조회(최대 500건) | 원시 배열 |
| 프론트 전용 | GET | `/api/logs/export?bizDay=&batch=` | 투입+분류 통합 Excel 다운로드 | `.xlsx` 바이너리 |
| 프론트 전용 | GET | `/api/test-data/comparison?bizDay=` | 투입/분류/결과 3-way 비교 | 원시 배열 |
| 프론트 전용 | POST | `/api/test-data/generate` | 테스트 데이터 수동 생성 | `ApiResponse` |
| 프론트 전용 | GET | `/api/test-data/summary?bizDay=` | 배치별 건수/수신시각 요약 | 원시 배열 |
| 프론트 전용 | GET | `/api/test-data/detail?bizDay=&batch=` | 상세 목록(투입/분류 로그 LEFT JOIN) | 원시 배열 |
| 프론트 전용 | POST | `/api/test-data/reset` | 선택 항목 수신시간 초기화 + 연관 로그/결과 삭제 | `ApiResponse` |
| 프론트 전용 | DELETE | `/api/test-data` | 선택 항목 삭제(연관 로그/결과 포함) | `ApiResponse` |
| 프론트 전용 | POST | `/api/test-data/upload` | 엑셀 업로드 | `ApiResponse` |
| 프론트 전용 | GET/POST | `/api/test-data/auto-config` | 자동생성 설정 조회/저장 | `AutoGenerateConfigDto` / `ApiResponse` |
| 프론트 전용 | GET | `/api/test-data/preview-chutes?range=` | 슈트 범위 파싱 미리보기 | `int[]` |
| 운영/인프라 | GET | `/health`, `/health/ready`, `/health/queue` | 헬스체크 | 텍스트/JSON |
| 운영/인프라 | GET | `/openapi/v1.json`, `/scalar` | API 스펙/대화형 문서 | JSON/HTML |

### 2.2 RCS 연동 4대 API 상세 (변경 금지 계약)

#### 2.2.1 `POST /api/v1/works/input` — 투입 데이터 수신

`InputController.cs:20-34`, 로직 `WorkService.cs:160-208`

| 필드 | 타입 | 검증 규칙 | 비고 |
|---|---|---|---|
| BizDay | string | `[Required]`, `^\d{8}$` 또는 `^\d{4}-\d{2}-\d{2}$` | |
| Batch | string | `[Required]`, 1~10자 | |
| InductionNo | **int** | 검증 없음(암묵 필수, 기본 0) | RCS 계약 유지 위해 **int 고정**(Sprint2 E-4 회귀 원복) |
| ChuteNo | string | `[Required]`, ≤20자 | |
| PId | **int** | 검증 없음 | 동일 사유로 int 고정 |
| Barcode | string? | ≤100자, `^[A-Za-z0-9\-_]*$`(빈 문자열 허용) | |
| Status | string | `[Required]`, `^(OK\|NG)$` | |
| Reason | string? | ≤500자 | |
| InTime | string | `[Required]`, ≤30자(자유 포맷, 파싱 실패 시 서버시각 대체) | |
| Qty | int | `[Range(1,9999)]`, 기본 1 | |

실패 사유:
1. `barcode+bizDay+batch`로 등록된 테스트데이터가 없음 → `F: "Barcode not found, or bizDay/batch does not match the registered data."`
2. 미처리(INPUT 로그 미부착) 가용 행 수 < `qty` → `F: "Not enough unprocessed rows: requested {qty}, available {n}."` — **부분 처리 없이 전체 거부**
3. 성공 → `S`, qty개 행에 대해 작업로그(INPUT) 생성. `EquipmentNo=InductionNo.ToString()`, `Pid=PId.ToString()`, 로그시각=`InTime` 파싱값(실패 시 서버 Now)

#### 2.2.2 `POST /api/v1/works/classification` — 분류 데이터 수신

`ClassificationController.cs:8-35`, 로직 `WorkService.cs:211-274`

| 필드 | 타입 | 검증 규칙 |
|---|---|---|
| BizDay | string | `[Required]`, 날짜 정규식 |
| Batch | string | `[Required]`, 1~10자 |
| ChuteNo | string | `[Required]`, ≤20자 |
| PId | **int** | 검증 없음(RCS 계약 유지) |
| Barcode | string | `[Required]`, ≤100자, `^[A-Za-z0-9\-_]+$` (Input과 달리 필수·빈 문자열 불허) |
| SortTime | string | `[Required]`, ≤30자 |
| Status | string | `[Required]`, `^(OK\|NG)$` |
| Reason | string? | ≤500자 |
| Qty | int | `[Range(1,9999)]`, 기본 1 |

실패 사유(우선순위 순):
1. barcode+bizDay+batch 등록 행 없음 → F
2. 등록은 있으나 요청 ChuteNo(3자리 정규화 후)와 일치하는 행이 없음 → `F: "Chute mismatch: barcode {b} expected chute(s) [...], received {c}."`(등록된 유효 슈트 힌트 포함)
3. 해당 슈트의 모든 행이 이미 분류 완료(가용 0) → `F: "Barcode {b} in chute {c} has already been fully classified."`
4. 가용 행 < qty → F(부족 사유)
5. 성공 → `S`, 작업로그(SORT) qty개 생성. `EquipmentNo=정규화된 ChuteNo`, `Pid=PId.ToString()`

#### 2.2.3 `POST /api/v1/works/results` — 작업 결과 전송

`ResultController.cs:8-42` — **요청 바디가 최상위 JSON 배열**(`List<ResultRequestGroup>`), 로직 `WorkService.cs:277-350`

`ResultRequestGroup`: `BizDay`(필수, 날짜 정규식), `Batch`(필수, 1~10자), `Items`(필수, `[MinLength(1)]`)

`ResultItem`: `Barcode`(필수, ≤100자, `^[A-Za-z0-9\-_]+$`), `ChuteNo`(필수, ≤20자), `Qty`(`[Range(1,9999)]`, 기본 1)

처리 로직:
- 요청 배열 null/빈 배열 → `F: "No data to process."`
- **트랜잭션 진입 전 사전 검증**: (bizDay,batch)별 등록 barcode 집합 조회 후, 미등록 barcode가 하나라도 있으면 **전체 요청 거부**(부분 INSERT 금지) → `F: "Barcode '{b}' not found, or bizDay/batch does not match the registered data."`(Sprint7 추가)
- **ChuteNo는 검증하지 않음**(의도적) — RCS가 보낸 슈트와 등록 슈트 불일치는 `GET /api/test-data/comparison`이 측정하는 대상이기 때문
- 유효 item 0개(전부 빈 barcode) → `F: "No valid data to process."`
- 성공 → `S`, item마다 qty개 작업결과를 트랜잭션 안에서 INSERT, ChuteNo 3자리 정규화
- input/classification과 달리 **"미처리 풀 소비" 개념이 없어 부족 체크가 없음**

#### 2.2.4 `GET /api/v1/works/unprocessed?bizDay=` — 미작업 데이터 조회

`UnprocessedController.cs:20-37`, 로직 `WorkService.cs:19-72`

- `bizDay` 쿼리 파라미터 필수(수동 검증) → 없으면 `400+Fail("bizDay parameter is required.")`
- **응답은 `ApiResponse`가 아니라 원시 배열**(`List<UnprocessedGroupResponse>`)을 `200`으로 반환 — 데이터 없으면 `[]`
- 응답 구조: `[ { bizDay, batch, items: [ { barcode, chuteNo, qty } ] } ]` — 같은 batch 내에서 `barcode+chuteNo`로 재그룹핑해 `qty=Count`로 산출
- **부수 효과(side effect)**: 조회된 행의 `ReceiveTime`을 서버시각으로 갱신("가져갔다" 마킹), 미처리 0건이고 자동생성 설정이 존재하면 그 자리에서 자동 생성 트리거 — 트랜잭션으로 원자 처리

### 2.3 박스(Box) API

#### `POST /api/v1/works/box` — 박스 마감 데이터 수신

`BoxController.cs:25-39`, 로직 `BoxService.cs:19-63`

`BoxRequest`: `BizDay`(필수, 날짜정규식), `Batch`(필수, 1~10자), `BoxNo`(필수, ≤50자), `ChuteNo`(필수, ≤10자), `Items`(필수, `[MinLength(1)]`), `EndTime`(선택, ≤50자)

`BoxItemDto`: `Barcode`(필수, ≤100자, `^[A-Za-z0-9\-_]+$`), `Qty`(`[Range(1,9999)]`, 기본 1)

- `(BizDay,Batch,BoxNo)` 중복 시 즉시 `F: "Box already exists for the given bizDay/batch/boxNo."` — **barcode 존재 검증은 하지 않음**(박스는 출고 기록 저장 목적)
- 빈 barcode 아이템은 필터링 후 저장
- 성공 시 `S`, Box+BoxItem을 트랜잭션 안에서 원자 저장

#### `GET /api/boxes?bizDay=&batch=` — 박스 목록 조회 (프론트 전용)

- `bizDay` 필수지만 **DataAnnotations 미적용**(POST 대비 검증 비대칭 — 이월 코드리뷰 항목)
- `batch` 옵션(생략 시 해당 bizDay 전체)
- 응답: `[ { id, bizDay, batch, boxNo, chuteNo, endTime, createdAt, items: [ { barcode, qty } ] } ]`

### 2.4 로그 조회/내보내기 · 테스트데이터 관리 API

로그: `GET /api/logs/input`, `/api/logs/sort`(각 `bizDay?`), `/api/logs/api-calls`(`date?`, 최대 500건), `/api/logs/export`(`bizDay` 필수+`batch?`, `.xlsx` 다운로드, 오류 시 400+Fail).

`GET /api/test-data/comparison?bizDay=`은 컨트롤러상 `TestDataController`에 위치하지만 `ILogService.GetResultComparisonAsync`를 호출 — 투입/분류/결과 3단계 존재 여부(`HasInput/HasSort/HasResult`), 슈트 일치 여부(`IsMatch`), 누락 여부(`IsMissing`)를 바코드 단위로 산출.

테스트데이터 관리 API 요약은 §2.1 표 참고. 상세 검증 규칙:
- **업로드**(`POST /api/test-data/upload`, `[RequestSizeLimit]` 10MB): (1) 파일 없음/0바이트 → F, (2) 10MB 초과 → F, (3) 확장자(`.xlsx`/`.xls`) 불일치 → F, (4) MIME 화이트리스트 불일치 → F, (5) 파싱 단계에서 빈 시트/유효행 0개/파싱 예외 → F. 성공 시 `S:"{n}건 업로드 완료"`. 5컬럼(BizDay,Batch,Barcode,Barcode2,ChuteNo) 신양식/구 4컬럼(ChuteNo가 4번째) 양식 자동 판별.
- **AutoGenerateConfigDto 검증**: `ChuteRange`(필수,≤200,`^[\d\s,\-]+$`), `BarcodeCountPerChute`(`[Range(1,1000)]`), `FixedBarcodes`(≤2000,옵션), `PrintWidth`/`PrintHeight`(`[Range(10,500)]`).
- **GenerateRequest 검증**: `BizDay`(필수,날짜정규식), `Batch`(필수,1~10자), `ChuteNos`(필수,≤200,`^[\d\s,\-]+$`), `BarcodeCount`(`[Range(1,10000)]`).

### 2.5 헬스체크/OpenAPI (운영·인프라용, 비즈니스 계약 아님)

| 경로 | 용도 | 응답 |
|---|---|---|
| `GET /health` | Liveness | 기본 OK |
| `GET /health/ready` | Readiness(DB+큐) | JSON |
| `GET /health/queue` | 큐 통계 | `{pending,capacity,enqueued,dropped,persisted}` |
| `GET /openapi/v1.json` | OpenAPI 3.x 스펙 | JSON |
| `GET /scalar` | 대화형 API 문서(개발 환경 한정) | HTML |

### 2.6 이식 시 유의사항 요약

- RCS 4개 API의 **필드명·타입(특히 `PId`/`InductionNo`=int)·`Status` 정규식(`OK`/`NG`)·qty 상한(9999)**은 RCS 측과의 계약이므로 백엔드를 새로 구현해도 그대로 보존해야 한다.
- 4개 RCS API + box API 모두 **비즈니스 실패를 HTTP 200 + `{Status:"F",...}`로 표현**하며, `400`은 DTO 검증 실패/날짜 파싱 실패, `500`은 미처리 예외에만 쓰인다 — 다른 스택으로 이식할 때 이 구분(200 vs 400 vs 500)을 동일하게 재현해야 RCS 클라이언트가 오작동하지 않는다.
- `unprocessed`는 유일하게 응답이 원시 배열이며, GET임에도 **부수 효과**(ReceiveTime 갱신, 자동생성 트리거)가 있다 — REST 관례상 이례적이므로 반드시 유지해야 하는 계약.
- `results`는 요청 바디가 **최상위 JSON 배열**이라는 점이 다른 3개 API와 다르다.

---

## 3. 백엔드 — 서비스·비즈니스 로직

### 3.1 서비스 목록 및 책임

모든 Controller는 인터페이스에만 의존(`Program.cs`에서 `AddScoped<IFoo, Foo>()`).

| 구현체 | 인터페이스 | 책임 요약 |
|---|---|---|
| `WorkService` | `IWorkService` | RCS 4대 API(미처리조회/투입/분류/결과) 처리, 자동생성 트리거 |
| `TestDataService` | `ITestDataService` | 테스트데이터 CRUD(수동생성/엑셀업로드/조회/초기화/삭제), 슈트 파서·바코드 생성기 공용 유틸 |
| `LogService` | `ILogService` | 투입/분류 로그 조회, 결과 비교(3-way join), API 호출 로그 조회 |
| `LogExportService` | `ILogExportService` | 투입/분류 통합 Excel 내보내기(하이브리드 페어링) |
| `AutoGenerateConfigService` | `IAutoGenerateConfigService` | 자동생성 규칙 + 인쇄 설정 단일 레코드 upsert |
| `BoxService` | `IBoxService` | 박스(카톤) 마감 데이터 수신/저장/조회 |
| `ApiCallLogQueue` + `ApiCallLogBackgroundWriter` | (없음) | RCS API 호출 로그 비동기 적재(Bounded Channel) + 배치 DB 기록 |

### 3.2 핵심 비즈니스 알고리즘 (신규 DB 재구현용 개념 설명)

#### 3.2.1 테스트데이터 생성 — 수동 vs 자동 vs 엑셀업로드

세 경로 모두 **동일한 슈트 파서**(`ParseChuteNos`)와 **동일한 ChuteNo 3자리 zero-pad**(`AppConstants.ChuteNoFormat="D3"`)를 공유한다.

- **수동 생성**: `ChuteNos`(예: `"1-5,8"`) 파싱 → 슈트 목록. `BarcodeCount`개의 바코드를 `chuteNos[i % chuteNos.Count]`로 **라운드로빈 배분**, 각 바코드는 `GenerateBarcode()`(`BC{yyyyMMddHHmmssfff}{랜덤4자리}`)로 자동 채번.
- **자동 생성**: RCS가 `GetUnprocessedAsync` 호출 시 해당 BizDay 미처리(ReceiveTime=null) 데이터가 0건이면 트리거. 다음 Batch = "해당 BizDay 마지막 Batch+1"(3자리, 없으면 `"001"`). 두 모드:
  - **모드 A(바코드 자동)**: 설정된 슈트마다 `BarcodeCountPerChute`개씩 신규 바코드 생성.
  - **모드 B(고정 바코드 목록)**: `FixedBarcodes`(콤마구분)를 슈트마다 순차 소진 배분(리스트 소진 시 중단, 남는 슈트는 미배분).
- **엑셀 업로드**: 5컬럼 양식(`BizDay, Batch, Barcode, Barcode2, ChuteNo`) 지원. 헤더 유무는 1행1열 값의 날짜형 여부로 자동판별. 구양식(4컬럼)/신양식(5컬럼) 판별은 "5번째 컬럼 값 존재 여부"로 분기. 빈 바코드 행 스킵.

#### 3.2.2 투입/분류 qty 묶음 처리 (동일 패턴)

1. `barcode+bizDay+batch`로 테스트데이터 후보 조회. 없으면 즉시 F.
2. (분류만) 요청 ChuteNo를 3자리 정규화 후 해당 슈트 배치 행만 재필터. 매칭 0건이면 "슈트 불일치" F.
3. 이미 해당 LogType(INPUT/SORT) 로그가 붙은 TestDataId 집합을 조회해 **미처리 행만 추출**(`available`).
4. `available.Count < req.Qty`면 **부분 처리 없이 전체 F** — "qty만큼 한꺼번에 처리되거나 아예 안 됨"이 원칙.
5. 충분하면 `available`에서 앞에서부터 qty개(`Take`)를 골라 각각 1개 로그 행으로 INSERT. qty=1이면 기존 단건 처리와 동일(하위호환).
6. 로그 시각은 요청 문자열을 `DateTime.TryParse`, 실패 시 서버 현재시각 폴백.

#### 3.2.3 미처리(unprocessed) 그룹핑

1. 트랜잭션 내에서 해당 BizDay의 `ReceiveTime==null` 행을 `Batch→ChuteNo→Barcode` 순 정렬로 조회. 0건이면 자동생성(§3.2.1) 시도.
2. 조회된 모든 행에 현재시각을 `ReceiveTime`으로 일괄 마킹(재조회 방지) 후 커밋.
3. **2단계 그룹핑**: 먼저 `Batch`로 묶고, 같은 Batch 안에서 다시 `Barcode+ChuteNo`로 재그룹핑하여 그룹 크기를 `Qty`로 산출 — "동일 품목이 동일 슈트에 N행 존재"를 "품목 1개+qty=N"으로 압축.

#### 3.2.4 results 존재검증

- 여러 그룹(`BizDay+Batch+Items[]`)으로 구성된 요청. **N+1 회피**: `(BizDay,Batch)` 조합을 `Distinct()`한 뒤 조합당 1회씩만 테스트데이터 Barcode 집합을 조회해 캐시(`Dictionary<(BizDay,Batch), HashSet<Barcode>>`).
- **트랜잭션 진입 전** 모든 item의 barcode가 등록 데이터에 존재하는지 검증. 하나라도 미등록이면 **전체 요청 거부**(부분 INSERT 금지). 빈 barcode 항목은 검증 스킵.
- ChuteNo는 검증하지 않음(의도적) — "RCS가 보낸 슈트 vs 등록 슈트" 불일치 측정이 §2/§9 결과비교 화면의 목적.
- 검증 통과 시 각 item을 Qty만큼 반복해 작업결과 행을 확장 생성. ChuteNo 3자리 정규화 후 저장. 최종 INSERT는 명시적 트랜잭션으로 원자 커밋.

#### 3.2.5 박스 저장

BizDay 정규화 → ChuteNo 3자리 zero-pad → `(BizDay,Batch,BoxNo)` 유니크 조합 중복검사(재전송 시 즉시 F, INSERT 안 함) → 빈 barcode item 필터링 → Box+BoxItem을 트랜잭션 내 원자 저장. barcode의 테스트데이터 존재 여부는 검증하지 않음(박스는 "출고 기록" 자체를 남기는 목적).

#### 3.2.6 로그 조회의 N+1 회피 패턴

`GetInputLogsAsync`/`GetSortLogsAsync`는 LINQ `Select` 안에서 상관 서브쿼리를 인라인해 ChuteNo/ReceiveTime을 가져온다. EF Core가 이를 SQL Server의 **단일 쿼리(OUTER APPLY)**로 번역해 N+1을 방지한다.

**주의점(재구현 시 유지할 동작)**: 이 서브쿼리는 `TestDataId`가 아닌 **Barcode 단독**으로 매칭한다(같은 바코드가 여러 Batch에 존재하면 임의의 한 행이 매칭될 수 있음). 반면 결과비교(§3.2.7)는 `TestDataId` 우선 매칭이라 더 정밀 — 두 조회 경로의 매칭 정밀도가 다르다는 점을 재구현 시 그대로 반영할지 결정 필요.

#### 3.2.7 결과 비교 3-way 매칭

테스트데이터를 기준행으로 순회하며 INPUT/SORT/RESULT 각각을 매칭:
- INPUT/SORT: `TestDataId` 키 우선 매칭, 실패 시 Barcode 폴백이되 **이미 사용된 로그 Id는 재사용 금지**(중복행 오매칭 방지).
- RESULT: SORT의 ChuteNo와 동일한 결과를 우선 매칭, 없으면 미사용 결과 중 첫 번째.
- `IsMatch` = INPUT/SORT/RESULT 모두 존재 + SORT.ChuteNo == RESULT.ChuteNo. `IsMissing` = 셋 중 하나라도 없음.
- **기존 결함(이월, Sprint6 코드리뷰)**: 결과 매칭 키가 Batch를 포함하지 않고 Barcode만으로 구성 — 같은 BizDay 내 다른 Batch의 결과와 오매칭될 수 있음. **신규 구현 시 Batch 포함 키로 교정 검토 권장.**

#### 3.2.8 Excel export 하이브리드 페어링

INPUT 로그를 SORT 로그와 짝지어 "투입→분류" 1행씩 만드는 알고리즘. **Phase 1(정밀 매칭) → Phase 2(시간순 폴백)** 순서:
- **Phase 1**: `TestDataId`(전역 유일 물품-로그 연결키)가 있는 SORT를 TestDataId별로 그룹핑해 `Queue<TestLog>`(LogTime→Id 순)로 구성. 같은 TestDataId를 가진 INPUT과 1:1 매칭(dequeue). 매칭된 SORT는 사용 처리.
- **Phase 2**: Phase 1 미매칭 INPUT에 대해 미사용 SORT를 `(Batch,Barcode)`로 그룹핑 후 LogTime 오름차순 큐에서 순서대로 매칭(zip 방식) — TestDataId 없는 레거시 로그/Phase1 미스매치 보정용.
- 출력은 INPUT 원래 LogTime 오름차순 유지. SORT가 끝내 안 붙으면 슈트/소요시간 칸 공백.

#### 3.2.9 소요시간 산식

`elapsed = (SORT.LogTime − INPUT.LogTime).TotalSeconds`, `"F1"` 포맷(소수 첫째자리 초). 조건: 매칭된 SORT가 있고 양쪽 LogTime 모두 존재하며 `span >= 0`(음수=이상치 데이터)일 때만 표기.

부가 규칙: 인덕션 번호 → 층 매핑(`MapFloor`) — 인덕션 1·2는 "2층", 3·4는 "1층", 그 외/파싱불가는 공백. 설비 배치에 고정된 하드코딩 규칙이라 재구현 시 그대로 이식 필요.

#### 3.2.10 슈트 정규화·파싱, ChuteNo zero-pad

- **파싱**(`ParseChuteNos`): 콤마구분 입력에서 `"a-b"`는 범위 전개, 단일 숫자는 그대로 추가. `HashSet<int>`로 중복 제거 후 오름차순 정렬 반환. 자동생성설정 검증/미리보기 API에서도 재사용.
- **zero-pad**(`AppConstants.ChuteNoFormat="D3"`): 슈트번호는 항상 3자리로 저장/비교(`"1"` vs `"001"` 불일치 방지, Sprint1 E-6). 전사 공통 패턴.
- **정렬 주의**: 상세 조회(`GetDetailAsync`)는 ChuteNo가 문자열이라 DB단 숫자 정렬이 안 되므로, 전체 로드 후 **메모리에서** `int.TryParse` 성공 시 숫자값·실패 시 `int.MaxValue`(후순위)로 1차 정렬, 2차 문자열 정렬하는 안전 패턴 사용.

### 3.3 서비스 × 데이터 개념 CRUD 매트릭스 (개념 수준)

| 서비스 | 테스트데이터 | 작업로그(INPUT/SORT) | 작업결과 | 박스 | 자동생성설정 | API호출로그 |
|---|---|---|---|---|---|---|
| TestDataService | C/R/U/D | D(연관삭제만) | D(연관삭제만) | — | — | — |
| WorkService | C/R/U | C/R | C | — | R | — |
| LogService | R | R | R | — | — | R |
| LogExportService | — | R | — | — | — | — |
| AutoGenerateConfigService | — | — | — | — | C/R/U | — |
| BoxService | — | — | — | C/R | — | — |
| ApiCallLogQueue/BackgroundWriter | — | — | — | — | — | C |

**연관 삭제 규칙**(`ResetReceiveTimeAsync`/`DeleteAsync`)은 **Barcode를 키**로 작업로그·작업결과를 함께 삭제한다 — TestDataId가 아닌 Barcode 매칭이므로, 신규 DB에서 동일 Barcode가 여러 Batch에 걸쳐 있으면 의도보다 넓게 삭제될 수 있다는 점을 재구현 시 유의.

### 3.4 인프라 컴포넌트: ApiCallLogQueue

- `Channel.CreateBounded<ApiCallLog>(Capacity=10,000)`, `FullMode=DropOldest`, `SingleReader=true`(백그라운드 워커 전용), `SingleWriter=false`(여러 요청 스레드 동시 Enqueue). 미들웨어는 `TryEnqueue`만 호출하고 실제 DB 쓰기는 하지 않아 요청 응답 지연 없음.
- **손실 카운팅**: `TryWrite` 직전 큐가 가득 찬 상태였는지로 추정해 `_dropped` 증가.
- **통계**: `GetStats()`가 `Pending/Capacity/Enqueued/Dropped/Persisted` 스냅샷 반환 → `/health/queue`, 헬스체크가 사용.
- **`ApiCallLogBackgroundWriter`**(BackgroundService): 큐에서 최소 1건 블로킹 대기 후, 최대 `BatchSize=100`건 단위로 모아 일괄 `SaveChangesAsync`. 서비스 종료 시 잔여 항목 가능한 만큼 저장. **저장 실패는 로그만 남기고 무시(로그 유실 허용 — 가용성 우선)**.

### 3.5 `AppConstants` 상수 목록

| 상수 | 값 | 용도 |
|---|---|---|
| `BatchNumberFormat` | `"D3"` | Batch 번호 3자리 zero-pad |
| `DefaultBatchNumber` | `"001"` | 최초 Batch 기본값 |
| `ChuteNoFormat` | `"D3"` | ChuteNo 3자리 zero-pad |
| `ApiCallLogMaxItems` | `500` | API 호출 로그 조회 최대 반환 건수 |
| `UploadMaxBytes` | `10MB` | 엑셀 업로드 최대 크기 |
| `ExcelMaxRows` | `10,000` | 엑셀 업로드 최대 행 수 |
| `QtyMaxPerRequest` | `9999` | input/classification/results/box 공통 qty 상한 |
| `LogTruncateConsoleLength` | `500` | 콘솔 로그 본문 절단 길이 |
| `LogTruncateDbLength` | `4000` | DB 로그 본문 절단 길이 |

---

## 4. 백엔드 — 인프라·크로스커팅

### 4.1 앱 부팅 순서 / 미들웨어 파이프라인

**빌드 단계(DI/옵션 등록)** — 등록 순서:
1. `builder.Host.UseSerilog(...)` — Serilog를 호스트 로깅 프로바이더로 교체
2. `AddDbContext<AppDbContext>` — SQL Server, `ConnectionStrings:DefaultConnection`
3. 서비스 DI 등록(전부 인터페이스 기반, `AddScoped`): `ITestDataService`, `IWorkService`, `ILogService`, `IAutoGenerateConfigService`, `IBoxService`, `ILogExportService`
4. `ApiCallLogQueue`(Singleton), `ApiCallLogBackgroundWriter`(HostedService)
5. CORS 정책 구성 (§4.4)
6. `AddControllers().AddJsonOptions(...)` — 응답 JSON camelCase
7. `AddOpenApi()`
8. `AddHealthChecks()` — DB + 큐
9. `ApiBehaviorOptions.InvalidModelStateResponseFactory` — 검증 실패 응답 표준화
10. `AddRateLimiter(...)`

**런타임 파이프라인**(`app.Use...` 순서, 순서 자체가 의미를 가짐):
1. EF Core 마이그레이션 부트스트랩(`ApplyMigrationsIdempotentAsync`)
2. HTTPS 강제 여부 판정(`Https:Force`, 기본 `!IsDevelopment()`) → 참이면 `UseHsts()`+`UseHttpsRedirection()`
3. `UseMiddleware<GlobalExceptionMiddleware>()` — **파이프라인 최상단**, 하위 모든 예외 포착
4. `UseSerilogRequestLogging()` — 요청 1건당 요약 로그
5. `UseDefaultFiles()` + `UseStaticFiles()` — SPA 정적 파일 서빙
6. `UseCors()`
7. `UseRateLimiter()`
8. `UseMiddleware<RcsApiLoggingMiddleware>()` — `/api/v1/` 요청만 로깅
9. `MapControllers()`
10. `MapOpenApi()` + 개발 환경 한정 `MapScalarApiReference("/scalar")`
11. `MapHealthChecks("/health")`, `/health/ready`, `/health/queue`
12. `MapFallbackToFile("index.html")`

**이식 시 재현 포인트**: 예외 미들웨어가 가장 바깥(정적파일/CORS/RateLimiter/RCS 로깅에서 발생하는 예외까지 포괄), RCS 로깅은 라우팅 이후·컨트롤러 직전에 위치해 요청 본문을 미리 버퍼링한다.

### 4.2 Serilog 구성 — 앱 로그 vs RCS API 전용 로그 분리

- **일반 앱 로그**: Console + File(`logs/app-.log`, 일자 롤링, `retainedFileCountLimit:30`, `shared:true`). `MinimumLevel.Default=Information`, `Microsoft/System=Warning`, `EFCore=Warning`. `Enrich:FromLogContext`.
- **RCS API 전용 로그**: 코드 레벨 서브로거로 추가. `Filter.ByIncludingOnly(Matching.FromSource<RcsApiLoggingMiddleware>())`로 해당 미들웨어 발행 이벤트만 필터링. 별도 파일 싱크(`logs/rcs_api-.log`, `retainedFileCountLimit:90` — 앱 로그의 3배, RCS 연동 감사 목적). 전용 구분선 출력 템플릿.
- **주의**: 이 서브로거는 `WriteTo.Logger`로 추가되므로 RCS 로그는 `app-*.log`에도 남고 `rcs_api-*.log`에도 남는 **이중 기록 구조**.
- **민감정보 마스킹**(`Utils/LogSanitizer.cs`): 민감 키(`password, pwd, passwd, token, accessToken, refreshToken, authorization, apiKey, api_key, secret, ssn, residentNo, rrn`, 대소문자 무시)를 JSON/쿼리스트링 패턴 정규식으로 찾아 값을 `***`로 치환. **로그 기록 직전에만 적용되며, 실제 요청 처리(컨트롤러)에는 원본 값이 그대로 전달**된다(마스킹은 로그 전용).

### 4.3 크로스커팅 미들웨어

**RcsApiLoggingMiddleware**:
- 대상: `/api/v1/`로 시작하는 요청만, 그 외는 즉시 통과
- 요청 본문: `EnableBuffering()` 후 전체 읽어 마스킹(RCS API는 항상 JSON 전제, Content-Type 필터 없음)
- **응답 본문 조건부 캡처(A-5)**: `Content-Type`이 `application/json`/`application/problem+json`/`application/xml`/`text/*`일 때만 메모리 캡처, 그 외는 `(skipped: <content-type>)`만 기록하고 원본 스트림 통과(바이너리/스트리밍 응답 안전)
- 예외 발생 시 응답 스트림 원복 후 재던짐 — 상위 `GlobalExceptionMiddleware`에 위임
- **finally 단일 경로 정리(F-2)**: `Stopwatch.Stop()`, 상태 기준 로그 레벨 분기(Error/Warning/Information), `EnqueueLog(...)`를 한 곳에서 수행
- 응답 본문 DB 저장 시 4000자로 절단

**GlobalExceptionMiddleware**:
- 파이프라인 최상단, 하위 모든 미처리 예외 포착. 분기 3가지:
  1. `OperationCanceledException`+요청 취소됨 → 응답 없이 Information 로그만(프론트 AbortController 정상 케이스)
  2. `ArgumentException` → 400+Fail, Warning 로그
  3. 그 외 → 500+Fail("...TraceId: ..."), Error 로그(스택 트레이스 포함하되 **클라이언트엔 미노출**, TraceIdentifier만 응답에 포함)
- 이미 응답 스트리밍 시작 후엔 덮어쓰지 않음. 응답 직렬화는 camelCase 고정(미들웨어 자체 재설정).

### 4.4 보안/운영 구성

| 항목 | 내용 |
|---|---|
| CORS | `Cors:AllowedOrigins` 배열 화이트리스트. 구성 누락 시 개발 편의 기본값(`localhost:5173`,`localhost:3000`)로 폴백 + 운영 배포 경고 주석. `AllowAnyMethod().AllowAnyHeader()`는 유지, Origin만 제한 |
| Rate Limiting | 클라이언트 IP 기준 파티셔닝, `FixedWindowLimiter` 분당 300건, 대기열 10건(OldestFirst). 초과 시 429+Fail |
| HSTS/HTTPS 리다이렉트 | `Https:Force` 구성 키(기본 `!IsDevelopment()`). 폐쇄망 HTTP 전용 운영 환경 위해 `Https__Force=false`로 끌 수 있는 탈출구 제공 |
| CSP | **백엔드가 아닌 프론트엔드 책임**(`frontend/index.html` `<meta>`) — 백엔드는 별도 CSP 미들웨어 없음 |
| 파일 업로드 제한 | `[RequestSizeLimit]`(10MB) + 컨트롤러 내부 재검증 + 확장자 화이트리스트(경로 조작 방지) + MIME 화이트리스트 이중 검증 |
| ModelState 표준화 | DataAnnotations 검증 실패 시 `ApiResponse.Fail(firstError)`로 통일된 400 — 프론트가 항상 동일 `{status,message}` 포맷 기대 가능 |
| 인증/인가 | 미구현(전 엔드포인트 익명) — 의도적 보류. **이식 시 필요하면 별도 설계 필요** |

### 4.5 관측성 (Observability)

| 엔드포인트 | 내용 |
|---|---|
| `/health` | 태그 필터 없음, 등록된 모든 헬스체크(사실상 liveness) |
| `/health/ready` | `tags.Contains("ready")` 필터 — DB(`AddDbContextCheck`)+큐 검사, 커스텀 JSON 응답. 큐 임계값: 적체 70% Degraded, 90% 또는 누적 손실 ≥100 Unhealthy |
| `/health/queue` | `ApiCallLogQueue.GetStats()`를 그대로 JSON 직렬화 |
| `/openapi/v1.json` | 전 환경 노출(RCS 연동 담당자가 직접 가져다 쓸 수 있도록 개발 환경 제한 없음) |
| `/scalar` | 개발 환경 한정 대화형 문서 |
| `UseSerilogRequestLogging()` | 모든 HTTP 요청 method/path/status/elapsed 요약 로그 1줄 |

### 4.6 구성 키 / 주입 방식 / 포트

| 구성 키 | 위치/기본값 | 주입 방식 |
|---|---|---|
| `ConnectionStrings:DefaultConnection` | `appsettings.json`은 빈 문자열(운영 비밀 미포함), `appsettings.Development.json`은 로컬 값 명시 | 운영: 환경변수(`ConnectionStrings__DefaultConnection`) 또는 User Secrets. 개발: appsettings 직접 값 |
| `Cors:AllowedOrigins` | appsettings 배열(`5173`/`3000`) | 배포 시 운영 도메인 교체, 누락 시 로컬 기본값 폴백 |
| `Https:Force` | 기본 `!IsDevelopment()` | 환경변수 `Https__Force` 또는 appsettings |
| `Serilog:*` | appsettings 표준 섹션 | `ReadFrom.Configuration` |
| `AllowedHosts` | `"*"` | appsettings |

- **포트**: 개발 프로필 `applicationUrl: http://localhost:5205`, `ASPNETCORE_ENVIRONMENT=Development`. `ASPNETCORE_URLS` 환경변수로 운영 override 가능(Kestrel 기본 동작).
- **Single-Port 서빙**: §1.2 참고.

### 4.7 EF Core 마이그레이션 부트스트랩 (역할만 언급)

앱 시작 시 매번 `ApplyMigrationsIdempotentAsync`가 실행되어 DB 존재 여부/마이그레이션 이력 존재 여부/기존 테이블 존재 여부를 순차 판별해 신규 DB·기존 `EnsureCreated()` DB·정상 마이그레이션 환경을 자동 분기 처리. 목적: 배포 시 수동 마이그레이션 명령 없이 앱 기동만으로 스키마를 안전하게 최신화하되 기존 운영 데이터를 보존. **스키마 상세는 본 문서 범위 밖**(개요 참고).

---

## 5. 프론트엔드 — 구조

### 5.1 라우팅 (`frontend/src/App.jsx`)

전체 페이지가 `Layout`으로 감싸진 뒤 `Routes` 렌더링.

| 경로 | 컴포넌트 | 비고 |
|---|---|---|
| `/` | → `/data-generator` 리다이렉트 | |
| `/data-generator` | `DataGenerator` | 기본 진입 페이지 |
| `/logs` | `Logs` | |
| `/comparison` | `ResultComparison` | |
| `/settings` | `Settings` | |
| `/boxes` | `Boxes` | |
| `*` | `NotFound`(인라인) | 404, "홈으로 이동" 링크 |

사이드바 메뉴/헤더 타이틀은 `Layout.jsx`의 `NAV_ITEMS`/`PAGE_TITLES`에 5개 경로 모두 등록.

### 5.2 Provider 계층 (`main.jsx`)

```
ErrorBoundary
 └ GlobalProvider   (업무일자/테마/자동새로고침)
    └ UiProvider    (토스트/컨펌 모달)
       └ BrowserRouter
          └ App (Layout + Routes)
```

`ErrorBoundary`가 최상위에서 렌더링 예외를 잡고, `GlobalProvider`/`UiProvider`가 라우터보다 바깥에 있어 모든 페이지가 두 컨텍스트를 공유한다.

### 5.3 GlobalContext

- **상태**: `globalDate`(업무일자, `Date` 객체, 기본값 `new Date()`), `theme`, `autoRefresh`, `refreshInterval`.
- **localStorage 영속화 + safe 가드**: `safeGetItem`/`safeSetItem`이 `try/catch`로 감싸 프라이빗 모드 등에서 접근 실패해도 앱이 죽지 않게 함. 테마는 `ALLOWED_THEMES=['dark','light']`, 새로고침 간격은 `ALLOWED_INTERVALS=[3000,5000,10000,30000,60000]` 화이트리스트로 검증 후 사용 — 저장값 손상/조작 시 안전 기본값 폴백.
- 훅 분리: `useGlobal()`은 별도 파일(`context/useGlobal.js`)에서 `useContext`만 반환(Fast Refresh 경고 회피).
- `globalDate`/`autoRefresh`/`refreshInterval`은 DataGenerator, Logs, ResultComparison, Boxes 4개 페이지가 폴링 주기와 조회 날짜로 공용 소비.

### 5.4 UiContext

`alert()`/`confirm()`을 대체하는 비차단형 토스트+모달 시스템.

- **toast**: `info/success/warning/error` 4종. `error`는 6초, 그 외 3초 후 자동 dismiss.
- **confirm**: `await confirm(message, options)` → `Promise<boolean>`.
- **키보드**: 모달 열림 중 `Escape`=취소, `Enter`=확인.
- 접근성: 토스트 스택 `role="status" aria-live="polite"`, 모달 `role="dialog" aria-modal="true"`.

---

## 6. 프론트엔드 — 페이지별 기능

### 6.1 DataGenerator — 테스트 데이터 생성/관리 (기본 진입 페이지)

- **목적**: 업무일자·배치·슈트범위·바코드개수로 테스트 데이터를 생성하고, 생성된 데이터를 요약(배치 단위)/상세(바코드 단위) 그리드로 조회·삭제·초기화·바코드 라벨 인쇄.
- **UI**: 좌측 생성 폼+엑셀 업로드 카드, 중앙 요약 그리드, 우측 상세 그리드, 우클릭 컨텍스트 메뉴.
- **플로우**:
  - 생성 폼은 `<form onSubmit>`으로 감싸 Enter 제출 지원.
  - 요약 행 클릭 → 해당 배치 상세 로드 + 선택/드래그 상태 초기화.
  - 상세 그리드: **일반 클릭**(단일 선택+드래그 시작), **드래그**(mouseEnter로 연속 범위 갱신), **Shift+클릭**(anchor~클릭 연속 범위), **Ctrl/Cmd+클릭**(개별 토글 누적). 그리드 밖에서 마우스 뗄 때도 `window` 레벨 `mouseup`으로 드래그 종료 보장.
  - 우클릭 컨텍스트 메뉴 → "선택영역 체크/해제"로 드래그 선택 결과를 실제 체크박스 집합에 반영. 외부 클릭/스크롤/Esc로 자동 닫힘.
  - **수신 초기화(일괄 처리)**: 체크된 배치들의 상세 조회를 `Promise.all` 병렬 수행 → id 취합 → 한 번에 초기화 API 호출 + 진행 토스트 → 완료 건수 요약 토스트.
  - **인쇄**: 체크된 항목을 A4 라벨(2열×4행, 99.14×67.48mm) 팝업 창에 그림. `innerHTML` 대신 `document.createElement`/`textContent`로 DOM 조립(XSS 방지). `barcode2` 있으면 듀얼 바코드 배치. 팝업 차단 시 토스트 안내.
- **호출 API**: `GET /test-data/summary`, `GET /test-data/detail`, `POST /test-data/generate`, `POST /test-data/upload`(멀티파트), `POST /test-data/reset`, `DELETE /test-data`.
- **AbortController**: 요약+선택 배치 상세를 폴링 간격마다 동시 조회하는 effect에 적용.
- **데이터 의존(개념)**: 테스트데이터(배치별 생성 수량·수신시각 요약 + 바코드별 투입/분류 처리 상태).

### 6.2 Logs — 투입/분류 로그 조회

- **목적**: RCS가 호출한 투입(INPUT)/분류(SORT) 로그를 조회·필터링·다운로드.
- **UI**: **탭이 아니라 좌우 2열 카드 레이아웃**으로 투입 로그 카드와 분류 로그 카드를 항상 동시에 나란히 표시(정정: "탭" 방식으로 오인하기 쉬우나 실제로는 탭 전환 UI/상태가 코드에 없음).
- **필터**: 컬럼별 텍스트 필터(투입: 날짜/배치/바코드/인덕션/PID/상태/사유/투입시간, 분류: 날짜/배치/바코드/슈트/PID/상태/사유/분류시간) + 상단 배치 셀렉트 + **통합검색**(모든 필드 OR 매칭, Esc로 초기화).
- **자동새로고침**: 전역 상태 변경 시 재실행되는 effect가 입력/분류 로그를 `Promise.all`로 동시 조회, 이전 요청은 AbortController로 취소.
- **Excel 다운로드**: `GET /logs/export`를 `responseType:'blob'`으로 호출, `Content-Disposition` 헤더에서 파일명 파싱 후 `<a download>`로 저장. 별도 controller로 중복 클릭 시 이전 요청 취소. 에러 응답이 Blob으로 오는 케이스를 텍스트로 재파싱해 메시지 추출.
- **호출 API**: `GET /logs/input`, `GET /logs/sort`, `GET /logs/export`.
- **데이터 의존(개념)**: 작업로그의 INPUT/SORT 두 LogType — RCS가 실제 호출한 투입/분류 이벤트 이력.

### 6.3 ResultComparison — 투입/분류/결과 3단 비교

- **목적**: 동일 바코드에 대한 [1]투입 로그, [2]분류 로그, [3]최종 결과 전송(작업결과)을 한 행에 나열해 슈트 일치/불일치, 누락 여부를 판정.
- **UI**: 상단 배치 셀렉트+필터 버튼 3종(전체/불일치/데이터 누락, 카운트 배지), 3단 헤더 그리드, 컬럼별 필터.
- **색상 규칙**: 행 단위 `isMissing`→`row-warning`, `!isMatch`(불일치)→`row-danger`. 셀 단위 `hasInput/hasSort/hasResult`가 false인 칸은 `missing-cell`. 배지: 일치=초록, 누락=회색, 불일치=빨강.
- **필터 로직**: MISMATCH는 `isMatch===false && hasSort && hasResult`(양쪽 존재하는데 슈트가 다른 케이스)인 행만, MISSING은 `isMissing===true`인 행만.
- **호출 API**: `GET /test-data/comparison`.
- **데이터 의존(개념)**: 테스트데이터(등록 원장) 대비 작업로그(INPUT/SORT)+작업결과 3자 조인 비교 — 결과 매칭 키의 Batch 미포함 기존 결함(§3.2.7)과 연결되는 화면.

### 6.4 Settings — 자동생성 규칙 + 인쇄 설정 통합

- **목적**: (1) 미처리 데이터 조회 시 자동 생성될 배치의 규칙(슈트범위/슈트당개수/고정바코드 모드)과 (2) 바코드 라벨 인쇄 용지 크기를 한 화면에서 설정.
- **UI**: 섹션1 좌측 폼(모드 라디오 auto/fixed, 슈트범위, 슈트당개수, 고정모드 시 바코드 목록 textarea)+우측 실시간 미리보기(파싱된 슈트 목록). 섹션2 좌측 인쇄 폭/높이 입력+프리셋 버튼(100×100/100×150/80×40)+우측 용지 비율 시각화.
- **통합 저장**: `handleSave(context)` 단일 함수가 `autoConfig`+`printConfig`를 합친 하나의 payload로 `POST /test-data/auto-config`를 호출, 컨텍스트("auto"|"print")에 따라 성공 토스트 메시지만 분기(두 버튼이 실제로는 같은 엔드포인트/페이로드 저장).
- **preview-chutes 디바운스**: `chuteRange` 변경 시 250ms 디바운스 후 `GET /test-data/preview-chutes` 호출 — 백엔드 `ParseChuteNos`와 동일 결과를 받아옴(프론트 자체 파싱 로직 제거, 단일 소스화). 매 변경마다 이전 타이머+요청 취소.
- **호출 API**: `GET /test-data/auto-config`(최초 로드), `POST /test-data/auto-config`(저장), `GET /test-data/preview-chutes`(디바운스 미리보기).
- **AbortController 주의**: `preview-chutes` 디바운스 effect에만 적용. **초기 `loadConfig()`와 `handleSave()`에는 미적용** — 다른 페이지와 달리 언마운트 시 취소되지 않는 유일한 지점(이식 시 인지 필요).
- **데이터 의존(개념)**: 자동생성설정 엔티티(자동 생성 규칙+인쇄 용지 크기를 단일 레코드로 저장).

### 6.5 Boxes — 박스(카톤) 마감 데이터 조회

- **목적**: 분류 후 마감된 박스 단위 데이터(박스번호/슈트/마감시간)와 그 내품(바코드+수량) 조회.
- **UI**: 상단 배치 셀렉트(날짜는 전역 상태 사용), 좌측 박스 목록 그리드, 우측 마스터-디테일 내품 그리드(좌측 행 클릭 시 우측에 해당 박스 items 표시).
- **플로우**: 배치 필터 변경 시 선택된 박스를 해제해 좌우 불일치 방지.
- **호출 API**: `GET /boxes`(`bizDay` 필수, `batch` 선택).
- **상태 사용**: 인라인 `role="alert"` 배너와 토스트를 **동시에 사용하는 유일한 페이지**.
- **데이터 의존(개념)**: 박스+박스내품 엔티티 — 박스 단위 마감 기록과 그 내품 리스트(바코드/수량)를 부모-자식으로 표현.

### 6.6 AbortController 적용 페이지 요약

| 페이지 | 적용 지점 |
|---|---|
| DataGenerator | 요약+선택배치 상세 폴링 effect |
| Logs | 로그 폴링 effect + Excel 다운로드 전용 controller |
| ResultComparison | 비교 데이터 폴링 effect |
| Boxes | 박스 목록 폴링 effect |
| Settings | `preview-chutes` 디바운스 조회에만 적용; 초기 `loadConfig`/`handleSave`에는 미적용 |

---

## 7. 프론트엔드 — 공용 컴포넌트·API 통합·빌드/보안

### 7.1 공용 컴포넌트

| 컴포넌트 | 역할 | 이식 시 유의점 |
|---|---|---|
| `Layout` | 좌측 접이식 사이드바+우측 메인 2단 구성. `NAV_ITEMS`/`PAGE_TITLES` 매핑으로 라우트별 타이틀. 헤더에 전역 자동새로고침 토글+간격 셀렉터, 전역 작업일자 DatePicker, 다크/라이트 토글을 `GlobalContext`에서 바인딩 | 새 페이지 추가 시 `PAGE_TITLES`/`NAV_ITEMS`에만 등록하면 헤더·사이드바 자동 반영되는 패턴 |
| `FilterInput` | 무상태 컨트롤드 입력. `onClick`에 `stopPropagation()` — 테이블 헤더 클릭(정렬)과 필터 인풋 클릭이 버블링 충돌하지 않도록 방지 | 컬럼 필터 재사용 시 그대로 이식 |
| `PrintLabel` | 화면 내 라벨 카드+`JsBarcode`로 SVG 바코드를 그리는 프리뷰용 컴포넌트 | **실제 인쇄 파이프라인과 분리됨** — `DataGenerator.jsx`에서 `import`만 되고 실제 렌더링 안 되는 **미사용(dead) 임포트**. `no-unused-vars`의 대문자 예외 패턴 때문에 lint에 안 걸림. 이식 시 재현할 아키텍처가 아니라 **정리 대상 후보**로 인지 |
| `ErrorBoundary` | 클래스 컴포넌트, 하위 트리 렌더링 예외 캐치. 폴백 UI에 오류 메시지+새로고침 버튼 | `main.jsx`에서 앱 최상위 1곳에서만 감싸 흰 화면(blank screen) 방지 |

**실제 인쇄 기능의 진짜 구현체**는 `PrintLabel.jsx`가 아니라 `DataGenerator.jsx`의 `handlePrint`다:
- 체크된 항목 없으면 토스트 경고 후 종료.
- `window.open`으로 빈 팝업 생성, `null`이면 팝업 차단 토스트 안내.
- A4 규격 CSS 인라인 삽입 — 99.14×67.48mm 라벨 2열×4행(8칸/페이지), 페이지 여백 상13.97/하13.03/좌4.83/우4.94mm, 모서리 반경 4mm 등 정밀 치수.
- `<script src="${origin}/JsBarcode.all.min.js">`로 **로컬 호스팅된 JsBarcode**를 팝업 문서에 주입(외부 CDN 미사용 — 폐쇄망 대응).
- 라벨 카드는 `innerHTML` 대신 `createElement`/`textContent`로 DOM 조립(XSS 방지).
- `barcode2` 있으면 좌측을 상/하 두 블록으로 분기해 듀얼 바코드 인쇄.
- `JsBarcode` 로드 완료까지 100ms 간격 최대 50회 폴링 후 `print()`→`close()`.

### 7.2 API 통합 계층 (`api/client.js`)

- `axios.create({ baseURL: VITE_API_BASE_URL || '/api', headers:{'Content-Type':'application/json'} })` — 단일 axios 인스턴스를 앱 전역이 재사용.
- `getErrorMessage(error)` 헬퍼 — 에러 메시지 추출 우선순위:
  1. 취소 요청(`ERR_CANCELED`/`CanceledError`)이면 `null` 반환 — 호출부는 `null`이면 토스트 없이 조용히 무시(AbortController와 짝을 이루는 패턴).
  2. `error.response` 있으면 서버 표준 `ApiResponse.Fail` 포맷(`data.message`) 우선, 문자열 응답이면 그대로, 둘 다 아니면 `서버 오류 (status)`.
  3. `error.request`만 있으면(응답 없음) `서버에 연결할 수 없습니다.`
  4. 그 외는 `error.message` 또는 범용 메시지.
- **blob 다운로드+에러 파싱 패턴**(`Logs.jsx`): `responseType:'blob'` 요청 실패 시 응답도 Blob으로 오므로 `.text()`→`JSON.parse`로 `ApiResponse.message` 추출, 실패하면 `getErrorMessage`로 폴백. 성공 시 `Content-Disposition`에서 파일명 파싱 후 `URL.createObjectURL`+`<a download>` 클릭 저장, 즉시 `revokeObjectURL`.
- **AbortController 관용구**:
  - 데이터 조회용: `useEffect` 안 `AbortController` 생성→요청에 `signal` 전달→cleanup에서 `abort()`. 의존성 변경/언마운트 시 이전 요청의 응답이 최신 상태를 덮어쓰지 않도록 방지.
  - 다운로드/버튼 액션용: `useRef`로 컨트롤러 보관 — "이전 요청 있으면 취소 후 새 컨트롤러로 교체" + 언마운트 시 abort.
  - 취소 오류는 `err.code==='ERR_CANCELED' || err.name==='CanceledError'`로 무시.

### 7.3 환경변수/빌드/프록시

- `.env.example`: `VITE_API_BASE_URL=/api`(개발은 프록시 경유 상대경로, 운영은 절대 URL), `VITE_API_PROXY_TARGET=http://localhost:5205`(Vite dev 서버 프록시 대상), `VITE_DEV_PORT=5173`.
- `vite.config.js`: `loadEnv(mode,cwd,'')`로 환경변수 로드 후 `server.port`/`server.proxy['/api']` 구성. `changeOrigin:true`로 CORS 회피. 하드코딩 없이 환경변수화 — 배포 환경 교체 시 `.env.production` 등만 교체하면 됨.

### 7.4 index.html — CSP/로컬 자산

- `Content-Security-Policy`: `default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self' ws: wss: http://localhost:5205; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'`.
  - `script-src 'self'`만 허용 — 외부 CDN 완전 배제. 바코드 라이브러리는 `public/JsBarcode.all.min.js`로 로컬 번들링 → **폐쇄망(인터넷 미연결) 환경에서도 인쇄 기능 동작**하는 핵심 근거.
  - `connect-src`의 `ws: wss:`/`localhost:5205`는 Vite HMR+로컬 백엔드 개발 편의(운영 배포 시 재검토 필요).
- `X-Content-Type-Options: nosniff`, `referrer: strict-origin-when-cross-origin` 메타 적용.

### 7.5 ESLint 규칙

- Flat config, `dist`/`public/` 전역 무시. `vite.config.js`/`eslint.config.js` 자체는 Node 전역 허용.
- 앱 소스: `js.configs.recommended`+`reactHooks.configs.flat.recommended`(훅 규칙)+`reactRefresh.configs.vite`(HMR 안전성).
- 보안/안정성 커스텀 룰: `no-eval`/`no-implied-eval`/`no-new-func`/`no-script-url`(동적 코드 실행·`javascript:` URL 차단), `eqeqeq:always`, `no-var`/`prefer-const`, `no-console`은 `warn`(`warn`/`error` 메서드 예외 허용).
- `no-unused-vars`의 `varsIgnorePattern:'^[A-Z_]'` — 대문자 시작(컴포넌트/상수 관례) 또는 `_` 시작 변수는 미사용이어도 통과. **`PrintLabel` dead import가 걸러지지 않는 원인**이 이 예외 패턴이므로, 이식 시 이 룰을 그대로 가져가면 동일한 사각지대가 재발할 수 있음을 인지.
- **Fast Refresh 훅 분리 패턴**: `XxxContext.jsx`(컴포넌트+Provider export)와 `useXxx.js`(훅만 export)를 별도 파일로 분리 — "컴포넌트 파일에서 훅을 함께 export하면 HMR이 재활성화되지 않는다"는 이유. `GlobalContext`/`useGlobal`, `UiContext`/`useUi` 쌍 모두 동일 패턴.

---

## 8. 기능 인벤토리

사용자 관점에서 본 기능 목록. "이식 필요"는 화면/동작을 재현해야 하는지, "이식 시 유의"는 계약/알고리즘 보존이 중요한지를 나타낸다.

| 기능 | 페이지/API | 유형 | 비고 |
|---|---|---|---|
| 테스트 데이터 수동 생성(슈트범위+바코드개수) | DataGenerator / `POST /test-data/generate` | 화면+API | 라운드로빈 배분 알고리즘 재현 |
| 테스트 데이터 엑셀 업로드 | DataGenerator / `POST /test-data/upload` | 화면+API | 신/구양식 자동판별, 크기/확장자/MIME 검증 |
| 테스트 데이터 요약/상세 조회 | DataGenerator / `GET /test-data/summary,detail` | 화면+API | ChuteNo 메모리 정렬 안전 패턴 |
| 선택 항목 삭제/수신시간 초기화 | DataGenerator / `DELETE /test-data`, `POST /test-data/reset` | 화면+API | Barcode 키 기반 연관 삭제(범위 주의) |
| 드래그/Shift/Ctrl 다중 선택 + 우클릭 메뉴 | DataGenerator | 화면 전용 | UI 인터랙션, DB 무관 |
| 바코드 라벨 인쇄(A4, 듀얼 바코드) | DataGenerator `handlePrint` | 화면 전용 | 로컬 JsBarcode, XSS 방지 DOM 조립 |
| 일괄 수신 초기화(병렬+진행률 토스트) | DataGenerator | 화면 전용 | `Promise.all` 패턴 |
| 투입 데이터 수신(RCS) | `POST /api/v1/works/input` | **RCS 계약** | qty 묶음, int 타입 고정 |
| 분류 데이터 수신(RCS) | `POST /api/v1/works/classification` | **RCS 계약** | 슈트 불일치/완료 판정 우선순위 |
| 작업 결과 전송(RCS) | `POST /api/v1/works/results` | **RCS 계약** | 최상위 배열, 존재검증(슈트 제외) |
| 미작업 데이터 조회(RCS, 부수효과) | `GET /api/v1/works/unprocessed` | **RCS 계약** | 원시 배열 응답 + ReceiveTime 마킹 + 자동생성 트리거 |
| 박스 마감 데이터 수신(RCS) | `POST /api/v1/works/box` | **RCS 계약(신규, Sprint8)** | 중복 거부, barcode 미검증 |
| 투입/분류 로그 조회 | Logs / `GET /logs/input,sort` | 화면+API | N+1 회피(OUTER APPLY), Barcode 단독 매칭 |
| 통합검색+컬럼 필터 | Logs | 화면 전용 | |
| 투입+분류 통합 Excel 내보내기 | Logs / `GET /logs/export` | 화면+API | 하이브리드 페어링(Phase1 TestDataId, Phase2 시간순), 소요시간=분류−투입 |
| 투입/분류/결과 3-way 비교 | ResultComparison / `GET /test-data/comparison` | 화면+API | TestDataId 우선 매칭+Barcode 폴백, 기존 결함(Batch 미포함) |
| 자동생성 규칙 설정(슈트범위/개수/고정바코드) | Settings / `GET,POST /test-data/auto-config` | 화면+API | 단일 레코드 upsert |
| 인쇄 용지 크기 설정 | Settings | 화면+API | auto-config와 동일 엔드포인트 통합 저장 |
| 슈트범위 실시간 미리보기 | Settings / `GET /test-data/preview-chutes` | 화면+API | 프론트-백 파서 단일화(디바운스) |
| 박스 목록/내품 조회(마스터-디테일) | Boxes / `GET /boxes` | 화면+API | |
| API 호출 이력 조회 | (백엔드만, 화면 소비처 명시 안 됨) / `GET /logs/api-calls` | API | 최대 500건, 확인 필요: 프론트 소비 화면 유무 |
| 자동 새로고침 토글(3/5/10/30/60초) | Layout(전역) | 화면 전용 | GlobalContext, localStorage 영속화 |
| 다크/라이트 테마 토글 | Layout(전역) | 화면 전용 | 화이트리스트 검증 |
| 토스트/컨펌 모달 시스템 | 전역(UiContext) | 화면 전용 | `alert`/`confirm` 완전 대체 |
| 헬스체크/큐 상태/OpenAPI 문서 | `/health*`, `/openapi`, `/scalar` | 운영 인프라 | 비즈니스 계약 아님 |

---

## 9. 데이터 의존 지점 (신규 DB 매핑 가이드)

> 컬럼을 나열하지 않고, 각 '데이터 개념'이 무엇을 담는지·누가 읽고 쓰는지·신규 DB 매핑 시 유의점을 정리한다.

### 9.1 테스트데이터 (Test Data)

- **무엇을 담는 개념인가**: "이 업무일자(BizDay)·배치(Batch)에 어떤 바코드가 어느 슈트(ChuteNo)로 배분되어 있는가"를 나타내는 **등록 원장**. RCS 미처리 조회가 소비하는 원천 데이터이자, 투입/분류 요청이 "이 바코드가 정말 등록돼 있는가"를 검증하는 기준.
- **핵심 상태 필드(개념)**: 수신 여부(ReceiveTime 유무 — RCS가 미처리 조회로 "가져갔는지" 마킹), 물품-로그 연결키(TestDataId 개념 — 아래 9.6 참고).
- **읽고/쓰는 기능**: 생성(수동/자동/엑셀업로드), 조회(요약/상세), 삭제/초기화(DataGenerator), 미처리 조회 시 존재 검증(WorkService input/classification/results), 결과비교(LogService)의 기준행.
- **신규 DB 매핑 유의점**:
  - "바코드+업무일자+배치" 조합이 RCS 3대 API(input/classification/results)가 공통으로 조회하는 검증 키다 — 이 3개 필드의 조합 유일성/조회 성능이 보장되어야 한다.
  - ChuteNo는 문자열이되 항상 3자리로 정규화 비교되는 도메인 규칙(`"1"`≠`"001"` 방지)이 있다 — 신규 DB에서도 슈트 값 비교 시 동일한 정규화 규칙을 적용해야 한다.
  - "수신 여부" 개념(ReceiveTime 유무)은 RCS unprocessed 조회의 부수효과(마킹)와 자동생성 트리거 조건(0건일 때)에 직결되므로, 신규 DB에서도 이 상태를 표현할 필드/플래그가 필요하다.

### 9.2 작업로그 (투입 INPUT / 분류 SORT)

- **무엇을 담는 개념인가**: RCS가 실제로 호출해 발생시킨 "이 시각에 이 설비에서 이 바코드를 투입/분류 처리했다"는 **이벤트 기록**. LogType(INPUT/SORT) 하나로 두 종류 이벤트를 표현.
- **읽고/쓰는 기능**: 생성은 WorkService(input/classification API), 조회는 LogService(Logs 페이지, 결과비교, Excel 내보내기).
- **신규 DB 매핑 유의점**:
  - **물품-로그 연결키(TestDataId 개념) 필요**: 정밀 매칭(결과비교, Excel export Phase1)은 이 연결키로 이루어지고, 없거나 실패 시 Barcode 단독 매칭으로 폴백한다. 신규 DB에서도 "이 로그가 테스트데이터의 어느 특정 행에서 발생했는가"를 명확히 연결할 키가 있어야 Barcode 중복(동일 바코드가 여러 Batch에 존재) 상황에서 오매칭을 피할 수 있다.
  - **qty 그룹핑**: 하나의 요청(qty>1)이 물리적으로 여러 개별 로그 행으로 확장 저장된다(요청 1건=행 N개). 미처리 조회는 반대로 "Barcode+ChuteNo가 같은 행 N개"를 "qty=N짜리 그룹 1개"로 압축해 응답한다. 신규 DB에서 이 N-행 전개/압축 관계를 유지할지, 아니면 로그 테이블에 qty 컬럼을 직접 둘지는 설계 선택 사항이나, **"미처리 가용 건수 계산"과 "이미 처리된 TestDataId 집합 조회"라는 조회 패턴 자체는 보존**해야 한다.
  - Status(OK/NG) 값은 업무 로직 분기 없이 그대로 저장되는 이력 값이다.
  - 로그시각(LogTime)은 요청 문자열 파싱 실패 시 서버 현재시각으로 폴백하는 규칙이 있다.

### 9.3 작업결과 (Work Result)

- **무엇을 담는 개념인가**: RCS가 "이 바코드를 이 슈트로 최종 처리 완료했다"고 전송한 **결과 기록**. 분류(SORT) 로그와 별개 개념 — 분류는 "분류 이벤트 발생", 결과는 "최종 결과 전송"이라는 별도 API/시점.
- **읽고/쓰는 기능**: 생성은 WorkService(results API), 조회는 LogService(결과비교).
- **신규 DB 매핑 유의점**:
  - 존재검증은 (BizDay,Batch)당 등록 Barcode 집합을 캐시해 N+1 없이 검증하는 패턴 — 신규 DB에서도 조회 효율을 위해 유사 인덱싱/캐싱 전략이 필요.
  - **ChuteNo는 검증 대상이 아니라 측정 대상**이다 — "SORT.ChuteNo vs RESULT.ChuteNo 불일치"가 업무적으로 의미 있는 신호(현재 결과비교 화면의 `IsMatch` 판정 근거)이므로, 신규 DB 설계 시에도 이 두 값을 별도로 저장해 비교 가능해야 한다.
  - **기존 결함 이월**: 현재 결과 매칭 키가 Batch를 포함하지 않아 같은 BizDay 내 다른 Batch 결과와 오매칭될 수 있다. 신규 구현 시 Batch를 포함한 매칭 키로 설계하는 것을 권장(신규 DB 이식은 이 결함을 고칠 좋은 기회).
  - qty 확장 저장(요청 1건=결과 행 N개)은 작업로그와 동일 패턴이나, **부족 체크(가용성 검증)가 없다**는 점이 작업로그와 다르다 — "미처리 풀 소비" 개념이 아니기 때문.

### 9.4 박스 + 내품 (Box + BoxItem)

- **무엇을 담는 개념인가**: 분류 후 여러 바코드가 하나의 물리적 박스(카톤)로 마감되었다는 **출고 단위 기록**. 박스(BizDay+Batch+BoxNo+ChuteNo+마감시각) 1건에 내품(바코드+수량) N건이 딸린 부모-자식 관계.
- **읽고/쓰는 기능**: 생성/중복검사는 BoxService(box API), 조회는 BoxService(Boxes 페이지, 마스터-디테일).
- **신규 DB 매핑 유의점**:
  - `(BizDay,Batch,BoxNo)` 조합의 **유니크 제약**이 핵심 비즈니스 규칙(재전송 시 거부) — 신규 DB에서도 이 조합에 유니크 제약을 걸어야 한다.
  - **barcode 존재 검증을 하지 않는다**(의도적) — 테스트데이터 등록 여부와 무관하게 "출고된 사실 자체"를 저장하는 목적이므로, 신규 DB에서도 박스 내품을 테스트데이터에 FK로 강결합하면 안 된다(설계 시 주의).
  - 부모-자식 원자 저장(트랜잭션) 요구사항 유지 필요.

### 9.5 자동생성설정 (Auto Generate Config)

- **무엇을 담는 개념인가**: "미처리 데이터가 없을 때 자동으로 생성할 배치의 규칙"(슈트범위, 슈트당 개수, 고정바코드 목록 중 택1 모드)과 "인쇄 라벨 용지 크기"를 함께 담는 **단일 설정 레코드**(다중 레코드 아님, 항상 최신 1건만 유지되는 upsert 대상).
- **읽고/쓰는 기능**: 조회/저장은 AutoGenerateConfigService(Settings 페이지, WorkService의 자동생성 트리거 시 읽기).
- **신규 DB 매핑 유의점**:
  - 단일 레코드(설정값) 개념이므로 신규 DB에서도 "테넌트/환경당 1건" 또는 "가장 최신 1건" 정책을 명확히 해야 한다.
  - 자동생성 트리거(WorkService)와 프리뷰 API(preview-chutes) 양쪽에서 **동일한 슈트 파서 로직**을 공유해야 한다는 제약이 있다 — 신규 DB/백엔드로 이식 시에도 파서를 한 곳에 두고 재사용해야 프론트-백 미리보기 불일치가 발생하지 않는다.

### 9.6 API 호출 로그 (API Call Log)

- **무엇을 담는 개념인가**: RCS가 `/api/v1/`로 호출한 모든 요청/응답의 **감사(audit) 기록**(요청/응답 본문 포함, 민감정보는 마스킹 후 저장). 비즈니스 데이터가 아니라 운영/트러블슈팅용 로그.
- **읽고/쓰는 기능**: 쓰기는 미들웨어→큐→백그라운드 워커, 읽기는 LogService(`GET /logs/api-calls`).
- **신규 DB 매핑 유의점**:
  - **쓰기 경로가 비동기 큐(Bounded, DropOldest)**를 경유한다 — 요청 스레드가 DB 쓰기를 기다리지 않는 것이 설계 의도이므로, 신규 DB로 이식 시에도 동기 쓰기로 되돌리면 RCS 응답 지연을 유발할 수 있다.
  - **로그 유실이 허용**된다(큐 가득 참 시 DropOldest, 저장 실패 시 무시) — 가용성 우선 정책. 신규 DB에서 "무손실 로그"가 요구사항이라면 이 정책 자체를 재검토해야 한다.
  - 응답 본문은 Content-Type 조건부 캡처(JSON/XML/text만) — 바이너리/스트리밍 응답은 저장하지 않는 규칙.

### 9.7 물품-로그 연결키(TestDataId) 개념의 전사적 중요성

- 결과비교(§3.2.7)와 Excel export 하이브리드 페어링(§3.2.8) 모두 **"이 로그가 테스트데이터의 어느 특정 행에서 발생했는가"를 나타내는 연결키**의 유무에 따라 정밀 매칭(연결키 우선)과 폴백 매칭(Barcode/시간순)을 이원화하고 있다.
- 로그 조회(§3.2.6)는 이 연결키를 쓰지 않고 Barcode 단독 매칭만 하는 **더 느슨한 경로**다.
- **신규 DB 설계 시 결정 필요 사항**: (1) 모든 로그 생성 경로에서 이 연결키를 항상 채우도록 강제할 것인가(현재는 정밀 매칭의 전제조건이나 항상 보장되지는 않는 것으로 보임 — Phase2 폴백의 존재가 그 방증), (2) 로그 조회 화면도 결과비교와 동일한 정밀도로 통일할 것인가. 이는 컬럼 설계가 아니라 **매칭 전략의 일관성 문제**이므로 이식 설계 단계에서 명시적으로 결정해야 한다.

---

## 10. 빌드·실행·배포 요약

| 항목 | 명령/설정 |
|---|---|
| 백엔드 빌드 | `dotnet build backend/BowooTestBatchApi.csproj` |
| 백엔드 실행 | `dotnet run --project backend/BowooTestBatchApi.csproj` |
| 백엔드 개발 포트 | `http://localhost:5205`(launchSettings.json), 운영은 `ASPNETCORE_URLS`로 override |
| 프론트 개발 | `cd frontend && npm install && npm run dev` |
| 프론트 빌드 | `npm run build` → 산출물을 `backend/wwwroot/`로 복사(Single-Port 배포) |
| 프론트 개발 포트 | `VITE_DEV_PORT=5173`(`.env.example`) |
| 린트 | `cd frontend && npx eslint .` |
| DB 연결 주입 | 환경변수 `ConnectionStrings__DefaultConnection` 또는 User Secrets(운영), `appsettings.Development.json`(개발) |
| CORS 허용 origin | `Cors:AllowedOrigins`(appsettings 배열) |
| HTTPS 강제 | `Https:Force`(기본 `!IsDevelopment()`, 환경변수 `Https__Force`로 override) |
| 헬스체크 | `/health`, `/health/ready`, `/health/queue` |
| API 문서 | `/openapi/v1.json`(전 환경), `/scalar`(개발 환경 한정) |

---

## 11. 이식 체크리스트

### 11.1 API 계약 (변경 금지 최우선)
- [ ] RCS 4개 API(`input`/`classification`/`results`/`unprocessed`) + `box` API의 **필드명·타입·검증 정규식·qty 상한(9999)**을 그대로 재현. 특히 `PId`/`InductionNo`는 **int 고정**(과거 string 변경 시도로 회귀 발생 전례).
- [ ] `Status` 두 도메인(RCS `OK`/`NG` vs `ApiResponse` `S`/`F`)을 혼동하지 않고 그대로 분리 유지.
- [ ] 비즈니스 실패는 HTTP 200+`{Status:"F"}`, DTO 검증 실패는 400, 미처리 예외는 500이라는 **3단 구분**을 동일하게 재현.
- [ ] `unprocessed`의 원시 배열 응답 + 부수효과(ReceiveTime 마킹, 자동생성 트리거)를 유지.
- [ ] `results`의 요청 바디가 최상위 JSON 배열이라는 형태를 유지.

### 11.2 비즈니스 로직/알고리즘
- [ ] qty 묶음 처리(투입/분류): "가용 행 < qty면 부분 처리 없이 전체 거부" 원칙 재현.
- [ ] 미처리 그룹핑(Batch→Barcode+ChuteNo 2단계, qty=Count 산출) 재현.
- [ ] results 존재검증(사전 전체 거부, ChuteNo는 검증 제외) 재현.
- [ ] 박스 중복검사(`BizDay+Batch+BoxNo` 유니크) + barcode 미검증 정책 재현.
- [ ] 결과비교 3-way 매칭(TestDataId 우선+Barcode 폴백, 중복 사용 방지) 재현 — **Batch 미포함 매칭 키 결함은 이식 시 교정 권장**.
- [ ] Excel export 하이브리드 페어링(Phase1 TestDataId, Phase2 시간순 zip) + 소요시간 산식(분류−투입, 음수 제외) 재현.
- [ ] 인덕션→층 매핑(1·2=2층, 3·4=1층) 하드코딩 규칙 재현.
- [ ] 슈트 파싱(`"a-b"` 범위 전개, 중복 제거, 정렬)과 ChuteNo 3자리 zero-pad를 전사 공통 유틸로 유지.
- [ ] ChuteNo 문자열 정렬 시 메모리 내 숫자 우선 정렬 패턴 재현(상세 조회).
- [ ] 연관 삭제(초기화/삭제)가 Barcode 키 기반이라는 점과 그 범위 리스크를 신규 설계에서 재검토(TestDataId 기반으로 좁히는 것을 고려).

### 11.3 인프라/크로스커팅
- [ ] 미들웨어 순서(예외 미들웨어 최상단 → 정적파일 → CORS → RateLimiter → RCS 로깅 → 컨트롤러) 재현.
- [ ] RCS API 전용 로그 파일 분리(90일 보관) + 앱 로그(30일 보관) 이중 기록 구조 재현 여부 결정.
- [ ] 민감정보 마스킹(로그 기록 직전에만 적용, 실제 처리에는 원본 전달) 재현.
- [ ] 비동기 API 호출 로그 큐(Bounded, DropOldest, 유실 허용) 패턴 재현 — 요청 스레드 블로킹 방지가 핵심 의도.
- [ ] Rate Limiting(IP당 분당 300건), HSTS/HTTPS 강제 스위치(`Https:Force`), CORS 화이트리스트 재현.
- [ ] 파일 업로드 3중 검증(크기/확장자/MIME) 재현.
- [ ] ModelState 검증 실패의 표준 `ApiResponse.Fail` 포맷 통일 재현.
- [ ] 헬스체크(`/health`, `/health/ready`, `/health/queue`) 및 임계값(70%/90%) 재현 여부 결정.
- [ ] Single-Port SPA+API 서빙 구조(정적파일이 CORS/RateLimit보다 먼저 처리됨) 재현 여부 결정.
- [ ] 인증/인가는 현재 미구현 — 신규 프로그램에서 필요하면 별도 설계.

### 11.4 프론트엔드
- [ ] 라우팅 5개 페이지(`/data-generator`,`/logs`,`/comparison`,`/settings`,`/boxes`) + 404 재현.
- [ ] `GlobalContext`(업무일자/테마/자동새로고침, localStorage 화이트리스트 검증)와 `UiContext`(토스트/컨펌) 재현.
- [ ] Context+훅 분리 파일 쌍(`XxxContext.jsx`/`useXxx.js`) 컨벤션 유지.
- [ ] 각 페이지의 AbortController 적용 패턴(조회는 effect형, 다운로드는 ref형) 재현. **Settings의 `loadConfig`/`handleSave` 미적용은 알려진 사각지대**로 재이식 시 개선 검토.
- [ ] Logs 페이지가 "탭"이 아니라 "좌우 2열 동시 표시"라는 실제 UI 형태를 정확히 재현(문서화 오인 방지).
- [ ] DataGenerator의 드래그/Shift/Ctrl 다중 선택, 우클릭 메뉴, A4 인쇄(로컬 JsBarcode, 듀얼 바코드) 재현.
- [ ] `PrintLabel.jsx`는 실제 인쇄 파이프라인이 아님(dead import) — 이식 시 `DataGenerator.jsx`의 `handlePrint`를 기준으로 재구현하고 `PrintLabel`은 정리 대상으로 별도 판단.
- [ ] CSP `script-src 'self'` + 외부 스크립트(JsBarcode 등) 로컬 사본 배치(`public/`) 유지 — 폐쇄망 대응 핵심.
- [ ] `.env` 3개 변수(`VITE_API_BASE_URL`/`VITE_API_PROXY_TARGET`/`VITE_DEV_PORT`) + `vite.config.js` 프록시 구성 재현.
- [ ] ESLint 보안 룰셋(`no-eval` 계열, `eqeqeq`, `no-var`) 이식하되 `no-unused-vars` 대문자 예외가 dead-import를 숨길 수 있음을 리뷰 프로세스에 반영.

### 11.5 데이터 개념 매핑 (신규 DB 설계 시 필수 결정 사항)
- [ ] 테스트데이터: "Barcode+BizDay+Batch" 조회 키 성능, ChuteNo 정규화 비교, 수신 여부(ReceiveTime 개념) 표현 방식 확정.
- [ ] 작업로그: 물품-로그 연결키(TestDataId 개념) 도입 여부와 항상 채워지도록 보장할지 결정. qty 확장 저장 vs qty 컬럼 직접 보유 중 택1.
- [ ] 작업결과: 매칭 키에 Batch 포함(기존 결함 교정), ChuteNo는 검증이 아닌 "비교 대상"으로 별도 보관.
- [ ] 박스+내품: `(BizDay,Batch,BoxNo)` 유니크 제약, barcode 존재검증 없음(FK 강결합 금지) 정책 유지.
- [ ] 자동생성설정: 단일 레코드(최신 1건) 정책, 슈트 파서 단일 소스 유지.
- [ ] API 호출 로그: 비동기 쓰기+유실 허용 정책을 신규 요구사항(예: 무손실 감사로그 필요 여부)에 맞게 재검토.

### 11.6 확인 필요 (본 분석 자료에서 확정하지 못한 사항)
- [ ] `GET /api/logs/api-calls`(API 호출 이력 조회)를 실제로 소비하는 프론트 화면이 분석 자료상 명시되지 않음 — 별도 화면 존재 여부 확인 필요.
- [ ] "빈 바코드 + NG Input 처리" 정책(사용자 메모리 기록상 미확정) — RCS 스펙 확인 대기 상태이므로 신규 설계 시 확정 필요.

