import type * as React from 'react'
import { cva, type VariantProps } from 'class-variance-authority'
import { cn } from '@/lib/utils'

const buttonVariants = cva(
  'inline-flex items-center justify-center gap-2 rounded-md text-[13px] font-medium transition-colors disabled:pointer-events-none disabled:opacity-40 focus-visible:outline-2 focus-visible:outline-accent',
  {
    variants: {
      variant: {
        solid:   'bg-accent/15 text-accent border border-accent/30 hover:bg-accent/25',
        outline: 'border border-line bg-elevated text-ink hover:bg-line/50',
        ghost:   'text-muted hover:bg-elevated hover:text-ink',
      },
      size: {
        sm: 'h-7 px-2.5',
        md: 'h-8 px-3',
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
