/**
 * Prints the Lighthouse scores slice 1 is measured against.
 *
 *   node scripts/report-lighthouse.mjs <report.json>
 *
 * The acceptance criteria in docs/IMPLEMENTATION_PLAN.md are specific numbers, so they
 * need a specific reading rather than "it felt fast".
 */
import { readFile } from "node:fs/promises";

const TARGETS = {
  performance: 90,
  accessibility: 95,
  "best-practices": 95,
  seo: 95,
};

const path = process.argv[2];
if (!path) {
  console.error("usage: node scripts/report-lighthouse.mjs <report.json>");
  process.exit(1);
}

const report = JSON.parse(await readFile(path, "utf8"));

let failed = false;
console.log("category          score  target");
for (const [key, target] of Object.entries(TARGETS)) {
  const score = Math.round((report.categories[key]?.score ?? 0) * 100);
  const ok = score >= target;
  if (!ok) failed = true;
  console.log(
    `${key.padEnd(17)} ${String(score).padStart(3)}    ${String(target).padStart(3)}  ${ok ? "PASS" : "FAIL"}`,
  );
}

console.log("\nmetric                        value");
for (const id of [
  "first-contentful-paint",
  "largest-contentful-paint",
  "cumulative-layout-shift",
  "total-blocking-time",
  "speed-index",
]) {
  const audit = report.audits[id];
  if (audit) console.log(`${id.padEnd(29)} ${audit.displayValue ?? "-"}`);
}

const failures = Object.values(report.audits).filter(
  (a) => a.score !== null && a.score < 1 && a.scoreDisplayMode === "binary",
);
if (failures.length > 0) {
  console.log("\nfailing audits:");
  for (const a of failures) console.log(`  - ${a.id}: ${a.title}`);
}

process.exitCode = failed ? 1 : 0;
