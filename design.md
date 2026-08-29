# Design — NexaOne MES

A locked design system for this app. Every page redesign reads this file before
emitting code. Do not regenerate per page; amend this file when the system grows.

## Genre

Modern-minimal, expressed as an industrial operations workbench: compact,
technical, calm, and explicit about system state.

## Macrostructure family

- Marketing and authentication: Split Studio, using H2 Split Diptych at 7/5 on
  desktop and a single-column proof-first stack on phones.
- App pages: Workbench. Desktop uses a persistent navigation seam and dense data
  canvas; Mobile and POP use the same hierarchy with 44px and 56px targets.
- Content and inventories: Index-First. Search, filters, and grouped rows precede
  detail; long uniform card catalogues are not allowed.
- Designer editor: Workbench with three explicit zones — components, canvas,
  properties — and the canvas preview must consume the same tokens as runtime.

## Theme

Custom “Nexa Industrial”; axes: cool-blue paper / compact sans UI / teal signal.

- `--color-paper` oklch(95.7% 0.006 255.5)
- `--color-paper-2` oklch(100% 0 0)
- `--color-ink` oklch(27.8% 0.030 256.8)
- `--color-ink-2` oklch(43.2% 0.046 265.3)
- `--color-rule` oklch(92.3% 0.008 253.9)
- `--color-accent` oklch(50.3% 0.092 175.9)
- `--color-accent-ink` oklch(100% 0 0)
- `--color-focus` oklch(50.3% 0.092 175.9)
- Bright teal `--color-signal` is a small status/brand marker, never body text or
  a white-text button fill. Accent coverage stays below 5% of a viewport.

## Typography

- Display: Pretendard Variable, weight 720, normal.
- Body: Pretendard Variable, weight 400–650.
- Mono: IBM Plex Mono / JetBrains Mono fallback, weight 500; only UI IDs, query
  IDs, paths, and measured values.
- Display tracking: `-0.018em`.
- Type scale: 1.25 major-third anchored at 13px; `--text-display` is
  `clamp(2rem, 3vw + 1rem, 3.052rem)`.
- Pretendard-only display/body is deliberate for Korean glyph coverage and dense
  plant terminals. The mono register provides the second functional voice.

## Spacing

Use the 4-point named scale in `tokens.css`. Product CSS uses `var(--space-*)`
or the temporary mapped `--nx-sp-*` aliases, never new raw spacing values.

## Motion

- `--ease-out: cubic-bezier(0.16, 1, 0.3, 1)` for entry and hover feedback.
- `--ease-in: cubic-bezier(0.7, 0, 0.84, 0)` for exits.
- `--ease-in-out: cubic-bezier(0.65, 0, 0.35, 1)` for state changes.
- No page-load reveal. Use button press feedback and state/panel crossfades only.
- Reduced-motion fallback is an opacity-only change of at most 150ms.

## Microinteractions stance

- Silent success when the result is visible; error toasts name the failed action
  and provide retry or undo.
- Focus is instant and uses a contrast-safe two-layer ring.
- Hover styling exists only inside `(hover: hover) and (pointer: fine)` and always
  has a keyboard/touch equivalent.
- Loading keeps the action label understandable; forms validate on blur after the
  field is touched.
- Touch targets: 44px on desktop and Mobile, 56px on POP.

## CTA voice

- Primary: dark teal fill, white text, 4px radius, 44px desktop height; specific
  verb such as “변경 저장”, “DB로 가져오기”, or “작업 시작”.
- Secondary: paper surface, 1px rule, same radius and height; no generic “확인”.
- Destructive: dark red plus icon/text; colour is never the only signal.

## Per-page allowances

- Authentication may retain one hand-built factory SVG as visual proof, but it
  must not invent live data or metrics.
- App pages use no decorative enrichment; data, controls, and operational state
  carry the page.
- Designer Home uses Index-First rows and visible search. Screen Editor and meta
  screens use Workbench density.
- Mobile/POP may enlarge controls and simplify columns without changing semantic
  order or action names.

## What pages MUST share

- NAF/NexaOne wordmark treatment, navy shell, and small teal signal.
- The token source and light/dark pair in `tokens.css`.
- Pretendard display/body and mono-only identifier register.
- Button geometry, field geometry, focus treatment, status colours, and empty,
  loading, error, and success state contracts.
