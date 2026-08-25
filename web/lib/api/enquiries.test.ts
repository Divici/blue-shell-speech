import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";

vi.mock("server-only", () => ({}));

const session = vi.hoisted(() => ({
  current: { providerPublicId: "8f3c1d2e-0000-4000-8000-0000000000f1" } as
    | { providerPublicId: string }
    | null,
}));

vi.mock("@/lib/auth/session", () => ({
  getSession: async () => session.current,
}));

import { enquiriesApi } from "./enquiries";
import { ApiConflictError } from "./errors";

/**
 * The consultation inbox's API client.
 *
 * The public form's client (`consultations.ts`) carries no session; this one is its
 * counterpart on the other side of the login, and the questions are the tenancy ones: does
 * the provider identity come from the cookie and nowhere else, is a 404 indistinguishable
 * from a refusal, and does a 409 keep the sentence the API wrote for a clinician.
 *
 * SYNTHETIC DATA ONLY.
 */

const ENQUIRY = "8f3c1d2e-0000-4000-8000-0000000000a1";

function respondWith(status: number, body?: unknown): typeof fetch {
  return vi.fn(
    async () =>
      new Response(body === undefined ? null : JSON.stringify(body), {
        status,
        headers: { "content-type": "application/json" },
      }),
  ) as unknown as typeof fetch;
}

function firstCall(): [string, RequestInit & { cache?: string }] {
  const calls = (globalThis.fetch as unknown as ReturnType<typeof vi.fn>).mock.calls;
  expect(calls).toHaveLength(1);
  return calls[0] as [string, RequestInit & { cache?: string }];
}

let originalFetch: typeof fetch;

beforeEach(() => {
  originalFetch = globalThis.fetch;
  process.env.API_BASE_URL = "http://api.test";
  session.current = { providerPublicId: "8f3c1d2e-0000-4000-8000-0000000000f1" };
});

afterEach(() => {
  globalThis.fetch = originalFetch;
  vi.restoreAllMocks();
});

describe("enquiriesApi.list", () => {
  /**
   * The provider identity comes from the encrypted session cookie and from nowhere the
   * browser can reach. Nothing in the inbox passes one in, and there is no parameter it
   * could pass one through.
   *
   * Control: the `"X-Provider-Id": session.providerPublicId` header in enquiries.ts.
   * Deleted → red, "AssertionError: expected null to be
   * '8f3c1d2e-0000-4000-8000-0000000000f1'" — the API then answers 401 to
   * every request, which is the safe direction and still a broken inbox.
   */
  it("forwards the session's provider and never caches", async () => {
    globalThis.fetch = respondWith(200, []);

    await enquiriesApi.list();

    const [url, init] = firstCall();
    expect(url).toBe("http://api.test/consultation-requests");
    expect(new Headers(init.headers).get("X-Provider-Id")).toBe(
      "8f3c1d2e-0000-4000-8000-0000000000f1",
    );
    // A parent's enquiry must never sit in a cache, at any layer.
    expect(init.cache).toBe("no-store");
    expect(init.signal).toBeDefined();
  });

  /**
   * Control: the `status` query parameter in enquiriesApi.list.
   * Deleted → red, "AssertionError: expected 'http://api.test/consultation-requests' to
   * be 'http://api.test/consultation-requests…'" — the inbox's filter would render
   * every enquiry while claiming to show the unanswered ones.
   */
  it("passes a status filter through", async () => {
    globalThis.fetch = respondWith(200, []);

    await enquiriesApi.list("New");

    expect(firstCall()[0]).toBe("http://api.test/consultation-requests?status=New");
  });

  /**
   * Control: the `.then((r) => r ?? [])` on list.
   * Deleted → red, "AssertionError: expected null to deeply equal []" — every consumer
   * would need its own null check, and the page would crash on the one that forgot.
   */
  it("is an empty inbox rather than null when the API answers 404", async () => {
    globalThis.fetch = respondWith(404);

    expect(await enquiriesApi.list()).toEqual([]);
  });
});

