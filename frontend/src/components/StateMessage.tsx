import { AlertTriangle, Loader2, Inbox } from 'lucide-react'

// 로딩/에러/빈-상태 표시 — 무한 스피너·앱 크래시 아님(계약 Slot1 empty/error).
export function LoadingRow({ label = '불러오는 중' }: { label?: string }) {
  return (
    <div className="flex items-center justify-center gap-2 px-4 py-10 text-[13px] text-muted">
      <Loader2 className="size-4 animate-spin" />
      {label}
    </div>
  )
}

export function ErrorRow({ message }: { message: string }) {
  return (
    <div className="flex items-center justify-center gap-2 px-4 py-10 text-[13px] text-offline">
      <AlertTriangle className="size-4" />
      데이터를 불러오지 못했습니다 — {message}
    </div>
  )
}

export function EmptyRow({ label = '표시할 데이터가 없습니다' }: { label?: string }) {
  return (
    <div className="flex items-center justify-center gap-2 px-4 py-10 text-[13px] text-faint">
      <Inbox className="size-4" />
      {label}
    </div>
  )
}
