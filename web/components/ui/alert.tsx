import * as React from "react";
import { cva, type VariantProps } from "class-variance-authority";
import { cn } from "@/lib/utils";

const alertVariants = cva("relative w-full rounded-lg border px-4 py-3 text-sm [&>svg]:absolute [&>svg]:left-4 [&>svg]:top-3.5 [&>svg]:size-4 [&>svg~*]:pl-7", {
  variants: {
    variant: {
      default: "bg-background text-foreground",
      info: "border-sky-600/30 bg-sky-50 text-sky-950 dark:bg-sky-950/40 dark:text-sky-100 [&>svg]:text-sky-600",
      warning: "border-amber-600/40 bg-amber-50 text-amber-950 dark:bg-amber-950/40 dark:text-amber-100 [&>svg]:text-amber-600",
      destructive: "border-red-600/40 bg-red-50 text-red-950 dark:bg-red-950/40 dark:text-red-100 [&>svg]:text-red-600",
      success: "border-emerald-600/30 bg-emerald-50 text-emerald-950 dark:bg-emerald-950/40 dark:text-emerald-100 [&>svg]:text-emerald-600",
    },
  },
  defaultVariants: { variant: "default" },
});

export function Alert({ className, variant, ...props }: React.HTMLAttributes<HTMLDivElement> & VariantProps<typeof alertVariants>) {
  return <div role="alert" className={cn(alertVariants({ variant }), className)} {...props} />;
}

export function AlertTitle({ className, ...props }: React.HTMLAttributes<HTMLHeadingElement>) {
  return <h3 className={cn("mb-1 font-semibold leading-tight", className)} {...props} />;
}

export function AlertDescription({ className, ...props }: React.HTMLAttributes<HTMLDivElement>) {
  return <div className={cn("text-sm [overflow-wrap:anywhere] [&_p]:leading-relaxed", className)} {...props} />;
}
