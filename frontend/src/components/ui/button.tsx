import type * as React from 'react'
import { cva, type VariantProps } from 'class-variance-authority'
import { cn } from '@/lib/utils'

const buttonVariants = cva(
  'inline-flex items-center justify-center gap-2 rounded-lg text-[13px] font-medium transition-colors disabled:pointer-events-none disabled:opacity-40 focus-visible:outline-2 focus-visible:outline-ink',
  {
    variants: {
      variant: {
        // primary — Rausch fill(안정 상태 brand-active #e00b41, 백 라벨 AA 4.89:1 — RESTYLE-CR-M1).
        // 안정/hover 모두 brand-active 유지(hover에서 밝은 #ff385c로 되돌리면 3.52:1로 AA 미달하므로 고정).
        solid:   'bg-brand-active text-white border border-transparent disabled:bg-brand-disabled',
        // secondary — white + 잉크 아웃라인
        outline: 'border border-ink bg-panel text-ink hover:bg-elevated',
        ghost:   'text-muted hover:bg-elevated hover:text-ink',
      },
      size: {
        sm: 'h-8 px-3',
        md: 'h-10 px-5',
      },
    },
    defaultVariants: { variant: 'outline', size: 'md' },
  },
)

interface ButtonProps
  extends React.ComponentProps<'button'>,
    VariantProps<typeof buttonVariants> {}

export function Button({ className, variant, size, ...props }: ButtonProps) {
  return <button className={cn(buttonVariants({ variant, size }), className)} {...props} />
}
