"use client";

import { useState } from "react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { ThemeProvider } from "next-themes";
import { LiveProvider } from "@/components/live-provider";
import { isApiError } from "@/lib/api";

export function Providers({ children }: { children: React.ReactNode }) {
  const [client] = useState(
    () =>
      new QueryClient({
        defaultOptions: {
          queries: {
            staleTime: 2_000,
            refetchOnWindowFocus: true,
            // Do not hammer a lab that is down or rejecting the key; the error panel offers a manual retry.
            retry: (count, error) => !(isApiError(error) && (error.kind === "unauthorized" || error.kind === "schema" || error.kind === "not-found")) && count < 1,
            retryDelay: 800,
          },
          mutations: { retry: false },
        },
      }),
  );
  return (
    <ThemeProvider attribute="class" defaultTheme="system" enableSystem disableTransitionOnChange>
      <QueryClientProvider client={client}>
        <LiveProvider>{children}</LiveProvider>
      </QueryClientProvider>
    </ThemeProvider>
  );
}
