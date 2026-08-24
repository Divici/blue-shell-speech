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
      {/* Shell body: hinge at the bottom, fanning to a rounded top edge. */}
      <path
        d="M32 58c-4.4 0-8-1.6-8-3.4 0-.5.3-1 .8-1.4C14.4 49.6 6 39.4 6 27.6 6 15.7 17.6 6 32 6s26 9.7 26 21.6c0 11.8-8.4 22-18.8 25.6.5.4.8.9.8 1.4 0 1.8-3.6 3.4-8 3.4Z"
        fill={body}
        fillOpacity={bodyOpacity}
      />

      {/* Ribs. Drawn from the hinge outward, shortest at the edges. */}
      <g stroke={ribs} strokeOpacity={variant === "light" ? 0.9 : 0.45} strokeWidth="2" strokeLinecap="round">
        <path d="M32 52V13" />
        <path d="M24.5 51 17 17.5" />
        <path d="M39.5 51 47 17.5" />
        <path d="M18 47.5 9.5 25" />
        <path d="M46 47.5 54.5 25" />
      </g>

      {/* Hinge highlight. */}
      <path
        d="M32 58c-4.4 0-8-1.6-8-3.4s3.6-3.4 8-3.4 8 1.5 8 3.4S36.4 58 32 58Z"
        fill={ribs}
        fillOpacity={variant === "light" ? 0.9 : 0.55}
      />
    </svg>
  );
}
