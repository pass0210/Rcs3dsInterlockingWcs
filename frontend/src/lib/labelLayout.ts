// ═══════════════════════════════════════════════════════════════════════════
// A4 2×4 라벨 정본(canonical) 기하 — docs/PROGRAM_STRUCTURE.md §6.1·§7.1 재현 대상.
//   이 값들은 임의 상수가 아니라 원본이 명시한 라벨 규격(source of truth)이다.
//   2열×4행(8칸/페이지), 라벨 99.14×67.48mm, 페이지 여백 상13.97/하13.03/좌4.83/우4.94mm, 모서리 반경 4mm.
// ═══════════════════════════════════════════════════════════════════════════
export const A4_LABEL = {
  pageWidthMm: 210,
  pageHeightMm: 297,
  marginTopMm: 13.97,
  marginBottomMm: 13.03,
  marginLeftMm: 4.83,
  marginRightMm: 4.94,
  labelWidthMm: 99.14,
  labelHeightMm: 67.48,
  cornerRadiusMm: 4,
  cols: 2,
  rows: 4,
  perPage: 8,
} as const

// 열 간격 = 사용 폭 − (좌우 여백 + 2×라벨 폭). 스펙값에서 계산으로 유도(≈1.95mm).
export const COLUMN_GAP_MM =
  A4_LABEL.pageWidthMm -
  A4_LABEL.marginLeftMm -
  A4_LABEL.marginRightMm -
  A4_LABEL.cols * A4_LABEL.labelWidthMm

/** N개 항목을 size 씩 끊어 페이지 배열로 분할(8칸/페이지 페이지네이션). */
export function chunk<T>(items: readonly T[], size: number): T[][] {
  const out: T[][] = []
  for (let i = 0; i < items.length; i += size) out.push(items.slice(i, i + size))
  return out
}

/** 슈트번호 3자리 zero-pad 표기(숫자만 대상 — 원본 ChuteNoFormat "D3" 재현). 비숫자는 원문 유지. */
export function padChute(chuteNo: string): string {
  return /^\d+$/.test(chuteNo) ? chuteNo.padStart(3, '0') : chuteNo
}
