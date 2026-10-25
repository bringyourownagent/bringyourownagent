# Giving your own agent to a site

A site can use your own AI agent, not only a model: an agent can look things up on the web before it answers. A site that asks, for example, for the latest news about a country can be answered by your Claude Code, which searches the web and reads the pages. Add your agent in Sources, with **Add an agent**.

What the wallet lets your agent do for a site:

- one question at a time, and nothing else is remembered between questions;
- it may search the web and read web pages, and nothing more: no files, no commands, none of your other tools, and not this wallet;
- it starts in an empty folder of the wallet's own, and the site's words are given to it as a question to answer, never as an order;
- it runs on your own subscription, so the wallet pays nothing for it.

A site gets your agent only when you give it that site: a site that asks for it by name (`agent/*`) sees it in its window, and you tick it yourself. The wallet never puts your agent into the route of a site that asked only for a model. A site that asks for your agent must say what it will ask: a goal of a few words ("a geography game that looks up news about countries") is not enough, a sentence is. The guard looks at that goal, and if it looks risky for your agent you decide. If the site later states another goal, you are asked again.

Every request goes through the guard first (see Agent guard). What the guard holds, you see: the request as the site sent it, what the site said it is for, and what the guard noticed. You can let it through, refuse it, or refuse it and take your agent away from that site. With no answer in two minutes it is refused.

This is not the same as letting an agent manage the wallet (the page "Manage by an agent"): that is an agent working for you on the wallet's settings. Here the agent works for a site, so it is kept on a short leash.

Add an agent with the program that runs it: a launcher such as `ccodeglm.cmd`, or `claude.exe`. Press Test to ask it one question before you add it. Only you can add or change an agent: no agent can do it through the MCP bridge, because the program you name is one the wallet starts.
