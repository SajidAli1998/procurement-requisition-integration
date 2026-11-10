import { FlaskConical } from "lucide-react";

export function IsolatedBadge() {
  return (
    <span
      className="inline-flex items-center gap-1.5 rounded-full border border-amber-600/40 bg-amber-50 px-2.5 py-1 text-xs font-semibold text-amber-900 dark:bg-amber-950/60 dark:text-amber-200"
      data-testid="isolated-badge"
      title="Everything here runs against in-memory mocks inside the Test Lab. Nothing reaches a real SAP or source system."
    >
      <FlaskConical className="size-3.5" aria-hidden />
      Isolated lab, no real SAP
    </span>
  );
}
