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

      {/* Ribs, fanning from the hinge to each scallop point. */}
      <g
        stroke={ribs}
        strokeOpacity={variant === "light" ? 0.85 : 0.4}
        strokeWidth="1.8"
        strokeLinecap="round"
      >
        <path d="M32 53V24" />
        <path d="m26 52-5-22" />
        <path d="m38 52 5-22" />
        <path d="M20.5 48 13 33" />
        <path d="M43.5 48 51 33" />
      </g>
    </svg>
  );
}
