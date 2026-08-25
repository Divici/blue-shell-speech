/**
 * Rasterises the app icons from the two committed SVG sources.
 *
 * The SVGs are the source of truth — they hold the same shell geometry as
 * `components/brand/ShellMark.tsx`, and `lib/pwa/shell-assets.test.ts` holds them to it.
 * The PNGs exist because neither Android's launcher nor iOS's home screen will take an SVG.
 *
 * Both the sources and the derivatives are committed, unlike the photography in
 * `optimize-images.mjs`: these are a few kilobytes each, they are referenced by
 * `app/manifest.ts`, and a manifest pointing at a file the build forgot to produce is the
 * commonest reason a browser silently declines to offer an install.
 *
 *   node scripts/generate-icons.mjs
 */
import sharp from "sharp";
import { mkdir, stat } from "node:fs/promises";
import path from "node:path";

const OUT = path.resolve("public/icons");

/** `ice`, from lib/design-tokens.ts. The tile colour of both sources. */
const TILE = "#E8F3FF";

const JOBS = [
  /*
   * The `any` icon keeps its rounded corners and its transparency — it is drawn on a
   * browser tab and in Android's legacy path, both of which composite it over their own
   * background.
   */
  { source: "icon.svg", out: "icon-192.png", size: 192 },
  { source: "icon.svg", out: "icon-512.png", size: 512 },

  /* The adaptive icon. Full bleed; the launcher supplies the shape. */
  { source: "icon-maskable.svg", out: "icon-maskable-512.png", size: 512 },

  /*
   * iOS reads this one, and it must be OPAQUE: Safari does not composite an
   * `apple-touch-icon`, it rounds it, so transparent corners land on the home screen as
   * black ones. Flattened onto the tile colour, which is what the rounded rect is already
   * filled with — so the result is the full-bleed square iOS wants, at the larger mark size
   * rather than the maskable safe-zone one, because iOS crops only the corners.
   */
  { source: "icon.svg", out: "apple-touch-icon.png", size: 180, flatten: TILE },
];

const bytes = (n) => `${(n / 1024).toFixed(1)} KB`;

async function main() {
  await mkdir(OUT, { recursive: true });

  for (const job of JOBS) {
    const source = path.join(OUT, job.source);
    const destination = path.join(OUT, job.out);

    // `density` drives librsvg's rasterisation: rendering a 512-unit viewBox at the
    // default 72 DPI and then upscaling produces soft edges on the shell's ribs.
    let pipeline = sharp(source, { density: 384 }).resize(job.size, job.size, {
      fit: "contain",
      background: { r: 0, g: 0, b: 0, alpha: 0 },
    });

    if (job.flatten) pipeline = pipeline.flatten({ background: job.flatten });

    await pipeline.png({ compressionLevel: 9, palette: true }).toFile(destination);

    const written = await stat(destination);
    console.log(`${job.out.padEnd(26)} ${job.size}x${job.size}  ${bytes(written.size)}`);
  }
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
