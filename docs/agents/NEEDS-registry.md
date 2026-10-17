# Resolved registry integration note

The merged engine now receives live text sources, selects language-specific chains, carries link-origin consent, and exposes the wallet-owned `TestSourceAsync` path. The registry delegates production tests to that path, and `SourceSet` rebuilds added sources (text and speech) from settings changes. The wallet holds, downloads and deletes no model.
