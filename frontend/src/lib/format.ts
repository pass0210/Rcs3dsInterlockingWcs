// 표시 포맷 헬퍼 — 계기판 판독값 정서(모노스페이스는 컴포넌트 클래스에서).

/** UTC ISO 문자열 → 로컬 "MM-DD HH:mm:ss". null이면 대시. */
export function fmtTime(iso: string | null | undefined): string {
  if (!iso) return '—'
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return '—'
  const p = (n: number) => String(n).padStart(2, '0')
  return `${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`
}

/** "HH:mm:ss" (상단바 마지막 갱신 표시). */
export function fmtClock(d: Date): string {
  const p = (n: number) => String(n).padStart(2, '0')
  return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`
}

/** null/undefined → 대시. */
export function dash(v: string | number | null | undefined): string {
  return v === null || v === undefined || v === '' ? '—' : String(v)
}
