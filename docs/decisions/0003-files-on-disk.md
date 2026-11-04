# 0003. Screenshots live on disk, not in the database

- **Status:** Accepted
- **Recorded:** 2026-10-25

## Context

An article can carry up to ten screenshots, a year of daily briefs adds up to thousands, and the database is often a
shared instance run by a DBA team.

## Decision

- Bytes are written to `Files:RootPath`, outside the web root, named by id.
- The database holds each file's name, its sniffed type, its size and its SHA-256.
- Files are served only through `/files/{id}/inline`, which re-sniffs the bytes and checks the viewer's TLP
  allowance.

## Consequences

- Backups must take the database and the data root together, and the install guide says so.
- The shared database stays small.
