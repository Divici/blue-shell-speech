/**
 * Encrypted offline storage for dictation drafts.
 *
 * THIS IS A DOCUMENTED DEVIATION (DECISIONS.md D005).
 *
 * blue-shell-frontend-engineering-rules §3 and §11 prohibit PHI in browser storage, and
 * CLAUDE.md non-negotiable #4 repeats it. This exists anyway, for a reason that file's own
 * closing clause permits: a dictation PWA that loses a five-minute clinical recap because
 * a house has poor signal is not usable in the field, and Michelle works in other people's
 * living rooms.
 *
 * WHAT MAKES IT ACCEPTABLE:
 *
 *   AES-GCM 256                 authenticated encryption — tampering is detected, not
 *                               just undecryptable
 *   non-extractable key         the CryptoKey cannot be exported by any script, including
 *                               one injected into this page
 *   wrapping key in memory only never persisted; a closed tab loses the ability to decrypt
 *   purge on server ack         the draft is deleted the moment the server has it
 *   24h hard TTL                enforced on read AND by a sweep, not only at write time
 *
 * `localStorage` and `sessionStorage` remain absolutely prohibited — plaintext, no expiry,
 * and lint-enforced.
 */

const DB_NAME = "blueshell-drafts";
const DB_VERSION = 1;
const STORE = "drafts";

/** 24 hours. A draft older than this is deleted unread. */
export const DRAFT_TTL_MS = 24 * 60 * 60 * 1000;

export interface DictationDraft {
  id: string;
  appointmentPublicId: string;
  /** Audio chunks, encrypted at rest. */
  takes: DraftTake[];
  createdAt: number;
  updatedAt: number;
}

export interface DraftTake {
  sequenceNumber: number;
  durationSeconds: number;
  mimeType: string;
  /** Ciphertext. Never the raw recording. */
  ciphertext: ArrayBuffer;
  /** 96-bit nonce. Typed with its buffer kind so it satisfies BufferSource. */
  iv: Uint8Array<ArrayBuffer>;
}

interface StoredDraft {
  id: string;
  appointmentPublicId: string;
  takes: DraftTake[];
  createdAt: number;
  updatedAt: number;
  expiresAt: number;
}

/**
 * The wrapping key.
 *
 * Generated per page load, held ONLY in this module's closure, and never persisted
 * anywhere. Closing the tab makes every stored draft permanently undecryptable — which is
 * the intended behaviour, not a limitation: it bounds how long PHI can sit on a personal
 * device to a single session, and the drafts are swept on next open regardless.
 *
 * The tradeoff is stated plainly: a draft does NOT survive a browser restart. It survives
 * what it needs to — a dead zone, a locked screen, a backgrounded tab.
 */
let wrappingKey: CryptoKey | null = null;

async function getKey(): Promise<CryptoKey> {
  if (wrappingKey) return wrappingKey;

  wrappingKey = await crypto.subtle.generateKey(
    { name: "AES-GCM", length: 256 },
    // extractable: FALSE. The key cannot be exported by any script, including one
    // injected into this page — so a stolen draft cannot be decrypted off-device.
    false,
    ["encrypt", "decrypt"],
  );

  return wrappingKey;
}

/** Clears the in-memory key. Every stored draft becomes permanently unreadable. */
export function forgetKey(): void {
  wrappingKey = null;
}

