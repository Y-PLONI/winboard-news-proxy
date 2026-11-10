'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { normalize, parse, NewsService } = require('../src/news');

test('normalize: title, body and media markers', () => {
  const r = normalize('**שמחת בית צאנז |** צילום משה גולדשטיין.\n[image-embedded#](https://stream.hagizra.news/x.jpg#1280x723:)');
  assert.equal(r.title, 'שמחת בית צאנז');
  assert.equal(r.body, 'צילום משה גולדשטיין.');
  assert.deepEqual(r.media, [{ kind: 'image', url: 'https://stream.hagizra.news/x.jpg', w: 1280, h: 723 }]);
});

test('normalize: video, audio, relative and foreign-host media', () => {
  const r = normalize([
    '**כותרת |** טקסט',
    '[video-embedded#](https://stream.hagizra.news/a/v.mp4#464x848)',
    '[audio-embedded#](https://stream.hagizra.news/b/a.mp3)',
    '[image-embedded#](/api/files/abc)',
    '[image-embedded#](https://evil.example/x.jpg#10x10)',
    '[image-embedded#](http://stream.hagizra.news/plain.jpg)',
    '[image-embedded#](https://user:pw@stream.hagizra.news/c.jpg)',
  ].join('\n'));
  assert.equal(r.body, 'טקסט');
  assert.deepEqual(r.media.map((m) => m.kind), ['video', 'audio', 'image', 'image', 'image', 'image']);
  assert.deepEqual(r.media[0], { kind: 'video', url: 'https://stream.hagizra.news/a/v.mp4', w: 464, h: 848 });
  assert.deepEqual(r.media[1], { kind: 'audio', url: 'https://stream.hagizra.news/b/a.mp3', w: 0, h: 0 });
  assert.equal(r.media[2].url, 'https://hagizra.news/api/files/abc');
  assert.deepEqual(r.media.slice(3).map((m) => m.url), ['', '', ''], 'foreign host, http and credentials are labelled but never loaded');
});

test('normalize: strips html, links and decodes entities', () => {
  const r = normalize('**כותרת**<b>גוף</b> &amp; [קישור](https://x.y) ![](a.png)');
  assert.equal(r.title, 'כותרת');
  assert.equal(r.body, 'גוף & קישור');
  assert.deepEqual(r.media.map((m) => m.kind), ['image']);
});

test('normalize: never lets html through', () => {
  const r = normalize('<script>alert(1)</script>טקסט');
  assert.ok(!r.body.includes('<'));
});

test('parse: bare array and {messages}, skips deleted/ads, sorts newest first', () => {
  const msgs = [
    { id: 1, text: 'ישן', timestamp: '2026-11-10T10:00:00Z' },
    { id: 2, text: 'חדש', timestamp: '2026-11-10T11:00:00Z', breakingId: 5 },
    { id: 3, text: 'נמחק', deleted: true, timestamp: '2026-11-10T12:00:00Z' },
    { id: 4, text: 'פרסומת', is_ads: true, timestamp: '2026-11-10T12:00:00Z' },
    { id: 'x', text: 'בלי מזהה' },
  ];
  for (const root of [msgs, { messages: msgs }]) {
    const items = parse(root);
    assert.deepEqual(items.map((i) => i.id), [2, 1]);
    assert.equal(items[0].breaking, true);
  }
  assert.throws(() => parse({ nope: 1 }));
});

test('NewsService: caches, dedupes in-flight, serves stale with backoff on failure', async () => {
  let calls = 0;
  let fail = false;
  let now = 1_000_000;
  const fetchImpl = async () => {
    calls++;
    if (fail) throw new Error('down');
    return { ok: true, json: async () => [{ id: 1, text: 'א', timestamp: '2026-11-10T10:00:00Z' }] };
  };
  const svc = new NewsService({ fetchImpl, now: () => now, ttlMs: 45_000 });

  const [a, b] = await Promise.all([svc.get(), svc.get()]);
  assert.equal(calls, 1);
  assert.equal(a.items.length, 1);
  assert.equal(b.stale, false);

  now += 10_000; await svc.get();
  assert.equal(calls, 1, 'fresh cache is reused');

  fail = true; now += 60_000;
  const s = await svc.get();
  assert.equal(calls, 2);
  assert.equal(s.stale, true);
  assert.equal(s.items.length, 1, 'stale items are kept');

  now += 5_000; await svc.get();
  assert.equal(calls, 2, 'backoff suppresses retry');
  now += 30_000; await svc.get();
  assert.equal(calls, 3);
});
