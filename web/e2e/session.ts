import { EncryptJWT, base64url } from "jose";
import { randomBytes } from "node:crypto";
import { readFileSync } from "node:fs";
import path from "node:path";

/**
 * A provider session, minted by the test rather than earned through the login flow.
 *
 * WHY NOT SIGN IN FOR REAL. Signing in needs an Identity store, a PBKDF2 hash, a TOTP
 * secret and a clock — an API with a database, which this job deliberately does not have
 * (`api-stub.mjs` says why at length). What the authenticated E2E tests here assert is
 * about the BROWSER: does a route-level fallback reach it before the data does. Standing
 * up .NET and SQL Server to answer that would duplicate the `api` job's signal and make a
 * front-end suite fail on a migration.
 *
 * The login flow itself is asserted where it is real: `Practice.Api.Tests` for the
 * credential, the lockout and the audit rows, and `auth.spec.ts` here for what an
 * unauthenticated visitor can reach — which is the half that must not depend on a session
 * existing at all.
 *
 * THE COOKIE IS THE ONLY THING BEING FORGED, and only because `web` issues it. It is
 * exactly what `createSession` writes: a JWE, `dir` + A256GCM, under the same key the
 * server is running with. If the key were wrong the cookie would decrypt to nothing, the
 * layout would redirect to `/login`, and the test would fail loudly rather than quietly
 * asserting against the wrong screen.
 *
 * SYNTHETIC IDENTITY ONLY. No name here belongs to anyone.
 */

export const SESSION_COOKIE = "bss_session";

/**
 * The key both sides use, resolved once and shared with the server through the
 * environment.
 *
 * THREE SOURCES, IN THIS ORDER, and no hard-coded fallback — a session-encryption key
 * committed to a public tree is a key somebody eventually deploys:
 *
 *   1. `SESSION_SECRET` already in the environment. This is CI, which generates an
 *      ephemeral key into `GITHUB_ENV` before the E2E step so the Playwright process and
 *      the server it starts share one.
 *   2. `web/.env.local`, which is what a developer's own `next start` would load. Reading
 *      it here means a locally running server and the test agree without anybody
 *      exporting anything.
 *   3. A fresh random key. Playwright passes it to the server it starts and the workers
 *      inherit it, so a machine with neither of the above still runs the suite.
 */
export function sessionSecret(): string {
  const fromEnvironment = process.env.SESSION_SECRET;
  if (fromEnvironment) return fromEnvironment;

  const fromEnvLocal = readFromEnvLocal();
  if (fromEnvLocal) return fromEnvLocal;

  return base64url.encode(randomBytes(32));
}

function readFromEnvLocal(): string | null {
  try {
    const file = readFileSync(path.resolve(__dirname, "..", ".env.local"), "utf8");
    const match = file.match(/^\s*SESSION_SECRET\s*=\s*["']?([^"'\s#]+)/m);
    return match?.[1] ?? null;
  } catch {
    // No .env.local is the normal case in CI, where the key comes from the environment.
    return null;
  }
}

/** Synthetic, and shaped like the real thing: opaque GUIDs, never sequential integers. */
export const TEST_PROVIDER = {
  userId: "11111111-2222-4333-8444-555555555555",
  providerPublicId: "99999999-8888-4777-8666-555555555555",
  displayName: "Test Provider",
} as const;

/**
 * Builds the cookie value.
 *
 * The claims and the expiry mirror `lib/auth/session.ts` exactly: `issuedAt` in seconds
 * because `getSession` enforces a twelve-hour absolute cap against it, and a thirty-minute
 * JWT expiry because that is the sliding window.
 */
export async function mintSessionCookie(secret: string): Promise<string> {
  const key = base64url.decode(secret);

  return new EncryptJWT({ ...TEST_PROVIDER, issuedAt: Math.floor(Date.now() / 1000) })
    .setProtectedHeader({ alg: "dir", enc: "A256GCM" })
    .setIssuedAt()
    .setExpirationTime(new Date(Date.now() + 30 * 60_000))
    .encrypt(key);
}
