<p align="center">
  <img src="docs/personalrss-wordmark.svg" alt="PersonalRSS" width="620">
</p>

<p align="center"><strong>Your feeds, quietly filtered.</strong></p>

PersonalRSS is a private, self-hosted feed reader that learns which stories matter to you. It brings RSS and Atom subscriptions into one calm reading view, scores new posts using an explainable local model, and lets you filter out the noise without sending your reading history to an AI service.

It can also publish each filtered feed as a standard RSS URL, so you can keep using an existing reader such as The Old Reader.

## What you can do

- Add feeds individually or import an OPML subscription export.
- Read inside PersonalRSS using comfortable **Reading**, compact **Scan**, or image-led **Gallery** layouts.
- Choose **Very interested**, **Interested**, **Not interested**, or **Never this topic** to teach the local preference model.
- Set one minimum relevance score that follows you between feeds and survives page refreshes.
- Keep unread state under your control, with optional mark-as-read-on-scroll and explicit per-post or whole-feed actions.
- Expand the full content supplied by a feed without leaving the page.
- Subscribe to PersonalRSS's filtered RSS output from another feed reader.

## How the filtering works

PersonalRSS starts with a transparent keyword baseline, then learns from the feedback you give. It considers recurring words and phrases in titles and summaries, together with exact author and source matches. Every article keeps its baseline, learned score, manual override, and a readable explanation separate, so a vote can always be undone without losing the score it replaced.

The model is deliberately local and lightweight: no cloud LLM, account, API key, or external preference service is required. It is lexical rather than semantic, so it recognizes recurring language and topics rather than hidden conceptual similarity. The scoring-provider interface still leaves room for optional embeddings or model providers later.

## Quick start

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
dotnet restore
dotnet run --project src/PersonalRSS.Web
```

Open the address printed by ASP.NET Core. Add one source manually or upload an OPML subscription export from **Manage sources**, then select a feed name to start reading. The dashboard refreshes sources automatically without clearing unread state. Use **Mark all unread** or **Mark all read** for a whole feed, and manage names or removal from the feed's own view. SQLite data is stored in `data/personalrss.db` relative to the web application's content directory.

The preview defaults to **Unread**. **All** adds 25 older read posts at a time, with **Load 25 older read posts** for further history. A fresh page load orders unread posts first and sorts each read-state group by relevance and publication date; button presses never reshuffle the current page. Each card's **Unread/Read** control persists an individual override. Manually setting a post unread protects it from scroll-based automatic reading until the user explicitly marks it read, votes on it, or uses **Mark all read**. **Mark all unread** resets every stored article in the feed to normal unread status, so scroll-based reading can resume from the beginning. Posts automatically or manually marked read remain in place for the current page session so reading never makes the list jump.

The layout selector keeps the original **Reading** cards and adds two faster image-and-title browsing modes. **Scan** uses a large image on the left with the title on the right, while **Gallery** arranges image-led tiles across the available width. All layouts retain voting, the read-state control, and **Show full feed content**. The **Display** controls can hide voting or reveal the normally hidden **Learning** details—relevance, baseline, and the scoring explanation. Layout and display choices are remembered by the browser, and all three layouts collapse cleanly to a single column on narrow screens.

**Mark read while scrolling** is enabled by default and can be disabled in the feed controls; the preference is remembered by the browser. It marks an unpinned post read only after the end of its card has remained inside the reading viewport for a short interval. The final displayed post uses the bottom of the viewport as its reading boundary because no following card provides additional scrolling room. Voting is also considered deliberate consumption and marks that post read. **Show full feed content** replaces the card's summary and thumbnail in place with the sanitized text, links, images, tables, and code supplied inside the RSS item; when expanded, **Show summary** appears both above and below the full content so a long post can be collapsed from either end. Feeds that publish only an excerpt cannot expose content they did not include.

The preview also shows relevance scores, baseline scores, and scoring reasons. Its unread total deliberately counts every newly stored article across all relevance scores, including articles hidden by the current preview threshold; **Mark all read** clears that whole-feed total. Four mutually exclusive feedback choices—**Very interested**, **Interested**, **Not interested**, and **Never this topic**—supply different positive or negative learning weights and immediately override the selected article's effective score. Click the active choice again to undo it and restore the stored automatic score. The baseline, learned automatic score, and manual effective score are stored separately, so feedback never destroys the score it replaced. A newly rejected article remains visible just long enough to undo the click; changing the score filter or leaving the page removes that temporary exception and applies filtering normally. Generated RSS items link back to their PersonalRSS preview because external readers cannot host interactive voting controls.

The local model learns from words and adjacent phrases in titles and summaries plus exact author and source matches. It ignores common words, gives strong feedback twice the weight of ordinary feedback, caps its adjustment, and explains the strongest matching evidence on every scored article. Refreshing a feed scores all fetched articles against one feedback snapshot, so the learning step does not make a database query for every article.

OPML imports preserve feed titles, accept nested folder exports, and are safe to repeat. Existing and repeated feed URLs are skipped rather than duplicated. Folder names are parsed for forward compatibility but are not stored in the current schema.

## Replace the live Windows instance

Double-click `Deploy-PersonalRSS.cmd`, or run the checked-in deployment script from a normal Windows PowerShell session with outbound network access:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Restart-PersonalRSS.ps1
```

