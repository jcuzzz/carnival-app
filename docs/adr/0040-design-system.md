# ADR-0040: The design system — tokens, school branding as configuration, one plain type/icon system

- **Status:** Accepted (2026-08). Partly superseded by [ADR-0044](0044-configurable-house-colours.md) (houses may carry a configured colour) and [ADR-0045](0045-school-settings-in-app.md) (the brand colour is edited in the app; configuration seeds the first start only).

## Context

The UI had drifted: a dozen tokens against dozens of hard-coded colour and size literals in one large stylesheet, a `--brand` variable nothing configured, no icon or type system, and nothing an agent adding a screen had to conform to. The platform must also fit any school's look without a code fork, and stay legible on a wet iPad in sunlight. The direction was clear and plain, not decorative, and flexible enough for any school.

## Decision

1. **Design tokens are the single source of visual truth.** `app/src/tokens.json` (W3C DTCG format) generates `app/src/tokens.css`, which is checked in. `styles.css` is layered (`@layer base, components`) and consumes tokens only: **every size and colour literal in the stylesheet is a token**, apart from a short list of keepers (`0`, percentages, grid and flex factors, unit idioms, and `@media` breakpoints, which cannot read `var()`). Off-scale values that existed when the rule was adopted became named legacy tokens, closed to new uses.
2. **The default palette is neutral** (ink, greys and one navy accent), so an unconfigured install still looks finished.
3. **School branding is runtime configuration, never code.** The server serves the school name and an optional brand colour on an anonymous read (`GET /api/v1/branding`). The client sets `--brand` and a computed `--brand-ink` (black or white, whichever contrasts more) on `<html>` at boot and keeps them in IndexedDB, so an offline cold start keeps the brand. A brand colour on which neither black nor white reaches 4.5:1 is refused. The header monogram derives from the school name; there is no logo asset.
4. **One plain typeface, self-hosted:** Inter variable, bundled and precached (no runtime font host), 18px root, tabular numerals for anything numeric that aligns or updates.
5. **Icons are `lucide-react`** and never the sole label, apart from named, recorded exceptions.
6. **An outdoor/high-contrast mode** (`data-theme="outdoor"`, a per-device toggle, also applied under `prefers-contrast: more`) overrides the same tokens at about 7:1. No dark mode.
7. **[`docs/development/design-system.md`](../development/design-system.md) is the constraint layer**: tokens, scales, the component vocabulary and the icon, type and 44px-target rules. Agents read it before any UI change; reviewers flag raw literals like any other defect.

## Consequences

- Every UI change has one file to conform to and a grep that proves it.
- Under WCAG 2.x maths every well-formed sRGB colour reaches at least about 4.58:1 against black or white, so the 4.5:1 brand check in practice refuses only malformed values. Raising the threshold would refuse some real school colours.
- The font adds roughly 40–55 KB gzipped to the bundle.
- Tokenising the existing stylesheet was done value for value, so it moved no pixel; the legacy tokens remain until a restyle retires them.

## Rejected alternatives

- **Tailwind + shadcn/ui re-platform:** rewrites the whole stylesheet inside the pilot runway.
- **An opinionated component kit** (Mantine, MUI, Chakra): a second visual language beside the existing CSS, which is the drift this removes.
- **House colours as a categorical encoding:** colour-vision and greyscale-print hazards, and one more thing every school must configure (revisited, as an optional addition, by ADR-0044).
- **A token build tool** (Style Dictionary, Terrazzo): a short script serves one output; the JSON stays DTCG-shaped so a tool can drop in later.
- **Figma or Penpot as source of truth:** no designer, and a second truth that rots.
