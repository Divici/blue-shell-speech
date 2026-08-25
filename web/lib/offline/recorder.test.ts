import { describe, it, expect } from "vitest";
import {
  pickMimeType,
  formatElapsed,
  secondsRemaining,
  MAX_TAKE_SECONDS,
} from "./recorder";

/**
 * Container selection.
 *
 * The iOS case is the one that matters. Assuming webm is universally available is the
 * single most common way a recording feature ships broken on iPhone — and Michelle's
 * phone is an iPhone.
 */
describe("pickMimeType", () => {
  it("prefers webm/opus where it is supported", () => {
    expect(pickMimeType(() => true)).toBe("audio/webm;codecs=opus");
  });

  it("falls back to mp4 on Safari, which does not support webm", () => {
    const safari = (type: string) => type.startsWith("audio/mp4");

    expect(pickMimeType(safari)).toBe("audio/mp4;codecs=mp4a.40.2");
  });

  it("falls back to plain mp4 when the codec string is not recognised", () => {
    const picky = (type: string) => type === "audio/mp4";

    expect(pickMimeType(picky)).toBe("audio/mp4");
  });

  /**
   * An empty string means "browser, you choose" — which is correct. Passing an
   * unsupported type instead throws at MediaRecorder construction, turning a recoverable
   * situation into a record button that does nothing.
   */
  it("returns an empty string rather than guessing when nothing is supported", () => {
    expect(pickMimeType(() => false)).toBe("");
  });
});

describe("formatElapsed", () => {
  it("pads seconds", () => {
    expect(formatElapsed(0)).toBe("0:00");
    expect(formatElapsed(7)).toBe("0:07");
    expect(formatElapsed(67)).toBe("1:07");
  });

  it("handles the cap exactly", () => {
    expect(formatElapsed(MAX_TAKE_SECONDS)).toBe("5:00");
  });
});

describe("secondsRemaining", () => {
  it("counts down to the cap", () => {
    expect(secondsRemaining(0)).toBe(MAX_TAKE_SECONDS);
    expect(secondsRemaining(60)).toBe(240);
  });

  it("never goes negative", () => {
    expect(secondsRemaining(MAX_TAKE_SECONDS + 30)).toBe(0);
  });
});

/**
 * The cap is 300 seconds, and it is load-bearing in three places: here, the
 * DictationTake CHECK constraint, and Michelle's actual working range of 2–5 minutes.
 * A change here without the others is a bug.
 */
describe("the take cap", () => {
  it("is five minutes", () => {
    expect(MAX_TAKE_SECONDS).toBe(300);
  });
});
