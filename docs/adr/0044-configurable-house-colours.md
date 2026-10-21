# ADR-0044: Configurable house colours

- **Status:** Accepted (2026-08). Reverses [ADR-0040](0040-design-system.md)'s "no house colours" rule; administered through the app since [ADR-0045](0045-school-settings-in-app.md).

## Context

ADR-0040 made houses text-only everywhere. Schools nevertheless identify strongly with their house colours and want them on the Dashboard. The design-system rules that colour is never the only signal and that nothing is decorative still bind, and state colours (`ok`, `warn`, `bad`, `info`) must keep their meaning on the screens where they carry it.

## Decision

1. **House colour is school configuration, never code, and optional per house.** Each house is `{ code, name, colour? }` in the school settings. A house without a colour renders text-only; an install that configures none looks exactly as before. House colours are runtime values like `--brand`, not design tokens.
2. **Validation:** a colour must parse as `#rgb` or `#rrggbb` and must differ from every other house's colour. **There is no contrast minimum**: schools use yellow and bright green, and a contrast floor refused exactly those. Visibility is handled by rendering instead: every swatch has a hairline border, so a light colour stays visible on white and in print.
3. **Where a person is present, a bad value is refused; at first start it degrades.** The settings screen refuses an invalid colour with the reason (ADR-0045). When the first start seeds houses from configuration (`School:HouseColours`), an unparseable or duplicate colour leaves that house text-only with a logged warning and never stops the API.
4. **The houses endpoint grows additively.** `GET /api/v1/school/houses` keeps `houses` (codes) and adds `houseMarks: [{ code, name, colour }]`. The client mints a `--house-<code>` custom property per house, persists the marks in IndexedDB for offline starts, and renders every house through one `HouseMark` component: **swatch plus name, the name always present**.
5. **Colour appears on the Dashboard only:** the standings bar fill and a swatch beside each house name. The deck, the outbox, Manage, every list of people and the reports stay text-only, because a house there is meta text on a row and colour would compete with state colours. Print omits the swatch and the bars fall back to the brand colour.

The current rule is design-system rule 4 in [`docs/development/design-system.md`](../development/design-system.md).

## Consequences

- One more optional thing a school configures; an unconfigured install is unchanged.
- The "no raw hex outside `tokens.css`" rule is not weakened: house colours are data, not stylesheet literals.
- Colour-vision and greyscale-print hazards return on one screen, mitigated by the always-present name, the swatch border and the screen-only rule.

## Rejected alternatives

- **House colours everywhere:** fights the state-colour semantics on the deck and the outbox.
- **A contrast floor (3:1 against white):** refuses the colours schools actually use; a border solves visibility without refusing anything.
- **An automated distinguishability rule** (a colour-difference threshold): calibrated for surface colours, not screen fills; only exact duplicates are refused.
- **Colours hard-coded per school:** a code fork.
- **Colours generated from a hash of the house code:** unpredictable, and no school would accept the result for its own houses.
- **Mandatory colours:** every school would have to configure them before an install looked finished.
