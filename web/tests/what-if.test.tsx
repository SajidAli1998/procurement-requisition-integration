import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { storyIsSafe } from "@/components/what-if/what-if-page";
import { NAV } from "@/components/nav";
import type { WhatIfStory } from "@/lib/types";

const story = (checks: WhatIfStory["checks"]): WhatIfStory => ({
  id: "network-down", title: "The internet goes down mid-sync", verdict: "Safe so far", headline: "h", story: ["s"], numbers: [], checks,
  timeline: [], next: null, links: [], at: new Date().toISOString(),
});

describe("what-if", () => {
  it("is safe only when every backend check passed", () => {
    expect(storyIsSafe(story([{ label: "No duplicates", ok: true, detail: "" }, { label: "Nothing lost", ok: true, detail: "" }]))).toBe(true);
    expect(storyIsSafe(story([{ label: "No duplicates", ok: true, detail: "" }, { label: "Nothing lost", ok: false, detail: "" }]))).toBe(false);
    expect(storyIsSafe(story([]))).toBe(false);
  });

  it("is in the navigation", () => {
    expect(NAV.some((n) => n.href === "/what-if")).toBe(true);
  });

  it("renders the situations from the API", async () => {
    const { WhatIfPage } = await import("@/components/what-if/what-if-page");
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    qc.setQueryData(["whatif"], [{ id: "network-down", title: "The internet goes down mid-sync", description: "d", group: "Connection" }]);
    render(
      <QueryClientProvider client={qc}>
        <WhatIfPage />
      </QueryClientProvider>,
    );
    expect(await screen.findByRole("button", { name: "Try: The internet goes down mid-sync" })).toBeEnabled();
  });
});
