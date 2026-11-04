# TIDE analyst walkthrough: findings (October 2026)

> **Status.** All eight bugs below are fixed. So are these polish items: accessible names on icon-only buttons, the
> brief picker showing author and brief date, a confirmation on "Dismiss all", the Actions page opening on "Mine",
> Trends falling back to month-to-date, a clearer Discussed toggle, and a prefilled CISA KEV source.
>
> Fixed since, from the workflow gaps: a past day's brief says so in a banner, labels its numbers by day, is presented
> by whoever starts it, and can record that presenter as cover in the rotation. Ending its meeting records both when
> the meeting was held (that day's meeting time by default, or one you choose) and when it was recorded. "Mark ready"
> asks first when the running order has warnings, and a sent brief's numbers are fixed (administrators can still
> correct them). The team edition lists each article's indicators and follow-up actions. "Why it matters to us" has its
> own TLP, and an article holding a part marked above its own TLP says so in the running order, the preview and on its
> slide. "Add to today's brief" on the article page still means the real today.
>
> Fixed in a second pass:
> - **Hunting:** hunts can be built from the whole story, or written by hand, with a window from the story's age.
> - **One story, many reports:** triage groups items by CVE, keeps a follow-up's link on the article, and can dismiss the
>   rest as covered. It also has "since the last brief", a time zone, and live search.
> - **Working as a team:**
>   - Single dismissals take their own reason.
>   - Decided items say who decided.
>   - Promoting is claimed atomically.
>   - The inbox refreshes live, and a brief or article says who else has it open.
>   - Duplicate actions in a story are flagged.
>   - Comments can be edited and deleted.
> - **Polish:**
>   - "Only mine" in the brief picker, a shorter wrap-up slide, and End in present mode's top bar.
>   - Assignees grouped by team, and per-person and P1 numbers on Workload.
>   - Last-dot defanging, a crypto wallet type, hints for retired ATT&CK ids, and three new starter tags.
>   - The excerpt shown as a reference, a retired source's items out of the inbox, and a new source polled at once.
>   - The rotation admin page shows past weeks.
>
> Actions raised for a past day's brief now record that day: reports count them then, and their SLA starts when they
> were recorded (ADR 0013). A new action's past due date warns.
>
> One correction to the findings: the closing note *was* saved. It only showed in the status form's input, so it now
> has an Outcome card and goes into the action's history. The "ready / not ready" pair of toasts came from an earlier
> attempt's error toast staying up; a confirmation now clears earlier errors.

