import type { Metadata } from "next";
import { ControlCenter } from "@/components/control/control-center";

export const metadata: Metadata = { title: "Control center" };

export default function ControlPage() {
  return <ControlCenter />;
}
