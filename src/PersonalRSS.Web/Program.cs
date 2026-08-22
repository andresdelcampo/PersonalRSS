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
        return Results.Ok(new { activeFeedback = (FeedbackKind?)null });
    }
    await repository.SetFeedbackAsync(id, kind, ct);
    return Results.Ok(new { activeFeedback = kind });
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
:root{color-scheme:light}*{box-sizing:border-box}body{font:16px system-ui;margin:0;color:#17212b;background:#f3f6f7}header{position:sticky;top:0;z-index:2;background:#ffffffee;backdrop-filter:blur(10px);border-bottom:1px solid #dbe3e7}header>div,main{max-width:1050px;margin:auto;padding:1rem 1.25rem}.top{display:flex;align-items:center;gap:.7rem;flex-wrap:wrap}h1{font-size:1.5rem;margin:0 auto 0 0}a{color:#126a5b}button,input{font:inherit}button{padding:.55rem .75rem;border:0;border-radius:8px;background:#126a5b;color:white;cursor:pointer}button.secondary{background:#e7eeec;color:#174d44}button.negative{background:#f4e7e5;color:#7a2f26}button.secondary[aria-pressed="true"]{background:#126a5b;color:white;box-shadow:0 0 0 3px #126a5b33}button.negative[aria-pressed="true"]{background:#a33a2d;color:white;box-shadow:0 0 0 3px #a33a2d33}button:disabled{opacity:.55;cursor:wait}.controls{display:flex;align-items:center;gap:.6rem;flex-wrap:wrap;margin-top:.8rem}.status{min-height:1.5rem;color:#52636d}.cards{display:grid;gap:1rem}.card{display:grid;grid-template-columns:minmax(0,1fr) 230px;gap:1rem;background:white;border:1px solid #dce4e8;border-radius:14px;padding:1.1rem;box-shadow:0 6px 20px #23323d0b}.card.no-image{grid-template-columns:1fr}.card h2{font-size:1.2rem;line-height:1.3;margin:.15rem 0}.meta{color:#64747d;font-size:.88rem}.summary{line-height:1.55;color:#33434c;display:-webkit-box;-webkit-line-clamp:5;-webkit-box-orient:vertical;overflow:hidden}.reason{font-size:.85rem;color:#64747d}.actions{display:flex;gap:.5rem;flex-wrap:wrap}.image{width:100%;height:165px;object-fit:cover;border-radius:10px;background:#e9eeef}.empty{background:white;padding:2rem;border-radius:14px;text-align:center;color:#607080}@media(max-width:700px){.card{grid-template-columns:1fr}.image{order:-1;height:210px}}
</style></head><body><header><div><div class="top"><a href="/">← Sources</a><h1 id="title">Article preview</h1><button id="refresh">Refresh source</button><button id="mark-read" class="secondary" disabled>Mark all read</button><button id="copy" class="secondary">Copy RSS URL</button></div><div class="controls"><label>Minimum score <strong id="score-label">0.5</strong> <input id="threshold" type="range" min="0" max="1" value="0.5" step="0.1"></label></div><div id="status" class="status" aria-live="polite"></div></div></header><main><div id="cards" class="cards"><div class="empty">Loading articles…</div></div></main>
<script>
const slug=decodeURIComponent(location.pathname.substring('/preview/'.length));
const cards=document.querySelector('#cards'),status=document.querySelector('#status'),title=document.querySelector('#title');
const threshold=document.querySelector('#threshold'),scoreLabel=document.querySelector('#score-label'),markRead=document.querySelector('#mark-read');
let feed,articles=[],temporarilyVisible=new Set();
async function start(){await refreshFeedState();if(!feed){cards.innerHTML='<div class="empty">Feed not found.</div>';return}title.textContent=feed.name;document.title=`${feed.name} — PersonalRSS`;await load()}
async function refreshFeedState(){const feeds=await fetch('/api/feeds').then(r=>r.json());feed=feeds.find(f=>f.slug===slug);updateMarkReadAction()}
async function load(){articles=await fetch(`/api/articles?feedId=${encodeURIComponent(feed.id)}&minScore=0&limit=200`).then(r=>r.json());render()}
function updateMarkReadAction(){const unread=feed?.unreadCount??0;markRead.textContent=unread>0?`Mark all read (${unread})`:'All read';markRead.disabled=!feed||unread===0}
function render(){const minimum=Number(threshold.value),unread=feed?.unreadCount??0;scoreLabel.textContent=minimum.toFixed(1);cards.replaceChildren();const visible=articles.filter(a=>a.score>=minimum||temporarilyVisible.has(a.id));status.textContent=`Showing ${visible.length} of ${articles.length} stored articles. ${unread} unread across all scores.`;if(!articles.length){cards.innerHTML='<div class="empty">No articles stored yet. Select “Refresh source” to fetch and score this feed.</div>';return}if(!visible.length){cards.innerHTML='<div class="empty">No articles meet this score threshold.</div>';return}for(const article of visible)cards.append(articleCard(article))}
function articleCard(article){const parsed=articleContent(article.summary,article.link);const card=document.createElement('article');card.className=`card${parsed.image?'':' no-image'}`;card.id=`article-${article.id.replaceAll('-','')}`;const body=document.createElement('div');const meta=document.createElement('div');meta.className='meta';meta.textContent=`${new Date(article.publishedAt).toLocaleString()} · relevance ${Number(article.score).toFixed(2)} · baseline ${Number(article.baselineScore).toFixed(2)}`;const heading=document.createElement('h2');const link=document.createElement('a');link.href=article.link;link.target='_blank';link.rel='noopener noreferrer';link.textContent=article.title;heading.append(link);const summary=document.createElement('p');summary.className='summary';summary.textContent=parsed.text||'No summary supplied by this feed.';const reason=document.createElement('p');reason.className='reason';reason.textContent=article.scoreReason||'No scoring explanation available.';const actions=document.createElement('div');actions.className='actions';actions.append(voteButton(article,'VeryInterested','Very interested','secondary',2),voteButton(article,'Interested','Interested','secondary',1),voteButton(article,'NotInterested','Not interested','negative',-1),voteButton(article,'NeverThisTopic','Never this topic','negative',-2));body.append(meta,heading,summary,reason,actions);card.append(body);if(parsed.image){const image=document.createElement('img');image.className='image';image.src=parsed.image;image.alt='';image.loading='lazy';image.referrerPolicy='no-referrer';card.append(image)}return card}
function voteButton(article,kind,label,className,kindValue){const button=document.createElement('button'),isActive=article.activeFeedback===kindValue;button.className=className;button.textContent=label;button.setAttribute('aria-pressed',String(isActive));button.title=isActive?'Click again to undo this choice.':kind==='NeverThisTopic'?'Strongly teaches the local model to avoid related articles.':'';button.onclick=async()=>{button.disabled=true;const response=await fetch(`/api/articles/${article.id}/feedback`,{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({kind})});if(response.ok){if(kindValue<0&&!isActive)temporarilyVisible.add(article.id);else temporarilyVisible.delete(article.id);status.textContent=isActive?`Feedback removed for “${article.title}”.`:`Feedback saved for “${article.title}”. Future articles will use this evidence.`;await load()}else{status.textContent='Could not update feedback.';button.disabled=false}};return button}
function articleContent(value,base){const documentValue=new DOMParser().parseFromString(value??'','text/html');documentValue.querySelectorAll('script,style,iframe,object,embed').forEach(node=>node.remove());const imageNode=documentValue.querySelector('img[src]');let image=null;if(imageNode)try{const candidate=new URL(imageNode.getAttribute('src'),base);if(candidate.protocol==='http:'||candidate.protocol==='https:')image=candidate.href}catch{}const text=(documentValue.body.textContent??'').replace(/\s+/g,' ').trim();return{text,image}}
threshold.oninput=()=>{temporarilyVisible.clear();render()};
document.querySelector('#refresh').onclick=async event=>{event.currentTarget.disabled=true;status.textContent='Fetching and scoring articles…';const response=await fetch(`/api/feeds/${feed.id}/refresh`,{method:'POST'});event.currentTarget.disabled=false;if(response.ok){await refreshFeedState();await load()}else status.textContent='Refresh failed. Check the source on the main page.'};
markRead.onclick=async()=>{const unread=feed.unreadCount??0;markRead.disabled=true;const response=await fetch(`/api/feeds/${feed.id}/viewed`,{method:'POST'});if(response.ok){feed.unreadCount=0;updateMarkReadAction();status.textContent=`Marked ${unread} ${unread===1?'article':'articles'} read across all scores.`}else{status.textContent='Could not mark this feed read.';updateMarkReadAction()}};
document.querySelector('#copy').onclick=async()=>{const url=new URL(`/feeds/${encodeURIComponent(slug)}.xml`,location.origin).href;await navigator.clipboard.writeText(url);status.textContent='RSS URL copied.'};
start().then(()=>{if(location.hash)document.querySelector(location.hash)?.scrollIntoView()}).catch(error=>{status.textContent='Could not load this preview.';console.error(error)});
</script></body></html>
""";
}