The script checks outbound HTTPS before touching the current process, publishes Release files to staging, verifies that the listener on `127.0.0.1:5187` is the expected PersonalRSS executable, replaces that process, waits for `/health`, and refreshes every configured feed. It reports success only when every refresh succeeds and no feed retains a `lastError`.

Do not launch the live instance from a restricted or sandboxed process. Such an instance can return `200` from `/health` while every feed refresh fails with Windows socket error `10013`. When Codex performs the replacement, the restart command must therefore be approved to run outside its workspace sandbox. A successful deployment requires both the health check and the all-feed refresh verification; `/health` alone is not sufficient.

## Docker

```powershell
docker compose up --build
```

Open <http://localhost:8080>. A named volume persists the database.

## Under the hood

- `PersonalRSS.Core` — domain models with no infrastructure dependencies.
- `PersonalRSS.Application` — ports and the feed-refresh use case.
- `PersonalRSS.Infrastructure` — SQLite/EF Core, RSS/Atom ingestion, local scoring, and RSS rendering.
- `PersonalRSS.Web` — the ASP.NET Core interface and HTTP API.
- `PersonalRSS.Tests` — focused behavior and scoring tests.

Dependency direction: `Web -> Infrastructure -> Application -> Core`.

## Initial HTTP surface

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/` | Management dashboard |
| `GET` | `/preview/{slug}` | Readable article cards and feedback controls |
| `GET` | `/health` | Health check |
| `GET` / `POST` | `/api/feeds` | List or add sources |
| `PUT` | `/api/feeds/{id}` | Rename a source without changing its stable RSS URL |
| `DELETE` | `/api/feeds/{id}` | Remove a source and its stored articles and feedback |
| `POST` | `/api/feeds/import/opml` | Upload an OPML subscription list |
| `POST` | `/api/feeds/{id}/refresh` | Fetch and score now |
| `POST` | `/api/feeds/{id}/viewed` | Mark the feed viewed for unread counting |
| `POST` | `/api/feeds/{id}/unread` | Mark all stored posts in a feed unread |
| `GET` | `/api/articles?feedId=&minScore=&limit=` | Inspect scored articles |
| `POST` | `/api/articles/{id}/feedback` | Set or clear one of the four feedback choices |
| `PUT` | `/api/articles/{id}/read-state` | Set one article read or unread |
| `POST` | `/api/articles/read` | Mark a collection of articles read |
| `GET` | `/feeds/{slug}.xml?minScore=0.5` | Reader-compatible filtered RSS |

## MVP boundaries

- Single-user/trusted-network design; authentication is not implemented.
- Refresh runs when the management dashboard loads. Background scheduling follows after ingestion proves reliable.
- Common RSS 2.0 and Atom are supported; unusual extensions may need dedicated handling.
- OPML folder names are not displayed yet; all imported feeds appear in one source list.
- The local learner is deliberately lightweight and lexical. It understands recurring terms and phrases but does not yet provide embedding-based semantic similarity.
- `EnsureCreated` simplifies first run. Add EF Core migrations before evolving important databases.
