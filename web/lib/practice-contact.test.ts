import { describe, it, expect } from "vitest";
import { resolvePracticeContact, PLACEHOLDER_CONTACT } from "./practice-contact";

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
