import { describe, it, expect } from "vitest";
import { readFileSync, statSync } from "node:fs";
import path from "node:path";
import manifest from "./manifest";
import { palette } from "@/lib/design-tokens";

/**
 * The web app manifest.
 *
 * This is what makes the dictation app installable, and installability is not cosmetic
 * here: **only a home-screen-installed PWA escapes iOS Safari's 7-day storage eviction**
 * (docs/ARCHITECTURE.md, "iOS realities"). An encrypted dictation draft sitting in a
 * browser tab is deleted after a week of not opening the site; the same draft in an
 * installed app is not. So a manifest that fails the installability criteria silently
 * downgrades the durability of clinical audio, and nothing in the UI would say so.
 *
 * WORK_QUEUE 2.2 owns the prompt and the standalone detection. This file owns the part
 * that has to be true before either of those means anything.
 */

const PUBLIC_DIR = path.resolve(__dirname, "..", "public");

/**
 * A PNG's real dimensions, read out of its IHDR chunk.
 *
 * Eight bytes of signature, then a four-byte length, the literal `IHDR`, and width and
 * height as big-endian 32-bit integers. Parsed by hand rather than with `sharp` so the
 * assertion depends on the file rather than on an image library agreeing with the file.
 */
function pngSize(file: string): { width: number; height: number } {
  const bytes = readFileSync(file);

  const signature = bytes.subarray(0, 8).toString("hex");
  if (signature !== "89504e470d0a1a0a") throw new Error(`${file} is not a PNG (${signature})`);
  if (bytes.subarray(12, 16).toString("ascii") !== "IHDR") throw new Error(`${file}: no IHDR`);

  return { width: bytes.readUInt32BE(16), height: bytes.readUInt32BE(20) };
}

