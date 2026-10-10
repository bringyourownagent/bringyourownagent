# Changelog

Both libraries share one version number and one rule set. Dates are UTC.

## 0.0.2 (2026-10-10)

- Releases now come only from GitHub Actions: npm through trusted publishing with provenance, PyPI through trusted publishing. No registry tokens exist anywhere.
- Added this changelog and links to it from both packages.
- No change to behaviour or to any rule code.

## 0.0.1 (2026-10-10)

First release, to claim the package names and to put the first draft rules into code.

- `validate_manifest` / `validateManifest`: checks a requester's manifest against the draft rules.
- `check_call` / `checkCall`: decides whether one call may go ahead under the visitor's grant (`allowed`, `warn`, `ask` or `blocked`), with reason codes.
- `apply_opinion` / `applyOpinion`: merges the opinion of an optional purpose judge.
- The `bringyourownagent` command line tool.
- 173 shared test vectors that both libraries pass identically.
