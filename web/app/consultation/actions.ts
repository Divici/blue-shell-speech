"use server";

import { headers } from "next/headers";
import type { ConsultationState } from "./state";
import {
  validateConsultation,
  isLikelyBot,
  type ConsultationInput,
} from "@/lib/consultation-schema";
import { RateLimiter, hashClientId } from "@/lib/rate-limit";

/**
 * Module-level so the counter survives between requests within a replica.
 *
 * Five submissions per ten minutes is far above any real parent's need — nobody enquires
 * about their child twice in a minute — and far below what a script needs to be
 * interesting as a cost-amplification vector.
 */
const limiter = new RateLimiter({ limit: 5, windowMs: 10 * 60_000 });

/**
 * Identifies the caller for rate-limiting purposes, hashed.
 *
 * Falls back to a constant when no forwarded address is present, which means every such
 * caller shares one bucket. That is the deliberate direction to fail: behind Container
 * Apps ingress the header is always set, and sharing a bucket over-throttles rather than
 * silently disabling the limit.
 */
async function clientKey(): Promise<string> {
  const headerList = await headers();
  const forwarded = headerList.get("x-forwarded-for")?.split(",")[0]?.trim();
  return hashClientId(forwarded || "unknown-client");
}


/**
 * Handles a consultation request.
 *
 * A Server Action, so validation runs on the server whether or not JavaScript is
 * available. Client-side validation on this form is a convenience; this is the control.
 *
 * NOTHING FROM THIS FORM IS LOGGED. It carries a child's first name and a parent's
 * description of their developmental concerns. Structured logs carry IDs and correlation
 * IDs only (docs/SECURITY.md) — a validation failure is logged as "which fields failed",
 * never as what the parent wrote.
 */
export async function submitConsultation(
  _previous: ConsultationState,
  formData: FormData,
): Promise<ConsultationState> {
  const input: ConsultationInput = {
    parentName: String(formData.get("parentName") ?? ""),
    email: String(formData.get("email") ?? ""),
    phone: String(formData.get("phone") ?? ""),
    childFirstName: String(formData.get("childFirstName") ?? ""),
    childAgeMonths: String(formData.get("childAgeMonths") ?? ""),
    concerns: String(formData.get("concerns") ?? ""),
    preferredContact: String(formData.get("preferredContact") ?? ""),
    website: String(formData.get("website") ?? ""),
  };

  /*
   * Honeypot: report success and store nothing.
   *
   * Telling a bot it was detected teaches whoever wrote it to stop filling the field.
   * A silent accept costs nothing and keeps the trap working.
   */
  if (isLikelyBot(input)) {
    return { status: "success", errors: {} };
  }

  /*
   * Rate limit AFTER the honeypot, so a bot burns no budget that a real parent might
   * need, and BEFORE validation, so a submission loop cannot make us do parsing work.
   */
  const { allowed, retryAfterMs } = limiter.check(await clientKey());
  if (!allowed) {
    const minutes = Math.max(1, Math.ceil(retryAfterMs / 60_000));
    return {
      status: "error",
      errors: {},
      message:
        `You've sent several requests already — please wait about ${minutes} ` +
        `minute${minutes === 1 ? "" : "s"} and try again, or call instead.`,
    };
  }

  const { errors, value } = validateConsultation(input);

  if (Object.keys(errors).length > 0) {
    // Echo the submitted values back so the parent does not lose what they wrote.
    // The honeypot is excluded — repopulating it would defeat the trap on resubmission.
    return {
      status: "error",
      errors,
      values: {
        parentName: value.parentName,
        email: value.email,
        phone: value.phone,
        childFirstName: value.childFirstName,
        // Echo what they typed, not the parsed number: a parse failure yields -1, and
        // showing "-1" back to someone who typed "two" is worse than showing "two".
        childAgeMonths: input.childAgeMonths,
        concerns: value.concerns,
        preferredContact: value.preferredContact,
      },
    };
  }

  /*
   * TODO(slice 3): POST to the .NET API, which persists a ConsultationRequest and sends
   * a CONTENTLESS notification — "New consultation request, sign in to view". Email is
   * not a channel we control, and a child's name plus a list of developmental concerns
   * sitting in a plaintext inbox is a disclosure (docs/DATA_MODEL.md).
   *
   * Until the API exists, the form validates and confirms but does not persist. That is
   * a deliberate, visible gap rather than a silent one: slice 1 ships the public site,
   * and the API arrives with authentication in slice 2.
   */

  return { status: "success", errors: {} };
}
