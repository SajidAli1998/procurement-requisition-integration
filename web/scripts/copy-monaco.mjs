// The editor loads Monaco from this app rather than a CDN.
import { cpSync, existsSync, mkdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const from = join(root, "node_modules", "monaco-editor", "min", "vs");
const to = join(root, "public", "monaco", "vs");
if (!existsSync(from)) {
  console.warn("monaco-editor is not installed; skipping copy");
  process.exit(0);
}
mkdirSync(to, { recursive: true });
cpSync(from, to, { recursive: true });
console.log("Monaco copied to public/monaco/vs");
