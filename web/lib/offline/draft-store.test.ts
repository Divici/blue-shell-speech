import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import "fake-indexeddb/auto";
import { webcrypto } from "node:crypto";

/**
 * The encrypted draft store.
 *
 * This is the one place in the application where PHI is deliberately written to a personal
 * device (DECISIONS.md D005), so the guarantees that make it acceptable are asserted
 * directly rather than described in a comment.
 */

// jsdom has no WebCrypto subtle implementation; Node's is the real thing.
beforeEach(async () => {
  vi.stubGlobal("crypto", webcrypto);

  /*
   * fake-indexeddb keeps one database for the whole file, and vi.resetModules() does not
   * touch it. Without this, drafts written by an earlier test are still present — which
   * showed up as a sweep counting four drafts instead of two, and would have masked a
   * real TTL bug just as easily.
   */
  await new Promise<void>((resolve) => {
    const request = indexedDB.deleteDatabase("blueshell-drafts");
    request.onsuccess = () => resolve();
    request.onerror = () => resolve();
    request.onblocked = () => resolve();
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.resetModules();
});

async function freshStore() {
  vi.resetModules();
  return import("./draft-store");
}

function audio(text: string): ArrayBuffer {
  return new TextEncoder().encode(text).buffer as ArrayBuffer;
}

function readBack(buffer: ArrayBuffer): string {
  return new TextDecoder().decode(buffer);
}

describe("encryption", () => {
  it("round-trips a take", async () => {
    const store = await freshStore();

    const take = await store.encryptTake(audio("clinical audio"), 1, 42, "audio/mp4");
    const plain = await store.decryptTake(take);

    expect(readBack(plain)).toBe("clinical audio");
    expect(take.durationSeconds).toBe(42);
  });

  /** The stored bytes must not contain the recording. */
  it("stores ciphertext, not the recording", async () => {
    const store = await freshStore();

    const take = await store.encryptTake(audio("Maya used two-word combinations"), 1, 10, "audio/mp4");

    expect(readBack(take.ciphertext)).not.toContain("Maya");
    expect(take.ciphertext.byteLength).toBeGreaterThan(0);
  });

  /**
   * A fresh IV per take.
   *
   * Reusing an IV with the same AES-GCM key leaks the XOR of the plaintexts and destroys
   * the authentication guarantee — the single worst mistake available in this design.
   */
  it("uses a distinct IV for every take", async () => {
    const store = await freshStore();

    const first = await store.encryptTake(audio("same"), 1, 10, "audio/mp4");
    const second = await store.encryptTake(audio("same"), 2, 10, "audio/mp4");

    expect(Array.from(first.iv)).not.toEqual(Array.from(second.iv));
    // Identical plaintext must not produce identical ciphertext.
    expect(readBack(first.ciphertext)).not.toBe(readBack(second.ciphertext));
  });

  /**
   * AES-GCM authenticates. A tampered draft must fail loudly rather than decrypting to
   * garbage that could be mistaken for a corrupted recording.
   */
  it("refuses to decrypt tampered ciphertext", async () => {
    const store = await freshStore();

    const take = await store.encryptTake(audio("clinical audio"), 1, 10, "audio/mp4");
    const bytes = new Uint8Array(take.ciphertext);
    // noUncheckedIndexedAccess treats bytes[0] as possibly undefined.
    bytes.set([(bytes[0] ?? 0) ^ 0xff], 0);

    await expect(
      store.decryptTake({ ...take, ciphertext: bytes.buffer as ArrayBuffer }),
    ).rejects.toThrow();
  });

  /**
   * The wrapping key lives only in memory. Losing it makes stored drafts permanently
   * unreadable — bounding how long PHI can sit on a personal device to one session.
   */
  it("cannot decrypt after the key is forgotten", async () => {
    const store = await freshStore();

    const take = await store.encryptTake(audio("clinical audio"), 1, 10, "audio/mp4");
    store.forgetKey();

    await expect(store.decryptTake(take)).rejects.toThrow();
  });
});

describe("persistence", () => {
  it("saves and loads a draft", async () => {
    const store = await freshStore();
    const take = await store.encryptTake(audio("take one"), 1, 30, "audio/mp4");

    await store.saveDraft({
      id: "draft-1",
      appointmentPublicId: "appt-1",
      takes: [take],
      createdAt: Date.now(),
      updatedAt: Date.now(),
    });

    const loaded = await store.loadDraft("draft-1");

    expect(loaded).not.toBeNull();
    expect(loaded!.takes).toHaveLength(1);
    expect(readBack(await store.decryptTake(loaded!.takes[0]!))).toBe("take one");
  });

  it("returns null for a draft that does not exist", async () => {
    const store = await freshStore();
    expect(await store.loadDraft("nope")).toBeNull();
  });

  /** Purged the moment the server acknowledges the upload. */
  it("purges a draft on demand", async () => {
    const store = await freshStore();
    const take = await store.encryptTake(audio("take"), 1, 10, "audio/mp4");

    await store.saveDraft({
      id: "draft-2",
      appointmentPublicId: "appt-1",
      takes: [take],
      createdAt: Date.now(),
      updatedAt: Date.now(),
    });

    await store.purgeDraft("draft-2");

    expect(await store.loadDraft("draft-2")).toBeNull();
  });
});

describe("the 24 hour TTL", () => {
  /**
   * Enforced on READ, not only by the sweep.
   *
   * A device that is never opened again would otherwise keep an expired draft
   * indefinitely — and "the sweep has not run yet" is not a reason to hand back stale PHI.
   */
  it("refuses to return an expired draft even before a sweep", async () => {
    const store = await freshStore();
    const take = await store.encryptTake(audio("stale"), 1, 10, "audio/mp4");

    await store.saveDraft({
      id: "old",
      appointmentPublicId: "appt-1",
      takes: [take],
      createdAt: Date.now() - store.DRAFT_TTL_MS - 1000,
      updatedAt: Date.now(),
    });

    expect(await store.loadDraft("old")).toBeNull();
  });

  it("deletes the expired draft when a read finds it", async () => {
    const store = await freshStore();
    const take = await store.encryptTake(audio("stale"), 1, 10, "audio/mp4");

    await store.saveDraft({
      id: "old",
      appointmentPublicId: "appt-1",
      takes: [take],
      createdAt: Date.now() - store.DRAFT_TTL_MS - 1000,
      updatedAt: Date.now(),
    });

    await store.loadDraft("old");

    // Zero remaining to purge: the read already removed it.
    expect(await store.purgeExpired()).toBe(0);
  });

  it("keeps a draft that is still inside the window", async () => {
    const store = await freshStore();
    const take = await store.encryptTake(audio("fresh"), 1, 10, "audio/mp4");

    await store.saveDraft({
      id: "recent",
      appointmentPublicId: "appt-1",
      takes: [take],
      createdAt: Date.now() - 1000,
      updatedAt: Date.now(),
    });

    expect(await store.loadDraft("recent")).not.toBeNull();
  });

  it("sweeps expired drafts and leaves current ones", async () => {
    const store = await freshStore();
    const take = await store.encryptTake(audio("x"), 1, 10, "audio/mp4");
    const now = Date.now();

    await store.saveDraft({
      id: "stale-1",
      appointmentPublicId: "a",
      takes: [take],
      createdAt: now - store.DRAFT_TTL_MS - 5000,
      updatedAt: now,
    });
    await store.saveDraft({
      id: "stale-2",
      appointmentPublicId: "b",
      takes: [take],
      createdAt: now - store.DRAFT_TTL_MS - 1000,
      updatedAt: now,
    });
    await store.saveDraft({
      id: "current",
      appointmentPublicId: "c",
      takes: [take],
      createdAt: now,
      updatedAt: now,
    });

    expect(await store.purgeExpired(now)).toBe(2);
    expect(await store.loadDraft("current")).not.toBeNull();
  });

  it("lists only unexpired drafts for sync", async () => {
    const store = await freshStore();
    const take = await store.encryptTake(audio("x"), 1, 10, "audio/mp4");
    const now = Date.now();

    await store.saveDraft({
      id: "stale",
      appointmentPublicId: "a",
      takes: [take],
      createdAt: now - store.DRAFT_TTL_MS - 1000,
      updatedAt: now,
    });
    await store.saveDraft({
      id: "pending",
      appointmentPublicId: "b",
      takes: [take],
      createdAt: now,
      updatedAt: now,
    });

    expect(await store.listPendingDrafts(now)).toEqual(["pending"]);
  });
});
