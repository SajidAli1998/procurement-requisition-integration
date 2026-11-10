import type { Metadata } from "next";

export async function generateMetadata({ params }: { params: Promise<{ id: string }> }): Promise<Metadata> {
  const { id } = await params;
  return { title: decodeURIComponent(id) };
}

export default function Layout({ children }: { children: React.ReactNode }) {
  return children;
}
