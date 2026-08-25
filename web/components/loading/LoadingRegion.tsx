/**
 * The announcement a loading screen makes.
 *
 * A SPINNER THAT ANNOUNCES NOTHING IS INVISIBLE. Every skeleton in this application is
 * built from `aria-hidden` rectangles, which is right — forty of them read out one by one
 * would be worse than silence — but it means the whole screen is inaudible unless
 * something says what is happening. This is that something, exactly once per screen.
 *
 * `role="status"` rather than `aria-live="polite"` written out: the role carries the same
 * politeness and also gives the region a name assistive technology can navigate to.
 * Setting both is not a belt-and-braces measure, it is a duplicate live region, and some
 * screen readers announce it twice.
 *
 * `aria-busy` is on the region rather than the document. It is the honest scope: the
 * layout around this — the practice navigation, the sign-out control — is interactive the
 * whole time the segment is streaming, and telling a screen reader the page is busy would
 * be describing a screen this is not.
 *
 * ON AN INITIAL PAGE LOAD THIS ANNOUNCES NOTHING, and that is a limitation rather than a
 * bug: a live region present in the first paint has no change to report. It speaks on
 * client-side navigation, which is the case the fallback exists for — a clinician moving
 * between screens on a container that has scaled to zero. On a cold document load the
 * browser's own loading indicator is still running.
 */
export function LoadingRegion({
  label,
  children,
  className = "",
}: {
  /**
   * What is being waited for, in words — "Loading today's visits". Written for someone
   * who cannot see the shapes, so it names the screen rather than saying "Loading".
   *
   * NEVER INTERPOLATE A RECORD INTO THIS. It is read aloud, potentially in a family's
   * living room, before the page it describes has even arrived.
   */
  label: string;
  children: React.ReactNode;
  className?: string;
}) {
  return (
    <div role="status" aria-busy="true" className={className}>
      <span className="sr-only">{label}</span>
      {children}
    </div>
  );
}
