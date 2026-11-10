'use strict';
// Fetches flashes from Hagizra and normalizes them. Port of HagizraWidget.Core (HagizraApi.cs + NewsNormalizer.cs).

const API = 'https://hagizra.news/api/messages';
const SITE = 'https://hagizra.news/';
const EDGE = /^[ ‎‏‪-‮⁦-⁩]+|[ ‎‏‪-‮⁦-⁩]+$/g;

const EMBEDDED = /\[(image|video|audio)-embedded#\]\([^)]*\)/gi;
const MD_IMAGE = /!\[[^\]]*\]\([^)]*\)/g;
const LINK = /\[([^\]]*)\]\([^)]*\)/g;
const TAG = /<\/?[a-zA-Z][^>]{0,200}>/g;
const TITLE = /^[\s‎‏]*\*{2,}([^*]+?)\*{2,}/;

const NAMED = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: ' ' };

function htmlDecode(s) {
  return s.replace(/&(#x[0-9a-f]+|#[0-9]+|amp|lt|gt|quot|apos|nbsp);/gi, (m, e) => {
    if (e[0] === '#') {
      const code = e[1].toLowerCase() === 'x' ? parseInt(e.slice(2), 16) : parseInt(e.slice(1), 10);
      try { return String.fromCodePoint(code); } catch { return m; }
    }
    return NAMED[e.toLowerCase()] ?? m;
  });
}

function clean(s) {
  return s.replace(/\*/g, '').replace(/_/g, ' ').replace(/\s+/g, ' ').replace(EDGE, '');
}

/** Raw markdown/HTML mix -> { title, body, media[] } (plain text only; never HTML). */
function normalize(raw) {
  if (!raw) return { title: '', body: '', media: [] };
  const media = new Set();

  let text = raw.replace(EMBEDDED, (_, kind) => { media.add(kind.toLowerCase()); return ' '; });
  text = text.replace(MD_IMAGE, () => { media.add('image'); return ' '; });
  text = text.replace(LINK, '$1');
  text = htmlDecode(text.replace(TAG, ''));

  let title = '';
  const m = TITLE.exec(text);
  if (m) {
    title = clean(m[1].replace(/\|+$/, ''));
    text = text.slice(m[0].length);
  }
  return { title, body: clean(text), media: [...media] };
}

/** Accepts a bare array or { messages: [...] }; unknown fields are ignored. */
function parse(root) {
  if (root && !Array.isArray(root) && Array.isArray(root.messages)) root = root.messages;
  if (!Array.isArray(root)) throw new Error('Unexpected messages payload');

  const items = [];
  for (const m of root) {
    if (!m || typeof m !== 'object' || m.deleted === true || m.is_ads === true) continue;
    if (!Number.isSafeInteger(m.id)) continue;
    const { title, body, media } = normalize(typeof m.text === 'string' ? m.text : '');
    if (!title && !body && media.length === 0) continue;
    const t = Date.parse(m.timestamp);
    items.push({
      id: m.id,
      time: Number.isNaN(t) ? Date.now() : t,
      title,
      body,
      media,
      breaking: typeof m.breakingId === 'number' && m.breakingId > 0,
    });
  }
  items.sort((a, b) => (b.time - a.time) || (b.id - a.id));
  return items;
}

async function fetchNews(limit, fetchImpl = fetch) {
  const res = await fetchImpl(`${API}?limit=${limit}&offset=0`, {
    headers: { 'user-agent': 'winboard-news-proxy/0.1', accept: 'application/json' },
    signal: AbortSignal.timeout(10_000),
  });
  if (!res.ok) throw new Error(`Hagizra HTTP ${res.status}`);
  return parse(await res.json());
}

/** Shared cache: one request serves all callers; stale data is served when Hagizra is unreachable. */
class NewsService {
  constructor({ limit = 25, ttlMs = 45_000, fetchImpl = fetch, now = Date.now } = {}) {
    this.limit = limit;
    this.ttlMs = ttlMs;
    this.fetchImpl = fetchImpl;
    this.now = now;
    this.items = [];
    this.fetchedAt = 0;
    this.failures = 0;
    this.retryAt = 0;
    this.inflight = null;
    this.lastError = null;
  }

  async get() {
    const t = this.now();
    const fresh = this.fetchedAt && t - this.fetchedAt < this.ttlMs;
    if (!fresh && t >= this.retryAt) {
      this.inflight ??= this.#refresh().finally(() => { this.inflight = null; });
      await this.inflight;
    }
    return { items: this.items, fetchedAt: this.fetchedAt, stale: this.failures > 0, error: this.lastError, site: SITE };
  }

  async #refresh() {
    try {
      this.items = await fetchNews(this.limit, this.fetchImpl);
      this.fetchedAt = this.now();
      this.failures = 0;
      this.retryAt = 0;
      this.lastError = null;
    } catch (e) {
      this.failures++;
      this.lastError = String(e?.message ?? e);
      const backoff = [30, 60, 120, 300, 900][Math.min(this.failures - 1, 4)] * 1000;
      this.retryAt = this.now() + backoff;
    }
  }
}

module.exports = { normalize, parse, fetchNews, NewsService, SITE };