function openDb(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(DB_NAME, DB_VERSION);

    request.onupgradeneeded = () => {
      const db = request.result;
      if (!db.objectStoreNames.contains(STORE)) {
        const store = db.createObjectStore(STORE, { keyPath: "id" });
        // Indexed so the TTL sweep is a range scan rather than a full read of every
        // draft — which would mean decrypting audio just to decide it is stale.
        store.createIndex("expiresAt", "expiresAt");
        store.createIndex("appointmentPublicId", "appointmentPublicId");
      }
    };

    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

function tx<T>(
  db: IDBDatabase,
  mode: IDBTransactionMode,
  work: (store: IDBObjectStore) => IDBRequest<T>,
): Promise<T> {
  return new Promise((resolve, reject) => {
    const transaction = db.transaction(STORE, mode);
    const request = work(transaction.objectStore(STORE));
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

export async function encryptTake(
  audio: ArrayBuffer,
  sequenceNumber: number,
  durationSeconds: number,
  mimeType: string,
): Promise<DraftTake> {
  const key = await getKey();

  /*
   * A fresh 96-bit IV per take.
   *
   * Reusing an IV with the same AES-GCM key is catastrophic — it leaks the XOR of the
   * plaintexts and destroys the authentication guarantee. Generated here rather than
   * derived from anything, so no counter can drift or restart.
   */
  const iv = crypto.getRandomValues(new Uint8Array(12));

  const ciphertext = await crypto.subtle.encrypt({ name: "AES-GCM", iv }, key, audio);

  return { sequenceNumber, durationSeconds, mimeType, ciphertext, iv };
}

export async function decryptTake(take: DraftTake): Promise<ArrayBuffer> {
  const key = await getKey();

  // Throws if the ciphertext was altered — AES-GCM authenticates, it does not merely
  // obscure. A tampered draft fails loudly rather than decrypting to garbage.
  return crypto.subtle.decrypt({ name: "AES-GCM", iv: take.iv }, key, take.ciphertext);
}

export async function saveDraft(draft: DictationDraft): Promise<void> {
  const db = await openDb();
  const now = Date.now();

  const stored: StoredDraft = {
    ...draft,
    updatedAt: now,
    expiresAt: draft.createdAt + DRAFT_TTL_MS,
  };

  await tx(db, "readwrite", (store) => store.put(stored));
  db.close();
}

/**
 * Reads a draft, enforcing the TTL on the way out.
 *
 * Checked on READ as well as by the sweep: a device that is never opened again would
 * otherwise keep an expired draft indefinitely, and a sweep that has not run yet is not a
 * reason to return stale PHI.
 */
export async function loadDraft(id: string): Promise<DictationDraft | null> {
  const db = await openDb();
  const stored = await tx<StoredDraft | undefined>(db, "readonly", (store) => store.get(id));

  if (!stored) {
    db.close();
    return null;
  }

  if (stored.expiresAt <= Date.now()) {
    await tx(db, "readwrite", (store) => store.delete(id));
    db.close();
    return null;
  }

  db.close();
  return {
    id: stored.id,
    appointmentPublicId: stored.appointmentPublicId,
    takes: stored.takes,
    createdAt: stored.createdAt,
    updatedAt: stored.updatedAt,
  };
}

/** Deletes a draft. Called the moment the server acknowledges the upload. */
export async function purgeDraft(id: string): Promise<void> {
  const db = await openDb();
  await tx(db, "readwrite", (store) => store.delete(id));
  db.close();
}

/**
 * Deletes every expired draft.
 *
 * Run on startup. The TTL is a promise about how long PHI may sit on a personal device,
 * and a promise that only holds when someone happens to open the right record is not a
 * promise.
 */
export async function purgeExpired(now = Date.now()): Promise<number> {
  const db = await openDb();

  const expired = await new Promise<string[]>((resolve, reject) => {
    const transaction = db.transaction(STORE, "readonly");
    const index = transaction.objectStore(STORE).index("expiresAt");
    const request = index.getAllKeys(IDBKeyRange.upperBound(now));
    request.onsuccess = () => resolve(request.result as string[]);
    request.onerror = () => reject(request.error);
  });

  for (const id of expired) {
    await tx(db, "readwrite", (store) => store.delete(id));
  }

  db.close();
  return expired.length;
}

/** Every non-expired draft id, for sync-on-foreground. */
export async function listPendingDrafts(now = Date.now()): Promise<string[]> {
  const db = await openDb();

  const ids = await new Promise<string[]>((resolve, reject) => {
    const transaction = db.transaction(STORE, "readonly");
    const index = transaction.objectStore(STORE).index("expiresAt");
    const request = index.getAllKeys(IDBKeyRange.lowerBound(now, true));
    request.onsuccess = () => resolve(request.result as string[]);
    request.onerror = () => reject(request.error);
  });

  db.close();
  return ids;
}
