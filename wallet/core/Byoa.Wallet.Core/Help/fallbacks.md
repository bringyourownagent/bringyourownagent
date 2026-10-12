# Fallbacks and order

Each skill has an ordered chain. The first enabled source that knows the language and is ready gets the call. If it is off, busy, missing, or fails, the wallet tries the next source. You can set a different order for a language.

A paid fallback asks before it runs unless you explicitly allowed it for that site. The wallet records which stop answered and the fallback route, but the site only receives the result and its cost receipt.
