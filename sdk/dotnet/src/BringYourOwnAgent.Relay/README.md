# BringYourOwnAgent.Relay (.NET)

The site's side of the BYOA **direct link** ([CONTRACT-DIRECT.md](https://github.com/bringyourownagent/bringyourownagent/blob/main/spec/CONTRACT-DIRECT.md)):
a visitor's agent wallet connects to your server over a WebSocket and your page asks it questions through you. No browser extension, no
account, no database — the library holds the one-time codes, the pairings and the answers a page has not picked up yet, in memory.

You add BYOA to an ASP.NET Core site in three lines, and the page talks to it over the endpoints below. The wallet is the visitor's own
program: it shows its consent, the grants and the limits stay with the visitor, and nothing of your page's text is ever written down.

```csharp
// Program.cs — a complete site that lends a visitor's own AI to its page
using BringYourOwnAgent.Relay;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddByoaRelay(options =>
{
    options.ManifestJson = File.ReadAllText("wwwroot/byoa/manifest.json");   // what you ask for; the links carry it
    options.ConnectCheck = true;                                            // your page shows the connect check code
});

var app = builder.Build();
app.UseWebSockets();                       // the relay cannot add middleware itself
app.MapByoaRelay();
app.UseDefaultFiles();
app.UseStaticFiles();
app.Run();
```

Your page then: asks `POST /api/r/{room}/byoa/code` for a link, opens `bringyourownagent://…` from the visitor's click, polls
`POST …/byoa/wait` until the wallet is in, and asks one participant a question at a time with `POST …/byoa/job` and `POST …/byoa/job/poll`.
A `room` is one browser page: a long random id the page makes once and sends with every request (`[A-Za-z0-9_-]{16,64}`) — it is what
keeps one visitor's wallet out of another visitor's page.

## The endpoints

| Endpoint | Body | Answer |
|---|---|---|
| `POST /api/r/{room}/byoa/code` | — | `{ session, link, check, expiresIn }` — the `bringyourownagent://connect` link, and the check code the page shows |
| `POST /api/r/{room}/byoa/wait` | `{ session }` | `{ state: "waiting" \| "connected" \| "expired", pairing, models, limits, capabilities }` |
| `POST /api/r/{room}/byoa/cancel` | `{ session }` | `{ cancelled }` — the page stopped waiting |
| `POST /api/r/{room}/byoa/status` | `{ pairing }` | `{ state: "live" \| "offline" \| "unknown", models, limits, skills, capabilities }` — after a reload |
| `POST /api/r/{room}/byoa/release` | `{ pairing }` | `{ released }` — «Disconnect»: `bye`, close, forget |
| `POST /api/r/{room}/byoa/job` | `{ pairing, model, messages, maxTokens?, temperature? }` | `{ id }`, or `{ error }` (`bad-request`, `offline`, `no-text`, `bad-model`, `busy`) with 400/429 |
| `POST /api/r/{room}/byoa/job/poll` | `{ id }` | `{ state: "pending" \| "done" \| "unknown", ok, text, partial, reason }` |
| `POST /api/r/{room}/byoa/job/cancel` | `{ id }` | `{ cancelled }` — «Stop»: the answer is dropped and the wallet is told to stop the call |
| `GET /byoa/ws` (WebSocket) | the wallet's frames | the wallet's channel |

`models` is the wallet's contract shape, one list per skill: `{"text.generate":[{ref,name,id,runs,kind,can?}],"image.generate":[...]}`. It carries every skill the visitor gave the site, not only `text.generate` (CONTRACT.md §3.3); each entry also carries `can`, the wallet's verdicts for the conditions the site asked about in `needs` (booleans only, at most four keys), and an agent entry carries `abilities` the same way. `capabilities` is the wallet's closed list of supported features (`needs`, `can`, `kind`, `cancel`, `image.generate`, `images-in`, `parts`, `frame-limit`); an older wallet sends no `capabilities`, and the page then knows it cannot check `needs` or `can`. The jobs stay text-only for now: `job` asks `text.generate` for a `ref` of the `text.generate` list.

The page asks for an answer by asking again every second and shows `partial` while the model writes: a long call must not hold one
request open behind a proxy. `state` never tells a «no» from a «no wallet».

## The options

`AddByoaRelay(options => …)` — every value has the reference site's default, so a site that sets only `ManifestJson` is complete.

