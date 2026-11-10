import Link from "next/link";
import { EmptyState } from "@/components/empty-state";

export default function NotFound() {
  return (
    <EmptyState
      title="Page not found"
      description="This page does not exist in the Test Lab."
      action={
        <Link className="text-sm text-primary underline" href="/">
          Back to the dashboard
        </Link>
      }
    />
  );
}
