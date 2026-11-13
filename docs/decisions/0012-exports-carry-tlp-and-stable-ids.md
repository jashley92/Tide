# 0012. Exports carry each article's TLP, leave out what's above the chosen ceiling, and use stable ids

- **Status:** Accepted
- **Recorded:** 2026-10-26

## Context

Indicators leave TIDE to go into a threat-intel platform, a SIEM watchlist, a partner's feed or a spreadsheet. Once a
file has left, TIDE can't take anything back, so the export has to carry its markings and has to make it hard to send
more than intended. The same indicator is exported again and again (today's brief, then the story it belongs to), and
the platform receiving it shouldn't end up with duplicates.

## Decision

- **A ceiling, chosen per export.** The person exporting picks the highest TLP to include. Articles marked above it are
  left out whole (their indicators, CVEs and techniques with them), the way the company-wide edition leaves them out.
  Indicators have no TLP of their own, so an indicator's marking is its article's.
- **Markings travel with every object.** In STIX each object carries the OASIS TLP 2.0 marking-definition id for its
  article's TLP; the CSV has a TLP column. An indicator, CVE or technique shared by several articles takes the highest
  of their markings, since its notes come from all of them.
- **Stable ids.** A report's id is its article's. An indicator's, CVE's or technique's id is a UUIDv5 (STIX namespace)
  of what it is (the pattern, the CVE id, the technique id), so the same object has the same id in every export and a
  platform updates it instead of adding another.
- **Only clean mappings in STIX.** Types with a clear STIX observable go into STIX patterns; host names, process names
  and "other" don't, because a guessed mapping would give consumers the wrong idea. The CSV has every indicator.
- **No push.** TIDE produces files. It doesn't post to a TAXII server or a platform's API.

## Consequences

- An export is never more permissive than the person chose, and what's in it says how it may be shared.
- Re-importing a later export updates objects in place; an indicator dropped from an article simply stops appearing,
  and TIDE doesn't send revocations.
- Pushing to TAXII or a platform API, if wanted, would be a separate decision, along with handling credentials.

## Amended 2026-11-12: the marking is part of an object's id

An indicator, CVE or technique kept one id across exports even when its marking differed: on CLEAR in one export and
AMBER in another (another ceiling, or another set of articles). A platform importing both then held two versions of one
object that disagreed about how it may be shared, and which won depended on import order.
- **An indicator's, CVE's or technique's id is a UUIDv5 (STIX namespace) of `type|value|tlp`**: the value is the STIX
  pattern, the CVE id or the technique id, and the TLP is the object's marking in that export, by its key (`clear`,
  `green`, `amber`, `amber-strict`, `red`).
- The same object at the same TLP still has the same id in every export, so re-imports still merge. The same object at
  another TLP is another object.
- A report's id is unchanged (its article's), as is the team identity's.
- Consumers see new ids for these objects from v1.13.0; UPGRADE.md says so.
