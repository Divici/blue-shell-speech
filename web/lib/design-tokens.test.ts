import { describe, it, expect } from "vitest";
import { contrastRatio, meetsAA } from "./contrast";
import { palette, textOn } from "./design-tokens";

/**
 * Slice 1 acceptance: "All body copy >= 4.5:1; the comps' light gray is darkened,
 * deviation noted."
 *
 * This suite is that criterion, executable. Every foreground/background pair the design
 * actually uses is enumerated and checked. Adding a pairing without adding it here is
 * how an inaccessible combination reaches production looking fine on a good monitor.
 */
describe("palette", () => {
  it("every colour is a valid 6-digit hex", () => {
    for (const [name, value] of Object.entries(palette)) {
      expect(value, `${name} = ${value}`).toMatch(/^#[0-9A-F]{6}$/);
    }
  });
});

describe("text contrast on light surfaces", () => {
  const lightSurfaces = [
    ["white", palette.white],
    ["mist", palette.mist],
    ["ice", palette.ice],
  ] as const;

  for (const [surfaceName, surface] of lightSurfaces) {
    it(`body text passes AA on ${surfaceName}`, () => {
      const ratio = contrastRatio(palette.ink, surface);
      expect(meetsAA(ratio, { large: false }), `${ratio.toFixed(2)}:1`).toBe(true);
    });

    it(`muted text passes AA on ${surfaceName}`, () => {
      const ratio = contrastRatio(palette.inkMuted, surface);
      expect(meetsAA(ratio, { large: false }), `${ratio.toFixed(2)}:1`).toBe(true);
    });

    it(`heading text passes AA on ${surfaceName}`, () => {
      const ratio = contrastRatio(palette.navy, surface);
      expect(meetsAA(ratio, { large: false }), `${ratio.toFixed(2)}:1`).toBe(true);
    });
  }
});

describe("text contrast on brand surfaces", () => {
  it("white text passes AA on the primary button", () => {
    const ratio = contrastRatio(palette.white, palette.blueAction);
    expect(meetsAA(ratio, { large: false }), `${ratio.toFixed(2)}:1`).toBe(true);
  });

  /**
   * Regression marker. The comp's brand blue is 3.81:1 against white, so its primary
   * button is inaccessible as drawn. If someone "simplifies" by collapsing blueAction
   * back into blue, this test explains what breaks.
   */
  it("the comp's brand blue does NOT pass with white text — hence blueAction", () => {
    const ratio = contrastRatio(palette.white, palette.blue);
    expect(meetsAA(ratio, { large: false }), `${ratio.toFixed(2)}:1`).toBe(false);
  });

  it("white text passes AA on the footer", () => {
    const ratio = contrastRatio(palette.white, palette.navy);
    expect(meetsAA(ratio, { large: false }), `${ratio.toFixed(2)}:1`).toBe(true);
  });
});

/**
 * The eyebrow ("COMMUNICATION OPENS DOORS") is small and blue on a near-white
 * background — exactly the combination that quietly fails.
 */
describe("the eyebrow label", () => {
  it("passes AA as normal text, not just as large text", () => {
    const ratio = contrastRatio(palette.blueDeep, palette.mist);
    expect(meetsAA(ratio, { large: false }), `${ratio.toFixed(2)}:1`).toBe(true);
  });
});

describe("textOn", () => {
  it("picks a foreground that passes AA for any palette surface", () => {
    for (const [name, surface] of Object.entries(palette)) {
      const fg = textOn(surface);
      const ratio = contrastRatio(fg, surface);
      expect(
        meetsAA(ratio, { large: false }),
        `${name} (${surface}) -> ${fg} = ${ratio.toFixed(2)}:1`,
      ).toBe(true);
    }
  });
});
