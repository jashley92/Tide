# 0007. A send freezes what went; email embeds images; Readers see only what was published

- **Status:** Accepted
- **Recorded:** 2026-10-26

## Context

M2 sends the brief by email through the organization's SMTP relay, and shows Readers an archive of the company-wide
edition. Articles stay editable after a brief is sent, and screenshots can be removed. Outlook (the main client)
ignores stylesheets and CSS variables, and blocks remote images by default. The inline image endpoint served a Reader
any screenshot at or under the public ceiling, whether or not it had ever been published.

## Decision

- **A send is frozen.** Every real send stores the email HTML, a plain semantic copy of the same content, and copies
  of its images (new `StoredFile`s, not references to the article's). The archive shows the frozen copy, never a
  fresh rendering. A failed send is logged but never published, and only a send the relay took marks the brief Sent.
- **One cut, one renderer.** The email and the SharePoint copy are rendered by `BriefRenderer` from a `BriefEdition`
  that `BriefEditions.Build` cut (ADR 0005). The renderer has no filter of its own; metrics are drawn only on the
  team edition.
- **Email embeds its images** as `cid:` linked resources with their real pixel width, and styles every tag inline.
  Its colours are literal hex from `EmailComposer.Palette`, which mirrors the light theme's `--tide-*` tokens: the
  one place outside `app.css` that names a colour.
- **The send is confirmed against what was shown.** The confirm step shows the marking, subject and every recipient;
  the send is refused if any of them, or the content, changed in between. The plan carries a hash of the email as
  it would be rendered (`SendPlan.Fingerprint`), so a colleague's edit to a bullet, caption, action or number after
  the confirmation is caught even when the marking and counts stay the same. Confirming reloads the preview beside
  it, so the analyst confirms the edition they can see.
- **One real send of an edition at a time.** A send is refused while another of the same edition is still with the
  relay (for up to 15 minutes, so one left "sending" by a stopped server doesn't block it for good), and freezing it
  moves the brief's edit version, so of two analysts confirming at once only one goes. (A freeze that meets another
  edition's, or any other move of the brief, runs every check once more rather than refusing.) Sending again on purpose is
  allowed: the confirmation says when and by whom the edition already went, and a send made after someone confirmed
  refuses theirs. A send that fails without saying whether the relay took it is logged as `Unknown`: it isn't
  published, and the next confirmation warns that it may already have gone. Recording a send's outcome is tried
  again if the database refuses it, so a send the relay took doesn't stay "sending".
- **Readers get only what was published.** For someone without the team edition, `/files/{id}/inline` serves only an
  image frozen in a sent company-wide edition: never a live screenshot, a team send's image, or a test send's.
- **Watched sends aren't retried behind the analyst's back.** There's no held mail queue: a
  failure is shown to the analyst, who sends again. The relay is still backed off after repeated failures
  (`OutboundCircuit`).

## Consequences

- The archive is exactly what readers received, and removing a screenshot later doesn't break it. Images are stored
  twice (screenshots are small).
- A Reader's access to images follows publication, not markings, so a mis-set ceiling after the fact can't widen it.
- The email palette must be kept in step with the light theme by hand.
