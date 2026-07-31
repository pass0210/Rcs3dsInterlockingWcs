// ═══════════════════════════════════════════════════════════════════════════
// 평균 사이클 시간(분류시작~복귀) 표시 상수·포맷터 — 단일 소스(절대규칙 #7).
//   소수 자리·디바운스 간격·트리거 이벤트 번호·카피 문구·포맷터를 한 곳에 모아 하드코딩 산재 0.
//   백엔드 GET /api/monitor/cycle-time-avg 는 raw double(초)을 반환 → 표기 포맷은 여기서만.
// ═══════════════════════════════════════════════════════════════════════════

/** 평균값 소수 자리수(표기). */
export const CYCLE_TIME_DECIMALS = 1

/** 실시간 재조회 디바운스 간격(ms) — event 9 연쇄 도착 시 재조회 폭주 억제. */
export const CYCLE_TIME_REFETCH_DEBOUNCE_MS = 500

/** 실시간 갱신 트리거 트레이스 이벤트 번호 = 9(READY_0TO1 = 복귀 완료 = 신규 ReturnedAt). */
export const CYCLE_TIME_TRIGGER_EVENT_NO = 9

/** 표시 카피(단일 소스). */
export const CYCLE_TIME_COPY = {
  /** 주표기 라벨. */
  label: '평균 사이클 시간(분류시작~복귀)',
  /** 수식 부기(툴팁/부제). */
  formula: 'Σ(복귀−분류시작)/N',
  /** n=0 빈 상태 문구. */
  empty: '측정 데이터 없음',
  /** 값 없음(빈/실패) placeholder. */
  placeholder: '—',
  /** 초기 로딩 표기. */
  loading: '…',
  /** 초 단위 접미사. */
  unit: '초',
} as const

/**
 * 평균 사이클 시간 표시 문자열. avgSeconds=null 또는 n=0 → placeholder("—").
 *   예: "12.3초 · n=5".
 */
export function formatCycleTime(avgSeconds: number | null, n: number): string {
  if (avgSeconds === null || n === 0) return CYCLE_TIME_COPY.placeholder
  return `${avgSeconds.toFixed(CYCLE_TIME_DECIMALS)}${CYCLE_TIME_COPY.unit} · n=${n}`
}
