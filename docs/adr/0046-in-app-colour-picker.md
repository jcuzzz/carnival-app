# ADR-0046: In-app colour picker (react-colorful), native input retired

- **Status:** Accepted (2026-09)

## Context

The School settings screen ([ADR-0045](0045-school-settings-in-app.md)) paired a native `<input type="color">` with a hex text field. A report that the Firefox picker did not update the hex field could not be reproduced, because the one thing automation cannot drive is the OS or browser colour dialog itself, which also differs by engine (Firefox fires `change` only on close, and its Windows dialog historically fired no live `input`). A control the platform cannot test end to end and cannot restyle is the wrong place for the only colour entry. Native iOS and Android apps are a stated end goal, so the picker must not deepen dependence on a browser dialog.

## Decision

1. **The native colour input and the standalone hex field are retired** wherever a colour is edited (brand colour, house colours).
2. **react-colorful** supplies the picker: MIT, zero dependencies, about 3 KB gzipped, keyboard and touch accessible, with an in-picker hex input. Pinned to an exact version and loaded only in the lazy school-settings chunk. If abandoned, the replacement surface is one component (`ColourPickerField`).
3. **One control, one shape everywhere:** a swatch trigger button (the current colour, or a "none" glyph, with the value in its accessible name) that expands an **inline panel**, not a modal. The panel holds the saturation and hue areas, a **row of preset common school colours** (tapping one selects it and closes the panel), the hex input (`#` tolerated), and an explicit clear button ("No colour" for a house, the default palette for the brand). Clearing is a button because, with the hex field gone, "empty means none" has no field to be empty in. The panel closes on the trigger, Escape (focus returns to the trigger), clear, a preset, or a click outside.
4. **Validation is unchanged and stays ours.** The picker constrains nothing; the server's rules (ADR-0040's 4.5:1 brand-ink rule, ADR-0044's parse and no-duplicate rules) refuse on save, and live hints render while picking.
5. **Styling by CSS override** of the library's classes, using tokens. The 44px rule applies to the interactive areas (trigger, hue bar, saturation surface), not the thumbs. These overrides are unlayered, scoped under `.colour-panel`, because the library injects an unlayered stylesheet that beats any layered rule; this is the recorded exception in the design system's Layers section. Preset hex values are content data in the component module, not stylesheet literals.

## Consequences

- No engine dialog is involved any more, so the unreproduced Firefox defect is moot rather than diagnosed.
- The picker is real DOM, so the journey tests drive it end to end.
- First third-party UI widget beyond Base UI: one more pinned dependency to watch.
- A wrapped app (WebView, Capacitor) carries it unchanged; a React Native rebuild replaces it like any other DOM component.

## Rejected alternatives

- **Keep the native input and work around engine differences:** untestable where it matters, unstylable, and browser-dialog dependent.
- **A separate hex field beside the picker:** two controls for one value; hex entry lives inside the picker.
- **A modal picker:** an expanding inline control matches the rest of the app and keeps the form in view.
- **Closing on `pointerdown` outside:** closing an inline panel mid-gesture reflows the page and the release lands on the wrong element; outside dismissal fires on the click.
