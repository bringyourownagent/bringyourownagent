# Resolved desktop integration note

The desktop now passes the live `SourceSet.Text` list to the engine, renders speech and `text.generate` chains, and uses the engine-owned wallet test call. Added text sources are rebuilt by `SourceSet` whenever settings change; no separate registration adapter is needed.
