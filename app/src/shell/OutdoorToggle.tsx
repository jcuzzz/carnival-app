import { useState } from "react";
import { Sun } from "lucide-react";
import { applyTheme, currentTheme } from "./theme";

// In the chrome because borrowed school iPads never get their OS accessibility settings changed.
export default function OutdoorToggle() {
  const [theme, setTheme] = useState(currentTheme);
  const on = theme === "outdoor";

  return (
    <button
      type="button"
      className="chip theme-toggle"
      aria-pressed={on}
      onClick={() => setTheme(applyTheme(on ? "standard" : "outdoor"))}
    >
      <Sun size={20} aria-hidden="true" />
      Outdoor
    </button>
  );
}
