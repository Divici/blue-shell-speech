import Link from "next/link";
import { ShellMark } from "@/components/brand/ShellMark";
import { WaveDivider } from "@/components/brand/WaveDivider";
import { NAV_ITEMS, SERVICE_CHIPS, PRACTICE_NAME, CONTACT } from "@/lib/site-content";

/**
 * Footer.
 *
 * No social links: the comps show Facebook/Instagram/email icons, but the practice has
 * no social accounts yet, and a link to a page that does not exist is worse than no
 * link. Add them when the accounts exist — a content change, not a build.
 *
 * No "Resources" entry for the same reason: the resource system is built, but no
 * handouts exist yet (docs/SITE_CONTENT.md).
 */
export function SiteFooter() {
  const year = new Date().getFullYear();

  return (
    <footer className="mt-auto bg-navy text-white">
      <WaveDivider fill="#1B4FA3" direction="up" className="-mt-px bg-white" />

      <div className="mx-auto grid max-w-6xl gap-10 px-4 pb-10 sm:px-6 sm:grid-cols-2 lg:grid-cols-3">
        <div>
          <Link href="/#top" className="flex items-center gap-2.5 font-display text-xl font-bold">
            <ShellMark size={36} variant="light" />
            <span className="leading-none">
              Blue Shell
              <span className="block font-sans text-[0.62rem] font-semibold uppercase tracking-[0.28em] text-white/70">
                Speech
              </span>
            </span>
          </Link>
          <p className="mt-4 max-w-xs text-sm leading-relaxed text-white/75">
            In-home speech-language therapy for children birth to 5 years in{" "}
            {CONTACT.serviceArea}.
          </p>
        </div>

        <nav aria-label="Footer">
          <h2 className="text-sm font-semibold uppercase tracking-[0.18em] text-white/60">
            Quick links
          </h2>
          <ul className="mt-4 space-y-2.5">
            {NAV_ITEMS.map((item) => (
              <li key={item.label}>
                <Link href={`/${item.href}`} className="text-sm text-white/85 hover:text-white">
                  {item.label}
                </Link>
              </li>
            ))}
            <li>
              <Link href="/consultation" className="text-sm text-white/85 hover:text-white">
                Free Consultation
              </Link>
            </li>
          </ul>
        </nav>

        <div>
          <h2 className="text-sm font-semibold uppercase tracking-[0.18em] text-white/60">
            Areas of support
          </h2>
          <ul className="mt-4 space-y-2.5">
            {SERVICE_CHIPS.map((chip) => (
              <li key={chip.label} className="text-sm text-white/85">
                {chip.label}
              </li>
            ))}
          </ul>
        </div>
      </div>

      <div className="border-t border-white/15">
        <div className="mx-auto flex max-w-6xl flex-col gap-2 px-4 py-5 text-xs text-white/65 sm:flex-row sm:items-center sm:justify-between sm:px-6">
          <p>
            © {year} {PRACTICE_NAME}. All rights reserved.
          </p>
          <p>
            <Link href="/login" className="hover:text-white">
              Provider login
            </Link>
          </p>
        </div>
      </div>
    </footer>
  );
}