- Section heads use a stacked functional label and heading. Decorative chapter
  numbers and left-border KPI cards are not part of the system.

## What pages MAY differ on

- Density: desktop, Mobile, and POP control-size profiles.
- Workbench panes: persistent, collapsible, or bottom-sheet according to width.
- Inventory presentation: table-like rows on desktop and labelled records on
  phones, while preserving the same order and actions.

## Responsive contract

- Required visual checks: 320, 375, 414, 768, 1280, and 1440 CSS px.
- Layout breakpoints are content-driven around 40rem, 60rem, and 90rem.
- `html` and `body` use `overflow-x: clip`; no `100vw` layout widths.
- Interactive labels remain one line; wide data uses an intentional internal
  scroller or labelled mobile record view.

## Exports

`tokens.css` at the project root is canonical. The formats below are portable
translations; they do not replace the source.

### tokens.css

```css
:root {
  --color-paper: oklch(95.7% 0.006 255.5);
  --color-paper-2: oklch(100% 0 0);
  --color-paper-3: oklch(97.5% 0.003 247.9);
  --color-ink: oklch(27.8% 0.030 256.8);
  --color-ink-2: oklch(43.2% 0.046 265.3);
  --color-rule: oklch(92.3% 0.008 253.9);
  --color-rule-2: oklch(76.6% 0.023 256.7);
  --color-muted: oklch(49.5% 0.033 263.2);
  --color-neutral: oklch(43.2% 0.046 265.3);
  --color-accent: oklch(50.3% 0.092 175.9);
  --color-accent-ink: oklch(100% 0 0);
  --color-focus: oklch(50.3% 0.092 175.9);

  --font-display: "Pretendard Variable", Pretendard, ui-sans-serif, sans-serif;
  --font-body: "Pretendard Variable", Pretendard, ui-sans-serif, sans-serif;
  --font-outlier: "IBM Plex Mono", "JetBrains Mono", ui-monospace, monospace;

  --space-3xs: 0.125rem; --space-2xs: 0.25rem; --space-xs: 0.5rem;
  --space-sm: 0.75rem; --space-md: 1rem; --space-lg: 1.5rem;
  --space-xl: 2rem; --space-2xl: 2.5rem; --space-3xl: 4rem;
  --space-4xl: 6rem; --space-5xl: 8rem;

  --text-xs: 0.6875rem; --text-sm: 0.75rem; --text-base: 0.8125rem;
  --text-md: 1rem; --text-lg: 1.25rem; --text-xl: 1.5625rem;
  --text-2xl: 1.9531rem;
  --text-display: clamp(2rem, 3vw + 1rem, 3.052rem);

  --ease-out: cubic-bezier(0.16, 1, 0.3, 1);
  --ease-in: cubic-bezier(0.7, 0, 0.84, 0);
  --ease-in-out: cubic-bezier(0.65, 0, 0.35, 1);
  --dur-micro: 120ms; --dur-short: 220ms; --dur-long: 320ms;
  --rule-hair: 1px; --rule-fine: 2px;
  --radius-input: 4px; --radius-card: 6px; --radius-panel: 10px;
  --radius-pill: 999px;
}
```

### Tailwind v4 `@theme`

```css
@theme {
  --color-paper: oklch(95.7% 0.006 255.5);
  --color-paper-2: oklch(100% 0 0);
  --color-paper-3: oklch(97.5% 0.003 247.9);
  --color-ink: oklch(27.8% 0.030 256.8);
  --color-ink-2: oklch(43.2% 0.046 265.3);
  --color-rule: oklch(92.3% 0.008 253.9);
  --color-rule-2: oklch(76.6% 0.023 256.7);
  --color-muted: oklch(49.5% 0.033 263.2);
  --color-accent: oklch(50.3% 0.092 175.9);
  --color-focus: oklch(50.3% 0.092 175.9);
  --font-display: "Pretendard Variable", Pretendard, ui-sans-serif, sans-serif;
  --font-body: "Pretendard Variable", Pretendard, ui-sans-serif, sans-serif;
  --font-outlier: "IBM Plex Mono", "JetBrains Mono", ui-monospace, monospace;
  --spacing-3xs: 0.125rem; --spacing-2xs: 0.25rem; --spacing-xs: 0.5rem;
  --spacing-sm: 0.75rem; --spacing-md: 1rem; --spacing-lg: 1.5rem;
  --spacing-xl: 2rem; --spacing-2xl: 2.5rem; --spacing-3xl: 4rem;
  --text-xs: 0.6875rem; --text-sm: 0.75rem; --text-base: 0.8125rem;
  --text-md: 1rem; --text-lg: 1.25rem; --text-xl: 1.5625rem;
  --text-2xl: 1.9531rem;
  --ease-out: cubic-bezier(0.16, 1, 0.3, 1);
  --ease-in: cubic-bezier(0.7, 0, 0.84, 0);
  --ease-in-out: cubic-bezier(0.65, 0, 0.35, 1);
  --radius-card: 6px; --radius-pill: 999px; --radius-input: 4px;
}
```

