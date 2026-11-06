# 0019. Outreach emails are written by TIDE and sent by the analyst, held to the template's TLP

- **Status:** Accepted
- **Recorded:** 2026-11-06

## Context

The action kind "Vendor inquiry" was for asking a vendor or partner a question. Analysts ask more than vendors: an
ISAC, an internal team, a site's owner. Each wrote the same email again, pasting the article's title, its CVEs and the
indicators by hand, and tracked the reply in a comment or not at all. Pasting indicators by hand is how a RED detail
or a live link ends up in an email to a vendor.

## Decision

- **One kind, renamed.** "Vendor inquiry" is now **Outreach**: asking anyone for information. The kind is stored as
  its number (6), so every action raised as a vendor inquiry is an Outreach action, with its outcomes (Answered, Not
  affected, Other), SLA, playbooks and card rules unchanged. The one place its name was stored, an administrator's SLA
  target (`Actions:Sla:Kind:VendorInquiry`), is moved by the `OutreachEmailsAndStackContacts` migration. The old name
  still reads as Outreach where it can arrive from outside: a server's `Actions:Sla:Kind` value, a configuration bundle
  exported before, and a trends link (`ActionService.KindNamed`, `SettingsCatalog.Renamed`). The API says `Outreach`
  where it said `VendorInquiry`.
- **A team library of templates.** Kept as the saved hunt queries are (ADR 0006): anyone with `WorkActions` adds,
  edits, retires and restores them, whoever saved one or an administrator deletes it, anyone with `ReadActions` reads
  them, each write is audited and published, and an edit from an older copy is refused. Placeholders use the hunt
  queries' `{{name}}` syntax; `{{indicators:domain}}` takes one kind by the names `{{values:domain}}` uses. A name TIDE
  doesn't know is refused when the template is saved.
- **Filled from the action, held to a ceiling.** Each template has a TLP ceiling. Filling cuts the article with the
  editions' own rule (`BriefEditions.BuildArticle`): an article above the ceiling is left out whole, indicators with
  it, and a key point or "why it matters" above it alone. The action's own indicators carry no marking, so they count
  as AMBER, as the API holds other unmarked internal text to AMBER. Our stack's product facts and the action's number
  and dates carry none, as the API gives them to any token. The indicators are the ones a hunt on the action searches
  (`ActionService.HuntInputsAsync`), defanged. TIDE names what it left out, marks each placeholder it couldn't fill
  in the text (`[[product.versions: nothing to fill]]`) and won't mark an email sent while one is left, and writes a
  TLP line for the email at the highest marking it filled in.
- **TIDE never sends it.** The analyst edits the filled subject and body, copies them into their own mail, and marks
  the email sent to whom. TIDE keeps what they marked sent, waits for the reply, reminds them to follow up after a set
  number of business days (an administrator's switch and number, and each person's own choice, as the other
  reminders), and records the reply, with a file if wanted, closing the action with an outcome by the rule every close
  follows.
- **Recipients.** A product on Our stack has a vendor or support contact (a name and an address), and a template may
  have a fixed To address. The To line offers the template's address, then the contact of each product the action is
  about (tagged on its article, or named by it). Every address is checked as one.

## Consequences

- No mail relay is involved, so nothing about the email can fail silently in TIDE, and the reply arrives in the
  analyst's own mailbox, threaded with what they sent. In exchange, "sent" is the analyst's word, and the reply is
  recorded by hand.
- An email's text is kept as marked sent, so the action's history shows what was asked, even after the template
  changes or is deleted.
- An integration that read `VendorInquiry` from the API reads `Outreach` now.
