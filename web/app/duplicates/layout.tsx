import type { Metadata } from "next";

export const metadata: Metadata = { title: "Duplicate detection" };

export default function Layout({ children }: { children: React.ReactNode }) {
  return children;
}
