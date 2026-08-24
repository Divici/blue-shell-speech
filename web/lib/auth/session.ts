import "server-only";

import { EncryptJWT, jwtDecrypt, base64url } from "jose";
import { cookies } from "next/headers";

/**
 * The provider session.
 *
 * ENCRYPTED, not merely signed. A signed JWT is readable by anyone holding the cookie —
 * and this payload names the clinician. Encrypting it (JWE, A256GCM) means the cookie is
 * opaque even to someone who has stolen it, and it cannot be read by a browser extension
 * or anything else that gets at cookie storage.
 *
 * The cookie is issued by `web` and never leaves it. The API is reached server-to-server
 * and issues nothing to the browser (docs/ARCHITECTURE.md, D003) — so the browser holds
 * exactly one credential, HttpOnly, and no access token ever reaches client JavaScript.
 */

export const SESSION_COOKIE = "bss_session";

/** Set on partial authentication: password accepted, second factor still owed. */
export const MFA_COOKIE = "bss_mfa";

export interface ProviderSession {
  userId: string;
  providerPublicId: string;
  displayName: string;
  /** Seconds since epoch. Absolute expiry, not sliding. */
  issuedAt: number;
}

/** Awaiting a second factor. Carries no authority whatsoever. */
export interface PendingMfa {
  userId: string;
  issuedAt: number;
}

/**
 * Sliding 30 minutes, absolute 12 hours (docs/SECURITY.md).
 *
 * The absolute cap is the one that matters: without it, a session on a device left signed
 * in stays valid indefinitely as long as it is touched occasionally.
 */
const SLIDING_MS = 30 * 60_000;
const ABSOLUTE_MS = 12 * 60 * 60_000;

/** The MFA step is a few minutes' work, not a session. */
const MFA_WINDOW_MS = 10 * 60_000;

function encryptionKey(): Uint8Array {
  const secret = process.env.SESSION_SECRET;

  if (!secret) {
    throw new Error(
      "SESSION_SECRET is not configured. Refusing to start an unauthenticated-by-accident " +
        "session layer — a missing key must fail loudly, never fall back to a default.",
    );
  }

  const key = base64url.decode(secret);
  if (key.length !== 32) {
    throw new Error(
      `SESSION_SECRET must decode to exactly 32 bytes for A256GCM, got ${key.length}. ` +
        "Generate one with: openssl rand -base64 32 | tr '+/' '-_' | tr -d '='",
    );
  }

  return key;
}

async function encrypt(payload: object, maxAgeMs: number): Promise<string> {
  return new EncryptJWT({ ...payload })
    .setProtectedHeader({ alg: "dir", enc: "A256GCM" })
    .setIssuedAt()
    .setExpirationTime(new Date(Date.now() + maxAgeMs))
    .encrypt(encryptionKey());
}

async function decrypt<T>(token: string): Promise<T | null> {
  try {
    const { payload } = await jwtDecrypt(token, encryptionKey());
    return payload as T;
  } catch {
    // Expired, tampered with, or encrypted under a rotated key. All three mean the same
    // thing to a caller — there is no session — and none of them should leak a reason.
    return null;
  }
}

const COOKIE_OPTIONS = {
  httpOnly: true,
  secure: process.env.NODE_ENV === "production",
  sameSite: "lax" as const,
  path: "/",
};

export async function createSession(session: Omit<ProviderSession, "issuedAt">) {
  const issuedAt = Math.floor(Date.now() / 1000);
  const token = await encrypt({ ...session, issuedAt }, SLIDING_MS);

  const store = await cookies();
  store.set(SESSION_COOKIE, token, { ...COOKIE_OPTIONS, maxAge: SLIDING_MS / 1000 });
  store.delete(MFA_COOKIE);
}

/**
 * Reads the session, enforcing the absolute cap.
 *
 * The JWT's own expiry handles the sliding window. The absolute cap is checked here
 * against issuedAt, because refreshing the token would otherwise extend it forever.
 */
export async function getSession(): Promise<ProviderSession | null> {
  const store = await cookies();
  const token = store.get(SESSION_COOKIE)?.value;
  if (!token) return null;

  const session = await decrypt<ProviderSession>(token);
  if (!session) return null;

  const ageMs = Date.now() - session.issuedAt * 1000;
  if (ageMs > ABSOLUTE_MS) return null;

  return session;
}

/** Extends the sliding window without touching issuedAt, so the absolute cap survives. */
export async function refreshSession(session: ProviderSession) {
  const token = await encrypt(session, SLIDING_MS);
  const store = await cookies();
  store.set(SESSION_COOKIE, token, { ...COOKIE_OPTIONS, maxAge: SLIDING_MS / 1000 });
}

export async function destroySession() {
  const store = await cookies();
  store.delete(SESSION_COOKIE);
  store.delete(MFA_COOKIE);
}

export async function createPendingMfa(userId: string) {
  const token = await encrypt(
    { userId, issuedAt: Math.floor(Date.now() / 1000) },
    MFA_WINDOW_MS,
  );

  const store = await cookies();
  store.set(MFA_COOKIE, token, { ...COOKIE_OPTIONS, maxAge: MFA_WINDOW_MS / 1000 });
}

/**
 * The pending-MFA state.
 *
 * Encrypted for the same reason as the session: the raw value is an Identity user id, and
 * handing that to the browser in readable form gives an attacker a valid parameter for
 * the MFA endpoints. The BFF reads the id from this cookie and never from the request
 * body — a client-supplied userId would let anyone attempt codes against any account.
 */
export async function getPendingMfa(): Promise<PendingMfa | null> {
  const store = await cookies();
  const token = store.get(MFA_COOKIE)?.value;
  if (!token) return null;

  return decrypt<PendingMfa>(token);
}
