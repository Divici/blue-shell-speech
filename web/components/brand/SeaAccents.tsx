/**
 * The decorative sea elements from the comps.
 *
 * Comp 2's "Graphic Elements" panel shows a coral shell, a starfish, and sea plants
 * scattered around the sections. The SVGs for these already existed in /assets and were
 * simply never placed — the page looked finished without them and reads as noticeably
 * warmer with them.
 *
 * Inlined rather than served as files: each is well under a kilobyte, and three <img>
 * requests on a page whose containers scale to zero costs more than the markup does.
 *
 * ALL PURELY DECORATIVE. aria-hidden, pointer-events-none, absolutely positioned so they
 * can never affect layout or produce a scrollbar.
 */

interface AccentProps {
  className?: string;
  size?: number;
}

/** A scallop in coral pink. The warm counterpoint to the blue palette. */
export function CoralShell({ className, size = 64 }: AccentProps) {
  return (
    <svg
      width={size}
      height={size * 0.8}
      viewBox="0 0 100 80"
      aria-hidden="true"
      focusable="false"
      className={className}
    >
      <path
        d="M50 70C21 70 9 56 13 36 17 16 33 7 50 7s33 9 37 29c4 20-8 34-37 34Z"
        fill="#FFAAA1"
      />
      <path
        d="M50 66V13M50 14 34 64M50 14l16 50M45 15 21 57M55 15l24 42"
        stroke="#FFE0DC"
        strokeWidth="3"
      />
      <path d="M17 56c20 12 46 12 66 0" fill="none" stroke="#E87E75" strokeWidth="2" />
    </svg>
  );
}

export function Starfish({ className, size = 48 }: AccentProps) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 100 100"
      aria-hidden="true"
      focusable="false"
      className={className}
    >
      <path
        d="m50 6 10 28 30-6-22 21 19 24-30-8-7 29-9-29-29 9 18-25L9 29l30 5Z"
        fill="#FF9C64"
        stroke="#E97A42"
        strokeWidth="2"
      />
      <circle cx="43" cy="46" r="2" fill="#DF6F36" />
      <circle cx="57" cy="51" r="2" fill="#DF6F36" />
      <circle cx="49" cy="60" r="2" fill="#DF6F36" />
    </svg>
  );
}

/** Kelp fronds. Tall and narrow, so it sits beside a column rather than behind it. */
export function SeaPlant({ className, size = 120 }: AccentProps) {
  return (
    <svg
      width={size}
      height={size * 1.375}
      viewBox="0 0 160 220"
      aria-hidden="true"
      focusable="false"
      className={className}
    >
      <g fill="none" strokeLinecap="round">
        <path d="M70 220C68 155 89 105 84 42" stroke="#4F9FA1" strokeWidth="5" />
        <path d="M101 220c-9-55 13-95 29-143" stroke="#79BFB1" strokeWidth="5" />
        <path d="M43 220c10-49-6-84-26-126" stroke="#2F79A8" strokeWidth="5" />
      </g>
      <g fill="#7FC5C0" fillOpacity="0.85">
        <path d="M83 100c-23-2-29-18-22-35 21 7 29 19 22 35Z" />
        <path d="M92 140c22-4 30-20 22-36-20 8-28 20-22 36Z" />
        <path d="M52 150c-21-6-26-23-16-38 19 10 25 23 16 38Z" />
      </g>
    </svg>
  );
}
