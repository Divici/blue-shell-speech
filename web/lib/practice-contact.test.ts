import { describe, it, expect } from "vitest";
import {
  resolvePracticeContact,
  resolveSiteEnvironment,
  PLACEHOLDER_CONTACT,
} from "./practice-contact";

/**
 * SITE_CONTENT.md: phone and email are PLACEHOLDER and must come from environment config,
 * never from the tree. CLAUDE.md non-negotiable #7: this repo is public.
 *
 * The risk this guards against is not a missing value in development — it is a placeholder
 * silently shipping to the live site, where a parent trying to reach a speech therapist
 * for their child dials a fake number.
 */
describe("resolvePracticeContact", () => {
  it("uses configured values when both are present", () => {
    const contact = resolvePracticeContact(
      { phone: "410-555-0142", email: "hello@example.com" },
      "production",
    );

    expect(contact.phone).toBe("410-555-0142");
    expect(contact.email).toBe("hello@example.com");
    expect(contact.isPlaceholder).toBe(false);
  });

  it("falls back to placeholders in development so the site still renders", () => {
    const contact = resolvePracticeContact({}, "development");

    expect(contact.phone).toBe(PLACEHOLDER_CONTACT.phone);
    expect(contact.email).toBe(PLACEHOLDER_CONTACT.email);
    expect(contact.isPlaceholder).toBe(true);
  });

  it("throws in production when configuration is missing", () => {
    expect(() => resolvePracticeContact({}, "production")).toThrowError(
      /practice contact/i,
    );
  });

  it("throws in production when only one value is configured", () => {
    expect(() =>
      resolvePracticeContact({ phone: "410-555-0142" }, "production"),
    ).toThrowError(/email/i);
  });

  it("treats blank strings as missing", () => {
    expect(() =>
      resolvePracticeContact({ phone: "   ", email: "hello@example.com" }, "production"),
    ).toThrowError(/phone/i);
  });

  it("trims surrounding whitespace from configured values", () => {
    const contact = resolvePracticeContact(
      { phone: "  410-555-0142  ", email: " hello@example.com " },
      "production",
    );

    expect(contact.phone).toBe("410-555-0142");
    expect(contact.email).toBe("hello@example.com");
  });

  it("never reports placeholder values as real contact details", () => {
    const contact = resolvePracticeContact(
      { phone: PLACEHOLDER_CONTACT.phone, email: PLACEHOLDER_CONTACT.email },
      "development",
    );

    expect(contact.isPlaceholder).toBe(true);
  });
});

/**
 * NEXT_PUBLIC_SITE_ENV, not NODE_ENV.
 *
 * A container built for the dev subscription runs with NODE_ENV=production, so NODE_ENV
 * cannot tell a demo deployment apart from the site real parents visit.
 */
describe("resolveSiteEnvironment", () => {
  it("treats only the explicit string 'production' as production", () => {
    expect(resolveSiteEnvironment("production")).toBe("production");
  });

  it("defaults to development when the flag is absent, empty, or unrecognised", () => {
    expect(resolveSiteEnvironment(undefined)).toBe("development");
    expect(resolveSiteEnvironment("")).toBe("development");
    expect(resolveSiteEnvironment("prod")).toBe("development");
    expect(resolveSiteEnvironment("PRODUCTION")).toBe("development");
  });
});

describe("production rejects placeholders, not just missing values", () => {
  it("throws when the placeholder phone is supplied explicitly", () => {
    expect(() =>
      resolvePracticeContact(
        { phone: PLACEHOLDER_CONTACT.phone, email: "real@example.com" },
        "production",
      ),
    ).toThrowError(/placeholder/i);
  });

  it("throws when the placeholder email is supplied explicitly", () => {
    expect(() =>
      resolvePracticeContact(
        { phone: "410-555-0142", email: PLACEHOLDER_CONTACT.email },
        "production",
      ),
    ).toThrowError(/placeholder/i);
  });

  /**
   * The failure this guards against: a deploy pipeline supplying placeholders to make a
   * failing build pass, which is exactly what a deploy pipeline tends to do.
   */
  it("accepts real values in production", () => {
    const contact = resolvePracticeContact(
      { phone: "410-555-0142", email: "hello@example.com" },
      "production",
    );
    expect(contact.isPlaceholder).toBe(false);
  });
});