describe("enquiriesApi.get", () => {
  /**
   * 404 becomes null rather than an exception, because the API answers 404 identically for
   * an enquiry that does not exist and one belonging to another provider (D052). Turning
   * one of them into an error would rebuild the oracle the API refused to give.
   *
   * Control: the `if (response.status === 404) return null;` in enquiries.ts.
   * Deleted → red, "Error: Enquiries API /consultation-requests/8f3c1d2e-… failed with 404"
   * instead of resolving to null.
   */
  it("answers null for anything the session cannot reach", async () => {
    globalThis.fetch = respondWith(404);

    expect(await enquiriesApi.get(ENQUIRY)).toBeNull();
  });

  it("returns the enquiry the API sent", async () => {
    globalThis.fetch = respondWith(200, { publicId: ENQUIRY, status: "New" });

    expect(await enquiriesApi.get(ENQUIRY)).toMatchObject({ publicId: ENQUIRY });
  });
});

describe("enquiriesApi transitions", () => {
  it.each([
    ["markContacted", "contacted"],
    ["decline", "declined"],
  ] as const)("posts %s to its own route", async (method, segment) => {
    globalThis.fetch = respondWith(200, { publicId: ENQUIRY, status: "Contacted" });

    await enquiriesApi[method](ENQUIRY);

    const [url, init] = firstCall();
    expect(url).toBe(`http://api.test/consultation-requests/${ENQUIRY}/${segment}`);
    expect(init.method).toBe("POST");
  });

  /**
   * A 409 is a RULE, not a malfunction: the enquiry is already closed, and the API's
   * sentence says what to do instead. Flattening it into a generic failure would replace
   * an explanation with "please try again", which is the one thing that will not help.
   *
   * Control: the `if (response.status === 409)` branch in enquiries.ts.
   * Deleted → red, "AssertionError: expected Error: Enquiries API /consultation-reques…
   * to be
   * an instance of ApiConflictError" — it arrives as a bare Error carrying the status code.
   */
  it("surfaces the API's own wording on a refusal", async () => {
    globalThis.fetch = respondWith(409, {
      message: "This enquiry has already become a patient.",
    });

    await expect(enquiriesApi.markContacted(ENQUIRY)).rejects.toBeInstanceOf(
      ApiConflictError,
    );
    await expect(enquiriesApi.decline(ENQUIRY)).rejects.toThrow(/already become a patient/);
  });

  /**
   * Control: the JSON body in enquiriesApi.convert.
   * Deleted → red, "SyntaxError: \"undefined\" is not valid JSON" when the test parses
   * the body — the API would create a patient with no surname, which its aggregate
   * refuses, so the conversion fails with a message written for a developer.
   */
  it("sends the surname and date of birth a conversion needs", async () => {
    globalThis.fetch = respondWith(200, { publicId: ENQUIRY, status: "Converted" });

    await enquiriesApi.convert(ENQUIRY, {
      lastName: "Reyes",
      dateOfBirth: "2024-02-24",
    });

    const [url, init] = firstCall();
    expect(url).toBe(`http://api.test/consultation-requests/${ENQUIRY}/convert`);
    expect(JSON.parse(String(init.body))).toEqual({
      lastName: "Reyes",
      dateOfBirth: "2024-02-24",
    });
  });
});

describe("no session", () => {
  /**
   * Every call refuses before it reaches the network.
   *
   * The route group already redirects an unauthenticated visitor, but hiding a page is not
   * authorization — this is the layer that would otherwise send an unidentified request to
   * an API whose query filter matches nothing, and render an empty inbox as though the
   * practice had no enquiries.
   *
   * Control: the `if (!session) throw` in enquiries.ts.
   * Deleted → red, "AssertionError: promise resolved \"[]\" instead of rejecting" — the fetch
   * goes out with no provider header.
   */
  it("throws rather than asking the API anonymously", async () => {
    session.current = null;
    globalThis.fetch = respondWith(200, []);

    await expect(enquiriesApi.list()).rejects.toThrow(/session/i);
    expect(globalThis.fetch).not.toHaveBeenCalled();
  });
});
