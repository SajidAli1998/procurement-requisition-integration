import { CheckCircle2, CircleSlash, ShieldAlert } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { JsonViewer } from "@/components/monaco";
import type { JsonValue, ValidationProblem, ValidationResult } from "@/lib/types";

function valueText(v: JsonValue | undefined): string {
  if (v === undefined || v === null) return "null (missing)";
  if (typeof v === "string") return v === "" ? '"" (empty)' : v;
  return JSON.stringify(v);
}

export function verdict(result: ValidationResult): { label: string; variant: "success" | "warning" | "destructive"; text: string } {
  if (result.valid && result.eligible) return { label: "Valid and eligible", variant: "success", text: "Every rule passes and the record is approved: the next run sends it to SAP." };
  if (result.valid && !result.approved)
    return { label: "Valid but not approved (will be skipped)", variant: "warning", text: `Status is "${result.status ?? "(none)"}": the integration skips it and never sends it to SAP.` };
  if (!result.approved)
    return { label: "Not approved (will be skipped); also invalid", variant: "warning", text: "The integration skips records that are not approved; fix the problems below before approving." };
  return { label: "Invalid (will be quarantined)", variant: "destructive", text: "The next run holds this record in quarantine and does not send it to SAP." };
}

function ProblemsTable({ problems }: { problems: ValidationProblem[] }) {
  if (problems.length === 0) return <p className="text-sm text-muted-foreground">No rule fails.</p>;
  return (
    <div className="overflow-x-auto rounded-md border">
      <table className="w-full text-sm" data-testid="validation-problems">
        <caption className="sr-only">Failing validation rules</caption>
        <thead className="bg-muted/40 text-xs text-muted-foreground">
          <tr>
            <th scope="col" className="px-2 py-1.5 text-left font-semibold">Reason</th>
            <th scope="col" className="px-2 py-1.5 text-left font-semibold">Field</th>
            <th scope="col" className="px-2 py-1.5 text-left font-semibold">Value</th>
            <th scope="col" className="px-2 py-1.5 text-left font-semibold">Message</th>
          </tr>
        </thead>
        <tbody>
          {problems.map((p, i) => (
            <tr key={`${p.field}-${i}`} className="border-t align-top">
              <td className="px-2 py-1.5 font-mono text-xs whitespace-nowrap">{p.reason}</td>
              <td className="px-2 py-1.5 font-mono text-xs break-anywhere">{p.field || "(record)"}</td>
              <td className="max-w-[16rem] px-2 py-1.5 font-mono text-xs break-anywhere">{valueText(p.value)}</td>
              <td className="px-2 py-1.5 break-anywhere">{p.message}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

export function ValidationResultPanel({ result, showPayload = true }: { result: ValidationResult; showPayload?: boolean }) {
  const v = verdict(result);
  const Icon = v.variant === "success" ? CheckCircle2 : v.variant === "warning" ? CircleSlash : ShieldAlert;
  return (
    <div className="space-y-3" data-testid="validation-result" data-valid={result.valid} data-eligible={result.eligible}>
      <Alert variant={v.variant}>
        <Icon />
        <AlertTitle data-testid="validation-verdict">{v.label}</AlertTitle>
        <AlertDescription>
          <p>{v.text}</p>
          <p className="mt-1 text-xs">
            {result.problems.length} failing rule{result.problems.length === 1 ? "" : "s"} · status {result.status ?? "(none)"} · checked by the integration&apos;s own parser and
            validator; nothing was stored.
          </p>
        </AlertDescription>
      </Alert>
      <ProblemsTable problems={result.problems} />
      {showPayload ? (
        <div>
          <h3 className="mb-1 text-sm font-medium">SAP payload preview</h3>
          {result.sapPayload ? (
            <JsonViewer value={result.sapPayload} maxHeight={360} label="SAP payload preview" />
          ) : (
            <p className="text-sm text-muted-foreground">No SAP payload: the record could not be parsed into a requisition.</p>
          )}
        </div>
      ) : null}
    </div>
  );
}
