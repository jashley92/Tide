# 0014. Metric values can be read from CSV, by key and date; an import never overwrites a person's value or a sent day

- **Status:** Accepted
- **Recorded:** 2026-10-28

## Context

The brief opens with numbers: administrators define the metrics, and analysts type each day's
values. Most of those numbers already exist in another system, such as a ticket queue, a mail gateway or a
vulnerability scanner, so typing them is a daily chore and a source of slips. A connector that calls those systems
would make TIDE reach out to them, with credentials, which ADR 0009 keeps opt-in and which is a larger step. Most of
those systems can already export CSV on a schedule.

## Decision

- **CSV in, two ways.** An administrator uploads a file on Administration > Metric imports. When
  `Metrics:Import:DropFolderEnabled` is set (it's off by default), TIDE also reads files left in `metrics-inbox` under
  `Files:RootPath` every `Metrics:Import:PollMinutes`. No network is involved: whatever produces the file puts it
  there.
- **The file.** It's comma-separated with a header row naming `date`, `metric_key` and `value`, plus an optional
  `note`. Other columns are ignored, so the metrics page's export reads back in. Rows are keyed by metric key and date:
  - Keys never change when a metric is renamed.
  - Dates are business days written as 2026-10-28.
  - Values are read as the entry box reads them.
- **Idempotent.** A row sets the value for its metric and day. Reading the same file again changes nothing, and a later
  file replaces an earlier import's value.
- **People win.** An import never replaces a value an analyst typed. Typing over an imported value makes it the
  analyst's (`IsOverride` records that it differed), and later imports leave it alone. A day whose brief has been sent
  is never changed: what went out is the record, as for typed values.
- **Partial, explained.** Rows that can't apply are skipped and each one says why: an unknown or retired metric, a bad
  date or number, a weekend, a future day, a sent day, a person's value, or a row repeated in the file. The rest
  apply. A file without the needed columns, or that isn't text, or is over 1 MB or 10,000 rows, is refused whole.
- **Provenance.** Each read is a `MetricImport` record: the file, where it came from, who uploaded it, the counts and
  the problems. Every imported value points to the import that set it (`MetricValue.Source` and `ImportId`), and is
  shown with an "imported" mark on the day's numbers.
- **The folder is handled conservatively:**
  - Only `*.csv` files directly in the folder are read. Links, hidden and temporary files are skipped.
  - A file is skipped until it has been unchanged for 30 seconds, and while it's still open.
  - Each file is moved to `processed` or `rejected` once read, and those copies are deleted after 90 days.
  - Imports run one at a time, so an upload and the folder can't race.
  - Only the background service may import as the drop folder.

## Consequences

- A scheduled export can feed the brief's numbers with no credentials in TIDE and no new outbound traffic.
- An analyst who corrects an imported number keeps their correction; to let imports manage it again, clear the value.
  A value cleared by hand comes back if a later file includes it.
- The account that drops the files needs modify access to `metrics-inbox`, and to nothing else under the data root
  (INSTALL.md, "Metric imports").
- An HTTP connector can come later and reuse the same rules. It would also go through the outbound guard of ADR 0009.
