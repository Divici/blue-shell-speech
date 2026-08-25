/**
 * Dictation recording.
 *
 * Wraps MediaRecorder with the rules this product actually needs:
 *
 *   300s hard cap per take    Michelle's real recaps run 2–5 minutes (DECISIONS.md D010).
 *                             The cap sits at the top of that range so it essentially
 *                             never fires — a limit that trips routinely is a design
 *                             flaw; one that catches only a runaway is a safety net.
 *
 *   pause and resume          One button that toggles. She is holding a phone in someone
 *                             else's living room, often with a child still in the room.
 *
 *   auto-stop at the cap      The take ends itself. A take that ends on its own is a take
 *                             nobody reaches for the phone to stop — which matters
 *                             because the workflow assumes no visual interaction once
 *                             recording has begun (presearch §7.7).
 */

/** 5 minutes. Also enforced by a CHECK constraint on DictationTake.DurationSeconds. */
export const MAX_TAKE_SECONDS = 300;

export type RecorderState = "idle" | "recording" | "paused" | "stopped";

export interface RecordedTake {
  blob: Blob;
  mimeType: string;
  durationSeconds: number;
}

/**
 * Picks a container the browser will actually produce.
 *
 * iOS Safari emits mp4/AAC and does NOT support webm — the assumption that
 * `audio/webm;codecs=opus` is universally available is the single most common way a
 * recording feature ships broken on iPhone (docs/ARCHITECTURE.md).
 *
 * Whatever comes out is transcoded server-side to 16 kHz PCM for Azure Speech, so the
 * container choice here is about what the device can do, not what the pipeline wants.
 */
export function pickMimeType(
  isSupported: (type: string) => boolean = (type) =>
    typeof MediaRecorder !== "undefined" && MediaRecorder.isTypeSupported(type),
): string {
  const candidates = [
    "audio/webm;codecs=opus",
    "audio/webm",
    "audio/mp4;codecs=mp4a.40.2",
    "audio/mp4",
    "audio/ogg;codecs=opus",
  ];

  for (const candidate of candidates) {
    if (isSupported(candidate)) return candidate;
  }

  /*
   * Empty string, not a guess.
   *
   * MediaRecorder treats "" as "you choose", which is correct on a browser whose
   * isTypeSupported we could not satisfy. Passing an unsupported type instead throws at
   * construction, turning a recoverable situation into a broken record button.
   */
  return "";
}

export interface RecorderCallbacks {
  onStateChange?: (state: RecorderState) => void;
  /** Fires roughly once a second so the UI can show elapsed time. */
  onTick?: (elapsedSeconds: number) => void;
  /** Fires when a take finishes, including when the cap ends it. */
  onTakeComplete?: (take: RecordedTake) => void;
  onError?: (error: Error) => void;
}

export class DictationRecorder {
  #recorder: MediaRecorder | null = null;
  #stream: MediaStream | null = null;
  #chunks: Blob[] = [];
  #state: RecorderState = "idle";
  #elapsedSeconds = 0;
  #ticker: ReturnType<typeof setInterval> | null = null;

  constructor(private readonly callbacks: RecorderCallbacks = {}) {}

  get state(): RecorderState {
    return this.#state;
  }

  get elapsedSeconds(): number {
    return this.#elapsedSeconds;
  }

  async start(): Promise<void> {
    if (this.#state === "recording") return;

    try {
      this.#stream = await navigator.mediaDevices.getUserMedia({
        audio: {
          // A living room with a child in it. These are the defaults that make speech
          // intelligible rather than the ones that make music sound good.
          echoCancellation: true,
          noiseSuppression: true,
          autoGainControl: true,
        },
      });
    } catch (error) {
      /*
       * A denied microphone is a permission problem, not a bug.
       *
       * Surfaced as a distinct message because the fix is in browser settings and no
       * amount of retrying will help.
       */
      this.callbacks.onError?.(
        new Error(
          "Microphone access was refused. Allow it in your browser settings and try again.",
        ),
      );
      return;
    }

    const mimeType = pickMimeType();
    this.#recorder = new MediaRecorder(
      this.#stream,
      mimeType ? { mimeType } : undefined,
    );

    this.#chunks = [];
    this.#elapsedSeconds = 0;

    this.#recorder.ondataavailable = (event) => {
      if (event.data.size > 0) this.#chunks.push(event.data);
    };

    this.#recorder.onstop = () => {
      const blob = new Blob(this.#chunks, {
        type: this.#recorder?.mimeType || mimeType || "audio/webm",
      });

      this.callbacks.onTakeComplete?.({
        blob,
        mimeType: blob.type,
        durationSeconds: this.#elapsedSeconds,
      });

      this.#teardown();
    };

    // Timeslice so chunks arrive during recording rather than only at stop. If the tab is
    // killed mid-take, what has already arrived is still recoverable.
    this.#recorder.start(1000);
    this.#setState("recording");
    this.#startTicking();
  }

  pause(): void {
    if (this.#state !== "recording" || !this.#recorder) return;

    this.#recorder.pause();
    this.#stopTicking();
    this.#setState("paused");
  }

  resume(): void {
    if (this.#state !== "paused" || !this.#recorder) return;

    this.#recorder.resume();
    this.#startTicking();
    this.#setState("recording");
  }

  stop(): void {
    if (this.#state === "idle" || this.#state === "stopped") return;

    this.#stopTicking();
    this.#recorder?.stop();
    this.#setState("stopped");
  }

  #startTicking(): void {
    this.#stopTicking();

    this.#ticker = setInterval(() => {
      this.#elapsedSeconds += 1;
      this.callbacks.onTick?.(this.#elapsedSeconds);

      // The cap ends the take itself — no tap required.
      if (this.#elapsedSeconds >= MAX_TAKE_SECONDS) this.stop();
    }, 1000);
  }

  #stopTicking(): void {
    if (this.#ticker) {
      clearInterval(this.#ticker);
      this.#ticker = null;
    }
  }

  /**
   * Releases the microphone.
   *
   * Not merely tidy: on a phone, a live MediaStream keeps the recording indicator lit and
   * the microphone held open. Leaving it running after a take would mean an app that
   * appears to be listening in a family's home when it is not.
   */
  #teardown(): void {
    this.#stopTicking();
    this.#stream?.getTracks().forEach((track) => track.stop());
    this.#stream = null;
    this.#recorder = null;
  }

  #setState(state: RecorderState): void {
    this.#state = state;
    this.callbacks.onStateChange?.(state);
  }
}

/** "4:07" — how a person reads elapsed time. */
export function formatElapsed(seconds: number): string {
  const minutes = Math.floor(seconds / 60);
  const remainder = seconds % 60;
  return `${minutes}:${String(remainder).padStart(2, "0")}`;
}

/** Seconds left before the cap ends the take. */
export function secondsRemaining(elapsed: number): number {
  return Math.max(0, MAX_TAKE_SECONDS - elapsed);
}
