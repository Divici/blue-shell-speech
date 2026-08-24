import { describe, it, expect } from "vitest";
import { contrastRatio, relativeLuminance, meetsAA } from "./contrast";

/**
 * WCAG 2.1 contrast, used to prove the accessibility acceptance criteria in
 * docs/IMPLEMENTATION_PLAN.md slice 1 rather than assert them.
 *
 * The comps use light-gray body copy that fails 4.5:1. That is a known, documented
 * deviation — we darken it. "Darken it" needs a number, and a number needs a test.
 */
describe("relativeLuminance", () => {
  it("is 0 for black and 1 for white", () => {
    expect(relativeLuminance("#000000")).toBeCloseTo(0, 5);
    expect(relativeLuminance("#FFFFFF")).toBeCloseTo(1, 5);
  });

  it("accepts shorthand and lowercase hex", () => {
    expect(relativeLuminance("#fff")).toBeCloseTo(1, 5);
    expect(relativeLuminance("#ffffff")).toBeCloseTo(1, 5);
  });

  it("rejects malformed input rather than guessing", () => {
    expect(() => relativeLuminance("not-a-colour")).toThrowError(/hex/i);
    expect(() => relativeLuminance("#12345")).toThrowError(/hex/i);
  });
});

describe("contrastRatio", () => {
  it("is 21:1 for black on white", () => {
    expect(contrastRatio("#000000", "#FFFFFF")).toBeCloseTo(21, 1);
  });

  it("is 1:1 for a colour against itself", () => {
    expect(contrastRatio("#2D7FF9", "#2D7FF9")).toBeCloseTo(1, 5);
  });

  it("is order-independent", () => {
    const a = contrastRatio("#4E5B6D", "#FFFFFF");
    const b = contrastRatio("#FFFFFF", "#4E5B6D");
    expect(a).toBeCloseTo(b, 5);
  });
});

describe("meetsAA", () => {
  it("requires 4.5:1 for normal text", () => {
    expect(meetsAA(4.5, { large: false })).toBe(true);
    expect(meetsAA(4.49, { large: false })).toBe(false);
  });

  it("requires only 3:1 for large text", () => {
    expect(meetsAA(3, { large: true })).toBe(true);
    expect(meetsAA(2.99, { large: true })).toBe(false);
  });
});

/**
 * The comps' own body-copy gray, kept here as a regression marker. If someone
 * reintroduces it, this test says why it was rejected.
 */
describe("the deviation from the comps", () => {
  it("the comps' light-gray body copy fails AA on white", () => {
    const compsGray = "#9CA3AF";
    expect(meetsAA(contrastRatio(compsGray, "#FFFFFF"), { large: false })).toBe(false);
  });

  it("the sampled dark slate passes AA on white", () => {
    // #4E5B6D — sampled from the comp's dark swatch, which its own label
    // mis-states as #AA5568.
    expect(meetsAA(contrastRatio("#4E5B6D", "#FFFFFF"), { large: false })).toBe(true);
  });
});