describe("the manifest", () => {
  const declared = manifest();

  /**
   * The four fields a browser checks before it will offer to install anything.
   *
   * Control: `display: "standalone"` in `app/manifest.ts`.
   * Changed to `"browser"` → red, "an installed app must open in its own window, not a
   * browser tab: expected 'browser' to be 'standalone'" — and in a browser tab the
   * 7-day eviction still applies, which is the whole point of installing.
   */
  it("declares an installable application", () => {
    expect(declared.name).toBe("Blue Shell Speech");
    expect(declared.short_name).toBeTruthy();
    // Home-screen labels truncate hard. Twelve characters is what iOS shows.
    expect((declared.short_name ?? "").length).toBeLessThanOrEqual(12);

    expect(declared.scope).toBe("/");
    expect(
      declared.display,
      "an installed app must open in its own window, not a browser tab",
    ).toBe("standalone");
  });

  /**
   * The installed app opens on the day's visits, which is the only screen Michelle wants
   * between two houses. Signed out it redirects to `/login`, which is correct and is the
   * reason `start_url` is not `/login` itself: a session that is still valid should not
   * cost her two taps.
   *
   * Control: `start_url` in `app/manifest.ts`.
   * Changed to `"/"` → red, "the installed app must open on the day's visits: expected
   * '/' to be '/today'" — and the home-screen icon would land her on the marketing site.
   */
  it("opens on the daily visit view", () => {
    expect(declared.start_url, "the installed app must open on the day's visits").toBe("/today");
  });

  /**
   * Colours come from the palette, not from a hex literal typed twice.
   *
   * `theme_color` is the status bar of the installed app on Android and the title bar on
   * desktop; `background_color` is what fills the screen before the first paint. Both are
   * brand surface, and both would drift silently from `lib/design-tokens.ts`.
   *
   * Control: `theme_color: palette.blue` in `app/manifest.ts`.
   * Replaced with the literal `"#2D7FF9"` → still green, because the literal IS the
   * palette value today; replaced with `palette.navy` → red, "expected '#1B4FA3' to be
   * '#2D7FF9'". Recorded honestly: this test pins the VALUE, and the shared constant is
   * what keeps the value in one place.
   */
  it("takes its colours from the palette", () => {
    expect(declared.theme_color).toBe(palette.blue);
    expect(declared.background_color).toBe(palette.mist);
  });

  /**
   * Every icon the manifest declares exists, is the size it says, and is the type it says.
   *
   * Derived from the manifest rather than from a list of filenames: an icon added next
   * week is checked by existing, which is the property docs/TEST_STRATEGY.md asks for and
   * the one a hard-coded list never has. A manifest pointing at a missing file is the
   * classic PWA failure — the browser silently declines to install and reports nothing.
   *
   * Control: the `icons` array in `app/manifest.ts`.
   * `"/icons/icon-192.png"` changed to `"/icons/icon-190.png"` → red, "the manifest
   * declares /icons/icon-190.png, which is not a file in web/public: expected [Function]
   * to not throw an error but 'Error: ENOENT: no such file or directory...' was thrown".
   */
  it("declares icons that exist at the sizes it claims", () => {
    const icons = declared.icons ?? [];

    // A floor, not a count: an exact number is the list again. Two is the platform
    // minimum — 192 for the home screen, 512 for the splash screen.
    expect(icons.length, `Icons declared: ${icons.map((i) => i.src).join(", ")}`).toBeGreaterThan(2);

    for (const icon of icons) {
      const file = path.join(PUBLIC_DIR, icon.src);

      expect(
        () => statSync(file),
        `the manifest declares ${icon.src}, which is not a file in web/public`,
      ).not.toThrow();

      if (icon.type === "image/png") {
        const [width, height] = (icon.sizes ?? "").split("x").map(Number);
        const actual = pngSize(file);

        expect(actual, `${icon.src} is declared ${icon.sizes} and is not`).toEqual({
          width,
          height,
        });
      }
    }
  });

  /**
   * A maskable icon, and PNGs at both platform sizes.
   *
   * Without `purpose: "maskable"` Android shrinks the icon into a white circle with a
   * border — the "sticker" look that says an app was ported rather than built. Asserted as
   * properties of the set rather than by naming files.
   *
   * Control: the `purpose: "maskable"` on the adaptive icon in `app/manifest.ts`.
   * Changed to `"any"` → red, "AssertionError: no icon is declared maskable, so Android
   * will letterbox the mark inside a white circle: expected undefined to be truthy".
   */
  it("covers both platform sizes and a maskable purpose", () => {
    const icons = declared.icons ?? [];
    const sizes = new Set(icons.map((icon) => icon.sizes));

    expect(sizes, `Sizes declared: ${[...sizes].join(", ")}`).toContain("192x192");
    expect(sizes, `Sizes declared: ${[...sizes].join(", ")}`).toContain("512x512");

    expect(
      icons.find((icon) => icon.purpose === "maskable"),
      "no icon is declared maskable, so Android will letterbox the mark inside a white circle",
    ).toBeTruthy();
  });

  /**
   * Nothing in the manifest identifies a patient, a provider or a place.
   *
   * The manifest is fetched unauthenticated by anything that can reach the origin, and it
   * is the one file in the application whose contents are chosen for a browser rather than
   * for a reader — which is exactly how a description ends up containing a practice
   * address. CLAUDE.md non-negotiable #7: Michelle's home address never enters the tree.
   *
   * Control: the `description` in `app/manifest.ts`.
   * "Visit us at 1400 Chesapeake Avenue, Maryland." appended → red, "AssertionError: the
   * manifest is public and unauthenticated; it must carry no address, phone number or
   * personal name: expected '{\"id\":\"/\",\"name\":\"Blue Shell Speech\",…' not to match
   * /\d{1,5}\s+\w+\s+(street|st|road|rd|a…/i".
   */
  it("carries nothing personal", () => {
    const serialised = JSON.stringify(declared);

    for (const pattern of [
      /\d{1,5}\s+\w+\s+(street|st|road|rd|avenue|ave|lane|ln|drive|dr|court|ct)\b/i,
      /\b\d{3}[-.\s]\d{3}[-.\s]\d{4}\b/,
      /\bmichelle\b/i,
    ]) {
      expect(
        serialised,
        "the manifest is public and unauthenticated; it must carry no address, phone " +
          "number or personal name",
      ).not.toMatch(pattern);
    }
  });
});
