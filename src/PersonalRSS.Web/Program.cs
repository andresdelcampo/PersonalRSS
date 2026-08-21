using System.Text.RegularExpressions;
using PersonalRSS.Application;
using PersonalRSS.Core;
using PersonalRSS.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddPersonalRssInfrastructure(builder.Configuration);
var app = builder.Build();
app.UseExceptionHandler();

using (var scope = app.Services.CreateScope())
{
    var repository = scope.ServiceProvider.GetRequiredService<IFeedRepository>();
    var connectionString = builder.Configuration.GetConnectionString("PersonalRss") ?? "Data Source=data/personalrss.db";
    if (connectionString.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(connectionString["Data Source=".Length..]));
        if (directory is not null) Directory.CreateDirectory(directory);
    }
    await repository.InitializeAsync();
}

app.MapGet("/", () => Results.Content(Dashboard.Html, "text/html"));
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "PersonalRSS" }));
var api = app.MapGroup("/api");
api.MapGet("/feeds", async (IFeedRepository repository, CancellationToken ct) => Results.Ok(await repository.GetFeedsAsync(ct)));
api.MapPost("/feeds", async (CreateFeedRequest request, IFeedRepository repository, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Name) || !Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["feed"] = ["Name and an absolute HTTP(S) feed URL are required."] });
    var slug = Slugify(request.Slug ?? request.Name);
    if (string.IsNullOrWhiteSpace(slug)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["slug"] = ["A usable name or slug is required."] });
    if (await repository.GetFeedBySlugAsync(slug, ct) is not null) return Results.Conflict(new { error = $"The slug '{slug}' already exists." });
    var feed = new FeedSource { Name = request.Name.Trim(), Slug = slug, Url = uri.ToString() };
    await repository.AddFeedAsync(feed, ct);
    return Results.Created($"/api/feeds/{feed.Id}", feed);
});
api.MapPost("/feeds/import/opml", async (IFormFile file, FeedImportService service, CancellationToken ct) =>
{
    if (file.Length == 0) return Results.BadRequest(new { error = "Choose a non-empty OPML file." });
    if (file.Length > 5 * 1024 * 1024) return Results.BadRequest(new { error = "OPML files are limited to 5 MB." });
    try
    {
        await using var content = file.OpenReadStream();
        return Results.Ok(await service.ImportAsync(content, ct));
    }
    catch (InvalidDataException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).DisableAntiforgery();
api.MapPost("/feeds/{id:guid}/refresh", async (Guid id, FeedRefreshService service, CancellationToken ct) =>
{
    try { return Results.Ok(await service.RefreshAsync(id, ct)); } catch (KeyNotFoundException) { return Results.NotFound(); }
});
api.MapGet("/articles", async (Guid? feedId, double? minScore, int? limit, IFeedRepository repository, CancellationToken ct) =>
    Results.Ok(await repository.GetArticlesAsync(feedId, Math.Clamp(minScore ?? 0.5, 0, 1), limit ?? 100, ct)));
api.MapPost("/articles/{id:guid}/feedback", async (Guid id, FeedbackRequest request, IFeedRepository repository, CancellationToken ct) =>
{
    if (!Enum.TryParse<FeedbackKind>(request.Kind, true, out var kind)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["kind"] = ["Use 'Interested' or 'NotInterested'."] });
    if (await repository.GetArticleAsync(id, ct) is null) return Results.NotFound();
    await repository.AddFeedbackAsync(new ArticleFeedback { ArticleId = id, Kind = kind }, ct);
    return Results.Accepted();
});
app.MapGet("/feeds/{slug}.xml", async (string slug, double? minScore, HttpRequest request, IFeedRepository repository, IFilteredFeedRenderer renderer, CancellationToken ct) =>
{
    var feed = await repository.GetFeedBySlugAsync(slug, ct);
    if (feed is null) return Results.NotFound();
    var articles = await repository.GetArticlesAsync(feed.Id, Math.Clamp(minScore ?? 0.5, 0, 1), 200, ct);
    var uri = new Uri($"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}{request.QueryString}");
    return Results.Content(renderer.Render(feed, articles, uri), "application/rss+xml; charset=utf-8");
});

app.Run();
static string Slugify(string value) => Regex.Replace(value.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
public sealed record CreateFeedRequest(string Name, string Url, string? Slug);
public sealed record FeedbackRequest(string Kind);

internal static class Dashboard
{
    public const string Html = """
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>PersonalRSS</title>
<style>body{font:16px system-ui;max-width:900px;margin:3rem auto;padding:0 1rem;color:#17212b;background:#f5f7f9}main{background:white;padding:2rem;border-radius:14px;box-shadow:0 8px 30px #1b2a3812}h1{margin-top:0}form{display:grid;grid-template-columns:1fr 2fr auto;gap:.7rem}.import{grid-template-columns:1fr auto;margin-top:.7rem;padding-top:.7rem;border-top:1px solid #e1e7eb}input,button{font:inherit;padding:.7rem;border:1px solid #bbc5cf;border-radius:8px}button{background:#126a5b;color:white;border:0;cursor:pointer}button:disabled{opacity:.6;cursor:wait}li{margin:.8rem 0}small{color:#607080}.message{min-height:1.5rem}@media(max-width:650px){form,.import{grid-template-columns:1fr}}</style></head>
<body><main><h1>PersonalRSS</h1><p>Your self-hosted relevance layer for existing RSS readers.</p><form id="add"><input name="name" placeholder="Feed name" required><input name="url" type="url" placeholder="https://example.com/feed.xml" required><button>Add feed</button></form><form id="import" class="import"><input name="file" type="file" accept=".opml,.xml,text/x-opml,application/xml,text/xml" required><button>Import OPML</button></form><p id="message" class="message" aria-live="polite"></p><h2>Sources</h2><ul id="feeds"><li>Loading…</li></ul><p><small>MVP: scoring uses the configurable keyword baseline. OPML imports are safe to repeat; existing feed URLs are skipped.</small></p></main>
<script>const list=document.querySelector('#feeds'),message=document.querySelector('#message');async function load(){const feeds=await fetch('/api/feeds').then(r=>r.json());list.innerHTML=feeds.length?'':'<li>No feeds yet.</li>';for(const f of feeds){const li=document.createElement('li');li.innerHTML=`<strong>${esc(f.name)}</strong> — <a href="/feeds/${encodeURIComponent(f.slug)}.xml">filtered feed</a> <button data-id="${f.id}">Refresh now</button><br><small>${esc(f.url)}${f.lastError?' · Error: '+esc(f.lastError):''}</small>`;list.append(li)}list.querySelectorAll('button').forEach(b=>b.onclick=async()=>{b.disabled=true;message.textContent='Refreshing…';const r=await fetch(`/api/feeds/${b.dataset.id}/refresh`,{method:'POST'});message.textContent=r.ok?'Refresh complete.':'Refresh failed.';b.disabled=false;load()})}document.querySelector('#add').onsubmit=async e=>{e.preventDefault();const d=new FormData(e.target);const r=await fetch('/api/feeds',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({name:d.get('name'),url:d.get('url')})});message.textContent=r.ok?'Feed added.':await errorMessage(r,'Could not add feed.');if(r.ok)e.target.reset();load()};document.querySelector('#import').onsubmit=async e=>{e.preventDefault();const button=e.target.querySelector('button');button.disabled=true;message.textContent='Importing subscriptions…';const r=await fetch('/api/feeds/import/opml',{method:'POST',body:new FormData(e.target)});if(r.ok){const result=await r.json();message.textContent=`Import complete: ${result.added} added, ${result.skipped} already present, ${result.invalid} invalid.`;e.target.reset();load()}else message.textContent=await errorMessage(r,'Could not import that OPML file.');button.disabled=false};async function errorMessage(r,fallback){try{const body=await r.json();return body.error??fallback}catch{return fallback}}function esc(s){const d=document.createElement('div');d.textContent=s??'';return d.innerHTML}load();</script></body></html>
""";
}
