# PersonalRSS

PersonalRSS is a self-hosted filtering layer between public RSS/Atom feeds and an existing reader such as The Old Reader. It fetches source feeds, scores articles, stores results in SQLite, and exposes new filtered RSS URLs.

PersonalRSS combines a transparent keyword baseline with an entirely local preference model. Feedback teaches weighted terms, phrases, authors, and sources; related future articles receive an explainable adjustment without calling an LLM or another service. The provider interface still leaves room for optional embeddings or model providers later without coupling them to ingestion or HTTP delivery.

## Architecture

- `PersonalRSS.Core` — domain models with no infrastructure dependencies.
- `PersonalRSS.Application` — ports and the feed-refresh use case.
- `PersonalRSS.Infrastructure` — SQLite/EF Core, RSS/Atom ingestion, baseline scoring, and RSS rendering.
- `PersonalRSS.Web` — minimal ASP.NET Core dashboard and API.
- `PersonalRSS.Tests` — focused scoring tests.

Dependency direction: `Web -> Infrastructure -> Application -> Core`.

## Run locally

Requires the .NET 8 SDK.

```powershell
dotnet restore
dotnet run --project src/PersonalRSS.Web
```

Open the address printed by ASP.NET Core. Add one source manually or upload an OPML subscription export from the expandable controls below the source list. Opening the dashboard refreshes every source automatically and reports persistent unread counts; refreshing and opening a feed do not clear them. Select a feed name to open its readable article cards, use **Mark all read** in the feed's top actions when you want to clear its unread count, use **Rename** to choose a friendlier display name, and copy the external-reader RSS URL from the preview. SQLite data is stored in `data/personalrss.db` relative to the web application's content directory.

The preview gives every post a prominent **Unread** or **Read** badge and a matching card treatment. Unread posts appear first; each read-state group is ordered by relevance and then publication date. That state uses the feed's explicit last-viewed marker: opening or expanding a post does not silently mark it read, while **Mark all read** updates every currently stored post. Each card also has an expandable **Show full feed content** section that renders the sanitized text, links, images, tables, and code supplied inside the RSS item without navigating or reloading; feeds that publish only an excerpt cannot expose content they did not include.

The preview also shows relevance scores, baseline scores, and scoring reasons. Its unread total deliberately counts every newly stored article across all relevance scores, including articles hidden by the current preview threshold; **Mark all read** clears that whole-feed total. Four mutually exclusive feedback choices—**Very interested**, **Interested**, **Not interested**, and **Never this topic**—supply different positive or negative learning weights and immediately override the selected article's effective score. Click the active choice again to undo it and restore the stored automatic score. The baseline, learned automatic score, and manual effective score are stored separately, so feedback never destroys the score it replaced. A newly rejected article remains visible just long enough to undo the click; changing the score filter or leaving the page removes that temporary exception and applies filtering normally. Generated RSS items link back to their PersonalRSS preview because external readers cannot host interactive voting controls.

The local model learns from words and adjacent phrases in titles and summaries plus exact author and source matches. It ignores common words, gives strong feedback twice the weight of ordinary feedback, caps its adjustment, and explains the strongest matching evidence on every scored article. Refreshing a feed scores all fetched articles against one feedback snapshot, so the learning step does not make a database query for every article.

OPML imports preserve feed titles, accept nested folder exports, and are safe to repeat. Existing and repeated feed URLs are skipped rather than duplicated. Folder names are parsed for forward compatibility but are not stored in the current schema.

## Docker

```powershell
docker compose up --build
```

Open <http://localhost:8080>. A named volume persists the database.

## Initial HTTP surface

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/` | Management dashboard |
| `GET` | `/preview/{slug}` | Readable article cards and feedback controls |
| `GET` | `/health` | Health check |
| `GET` / `POST` | `/api/feeds` | List or add sources |
| `PUT` | `/api/feeds/{id}` | Rename a source without changing its stable RSS URL |
| `POST` | `/api/feeds/import/opml` | Upload an OPML subscription list |
| `POST` | `/api/feeds/{id}/refresh` | Fetch and score now |
| `POST` | `/api/feeds/{id}/viewed` | Mark the feed viewed for unread counting |
| `GET` | `/api/articles?feedId=&minScore=&limit=` | Inspect scored articles |
| `POST` | `/api/articles/{id}/feedback` | Store `Interested` or `NotInterested` |
| `GET` | `/feeds/{slug}.xml?minScore=0.5` | Reader-compatible filtered RSS |

## MVP boundaries

- Single-user/trusted-network design; authentication is not implemented.
- Refresh runs when the management dashboard loads. Background scheduling follows after ingestion proves reliable.
- Common RSS 2.0 and Atom are supported; unusual extensions may need dedicated handling.
- OPML folder names are not displayed yet; all imported feeds appear in one source list.
- The local learner is deliberately lightweight and lexical. It understands recurring terms and phrases but does not yet provide embedding-based semantic similarity.
- `EnsureCreated` simplifies first run. Add EF Core migrations before evolving important databases.

## Likely next increments

1. Scheduled refresh with per-feed intervals and conditional HTTP requests.
2. OPML export and source-folder organization.
3. Confidence-aware scoring, explicit High/Maybe/Filtered feeds, and preference inspection/editing.
4. Authentication and SSRF defenses before exposure beyond a trusted LAN.
5. Observability, retention rules, and full EF Core migrations. Startup currently performs narrow compatibility upgrades for existing MVP databases.
