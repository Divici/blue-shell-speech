import { describe, it, expect, beforeEach, vi } from "vitest";

/**
 * Session encryption.
 *
 * `next/headers` is mocked because these assert the crypto and the expiry rules, not the
 * cookie plumbing — the plumbing is covered by the E2E sign-in flow.
 */
const store = new Map<string, { value: string; options?: unknown }>();

vi.mock("server-only", () => ({}));
vi.mock("next/headers", () => ({
  cookies: async () => ({
    get: (name: string) => store.get(name),
    set: (name: string, value: string, options?: unknown) =>
      store.set(name, { value, options }),
    delete: (name: string) => store.delete(name),
  }),
}));

const VALID_KEY = "8ZQ8kQ1sQ6xX3vN0pR7tU2wY5zB9dF1hJ4kL6nP8qS0";

describe("session", () => {
  beforeEach(() => {
    store.clear();
    vi.resetModules();
    process.env.SESSION_SECRET = VALID_KEY;
  });

  it("round-trips a session through encryption", async () => {
    const { createSession, getSession } = await import("./session");

    await createSession({
      userId: "user-1",
      providerPublicId: "11111111-1111-1111-1111-111111111111",
      displayName: "Michelle",
    });

    const session = await getSession();
    expect(session?.userId).toBe("user-1");
    expect(session?.displayName).toBe("Michelle");
  });

  /**
   * The cookie must be opaque. A signed-only token would let anyone holding the cookie
   * read the clinician's name straight out of it.
   */
  it("stores the session encrypted, not merely signed", async () => {
    const { createSession, SESSION_COOKIE } = await import("./session");

    await createSession({
      userId: "user-1",
      providerPublicId: "11111111-1111-1111-1111-111111111111",
      displayName: "Michelle",
    });

    const raw = store.get(SESSION_COOKIE)!.value;
    expect(raw).not.toContain("Michelle");
    expect(raw).not.toContain("user-1");
    // JWE compact serialisation has five segments; a signed JWS has three.
    expect(raw.split(".")).toHaveLength(5);
  });

  it("marks the cookie HttpOnly and SameSite=Lax", async () => {
    const { createSession, SESSION_COOKIE } = await import("./session");

    await createSession({
      userId: "user-1",
      providerPublicId: "11111111-1111-1111-1111-111111111111",
      displayName: "Michelle",
    });

    const options = store.get(SESSION_COOKIE)!.options as Record<string, unknown>;
    expect(options.httpOnly).toBe(true);
    expect(options.sameSite).toBe("lax");
  });

  it("returns null for a tampered cookie rather than throwing", async () => {
    const { createSession, getSession, SESSION_COOKIE } = await import("./session");

    await createSession({
      userId: "user-1",
      providerPublicId: "11111111-1111-1111-1111-111111111111",
      displayName: "Michelle",
    });

    const raw = store.get(SESSION_COOKIE)!.value;
    store.set(SESSION_COOKIE, { value: raw.slice(0, -4) + "AAAA" });

    expect(await getSession()).toBeNull();
  });

  /**
   * A missing key must fail loudly. Falling back to a default would produce a session
   * layer that appears to work and protects nothing.
   */
  it("refuses to operate without SESSION_SECRET", async () => {
    delete process.env.SESSION_SECRET;
    const { createSession } = await import("./session");

    await expect(
      createSession({
        userId: "user-1",
        providerPublicId: "11111111-1111-1111-1111-111111111111",
        displayName: "Michelle",
      }),
    ).rejects.toThrow(/SESSION_SECRET/);
  });

  it("rejects a key that is not 32 bytes", async () => {
    process.env.SESSION_SECRET = "dG9vLXNob3J0";
    const { createSession } = await import("./session");

    await expect(
      createSession({
        userId: "user-1",
        providerPublicId: "11111111-1111-1111-1111-111111111111",
        displayName: "Michelle",
      }),
    ).rejects.toThrow(/32 bytes/);
  });

  it("clears both cookies on destroy", async () => {
    const { createSession, createPendingMfa, destroySession, getSession, getPendingMfa } =
      await import("./session");

    await createPendingMfa("user-1");
    await createSession({
      userId: "user-1",
      providerPublicId: "11111111-1111-1111-1111-111111111111",
      displayName: "Michelle",
    });

    await destroySession();

    expect(await getSession()).toBeNull();
    expect(await getPendingMfa()).toBeNull();
  });

  /** Completing MFA must clear the partial-auth cookie, not leave it usable. */
  it("clears the pending-MFA cookie once a session exists", async () => {
    const { createPendingMfa, createSession, getPendingMfa } = await import("./session");

    await createPendingMfa("user-1");
    expect(await getPendingMfa()).not.toBeNull();

    await createSession({
      userId: "user-1",
      providerPublicId: "11111111-1111-1111-1111-111111111111",
      displayName: "Michelle",
    });

    expect(await getPendingMfa()).toBeNull();
  });
});
