/**
 * Refusals the API makes that are RULES rather than faults.
 *
 * A 409 from this API carries a sentence written for a clinician — "this note is signed,
 * create an amendment instead", "this enquiry has already become a patient" — and the UI
 * surfaces it verbatim. Flattening it into a generic failure would replace an explanation
 * of what to do next with "please try again", which is the one thing that will not help.
 *
 * ONE CLASS, IN A MODULE OF ITS OWN, because two clients now raise it and `instanceof` is
 * how every caller tells a rule from a malfunction. Two separate declarations would be two
 * distinct types, and a `catch` written against one would silently miss the other.
 *
 * Not `server-only`: it is a class with no I/O, and keeping the marker off it means a
 * shared type can be named in a boundary a Client Component reads.
 */
export class ApiConflictError extends Error {}