| Option | Default | What it is |
|---|---|---|
| `ManifestJson` | — (required) | Your manifest as JSON text; the links carry it and `welcome` echoes it byte for byte |
| `ConnectCheck` | `false` | Add `"connect": { "check": true }` to the manifest of the links: your page shows the check code |
| `WalletPath` | `/byoa/ws` | Where the wallet dials in |
| `RoomPathPrefix`, `RelayPath` | `/api/r`, `byoa` | The page's endpoints: `{RoomPathPrefix}/{room}/{RelayPath}/…` |
| `MaxCallChars` | `60 000` | The site's own cap on the characters of one call (the same number its manifest states) |
| `MaxCallMessages` | `128` | Messages of one call ([CONTRACT.md](https://github.com/bringyourownagent/bringyourownagent/blob/main/spec/CONTRACT.md) §3.2) |
| `MaxCallTokens`, `DefaultCallTokens`, `DefaultTemperature` | `8 000`, `3000`, `0.7` | What a call may ask for, and what it asks for by default |
| `MaxAnswerChars` | `128 000` | The longest answer the site keeps |
| `MaxModels` | `64` | Participants read out of one skill's list of a grant (at most eight skills are read) |
| `MaxJobRequestBytes` | `1 500 000` | A bigger request body is refused (413) before it is read |
| `CodeLifeSeconds` | `120` | How long a one-time code lives |
| `CodesPerMinute`, `MaxCodes` | `10`, `2000` | Per visitor address, and the whole table |
| `MaxPairings`, `PairingIdleHours` | `1000`, `24` | A full table makes room instead of refusing; idle pairings are forgotten |
| `JobSeconds`, `JobsPerMinute`, `MaxJobsPerRoom`, `MaxJobs` | `300`, `30`, `24`, `2000` | How long a call may take and how many a page may start |
| `HelloSeconds`, `IdleSeconds`, `MessageSeconds`, `SendTimeoutSeconds` | `10`, `75`, `30`, `5` | What a wallet connection may cost the server |

A site that calls the wallet from its own server code takes the channel and calls it directly:

```csharp
var channel = hub.Channel(room, pairing);                       // the live connection of this room's wallet, or null
if (channel is not null && channel.Has("audio.transcribe"))
{
    var answer = await channel.CallAsync("audio.transcribe", input, @params,
                                         maxChars: 4096, timeout: TimeSpan.FromSeconds(60), ct,
                                         onCallId: id => callId = id);   // channel.Cancel(callId) then tells the wallet to stop it
}
```

`channel.Cancel(id)` sends `{"type":"cancel","id":N}` to the wallet (CONTRACT-DIRECT.md §4, wallet 0.36): the wallet stops the call and answers `ok:false`, which the channel drops, and the id comes from `onCallId`. The relay's own `job/cancel` route does the same for a page's job.

`AddByoaRelay` registers the `DirectHub` and the `WalletJobs` as singletons, so a minimal-API handler takes them as parameters and any
other class of yours takes them from its constructor.

## The connect check code

A link can be sent to a visitor by somebody else. `check` — four characters the page shows large while it waits, and the wallet shows in
its consent window — is how the visitor sees that the connection he started is the one his wallet is asked to allow
([CONTRACT-DIRECT.md](https://github.com/bringyourownagent/bringyourownagent/blob/main/spec/CONTRACT-DIRECT.md) §3a):

```csharp
ConnectCheck.Compute(session);      // "K7MQ": SHA-256("BYOA-CHECK-1:" + session), four characters of ABCDEFGHJKMNPQRSTUVWXYZ23456789
```

`code` already carries it. A site that shows it sets `ConnectCheck = true`, and the wallet then requires the visitor to tick that the two
codes match before it allows anything.

## Security notes

- **The room is the boundary.** A code and a pairing belong to the room that asked for the code; `wait`, `status`, `release`, `job`,
  `job/poll` and `job/cancel` all refuse a pairing that is not this room's. Never take a room id from anywhere but the page's own random id.
- **Codes** are 192 random bits, URL-safe, single use, short-lived, rate-limited per visitor address; a code the server did not issue
  answers `bad-session` (a MUST: the wallet's proof that the server issued the link rests on it).
- **Pairings** are 192 random bits and a bearer secret of your server: keep them where script on the page cannot read them if that page can
  run third-party script. One live connection per pairing, the newest wins.
- **Bounded.** Frames from the wallet up to 4 MB (a bigger one ends the connection), one buffer per connection, request bodies over
  `MaxJobRequestBytes` refused before they are read, answers cut at `MaxAnswerChars`, the number of codes, pairings and jobs all capped.
  Set Kestrel's own `MaxRequestBodySize` as well (the reference site uses `1_500_000`) so no endpoint of your app reads more than that.
- **The wallet's text is the visitor's input.** It is cleaned and cut on the server (control characters, bidi overrides, newline runs), and
  your page must still show it with `textContent` and DOM building only — never as markup.
- **Nothing is kept.** No accounts, no database, no logging of message or answer text: a restart forgets every pairing, and a wallet then
  needs a new link.
- **The visitor's address** behind a proxy comes from a header your own proxy sets (`app.UseForwardedHeaders()`), never from one a visitor
  can send: the per-address rate limits are only as good as that.

## Testing with the fake wallet

The test project (`sdk/dotnet/tests/BringYourOwnAgent.Relay.Tests`) carries a pretend wallet — the same one the reference site's console
tool uses — that speaks the whole protocol: hello with a grant, welcome, calls answered in streamed pieces after a delay, a grant change, a
dropped connection. `SiteTests` starts a real site in process (Kestrel on a loopback port), connects that wallet over a real WebSocket and
runs the whole flow:

```sh
dotnet test sdk/dotnet/tests/BringYourOwnAgent.Relay.Tests
```

For a site of your own, point the same wallet at a link your page shows:

```sh
dotnet run --project tools/FakeWallet -- "<the bringyourownagent:// link>"
```

## Links

- Website: <https://bringyourownagent.org>
- Protocol: [CONTRACT-DIRECT.md](https://github.com/bringyourownagent/bringyourownagent/blob/main/spec/CONTRACT-DIRECT.md) (the direct
  link) and [CONTRACT.md](https://github.com/bringyourownagent/bringyourownagent/blob/main/spec/CONTRACT.md) (manifests and skills)
- The reference site that uses this library: ChatToAgents (`tools/FakeWallet` there is the console fake wallet)

## License

[Apache License 2.0](https://github.com/bringyourownagent/bringyourownagent/blob/main/LICENSE).