/**
 * WCAG 2.1 relative luminance and contrast ratio.
 *
 * Exists so accessibility claims in this codebase are measured rather than asserted.
 * blue-shell-frontend-engineering-rules requires 4.5:1 body copy; the comps do not meet
 * it, and "we darkened it" is only meaningful with a number attached.
 *
 * https://www.w3.org/TR/WCAG21/#dfn-relative-luminance
 */

const HEX = /^#?([0-9a-f]{3}|[0-9a-f]{6})$/i;

function toRgb(hex: string): [number, number, number] {
  const match = HEX.exec(hex.trim());
  if (!match) {
    throw new Error(
      `Expected a 3- or 6-digit hex colour, received "${hex}". ` +
        "Refusing to guess — a silently wrong colour defeats the point of measuring it.",
    );
  }

  let body = match[1] as string;
  if (body.length === 3) {
    body = body
      .split("")
      .map((c) => c + c)
      .join("");
  }

  return [
    Number.parseInt(body.slice(0, 2), 16),
    Number.parseInt(body.slice(2, 4), 16),
    Number.parseInt(body.slice(4, 6), 16),
  ];
}

/** Linearises one 0–255 channel per the WCAG definition. */
function linearise(channel: number): number {
  const c = channel / 255;
  return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
}

export function relativeLuminance(hex: string): number {
  const [r, g, b] = toRgb(hex);
  return 0.2126 * linearise(r) + 0.7152 * linearise(g) + 0.0722 * linearise(b);
}

/** Contrast ratio between two colours, 1:1 to 21:1. Order-independent. */
export function contrastRatio(foreground: string, background: string): number {
  const a = relativeLuminance(foreground);
  const b = relativeLuminance(background);
  const lighter = Math.max(a, b);
  const darker = Math.min(a, b);
  return (lighter + 0.05) / (darker + 0.05);
}

/**
 * WCAG AA: 4.5:1 for normal text, 3:1 for large text
 * (≥ 24px, or ≥ 18.66px when bold).
 */
export function meetsAA(ratio: number, { large }: { large: boolean }): boolean {
  return ratio >= (large ? 3 : 4.5);
}
