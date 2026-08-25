import type { PatientAddress } from "@/lib/api/patients";
import { ADDRESS_TYPE_LABELS, type AddressCorrectionInput } from "@/lib/address-schema";
import { CorrectAddressForm, RecordAddressForm } from "./AddressForm";

/**
 * Where a child is seen, and where the bill goes.
 *
 * The record is VERSIONED, not overwritten: recording a move closes the current address of
 * that type and keeps the row, because a note describing a visit last spring refers to
 * where the family lived then. So this section shows current addresses and previous ones,
 * and offers the two operations separately —
 *
 * - **Record a new address** for a move. Supersedes; nothing is lost.
 * - **Correct this address** for a typo. Changes one row in place, with no type and no
 *   dates in the form at all, because the family never lived at the mistyped address.
 *
 * One control doing both would be wrong whichever way it guessed: used for a typo it
 * invents a move, used for a move it erases where past visits happened.
 */
export function AddressSection({
  patientPublicId,
  addresses,
  defaultEffectiveFrom,
}: {
  patientPublicId: string;
  addresses: PatientAddress[];
  /** The practice-local date, resolved on the server (D057). */
  defaultEffectiveFrom: string;
}) {
  const current = addresses.filter((a) => a.isCurrent);
  const previous = addresses.filter((a) => !a.isCurrent);

  // Session before Billing: the session address is the one read on the way to a visit.
  const ordered = [...current].sort(
    (a, b) => Number(b.addressType === "Session") - Number(a.addressType === "Session"),
  );

  return (
    <section className="mt-6 rounded-2xl border border-ice bg-white p-6">
      <h2 id="addresses" className="font-display text-xl font-bold text-navy">
        Addresses
      </h2>

      {current.length === 0 ? (
        <p className="mt-4 text-ink-muted">
          No address on file. Add one below so visits have somewhere to happen.
        </p>
      ) : (
        <ul aria-labelledby="addresses" className="mt-5 grid gap-5 lg:grid-cols-2">
          {ordered.map((address) => (
            <li
              key={address.publicId}
              className="rounded-2xl border border-ice bg-white p-5"
            >
              <h3 className="text-sm font-semibold uppercase tracking-wide text-ink-muted">
                {ADDRESS_TYPE_LABELS[address.addressType] ?? address.addressType}
              </h3>

              <address className="mt-2 text-base not-italic leading-relaxed text-ink">
                {address.line1}
                {address.line2 && (
                  <>
                    <br />
                    {address.line2}
                  </>
                )}
                <br />
                {address.city}, {address.state} {address.postalCode}
              </address>

              {address.notes && (
                <p className="mt-2 text-sm text-ink-muted">{address.notes}</p>
              )}

              <p className="mt-2 text-sm text-ink-muted">
                In use since {formatAddressDate(address.effectiveFrom)}
              </p>

              <details className="mt-4 border-t border-ice pt-4">
                <summary className="cursor-pointer text-sm font-semibold text-blue-deep hover:underline">
                  Correct this address
                </summary>

                {/* Keyed on the saved values, for the reason GuardianSection records. */}
                <CorrectAddressForm
                  key={addressKey(address)}
                  patientPublicId={patientPublicId}
                  addressPublicId={address.publicId}
                  defaults={toFormValues(address)}
                  idPrefix={`address-${address.publicId}`}
                />
              </details>
            </li>
          ))}
        </ul>
      )}

      {/*
        PREVIOUS ADDRESSES ARE SHOWN, NOT HIDDEN.

        They are the reason a move supersedes rather than overwrites, and a superbill or a
        note from that period refers to them. Read-only here: correcting a historical
        address is possible through the API, but the case that matters on this page is the
        current one, and an edit form per historical row would bury it.
      */}
      {previous.length > 0 && (
        <div className="mt-8">
          <h3
            id="previous-addresses"
            className="text-sm font-semibold uppercase tracking-wide text-ink-muted"
          >
            Previously
          </h3>
          <ul aria-labelledby="previous-addresses" className="mt-3 space-y-3">
            {previous.map((address) => (
              <li key={address.publicId} className="text-sm leading-relaxed text-ink-muted">
                <span className="text-ink">
                  {address.line1}
                  {address.line2 ? `, ${address.line2}` : ""}, {address.city},{" "}
                  {address.state} {address.postalCode}
                </span>
                {" — "}
                {ADDRESS_TYPE_LABELS[address.addressType] ?? address.addressType},{" "}
                {formatAddressDate(address.effectiveFrom)} to{" "}
                {address.effectiveTo ? formatAddressDate(address.effectiveTo) : "—"}
              </li>
            ))}
          </ul>
        </div>
      )}

      <div className="mt-8 rounded-2xl border border-ice bg-mist p-6">
        <h3 className="font-display text-lg font-bold text-navy">
          Record a new address
        </h3>
        <p className="mt-1 text-sm text-ink-muted">
          Use this when the family has moved.
        </p>
        <RecordAddressForm
          patientPublicId={patientPublicId}
          defaultEffectiveFrom={defaultEffectiveFrom}
          idPrefix="new-address"
        />
      </div>
    </section>
  );
}

/** Everything the correction form renders, so a saved change remounts it. */
function addressKey(address: PatientAddress): string {
  return [
    address.publicId,
    address.line1,
    address.line2 ?? "",
    address.city,
    address.state,
    address.postalCode,
    address.notes ?? "",
  ].join("|");
}

function toFormValues(address: PatientAddress): AddressCorrectionInput {
  return {
    line1: address.line1,
    line2: address.line2 ?? "",
    city: address.city,
    state: address.state,
    postalCode: address.postalCode,
    notes: address.notes ?? "",
  };
}

/**
 * A DateOnly ("2026-06-01") rendered as a person writes it.
 *
 * Pinned to UTC at midday, the same treatment goal dates get. A bare yyyy-mm-dd parsed as
 * an instant is midnight UTC, which is the previous evening anywhere west of Greenwich — so
 * a move-in date would render a day early on Michelle's own phone. The date carries no
 * time, so there is no zone to convert FROM; keeping it out of the arithmetic is the only
 * correct move.
 */
function formatAddressDate(isoDate: string): string {
  return new Intl.DateTimeFormat("en-US", {
    year: "numeric",
    month: "long",
    day: "numeric",
    timeZone: "UTC",
  }).format(new Date(`${isoDate}T12:00:00Z`));
}
