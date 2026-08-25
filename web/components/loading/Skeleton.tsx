/**
 * The placeholder shapes a loading screen is built from.
 *
 * A Server Component with no state and no event handlers — a skeleton that shipped
 * JavaScript to a browser in order to render a grey rectangle would be paying for the
 * cold start twice.
 *
 * WHY SHAPES RATHER THAN A SPINNER. These are used where the shape of what is coming is
 * known: a day's visits, a caseload, a note's four sections. Reserving the real layout's
 * dimensions means content lands in the space already held for it, so nothing jumps
 * (CLS ≤ 0.1 — blue-shell-frontend-engineering-rules §6). A centred spinner reserves
 * nothing and every arrival is a layout shift. Where the shape is NOT known — a form
 * being submitted, a credential being checked — the answer is `Spinner`, not this.
 *
 * NOTHING HERE CARRIES DATA. A skeleton is drawn from constants; it never renders a name,
 * an initial, or a length derived from a record. That is a privacy property as well as a
 * rendering one: this is the frame that is on screen while a patient list is in flight,
 * and it must be identical whether the caseload is empty or forty children.
 */

/**
 * One placeholder bar.
 *
 * `aria-hidden` throughout: the announcement belongs to the `LoadingRegion` around these,
 * once, rather than to each of forty rectangles that mean nothing individually.
 *
 * MOTION IS OPT-OUT TWICE OVER. `motion-reduce:animate-none` states the intent at the
 * element, and `app/globals.css` neutralises every animation under
 * `prefers-reduced-motion: reduce` as a backstop. The class is the control a test can
 * name; the stylesheet is what catches anything that forgets it.
 */
export function Skeleton({ className = "" }: { className?: string }) {
  return (
    <span
      aria-hidden="true"
      className={`block animate-pulse rounded-lg bg-ice motion-reduce:animate-none ${className}`}
    />
  );
}

/**
 * A run of text lines, shortest last.
 *
 * Paragraphs do not end flush, and a block of equal-length bars reads as a table rather
 * than as prose. The widths are fixed fractions rather than random ones: a server and a
 * client that disagree about a random width is a hydration mismatch (D041, the same
 * reasoning as the ambient bubbles).
 */
export function SkeletonText({
  lines = 3,
  className = "",
}: {
  lines?: number;
  className?: string;
}) {
  const widths = ["w-full", "w-11/12", "w-4/5", "w-full", "w-3/5"];

  return (
    <span className={`block space-y-2.5 ${className}`}>
      {Array.from({ length: lines }, (_, index) => (
        <Skeleton
          key={index}
          className={`h-3.5 ${index === lines - 1 ? "w-2/5" : widths[index % widths.length]}`}
        />
      ))}
    </span>
  );
}

/** A card-shaped placeholder — the border and padding the real cards use. */
export function SkeletonCard({
  className = "",
  children,
}: {
  className?: string;
  children: React.ReactNode;
}) {
  return (
    <div className={`rounded-2xl border border-ice bg-white p-6 ${className}`}>{children}</div>
  );
}
