import { Inbox } from "lucide-react";
import { cn } from "@/lib/utils";

export function EmptyState({ title, description, action, className, icon: Icon = Inbox }: { title: string; description?: React.ReactNode; action?: React.ReactNode; className?: string; icon?: React.ComponentType<{ className?: string }> }) {
  return (
    <div className={cn("flex flex-col items-center justify-center gap-2 rounded-lg border border-dashed p-8 text-center", className)} data-testid="empty-state">
      <Icon className="size-8 text-muted-foreground" />
      <p className="text-sm font-medium">{title}</p>
      {description ? <div className="max-w-md text-sm text-muted-foreground">{description}</div> : null}
      {action}
    </div>
  );
}
