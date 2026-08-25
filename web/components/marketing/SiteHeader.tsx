"use client";

import Link from "next/link";
import { useEffect, useRef, useState } from "react";
import { ShellMark } from "@/components/brand/ShellMark";
import { NAV_ITEMS, PRACTICE_NAME } from "@/lib/site-content";

/**
 * AT A GLANCE
 * -----------
 * The public website's top navigation bar.
 *
 * Note the `"use client"` at the very top of this file. In the Next.js App Router, files
 * run on the SERVER by default and only ship to the browser if they opt in with that
 * directive. This one opts in for a single reason: the mobile menu has to open and close,
 * which needs `useState`.
 *
 * Keeping that boundary tight is deliberate — everything the browser receives is code an
 * attacker can read, so the less that crosses, the better.
 *
 * Sticky site header.
 *
 * A Client Component, and only because of the mobile menu — the nav links themselves are
 * plain anchors that work with JavaScript disabled. Keeping the boundary this tight is
 * the point: everything else on the page stays a Server Component.
 *
 * `Login` is styled as a secondary action. Michelle's parents should be drawn to
 * "Free Consultation"; the login is for her.
 *
 * A disclosure, not a dialog. Below `md` the inline list is display:none and the panel
 * takes over, and it is deliberately NOT a focus trap: the panel follows its button in DOM
 * order inside the same landmark, so the natural tab order already walks into it. Escape
 * closes it and hands focus back.
 */
export function SiteHeader() {
  const [menuOpen, setMenuOpen] = useState(false);
  const menuButtonRef = useRef<HTMLButtonElement>(null);

  // Escape closes the menu and returns focus to the control that opened it —
  // otherwise focus is stranded on a hidden element.
  useEffect(() => {
    if (!menuOpen) return;

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        setMenuOpen(false);
        menuButtonRef.current?.focus();
      }
    };

    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [menuOpen]);

  return (
    <header className="sticky top-0 z-50 border-b border-ice/70 bg-white/90 backdrop-blur-sm">
      {/*
        The bar is an inner wrapper so the collapsed panel can sit INSIDE the landmark.
        It previously followed </nav>, which meant that on a phone — where the inline list
        is display:none — the navigation landmark contained a logo and a button, and the
        site's actual navigation was not in it. Landmark-first assistive technology found
        nothing to navigate.
      */}
      <nav aria-label="Main">
        <div className="mx-auto flex max-w-6xl items-center gap-4 px-4 py-3 sm:px-6">
          <Link
            href="/#top"
            className="flex items-center gap-2.5 font-display text-xl font-bold text-navy"
          >
            <ShellMark size={36} />
            <span className="leading-none">
              {PRACTICE_NAME.split(" ").slice(0, 2).join(" ")}
              <span className="block text-[0.62rem] font-sans font-semibold uppercase tracking-[0.28em] text-blue-deep">
                Speech
              </span>
            </span>
          </Link>

          <ul className="ml-auto hidden items-center gap-7 md:flex">
            {NAV_ITEMS.map((item) => (
              <li key={item.label}>
                <Link
                  href={item.href}
                  className="text-sm font-medium text-ink transition-colors hover:text-blue-deep"
                >
                  {item.label}
                </Link>
              </li>
            ))}
          </ul>

          <div className="ml-auto flex items-center gap-2 md:ml-0">
            <Link
              href="/login"
              className="hidden rounded-full border border-ice px-4 py-2 text-sm font-medium text-ink-muted transition-colors hover:border-blue hover:text-blue-deep sm:inline-block"
            >
              Login
            </Link>
            <Link
              href="/consultation"
              className="hidden rounded-full bg-blue-action px-5 py-2.5 text-sm font-semibold text-white transition-opacity hover:opacity-90 sm:inline-block"
            >
              Free Consultation
            </Link>

            <button
              ref={menuButtonRef}
              type="button"
              onClick={() => setMenuOpen((open) => !open)}
              aria-expanded={menuOpen}
              aria-controls="mobile-menu"
              className="rounded-lg p-2 text-navy md:hidden"
            >
              <span className="sr-only">{menuOpen ? "Close menu" : "Open menu"}</span>
              <svg width="24" height="24" viewBox="0 0 24 24" fill="none" aria-hidden="true">
                {menuOpen ? (
                  <path
                    d="m6 6 12 12M18 6 6 18"
                    stroke="currentColor"
                    strokeWidth="2"
                    strokeLinecap="round"
                  />
                ) : (
                  <path
                    d="M4 7h16M4 12h16M4 17h16"
                    stroke="currentColor"
                    strokeWidth="2"
                    strokeLinecap="round"
                  />
                )}
              </svg>
            </button>
          </div>
        </div>

        {menuOpen && (
          <div id="mobile-menu" className="border-t border-ice bg-white md:hidden">
            <ul className="mx-auto flex max-w-6xl flex-col px-4 py-2 sm:px-6">
              {NAV_ITEMS.map((item) => (
                <li key={item.label}>
                  <Link
                    href={item.href}
                    onClick={() => setMenuOpen(false)}
                    className="block py-3 text-base font-medium text-ink"
                  >
                    {item.label}
                  </Link>
                </li>
              ))}
              <li className="flex gap-2 py-3">
                <Link
                  href="/consultation"
                  onClick={() => setMenuOpen(false)}
                  className="flex-1 rounded-full bg-blue-action px-5 py-3 text-center text-sm font-semibold text-white"
                >
                  Free Consultation
                </Link>
                <Link
                  href="/login"
                  onClick={() => setMenuOpen(false)}
                  className="rounded-full border border-ice px-5 py-3 text-center text-sm font-medium text-ink-muted"
                >
                  Login
                </Link>
              </li>
            </ul>
          </div>
        )}
      </nav>
    </header>
  );
}
