/**
 * A stand-in for the .NET API, for the E2E run only.
 *
 * WHY THIS EXISTS. The consultation form no longer confirms anything it has not stored, so
 * the browser flow "fill in → submit → thank you" now depends on a POST reaching an API
 * that answers. The E2E job has no database and no .NET runtime — it deliberately never
 * has, which is why `auth.spec.ts` says in its own header that it does not need the API —
 * and standing a real one up would double the job, duplicate the signal the `api` job
 * already gives, and make a front-end suite fail on a migration.
 *
 * WHAT IT IS NOT. It is not a mock of the API's behaviour and must never grow into one.
 * Everything the API decides — which provider receives the enquiry, what is refused, what
 * is audited, what the notification carries — is asserted against real SQL Server in
 * `Practice.Api.Tests.ConsultationIntakeTests`. Duplicating any of that here would produce
 * two descriptions of one contract, and the wrong one would be the one that stays green.
 *
 * All this answers is: was a request made, and what happens in the browser when the answer
 * is a success or a failure.
 *
 * NO SHARED FLAGS. The suite runs three browser projects in parallel against one instance,
 * so "make the next request fail" would be a race between workers. Failure is triggered by
 * the CONTENT of a request instead — a reserved child name — which is deterministic per
 * request and needs no state. Received submissions are likewise keyed by child name, so
 * each test reads only its own.
 *
 * SYNTHETIC DATA ONLY, held in memory for the life of the run.
 */

import { createServer } from "node:http";
import { randomUUID } from "node:crypto";

// Kept in step with the default in `e2e/consultation-api.ts`.
const PORT = Number(process.env.API_STUB_PORT ?? 3001);

/*
 * The triggers live in api-stub-contract.mjs, which the specs import too.
 *
 * THIS FILE LISTENS ON IMPORT, so a spec that reached in here for a constant would start a
 * second stub inside a Playwright worker — measured: the whole run dies with
 * `EADDRINUSE: 127.0.0.1:3001` before any test executes. Constants with no side effect
 * next to them is the fix; a copy in the spec is not, because a duplicated trigger stops
 * triggering the day one of the two changes and the test then passes against a fast path.
 *
 * WHAT A DELAY IS FOR, AND WHY IT IS NOT A LIE ABOUT THE API. Nothing here decides anything
 * the API decides. It reproduces the one property this deployment has and a developer's
 * laptop does not: this tier scales to zero and its database auto-pauses, so a first
 * request is measured in tens of seconds. Every screen in this product was built against a
 * local database answering in single-digit milliseconds, which is exactly why nothing
 * pended in development and the gap went unseen.
 */
import {
  SLOW_DAY,
  SLOW_LOGIN_EMAIL,
  SLOW_MS,
  UNSTORABLE_CHILD,
} from "./api-stub-contract.mjs";

const held = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/** childFirstName → number of submissions seen. */
const received = new Map();

function json(response, status, body) {
  const payload = JSON.stringify(body);
  response.writeHead(status, {
    "content-type": "application/json",
    "content-length": Buffer.byteLength(payload),
  });
  response.end(payload);
}

async function readJson(request) {
  const chunks = [];
  for await (const chunk of request) chunks.push(chunk);
  try {
    return JSON.parse(Buffer.concat(chunks).toString("utf8"));
  } catch {
    return null;
  }
}

const server = createServer(async (request, response) => {
  const url = new URL(request.url ?? "/", `http://127.0.0.1:${PORT}`);

  if (request.method === "GET" && url.pathname === "/_health") {
    return json(response, 200, { ok: true });
  }

  // How many submissions this stub has seen for one child. The tests use a distinct name
  // each, so this is per-test rather than global.
  if (request.method === "GET" && url.pathname === "/_received") {
    const child = url.searchParams.get("child") ?? "";
    return json(response, 200, { count: received.get(child) ?? 0 });
  }

  /*
   * A day's schedule, always empty.
   *
   * The list itself is asserted against real SQL Server in `Practice.Api.Tests`; what this
   * answers is whether the browser sees a fallback while it waits. An empty day keeps
   * every child out of this file — there is no synthetic caseload here to drift out of
   * step with the seeder's.
   */
  if (request.method === "GET" && url.pathname.startsWith("/appointments/day/")) {
    const date = url.pathname.slice("/appointments/day/".length);
    if (date === SLOW_DAY) await held(SLOW_MS);

    return json(response, 200, { date, visits: [], totalMileage: 0 });
  }

  /*
   * Step one of sign-in, refused.
   *
   * ALWAYS "invalid", never a success: a stubbed success would hand out a session this
   * process has no business issuing, and the reported bug is about what the FORM does
   * while it waits, not about what a correct password leads to.
   */
  if (request.method === "POST" && url.pathname === "/auth/password") {
    const body = await readJson(request);
    if ((body?.email ?? "") === SLOW_LOGIN_EMAIL) await held(SLOW_MS);

    return json(response, 200, { status: "invalid", userId: null, lockoutSeconds: null });
  }

  if (request.method === "POST" && url.pathname === "/consultation-requests") {
    const body = await readJson(request);
    const child = body?.childFirstName ?? "";

    received.set(child, (received.get(child) ?? 0) + 1);

    if (child === UNSTORABLE_CHILD) {
      return json(response, 503, {
        title: "Service Unavailable",
        detail: "The practice cannot accept requests at the moment.",
      });
    }

    return json(response, 201, { publicId: randomUUID() });
  }

  json(response, 404, { title: "Not Found" });
});

// Loopback only. Nothing here should be reachable from anywhere else, even for a few
// seconds on a developer's machine.
server.listen(PORT, "127.0.0.1", () => {
  process.stdout.write(`api-stub listening on http://127.0.0.1:${PORT}\n`);
});
