import { useEffect, useState } from "react";
import { DIRECT_WRITE_NETWORK_ERROR_TEXT, errorText } from "../../api/errorText";
import { keys } from "../../api/queryKeys";
import { applyCarnivalTemplate, listEventTemplates } from "../../api/resources";
import type { ApplyTemplateResult, CarnivalType, TemplateSummary } from "../../api/types";
import { useApiQuery, useInvalidate } from "../../api/useApiQuery";
import { counted } from "../events/counted";
import { carnivalSport } from "../events/eventLabels";
import { showToast } from "../../shell/toast";

// Idempotent, so there is no armed second tap: what it will do is stated above the button.
export function ApplyTemplateSection({
  carnivalId,
  carnivalType,
  locked,
}: {
  carnivalId: string;
  carnivalType: CarnivalType | undefined;
  /** Closed or finalised: nothing more is scheduled into it. */
  locked: boolean;
}) {
  const templates = useApiQuery(keys.eventTemplates(), listEventTemplates);
  const [template, setTemplate] = useState("");
  const [applying, setApplying] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [outcome, setOutcome] = useState<ApplyTemplateResult | null>(null);
  const invalidate = useInvalidate();

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
    } finally {
      setApplying(false);
    }
  }

  return (
    <section className="subsection">
      <h2>Apply a template</h2>
      <p className="row-sub">
        The fastest way to set a carnival up: one press does Divisions and Events, and builds the
        program. Applying twice adds nothing it already has.
      </p>

      {templates.error && (
        <div className="alert notice" role="status">
          {errorText(
            templates.error,
            "Can’t reach the server, so the templates couldn’t be listed.",
          )}{" "}
          Divisions and Events can still be built by hand.
        </div>
      )}
      {error && (
        <div className="alert error" role="alert">
          {error}
        </div>
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
              disabled={locked}
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
            Its events are created in the shared catalogue or reused where they already exist, this
            carnival’s divisions profile is seeded, one division is scheduled per event × age ×
            category × tier, and the template’s program is built. Nothing is removed.
          </p>
          <div className="button-row">
            <button
              className="primary"
              type="button"
              disabled={applying || locked || template === ""}
              onClick={() => void apply()}
            >
              {applying ? "Applying…" : "Apply template"}
            </button>
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
              ? "The divisions profile was seeded from the template."
              : "The divisions profile was already set, and was left as it is."}{" "}
            {counted(outcome.slotsCreated, "slot")} across{" "}
            {counted(outcome.streamsCreated, "stream")}.
          </div>
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
