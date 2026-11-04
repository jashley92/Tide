# 0009. Intake reaches out only when asked, keeps one item per address, and never renders feed HTML

- **Status:** Accepted
- **Recorded:** 2026-10-26

## Context

M4 brings items into TIDE from outside: RSS and Atom feeds, the CISA KEV catalog, and CVE data from NVD and FIRST.
TIDE runs on an internal server that often reaches the internet only through a proxy, if at all. Feeds are untrusted
input (XML with entities, HTML summaries), the same story arrives from several outlets with tracking tags on its
links, and the inbox has to stay fast to triage at the start of a busy day.

## Decision

- **Off until switched on.** Polling (`Intake:Enabled`) and CVE enrichment (`Cve:Enabled`) are off by default. An
  administrator can still poll a source or refresh CVE data by hand. Requests go only to administered sources and the
  configured CVE service addresses (which can point at an internal mirror), through one client with a proxy setting,
  a time limit, a size limit, no https-to-http redirects, and a per-host back-off. A failing source is polled less
  often (its interval doubles per failure, up to 16 times).
- **Never into the server or, by default, the internal network.** Added October 2026: a feed's operator controls its
  redirects, so the client could be pointed at the server itself or an intranet host. Loopback, link-local (cloud
  metadata among them) and reserved addresses are always refused. Private ranges are refused unless
  `Intake:AllowPrivateNetworks` is set, for organizations whose feeds or CVE mirror are internal. Redirects are
  followed by TIDE, not the HTTP handler, so each hop's host is checked. Going direct, each connection is checked
  again on the address it actually reaches, which defeats DNS rebinding. Through a proxy, which resolves names
  itself, only the per-hop check applies, and a name the server can't resolve (a network that reaches the internet
  only through the proxy) is left to the proxy; an address, or a name that does resolve, is still checked.
- **Our own parser.** RSS 2.0, RSS 1.0 and Atom are read with `XmlReader` with no resolver, rather than a syndication
  library: a small surface we control, and no dependency to track. Summaries are reduced to plain text; no feed HTML is
  ever rendered. (November 2026:) a DOCTYPE is skipped unread rather than refused, as old RSS 0.91 generators still
  write one; an entity it would have declared stays undeclared and refuses the feed, so nothing is expanded or fetched.
- **A feed can be filtered by its own tags and by its titles.** Added October 2026.
  - **Tags** are the labels the feed puts on each item: RSS `<category>`, Atom `category`, RSS 1.0 `dc:subject`. A
    filter can be "only Security" for a general tech-news site. Tags match whole, ignoring case.
  - **Title words** are words or phrases matched as whole words, ignoring case and spacing. A word ends at anything
    that isn't a letter or digit, so "ransomware" matches "anti-ransomware" but not "ransomwares". This is for feeds
    that don't tag their items.
  - **How they combine.** An item passes the "only" side with an only-tag *or* an only-word in its title; with
    neither side set, everything passes. A skip-tag or skip-word then leaves it out. Skips win.
  - Items left out are never stored, so they aren't in the inbox or the dismissed tab. The poll counts them, and the
    source keeps the tags its feed last carried so the administrator can pick from real names.
  - Changing a filter clears the feed's validators, so the next poll reads it whole and takes in what's now allowed.
  - The KEV catalog is a list of CVEs, not news, and takes no filter.
- **One item per address.** An item's key is the SHA-256 of its canonical address (lower-cased host, default port,
  no fragment, tracking parameters removed, query sorted), so the same link polled twice or seen in two feeds is one
  item. A different address whose headline is mostly the same words as a recent item's is **flagged**, not hidden:
  the analyst confirms a duplicate with one key.
- **A starter list, added paused.** Added November 2026. Sources offers a short list of public cyber security
  sources (`StarterSources`), each checked to fetch and parse with dated items: a recommended dozen or so, ticked, and
  more in a collapsed group. Those picked are added paused and TLP:CLEAR, so a new install still reaches out only
  when an administrator turns a source on. One already here by name or address is left as it is.
- **Coverage, not just novelty.** The inbox says when an item's CVE or link was already covered, and on which brief.
  (October 2026:) new items sharing a CVE are grouped; a follow-up report can be added to the article that covers it
  as another source, and the rest of the story's items dismissed as already covered.
- **Promotion makes a draft.** A promoted item becomes a draft article with the source's default TLP, its CVEs and
  its summary shown beside the bottom line as a reference to rewrite (October 2026: no longer copied into it, where it
  was easy to leave in a brief by accident); it's never published as it came.
- **CVE context is stored, not fetched on view.** Pages read `CveRecord`s; the enrichment run refreshes KEV and EPSS
  daily and NVD weekly, paced to NVD's rate limit. A CVE on KEV or with a high EPSS score suggests (never sets) the
  Threat classification.
- **Volume isn't audited.** Feed items and CVE records are kept out of the audit log (they're machine-written and
  numerous); a triage decision records who made it and when on the item itself. Items nobody kept are pruned after
  `Intake:RetentionDays`.

## Consequences

- A new install shows an empty inbox until someone adds sources (or picks from the starter list) and turns polling
  on; that's deliberate.
- Feeds that rely on a DTD's entities or exotic extensions aren't read; they're rare, and the source shows the error.
- A starter source's address can move or stop working; the list is checked when it's changed, and a broken one shows
  its error on Sources like any other.
- An install whose feeds or CVE mirror are on private addresses sets `Intake:AllowPrivateNetworks` after upgrading;
  until then those sources show "a private address, which TIDE doesn't fetch from".
- Similar-title matching is approximate; it errs towards flagging, and nothing is dropped automatically.
