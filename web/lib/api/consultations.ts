import "server-only";

import { apiSignal } from "@/lib/api/timeouts";

/**
 * Public consultation intake, server to server.
 *
 * `server-only`: importing this from a Client Component is a build error, not a runtime
 * leak of the internal API URL.
 *
 * THE ONE CLIENT HERE THAT CARRIES NO SESSION. `patients.ts` and `schedule.ts` forward the
 * provider identity read from the encrypted session cookie; there is no cookie behind this
 * call, because the person filling the form has no account and never will unless their
 * child becomes a patient. It follows that no provider identity is sent at all — the API
 * resolves whose enquiry it is, and a provider id supplied by an anonymous caller would be
 * a visitor choosing whose records to write into.
 */

export interface ConsultationSubmission {
  parentName: string;
  email: string;
  phone: string;
  childFirstName: string;
  childAgeMonths: number;
  concerns: string;
  preferredContact: string;
  /**
   * SHA-256 of the caller's address, computed by this tier because it is the only one that
   * can see a client address at all. It is the SAME value the rate limiter keys on
   * (`lib/rate-limit.ts`) — one derivation, two uses. The raw address never leaves here.
   */
  sourceIpHash: string;
}

/**
 * Stored, or not. There is no third answer.
 *
 * Deliberately not a status code or an error object: every way this can fail means the same
 * thing to the parent waiting — their enquiry is not recorded and they should call instead —
 * and a caller holding a status code will eventually branch on one and tell somebody their
 * request went through when it did not.
 */
export type ConsultationSubmitResult =
  | { stored: true; publicId: string }
  | { stored: false };

function apiBaseUrl(): string {
  const url = process.env.API_BASE_URL;

  if (!url) {
    /*
     * THROWS rather than reporting not-stored.
     *
     * The two are very different failures. Not-stored is correct when the API is down: it
     * is transient, and the parent is told to call. A deployment with no API_BASE_URL is
     * broken for every submission forever, and dressing that up as a transient failure is
     * how it survives to production unnoticed.
     */
    throw new Error(
      "API_BASE_URL is not configured. The BFF cannot record consultation requests, and " +
        "failing here is better than confirming enquiries that are going nowhere.",
    );
  }

  return url.replace(/\/$/, "");
}

export const consultationsApi = {
  async submit(submission: ConsultationSubmission): Promise<ConsultationSubmitResult> {
    const base = apiBaseUrl();

    let response: Response;
    try {
      response = await fetch(`${base}/consultation-requests`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          parentName: submission.parentName,
          email: submission.email,
          phone: submission.phone,
          childFirstName: submission.childFirstName,
          childAgeMonths: submission.childAgeMonths,
          concerns: submission.concerns,
          // The API matches this against the enum BY NAME. See ConsultationEndpoints for
          // why it is not an integer.
          preferredContactMethod: submission.preferredContact,
          sourceIpHash: submission.sourceIpHash,
        }),
        // A parent's enquiry must never sit in a cache, at any layer.
        cache: "no-store",
        /*
         * The shared bound, which used to be 25 seconds local to this file.
         *
         * That number was chosen for a cold start — the API scales to zero, and Container
         * Apps ingress queues the request while a replica wakes rather than refusing it —
         * and it was still too short, because it also had to outlast the API's retry
         * policy against an auto-paused database. Sitting under it turned "the practice
         * had no traffic this morning" into "your enquiry was lost" on a submission the
         * API went on to commit, which is the worst possible answer here: the comment
         * below says there is deliberately no retry precisely because a POST that timed
         * out may have succeeded.
         */
        signal: apiSignal(),
      });
    } catch {
      /*
       * NO RETRY, deliberately.
       *
       * A POST that timed out may well have committed — the API's transaction does not
       * know the caller went away — and there is no idempotency key on this route. Trying
       * again would produce two enquiries for one family, in an inbox one person reads by
       * hand. One attempt, an honest failure, and a phone number on the page.
       */
      report("unreachable");
      return { stored: false };
    }

    if (!response.ok) {
      /*
       * 400 is OUR defect, not the parent's: the form validated this submission and the
       * API refused it, so the two validators disagree. It is reported to the parent
       * identically to a 503 — there is nothing they could do differently — and the status
       * is what tells whoever reads the logs which of the two it was.
       */
      report(String(response.status));
      return { stored: false };
    }

    const body = (await response.json().catch(() => null)) as { publicId?: string } | null;

    if (!body?.publicId) {
      // A 201 with no id is not evidence of storage. Confirming to a parent on the
      // strength of a status code alone is exactly the reassurance this task exists to
      // stop being false.
      report("no-id");
      return { stored: false };
    }

    return { stored: true, publicId: body.publicId };
  },
};

/**
 * A STATUS, and nothing else, ever.
 *
 * A failure that leaves no trace looks exactly like a quiet week, so something has to be
 * written. What must never be written is any part of the submission: it holds a child's
 * first name and a parent's description of their difficulties, and non-negotiable #3 puts
 * that out of reach of every log line in this system. The value here is a status code or
 * one of two fixed words.
 */
function report(outcome: string): void {
  console.error(`Consultation request not stored: ${outcome}`);
}
