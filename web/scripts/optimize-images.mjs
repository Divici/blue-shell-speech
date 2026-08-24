/**
 * Converts source photography into responsive AVIF/WebP derivatives.
 *
 * The originals are multi-megabyte PNGs: children.png is 2.1 MB and headshot.PNG is
 * 2.4 MB. Shipping either as-is would blow the LCP budget on its own — and the
 * containers scale to zero, so a first visitor is already paying a cold start before a
 * single byte of image arrives.
 *
 * The originals stay OUT of git (see .gitignore). Only the derivatives are committed:
 * a multi-megabyte binary in a public repository's history is permanent.
 *
 *   node scripts/optimize-images.mjs
 */
import sharp from "sharp";
import { mkdir, stat } from "node:fs/promises";
import path from "node:path";

const SRC = path.resolve("../assets");
const OUT = path.resolve("public/img");

/**
 * Widths chosen to match how each image is actually laid out, not a generic ladder.
 * The hero photo spans up to ~720px in a two-column layout; the headshot is a portrait
 * capped around 480px. 2x covers retina.
 */
const JOBS = [
  { file: "children.png", name: "children", widths: [360, 540, 720, 1080, 1440] },
  { file: "headshot.PNG", name: "headshot", widths: [240, 320, 480, 640, 960] },
];

const bytes = (n) => `${(n / 1024).toFixed(0)} KB`;

async function main() {
  await mkdir(OUT, { recursive: true });

  for (const job of JOBS) {
    const src = path.join(SRC, job.file);

    let original;
    try {
      original = await stat(src);
    } catch {
      console.warn(`skip ${job.file} — not found at ${src}`);
      continue;
    }

    const meta = await sharp(src).metadata();
    console.log(
      `\n${job.file}  ${meta.width}x${meta.height}  ${bytes(original.size)}`,
    );

    for (const width of job.widths) {
      if (meta.width && width > meta.width) continue;

      for (const format of ["avif", "webp"]) {
        const out = path.join(OUT, `${job.name}-${width}.${format}`);
        const pipeline = sharp(src).resize({ width, withoutEnlargement: true });

        // AVIF at quality 50 is visually indistinguishable from the PNG here and
        // roughly half the size of equivalent WebP. WebP is the fallback for older
        // Safari; both are widely supported, so no JPEG tier is generated.
        await (format === "avif"
          ? pipeline.avif({ quality: 50, effort: 6 })
          : pipeline.webp({ quality: 72 })
        ).toFile(out);

        const { size } = await stat(out);
        console.log(`   ${path.basename(out).padEnd(24)} ${bytes(size)}`);
      }
    }
  }
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
