import Link from "next/link";
import Image from "next/image";
import { HERO, BADGES } from "@/lib/site-content";
import { WaveDivider } from "@/components/brand/WaveDivider";
import { ArrowRightIcon, HomeIcon, HeartCheckIcon, StarIcon } from "@/components/icons";

const BADGE_ICONS = {
  home: HomeIcon,
  heart: HeartCheckIcon,
  star: StarIcon,
} as const;

/**
 * Hero. A Server Component — nothing here is interactive.
 *
 * The photo is the LCP element, so it carries `priority` (preloaded, not lazy-loaded)
 * and explicit dimensions. Both matter more than usual: the containers scale to zero,
 * so a first visitor is already paying a cold start before the image request begins.
 */
export function Hero() {
  return (
    <section id="top" className="relative bg-mist">
      <div className="mx-auto grid max-w-6xl items-center gap-10 px-4 pt-12 pb-4 sm:px-6 lg:grid-cols-2 lg:gap-14 lg:pt-20">
        <div>
          <p className="mb-4 flex items-center gap-2 text-sm font-semibold uppercase tracking-[0.18em] text-blue-deep">
            <StarIcon size={18} className="text-amber" />
            {HERO.eyebrow}
          </p>

          {/*
            No forced line breaks. The comp's three lines come from its column width,
            not from hard breaks — forcing them here produced four lines at desktop and
            worse breaks on narrow viewports. `text-balance` lets the browser distribute
            words evenly instead.
          */}
          <h1 className="font-display text-[clamp(2.1rem,4.4vw,3.25rem)] font-bold leading-[1.1] text-balance text-navy">
            {HERO.heading.join(" ")}
          </h1>

          <p className="mt-5 max-w-xl text-lg leading-relaxed text-ink-muted">
            {HERO.body}
          </p>

          <div className="mt-8 flex flex-wrap gap-3">
            <Link
              href={HERO.primaryCta.href}
              className="inline-flex items-center gap-2 rounded-full bg-blue-action px-6 py-3.5 font-semibold text-white transition-opacity hover:opacity-90"
            >
              {HERO.primaryCta.label}
            </Link>
            <Link
              href={HERO.secondaryCta.href}
              className="inline-flex items-center gap-2 rounded-full border border-blue/40 bg-white px-6 py-3.5 font-semibold text-blue-deep transition-colors hover:border-blue"
            >
              {HERO.secondaryCta.label}
              <ArrowRightIcon size={18} />
            </Link>
          </div>
        </div>

        <div className="relative">
          {/*
            The organic blob mask from the comps, as a border-radius rather than an SVG
            clip-path — it scales with the element, costs nothing to render, and degrades
            to a rounded rectangle rather than disappearing if unsupported.
          */}
          <div className="overflow-hidden rounded-[42%_58%_46%_54%/50%_42%_58%_50%] shadow-[0_18px_50px_-24px_rgba(27,79,163,0.45)]">
            <Image
              src="/img/children-1080.avif"
              alt="Two young children playing together with colourful building blocks"
              width={1080}
              height={720}
              priority
              sizes="(max-width: 1024px) 100vw, 540px"
              className="h-auto w-full object-cover"
            />
          </div>
        </div>
      </div>

      <ul className="mx-auto mt-10 grid max-w-6xl gap-4 px-4 pb-4 sm:px-6 sm:grid-cols-3">
        {BADGES.map((badge) => {
          const BadgeIcon = BADGE_ICONS[badge.icon];
          return (
            <li key={badge.title} className="flex items-center gap-3">
              <span className="grid size-11 shrink-0 place-items-center rounded-2xl bg-ice text-blue-deep">
                <BadgeIcon size={22} />
              </span>
              <span>
                <span className="block font-semibold text-navy">{badge.title}</span>
                <span className="block text-sm text-ink-muted">{badge.caption}</span>
              </span>
            </li>
          );
        })}
      </ul>

      <WaveDivider fill="#FFFFFF" />
    </section>
  );
}
