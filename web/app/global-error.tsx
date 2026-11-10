"use client";

import "./globals.css";

export default function GlobalError({ retry }: { error: unknown; retry: () => void }) {
  return (
    <html lang="en">
      <body className="flex min-h-screen items-center justify-center bg-background p-6 text-foreground">
        <title>Something went wrong · Procurement Test Lab</title>
        <div className="max-w-md space-y-3 text-center">
          <h1 className="text-lg font-semibold">The Test Lab hit an unexpected error</h1>
          <p className="text-sm text-muted-foreground">Reload the page, or try again below.</p>
          <button className="rounded-md bg-primary px-4 py-2 text-sm text-primary-foreground" onClick={retry} type="button">
            Try again
          </button>
        </div>
      </body>
    </html>
  );
}
