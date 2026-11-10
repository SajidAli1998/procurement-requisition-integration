import * as React from "react";
import { cva, type VariantProps } from "class-variance-authority";
import { cn } from "@/lib/utils";

export const badgeVariants = cva(
  "inline-flex items-center gap-1 rounded-md border px-2 py-0.5 text-xs font-medium whitespace-nowrap [&_svg]:size-3",
  {
    variants: {
      tone: {
        success: "border-emerald-600/30 bg-emerald-50 text-emerald-800 dark:bg-emerald-950/60 dark:text-emerald-300 dark:border-emerald-400/30",
        info: "border-sky-600/30 bg-sky-50 text-sky-800 dark:bg-sky-950/60 dark:text-sky-300 dark:border-sky-400/30",
        accent: "border-violet-600/30 bg-violet-50 text-violet-800 dark:bg-violet-950/60 dark:text-violet-300 dark:border-violet-400/30",
        danger: "border-red-600/30 bg-red-50 text-red-800 dark:bg-red-950/60 dark:text-red-300 dark:border-red-400/30",
        warning: "border-amber-600/40 bg-amber-50 text-amber-900 dark:bg-amber-950/60 dark:text-amber-300 dark:border-amber-400/30",
        critical: "border-orange-600/40 bg-orange-50 text-orange-900 dark:bg-orange-950/60 dark:text-orange-300 dark:border-orange-400/30",
        muted: "border-slate-400/40 bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300 dark:border-slate-500/40",
        neutral: "border-border bg-background text-foreground",
      },
    },
    defaultVariants: { tone: "neutral" },
  },
);

export interface BadgeProps extends React.HTMLAttributes<HTMLSpanElement>, VariantProps<typeof badgeVariants> {}

export function Badge({ className, tone, ...props }: BadgeProps) {
  return <span className={cn(badgeVariants({ tone }), className)} {...props} />;
}
