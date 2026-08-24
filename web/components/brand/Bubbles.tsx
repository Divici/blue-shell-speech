interface BubblesProps {
  className?: string;
  /** Rough count. Positions are fixed, not random — see below. */
  variant?: "left" | "right";
}

/**
 * The drifting bubbles from the comps.
 *
 * Positions are HARDCODED, not randomised. Random values would differ between the server
 * render and the client hydration, producing a React hydration mismatch — and even with
 * that solved, a decorative element that moves on every reload is a layout the designer
 * never approved.
 *
 * Purely decorative: aria-hidden, pointer-events-none, and positioned absolutely so it
 * can never affect layout or cause a scrollbar.
 *
 * The float animation is disabled under `prefers-reduced-motion` by the global rule in
 * globals.css — vestibular disorders are a real accessibility concern, and gently
 * drifting circles are exactly the kind of ambient motion that triggers them.
 */
const LAYOUTS = {
  left: [
    { size: 10, top: "18%", left: "4%", delay: "0s" },
    { size: 6, top: "34%", left: "11%", delay: "1.4s" },
    { size: 14, top: "58%", left: "6%", delay: "2.6s" },
    { size: 8, top: "76%", left: "13%", delay: "0.8s" },
  ],
  right: [
    { size: 12, top: "22%", left: "88%", delay: "0.6s" },
    { size: 7, top: "44%", left: "94%", delay: "2.1s" },
    { size: 16, top: "66%", left: "86%", delay: "1.2s" },
    { size: 9, top: "82%", left: "92%", delay: "3s" },
  ],
} as const;

export function Bubbles({ className, variant = "left" }: BubblesProps) {
  return (
    <div
      aria-hidden="true"
      className={`pointer-events-none absolute inset-0 overflow-hidden ${className ?? ""}`}
    >
      {LAYOUTS[variant].map((bubble, index) => (
        <span
          key={index}
          className="bubble absolute rounded-full bg-blue/15"
          style={{
            width: bubble.size,
            height: bubble.size,
            top: bubble.top,
            left: bubble.left,
            animationDelay: bubble.delay,
          }}
        />
      ))}
    </div>
  );
}
