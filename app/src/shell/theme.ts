const KEY = "carnival.theme";
const ATTR = "data-theme";

export type Theme = "standard" | "outdoor";

// Storage throws outright in some private/managed browser modes, and a theme
// is never worth failing a boot over.
function stored(): Theme | null {
  try {
    const value = localStorage.getItem(KEY);
    return value === "outdoor" || value === "standard" ? value : null;
  } catch {
    return null;
  }
}

export function currentTheme(): Theme {
  return document.documentElement.getAttribute(ATTR) === "outdoor" ? "outdoor" : "standard";
}

export function applyTheme(theme: Theme): Theme {
  const root = document.documentElement;
  if (theme === "outdoor") root.setAttribute(ATTR, "outdoor");
  else root.removeAttribute(ATTR);
  try {
    localStorage.setItem(KEY, theme);
  } catch {
    // Unpersisted is still applied — the choice just does not survive a reload.
  }
  return theme;
}

// "standard" leaves the attribute off so tokens.css can still honour prefers-contrast: more.
export function initTheme(): void {
  if (stored() === "outdoor") document.documentElement.setAttribute(ATTR, "outdoor");
}
