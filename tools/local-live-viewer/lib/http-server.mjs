import http from 'node:http';
import { readFile } from 'node:fs/promises';
import { dirname, resolve, sep, extname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
import { SceneStore } from './scene-store.mjs';
import { ProtocolError } from './protocol.mjs';
import { RevisionStore } from './revision-store.mjs';
import { revisionApi } from './revision-api.mjs';
import { createSyncLog } from './sync-log.mjs';
import { IfcExports } from './ifc-exports.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const mime = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.mjs': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8' };

function body(request, maxBytes) {
  return new Promise((resolveBody, reject) => {
    let size = 0, rejected = false;
    const chunks = [];
    request.on('data', chunk => {
      size += chunk.length;
      if (size > maxBytes) {
        if (!rejected) reject(new ProtocolError('Paquete demasiado grande', 413));
        rejected = true;
        chunks.length = 0;
      } else if (!rejected) chunks.push(chunk);
    });
    request.on('end', () => {
      if (!rejected) {
        try { resolveBody(JSON.parse(Buffer.concat(chunks).toString('utf8'))); }
        catch { reject(new ProtocolError('JSON inválido')); }
      }
    });
    request.on('error', reject);
  });
}

export function createLocalServer({ maxBytes = 32 * 1024 * 1024, dataPath = ':memory:' } = {}) {
  const store = new SceneStore();
  const revisions = new RevisionStore(dataPath);
  const exports = new IfcExports(dataPath === ':memory:' ? null : resolve(dirname(dataPath), 'exports'));
  const log = createSyncLog(dataPath === ':memory:' ? null : resolve(dirname(dataPath), 'logs'));
  const listeners = new Set();
  const bootId = randomUUID();
  const send = (response, data) => {
    if (response.destroyed || response.writableEnded) return;
    // write(false) is normal for a large snapshot. Only drop clients accumulating a bounded backlog.
    if (response.writableLength > maxBytes * 2) { response.end(); return; }
    response.write(`data: ${JSON.stringify(data)}\n\n`);
  };
  const server = http.createServer(async (request, response) => {
    const port = server.address()?.port;
    const host = request.headers.host;
    const permittedHosts = [`127.0.0.1:${port}`, `localhost:${port}`];
    const permittedOrigins = permittedHosts.map(h => `http://${h}`);
    response.setHeader('X-Content-Type-Options', 'nosniff');
    response.setHeader('Cache-Control', 'no-store');
    response.setHeader('Content-Security-Policy', "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self'; connect-src 'self'; img-src 'self' data:; frame-ancestors 'none'");
    const json = (status, data) => {
      response.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8' });
      response.end(JSON.stringify(data));
    };
    if (!permittedHosts.includes(host) || (request.headers.origin && !permittedOrigins.includes(request.headers.origin)))
      return json(403, { error: 'Solo se admite el visor del origen local' });
    try {
      const url = new URL(request.url, `http://${host}`);
      if (request.method === 'GET' && url.pathname === '/api/status') return json(200, { application: 'cmcoordinator-local-live-viewer', protocols: [1, 2], bootId, ...store.status(), ...revisions.status() });
      if (await exports.api({ request, response, url, store: revisions, json })) return;
      if (await revisionApi({ request, response, url, store: revisions, json, log, notify: data => {
        for (const client of listeners) send(client, data);
      } })) return;
      if (request.method === 'GET' && url.pathname === '/api/state') return json(200, store.snapshot());
      if (request.method === 'POST' && url.pathname === '/api/sync') {
        if (!/^application\/json(?:;|$)/i.test(request.headers['content-type'] ?? ''))
          return json(415, { error: 'Content-Type application/json requerido' });
        if (Number(request.headers['content-length']) > maxBytes) {
          request.resume(); return json(413, { error: 'Paquete demasiado grande' });
        }
        const applied = store.apply(await body(request, maxBytes));
        if (!applied.duplicate) for (const client of listeners) send(client, { kind: 'patch', packet: applied.packet });
        return json(200, { ok: true, sequence: store.status().sequence, duplicate: applied.duplicate });
      }
      if (request.method === 'GET' && url.pathname === '/events') {
        if (listeners.size >= 8) return json(503, { error: 'Demasiados visores abiertos' });
        response.writeHead(200, { 'Content-Type': 'text/event-stream', Connection: 'keep-alive' });
        listeners.add(response);
        send(response, revisions.status().revisionId ? { kind: 'revision', revision: revisions.manifest(undefined, false) } : { kind: 'snapshot', state: store.snapshot() });
        const heartbeat = setInterval(() => response.write(': conectado\n\n'), 15000);
        response.on('close', () => { clearInterval(heartbeat); listeners.delete(response); });
        response.on('error', () => { clearInterval(heartbeat); listeners.delete(response); response.end(); });
        return;
      }
      if (request.method !== 'GET') return json(405, { error: 'Método no permitido' });
      const vendor = url.pathname.startsWith('/vendor/three/');
      const base = vendor ? resolve(root, 'node_modules/three') : resolve(root, 'public');
      const relative = vendor ? url.pathname.slice('/vendor/three/'.length) : url.pathname === '/' ? 'index.html' : url.pathname.slice(1);
      const file = resolve(base, decodeURIComponent(relative));
      if (!file.startsWith(base + sep) || !mime[extname(file)]) return json(404, { error: 'No encontrado' });
      const bytes = await readFile(file);
      response.writeHead(200, { 'Content-Type': mime[extname(file)] });
      response.end(bytes);
    } catch (error) {
      if (error.errcode === 13 || error.errcode === 10) error.status = 507;
      log('receiver', 'REQUEST_FAILED', { status: error.status ?? 500, errorType: error.constructor.name });
      if (response.headersSent) return response.end();
      if (error.code === 'ENOENT') return json(404, { error: 'Archivo no encontrado; ejecuta npm ci si falta Three.js' });
      json(error.status ?? 500, { error: error.status ? error.message : 'Error interno del visor local' });
    }
  });
  server.requestTimeout = 15000;
  server.headersTimeout = 10000;
  server.on('close', () => { for (const response of listeners) response.end(); });
  server.on('close', () => revisions.close());
  server.closeViewers = () => { for (const response of listeners) response.end(); };
  return server;
}
