/**
 * The indeterminate one, for work whose shape is not known in advance.
 *
 * A skeleton claims to know what is coming and reserves room for it. Submitting a form,
 * checking a credential, signing a note — none of those has a shape to reserve, and a
 * skeleton drawn for them would be a lie about the layout as well as a layout shift when
 * the truth arrived. This is what goes there instead.
 *
 * INSIDE A BUTTON, NOT INSTEAD OF ONE. It sits beside a label that has already changed to
 * the present tense ("Signing…"), because the label is what a screen reader reads and what
 * survives `prefers-reduced-motion`. The ring is `aria-hidden` and adds nothing to the
 * accessible name.
 *
 * `currentColor` so one component serves a white-on-blue primary button and a blue-on-white
 * secondary without a variant prop.
 *
 * REDUCED MOTION LEAVES A STATIC RING rather than nothing. `animate-spin` rotates a
 * three-quarter arc from 0° to 360°, so the frozen frame is the arc at its starting
 * position — still a legible "something is happening" mark next to the label, which a
 * `display: none` would not be.
 */
export function Spinner({ size = 16, className = "" }: { size?: number; className?: string }) {
  return (
    <svg
      aria-hidden="true"
      focusable="false"
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      className={`animate-spin motion-reduce:animate-none ${className}`}
    >
      {/* The track, at low opacity — without it the arc reads as a comma rather than a ring. */}
      <circle cx="12" cy="12" r="9" stroke="currentColor" strokeWidth="3" opacity="0.25" />
      <path
        d="M21 12a9 9 0 0 0-9-9"
        stroke="currentColor"
        strokeWidth="3"
        strokeLinecap="round"
      />
    </svg>
  );
}
