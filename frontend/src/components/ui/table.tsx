import type * as React from 'react'
import { cn } from '@/lib/utils'

// 밀집 데이터 테이블 프리미티브. 가로 스크롤은 감싸는 컨테이너가 담당(page body 미스크롤).
export function Table({ className, ...props }: React.ComponentProps<'table'>) {
  return (
    <div className="w-full overflow-x-auto">
      <table className={cn('w-full border-collapse text-[13px]', className)} {...props} />
    </div>
  )
}

export function THead({ className, ...props }: React.ComponentProps<'thead'>) {
  return (
    <thead
      className={cn(
        'sticky top-0 z-10 bg-elevated text-[11px] font-semibold uppercase tracking-wider text-muted',
        className,
      )}
      {...props}
    />
  )
}

export function TBody({ className, ...props }: React.ComponentProps<'tbody'>) {
  return <tbody className={className} {...props} />
}

export function TR({ className, ...props }: React.ComponentProps<'tr'>) {
  return (
    <tr className={cn('border-b border-line/60 hover:bg-elevated/50', className)} {...props} />
  )
}

export function TH({ className, ...props }: React.ComponentProps<'th'>) {
  return (
    <th className={cn('px-3 py-2 text-left font-semibold whitespace-nowrap', className)} {...props} />
  )
}

export function TD({ className, ...props }: React.ComponentProps<'td'>) {
  return <td className={cn('px-3 py-2 align-middle', className)} {...props} />
}
