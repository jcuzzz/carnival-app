import { useEffect, useRef, useState, type CSSProperties, type KeyboardEvent } from "react";
import { Ban, ChevronDown } from "lucide-react";
import { HexColorInput, HexColorPicker } from "react-colorful";

const ICON = 20;

const NONE = "No colour";

/** Content data, not styling: school colours, not tokens. */
export const PRESET_COLOURS: readonly { name: string; hex: string }[] = [
  { name: "red", hex: "#d32f2f" },
  { name: "orange", hex: "#ef6c00" },
  { name: "yellow", hex: "#fbc02d" },
  { name: "green", hex: "#388e3c" },
  { name: "teal", hex: "#00796b" },
  { name: "blue", hex: "#1976d2" },
  { name: "navy", hex: "#1a237e" },
  { name: "purple", hex: "#7b1fa2" },
  { name: "pink", hex: "#c2185b" },
  { name: "maroon", hex: "#8e1c1c" },
  { name: "brown", hex: "#5d4037" },
  { name: "black", hex: "#212121" },
];

export function ColourPickerField({
  id,
  label,
  caption,
  clearLabel,
  value,
  colour,
  pickerColour,
  describedBy,
  invalid,
  className,
  onChange,
}: {
  /** On the TRIGGER: a server refusal names the field and focus lands here. */
  id: string;
  /** The trigger's accessible name; it has to name its row, not just its column. */
  label: string;
  /** The visible caption where the row already says which house this is. */
  caption?: string;
  clearLabel: string;
  /** The form's raw string — "" is a real value meaning no colour. */
  value: string;
  /** `value` normalised, or null when it is blank or not a colour yet. */
  colour: string | null;
  /** What the picker opens on when there is no colour to open on. */
  pickerColour: string;
  describedBy?: string;
  invalid?: boolean;
  className?: string;
  onChange: (value: string) => void;
}) {
  const [open, setOpen] = useState(false);
  const trigger = useRef<HTMLButtonElement | null>(null);
  const panel = useRef<HTMLDivElement | null>(null);

  function close(): void {
    setOpen(false);
    trigger.current?.focus();
  }

  // Closes on the click, not the pointerdown: the panel is inline, so closing mid-gesture reflows
  // the page under the finger. The pointerdown only records where a gesture began.
  useEffect(() => {
    if (!open) return;
    const mine = (target: EventTarget | null): boolean =>
      target instanceof Node &&
      (panel.current?.contains(target) === true || trigger.current?.contains(target) === true);
    let began: "in" | "out" = "in";
    function down(e: Event): void {
      began = mine(e.target) ? "in" : "out";
    }
    function up(e: Event): void {
      // Not close(): focus belongs to whatever was hit.
      if (began === "out" && !mine(e.target)) setOpen(false);
    }
    document.addEventListener("pointerdown", down, true);
    document.addEventListener("click", up, true);
    return () => {
      document.removeEventListener("pointerdown", down, true);
      document.removeEventListener("click", up, true);
    };
  }, [open]);

  function onKeyDown(e: KeyboardEvent<HTMLDivElement>): void {
    if (e.key !== "Escape") return;
    e.stopPropagation();
    close();
  }

  return (
    <div className={className ? `field ${className}` : "field"}>
      <span>{caption ?? label}</span>
      <button
        type="button"
        id={id}
        className="colour-trigger"
        ref={trigger}
        aria-expanded={open}
        aria-describedby={describedBy}
        aria-invalid={invalid ? true : undefined}
        onClick={() => setOpen(!open)}
      >
        {/* The space is a SIBLING of the span: an accessible name concatenates
            each element's trimmed text, so one inside it would be lost. */}
        <span className="visually-hidden">{label}</span>{" "}
        {colour ? (
          <span
            className="colour-swatch"
            style={{ "--swatch": colour } as CSSProperties}
            aria-hidden="true"
          />
        ) : (
          <Ban size={ICON} aria-hidden="true" />
        )}{" "}
        {value.trim() || NONE}
        <ChevronDown size={ICON} className="colour-chevron" aria-hidden="true" />
      </button>
      {open && (
        <div className="colour-panel" ref={panel} onKeyDown={onKeyDown}>
          <HexColorPicker color={colour ?? pickerColour} onChange={onChange} />
          {/* A preset is a finished choice, so it closes; the bands do not. */}
          <div className="colour-presets">
            {PRESET_COLOURS.map((preset) => (
              <button
                key={preset.hex}
                type="button"
                className="colour-preset"
                aria-label={`Preset ${preset.name} ${preset.hex}`}
                style={{ "--swatch": preset.hex } as CSSProperties}
                onClick={() => {
                  onChange(preset.hex);
                  close();
                }}
              />
            ))}
          </div>
          <HexColorInput
            aria-label={`${label} hex`}
            color={value}
            prefixed
            onChange={onChange}
          />
          <button
            type="button"
            onClick={() => {
              onChange("");
              close();
            }}
          >
            {clearLabel}
          </button>
        </div>
      )}
    </div>
  );
}
