import type * as React from 'react'
import { ChevronDown } from 'lucide-react'
import { cn } from '@/lib/utils'

// 스타일드 네이티브 select — 접근성/키보드 기본 제공, 의존성 최소(밀집 운영툴 적합).
export function Select({ className, children, ...props }: React.ComponentProps<'select'>) {
  return (
    <div className="relative inline-flex">
      <select
        className={cn(
          'h-8 appearance-none rounded-lg border border-line bg-panel pl-3 pr-8 text-[13px] text-ink',
          'hover:border-ink/40 focus-visible:outline-2 focus-visible:outline-ink',
          'disabled:opacity-40',
          className,
        )}
        {...props}
      >
        {children}
      </select>
      <ChevronDown className="pointer-events-none absolute right-2 top-1/2 size-4 -translate-y-1/2 text-muted" />
    </div>
  )
}
