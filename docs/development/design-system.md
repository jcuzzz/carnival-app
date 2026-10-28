# Design system

The constraint layer for every UI change (ADR-0040 §7). Read it before you touch a screen, the way `HANDOFF.md` is read at session start. It is short on purpose.

Source of truth: **`app/src/tokens.json`** (DTCG) → `npm run generate:tokens` → **`app/src/tokens.css`** (checked in). `app/src/styles.css` consumes tokens and nothing else.

## The rules

1. **No raw hex outside `tokens.css`.** `grep -rn '#[0-9a-fA-F]' app/src --include=*.css` must return `tokens.css` and nothing else. (`index.html`'s `theme-color` and the PWA manifest in `vite.config.ts` are the two known exceptions; branding drives both, ADR-0040 §3.)
2. **No raw size or colour literal in `styles.css`; every one is a token** (ADR-0040 amendment 2026-09-28). The keepers: `0`, percentages, `fr`/`minmax`/flex factors/`order`/grid lines, opacity `0`/`1` and the keyframe start `opacity: 0.4`, `var(--x, 0)` fallbacks, unit idioms (`1ch`, `100dvh`, `100cqw`, `-45deg`), the `.visually-hidden` idiom, the `@page` margin, the reduced-motion kill switch and `@media` breakpoints. Use the scales below; a value outside them needs a reason, in the ADR that introduces it.
3. **Never hard-code the navy.** `--brand` / `--brand-ink` belong to the school and are set at runtime; a school's colour is configuration, never code.
4. **House colour is configuration and optional (ADR-0044); it is never the only signal — the house name is always beside it — and never appears where state colours carry meaning: today, the Dashboard only.**
5. **Icons are `lucide-react`, `size={20}`, `aria-hidden="true"`, and never the sole label** — icon beside text, always. An icon-only control does not ship — with TWO named exceptions: the deck row's kebab menu trigger (ADR-0041 §2), whose accessible name carries the swimmer ("Actions for ..."), and the deck header's back arrow (ADR-0042 §1), named "Back to carnival".
6. **Every interactive target is ≥44px high.** Wet fingers at arm's length (ADR-0006). `--size-header-chip`, `--size-outbox-chip` and `--size-dismiss` (32–36px) predate this rule; they are debt, not licence.
7. **`font-variant-numeric: tabular-nums` on anything numeric that aligns down a column or updates in place** — times, counts, ranks, totals, lane numbers. Proportional digits jitter.
8. **Colour is never the only signal.** A state carries its word or a glyph as well (ADR-0029 §6): sunlight and colour-blindness both erase hue.
9. **Nothing decorative.** One shadow (`--shadow-banner-text` is a text-shadow for legibility on the demo stripe, not elevation), one accent, no gradient that is not doing a job, no motion beyond `--motion-toast` and `--motion-flash` — and those are already off under `prefers-reduced-motion`.

## Colour

| Token | Value | Use |
|---|---|---|
| `--ink` | `#1a2330` | Body text, hard rules |
| `--muted` | `#5a6675` | Captions, meta lines, row subtitles |
| `--line` | `#d7dce3` | Borders and dividers |
| `--paper` | `#ffffff` | Card, input, chip and table surface |
| `--wash` | `#f2f4f7` | Page background, pressed-row tint |
| `--wash-faint` | `#fbfcfe` | A deck row still awaiting a result |
| `--placeholder` | `#9aa3b0` | Input placeholder text |
| `--brand` | `#123456` | School accent — **default only**, the server supplies the real one |
| `--brand-ink` | `#ffffff` | Text and rules on the brand fill (computed at runtime) |
| `--status-ink` | `#ffffff` | Text on a filled `ok`/`warn`/`bad`/`info` |
| `--focus-ring` | `#7aa7d9` | The one `:focus-visible` outline |
| `--ok` `--warn` `--bad` `--info` | `#1a7f37` `#9a6700` `#b42318` `#175cd3` | State foregrounds and filled badges |
| `--warn-deep` `--bad-deep` | `#7a5200` `#8a1a10` | The edge of an amber/red fill |
| `--alert-{ok,warn,bad,info}-bg` | `#effaf1` `#fdf7e8` `#fdf1f0` `#eef4fd` | Tinted panel behind a state |
| `--alert-{ok,warn,bad,info}-line` | `#bfe3c6` `#e6cf9a` `#f1c1bc` `#c3d7f4` | That panel's border |
| `--dot-ok` `--dot-pending` `--dot-trouble` | `#7ee2a0` `#ffd66e` `#ff8f80` | The outbox dot, lightened to carry on the brand fill |
| `--line-print` | `#999999` | Table rule in `@media print` |
| `--scroll-shade` `-faint` `-clear`, `--paper-clear` | `rgba()` | A table's scroll-edge fade; fixed, so outdoor does not darken it |
| `--brand-ink-edge` `-edge-strong` `-soft` | `color-mix()` of `--brand-ink` | Chip outlines and secondary text on the brand fill. Resolves at `:root` — `--brand`/`--brand-ink` are set on `<html>` only |

## Scales

| Space | | Radius | | Type (root 18px) | |
|---|---|---|---|---|---|
| `--space-3xs` | 0.15rem | `--radius-xs` | 4px — bars, meters | `--text-2xs` | 0.75rem — badges, eyebrows |
| `--space-2xs` | 0.25rem | `--radius-sm` | 6px — inputs, buttons, alerts | `--text-xs` | 0.8rem |
| `--space-xs` | 0.35rem | `--radius-md` | 8px — cards, toasts, panels | `--text-sm` | 0.85rem — meta lines |
| `--space-sm` | 0.5rem | `--radius-lg` | 10px — tiles | `--text-md` | 0.9rem — tables, chips |
| `--space-md` | 0.75rem | `--radius-pill` | 999px — chips, badges, dots | `--text-base` | 1rem |
| `--space-lg` | 1rem | | | `--text-lg` | 1.05rem — h2, tile names |
| `--space-xl` | 1.5rem | | | `--text-xl` | 1.2rem — standings totals |
| `--space-2xl` | 2rem | | | `--text-2xl` | 1.35rem — h1 |
| | | | | `--text-time` | 1.3rem — the deck's time field |

**Legacy, closed to new uses** (the restyle retires them): the off-scale spacing `--space-4xs` 0.1rem, `-3xs-plus` 0.2rem, `-2xs-plus` 0.3rem, `-xs-plus` 0.4rem, `-sm-plus` 0.6rem, `-md-plus` 0.85rem, `-lg-plus` 1.25rem and the one-off roles `-demo-banner-y`, `-cell-y-wide`, `-result-button-x`, `-badge-hit`, `-caption-drop`, `-demo-reserve`; and the off-scale type sizes `--text-md-plus` 0.95rem, `--text-dismiss` 1.1rem and `--text-badge-glyph` 0.85em (relative to its badge).

Radius also holds `--radius-circle` (50%); space holds `--space-print-gap` (4mm), the program grid's gap on paper.

Sizes: `--size-*` is every pinned MEASUREMENT — targets (`--size-tap` 44px, `--size-tap-tall` 48px), bars, columns, measures — each named for its role, never its number; two equal values with different jobs are two tokens. The oldest: `--size-add-button` (5.5rem), the held width of the row Add buttons so “Adding…” does not resize the row (ADR-0059 §2); `--size-attempt-slot` (3.2rem), the attempt cell's mark and each of its buttons (ADR-0053 §6); `--size-jump-athlete` (7rem), the card grid's sticky athlete column (ADR-0056 §4), narrow enough that a 390px phone shows a whole group of three attempt boxes beside it (amendment 2026-09-25 (b)).

Type: `--font-ui` is `"Inter Variable", system-ui, "Segoe UI", Roboto, sans-serif` — self-hosted, bundled, no runtime font host. `--font-size-root` is 18px; everything else is `rem`, so the app scales from that one number. Print sets `--font-size-root-print` (10pt), so rem is not px on paper: never convert between them. Weights are `--font-weight-{regular,medium,semibold,bold}`, line heights `--font-leading-{none,tight}`, letter-spacing `--font-tracking-{tight,caps,wide}` — em on purpose, so never `@property`-register the tracking tokens. Elevation: `--shadow-menu` is the **only** shadow (toasts, menus, popovers). Nothing else lifts off the page.

- `--border-*`: widths — `hairline` 1px (every border), `heavy` 2px, `indicator` 3px (focus outline, current tab), `flag` 4px, `offset` 1px (`outline-offset`).
- `--z-*`: stacking, low to high — `sticky-cell` 1, `header` 10, `float` 30 (demo banner, menus, popovers), `toast` 40, `idle` 100.
- `--motion-*`: `toast` 140ms, `flash` 160ms. Nothing else moves.
- `--opacity-*`: `disabled` 0.5, `voided` 0.6, `quiet` 0.75.
- Breakpoints (`nav-compact` 560px, `phone` 600px, `below-tablet` 767px, `tablet` 768px) are written literally in `@media`, which cannot read `var()`; `tokens.json` holds them as a comment-only group.

## Outdoor / high contrast

`data-theme="outdoor"` on `<html>` overrides `--ink`, `--muted`, `--line`, `--wash` and `--placeholder` at ~7:1. `@media (prefers-contrast: more)` applies the same overrides automatically. The toggle lives in the shell header (`shell/OutdoorToggle.tsx`), is persisted per device in `localStorage["carnival.theme"]`, and is applied before first paint from `main.tsx`. There is no dark mode — on a pool deck it is worse, and nothing here wants it.

To add an outdoor value, put it on the token in `tokens.json`:

```json
"muted": { "$value": "#5a6675", "$extensions": { "carnival.outdoor": "#3d4654" } }
```

then `npm run generate:tokens` and commit both files.

## Component vocabulary

Use what exists before inventing. All of it is in `app/src/styles.css`, under `@layer components`.

| Class | What it is |
|---|---|
| `.card` | The default container: paper, `--radius-md`, one border. Group related rows or fields |
| `.tile` | A whole-tile tap target in `.tile-grid` (carnival landing). `.tile-head` / `.tile-progress` / `.tile-bar` / `.tile-count` / `.tile-status` |
| `.chip` | A pill-shaped span or button — the shell's outdoor toggle. `.chip-count` = a trailing `0/3`, also used inside a picker; `.chip-tick` = the completion tick beside it |
| `.picker-trigger` / `.picker-menu` / `.picker-item` | One axis of the deck header (ADR-0042 §2): a closed button reading the current value, over one Base UI menu of link items. `.picker-item.current` = where you already are; `.picker-trigger.static` = an axis with a single value, so nothing to open |
| `.badge` | A non-interactive state pill, outlined in `currentColor`: `.ok` `.warn` `.bad` `.info` `.muted`, plus `.first` (filled — the heat winner) |
| `.sync-badge` | The *filled* lifecycle pill on the deck: `.queued` `.syncing` `.accepted` `.conflict` `.rejected` `.cancelled` `.discarded`. Always carries a glyph and a word |
| `.alert` | A tinted block message: `.error` `.notice` `.warn` `.success`. Inline, never a modal |
| `.button-row` | The wrapping row that holds buttons. `button.primary` (one per screen), `button.danger`, `button.armed` (a two-tap destructive confirm). A navigation escape in the row is `a.button`, `a.button.primary-link` for the one being steered at |
| `.field` / `.form-grid` / `.check-row` | A labelled control; the auto-fitting grid of them; a checkbox and its text as one 44px target |
| `.brand-preview` / `.colour-trigger` / `.colour-swatch` / `.colour-chevron` / `.colour-panel` / `.colour-presets` / `.colour-preset` | School settings only (ADR-0045/0046): the 44px band painted with the candidate brand colour through inline `--band` / `--band-ink`; the swatch trigger button (current colour or a none glyph + hex text) that expands the react-colorful panel — picker, preset row (tap = select + close; colours arrive via inline `--swatch`), in-panel hex entry, explicit clear |
| `.table-wrap` | Every `<table>` goes inside one — it is what scrolls on a phone and what un-clips for print |
| `.row-list` / `.row-link` / `.row-main` / `.row-title` / `.row-sub` | The list-of-people idiom: a name over its meta line, the whole row tappable |
| `.row-status` / `.row-state` / `.row-action` | The deck row's right-hand cluster. **Width-reserved**: a state change swaps words, never geometry |
| `.role-status` / `.row-armed.role-revoke` | The Roles page, `/school/roles` (ADR-0048, moved by ADR-0055 §1): the state badge + revoke trigger beside a person, dropping to its own line under 600px; the revoke strip whose sentence owns a full line above its reason field |
| `.house-mark` / `.house-swatch` | A house, named. The swatch appears only where the school configured a colour (ADR-0044); the name is always rendered, and print drops the swatch |
| `.visually-hidden` / `.mono` / `.no-print` | Utilities. `.mono` is tabular numerals on something that has no class of its own |

### State variants — when each

- `ok` — the thing is done, verified or a record stands.
- `info` — in flight, or a live action the official can still take (Undo).
- `warn` — needs attention but nothing is lost: queued, stale, outside the plausible range, parked.
- `bad` — refused, conflicted, void or destructive.
- `muted` — finished with and nothing to do; never competes with a live problem.

## Layers

`@layer base, components;` — declared in `tokens.css` (which owns `:root`) and repeated at the head of `styles.css`. Element defaults, the reset and `::placeholder` live in `base`; **every class lives in `components`**. A component rule therefore never has to out-specify a reset, and `!important` is not an answer to anything here. One recorded exception (ADR-0046): the `.colour-panel .react-colorful__*` overrides are UNLAYERED, because react-colorful injects an unlayered stylesheet at runtime and unlayered CSS beats any layered rule — a layered override cannot win. Scope any such exception under one component class and say why in place.

## Before you commit a UI change

1. `grep -rn '#[0-9a-fA-F]' app/src --include=*.css` → `tokens.css` only. Raw sizes, colours, durations, weights, line heights, z-indexes and opacities (comments stripped, keepers filtered) → nothing:
   ```sh
   perl -0777 -pe 's/\r//g; s{/\*.*?\*/}{$&=~s/[^\n]//gr}gse' app/src/styles.css \
     | grep -nE '[0-9](px|rem|em|mm|pt|vw|m?s)\b|rgba?\(|color-mix|#[0-9a-fA-F]{3}|(font-weight|z-index|line-height|opacity): *[0-9.]' \
     | grep -vE '@media|!important|opacity: (0|1|0\.4);$|^[0-9]+: *(width|height): 1px;$|^[0-9]+: *margin: 12mm;$'
   ```
2. `npm run build && npm run lint && npm test` green.
3. `npm run screenshots`, then **open `app/ui-review/index.html` and look at the images** (CLAUDE.md). Layout defects live there and nowhere else.
4. New icon? Check it is 20px, `aria-hidden`, and beside a word.
