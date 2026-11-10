'use strict';
// Local HTTPS server that answers for windows.msn.com (the Windows Widgets "Discover" board shell).
// Zero dependencies. Listens on 127.0.0.1 only.
//
//   node server.js              HTTPS on :443 (needs certs/server.pfx from scripts/install.ps1)
//   node server.js --http       plain HTTP on :8443 (local testing, no cert, no hosts change)
//   node server.js --port 9000  custom port

const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const https = require('node:https');
const { NewsService } = require('./src/news');

const args = process.argv.slice(2);
const plainHttp = args.includes('--http');
const portArg = args.indexOf('--port');
const PORT = portArg >= 0 ? Number(args[portArg + 1]) : (plainHttp ? 8443 : 443);
const HOST = '127.0.0.1';

const ROOT = __dirname;
const PAGE = path.join(ROOT, 'public', 'app.html');
const LOG_DIR = path.join(ROOT, 'logs');
const LOG_FILE = path.join(LOG_DIR, 'requests.log');
fs.mkdirSync(LOG_DIR, { recursive: true });

function log(entry) {
  try {
    if (fs.existsSync(LOG_FILE) && fs.statSync(LOG_FILE).size > 1_000_000) fs.renameSync(LOG_FILE, LOG_FILE + '.old');
    fs.appendFileSync(LOG_FILE, JSON.stringify({ t: new Date().toISOString(), ...entry }) + '\n');
  } catch { /* logging must never break serving */ }
}

const news = new NewsService();

function send(res, status, type, body, extra = {}) {
  res.writeHead(status, { 'content-type': type, 'cache-control': 'no-store', 'x-content-type-options': 'nosniff', ...extra });
  res.end(body);
}

function readBody(req, limit = 16 * 1024) {
  return new Promise((resolve) => {
    let size = 0;
    const chunks = [];
    req.on('data', (c) => { size += c.length; if (size <= limit) chunks.push(c); });
    req.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
    req.on('error', () => resolve(''));
  });
}

async function handler(req, res) {
  const url = new URL(req.url, 'https://windows.msn.com');
  const p = url.pathname;
  const h = req.headers;
  log({ m: req.method, url: req.url, host: h.host, ua: h['user-agent'], accept: h.accept, ref: h.referer, sfd: h['sec-fetch-dest'] });

  if (p === '/healthz') return send(res, 200, 'text/plain; charset=utf-8', 'ok');

  if (p === '/news.json' && req.method === 'GET') {
    return send(res, 200, 'application/json; charset=utf-8', JSON.stringify(await news.get()));
  }

  if (p === '/diag' && req.method === 'POST') {
    log({ diag: await readBody(req) });
    return send(res, 204, 'text/plain', '');
  }

  const wantsHtml = (h.accept || '').includes('text/html');
  if (req.method === 'GET' && (p === '/' || p === '/app.html' || (wantsHtml && !path.extname(p)))) {
    return send(res, 200, 'text/html; charset=utf-8', fs.readFileSync(PAGE));
  }

  return send(res, 404, 'text/plain; charset=utf-8', 'not found');
}

function onRequest(req, res) {
  handler(req, res).catch((e) => {
    log({ error: String(e?.stack ?? e) });
    if (!res.headersSent) send(res, 500, 'text/plain', 'error');
  });
}

let server;
if (plainHttp) {
  server = http.createServer(onRequest);
} else {
  const pfxFile = path.join(ROOT, 'certs', 'server.pfx');
  const passFile = path.join(ROOT, 'certs', 'pass.txt');
  if (!fs.existsSync(pfxFile) || !fs.existsSync(passFile)) {
    console.error('Missing certs/server.pfx. Run install.cmd first (or use --http for local testing).');
    process.exit(1);
  }
  server = https.createServer({ pfx: fs.readFileSync(pfxFile), passphrase: fs.readFileSync(passFile, 'utf8').trim() }, onRequest);
}

server.on('tlsClientError', (e) => log({ tlsError: e.message }));
server.on('error', (e) => {
  console.error(e.code === 'EADDRINUSE' ? `Port ${PORT} is already in use.` : e);
  process.exit(1);
});
server.listen(PORT, HOST, () => {
  console.log(`winboard-news-proxy listening on ${plainHttp ? 'http' : 'https'}://${HOST}:${PORT}`);
});
