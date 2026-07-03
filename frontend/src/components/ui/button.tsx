import type * as React from 'react'
import { cva, type VariantProps } from 'class-variance-authority'
import { cn } from '@/lib/utils'

const buttonVariants = cva(
  'inline-flex items-center justify-center gap-2 rounded-lg text-[13px] font-medium transition-colors disabled:pointer-events-none disabled:opacity-40 focus-visible:outline-2 focus-visible:outline-ink',
  {
    variants: {
      variant: {
        // primary — Rausch fill, white, 프레스 시 brand-active (F2/F3 CTA 상속)
        solid:   'bg-brand text-white border border-transparent hover:bg-brand-active disabled:bg-brand-disabled',
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
