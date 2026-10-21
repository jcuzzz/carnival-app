import { useState, type CSSProperties, type RefObject } from "react";
import type { MeasureType } from "../api/types";
import {
  measureCaption,
  measureNoun,
  readMeasureField,
  type MeasureFieldState,
  type MeasureVariant,
} from "./markMask";

/**
 * Renders the keystrokes; for a mark, never the metre reading, which would put the next digit
 * after an explicit point. The iOS decimal pad has no colon key, hence positional digits.
 */
export function MeasureInput({
  id,
  measureType,
  raw,
  onRawChange,
  onEnter,
  disabled,
  ariaLabel,
  inputRef,
  variant,
  placeholder,
}: {
  id: string;
  measureType: MeasureType;
  raw: string;
  onRawChange: (next: string) => void;
  onEnter?: (state: MeasureFieldState) => void;
  disabled?: boolean;
  ariaLabel: string;
  inputRef?: RefObject<HTMLInputElement | null>;
  variant?: MeasureVariant;
  placeholder?: string;
}) {
  const state = readMeasureField(measureType, raw);
  const [focused, setFocused] = useState(false);
  const hintId = `${id}-hint`;
  // One caption per deck, not per row: it belongs to the row being worked.
  const showHint = focused || state.mode !== "empty";

  return (
    <span className="time-field">
      <span
        className="time-box"
        style={{ "--ghost-chars": state.ghost.length } as CSSProperties}
      >
        <input
          id={id}
          ref={inputRef}
          className={`time${state.error ? " invalid" : ""}`}
          inputMode={measureType === "Count" ? "numeric" : "decimal"}
          enterKeyHint="done"
          autoComplete="off"
          autoCorrect="off"
          spellCheck={false}
          placeholder={placeholder ?? measureNoun(measureType, variant)}
          aria-label={ariaLabel}
          aria-describedby={hintId}
          aria-invalid={state.error ? true : undefined}
          value={state.input}
          disabled={disabled}
          onFocus={() => setFocused(true)}
          onBlur={() => setFocused(false)}
          onChange={(e) => onRawChange(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              e.preventDefault();
              onEnter?.(state);
            }
          }}
        />
        <span className="time-ghost" aria-hidden="true">
          {state.ghost}
        </span>
      </span>
      {/* Always mounted and reserving its line, so a caption never moves the rows under it. */}
      <span
        id={hintId}
        className={`time-hint${showHint ? " on" : ""}${state.error ? " bad" : ""}`}
        {...(state.error ? { role: "alert" } : {})}
      >
        {measureCaption(measureType, state, variant)}
      </span>
    </span>
  );
}
