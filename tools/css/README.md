# Splitting the stylesheet

`split-app-css.mjs` cuts `src/Tide.Web/wwwroot/app.css` into ordered parts under `wwwroot/css/` (see "Where the styles
live" in [docs/architecture/frontend.md](../../docs/architecture/frontend.md#design-tokens)). It finds each cut by the
section heading the part starts at, not by line number, and checks that each heading is at the top level of the file,
found once and in order, and that the parts joined are `app.css` byte for byte. Then it:

- writes the parts, deleting any an earlier run wrote;
- replaces the `app.css` `<link>` in `App.razor` (or the parts' `<link>`s from an earlier run) with one per part, in
  order, each with `?v=<version>`;
- writes `split-manifest.json`: the parts, which `StylesheetSplitTests` checks are the ones on disk, and the SHA-256
  of `app.css` they were cut from (line endings as LF), for the record;
- deletes `app.css`.

```bash
node tools/css/split-app-css.mjs                                  # where it would cut; writes nothing
node tools/css/split-app-css.mjs --write --version 20261201-split # do it
```

## Running it again on a newer main

The split is two commits: the tooling (this script, the tests and CI scripts that read the parts, the docs), then
**Split the stylesheet into parts by area**, which is only what the script wrote. Changes to `app.css` merged since
then are taken by running the script again on the newer `app.css`, never by resolving conflicts in the parts:

```bash
git fetch origin
git checkout -B ui/split-app-css origin/main
git cherry-pick <the tooling commit>            # conflicts here are in tests or docs: resolve as usual
node tools/css/split-app-css.mjs                # check the cuts on the new app.css
node tools/css/split-app-css.mjs --write --version <yyyymmdd>-split
git add -A src/Tide.Web tools/css
git commit -m "Split the stylesheet into parts by area"
```

If a heading the script cuts at was renamed or removed on main, it stops and names the part. Change that part's
`starts` in the script to the new heading, which must start a line at the top level of the file, outside any block.
A change that added a section at the end of `app.css` lands in `90-option-e.css`, as it would have in the single file.

After the split merges, a branch that still edits `app.css` doesn't merge cleanly: re-apply its hunk to the part that
holds those rules (the same text is there, unchanged), bump that part's `?v=`, and drop `app.css` again.

Once the split is on main this script has done its job. The byte-for-byte check is the script's, at the moment it
cuts: no test repeats it, since every later edit to a part would fail it. The script and the manifest can go once no
open branch still edits `app.css`.
