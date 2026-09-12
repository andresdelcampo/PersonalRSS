const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '../src/PersonalRSS.Web/Program.cs'), 'utf8');
const loadFunction = source.slice(source.indexOf('async function load(resetOrder='), source.indexOf('function visibleStateTargets'));

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