Three analysts used TIDE through its UI at the same time, on a fresh demo database (`App_Data/demo.db`,
http://localhost:5112), with real feeds. The three business days of briefs were written after the fact, on Mon Oct 26:

| Day | Analyst | Articles | Brief |
|---|---|---|---|
| Wed Oct 21 | Dev Analyst (me) | PHP2Shell patches, SafePay #StopRansomware, CISA KEV +6 (Plex), ShinyHunters negotiator, Chrome ClickFix warnings | presented, both editions sent |
| Thu Oct 22 | Robin (helper agent, `?as=Robin`) | GitLab CVSS 10 RCE, LiteLLM supply chain, PHP2Shell pre-disclosure probing, Storm-0940/ConnectWise, Node.js Brotli, Bronze Sandcastle | presented, both editions sent |
| Fri Oct 23 | Sam (helper agent, `?as=Sam`) | VeloCloud zero-day, PHP2Shell/Mirai, Pillow, Railway breach, Linux KSMBD, eMudhra exits TLS | presented, both editions sent |

- **Totals:** 17 articles, each with 4–6 summary bullets, tags, ATT&CK techniques, CVEs and published indicators.
  33 actions (A-3 to A-35) across four teams.
- **Stories:** PHP2Shell (3 articles), ConnectWise supply chain, and VeloCloud exploitation.
- **Actions worked by colleagues:** Robin closed A-3 (PHP patching). Sam closed A-4 (PHP2Shell hunt) and A-25
  (IOC sweep with a generated KQL query).
- **Admin setup:**
  - Added 7 RSS sources (BleepingComputer, The Hacker News, CISA advisories, Krebs, SecurityWeek, The Record, Dark
    Reading) and the CISA KEV catalog, and retired the sample feed.
  - Added two teams and their members.
  - Set SLA targets for IOC sweeps and blocks.
  - Set edition recipients, AD group mappings and a manager to notify.
  - Imported the example metrics.

## Bugs

1. **The session crashes when toasts overlap (high).** I saved with "Save and mark ready", which raised three toasts at once.
   - **What happened:** the page went to "Something went wrong. Reload the page to continue", and unsaved edits were
     lost.
   - **Log:** `InvalidOperationException: Collection was modified` in `ToastHost.BuildRenderTree`.
   - **Cause:** `ToastService.Add` schedules `Dismiss` with `Task.Delay(...).ContinueWith(..., TaskScheduler.Default)`,
     so the list changes on a thread-pool thread while the circuit renders it.
   - **Fix:** give the host a snapshot of the list under a lock, or dismiss through the component's `InvokeAsync`.
2. **"Paste several" strips a leading `**`.** The bullet-marker stripping treats `*` as a list marker.
   - **What happened:** `**CVE-2026-110264** (CVSS 10.0) …` was stored as `CVE-2026-110264** …`, so the bold broke.
   - **Scale:** Robin hand-fixed it on 5 articles and Sam on 7 bullets.
   - **Fix:** only strip `-`, `*` or `•` when a space follows.
3. **The sticky Save bar covers the indicator "Add" button, and Enter doesn't add.**
   - **What happened:** clicks landed on `.tide-sticky-save`. 9 indicators, 2 CVEs and a wallet address silently failed
     to add on the first pass.
   - **Fix:** add bottom padding so the last control can scroll clear of the bar, and make Enter in the box add.
4. **Save and Comment only enable after the field loses focus** (bound on change, not input). The click that blurs the
   field is lost, so a fix looks saved when it isn't. One of Sam's comments was posted garbled. The new-story "Start"
   button behaves the same way.
5. **The outcome note on a closed action isn't shown anywhere.** Robin closed A-3 as Done, Remediated, with a note, and
   the note doesn't appear on the action, in its history or in the closed list. It's either not saved or not
   rendered.
6. **"Save and mark ready" says both yes and no.** With no bullets it showed "Saved", "Marked ready for a brief" and
   "It isn't ready yet: Add at least one bullet". The article ended up Ready.
7. **The send log renders "Sentas sent"**: the space before the "as sent" link is missing.
8. **Startup noise:** a new database logs `no such table: RoleMappings` before the migrations run.

## Workflow gaps

**Writing a brief after the fact.** Analysts do this after a sick day or an outage, and here it was the whole exercise.
- "Today's brief" and the editor's "Add to today's brief" always mean the real today. You reach a past brief only by
  URL or the date arrows, and nothing says the date is in the past.
- **End the meeting** stamps the real clock ("met Oct 26, 11:56 PM"), so the archive says Friday's brief met on
  Monday. Ask for the date, or default to the brief's date.
- **Dates on actions and articles:**
  - New actions take their due date from today's SLA, and every action due on the brief's day is Overdue the moment
    it's created.
  - Closing them now records them as "Missed", which skews Workload and the SLA trends.
  - Articles are all stamped Oct 26.
- **Presenter and rotation:** the presenter defaulted to the rotation's person (Robin, all week), and switching it
  records no cover or swap, unlike the rotation's own approval flow (ADR 0010). The rotation admin page starts at the
  current week, so past weeks can't be seen.

**Hunting.**
- **Hunt queries only use the action's own article.** A-4, the PHP2Shell hunt, hangs off an article with no
  indicators, while the story's later article has 13. You can't generate the hunt that needs it most.
  - Generate from every article in the story, or let the hunter pick a source article.
  - Allow attaching a hand-written behavioural query, such as "php-fpm spawning sh".
- The generated KQL always uses `ago(30d)`. It could take its window from the action or article date.

**Editions.**
- Neither email edition shows indicators or the actions raised, only CVEs and ATT&CK. A compact indicator table, or
  "13 indicators attached" with a link, would help the team edition.
- "Why it matters to us" always goes into the company-wide edition, even though it often holds internal context.
  Give it a TLP of its own, or warn when the company-wide edition includes it.
- A bullet raised to AMBER lifts the edition's marking, but the article's badge in the running order and on its slide
  still says CLEAR.

**Brief readiness.**
- "Mark ready" has no brief-level gate. Two articles still said "Add a scorecard tag" and it went Ready anyway.
  Show "2 warnings, ready anyway?".
- Today's numbers stay editable after the brief is sent, so the page can disagree with what went out.

**One story, many reports.**
- An article holds one source link. A follow-up report (LiteLLM's Oct 22 update) can't be attached, so its URL is
  lost when it's dismissed as "Already covered". Support several sources per article, and "attach to article" from
  triage.
- Triage doesn't group coverage of one story. PHP2Shell had 11 items from 4 sources over 3 days, all carrying the
  same CVEs, and none were flagged as related. Cluster them by shared CVE, and offer "dismiss the rest as covered".
- Triage has no date filter or "since the last brief" view. Item times show local time without a zone, while the
  brief window was reasoned in UTC.

**Working as a team.**
- **Shared inbox:**
  - "Dismiss all N shown" is one click with no confirmation, in an inbox everyone shares. The reason dropdown beside
    it also silently sets the reason for single dismissals.
  - The Dismissed and Promoted tabs don't say who acted, or why.
  - Nothing stops two analysts promoting the same item. A "claimed by" marker or a warning would.
- No warning about duplicate actions within a story. Sam's A-25 overlapped A-31, both assigned to Sam.
- Comments can't be edited or deleted.
- No presence ("Robin is editing the Oct 22 brief"). Lists such as stories and triage counts only update on reload.
- Concurrency itself was solid. Three analysts promoted, edited and raised actions at once with no errors, and the
  action numbers simply interleave.

## UX polish

**Accessibility**
- Icon-only buttons have no accessible name: the add buttons in the brief picker, move up/down/remove, and the
  Present header buttons.
- Scorecard tag buttons have no `aria-pressed`.
- The "Discussed" toggle's on state is only a small icon change. Robin unchecked one by accident.

**Brief builder**
- The "Add articles" picker mixes everyone's articles, with no author or target date. Add a "Mine" filter, and show
  which brief an article is already on.
- On a past date, an article on a later brief says "briefed before". Say "briefed Oct 26".

**Present mode**
- The wrap-up slide repeats every article's recommended actions before the list of actions raised, which makes a
  very long slide.
- "End the meeting" sits below the fold. Put it in the top bar too.
- The meeting timer stayed at "0 min".

**Actions**
- The actions page opens on all open actions rather than "Mine".
- The new-action assignee list shows everyone, not the chosen team's members.
- On Workload, clicking a holder could open that person's list, and a P1 count would help.

**Trends**
- Trends opens on the last full month. With all the data in October that was September, all zeros, and it looked
  broken. Fall back to month-to-date when the last full month is empty.

**Taxonomy and indicators**
- **Taxonomy gaps.** No tag fits "disclosed, no patch yet" (KSMBD) or "PKI / certificate trust" (eMudhra), so both
  went untagged. Possible additions: "Unpatched disclosure", "PKI & trust", "Open source".
- **Defanging.** File names are defanged like domains (`cook[.]pth`), and IPs have every dot bracketed. The sources
  and most tools bracket only the last dot.
- **Indicator types.** There's no "Crypto wallet" type, so the Railway wallet address went in as "Other".
- **ATT&CK.** "T1574.002" finds nothing, because ATT&CK merged it into T1574.001 and reporting still cites the old id.
  A hint for merged or retired ids would help.

**Triage and the editor**
- Promote fills the bottom line with the publisher's own excerpt. That's easy to leave in a brief by accident; show
  it as a reference to rewrite instead.
- Triage search only runs on Enter.
- Items from a retired source stay in the inbox.

**Admin**
- A new CISA KEV source shows its default address only as a placeholder. Prefill the address and the name.
- A just-added source shows "Never" polled until the page reloads, even though its first poll ran.

## What worked well

- **CVE context.** It saw that the PHP2Shell CVEs had joined KEV and suggested reclassifying the article as a threat.
  Triage's "covered:" hints link to articles already written about a CVE.
- **Per-bullet TLP and the editions.** An internal AMBER bullet showed in the team edition and was dropped cleanly
  from the company-wide one.
- **Present mode** is smooth: the slide strip, Today's numbers with comparison to the previous business day, and
  Discussed (D).
- **Sending** has a clear confirmation (subject, marking and recipients) and a send log. In dev it writes `.eml` files
  to `App_Data/mail`.
- **Stories** tie the three PHP2Shell articles together across the three days.
- **Hunt queries** generated straight from an article's indicators, with the result recorded on the action.
- **Trends** (once on October) showed 17 articles and T1190 as the top technique. Workload gives a clear view per
  team.
