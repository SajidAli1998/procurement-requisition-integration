"use client";

import { useEffect } from "react";
import { TriangleAlert } from "lucide-react";
import { EmptyState } from "@/components/empty-state";
import { Button } from "@/components/ui/button";

export default function ErrorPage({ error, retry }: { error: unknown; retry: () => void }) {
  useEffect(() => {
    console.error(error);
  }, [error]);

  const message = error instanceof Error ? error.message : "An unexpected error occurred.";

  return (
    <EmptyState
      icon={TriangleAlert}
      title="This page could not be shown"
      description={
        <>
          {message} If the Test Lab API was stopped, start it again with <code>scripts/testlab.sh</code>.
        </>
      }
      action={<Button onClick={retry}>Try again</Button>}
    />
  );
}
