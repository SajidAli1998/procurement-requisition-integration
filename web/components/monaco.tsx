"use client";

import dynamic from "next/dynamic";
import { useTheme } from "next-themes";
import { useEffect, useState } from "react";
import { prettyJson } from "@/lib/utils";
import { Skeleton } from "@/components/ui/skeleton";

// Served from /monaco/vs (see scripts/copy-monaco.mjs) so the editor never loads from a CDN.
let configured = false;
async function configureMonaco() {
  if (configured) return;
  const { loader } = await import("@monaco-editor/react");
  loader.config({ paths: { vs: "/monaco/vs" } });
  configured = true;
}

const Editor = dynamic(
  async () => {
    await configureMonaco();
    return (await import("@monaco-editor/react")).default;
  },
  { ssr: false, loading: () => <Skeleton className="h-full min-h-24 w-full" /> },
);

const DiffEditor = dynamic(
  async () => {
    await configureMonaco();
    return (await import("@monaco-editor/react")).DiffEditor;
  },
  { ssr: false, loading: () => <Skeleton className="h-full min-h-24 w-full" /> },
);

function useMonacoTheme() {
  const { resolvedTheme } = useTheme();
  return resolvedTheme === "dark" ? "vs-dark" : "light";
}

function lineHeight(text: string, min: number, max: number) {
  const lines = text.split("\n").length;
  return Math.max(min, Math.min(max, lines * 19 + 16));
}

export function JsonViewer({ value, height, maxHeight = 420, minHeight = 60, label, language = "json" }: { value: unknown; height?: number; maxHeight?: number; minHeight?: number; label?: string; language?: string }) {
  const theme = useMonacoTheme();
  const text = typeof value === "string" ? prettyJson(value) : prettyJson(value ?? null);
  const h = height ?? lineHeight(text, minHeight, maxHeight);
  return (
    <div className="overflow-hidden rounded-md border" style={{ height: h }} aria-label={label ?? "JSON viewer"} data-testid="json-viewer">
      <Editor
        height={h}
        language={language}
        value={text}
        theme={theme}
        options={{ readOnly: true, domReadOnly: true, minimap: { enabled: false }, scrollBeyondLastLine: false, fontSize: 12, wordWrap: "on", lineNumbers: "off", folding: true, automaticLayout: true, renderLineHighlight: "none" }}
      />
    </div>
  );
}

export function JsonEditor({ value, onChange, height = 420, label, onMountReady }: { value: string; onChange: (text: string) => void; height?: number; label?: string; onMountReady?: () => void }) {
  const theme = useMonacoTheme();
  return (
    <div className="overflow-hidden rounded-md border" style={{ height }} aria-label={label ?? "JSON editor"} data-testid="json-editor">
      <Editor
        height={height}
        language="json"
        value={value}
        theme={theme}
        onChange={(v) => onChange(v ?? "")}
        onMount={() => onMountReady?.()}
        options={{ minimap: { enabled: false }, scrollBeyondLastLine: false, fontSize: 12, wordWrap: "on", automaticLayout: true, tabSize: 2 }}
      />
    </div>
  );
}

export function JsonDiff({ original, modified, height = 420, labels }: { original: unknown; modified: unknown; height?: number; labels?: [string, string] }) {
  const theme = useMonacoTheme();
  const [narrow, setNarrow] = useState(false);
  useEffect(() => {
    const mq = window.matchMedia("(max-width: 767px)");
    const update = () => setNarrow(mq.matches);
    update();
    mq.addEventListener("change", update);
    return () => mq.removeEventListener("change", update);
  }, []);
  return (
    <div>
      {labels ? (
        <div className="mb-1 grid grid-cols-2 gap-2 text-xs font-medium text-muted-foreground">
          <span>{labels[0]}</span>
          <span>{labels[1]}</span>
        </div>
      ) : null}
      <div className="overflow-hidden rounded-md border" style={{ height }} data-testid="json-diff">
        <DiffEditor
          height={height}
          language="json"
          original={prettyJson(original ?? null)}
          modified={prettyJson(modified ?? null)}
          theme={theme}
          options={{ readOnly: true, renderSideBySide: !narrow, minimap: { enabled: false }, fontSize: 12, wordWrap: "on", automaticLayout: true, scrollBeyondLastLine: false }}
        />
      </div>
    </div>
  );
}
