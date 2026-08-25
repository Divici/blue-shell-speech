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

/** A submission naming this child is answered 503, as an unreachable practice would be. */
const UNSTORABLE_CHILD = "Unstorable";

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
