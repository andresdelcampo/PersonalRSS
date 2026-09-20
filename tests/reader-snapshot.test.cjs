const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '../src/PersonalRSS.Web/Program.cs'), 'utf8');
const loadFunction = source.slice(source.indexOf('async function load(resetOrder='), source.indexOf('function visibleStateTargets'));
const selectFunctions = source.slice(source.indexOf('function selectAllFeeds('), source.indexOf('function showWelcome('));
const refreshFunction = source.slice(source.indexOf('async function loadAndRefresh('), source.indexOf('renameForm.onsubmit'));
const bandFunction = source.slice(source.indexOf('async function setBand('), source.indexOf('function setDisplayOptions'));

function reader(allFeeds) {
  const context = vm.createContext({
    allFeeds, feed: { id: 'feed' }, articles: [], requests: 0, renders: 0, counts: 0,
    response: [{ id: 'unread' }],
    fetch: async () => { context.requests++; return { json: async () => context.response }; },
    refreshFeedState: async () => { context.counts++; },
    prepareDisplayOrder: () => {}, render: () => { context.renders++; }
  });
  vm.runInContext(loadFunction, context);
  return context;
}

test('background rescore keeps session-read cards, their objects and order in All feeds', async () => {
  const page = reader(true);
  page.response = [{ id: 'read-later', isUnread: true }, { id: 'unread', isUnread: true }];
  await page.load(true);
  const snapshot = page.articles;
  snapshot[0].isUnread = false;
  page.response = [{ id: 'new-unread' }, { id: 'unread' }];
  await page.load(false);
  assert.equal(page.articles, snapshot);
  assert.deepEqual(page.articles.map(x => x.id), ['read-later', 'unread']);
  assert.equal(page.renders, 1);
  assert.equal(page.requests, 1);
  assert.equal(page.counts, 1);
});

test('a new All feeds session fetches the latest unread snapshot', async () => {
  const page = reader(true);
  await page.load(true);
  const previous = page.articles;
  page.response = [{ id: 'new-unread' }];
  await page.load(true);
  assert.notEqual(page.articles, previous);
  assert.equal(page.requests, 2);
  assert.equal(page.renders, 2);
});

test('single-feed background reload still updates its articles', async () => {
  const page = reader(false);
  await page.load(false);
  assert.equal(page.requests, 1);
  assert.equal(page.renders, 1);
});

test('switching relevance refreshes the snapshot and resets the posts page to the top', async () => {
  let scrolls = 0;
  let loads = 0;
  const button = () => ({ setAttribute: () => {} });
  const context = vm.createContext({
    bandMode: 'high', viewMode: 'unread', bandHigh: button(), bandMaybe: button(), bandFiltered: button(),
    sessionRead: new Set(['read-during-previous-band']),
    temporarilyVisible: new Set(), readLimit: 25, bandSwitchInFlight: false, localStorage: { setItem: () => {} },
    render: () => {}, load: async () => { loads++; }, setStatus: () => {},
    window: { scrollTo: (x, y) => { scrolls++; assert.equal(x, 0); assert.equal(y, 0); } }
  });
  vm.runInContext(bandFunction, context);
  await context.setBand('maybe');
  assert.equal(scrolls, 1);
  assert.equal(loads, 1);
  assert.equal(context.sessionRead.size, 0);
  await context.setBand('maybe');
  assert.equal(scrolls, 1);
  assert.equal(loads, 1);
});

test('clicking the active feed reloads its posts page', () => {
  let reloads = 0;
  const context = vm.createContext({
    localStorage: { setItem: () => {} }, history: { pushState: () => {} },
    list: { querySelectorAll: () => [] }, welcome: {}, innerWidth: 1000,
    frame: { hidden: false, src: 'http://localhost/preview/example?embedded=1', contentWindow: { location: { reload: () => reloads++ } } },
    encodeURIComponent, setTimeout
  });
  vm.runInContext(selectFunctions, context);
  context.selectFeed({ slug: 'example' }, true);
  assert.equal(reloads, 1);
  context.selectFeed({ slug: 'example' }, false);
  assert.equal(reloads, 1);
  context.frame.src = 'http://localhost/preview/all?embedded=1&all=1';
  context.selectAllFeeds(true);
  assert.equal(reloads, 2);
});

test('explicit feed refresh reloads an active All feeds page', async () => {
  let reloads = 0;
  const feeds = [{ id: 'feed', unreadCount: 1, maybeUnreadCount: 2 }];
  const context = vm.createContext({
    localStorage: { setItem: () => {} }, refreshButton: {}, message: {},
    getFeeds: async () => feeds, renderFeeds: () => {}, refreshFeeds: async () => [{ ok: true }],
    frame: { hidden: false, contentWindow: { location: { reload: () => reloads++ } } },
    selectedSlug: () => '*'
  });
  vm.runInContext(refreshFunction, context);
  await context.loadAndRefresh(true, true);
  assert.equal(reloads, 1);
});
