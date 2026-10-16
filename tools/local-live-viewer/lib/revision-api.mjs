import { ProtocolError } from './protocol.mjs';
import { PART_BYTES } from './revision-store.mjs';

export function readBody(request, maxBytes, binary = false) {
  return new Promise((resolve, reject) => {
    const chunks = []; let bytes = 0, rejected = false;
    request.on('data', chunk => {
      bytes += chunk.length;
      if (bytes > maxBytes) {
        if (!rejected) reject(new ProtocolError('Cuerpo demasiado grande', 413));
        rejected = true; chunks.length = 0;
      } else if (!rejected) chunks.push(chunk);
    });
    request.on('end', () => {
      if (rejected) return;
      const content = Buffer.concat(chunks);
      if (binary) return resolve(content);
      try { resolve(JSON.parse(content.toString('utf8'))); } catch { reject(new ProtocolError('JSON inválido')); }
    });
    request.on('error', reject);
  });
}

export async function revisionApi({ request, response, url, store, json, notify, log }) {
  if (!url.pathname.startsWith('/api/v2/')) return false;
  if (request.method === 'POST' && !request.headers['content-type']?.startsWith('application/json') &&
      !request.headers['content-type']?.startsWith('application/octet-stream')) throw new ProtocolError('Content-Type requerido', 415);
  if (request.method === 'POST' && url.pathname === '/api/v2/jobs') {
    const value = await readBody(request, 16 * 1024);
    json(200, store.begin(value)); log('receiver', 'JOB_BEGIN', { jobId: value.jobId, bytes: value.byteLength }); return true;
  }
  let match = url.pathname.match(/^\/api\/v2\/jobs\/([\w-]+)\/chunks\/(\d+)$/);
  if (request.method === 'POST' && match) {
    const started = performance.now(), bytes = await readBody(request, PART_BYTES, true);
    json(200, store.chunk(match[1], Number(match[2]), bytes, request.headers['x-chunk-sha256']));
    log('receiver', 'CHUNK_DURABLE', { jobId: match[1], index: Number(match[2]), bytes: bytes.length, durationMs: Math.round(performance.now() - started) }); return true;
  }
  match = url.pathname.match(/^\/api\/v2\/jobs\/([\w-]+)\/commit$/);
  if (request.method === 'POST' && match) {
    await readBody(request, 1024);
    const started = performance.now(), result = store.commit(match[1]);
    log('receiver', 'REVISION_COMMIT', { jobId: match[1], durationMs: Math.round(performance.now() - started), duplicate: result.duplicate });
    json(200, result);
    if (!result.duplicate) notify({ kind: 'revision', revision: store.manifest(undefined, false) });
    return true;
  }
  match = url.pathname.match(/^\/api\/v2\/revisions\/([\w-]+)(?:\/parts\/(\d+))?$/);
  if (request.method === 'GET' && match) {
    if (match[2] === undefined) json(200, store.manifest(match[1]));
    else {
      const part = store.part(match[1], Number(match[2]), url.searchParams.get('mode') ?? 'base');
      response.writeHead(200, { 'Content-Type': 'application/octet-stream', 'X-Chunk-Sha256': part.hash });
      response.end(Buffer.from(part.payload));
    }
    return true;
  }
  if (request.method === 'POST' && url.pathname === '/api/v2/viewer-ack') {
    const value = await readBody(request, 1024); json(200, store.acknowledgeViewer(value.revisionId));
    log('viewer', 'REVISION_PRESENTED', { jobId: value.revisionId }); return true;
  }
  if (request.method === 'POST' && url.pathname === '/api/v2/telemetry') {
    const value = await readBody(request, 1024);
    if (!['VIEWER_APPLIED', 'VIEWER_LOAD_FAILED'].includes(value.code) || !Number.isFinite(value.durationMs) || value.durationMs < 0 || value.durationMs > 86400000)
      throw new ProtocolError('Diagnóstico inválido');
    log('viewer', value.code, { durationMs: value.durationMs }); json(200, { ok: true }); return true;
  }
  throw new ProtocolError('Ruta v2 no disponible', 404);
}
