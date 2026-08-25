/**
 * Age, in the unit early intervention uses.
 *
 * Its own module because two screens need it from two different starting points: a patient
 * record holds a date of birth, and a consultation enquiry holds an age in months the
 * parent typed. Rendering them differently — "2y 6m" on one screen and "30 months" on the
 * next — would make the same child look like two different ages to the person deciding
 * whether to take them on.
 *
 * Deliberately not `server-only`: the enquiry detail page's action panel is a Client
 * Component and shows the same string.
 */

/** "2y 6m", or "7m" under a year — how clinicians actually say it. */
export function formatAgeMonths(months: number): string {
  const whole = Math.max(0, Math.floor(months));
  const years = Math.floor(whole / 12);
  const remainder = whole % 12;
  return years === 0 ? `${remainder}m` : `${years}y ${remainder}m`;
}
