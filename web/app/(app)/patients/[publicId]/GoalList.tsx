import type { Goal } from "@/lib/api/notes";
import {
  isAacDomain,
  AAC_MODALITY_LABELS,
  CUE_LEVEL_LABELS,
  GOAL_DOMAIN_LABELS,
  GOAL_STATUS_LABELS,
} from "@/lib/goal-schema";
import { CloseGoalActions } from "./CloseGoalActions";

/**
 * A patient's treatment goals.
 *
 * Split in two: what is being worked on now, and what has been closed. Nothing is ever
 * removed — a met goal is the record of what therapy accomplished, and "what has this
 * child achieved" is the question families and payers ask.
 *
 * These are also what the dictation pipeline classifies against (presearch §5.4), so the
 * page a clinician reads and the list extraction targets are the same list.
 */
export function GoalList({
  patientPublicId,
  goals,
}: {
  patientPublicId: string;
  goals: Goal[];
}) {
  // A paused goal is still on the plan, so it belongs with the current ones. Only Met and
  // Discontinued are closed — the two the aggregate refuses to reopen.
  const current = goals.filter((g) => g.status === "Active" || g.status === "OnHold");
  const closed = goals.filter((g) => g.status === "Met" || g.status === "Discontinued");

  if (goals.length === 0) {
    return (
      <p className="mt-4 text-ink-muted">
        No goals recorded yet. Add the first one below.
      </p>
    );
  }

  return (
    <div className="mt-5 space-y-8">
      {current.length > 0 && (
        <div>
          <h3 id="goals-current" className="text-sm font-semibold uppercase tracking-wide text-ink-muted">
            Current goals
          </h3>
          <ul aria-labelledby="goals-current" className="mt-3 space-y-4">
            {current.map((goal) => (
              <GoalCard key={goal.publicId} goal={goal} patientPublicId={patientPublicId} />
            ))}
          </ul>
        </div>
      )}

      {closed.length > 0 && (
        <div>
          <h3 id="goals-closed" className="text-sm font-semibold uppercase tracking-wide text-ink-muted">
            Closed goals
          </h3>
          <ul aria-labelledby="goals-closed" className="mt-3 space-y-4">
            {closed.map((goal) => (
              <GoalCard key={goal.publicId} goal={goal} patientPublicId={patientPublicId} />
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}

function GoalCard({ goal, patientPublicId }: { goal: Goal; patientPublicId: string }) {
  const isOpen = goal.status === "Active" || goal.status === "OnHold";

  return (
    <li className={`rounded-2xl border border-ice bg-white p-5 ${isOpen ? "" : "opacity-80"}`}>
      <div className="flex flex-wrap items-center gap-2">
        <span className="rounded-full bg-ice px-3 py-1 text-xs font-semibold text-blue-deep">
          {GOAL_DOMAIN_LABELS[goal.domain] ?? goal.domain}
        </span>

        {/* Active is the unremarkable case and carries no badge, the same way a Scheduled
            visit does not on the day view. Every other status is worth calling out. */}
        {goal.status !== "Active" && <StatusBadge status={goal.status} />}
      </div>

      <p className="mt-3 text-lg font-semibold leading-snug text-navy">{goal.goalText}</p>

      {goal.targetCriteria && (
        <p className="mt-2 text-sm leading-relaxed text-ink">
          <span className="font-semibold text-navy">Target: </span>
          {goal.targetCriteria}
        </p>
      )}

      {goal.cueLevelExpected && (
        <p className="mt-1 text-sm leading-relaxed text-ink">
          <span className="font-semibold text-navy">Expected cue level: </span>
          {CUE_LEVEL_LABELS[goal.cueLevelExpected] ?? goal.cueLevelExpected}
        </p>
      )}

      {/*
        AAC details, keyed off the DOMAIN rather than off whether a value is present.

        The aggregate throws on AAC details attached to a non-AAC goal and
        CK_Goals_AacFieldsOnlyOnAacGoals refuses the row, so both columns are null here by
        construction. Rendering "whatever is in the AAC columns" would be agreeing with the
        data instead of with the rule — and would start displaying a modality on an
        articulation goal the day anything managed to write one.
      */}
      {isAacDomain(goal.domain) && (goal.aacModality || goal.aacDeviceNotes) && (
        <div className="mt-3 rounded-xl bg-mist px-4 py-3">
          {goal.aacModality && (
            <p className="text-sm text-ink">
              <span className="font-semibold text-navy">Modality: </span>
              {AAC_MODALITY_LABELS[goal.aacModality] ?? goal.aacModality}
            </p>
          )}
          {goal.aacDeviceNotes && (
            <p className="mt-1 text-sm leading-relaxed text-ink">{goal.aacDeviceNotes}</p>
          )}
        </div>
      )}

      <p className="mt-3 text-sm text-ink-muted">
        Started {formatGoalDate(goal.startDate)}
        {goal.endDate && <> · {closedVerb(goal.status)} {formatGoalDate(goal.endDate)}</>}
      </p>

      {isOpen && (
        <div className="mt-4 border-t border-ice pt-4">
          <CloseGoalActions
            patientPublicId={patientPublicId}
            goalPublicId={goal.publicId}
            goalSummary={summarise(goal.goalText)}
          />
        </div>
      )}
    </li>
  );
}

function StatusBadge({ status }: { status: Goal["status"] }) {
  const styles =
    status === "Met"
      ? "bg-teal/15 text-teal"
      : status === "Discontinued"
        ? "bg-sand/40 text-navy"
        : "bg-ice text-blue-deep";

  return (
    <span className={`rounded-full px-3 py-1 text-xs font-semibold ${styles}`}>
      {GOAL_STATUS_LABELS[status] ?? status}
    </span>
  );
}

/** How the goal ended, in the word the clinician chose when she ended it. */
function closedVerb(status: Goal["status"]): string {
  return status === "Met" ? "met" : status === "Discontinued" ? "discontinued" : "ended";
}

/**
 * A DateOnly ("2026-06-01") rendered as a person writes it.
 *
 * Pinned to UTC at midday. A bare yyyy-mm-dd parsed as an instant is midnight UTC, which
 * is the previous evening anywhere west of Greenwich — so a start date would render a day
 * early on Michelle's own phone. The date carries no time, so there is no zone to convert
 * FROM; the only correct move is to keep it out of the arithmetic entirely.
 */
function formatGoalDate(isoDate: string): string {
  return new Intl.DateTimeFormat("en-US", {
    year: "numeric",
    month: "long",
    day: "numeric",
    timeZone: "UTC",
  }).format(new Date(`${isoDate}T12:00:00Z`));
}

/**
 * Enough of the goal to tell two buttons apart, and no more.
 *
 * The accessible label has to disambiguate a column of identical "Mark met" buttons, but a
 * thousand-character goal read aloud in full is not a label — it is an obstacle. This is
 * also the only part of the goal text that crosses into a client component (D061's rule
 * about not shipping PHI a component does not need, applied at a smaller scale).
 */
function summarise(goalText: string): string {
  const collapsed = goalText.replace(/\s+/g, " ").trim();
  return collapsed.length <= 80 ? collapsed : `${collapsed.slice(0, 80)}…`;
}
