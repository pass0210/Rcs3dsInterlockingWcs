import type * as React from 'react'
import { cva, type VariantProps } from 'class-variance-authority'
import { cn } from '@/lib/utils'

// 상태 태그 — 계기판 상태색 체계. 은은한 배경 + 선명한 텍스트/도트로 밀집 가독.
const badgeVariants = cva(
  'inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5 text-[11px] font-medium leading-none whitespace-nowrap',
  {
    variants: {
      tone: {
        neutral: 'border-line bg-elevated text-muted',
        online:  'border-online/30 bg-online/10 text-online',
        offline: 'border-offline/30 bg-offline/10 text-offline',
        warn:    'border-warn/30 bg-warn/10 text-warn',
        busy:    'border-busy/30 bg-busy/10 text-busy',
        accent:  'border-accent/30 bg-accent/10 text-accent',
      },
    },
    defaultVariants: { tone: 'neutral' },
  },
)

interface BadgeProps
  extends React.ComponentProps<'span'>,
    VariantProps<typeof badgeVariants> {
  dot?: boolean
}

export function Badge({ className, tone, dot, children, ...props }: BadgeProps) {
  return (
    <span className={cn(badgeVariants({ tone }), className)} {...props}>
      {dot && <span className="size-1.5 rounded-full bg-current" />}
      {children}
    </span>
  )
}
