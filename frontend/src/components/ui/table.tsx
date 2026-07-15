import type * as React from 'react'
import { cn } from '@/lib/utils'

// 밀집 데이터 테이블 프리미티브. 스크롤(가로·세로)은 감싸는 컨테이너(CardContent/스크롤 본문 div,
// overflow-auto)가 담당한다 — page body 미스크롤. ★ S-UI-LAYOUT: 래퍼 div 에서 overflow-x-auto 를
// 제거한다. 그 값은 CSS 명세상 overflow-y 도 auto 로 승격시켜 래퍼를 별도 스크롤 컨테이너로 만들었고,
// 그 결과 sticky thead 가 (세로로 스크롤하지 않는) 이 래퍼에 붙어 상위 컨테이너 스크롤을 따라가지 못했다.
// 래퍼에서 overflow 를 없애면 sticky thead 가 감싸는 단일 스크롤 컨테이너에 정확히 고정된다(양축 스크롤 일원화).
export function Table({ className, ...props }: React.ComponentProps<'table'>) {
  return (
    <div className="w-full">
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
