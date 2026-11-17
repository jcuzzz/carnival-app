import { useEffect, useState } from "react";
import { ApiError } from "../../api/client";
import { DIRECT_WRITE_NETWORK_ERROR_TEXT, errorText } from "../../api/errorText";
import { keys } from "../../api/queryKeys";
import { applyCarnivalTemplate, listEventTemplates } from "../../api/resources";
import type { ApplyTemplateResult, CarnivalType, TemplateSummary } from "../../api/types";
import { useApiQuery, useInvalidate } from "../../api/useApiQuery";
import { counted } from "../events/counted";
import { carnivalSport } from "../events/eventLabels";
import { showToast } from "../../shell/toast";
import { useConnectivity } from "../../shell/useOnline";
import { AgesRequiredNotice } from "./AgesRequiredNotice";

// Idempotent, so there is no armed second tap: what it will do is stated above the button.
export function ApplyTemplateSection({
  carnivalId,
  carnivalType,
  empty,
  canSetAges,
}: {
  carnivalId: string;
  carnivalType: CarnivalType | undefined;
  /** No division here yet: the screen's primary. Otherwise a disclosure. */
  empty: boolean;
  /** SystemAdministrator: a refusal for want of ages may link at School settings. */
  canSetAges: boolean;
}) {
  const [open, setOpen] = useState(false);
  const [template, setTemplate] = useState("");
  const [applying, setApplying] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [agesRequired, setAgesRequired] = useState(false);
  const [outcome, setOutcome] = useState<ApplyTemplateResult | null>(null);
  const invalidate = useInvalidate();
  const online = useConnectivity() === "connected";
  // The outcome keeps it open: the press itself is what turns an empty carnival into a full one.
  const expanded = empty || open || outcome !== null;
  const templates = useApiQuery(keys.eventTemplates(), listEventTemplates, { enabled: expanded });

  const readable = (templates.data ?? []).filter((t) => t.readable);
  // Only the template that MATCHES is chosen for them: a Cross-country carnival
  // pre-armed with the swimming template is one press from a wrong program.
  const preferred = readable.find((t) => matchesType(t, carnivalType))?.id;

  useEffect(() => {
    if (template === "" && preferred !== undefined) setTemplate(preferred);
  }, [preferred, template]);

  async function apply(): Promise<void> {
    if (applying || template === "") return;
    setApplying(true);
    setError(null);
    setAgesRequired(false);
    try {
      const result = await applyCarnivalTemplate(carnivalId, template);
      setOutcome(result);
      showToast(
        `${result.templateName} applied — ${counted(result.eventsCreated, "division")} ` +
          `scheduled, ${counted(result.slotsCreated, "program slot")} built.`,
      );
      // Prefix key: the profile, the events and the program all moved.
      invalidate(
        keys.carnival(carnivalId),
        keys.eventDefinitions(),
        keys.eventDefinitions(true),
      );
    } catch (err) {
      setError(errorText(err, DIRECT_WRITE_NETWORK_ERROR_TEXT));
      setAgesRequired(err instanceof ApiError && err.code === "AGES_REQUIRED");
    } finally {
      setApplying(false);
    }
  }

  function toggle(): void {
    setOpen(!expanded);
    if (expanded) setOutcome(null);
  }

  const sport = carnivalSport(carnivalType).toLowerCase();
  const label =
    sport !== "" && template === preferred
      ? `Add the standard ${sport} events`
      : "Add the standard events";

  return (
    <section className="subsection">
      {empty ? (
        <h2>Start from the standard events</h2>
      ) : (
        <button type="button" aria-expanded={expanded} onClick={toggle}>
          {expanded ? "Hide the standard events" : "Add the rest of the standard events"}
        </button>
      )}
      {expanded && (
        <div className={empty ? undefined : "disclosure-body"}>
          <p className="row-sub">
            {empty
              ? "The fastest way to set a carnival up: one press adds the events and their " +
                "divisions, and builds the program. Pressing it twice adds nothing it already has."
              : "Adds whatever of the standard events this carnival doesn’t have yet. Nothing it " +
                "already has is changed or removed."}
          </p>

          {templates.error && (
            <div className="alert notice" role="status">
              {errorText(
                templates.error,
                "Can’t reach the server, so the templates couldn’t be listed.",
              )}{" "}
              Events can still be added one at a time on this screen.
            </div>
          )}
          {agesRequired ? (
            <AgesRequiredNotice canSetAges={canSetAges} />
          ) : (
            error && (
              <div className="alert error" role="alert">
                {error}
              </div>
            )
          )}

          {templates.loading ? (
            <p>Loading…</p>
          ) : readable.length === 0 ? (
            !templates.error && <p className="row-sub">This server holds no readable template.</p>
          ) : (
            <div className="card">
              <label className="field wide flush-top">
                <span>Carnival template</span>
                <select
                  aria-label="Carnival template"
                  value={template}
                  onChange={(e) => setTemplate(e.target.value)}
                >
                  {template === "" && <option value="">Choose a template…</option>}
                  {readable.map((t) => (
                    <option key={t.id} value={t.id}>
                      {t.name ?? t.id} ({t.definitions} events)
                    </option>
                  ))}
                </select>
              </label>
              <p className="row-sub">
                Its events are created in the shared catalogue or reused where they already exist,
                one division is scheduled for each of this carnival’s ages, categories and tiers,
                and the template’s program is built. Nothing is removed.
              </p>
              <div className="button-row">
                {/* Refused for want of ages, the School settings link is what it steers at. */}
                <button
                  className={empty && !agesRequired ? "primary" : undefined}
                  type="button"
                  disabled={applying || template === "" || !online}
                  onClick={() => void apply()}
                >
                  {applying ? "Adding…" : label}
                </button>
                {!online && (
                  <span>Can’t reach the server, so the standard events can’t be added now.</span>
                )}
              </div>
            </div>
          )}

          {outcome && (
            <div className="alert success" role="status">
              <strong>
                {outcome.templateName} applied — {counted(outcome.eventsCreated, "division")}{" "}
                scheduled.
              </strong>
              <div>
                {counted(outcome.definitionsCreated, "event")} created in the catalogue,{" "}
                {outcome.definitionsExisting} reused
                {outcome.eventsAlreadyScheduled > 0 &&
                  `, ${counted(outcome.eventsAlreadyScheduled, "division")} already scheduled`}
                .
              </div>
              <div>
                {outcome.divisionProfileSeeded
                  ? "This carnival now has its own ages, from the school’s, with the template’s " +
                    "categories and tiers."
                  : "This carnival’s saved ages, categories and tiers were used."}{" "}
                {counted(outcome.slotsCreated, "slot")} across{" "}
                {counted(outcome.streamsCreated, "stream")}.
              </div>
            </div>
          )}
        </div>
      )}
    </section>
  );
}

/** "CrossCountry" is one word on the wire and two in the catalogue's sport. */
function matchesType(template: TemplateSummary, type: CarnivalType | undefined): boolean {
  const sport = letters(carnivalSport(type));
  if (sport === "") return false;
  return letters(template.id) === sport || letters(template.name ?? "").includes(sport);
}

function letters(text: string): string {
  return text.toLowerCase().replace(/[^a-z]/g, "");
}
