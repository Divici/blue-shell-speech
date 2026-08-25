"use client";

import { useActionState } from "react";
import { Spinner } from "@/components/loading/Spinner";
import { markGoalMet, discontinueGoal } from "./actions";
import { INITIAL_GOAL_TRANSITION_STATE } from "./state";

/**
 * Closing a goal.
 *
 * Two real <form>s posting to server actions, so both work before hydration and without
 * JavaScript — the same reasoning as the day view's start-note button, on a page that is
 * read on a phone with one bar.
 *
 * NEITHER IS A DELETE. Marking met and discontinuing are transitions on the aggregate: the
 * row keeps its text and gains an end date. Nothing on this screen can remove a goal,
 * because there is no endpoint that removes one.
 *
 * The consequence is stated in text next to the buttons rather than behind a confirmation
 * step. The aggregate refuses to reopen a closed goal — "write a new goal instead" — so
 * the clinician needs to know that BEFORE she taps, which a dialog after the fact does not
 * achieve. A disclosure would also hide the two controls this card exists for.
 */
export function CloseGoalActions({
  patientPublicId,
  goalPublicId,
  goalSummary,
}: {
  patientPublicId: string;
  goalPublicId: string;
  /** Enough of the goal to disambiguate one card's buttons from the next card's. */
  goalSummary: string;
}) {
  const [metState, markMetAction, markingMet] = useActionState(
    markGoalMet,
    INITIAL_GOAL_TRANSITION_STATE,
  );
  const [stopState, discontinueAction, discontinuing] = useActionState(
    discontinueGoal,
    INITIAL_GOAL_TRANSITION_STATE,
  );

  const problem =
    metState.status === "error"
      ? metState.message
      : stopState.status === "error"
        ? stopState.message
        : null;

  return (
    <div>
      <div className="flex flex-wrap items-center gap-3">
        <form action={markMetAction}>
          <Identifiers patientPublicId={patientPublicId} goalPublicId={goalPublicId} />
          {/*
            aria-label rather than an appended sr-only span: every card carries the same
            two words, and the name-computation algorithm joins adjacent nodes without a
            separator. The visible text is a prefix of the label, which is what WCAG 2.5.3
            (Label in Name) requires.
          */}
          <button
            type="submit"
            disabled={markingMet || discontinuing}
            aria-label={`Mark met: ${goalSummary}`}
            className="inline-flex items-center gap-2 rounded-full bg-teal px-5 py-2.5 text-sm font-semibold text-white transition-opacity hover:opacity-90 disabled:opacity-70"
          >
            {markingMet && <Spinner size={14} />}
            {markingMet ? "Marking…" : "Mark met"}
          </button>
        </form>

        <form action={discontinueAction}>
          <Identifiers patientPublicId={patientPublicId} goalPublicId={goalPublicId} />
          <button
            type="submit"
            disabled={markingMet || discontinuing}
            aria-label={`Discontinue: ${goalSummary}`}
            className="inline-flex items-center gap-2 rounded-full border border-ice bg-white px-5 py-2.5 text-sm font-semibold text-ink-muted transition-colors hover:border-blue hover:text-blue-deep disabled:opacity-70"
          >
            {discontinuing && <Spinner size={14} />}
            {discontinuing ? "Stopping…" : "Discontinue"}
          </button>
        </form>

        <p className="text-sm text-ink-muted">
          A closed goal stays on the record and cannot be reopened.
        </p>
      </div>

      {problem && (
        <p
          role="alert"
          className="mt-3 rounded-xl border border-coral bg-coral/10 px-3 py-2 text-sm text-navy"
        >
          {problem}
        </p>
      )}
    </div>
  );
}

/**
 * Both identifiers travel with the request.
 *
 * The action never reads a patient or goal id from anywhere but the form, and the API
 * re-checks ownership of both against the session's provider — a goal belonging to someone
 * else answers 404, identically to one that does not exist (D052). Nothing here is an
 * authorization decision; it is just addressing.
 */
function Identifiers({
  patientPublicId,
  goalPublicId,
}: {
  patientPublicId: string;
  goalPublicId: string;
}) {
  return (
    <>
      <input type="hidden" name="patientPublicId" value={patientPublicId} />
      <input type="hidden" name="goalPublicId" value={goalPublicId} />
    </>
  );
}
