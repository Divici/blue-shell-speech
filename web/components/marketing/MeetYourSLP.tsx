import { ResponsiveImage, IMAGE_WIDTHS } from "@/components/ResponsiveImage";
import { ABOUT, SERVICE_CHIPS } from "@/lib/site-content";
import { SeaPlant } from "@/components/brand/SeaAccents";
import {
  ChatIcon,
  PeopleIcon,
  HandHeartIcon,
  HomeIcon,
  AacIcon,
  HeartCheckIcon,
} from "@/components/icons";

const CHIP_ICONS = {
  chat: ChatIcon,
  people: PeopleIcon,
  "hand-heart": HandHeartIcon,
  home: HomeIcon,
  aac: AacIcon,
} as const;

/**
 * "Meet Your SLP", with the service chips.
 *
 * The chips replace the services grid Michelle removed. They matter for a specific
 * reason: the three hero badges describe HOW she works — in-home, birth-to-5,
 * personalized — and none describe WHAT she treats. Cutting the grid removed the only
 * mention of AAC from the entire site, and a parent searching for an AAC provider would
 * have found nothing.
 *
 * The chips carry `id="services"` because the nav's Services link scrolls here.
 */
export function MeetYourSLP() {
  return (
    <section id="about" className="relative overflow-hidden bg-white pb-[var(--spacing-section)]">
      <SeaPlant
        size={150}
        className="pointer-events-none absolute -right-8 bottom-0 hidden opacity-70 xl:block"
      />
      <div className="mx-auto grid max-w-6xl items-center gap-10 px-4 sm:px-6 lg:grid-cols-[minmax(0,0.85fr)_minmax(0,1fr)] lg:gap-16">
        <div className="relative mx-auto w-full max-w-sm lg:max-w-none">
          <div className="overflow-hidden rounded-[54%_46%_52%_48%/44%_50%_50%_56%] bg-ice shadow-[0_18px_50px_-26px_rgba(27,79,163,0.5)]">
            <ResponsiveImage
              name="headshot"
              widths={IMAGE_WIDTHS.headshot}
              fallbackWidth={640}
              alt="Michelle, licensed Speech-Language Pathologist"
              width={640}
              height={800}
              sizes="(max-width: 1024px) 60vw, 380px"
              className="h-auto w-full object-cover"
            />
          </div>
        </div>

        <div>
          <p className="mb-3 text-sm font-semibold uppercase tracking-[0.18em] text-blue-deep">
            {ABOUT.eyebrow}
          </p>
          <h2 className="font-display text-[clamp(1.9rem,4vw,2.75rem)] font-bold text-navy">
            {ABOUT.heading}
          </h2>

          <p className="mt-4 max-w-xl text-lg leading-relaxed text-ink-muted">
            {ABOUT.body}
          </p>

          <ul className="mt-6 space-y-3">
            {ABOUT.credentials.map((credential) => (
              <li key={credential} className="flex items-start gap-3">
                <HeartCheckIcon
                  size={20}
                  className="mt-0.5 shrink-0 text-teal"
                />
                <span className="text-ink">{credential}</span>
              </li>
            ))}
          </ul>

          <div id="services" className="mt-8 scroll-mt-28">
            <h3 className="text-sm font-semibold uppercase tracking-[0.18em] text-ink-muted">
              Areas of support
            </h3>
            <ul className="mt-3 flex flex-wrap gap-2">
              {SERVICE_CHIPS.map((chip) => {
                const ChipIcon = CHIP_ICONS[chip.icon];
                return (
                  <li key={chip.label}>
                    <span className="inline-flex items-center gap-2 rounded-full border border-ice bg-mist px-3.5 py-2 text-sm font-medium text-ink">
                      <ChipIcon size={17} className="text-blue-deep" />
                      {chip.label}
                    </span>
                  </li>
                );
              })}
            </ul>
          </div>
        </div>
      </div>
    </section>
  );
}
