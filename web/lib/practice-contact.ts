/**
 * Practice contact details.
 *
 * These are read from environment configuration and never committed. The repository is
 * public (CLAUDE.md non-negotiable #7), and Michelle's real phone, email, and address must
 * not enter the tree in any form — including git history and screenshots.
 *
 * Confirmed values live in docs/SITE_CONTENT.md as PLACEHOLDER until she supplies real ones.
 */

export interface PracticeContact {
  readonly phone: string;
  readonly email: string;
  /** True when the site is showing stand-in details. Never true in production. */
  readonly isPlaceholder: boolean;
}

export interface PracticeContactConfig {
  readonly phone?: string | undefined;
  readonly email?: string | undefined;
}

/**
 * Obvious stand-ins. 555-01xx is reserved for fictional use, so a placeholder that escapes
 * into a screenshot cannot ring a real person.
 */
export const PLACEHOLDER_CONTACT = {
  phone: "410-555-0100",
  email: "hello@blueshellspeech.example",
} as const;

const present = (value: string | undefined): string | undefined => {
  const trimmed = value?.trim();
  return trimmed ? trimmed : undefined;
};

/**
 * Resolves contact details, failing loudly rather than shipping a placeholder.
 *
 * In development a missing value falls back to a stand-in so the site still renders. In
 * production it throws at startup — a build that fails is recoverable, whereas a live page
 * telling a parent to call a fake number is not caught by anything downstream.
 */
export function resolvePracticeContact(
  config: PracticeContactConfig,
  nodeEnv: string = process.env.NODE_ENV ?? "development",
): PracticeContact {
  const phone = present(config.phone);
  const email = present(config.email);

  if (nodeEnv === "production") {
    const missing: string[] = [];
    if (!phone) missing.push("phone (NEXT_PUBLIC_PRACTICE_PHONE)");
    if (!email) missing.push("email (NEXT_PUBLIC_PRACTICE_EMAIL)");

    if (missing.length > 0) {
      throw new Error(
        `Practice contact configuration is incomplete in production: missing ${missing.join(", ")}. ` +
          "Set these in environment config — they must never be committed. See docs/SITE_CONTENT.md.",
      );
    }
  }

  const resolvedPhone = phone ?? PLACEHOLDER_CONTACT.phone;
  const resolvedEmail = email ?? PLACEHOLDER_CONTACT.email;

  return {
    phone: resolvedPhone,
    email: resolvedEmail,
    isPlaceholder:
      resolvedPhone === PLACEHOLDER_CONTACT.phone ||
      resolvedEmail === PLACEHOLDER_CONTACT.email,
  };
}

/** Convenience binding to the app's environment variables. */
export const practiceContact = (): PracticeContact =>
  resolvePracticeContact({
    phone: process.env.NEXT_PUBLIC_PRACTICE_PHONE,
    email: process.env.NEXT_PUBLIC_PRACTICE_EMAIL,
  });
