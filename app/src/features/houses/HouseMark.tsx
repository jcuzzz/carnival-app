import type { CSSProperties } from "react";
import { houseColourVarOf, houseNameOf, useHouseMarks } from "../../branding/houseMarks";

/** The swatch only adds to the name, so nothing on screen depends on hue. */
export function HouseMark({ code }: { code: string }) {
  const marks = useHouseMarks();
  const colour = houseColourVarOf(marks, code);
  return (
    <span className="house-mark">
      {colour && (
        <span
          className="house-swatch"
          style={{ "--swatch": colour } as CSSProperties}
          aria-hidden="true"
        />
      )}
      {houseNameOf(marks, code)}
    </span>
  );
}
