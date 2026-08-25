"use client";

import Link, { useLinkStatus } from "next/link";

/**
 * A link that admits it has been pressed.
 *
 * WHY THIS EXISTS RATHER THAN A BUTTON. The status tabs on the enquiry inbox and the day
 * arrows on the schedule LOOK like in-page controls, but they are navigations, and
 * deliberately so: each one is a URL, so the phone's back button works, a filtered inbox
 * can be bookmarked, and the screen needs no JavaScript to change what it shows. Replacing
 * them with `router.push` inside a transition would buy a pending state and cost all
 * three. So the link stays a link and the feedback is added underneath it.
 *
 * THE GAP IT CLOSES IS SPECIFIC. `loading.tsx` swaps the whole segment for a skeleton, so
 * the page does react — but the tab strip is rendered from the search parameter the render
 * is still waiting on, which means for the length of a cold start the OLD tab is still the
 * highlighted one. The clinician has tapped "New", the page is visibly working, and the
 * control she tapped looks untouched. This is the mark that says which one she pressed.
 *
 * `useLinkStatus` requires a descendant of `<Link>`, which is why the hint is its own
 * component and why this wrapper exists at all.
 *
 * PREFETCH IS LEFT ALONE. Next's guidance is that this hook pairs with `prefetch={false}`,
 * and that is the wrong trade here: these routes are `force-dynamic`, so a prefetch fetches
 * the loading shell and nothing else, and turning it off would make every tap slower to
 * buy a more reliable dot.
 */
export function PendingLink({
  href,
  className,
  children,
  ...rest
}: {
  href: string;
  className?: string;
  children: React.ReactNode;
} & Omit<React.ComponentProps<typeof Link>, "href" | "className" | "children">) {
  return (
    /*
     * `relative` is added rather than asked for, because the hint below is positioned
     * against this element and a caller that forgot it would put the dot in the corner of
     * the page. None of the call sites position themselves, so there is nothing to clash
     * with.
     */
    <Link href={href} className={`relative ${className ?? ""}`} {...rest}>
      {children}
      <LinkPendingHint />
    </Link>
  );
}

/**
 * The mark itself.
 *
 * OUT OF THE LAYOUT ENTIRELY, which is the second version of this. The first reserved its
 * space inline — always rendered, opacity toggled — which is what the Next.js
 * documentation suggests and does avoid a shift. It also pushed the label of every tab
 * about six pixels left of centre, across a strip of five, permanently. Absolute
 * positioning gets the same guarantee for free: the dot occupies no space at any time, so
 * there is nothing to reserve and nothing to shift, and the text stays where it was drawn.
 *
 * It sits INSIDE the pill's own right padding — `px-4` on every call site, and the dot is
 * six pixels from the edge — so it never overlaps a label or an arrow glyph.
 *
 * Still rendered unconditionally rather than mounted on demand, now for the transition
 * rather than the layout: a `transition-opacity` on an element that does not exist yet has
 * nothing to animate from, and the dot would snap in.
 *
 * `aria-hidden`, and that is not an oversight. The announcement for a navigation belongs to
 * the destination's `LoadingRegion`, which is a `role="status"` that says which screen is
 * loading. A second live region on the link would announce the same wait twice, in less
 * useful words — and a bare dot has nothing meaningful to say on its own.
 *
 * The pulse is `motion-reduce:animate-none`; under reduced motion the dot still appears, it
 * simply stops throbbing, so the feedback survives the preference rather than disappearing
 * with it.
 */
function LinkPendingHint() {
  const { pending } = useLinkStatus();

  return (
    <span
      aria-hidden="true"
      data-pending={pending ? "true" : "false"}
      className={`absolute top-1/2 right-1.5 size-1.5 -translate-y-1/2 rounded-full bg-current transition-opacity ${
        pending ? "animate-pulse opacity-100 motion-reduce:animate-none" : "opacity-0"
      }`}
    />
  );
}
