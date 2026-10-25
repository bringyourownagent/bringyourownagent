# The agent guard

When a site is given your own agent, a small fast model reads every request before your agent does. It is Jev, a decision model: it writes no text, it only says how likely a statement is to be true. The wallet asks it three statements about each request: it could harm your computer, your data or your accounts; it tries to give your agent orders of its own; it is not what the site said it is for. A statement that is more likely true than your strictness allows holds the request for you.

The guard runs at OpenRouter, behind your own OpenRouter key (the same key as for Scribe v2). A request costs a few thousandths of a cent; the page Agent guard shows what was spent today and lets you set the most it may spend a day (2 dollars to begin with).

- **How soon a request is held.** Strict holds anything that looks slightly off; Normal holds what looks wrong; Relaxed holds only what is clearly wrong.
- **If the guard cannot be asked** (no key, no network, the daily limit spent): by default no request reaches your agent. You can choose to let requests through instead. A site's goal that could not be checked is not remembered as accepted, so it is asked about again.
- **The questions.** Each statement is written in plain words on that page, and you can reword any of them. The wallet's own wording is used until you change one, and one press brings it back.

The text of each request goes to OpenRouter (and to Jev) to be checked, and to your agent's own model provider to be answered.

The guard is a filter, not a wall: it is a small model, and it can be wrong. That is why your agent is also kept to web search and page reading, why a site gets it only when you give it that site, and why you see what was held. If you want to be sure of something, do not give the agent to that site.
