"use client";

import type { UseQueryResult } from "@tanstack/react-query";
import { Skeleton } from "@/components/ui/skeleton";
import { ErrorPanel } from "@/components/error-panel";

export function QueryState<T>({ query, children, skeleton, title }: { query: UseQueryResult<T>; children: (data: T) => React.ReactNode; skeleton?: React.ReactNode; title?: string }) {
  if (query.isPending) {
    return (
      <div role="status" aria-busy="true" aria-label="Loading" data-testid="loading">
        {skeleton ?? (
          <div className="space-y-2">
            <Skeleton className="h-8 w-full" />
            <Skeleton className="h-8 w-full" />
            <Skeleton className="h-8 w-2/3" />
          </div>
        )}
        <span className="sr-only">Loading</span>
      </div>
    );
  }
  if (query.isError) return <ErrorPanel error={query.error} title={title} onRetry={() => void query.refetch()} />;
  return <>{children(query.data as T)}</>;
}
