import { STEPS } from "@/lib/site-content";
import { WaveDivider } from "@/components/brand/WaveDivider";
import {
  CalendarIcon,
  ChatIcon,
  HeartCheckIcon,
  StarIcon,
} from "@/components/icons";

const STEP_ICONS = {
  calendar: CalendarIcon,
  chat: ChatIcon,
  heart: HeartCheckIcon,
  star: StarIcon,
} as const;

/** Tints cycle through the comp's warm accents so each step reads distinctly. */
const STEP_TINTS = [
  "bg-ice text-blue-deep",
  "bg-teal/15 text-teal",
  "bg-sand/40 text-navy",
  "bg-coral/25 text-navy",
] as const;

/**
 * "Getting Started is Easy" — the four-step path from enquiry to first session.
 *
 * An ordered list, because the steps are genuinely sequential. The connecting arrows in
 * the comps are decorative and hidden from assistive tech; the numbering carries the
 * order instead, and it survives a narrow viewport where the arrows disappear.
 */
export function GettingStarted() {
  return (
    <section className="bg-ice/45 pt-[--spacing-section]">
      <div className="mx-auto max-w-6xl px-4 sm:px-6">
        <p className="text-center text-sm font-semibold uppercase tracking-[0.18em] text-blue-deep">
          How it works
        </p>
        <h2 className="mt-2 text-center font-display text-[clamp(1.9rem,4vw,2.75rem)] font-bold text-navy">
          Getting Started is Easy
        </h2>

        <ol className="mt-12 grid gap-8 sm:grid-cols-2 lg:grid-cols-4">
          {STEPS.map((step, index) => {
            const StepIcon = STEP_ICONS[step.icon];
            return (
              <li key={step.number} className="relative text-center">
                <span
                  className={`mx-auto grid size-16 place-items-center rounded-3xl ${STEP_TINTS[index]}`}
                >
                  <StepIcon size={28} />
                </span>

                <h3 className="mt-4 font-semibold text-navy">
                  <span className="text-blue-deep">{step.number}.</span> {step.title}
                </h3>
                <p className="mt-2 text-sm leading-relaxed text-ink-muted">{step.body}</p>

                {index < STEPS.length - 1 && (
                  <span
                    aria-hidden="true"
                    className="pointer-events-none absolute top-8 -right-4 hidden text-2xl leading-none text-blue/35 lg:block"
                  >
                    ›
                  </span>
                )}
              </li>
            );
          })}
        </ol>
      </div>

      <WaveDivider fill="#FFFFFF" className="mt-[--spacing-section]" />
    </section>
  );
}
