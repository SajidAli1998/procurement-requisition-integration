import next from "eslint-config-next/core-web-vitals";
import nextTs from "eslint-config-next/typescript";

const config = [
  ...next,
  ...nextTs,
  { ignores: [".next/**", "node_modules/**", "public/monaco/**", "playwright-report/**", "test-results/**", "next-env.d.ts"] },
];

export default config;
