"use client";

import { AlertOctagon, KeyRound, RefreshCw, ServerCrash, WifiOff } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@/components/ui/collapsible";
import { ApiError, isApiError } from "@/lib/api";

function describeError(error: unknown): { title: string; body: string; hint?: string; icon: React.ComponentType<{ className?: string }> } {
  if (isApiError(error)) {
    switch (error.kind) {
      case "unauthorized":
        return {
          title: "Lab key rejected",
          body: `The Test Lab API refused the request (HTTP ${error.status}): ${error.problem?.detail ?? error.message}`,
          hint: "Your lab session is not authorised. The web server's TESTLAB_KEY does not match the key the Test Lab API was started with. Fix web/.env.local and restart the web server. Nothing was changed in the lab.",
          icon: KeyRound,
        };
      case "network":
        return {
          title: "Connection lost",
          body: "The browser could not reach the web server. Check your network connection. The page tries again once it is back.",
          icon: WifiOff,
        };
      case "timeout":
        return { title: "The lab did not answer in time", body: error.message, hint: "The lab may be busy or slow. Retry, or check the Test Lab API log.", icon: WifiOff };
      case "schema":
        return {
          title: "Unexpected response from the lab",
          body: error.message,
          hint: "The API returned data in a shape this page does not understand, so nothing is shown rather than something wrong. Check that the web app and the Test Lab API versions match.",
          icon: AlertOctagon,
        };
      default:
        if (error.status === 502 || error.status === 503 || error.status === 504) {
          return {
            title: "Test Lab API unavailable",
            body: error.problem?.detail ?? error.message,
            hint: "Start the Test Lab API (see README) and retry. No data is shown until it answers.",
            icon: ServerCrash,
          };
        }
        return { title: error.problem?.title ?? `Request failed (HTTP ${error.status})`, body: error.problem?.detail ?? error.message, icon: AlertOctagon };
    }
  }
  return { title: "Something went wrong", body: error instanceof Error ? error.message : String(error), icon: AlertOctagon };
}

export function ErrorPanel({ error, onRetry, title, compact }: { error: unknown; onRetry?: () => void; title?: string; compact?: boolean }) {
  const d = describeError(error);
  const Icon = d.icon;
  const api = isApiError(error) ? (error as ApiError) : null;
  return (
    <Alert variant="destructive" data-testid="error-panel" data-error-kind={api?.kind ?? "unknown"}>
      <Icon />
      <AlertTitle>{title ? `${title}: ${d.title}` : d.title}</AlertTitle>
      <AlertDescription>
        <p className="break-anywhere">{d.body}</p>
        {d.hint ? <p className="mt-1 text-xs opacity-90">{d.hint}</p> : null}
        {api && !compact ? (
          <dl className="mt-2 grid grid-cols-[auto_1fr] gap-x-3 gap-y-0.5 text-xs">
            <dt className="font-medium">Request</dt>
            <dd className="font-mono break-anywhere">
              {api.method} /api/lab{api.path}
            </dd>
            <dt className="font-medium">Status</dt>
            <dd>{api.status === 0 ? "no response" : api.status}</dd>
            {api.problem?.code ? (
              <>
                <dt className="font-medium">Code</dt>
                <dd className="font-mono">{String(api.problem.code)}</dd>
              </>
            ) : null}
            {api.problem?.suggestedAction ? (
              <>
                <dt className="font-medium">Suggested</dt>
                <dd className="break-anywhere">{String(api.problem.suggestedAction)}</dd>
              </>
            ) : null}
            {api.problem?.traceId ? (
              <>
                <dt className="font-medium">Trace</dt>
                <dd className="font-mono break-anywhere">{String(api.problem.traceId)}</dd>
              </>
            ) : null}
          </dl>
        ) : null}
        {api?.bodyText && !compact ? (
          <Collapsible className="mt-2">
            <CollapsibleTrigger className="text-xs underline underline-offset-2">Raw response</CollapsibleTrigger>
            <CollapsibleContent>
              <pre className="mt-1 max-h-48 overflow-auto whitespace-pre-wrap rounded bg-background/60 p-2 text-xs break-anywhere">{api.bodyText}</pre>
            </CollapsibleContent>
          </Collapsible>
        ) : null}
        {onRetry ? (
          <Button variant="outline" size="sm" className="mt-3" onClick={onRetry}>
            <RefreshCw /> Retry
          </Button>
        ) : null}
      </AlertDescription>
    </Alert>
  );
}
