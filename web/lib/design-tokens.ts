/**
 * Design tokens, from comp 2's design-system sidebar.
 *
 * Two deliberate departures from the comp, both required:
 *
 * 1. THE COMP'S DARK SWATCH IS MISLABELLED. It reads `#AA5568` — a mauve — but the
 *    rendered pixels sample to `#4E5B6D`, a blue-slate. The pixels are the design; the
 *    label is a typo. Sampled 2026-08-24 from the swatch centre.
 *
 * 2. BODY COPY IS DARKENED. The comps set body text in a light gray (~#9CA3AF) that
 *    fails WCAG AA on white at roughly 2.5:1. Accessibility beats fidelity — see
 *    blue-shell-frontend-engineering-rules and docs/SITE_CONTENT.md.
 *
 * Every pairing used in the UI is contrast-tested in design-tokens.test.ts. Add a
 * colour here and add its pairing there.
 */

export const palette = {
  /**
   * Brand blue, exactly as the comp specifies.
   *
   * DECORATIVE AND LARGE TEXT ONLY. White text on this is 3.81:1, which fails WCAG AA
   * for normal text — so the comp's own primary button is inaccessible as drawn. Use
   * `blueAction` for anything carrying white label text.
   */
  blue: "#2D7FF9",

  /**
   * Interactive surfaces: primary buttons, CTA backgrounds — anywhere white text sits
   * on blue. 4.77:1, the smallest darkening of `blue` that clears AA, so the brand
   * still reads as itself.
   */
  blueAction: "#1D6FE0",
  /** Deeper blue for small text and hover states — the eyebrow needs the extra weight. */
  blueDeep: "#1B5FCC",
  /** Headings, footer background. */
  navy: "#1B4FA3",

  /** Tinted surfaces. `ice` is the section wash, `mist` the page background. */
  ice: "#E8F3FF",
  mist: "#F5FAFF",
  white: "#FFFFFF",

  /** Accents from the comp: teal, and the warm trio used on the step icons. */
  teal: "#4E9E9A",
  sand: "#FFD786",
  coral: "#FF8FA3",
  amber: "#FFBD59",

  /**
   * Text. `ink` is the sampled dark slate; `inkMuted` is the darkened replacement for
   * the comps' failing light gray.
   */
  ink: "#3A4553",
  inkMuted: "#55606E",
} as const;

export type PaletteColor = (typeof palette)[keyof typeof palette];
