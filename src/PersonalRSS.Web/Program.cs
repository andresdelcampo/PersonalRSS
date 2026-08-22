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
app.MapGet("/preview/{slug}", () => Results.Content(PreviewPage.Html, "text/html"));
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "PersonalRSS" }));
var api = app.MapGroup("/api");
api.MapGet("/feeds", async (IFeedRepository repository, CancellationToken ct) =>
{
    var feeds = await repository.GetFeedsAsync(ct);
    var unreadCounts = await repository.GetUnreadCountsAsync(ct);
    return Results.Ok(feeds.Select(feed => new FeedSummary(feed.Id, feed.Name, feed.Slug, feed.Url, feed.LastRefreshedAt, feed.LastViewedAt, feed.LastError, unreadCounts.GetValueOrDefault(feed.Id))));
});
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
api.MapPut("/feeds/{id:guid}", async (Guid id, RenameFeedRequest request, IFeedRepository repository, CancellationToken ct) =>
{
    var name = request.Name?.Trim();
    if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Enter a feed name of 200 characters or fewer."] });
    var feed = await repository.GetFeedAsync(id, ct);
    if (feed is null) return Results.NotFound();
    feed.Name = name;
    await repository.SaveFeedAsync(feed, ct);
    return Results.Ok(feed);
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
api.MapPost("/feeds/{id:guid}/viewed", async (Guid id, IFeedRepository repository, CancellationToken ct) =>
    await repository.MarkFeedViewedAsync(id, DateTimeOffset.UtcNow, ct) ? Results.NoContent() : Results.NotFound());
api.MapGet("/articles", async (Guid? feedId, double? minScore, int? limit, IFeedRepository repository, CancellationToken ct) =>
    Results.Ok(await repository.GetArticlesAsync(feedId, Math.Clamp(minScore ?? 0.5, 0, 1), limit ?? 100, ct)));
api.MapPost("/articles/{id:guid}/feedback", async (Guid id, FeedbackRequest request, IFeedRepository repository, CancellationToken ct) =>
{
    if (!Enum.TryParse<FeedbackKind>(request.Kind, true, out var kind) || !Enum.IsDefined(kind))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["kind"] = ["Use 'VeryInterested', 'Interested', 'NotInterested', or 'NeverThisTopic'."] });
    var article = await repository.GetArticleAsync(id, ct);
    if (article is null) return Results.NotFound();
    var current = (await repository.GetArticlesAsync(article.FeedSourceId, 0, 500, ct)).SingleOrDefault(x => x.Id == id)?.ActiveFeedback;
    if (current == kind)
    {
        await repository.ClearFeedbackAsync(id, ct);
        await repository.SetArticleReadStateAsync(id, false, false, DateTimeOffset.UtcNow, ct);
        return Results.Ok(new { activeFeedback = (FeedbackKind?)null, isUnread = false });
    }
    await repository.SetFeedbackAsync(id, kind, ct);
    await repository.SetArticleReadStateAsync(id, false, false, DateTimeOffset.UtcNow, ct);
    return Results.Ok(new { activeFeedback = kind, isUnread = false });
});
api.MapPut("/articles/{id:guid}/read-state", async (Guid id, ReadStateRequest request, IFeedRepository repository, CancellationToken ct) =>
{
    if (request.Automatic && request.IsUnread) return Results.BadRequest(new { error = "Automatic updates can only mark articles read." });
    var article = await repository.GetArticleAsync(id, ct);
    if (article is null) return Results.NotFound();
    if (request.Automatic && article.IsUnreadPinned) return Results.Ok(new { changed = false, isUnread = true, isUnreadPinned = true });
    await repository.SetArticleReadStateAsync(id, request.IsUnread, request.Automatic, DateTimeOffset.UtcNow, ct);
    return Results.Ok(new { changed = true, isUnread = request.IsUnread, isUnreadPinned = request.IsUnread });
});
api.MapPost("/articles/read", async (MarkArticlesReadRequest request, IFeedRepository repository, CancellationToken ct) =>
{
    var ids = request.ArticleIds?.Distinct().Take(500).ToArray() ?? [];
    if (ids.Length == 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["articleIds"] = ["Choose at least one article."] });
    var changed = await repository.MarkArticlesReadAsync(ids, request.Automatic, DateTimeOffset.UtcNow, ct);
    return Results.Ok(new { changed });
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
public sealed record RenameFeedRequest(string? Name);
public sealed record FeedbackRequest(string Kind);
public sealed record ReadStateRequest(bool IsUnread, bool Automatic = false);
public sealed record MarkArticlesReadRequest(IReadOnlyList<Guid>? ArticleIds, bool Automatic = false);
public sealed record FeedSummary(Guid Id, string Name, string Slug, string Url, DateTimeOffset? LastRefreshedAt, DateTimeOffset? LastViewedAt, string? LastError, int UnreadCount);

internal static class Dashboard
{
    public const string Html = """
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>PersonalRSS</title>
<style>body{font:16px system-ui;max-width:900px;margin:3rem auto;padding:0 1rem;color:#17212b;background:#f5f7f9}main{background:white;padding:2rem;border-radius:14px;box-shadow:0 8px 30px #1b2a3812}h1{margin-top:0}h2{margin-bottom:.4rem}a{color:#126a5b}#feeds{list-style:none;padding:0}.source-item{margin:.65rem 0;padding:.8rem .9rem;border-left:4px solid #d3dce1;border-radius:8px;background:#f8fafb}.source-item.has-unread{border-left-color:#16866f;background:#edf8f4;box-shadow:0 2px 10px #126a5b0b}.feed-name{font-size:1.05rem;text-decoration-thickness:1px;text-underline-offset:2px}.has-unread .feed-name{color:#0b5b4c;font-weight:750}.is-read .feed-name{color:#52636d;font-weight:550}.unread-count{display:inline-block;margin-left:.4rem;padding:.15rem .48rem;border-radius:999px;font-size:.82rem}.has-unread .unread-count{background:#126a5b;color:white;font-weight:750;box-shadow:0 1px 4px #126a5b33}.is-read .unread-count{background:#e8edef;color:#687680;font-weight:500}form{display:grid;grid-template-columns:1fr 2fr auto;gap:.7rem;margin-top:.8rem}#import{grid-template-columns:1fr auto}input,select,button{font:inherit;padding:.7rem;border:1px solid #bbc5cf;border-radius:8px}select{min-width:0;background:white}button{background:#126a5b;color:white;border:0;cursor:pointer}button:disabled{opacity:.6;cursor:wait}small{color:#607080}.message{min-height:1.5rem;margin:.25rem 0}.manage{margin-top:2rem;padding-top:1rem;border-top:1px solid #e1e7eb}.manage h2{font-size:1.1rem}details{margin:.65rem 0;border:1px solid #d8e0e5;border-radius:9px;background:#f8fafb}summary{padding:.7rem .9rem;color:#174d44;font-weight:600;cursor:pointer}details form{padding:0 .9rem .9rem;margin-top:.1rem}@media(max-width:650px){form,#import{grid-template-columns:1fr}}</style></head>
<body><main><h1>PersonalRSS</h1><p>Your self-hosted relevance layer for existing RSS readers.</p><h2>Sources</h2><p id="message" class="message" aria-live="polite"></p><ul id="feeds"><li>Loading…</li></ul><section class="manage"><h2>Manage sources</h2><details><summary>Add a feed</summary><form id="add"><input name="name" placeholder="Feed name" required><input name="url" type="url" placeholder="https://example.com/feed.xml" required><button>Add feed</button></form></details><details><summary>Import an OPML file</summary><form id="import"><input name="file" type="file" accept=".opml,.xml,text/x-opml,application/xml,text/xml" required><button>Import OPML</button></form></details><details><summary>Rename a feed</summary><form id="rename"><select name="id" aria-label="Feed to rename" required><option value="">Loading feeds…</option></select><input name="name" aria-label="New feed name" placeholder="New feed name" maxlength="200" required><button>Rename feed</button></form></details></section><p><small>Scoring combines the configured keyword baseline with a local, explainable model learned from your feedback. No external model or service is used.</small></p></main>
<script>
const list=document.querySelector('#feeds'),message=document.querySelector('#message');
const renameForm=document.querySelector('#rename'),renameSelect=renameForm.querySelector('select'),renameButton=renameForm.querySelector('button');
function syncRenameOptions(feeds){const selected=renameSelect.value;renameSelect.replaceChildren();if(!feeds.length){const option=document.createElement('option');option.textContent='No feeds available';option.value='';renameSelect.append(option);renameButton.disabled=true;return}for(const feed of feeds){const option=document.createElement('option');option.value=feed.id;option.textContent=feed.name;renameSelect.append(option)}if(feeds.some(feed=>feed.id===selected))renameSelect.value=selected;renameButton.disabled=false}
function renderFeeds(feeds){syncRenameOptions(feeds);list.replaceChildren();if(!feeds.length){list.innerHTML='<li>No feeds yet.</li>';return}for(const f of feeds){const li=document.createElement('li'),count=f.unreadCount??0;li.className=`source-item ${count>0?'has-unread':'is-read'}`;li.innerHTML=`<a class="feed-name" href="/preview/${encodeURIComponent(f.slug)}">${esc(f.name)}</a><span class="unread-count">${count} unread</span><br><small>${esc(f.url)}${f.lastError?' · Error: '+esc(f.lastError):''}</small>`;list.append(li)}}
async function getFeeds(){const response=await fetch('/api/feeds');if(!response.ok)throw new Error('Could not load feeds.');return response.json()}
async function refreshFeed(feed){try{const response=await fetch(`/api/feeds/${feed.id}/refresh`,{method:'POST'});return{ok:response.ok}}catch{return{ok:false}}}
async function refreshFeeds(feeds){const results=new Array(feeds.length),queue=feeds.map((feed,index)=>({feed,index}));async function worker(){while(queue.length){const item=queue.shift();if(item)results[item.index]=await refreshFeed(item.feed)}}await Promise.all(Array.from({length:Math.min(4,feeds.length)},worker));return results}
async function loadAndRefresh(){let feeds=await getFeeds();renderFeeds(feeds);if(!feeds.length){message.textContent='';return}message.textContent=`Refreshing ${feeds.length} ${feeds.length===1?'feed':'feeds'}…`;const results=await refreshFeeds(feeds);feeds=await getFeeds();renderFeeds(feeds);const total=feeds.reduce((sum,feed)=>sum+(feed.unreadCount??0),0),failed=results.filter(result=>!result.ok).length;message.textContent=`Refresh complete: ${total} unread ${total===1?'post':'posts'}${failed?`; ${failed} ${failed===1?'feed':'feeds'} failed`:''}.`}
renameForm.onsubmit=async e=>{e.preventDefault();const data=new FormData(e.target),id=data.get('id'),name=data.get('name');renameButton.disabled=true;const response=await fetch(`/api/feeds/${id}`,{method:'PUT',headers:{'content-type':'application/json'},body:JSON.stringify({name})});message.textContent=response.ok?'Feed renamed.':await errorMessage(response,'Could not rename feed.');if(response.ok){e.target.elements.name.value='';renderFeeds(await getFeeds())}else renameButton.disabled=false};
document.querySelector('#add').onsubmit=async e=>{e.preventDefault();const d=new FormData(e.target);const r=await fetch('/api/feeds',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({name:d.get('name'),url:d.get('url')})});message.textContent=r.ok?'Feed added.':await errorMessage(r,'Could not add feed.');if(r.ok){e.target.reset();await loadAndRefresh()}};
document.querySelector('#import').onsubmit=async e=>{e.preventDefault();const button=e.target.querySelector('button');button.disabled=true;message.textContent='Importing subscriptions…';const r=await fetch('/api/feeds/import/opml',{method:'POST',body:new FormData(e.target)});if(r.ok){const result=await r.json();message.textContent=`Import complete: ${result.added} added, ${result.skipped} already present, ${result.invalid} invalid.`;e.target.reset();await loadAndRefresh()}else message.textContent=await errorMessage(r,'Could not import that OPML file.');button.disabled=false};
async function errorMessage(r,fallback){try{const body=await r.json();return body.error??Object.values(body.errors??{}).flat()[0]??fallback}catch{return fallback}}function esc(s){const d=document.createElement('div');d.textContent=s??'';return d.innerHTML}
loadAndRefresh().catch(error=>{message.textContent=error.message;console.error(error)});
</script></body></html>
""";
}

internal static class PreviewPage
{
    public const string Html = """
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Article preview — PersonalRSS</title>
<style>
:root{color-scheme:light}*{box-sizing:border-box}body{font:16px system-ui;margin:0;color:#17212b;background:#f3f6f7}header{position:sticky;top:0;z-index:2;background:#ffffffee;backdrop-filter:blur(10px);border-bottom:1px solid #dbe3e7}header>div,main{max-width:1050px;margin:auto;padding:1rem 1.25rem}.top{display:flex;align-items:center;gap:.7rem;flex-wrap:wrap}h1{font-size:1.5rem;margin:0 auto 0 0}a{color:#126a5b}button,input{font:inherit}button{padding:.55rem .75rem;border:0;border-radius:8px;background:#126a5b;color:white;cursor:pointer}button.secondary{background:#e7eeec;color:#174d44}button.negative{background:#f4e7e5;color:#7a2f26}button.secondary[aria-pressed="true"]{background:#126a5b;color:white;box-shadow:0 0 0 3px #126a5b33}button.negative[aria-pressed="true"]{background:#a33a2d;color:white;box-shadow:0 0 0 3px #a33a2d33}button:disabled{opacity:.55;cursor:wait}.controls{display:flex;align-items:center;gap:.7rem 1rem;flex-wrap:wrap;margin-top:.8rem}.view-toggle{display:flex;gap:.35rem}.view-toggle button{padding:.38rem .65rem}.auto-read{display:flex;align-items:center;gap:.35rem}.status{min-height:1.5rem;color:#52636d}.cards{display:grid;gap:1rem}.card{display:grid;grid-template-columns:minmax(0,1fr) 230px;gap:1rem;background:white;border:1px solid #dce4e8;border-radius:14px;padding:1.1rem;box-shadow:0 6px 20px #23323d0b}.card.is-unread{border-left:5px solid #16866f;box-shadow:0 7px 24px #126a5b17}.card.is-read{background:#f9fbfb;border-color:#dfe6e9}.card.no-image,.card.is-expanded{grid-template-columns:1fr}.card.is-expanded>.image{display:none}.card h2{font-size:1.2rem;line-height:1.3;margin:.15rem 0}.meta{display:flex;align-items:center;gap:.45rem;flex-wrap:wrap;color:#64747d;font-size:.88rem}.read-state{display:inline-block;padding:.16rem .5rem;border:0;border-radius:999px;font-size:.75rem;font-weight:750;letter-spacing:.03em;text-transform:uppercase;cursor:pointer}.read-state[aria-pressed="true"]{background:#126a5b;color:white}.read-state[aria-pressed="false"]{background:#e3e9eb;color:#5f6d75;box-shadow:none}.is-read h2 a{color:#50616a}.summary{line-height:1.55;color:#33434c;display:-webkit-box;-webkit-line-clamp:5;-webkit-box-orient:vertical;overflow:hidden}.summary[hidden],.full-content[hidden]{display:none}.content-toggle{display:block;background:transparent;color:#126a5b;padding:.2rem 0;margin:.15rem 0 0;border-radius:0;font-weight:700;text-align:left}.reason{font-size:.85rem;color:#64747d}.actions{display:flex;gap:.35rem;flex-wrap:wrap}.actions button{padding:.38rem .58rem;border-radius:7px;font-size:.9rem}.image{width:100%;height:165px;object-fit:cover;border-radius:10px;background:#e9eeef}.full-content{max-width:760px;line-height:1.65;color:#273841}.full-content img{display:block;max-width:100%;height:auto;margin:1rem auto;border-radius:8px}.full-content table{display:block;max-width:100%;overflow-x:auto;border-collapse:collapse}.full-content th,.full-content td{padding:.35rem .55rem;border:1px solid #d7e0e4}.full-content pre{max-width:100%;overflow-x:auto;padding:.8rem;background:#eef2f3;border-radius:8px}.full-content blockquote{margin-left:0;padding-left:1rem;border-left:3px solid #b8c8ce;color:#53656e}.read-sentinel{grid-column:1/-1;height:1px}.load-older{display:block;margin:1rem auto 0}.load-older[hidden]{display:none}.empty{background:white;padding:2rem;border-radius:14px;text-align:center;color:#607080}@media(max-width:700px){.card{grid-template-columns:1fr}.image{order:-1;height:210px}}
</style></head><body><header><div><div class="top"><a href="/">← Sources</a><h1 id="title">Article preview</h1><button id="refresh">Refresh source</button><button id="mark-visible" class="secondary" disabled>Mark visible read</button><button id="mark-read" class="secondary" disabled>Mark all read</button><button id="copy" class="secondary">Copy RSS URL</button></div><div class="controls"><div class="view-toggle" role="group" aria-label="Posts to show"><button id="show-unread" class="secondary" aria-pressed="true">Unread</button><button id="show-all" class="secondary" aria-pressed="false">All posts</button></div><label>Minimum score <strong id="score-label">0.5</strong> <input id="threshold" type="range" min="0" max="1" value="0.5" step="0.1"></label><label class="auto-read"><input id="auto-read" type="checkbox" checked> Mark read while scrolling</label></div><div id="status" class="status" aria-live="polite"></div></div></header><main><div id="cards" class="cards"><div class="empty">Loading articles…</div></div><button id="load-older" class="secondary load-older" hidden>Load 25 older read posts</button></main>
<script>
const slug=decodeURIComponent(location.pathname.substring('/preview/'.length));
const cards=document.querySelector('#cards'),status=document.querySelector('#status'),title=document.querySelector('#title');
const threshold=document.querySelector('#threshold'),scoreLabel=document.querySelector('#score-label'),markRead=document.querySelector('#mark-read'),markVisible=document.querySelector('#mark-visible');
const showUnread=document.querySelector('#show-unread'),showAll=document.querySelector('#show-all'),autoRead=document.querySelector('#auto-read'),loadOlder=document.querySelector('#load-older');
let feed,articles=[],temporarilyVisible=new Set(),expandedArticles=new Set(),sessionRead=new Set(),displayOrder=new Map(),renderedArticleIds=[],viewMode='unread',readLimit=25;
let readObserver,autoFlushTimer;const readTimers=new Map(),autoQueue=new Set();
async function start(){autoRead.checked=localStorage.getItem('personalrss.autoRead')!=='false';await refreshFeedState();if(!feed){cards.innerHTML='<div class="empty">Feed not found.</div>';return}title.textContent=feed.name;document.title=`${feed.name} — PersonalRSS`;await load(true)}
async function refreshFeedState(){const feeds=await fetch('/api/feeds').then(r=>r.json());feed=feeds.find(f=>f.slug===slug);updateMarkReadAction()}
async function load(resetOrder=false){articles=await fetch(`/api/articles?feedId=${encodeURIComponent(feed.id)}&minScore=0&limit=500`).then(r=>r.json());prepareDisplayOrder(resetOrder);render()}
function updateMarkReadAction(){const unread=feed?.unreadCount??0;markRead.textContent=unread>0?`Mark all read (${unread})`:'All read';markRead.disabled=!feed||unread===0}
function isUnread(article){return article.isUnread===true}
function compareArticles(a,b){const unreadOrder=Number(isUnread(b))-Number(isUnread(a));if(unreadOrder)return unreadOrder;const relevanceOrder=Number(b.score)-Number(a.score);if(relevanceOrder)return relevanceOrder;return new Date(b.publishedAt)-new Date(a.publishedAt)}
function prepareDisplayOrder(reset){if(reset)displayOrder.clear();const ordered=[...articles].sort(compareArticles);let next=displayOrder.size;for(const article of ordered)if(!displayOrder.has(article.id))displayOrder.set(article.id,next++)}
function displaySort(a,b){return (displayOrder.get(a.id)??Number.MAX_SAFE_INTEGER)-(displayOrder.get(b.id)??Number.MAX_SAFE_INTEGER)}
function matchingArticles(){const minimum=Number(threshold.value);return articles.filter(article=>article.score>=minimum||temporarilyVisible.has(article.id)).sort(displaySort)}
function visibleArticles(){const matching=matchingArticles();if(viewMode==='unread')return matching.filter(article=>isUnread(article)||sessionRead.has(article.id));const always=matching.filter(article=>isUnread(article)||sessionRead.has(article.id)),older=matching.filter(article=>!isUnread(article)&&!sessionRead.has(article.id)).slice(0,readLimit),ids=new Set(always.map(article=>article.id));return [...always,...older.filter(article=>!ids.has(article.id))].sort(displaySort)}
function updateViewActions(){showUnread.setAttribute('aria-pressed',String(viewMode==='unread'));showAll.setAttribute('aria-pressed',String(viewMode==='all'));const visibleUnread=renderedArticleIds.map(id=>articles.find(article=>article.id===id)).filter(article=>article&&isUnread(article));markVisible.textContent=visibleUnread.length?`Mark visible read (${visibleUnread.length})`:'Visible posts read';markVisible.disabled=visibleUnread.length===0;const olderCount=matchingArticles().filter(article=>!isUnread(article)&&!sessionRead.has(article.id)).length;loadOlder.hidden=viewMode!=='all'||olderCount<=readLimit;updateMarkReadAction()}
function updateStatus(){const unread=feed?.unreadCount??0,mode=viewMode==='unread'?'unread view':`all posts; ${readLimit} older at a time`;scoreLabel.textContent=Number(threshold.value).toFixed(1);status.textContent=`Showing ${renderedArticleIds.length} of ${articles.length} loaded posts (${mode}). ${unread} unread across all scores.`}
function render(){disconnectReadObserver();cards.replaceChildren();const visible=visibleArticles();renderedArticleIds=visible.map(article=>article.id);if(!articles.length)cards.innerHTML='<div class="empty">No articles stored yet. Select “Refresh source” to fetch and score this feed.</div>';else if(!visible.length)cards.innerHTML=`<div class="empty">${viewMode==='unread'?'No unread articles meet this score threshold. Choose All posts to browse read history.':'No articles meet this score threshold.'}</div>`;else for(const article of visible)cards.append(articleCard(article));updateViewActions();updateStatus();setupReadObserver()}
function articleCard(article){const parsed=articleContent(article.summary,article.link),unread=isUnread(article);const card=document.createElement('article');card.className=`card ${unread?'is-unread':'is-read'}${parsed.image?'':' no-image'}`;card.id=`article-${article.id.replaceAll('-','')}`;card.dataset.articleId=article.id;const body=document.createElement('div');body.className='article-body';const meta=document.createElement('div');meta.className='meta';const readState=document.createElement('button');readState.type='button';readState.className='read-state';readState.onclick=()=>setArticleReadState(article,!isUnread(article),card,readState);meta.append(readState,document.createTextNode(`${new Date(article.publishedAt).toLocaleString()} · relevance ${Number(article.score).toFixed(2)} · baseline ${Number(article.baselineScore).toFixed(2)}`));const heading=document.createElement('h2');const link=document.createElement('a');link.href=article.link;link.target='_blank';link.rel='noopener noreferrer';link.textContent=article.title;heading.append(link);const summary=document.createElement('p');summary.className='summary';summary.textContent=parsed.text||'No summary supplied by this feed.';const reading=document.createElement('div');reading.className='reading';reading.append(summary);let content,toggle;if(parsed.content){content=document.createElement('div');content.className='full-content';content.id=`content-${article.id.replaceAll('-','')}`;content.append(parsed.content);toggle=document.createElement('button');toggle.type='button';toggle.className='content-toggle';toggle.setAttribute('aria-controls',content.id);toggle.onclick=()=>setArticleExpanded(card,article,summary,content,toggle,!expandedArticles.has(article.id));reading.append(content,toggle)}const reason=document.createElement('p');reason.className='reason';reason.textContent=article.scoreReason||'No scoring explanation available.';const actions=document.createElement('div');actions.className='actions';actions.append(voteButton(article,'VeryInterested','Very interested','secondary',2),voteButton(article,'Interested','Interested','secondary',1),voteButton(article,'NotInterested','Not interested','negative',-1),voteButton(article,'NeverThisTopic','Never this topic','negative',-2));body.append(meta,heading,reading,reason,actions);card.append(body);if(parsed.image){const image=document.createElement('img');image.className='image';image.src=parsed.image;image.alt='';image.loading='lazy';image.referrerPolicy='no-referrer';card.append(image)}const sentinel=document.createElement('span');sentinel.className='read-sentinel';sentinel.dataset.articleId=article.id;sentinel.setAttribute('aria-hidden','true');card.append(sentinel);updateCardReadAppearance(card,article);if(content&&toggle)setArticleExpanded(card,article,summary,content,toggle,expandedArticles.has(article.id));return card}
function updateCardReadAppearance(card,article){const unread=isUnread(article),button=card.querySelector('.read-state');card.classList.toggle('is-unread',unread);card.classList.toggle('is-read',!unread);button.textContent=unread?'Unread':'Read';button.setAttribute('aria-pressed',String(unread));button.title=unread?'Click to mark this post read.':'Click to mark this post unread and protect it from automatic reading.'}
async function setArticleReadState(article,unread,card,button){button.disabled=true;const response=await fetch(`/api/articles/${article.id}/read-state`,{method:'PUT',headers:{'content-type':'application/json'},body:JSON.stringify({isUnread:unread,automatic:false})});if(!response.ok){button.disabled=false;status.textContent='Could not update this post’s read state.';return}const wasUnread=isUnread(article);article.isUnread=unread;article.isUnreadPinned=unread;article.readAt=unread?null:new Date().toISOString();if(unread)sessionRead.delete(article.id);else sessionRead.add(article.id);if(feed)feed.unreadCount=Math.max(0,(feed.unreadCount??0)+(unread&&!wasUnread?1:!unread&&wasUnread?-1:0));updateCardReadAppearance(card,article);button.disabled=false;updateViewActions();updateStatus()}
function setArticleExpanded(card,article,summary,content,toggle,expanded){summary.hidden=expanded;content.hidden=!expanded;toggle.textContent=expanded?'Show summary':'Show full feed content';toggle.setAttribute('aria-expanded',String(expanded));card.classList.toggle('is-expanded',expanded);if(expanded)expandedArticles.add(article.id);else expandedArticles.delete(article.id)}
function voteButton(article,kind,label,className,kindValue){const button=document.createElement('button'),isActive=article.activeFeedback===kindValue;button.className=className;button.textContent=label;button.setAttribute('aria-pressed',String(isActive));button.title=isActive?'Click again to undo this choice.':kind==='NeverThisTopic'?'Strongly teaches the local model to avoid related articles.':'';button.onclick=async()=>{button.disabled=true;const wasUnread=isUnread(article),response=await fetch(`/api/articles/${article.id}/feedback`,{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({kind})});if(response.ok){if(kindValue<0&&!isActive)temporarilyVisible.add(article.id);else temporarilyVisible.delete(article.id);if(wasUnread){sessionRead.add(article.id);if(feed)feed.unreadCount=Math.max(0,(feed.unreadCount??0)-1)}await load(false);status.textContent=isActive?`Feedback removed for “${article.title}”; the post remains read.`:`Feedback saved for “${article.title}”. The post is now read and future articles will use this evidence.`}else{status.textContent='Could not update feedback.';button.disabled=false}};return button}
function articleContent(value,base){if(!value?.trim())return{text:'',image:null,content:null};const documentValue=new DOMParser().parseFromString(value,'text/html'),allowedTags=new Set(['A','B','BLOCKQUOTE','BR','CODE','DIV','EM','FIGCAPTION','FIGURE','H1','H2','H3','H4','H5','H6','HR','I','IMG','LI','OL','P','PRE','SPAN','STRONG','SUB','SUP','TABLE','TBODY','TD','TH','THEAD','TR','UL']);documentValue.querySelectorAll('script,style,iframe,object,embed,form,input,button,textarea,select,svg,math').forEach(node=>node.remove());for(const node of [...documentValue.body.querySelectorAll('*')].reverse()){if(!allowedTags.has(node.tagName)){node.replaceWith(...node.childNodes);continue}const href=node.getAttribute('href'),src=node.getAttribute('src'),alt=node.getAttribute('alt');for(const attribute of [...node.attributes])node.removeAttribute(attribute.name);if(node.tagName==='A'){const safeHref=safeUrl(href,base,true);if(safeHref){node.href=safeHref;node.target='_blank';node.rel='noopener noreferrer'}}if(node.tagName==='IMG'){const safeSrc=safeUrl(src,base,false);if(safeSrc){node.src=safeSrc;node.alt=alt??'';node.loading='lazy';node.referrerPolicy='no-referrer'}else node.remove()}}const imageNode=documentValue.querySelector('img[src]'),image=imageNode?.src??null,text=(documentValue.body.textContent??'').replace(/\s+/g,' ').trim(),content=document.createDocumentFragment();content.append(...documentValue.body.childNodes);return{text,image,content}}
function safeUrl(value,base,allowMail){if(!value)return null;try{const candidate=new URL(value,base);return candidate.protocol==='http:'||candidate.protocol==='https:'||(allowMail&&candidate.protocol==='mailto:')?candidate.href:null}catch{return null}}
function disconnectReadObserver(){readObserver?.disconnect();readObserver=null;for(const timer of readTimers.values())clearTimeout(timer);readTimers.clear();autoQueue.clear();if(autoFlushTimer)clearTimeout(autoFlushTimer);autoFlushTimer=null}
function setupReadObserver(){disconnectReadObserver();if(!autoRead.checked)return;readObserver=new IntersectionObserver(entries=>{for(const entry of entries){const id=entry.target.dataset.articleId,article=articles.find(item=>item.id===id);if(!article||!isUnread(article)||article.isUnreadPinned)continue;if(entry.isIntersecting){if(!readTimers.has(id))readTimers.set(id,setTimeout(()=>{readTimers.delete(id);queueAutomaticRead(id)},1000))}else if(readTimers.has(id)){clearTimeout(readTimers.get(id));readTimers.delete(id)}}},{rootMargin:'0px 0px -20% 0px',threshold:0});for(const sentinel of cards.querySelectorAll('.read-sentinel')){const article=articles.find(item=>item.id===sentinel.dataset.articleId);if(article&&isUnread(article)&&!article.isUnreadPinned)readObserver.observe(sentinel)}}
function queueAutomaticRead(id){const article=articles.find(item=>item.id===id);if(!autoRead.checked||!article||!isUnread(article)||article.isUnreadPinned)return;autoQueue.add(id);if(!autoFlushTimer)autoFlushTimer=setTimeout(flushAutomaticReads,250)}
async function flushAutomaticReads(){autoFlushTimer=null;const ids=[...autoQueue].filter(id=>{const article=articles.find(item=>item.id===id);return article&&isUnread(article)&&!article.isUnreadPinned});autoQueue.clear();if(!ids.length)return;const response=await fetch('/api/articles/read',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({articleIds:ids,automatic:true})});if(!response.ok){status.textContent='Could not save automatic read progress.';return}for(const id of ids){const article=articles.find(item=>item.id===id);if(!article)continue;article.isUnread=false;article.readAt=new Date().toISOString();sessionRead.add(id);const card=document.getElementById(`article-${id.replaceAll('-','')}`);if(card)updateCardReadAppearance(card,article)}if(feed)feed.unreadCount=Math.max(0,(feed.unreadCount??0)-ids.length);updateViewActions();updateStatus()}
threshold.oninput=()=>{temporarilyVisible.clear();readLimit=25;render()};
showUnread.onclick=()=>{viewMode='unread';render()};showAll.onclick=()=>{viewMode='all';readLimit=25;render()};
autoRead.onchange=()=>{localStorage.setItem('personalrss.autoRead',String(autoRead.checked));setupReadObserver()};
loadOlder.onclick=()=>{readLimit+=25;render()};
markVisible.onclick=async()=>{const ids=renderedArticleIds.filter(id=>{const article=articles.find(item=>item.id===id);return article&&isUnread(article)});if(!ids.length)return;markVisible.disabled=true;const response=await fetch('/api/articles/read',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({articleIds:ids,automatic:false})});if(!response.ok){status.textContent='Could not mark the visible posts read.';updateViewActions();return}for(const id of ids){const article=articles.find(item=>item.id===id);if(!article)continue;article.isUnread=false;article.isUnreadPinned=false;article.readAt=new Date().toISOString();sessionRead.add(id);const card=document.getElementById(`article-${id.replaceAll('-','')}`);if(card)updateCardReadAppearance(card,article)}if(feed)feed.unreadCount=Math.max(0,(feed.unreadCount??0)-ids.length);updateViewActions();status.textContent=`Marked ${ids.length} visible ${ids.length===1?'post':'posts'} read. They will remain here until the view is refreshed.`};
document.querySelector('#refresh').onclick=async event=>{event.currentTarget.disabled=true;status.textContent='Fetching and scoring articles…';const response=await fetch(`/api/feeds/${feed.id}/refresh`,{method:'POST'});event.currentTarget.disabled=false;if(response.ok){await refreshFeedState();await load(false)}else status.textContent='Refresh failed. Check the source on the main page.'};
markRead.onclick=async()=>{const unread=feed.unreadCount??0;markRead.disabled=true;const response=await fetch(`/api/feeds/${feed.id}/viewed`,{method:'POST'});if(response.ok){for(const article of articles)if(isUnread(article)){article.isUnread=false;article.isUnreadPinned=false;article.readAt=new Date().toISOString();sessionRead.add(article.id);const card=document.getElementById(`article-${article.id.replaceAll('-','')}`);if(card)updateCardReadAppearance(card,article)}await refreshFeedState();updateViewActions();status.textContent=`Marked ${unread} ${unread===1?'article':'articles'} read across all scores. Loaded posts remain here until refresh.`}else{status.textContent='Could not mark this feed read.';updateMarkReadAction()}};
document.querySelector('#copy').onclick=async()=>{const url=new URL(`/feeds/${encodeURIComponent(slug)}.xml`,location.origin).href;await navigator.clipboard.writeText(url);status.textContent='RSS URL copied.'};
start().then(()=>{if(location.hash)document.querySelector(location.hash)?.scrollIntoView()}).catch(error=>{status.textContent='Could not load this preview.';console.error(error)});
</script></body></html>
""";
}
