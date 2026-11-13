# 0023. The context rail docks from 1500px and is an overlay below

- **Status:** Accepted
- **Recorded:** 2026-11-12
- **Accepted:** 2026-11-09. The owner chose 1500px as the width at which the rail opens by default.

## Context

Pages had each grown their own side panel. One context rail on the right of every page that needs one replaces them
(Today, Triage, the article editor, Actions, an action, Search, Our stack, the Library, Adversaries). The team works on
laptops at about 1920×1080, where a 380px rail fits beside content held to 1400px; at 1440px it would squeeze the page.

## Decision

- In a window **1500px or wider** the rail is docked open beside the content. The Context button, Close or `]` closes
  it and the page takes the width back; the choice is remembered per browser (`tide-rail`), applied before paint.
- **Below 1500px** it starts closed and opens **over** the content as a modal overlay, which isn't remembered.
- The breakpoint lives in `app.css` alone (`--tide-rail-docks`); script reads it rather than repeating it.

## Consequences

- A page with no rail content looks and measures as it did before the rail existed.
- At 1440px, the common screenshot width, the rail is shut until asked for. The layout check holds the rail docked at
  1920 and 1500, and the overlay at 1440 and narrower.
