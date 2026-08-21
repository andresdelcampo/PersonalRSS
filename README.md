# PersonalRSS

PersonalRSS is a self-hosted filtering layer between public RSS/Atom feeds and an existing reader such as The Old Reader. It fetches source feeds, scores articles, stores results in SQLite, and exposes new filtered RSS URLs.

This repository is intentionally an MVP foundation. The first scoring provider is a transparent keyword baseline; its provider interface and stored feedback make it possible to add embeddings or learned ranking later without coupling that work to ingestion or HTTP delivery.

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

Open the address printed by ASP.NET Core. Add one source manually or upload an OPML subscription export from the expandable controls below the source list. Opening the dashboard refreshes every source automatically and reports persistent unread counts; refreshing does not clear them. Select a feed name to open its readable article cards and mark that feed viewed, use **Rename** to choose a friendlier display name, and copy the external-reader RSS URL from the preview. SQLite data is stored in `data/personalrss.db` relative to the web application's content directory.

The preview shows feed-provided images, summary text, relevance scores, and scoring reasons. **More like this** and **Less like this** immediately override the selected article's score and store the vote as future training data. Generated RSS items link back to their PersonalRSS preview because external readers cannot host interactive voting controls.

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
- Feedback immediately includes or excludes the selected article, but does not retrain the baseline provider for future articles yet.
- `EnsureCreated` simplifies first run. Add EF Core migrations before evolving important databases.

## Likely next increments

1. Scheduled refresh with per-feed intervals and conditional HTTP requests.
2. OPML export and source-folder organization.
3. Feedback-aware scoring, then optional local embeddings or BYOK model support.
4. Authentication and SSRF defenses before exposure beyond a trusted LAN.
5. Observability, retention rules, and database migrations.
