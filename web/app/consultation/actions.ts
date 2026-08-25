"use server";

import type { ConsultationState } from "./state";
import {
  validateConsultation,
  isLikelyBot,
  type ConsultationInput,
} from "@/lib/consultation-schema";
import { RateLimiter } from "@/lib/rate-limit";
import { clientKey } from "@/lib/client-key";
import { consultationsApi } from "@/lib/api/consultations";
import { practiceContact } from "@/lib/practice-contact";

/**
 * Module-level so the counter survives between requests within a replica.
 *
 * Five submissions per ten minutes is far above any real parent's need — nobody enquires
 * about their child twice in a minute — and far below what a script needs to be
 * interesting as a cost-amplification vector.
 */
const limiter = new RateLimiter({ limit: 5, windowMs: 10 * 60_000 });

/*
 * `clientKey()` used to be declared here.
 *
 * It moved to `lib/client-key.ts` when the API's login rate limiter needed the same value:
 * the browser never reaches `api`, so the caller's real address is observable only at this
 * tier and has to be derived here and forwarded (`lib/auth/api-client.ts`). One derivation,
 * three uses — this limiter, `ConsultationRequest.SourceIpHash`, and the API's partition
 * key. A second one would produce values that correlate with nothing either of the others
 * ever recorded, which is D080's argument for why there is only ever one.
 */

/**
 * Handles a consultation request.
 *
 * A Server Action, so validation runs on the server whether or not JavaScript is
 * available. Client-side validation on this form is a convenience; this is the control.
 *
 * NOTHING FROM THIS FORM IS LOGGED — and there is no logging in this file at all, which is
 * the honest version of that sentence. It carries a child's first name and a parent's
 * description of their developmental concerns. This comment used to go on to describe a
 * validation failure being "logged as which fields failed"; nothing here writes a log line,
 * so that described a mechanism rather than the code (the D072 defect class).
 *
 * One line IS written, and not by this file: `lib/api/consultations.ts` reports a failed
 * submission as a bare status code, because a submission that vanishes with no trace
 * anywhere looks exactly like a quiet week. It carries the status and nothing else —
 * no field names, no values (docs/SECURITY.md, non-negotiable #3).
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
  const sourceIpHash = await clientKey();

  const { allowed, retryAfterMs } = limiter.check(sourceIpHash);
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
   * PERSISTED BEFORE THE PARENT IS THANKED.
   *
   * This is where slice 1's one unmet criterion was: the form validated, confirmed, and
   * stored nothing, which was recorded rather than hidden (docs/SLICE_1_VERIFICATION.md).
   * Closing it changes what the confirmation MEANS — "your request is on its way" is now a
   * claim about a row that exists — so every path that fails to produce one has to stop
   * making it.
   *
   * The API sends the notification, not this tier, and it is CONTENTLESS by construction:
   * IConsultationNotifier has no parameter through which a child's name could travel
   * (CLAUDE.md, docs/DATA_MODEL.md).
   */
  let outcome: Awaited<ReturnType<typeof consultationsApi.submit>>;
  try {
    outcome = await consultationsApi.submit({
      parentName: value.parentName,
      email: value.email,
      phone: value.phone,
      childFirstName: value.childFirstName,
      childAgeMonths: value.childAgeMonths,
      concerns: value.concerns,
      preferredContact: value.preferredContact,
      sourceIpHash,
    });
  } catch {
    /*
     * A missing API_BASE_URL throws from the client, deliberately, and it must not take
     * the page down with it: a broken deployment is still a parent sitting in front of a
     * form. They get the same recoverable error as an API that is merely down, and the
     * throw is what a container log has to show for it.
     */
    outcome = { stored: false };
  }

  if (!outcome.stored) {
    /*
     * NOT a success. A family told "we'll be in touch" about an enquiry that was never
     * recorded does not follow up, and nobody finds out — which is a worse outcome than an
     * error message by a wide margin.
     *
     * The values are echoed back for the same reason a validation failure echoes them: the
     * free-text description of a child is the hardest part of this form to type, and it is
     * the part somebody who has just been told to try again is most likely to abandon over.
     */
    return {
      status: "error",
      errors: {},
      values: {
        parentName: value.parentName,
        email: value.email,
        phone: value.phone,
        childFirstName: value.childFirstName,
        childAgeMonths: input.childAgeMonths,
        concerns: value.concerns,
        preferredContact: value.preferredContact,
      },
      message:
        "We couldn't save your request just now. Please try again in a few minutes — " +
        `or call ${practiceContact().phone} and we'll take the details over the phone.`,
    };
  }

  return { status: "success", errors: {} };
}
