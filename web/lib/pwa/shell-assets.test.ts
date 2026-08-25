import { describe, it, expect } from "vitest";
import { readFileSync } from "node:fs";
import path from "node:path";
import { contrastRatio, meetsAA } from "@/lib/contrast";
import { palette } from "@/lib/design-tokens";

/**
 * The offline shell and the app icons — the static half of the PWA.
 *
 * These are hand-authored files in `public/`, outside the component tree and outside
 * Tailwind, because the offline screen has to render with **no network and no
 * JavaScript**: it is the page Michelle sees in a car with no signal, and a screen that
 * depends on a hashed Next chunk being in the cache is a screen that renders unstyled the
 * first time a deploy changes the hash. Being outside the design system is the cost, so
 * these tests are the thing that stops it drifting away from the design system.
 */

const WEB_ROOT = path.resolve(__dirname, "..", "..");
const PUBLIC_DIR = path.join(WEB_ROOT, "public");

const read = (relative: string) => readFileSync(path.join(PUBLIC_DIR, relative), "utf8");

/** Every colour in the palette, uppercased, as the set a static asset may draw from. */
const PALETTE_HEXES = new Set(Object.values(palette).map((hex) => hex.toUpperCase()));

function hexesIn(source: string): string[] {
  return [...source.matchAll(/#[0-9a-fA-F]{6}\b/g)].map((match) => match[0].toUpperCase());
}

describe("the offline shell", () => {
  const html = read("offline.html");

  /**
   * It runs no JavaScript at all.
   *
   * Two reasons, and both are load-bearing. A static file cannot carry a CSP nonce, so an
   * inline script would either be blocked or would need `script-src 'unsafe-inline'` kept
   * alive for a file that has no need of it — and D042's deviation is scoped to the
   * public marketing pages, not extended. And a screen whose job is to be reliable when
   * everything else has failed should have nothing left that can fail.
   *
   * Control: the absence of a script in `public/offline.html`.
   * `<script>console.log("hi")</script>` added before `</body>` → red, "AssertionError: the
   * offline shell must run no JavaScript: it cannot carry a CSP nonce, and it is the screen
   * that has to work when everything else has not: expected '<!doctype html>\n<!--\n  The
   * offline …' not to match /<script/i".
   */
  it("runs no JavaScript", () => {
    expect(
      html,
      "the offline shell must run no JavaScript: it cannot carry a CSP nonce, and it is " +
        "the screen that has to work when everything else has not",
    ).not.toMatch(/<script/i);
  });

  /**
   * Every asset it loads is same-origin and root-relative.
   *
   * A cross-origin reference is unreachable by definition on the screen that exists
   * because the network is unreachable, and `default-src 'self'` would block it anyway.
   *
   * Control: the `href="/offline.css"` in `public/offline.html`.
   * Changed to `href="https://cdn.example.com/offline.css"` → red, "AssertionError: the
   * offline shell loads https://cdn.example.com/offline.css from another origin —
   * unreachable on the screen that exists because the network is: expected false to be
   * true".
   */
  it("loads nothing from another origin", () => {
    const references = [...html.matchAll(/(?:href|src)="([^"]+)"/g)].map((m) => m[1] ?? "");

    expect(references.length).toBeGreaterThan(0);

    for (const reference of references) {
      expect(
        reference.startsWith("/") || reference.startsWith("#"),
        `the offline shell loads ${reference} from another origin — unreachable on the ` +
          `screen that exists because the network is`,
      ).toBe(true);
    }
  });

  /**
   * It says what does not work, rather than showing an empty version of what does.
   *
   * The brief for this screen is honesty: an offline app that renders a hollow dashboard
   * teaches a clinician that the dashboard is empty. This one names the three things that
   * need a connection and explains that they are absent from the device on purpose.
   *
   * SCOPED TO THE LIST, not to the page. The first version of this test matched the three
   * subjects anywhere in the HTML and STAYED GREEN with the whole `<ul>` deleted — the
   * explanatory paragraph above it happens to use the same three words, so the prose was
   * covering for the enumeration. Matching the list itself also makes the claim stronger:
   * the screen names them one by one rather than mentioning them in passing.
   *
   * Control: the `<ul class="unavailable">` in `public/offline.html`.
   * Deleted → red, "AssertionError: the offline shell must ENUMERATE what needs a
   * connection, not mention it in passing: expected undefined to be truthy".
   */
  it("names what it cannot do", () => {
    const list = /<ul class="unavailable">([\s\S]*?)<\/ul>/.exec(html)?.[1];

    expect(
      list,
      "the offline shell must ENUMERATE what needs a connection, not mention it in passing",
    ).toBeTruthy();

    const items = [...(list ?? "").matchAll(/<li>([\s\S]*?)<\/li>/g)].map((m) => m[1] ?? "");
    expect(items.length).toBeGreaterThanOrEqual(3);

    for (const subject of ["patient record", "schedule", "note"]) {
      expect(
        items.join(" · "),
        `the offline shell must name ${subject} as unavailable rather than leaving the ` +
          `user to discover it`,
      ).toMatch(new RegExp(subject, "i"));
    }

    // And a way back, which on a static page is an ordinary link rather than a button
    // that needs script to do anything.
    expect(html).toMatch(/<a[^>]+href="\/"/);
  });

  /**
   * It claims nothing about data being available offline.
   *
   * The temptation on this screen is reassurance — "your notes are saved locally", "your
   * recordings are safe". None of that is true at this point in the build, and a false
   * reassurance on a clinical tool is worse than an unhelpful screen. When WORK_QUEUE 2.4
   * lands recording, the copy changes and this test changes with it, deliberately.
   *
   * Control: the copy in `public/offline.html`.
   * "Your notes are saved on this device and will upload later." added → red,
   * "AssertionError: the offline shell claims saved data that does not exist yet: expected
   * '<!doctype html>\n<!--\n  The offline …' not to match
   * /notes are saved (on|to) this device/i".
   */
  it("promises nothing that is not true yet", () => {
    for (const overclaim of [
      /notes are saved (on|to) this device/i,
      /recordings? (are|is) safe/i,
      /available offline/i,
    ]) {
      expect(html, "the offline shell claims saved data that does not exist yet").not.toMatch(
        overclaim,
      );
    }
  });

  /**
   * It is a real document, and a legible one on a phone.
   *
   * Control: the `<meta name="viewport">` in `public/offline.html`.
   * Deleted → red, "AssertionError: expected '<!doctype html>\n<!--\n  The offline …' to
   * match /name="viewport"/" — and iOS renders the page at 980px wide and scales it down,
   * which is 6pt text held at arm's length in a car.
   */
  it("is a complete, responsive document", () => {
    expect(html).toMatch(/^<!doctype html>/i);
    expect(html).toMatch(/<html[^>]+lang="en"/);
    expect(html).toMatch(/name="viewport"/);
    expect(html).toMatch(/<title>/);
    expect(html).toMatch(/<h1[\s>]/);
  });
});

describe("the offline stylesheet", () => {
  const css = read("offline.css");

  /**
   * Every colour in it is a palette colour.
   *
   * This file cannot import `lib/design-tokens.ts` — it is served raw to a browser with no
   * build step in front of it — so the tokens are copied in as literals, and copies drift.
   * Checking membership of the palette rather than a list of expected values means a
   * colour added to the palette is allowed and a colour invented here is not.
   *
   * Control: a hex literal in `public/offline.css`.
   * `--ink: #3A4553` changed to `#3A4554` → red, "AssertionError: #3A4554 in offline.css is
   * not a palette colour. This file is outside the design system by necessity; the palette
   * is still the only source of colour. Palette: #2D7FF9, #1D6FE0, #1B5FCC, #1B4FA3,
   * #E8F3FF, #F5FAFF, #FFFFFF, #4E9E9A, #FFD786, #FF8FA3, #FFBD59, #3A4553, #55606E.:
   * expected false to be true".
   */
  it("draws every colour from the palette", () => {
    const used = hexesIn(css);

    expect(used.length, "no colours found in offline.css — has the scan stopped working?")
      .toBeGreaterThan(2);

    for (const hex of used) {
      expect(
        PALETTE_HEXES.has(hex),
        `${hex} in offline.css is not a palette colour. This file is outside the design ` +
          `system by necessity; the palette is still the only source of colour. ` +
          `Palette: ${[...PALETTE_HEXES].join(", ")}.`,
      ).toBe(true);
    }
  });

  /**
   * Body copy on the offline shell clears WCAG AA, like body copy everywhere else.
   *
   * The colours are read out of the file's custom properties rather than assumed, so this
   * measures what ships. Same standard as `design-tokens.test.ts`; the comps' light gray
   * fails at 2.5:1 and is exactly the thing that would get pasted into a hand-written
   * stylesheet nobody was watching.
   *
   * Control: `--ink` in `public/offline.css`.
   * Set to `#55606E` (the palette's own `inkMuted`) → STILL GREEN, because that colour
   * passes AA on mist and is asserted to in `design-tokens.test.ts`; the mutation was too
   * weak to prove anything. Set to `#2D7FF9` — a palette colour, so the membership test
   * above stays green and this one is isolated — → red, "AssertionError: 3.63:1: expected
   * false to be true". Brand blue as body copy is the exact mistake D033 exists for.
   */
  it("passes AA for body copy", () => {
    const value = (name: string): string => {
      const match = new RegExp(`--${name}:\\s*(#[0-9a-fA-F]{6})`).exec(css);
      if (!match?.[1]) throw new Error(`offline.css declares no --${name}`);
      return match[1];
    };

    const ratio = contrastRatio(value("ink"), value("surface"));
    expect(meetsAA(ratio, { large: false }), `${ratio.toFixed(2)}:1`).toBe(true);
  });
});

describe("the app icons", () => {
  /**
   * The icon is the same shell as the header mark, not a second drawing of one.
   *
   * `ShellMark.tsx` holds the geometry and a paragraph about why the ribs are weighted the
   * way they are; the icon SVGs are separate files because a rasteriser needs a file. Two
   * drawings of the same logo diverge, and the divergence is invisible until somebody sees
   * the home screen and the header side by side.
   *
   * EVERY copy of the path is checked, not just the first one found. Each icon carries the
   * body twice — once as the visible fill and once inside the `<clipPath>` that keeps the
   * ribs off the tile — and a `toContain` on the file passed happily with the visible path
   * mutated, because the clip copy still matched. Two copies covering for each other, found
   * by running the deletion (docs/TEST_STRATEGY.md).
   *
   * Control: any occurrence of the body path in `public/icons/icon.svg`.
   * One coordinate of the visible path changed (`-23-27` → `-23-26`) → red,
   * "AssertionError: icons/icon.svg draws a shell that is not the one in
   * components/brand/ShellMark.tsx. A second drawing of the logo drifts from the first,
   * and nobody sees it until the home screen and the header are side by side.: expected
   * 'M31 55c-11-2-21-12-23-26l7 3 6-6 6 5…' to be 'M31 55c-11-2-21-12-23-27l7 3 6-6 6 5…'".
   */
  it("draw the same shell as the header mark", () => {
    const component = readFileSync(
      path.join(WEB_ROOT, "components", "brand", "ShellMark.tsx"),
      "utf8",
    );

    const body = /d="(M31 55[^"]+)"/.exec(component)?.[1];
    expect(body, "the shell body path was not found in ShellMark.tsx").toBeTruthy();

    for (const icon of ["icons/icon.svg", "icons/icon-maskable.svg"]) {
      const drawn = [...read(icon).matchAll(/d="(M31 55[^"]*)"/g)].map((m) => m[1]);

      expect(drawn.length, `${icon} does not draw the shell at all`).toBeGreaterThan(0);

      for (const drawnPath of drawn) {
        expect(
          drawnPath,
          `${icon} draws a shell that is not the one in components/brand/ShellMark.tsx. ` +
            `A second drawing of the logo drifts from the first, and nobody sees it until ` +
            `the home screen and the header are side by side.`,
        ).toBe(body);
      }
    }
  });

  /**
   * Control: a hex literal in `public/icons/icon.svg`.
   * The tile changed from `#E8F3FF` to `#E8F3FE` → red, "AssertionError: #E8F3FE in
   * icons/icon.svg is not a palette colour: expected false to be true".
   */
  it("use palette colours only", () => {
    for (const icon of ["icons/icon.svg", "icons/icon-maskable.svg"]) {
      const used = hexesIn(read(icon));
      expect(used.length, `no colours found in ${icon}`).toBeGreaterThan(0);

      for (const hex of used) {
        expect(PALETTE_HEXES.has(hex), `${hex} in ${icon} is not a palette colour`).toBe(true);
      }
    }
  });

  /**
   * The maskable icon keeps the mark inside Android's safe zone.
   *
   * A maskable icon is cropped to whatever shape the launcher wants — circle, squircle,
   * teardrop — and only the middle 80% is guaranteed to survive. The shell is drawn inside
   * a group whose scale and offset put it well within that circle; a full-bleed mark would
   * have its scallops sliced off on a round launcher.
   *
   * Control: the `<g transform>` on the shell in `public/icons/icon-maskable.svg`.
   * Changed to `translate(0 0) scale(8)` → red, "AssertionError: the maskable icon draws
   * the shell across 512px of a 512px canvas; Android may crop everything outside the
   * middle 409.6px: expected 512 to be less than or equal to 409.6".
   */
  it("keep the maskable mark inside the safe zone", () => {
    const svg = read("icons/icon-maskable.svg");

    const viewBox = /viewBox="0 0 (\d+) \1"/.exec(svg)?.[1];
    expect(viewBox, "icon-maskable.svg must be a square viewBox").toBeTruthy();

    const canvas = Number(viewBox);
    const transform = /transform="translate\(([\d.]+) ([\d.]+)\) scale\(([\d.]+)\)"/.exec(svg);
    expect(transform, "the shell must be placed with an explicit translate+scale").toBeTruthy();

    // The mark is drawn on ShellMark's 64-unit grid before the transform.
    const drawn = 64 * Number(transform?.[3]);
    const safe = canvas * 0.8;

    expect(
      drawn,
      `the maskable icon draws the shell across ${drawn}px of a ${canvas}px canvas; ` +
        `Android may crop everything outside the middle ${safe}px`,
    ).toBeLessThanOrEqual(safe);

    // And centred, or the safe zone is the wrong 80%.
    expect(Number(transform?.[1])).toBeCloseTo((canvas - drawn) / 2, 1);
    expect(Number(transform?.[2])).toBeCloseTo((canvas - drawn) / 2, 1);
  });
});
