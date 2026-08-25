import type { Guardian } from "@/lib/api/patients";
import { recordsReleaseState, type GuardianInput } from "@/lib/guardian-schema";
import { GuardianForm } from "./GuardianForm";

/**
 * The adults on a child's record.
 *
 * Two facts are shown about each of them, and they are shown SEPARATELY because they are
 * separate facts: who Michelle calls, and who may receive the file. The second is stated
 * either way — "May receive records" or "No records access" — never left to be inferred
 * from a missing badge, because silence on that question reads as "unknown" and the reader
 * is deciding whether to hand over a child's medical history.
 *
 * Three states are handled explicitly and none of them invents an answer:
 *
 * - **No guardians.** A record nobody has filled in yet.
 * - **Guardians, none authorised.** A real state, not an error — a family whose custody
 *   paperwork has not arrived has nobody entitled to the file yet. Said plainly, because
 *   this is the one that looks like the others until somebody asks for records.
 * - **Someone authorised.** The ordinary case, and it carries no banner.
 */
export function GuardianSection({
  patientPublicId,
  guardians,
}: {
  patientPublicId: string;
  guardians: Guardian[];
}) {
  const release = recordsReleaseState(guardians);

  // The primary contact first — it is who the page is read for between houses. Everyone
  // else follows in the order the record holds them.
  const ordered = [...guardians].sort(
    (a, b) => Number(b.isPrimaryContact) - Number(a.isPrimaryContact),
  );

  return (
    <section className="mt-6 rounded-2xl border border-ice bg-white p-6">
      <h2 id="guardians" className="font-display text-xl font-bold text-navy">
        Guardians
      </h2>

      {release === "none-authorised" && (
        <p
          role="status"
          className="mt-4 rounded-xl border border-coral bg-coral/10 px-4 py-3 text-sm leading-relaxed text-navy"
        >
          <span className="font-semibold">
            No one on this record may receive this child&rsquo;s records.
          </span>{" "}
          Legal authority is recorded per guardian and is not implied by being the primary
          contact. If someone here is entitled to the record, edit them and say so.
        </p>
      )}

      {release === "no-guardians" ? (
        <p className="mt-4 text-ink-muted">
          No guardians recorded yet. Add the first one below.
        </p>
      ) : (
        <ul aria-labelledby="guardians" className="mt-5 grid gap-5 lg:grid-cols-2">
          {ordered.map((guardian) => (
            <li
              key={guardian.publicId}
              className="rounded-2xl border border-ice bg-white p-5"
            >
              <div className="flex flex-wrap items-center gap-2">
                <h3 className="text-lg font-semibold leading-snug text-navy">
                  {guardian.firstName} {guardian.lastName}
                </h3>
                {guardian.isPrimaryContact && (
                  <span className="rounded-full bg-ice px-3 py-1 text-xs font-semibold text-blue-deep">
                    Primary contact
                  </span>
                )}
              </div>

              <p className="mt-1 text-sm text-ink-muted">{guardian.relationship}</p>

              {guardian.phone && (
                <p className="mt-2 text-sm">
                  <a
                    href={`tel:${guardian.phone.replace(/[^0-9+]/g, "")}`}
                    className="text-blue-deep hover:underline"
                  >
                    {guardian.phone}
                  </a>
                </p>
              )}
              {guardian.email && (
                <p className="break-words text-sm">
                  <a
                    href={`mailto:${guardian.email}`}
                    className="text-blue-deep hover:underline"
                  >
                    {guardian.email}
                  </a>
                </p>
              )}
              {!guardian.phone && !guardian.email && (
                <p className="mt-2 text-sm text-ink-muted">No contact details on file.</p>
              )}

              {/*
                STATED IN BOTH DIRECTIONS.

                An absent badge would mean "no authority" and "we have not recorded it"
                equally, and the reader cannot tell which. On the question of who may hold
                a child's medical file, an unlabelled card is worse than a plain "no".
              */}
              <p className="mt-3">
                {guardian.hasLegalAuthority ? (
                  <span className="inline-block rounded-full bg-teal/15 px-2.5 py-1 text-xs font-semibold text-teal">
                    May receive records
                  </span>
                ) : (
                  <span className="inline-block rounded-full bg-sand/40 px-2.5 py-1 text-xs font-semibold text-navy">
                    No records access
                  </span>
                )}
              </p>

              {/*
                The edit form is disclosed rather than always open: a record with three
                guardians would otherwise be three full forms deep before the address
                section. Unlike the close-goal buttons in D063, the controls this card
                exists for — the name, the number, the authority — are all still visible
                with it shut.
              */}
              <details className="mt-4 border-t border-ice pt-4">
                <summary className="cursor-pointer text-sm font-semibold text-blue-deep hover:underline">
                  Edit {guardian.firstName} {guardian.lastName}
                </summary>

                {/*
                  KEYED ON THE SAVED VALUES.

                  React 19 resets an uncontrolled form after an action, and a reset restores
                  defaultValue — but the values that arrive after a save come from a fresh
                  render of this server component. Keying on them remounts the form against
                  what is now on the record, so the fields and the card above can never
                  disagree (the uncontrolled + key-remount pattern D062 records).
                */}
                <GuardianForm
                  key={guardianKey(guardian)}
                  patientPublicId={patientPublicId}
                  guardianPublicId={guardian.publicId}
                  defaults={toFormValues(guardian)}
                  idPrefix={`guardian-${guardian.publicId}`}
                />
              </details>
            </li>
          ))}
        </ul>
      )}

      <div className="mt-8 rounded-2xl border border-ice bg-mist p-6">
        <h3 className="font-display text-lg font-bold text-navy">Add a guardian</h3>
        <GuardianForm patientPublicId={patientPublicId} idPrefix="new-guardian" />
      </div>
    </section>
  );
}

/**
 * Everything the form renders, so a change to any of it remounts the form.
 *
 * The public id alone would not: it is stable across an edit, which is precisely when the
 * defaults need replacing.
 */
function guardianKey(guardian: Guardian): string {
  return [
    guardian.publicId,
    guardian.firstName,
    guardian.lastName,
    guardian.relationship,
    guardian.phone ?? "",
    guardian.email ?? "",
    guardian.isPrimaryContact,
    guardian.hasLegalAuthority,
  ].join("|");
}

/**
 * The stored guardian as the form's fields.
 *
 * `hasLegalAuthority` becomes "yes" or "no" — never the empty string. An existing guardian
 * HAS an answer on record, so the radio arrives selected; only a new one starts with
 * nothing chosen. The empty string means "nobody has answered this yet", and that is not
 * true of a row that is already saved.
 */
function toFormValues(guardian: Guardian): GuardianInput {
  return {
    firstName: guardian.firstName,
    lastName: guardian.lastName,
    relationship: guardian.relationship,
    phone: guardian.phone ?? "",
    email: guardian.email ?? "",
    isPrimaryContact: guardian.isPrimaryContact,
    hasLegalAuthority: guardian.hasLegalAuthority ? "yes" : "no",
  };
}
