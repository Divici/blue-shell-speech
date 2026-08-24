import type { SVGProps } from "react";

/**
 * The icon set from comp 2's "Icons Style" panel: soft, rounded, single-weight strokes.
 *
 * Hand-built rather than pulled from a library. The comps specify a particular roundness
 * and stroke weight, and matching that with an off-the-shelf set means either overriding
 * every icon or accepting a slightly different visual voice on a page whose whole job is
 * feeling warm and unclinical.
 *
 * Every icon is `aria-hidden` — each one sits next to a visible text label, and an icon
 * that announces itself makes a screen reader say everything twice.
 */

type IconProps = SVGProps<SVGSVGElement> & { size?: number };

function Icon({ size = 24, children, ...rest }: IconProps) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.75}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
      {...rest}
    >
      {children}
    </svg>
  );
}

/** Speech & language therapy. */
export const ChatIcon = (props: IconProps) => (
  <Icon {...props}>
    <path d="M20 12.5a7.5 7.5 0 0 1-7.5 7.5H8l-3.5 2.5V18A7.5 7.5 0 0 1 12.5 5h0A7.5 7.5 0 0 1 20 12.5Z" />
    <path d="M9.5 12h.01M12.5 12h.01M15.5 12h.01" />
  </Icon>
);

/** Social communication. */
export const PeopleIcon = (props: IconProps) => (
  <Icon {...props}>
    <circle cx="9" cy="8" r="3" />
    <circle cx="17" cy="9.5" r="2.25" />
    <path d="M3.5 19a5.5 5.5 0 0 1 11 0" />
    <path d="M15 19a4.5 4.5 0 0 1 5.5-4.4" />
  </Icon>
);

/** Early intervention — a hand holding a heart. */
export const HandHeartIcon = (props: IconProps) => (
  <Icon {...props}>
    <path d="M12 9.5c1-1.6 3.6-1.3 3.6.9 0 1.7-2.2 3.3-3.6 4.2-1.4-.9-3.6-2.5-3.6-4.2 0-2.2 2.6-2.5 3.6-.9Z" />
    <path d="M3.5 15.5a2 2 0 0 1 2.8 0l2 2h3.2a1.5 1.5 0 0 1 0 3H8" />
    <path d="M20.5 15.5c-2 1.6-4 3.4-6.2 4.6a4 4 0 0 1-1.9.4H8" />
  </Icon>
);

/** In-home therapy. */
export const HomeIcon = (props: IconProps) => (
  <Icon {...props}>
    <path d="M4 10.5 12 4l8 6.5" />
    <path d="M6 9.8V19a1 1 0 0 0 1 1h10a1 1 0 0 0 1-1V9.8" />
    <path d="M10 20v-4.5a2 2 0 0 1 4 0V20" />
  </Icon>
);

/** Scheduling — request a consultation. */
export const CalendarIcon = (props: IconProps) => (
  <Icon {...props}>
    <rect x="3.5" y="5" width="17" height="15" rx="3" />
    <path d="M3.5 9.5h17M8 3.5v3M16 3.5v3" />
    <path d="M8.5 13.5h2M13.5 13.5h2M8.5 16.5h2" />
  </Icon>
);

/** Personalized care / a plan tailored to the child. */
export const HeartCheckIcon = (props: IconProps) => (
  <Icon {...props}>
    <path d="M12 20s-7-4.4-7-9a4 4 0 0 1 7-2.6A4 4 0 0 1 19 11c0 4.6-7 9-7 9Z" />
    <path d="m9.5 11.5 1.8 1.8 3.4-3.4" />
  </Icon>
);

/** Milestones — start of therapy. */
export const StarIcon = (props: IconProps) => (
  <Icon {...props}>
    <path d="m12 4 2.5 5.1 5.5.8-4 3.9.9 5.6L12 16.7l-4.9 2.7.9-5.6-4-3.9 5.5-.8L12 4Z" />
  </Icon>
);

/** Augmentative and alternative communication. */
export const AacIcon = (props: IconProps) => (
  <Icon {...props}>
    <rect x="3.5" y="5" width="17" height="14" rx="2.5" />
    <path d="M7.5 9h3v3h-3zM13.5 9h3v3h-3zM7.5 14.5h9" />
  </Icon>
);

export const PhoneIcon = (props: IconProps) => (
  <Icon {...props}>
    <path d="M6.5 4h3l1.5 4-2 1.5a11 11 0 0 0 5.5 5.5L16 13l4 1.5v3a2 2 0 0 1-2.2 2A15.5 15.5 0 0 1 4.5 6.2 2 2 0 0 1 6.5 4Z" />
  </Icon>
);

export const MailIcon = (props: IconProps) => (
  <Icon {...props}>
    <rect x="3" y="5.5" width="18" height="13" rx="2.5" />
    <path d="m3.8 7 7.1 5.3a2 2 0 0 0 2.2 0L20.2 7" />
  </Icon>
);

export const ArrowRightIcon = (props: IconProps) => (
  <Icon {...props}>
    <path d="M5 12h13M13 7l5 5-5 5" />
  </Icon>
);
