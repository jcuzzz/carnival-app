// CSS cannot reach a scripted scroll: `behavior: "smooth"` overrides `scroll-behavior` outright.
export function scrollBehavior(): ScrollBehavior {
  return window.matchMedia?.("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth";
}
