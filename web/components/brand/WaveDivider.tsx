interface WaveDividerProps {
  /** Colour of the section BELOW the wave — the wave paints the incoming section. */
  fill: string;
  /** `down` sits at a section's bottom edge; `up` at its top. */
  direction?: "down" | "up";
  className?: string;
}

/**
 * The soft wave that separates sections in the comps.
 *
 * `preserveAspectRatio="none"` lets one path stretch to any viewport width without
 * generating a horizontal scrollbar — the wave is decorative, so distorting it is fine
 * and is what keeps CLS at zero across breakpoints.
 *
 * The container has an explicit height so the browser reserves space before paint.
 * A wave that reflows on load is a layout shift on the largest element above the fold.
 */
export function WaveDivider({ fill, direction = "down", className }: WaveDividerProps) {
  return (
    <div
      aria-hidden="true"
      className={className}
      style={{
        lineHeight: 0,
        transform: direction === "up" ? "rotate(180deg)" : undefined,
      }}
    >
      <svg
        viewBox="0 0 1440 90"
        preserveAspectRatio="none"
        style={{ display: "block", width: "100%", height: "clamp(36px, 5vw, 72px)" }}
        focusable="false"
      >
        <path
          d="M0 42c120-28 240-38 360-24s240 52 360 55 240-30 360-46 240-12 360 6v57H0V42Z"
          fill={fill}
          fillOpacity="0.55"
        />
        <path
          d="M0 58c144-30 288-34 432-16s288 46 432 42 288-32 432-44v50H0V58Z"
          fill={fill}
        />
      </svg>
    </div>
  );
}
