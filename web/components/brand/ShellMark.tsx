import { palette } from "@/lib/design-tokens";

interface ShellMarkProps {
  /** Rendered size in px. The mark is drawn on a 64×64 grid and scales cleanly. */
  size?: number;
  className?: string;
  /** Light variant for the navy footer. */
  variant?: "brand" | "light";
}

/**
 * The Blue Shell Speech mark: a scallop shell.
 *
 * Inline SVG rather than a file — it appears in the header and footer of every page, so
 * an <img> would cost two extra requests on the critical path of a site whose containers
 * scale to zero and whose LCP budget is 2.5s.
 *
 * Decorative by default: the wordmark beside it carries the accessible name, so
 * announcing "shell" as well would make a screen reader read the brand twice.
 */
export function ShellMark({ size = 40, className, variant = "brand" }: ShellMarkProps) {
  const ribs = variant === "light" ? "#FFFFFF" : palette.navy;
  const body = variant === "light" ? "#FFFFFF" : palette.blue;
  const bodyOpacity = variant === "light" ? 0.22 : 1;

  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 64 64"
      fill="none"
      aria-hidden="true"
      focusable="false"
      className={className}
    >
      {/*
        Scallop shell: hinge at the bottom, fanning upward to a scalloped top edge.
        The zig-zag along the top is what reads as "shell" at 36px — a smooth arc
        reads as a generic blob, which is what the first attempt produced.
      */}
      <path
        d="M31 55c-11-2-21-12-23-27l7 3 6-6 6 5 6-6 6 6 6-5 6 6 7-3c-2 15-12 25-23 27l1 2a17 17 0 0 1-6 0Z"
        fill={body}
        fillOpacity={bodyOpacity}
      />

      {/*
        Ribs, fanning from the hinge to each scallop point.

        Weight and opacity are tuned for the size this is actually used at — 30–40px in
        the header, where the mark has to read as a shell in a browser tab and next to a
        wordmark. An earlier version used 1.8px at 40% opacity, which at 36px rendered as
        a featureless blue blob: the ribs were technically present and visually absent.
      */}
      <g
        stroke={ribs}
        strokeOpacity={variant === "light" ? 0.9 : 0.75}
        strokeWidth="2.6"
        strokeLinecap="round"
      >
        <path d="M32 52V22" />
        <path d="m25 51-5-23" />
        <path d="m39 51 5-23" />
        <path d="M19 47 12 31" />
        <path d="M45 47 52 31" />
      </g>
    </svg>
  );
}
