/**
 * Practice contact details.
 *
 * Read from environment configuration and never committed. The repository is public
 * (CLAUDE.md non-negotiable #7): Michelle's real phone, email, and address must not
 * enter the tree in any form, including git history and screenshots.
 *
 * Confirmed values live in docs/SITE_CONTENT.md as PLACEHOLDER until she supplies real
 * ones.
 */

export interface PracticeContact {
  readonly phone: string;
  readonly email: string;
  /** True when the site is showing stand-in details. Never true on the live site. */
  readonly isPlaceholder: boolean;
}

export interface PracticeContactConfig {
  readonly phone?: string | undefined;
  readonly email?: string | undefined;
}

/**
 * Which deployment this is — NOT the same as NODE_ENV.
 *
 * A container built for the dev subscription still runs with NODE_ENV=production, so
 * NODE_ENV cannot distinguish "the demo deployment" from "the site real parents visit".
 * Development and production are separate Azure subscriptions (DECISIONS.md D025), and
 * this flag is how the application knows which one it is running in.
 */
export type SiteEnvironment = "development" | "production";

/**
 * Obvious stand-ins. 555-01xx is reserved for fictional use, so a placeholder that
 * escapes into a screenshot cannot ring a real person.
 */
export const PLACEHOLDER_CONTACT = {
  phone: "410-555-0100",
  email: "hello@blueshellspeech.example",
} as const;

const present = (value: string | undefined): string | undefined => {
  const trimmed = value?.trim();
  return trimmed ? trimmed : undefined;
};

const isPlaceholderValue = (phone: string, email: string): boolean =>
  phone === PLACEHOLDER_CONTACT.phone || email === PLACEHOLDER_CONTACT.email;

/**
 * Resolves contact details, failing loudly rather than shipping a placeholder.
 *
 * In development, missing values fall back to stand-ins so the site still renders. In
 * production it throws — at build time, because the homepage is statically prerendered.
 *
 * Production rejects placeholder values as well as missing ones. Rejecting only
 * *missing* values would be defeated the moment a deploy pipeline supplied placeholders
 * to make the build pass, which is exactly what a deploy pipeline tends to do.
 *
 * A failed build is recoverable. A live page telling a parent to call a fake number is
 * not caught by anything downstream.
 */
export function resolvePracticeContact(
  config: PracticeContactConfig,
  siteEnv: SiteEnvironment = resolveSiteEnvironment(),
): PracticeContact {
  const phone = present(config.phone);
  const email = present(config.email);

  if (siteEnv === "production") {
    const missing: string[] = [];
    if (!phone) missing.push("phone (NEXT_PUBLIC_PRACTICE_PHONE)");
    if (!email) missing.push("email (NEXT_PUBLIC_PRACTICE_EMAIL)");

    if (missing.length > 0) {
      throw new Error(
        `Practice contact configuration is incomplete in production: missing ${missing.join(", ")}. ` +
          "Set these in environment config — they must never be committed. See docs/SITE_CONTENT.md.",
      );
    }

    if (isPlaceholderValue(phone as string, email as string)) {
      throw new Error(
        "Practice contact configuration still holds placeholder values in production. " +
          "A parent must never be shown a fake phone number or email. See docs/SITE_CONTENT.md.",
      );
    }
  }

  const resolvedPhone = phone ?? PLACEHOLDER_CONTACT.phone;
  const resolvedEmail = email ?? PLACEHOLDER_CONTACT.email;

  return {
    phone: resolvedPhone,
    email: resolvedEmail,
    isPlaceholder: isPlaceholderValue(resolvedPhone, resolvedEmail),
  };
}

/** Defaults to the safer interpretation when the flag is absent or unrecognised. */
export function resolveSiteEnvironment(
  raw: string | undefined = process.env.NEXT_PUBLIC_SITE_ENV,
): SiteEnvironment {
  return raw === "production" ? "production" : "development";
}

/** Convenience binding to the app's environment variables. */
export const practiceContact = (): PracticeContact =>
  resolvePracticeContact({
    phone: process.env.NEXT_PUBLIC_PRACTICE_PHONE,
    email: process.env.NEXT_PUBLIC_PRACTICE_EMAIL,
  });
