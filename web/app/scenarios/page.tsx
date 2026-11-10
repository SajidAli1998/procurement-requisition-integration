import type { Metadata } from "next";
import { ScenarioRunner } from "@/components/scenarios/scenario-runner";

export const metadata: Metadata = { title: "Scenario runner" };

export default function ScenariosPage() {
  return <ScenarioRunner />;
}
