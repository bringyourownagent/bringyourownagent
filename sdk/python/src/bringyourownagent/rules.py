"""Vocabulary of the BYOA 0.1-draft rules.

See spec/rules-0.1-draft.md in https://github.com/bringyourownagent/bringyourownagent.
Everything here is a draft for discussion and may change.
"""

from typing import Tuple

#: Protocol versions this library understands (the ``byoa`` field).
SUPPORTED_VERSIONS: Tuple[str, ...] = ("0.1-draft",)

#: The first skill vocabulary. Well-formed ids outside it only produce a warning.
KNOWN_SKILLS: Tuple[str, ...] = (
    "text.generate",
    "text.understand",
    "text.embed",
    "audio.transcribe",
    "audio.speak",
    "image.understand",
    "image.generate",
    "video.understand",
    "video.generate",
)

#: How sensitive the requester's data is (the manifest ``privacy`` field).
PRIVACY_CLASSES: Tuple[str, ...] = (
    "public",
    "personal",
    "personal-voice",
    "sensitive",
    "local-only",
)

#: Hard limits a manifest (``purpose.limits``) or a grant (``limits``) may set.
KNOWN_LIMITS: Tuple[str, ...] = ("maxCallsPerMinute", "maxClipSeconds", "maxFrameBytes", "maxTextChars")

#: Limits that count things and therefore must be whole numbers.
COUNT_LIMITS: Tuple[str, ...] = ("maxCallsPerMinute", "maxFrameBytes", "maxTextChars")

#: Source kinds a skill's ``models.kind`` may name (which sources may fill the skill).
SOURCE_KINDS: Tuple[str, ...] = ("model", "agent", "any")

#: What a wallet does with a source that fails a known condition (``models.onMismatch``).
ON_MISMATCH: Tuple[str, ...] = ("drop", "show")

#: The four properties a ``models.needs`` condition may name.
NEEDS_PROPERTIES: Tuple[str, ...] = ("context", "input", "output", "runs")

#: Modalities of the ``input`` and ``output`` properties.
MODALITIES: Tuple[str, ...] = ("audio", "image", "text")

#: Values of the ``runs`` property: the grant's own vocabulary, never a second one.
RUNS: Tuple[str, ...] = ("api-key", "home-server", "local")

#: Abilities a ``models.abilities`` entry may name; ``image.read`` is reserved and not enforced.
ABILITIES: Tuple[str, ...] = ("web.read", "web.search")

#: Periods allowed in the manifest ``expectedCost`` object.
COST_PERIODS: Tuple[str, ...] = ("perCall", "perSession", "perHour")

#: Kinds of provider the agent wallet can route a call to.
PROVIDER_KINDS: Tuple[str, ...] = (
    "subscription",
    "api-key",
    "connected-account",
    "local-model",
    "home-server",
)

#: Providers that keep the data on the visitor's computer or in their home.
LOCAL_PROVIDERS: Tuple[str, ...] = ("local-model", "home-server")

#: The four decisions of the call gate, mildest first.
DECISIONS: Tuple[str, ...] = ("allowed", "warn", "ask", "blocked")

#: Longest allowed ``purpose.summary``, in Unicode code points.
SUMMARY_MAX_LENGTH = 200

#: Longest allowed skill id, in characters.
SKILL_ID_MAX_LENGTH = 64

#: The rate limit window, in milliseconds.
RATE_WINDOW_MS = 60000
