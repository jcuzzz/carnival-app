import { Check } from "lucide-react";
import { href } from "../../router";
import { firstUndoneStep, type SetupStep } from "./setupSteps";

const ICON = 20;

/** A step whose screen is rendering the list keeps its place but drops the link. */
export function SetupChecklist({
  steps,
  currentPath,
}: {
  steps: readonly SetupStep[];
  currentPath?: string;
}) {
  const next = firstUndoneStep(steps);
  return (
    <ol className="row-list card">
      {steps.map((step, index) => {
        const body = (
          <span className="row-main">
            <span className="row-title">
              {index + 1}. {step.title}
              {step.done === true && (
                <>
                  <span className="badge ok">Done</span>
                  <Check size={ICON} className="chip-tick" aria-hidden="true" />
                </>
              )}
              {step.optional && step.done !== true && (
                <span className="badge muted">Optional</span>
              )}
              {step === next && <span className="badge info">Do this next</span>}
            </span>
            <span className="row-sub">{step.detail}</span>
          </span>
        );
        return (
          <li key={step.id} className={step === next ? "awaiting" : undefined}>
            {step.path === null || step.path === currentPath ? (
              <span className="row-link">{body}</span>
            ) : (
              <a className="row-link" href={href(step.path)}>
                {body}
              </a>
            )}
            {step.warning && (
              <div className="alert warn" role="status">
                {step.warning}
              </div>
            )}
          </li>
        );
      })}
    </ol>
  );
}