### DTCG `tokens.json`

```json
{
  "$schema": "https://design-tokens.github.io/community-group/format/",
  "color": {
    "paper": { "$value": "oklch(95.7% 0.006 255.5)", "$type": "color" },
    "paper-2": { "$value": "oklch(100% 0 0)", "$type": "color" },
    "paper-3": { "$value": "oklch(97.5% 0.003 247.9)", "$type": "color" },
    "ink": { "$value": "oklch(27.8% 0.030 256.8)", "$type": "color" },
    "ink-2": { "$value": "oklch(43.2% 0.046 265.3)", "$type": "color" },
    "rule": { "$value": "oklch(92.3% 0.008 253.9)", "$type": "color" },
    "accent": { "$value": "oklch(50.3% 0.092 175.9)", "$type": "color" },
    "accent-ink": { "$value": "oklch(100% 0 0)", "$type": "color" },
    "focus": { "$value": "oklch(50.3% 0.092 175.9)", "$type": "color" }
  },
  "font": {
    "display": { "$value": "Pretendard Variable, Pretendard, sans-serif", "$type": "fontFamily" },
    "body": { "$value": "Pretendard Variable, Pretendard, sans-serif", "$type": "fontFamily" },
    "outlier": { "$value": "IBM Plex Mono, JetBrains Mono, monospace", "$type": "fontFamily" }
  },
  "space": {
    "2xs": { "$value": "0.25rem", "$type": "dimension" },
    "xs": { "$value": "0.5rem", "$type": "dimension" },
    "sm": { "$value": "0.75rem", "$type": "dimension" },
    "md": { "$value": "1rem", "$type": "dimension" },
    "lg": { "$value": "1.5rem", "$type": "dimension" },
    "xl": { "$value": "2rem", "$type": "dimension" }
  },
  "size": {
    "text-base": { "$value": "0.8125rem", "$type": "dimension" },
    "text-lg": { "$value": "1.25rem", "$type": "dimension" },
    "text-display": { "$value": "3.052rem", "$type": "dimension" }
  },
  "duration": {
    "micro": { "$value": "120ms", "$type": "duration" },
    "short": { "$value": "220ms", "$type": "duration" },
    "long": { "$value": "320ms", "$type": "duration" }
  }
}
```

### shadcn/ui CSS variables

```css
:root {
  --background: 95.7% 0.006 255.5;
  --foreground: 27.8% 0.030 256.8;
  --card: 100% 0 0;
  --card-foreground: 27.8% 0.030 256.8;
  --popover: 100% 0 0;
  --popover-foreground: 27.8% 0.030 256.8;
  --primary: 50.3% 0.092 175.9;
  --primary-foreground: 100% 0 0;
  --secondary: 97.5% 0.003 247.9;
  --secondary-foreground: 43.2% 0.046 265.3;
  --muted: 92.3% 0.008 253.9;
  --muted-foreground: 49.5% 0.033 263.2;
  --accent: 50.3% 0.092 175.9;
  --accent-foreground: 100% 0 0;
  --destructive: 50.3% 0.180 23.6;
  --destructive-foreground: 100% 0 0;
  --border: 92.3% 0.008 253.9;
  --input: 92.3% 0.008 253.9;
  --ring: 50.3% 0.092 175.9;
  --radius: 6px;
}
```
