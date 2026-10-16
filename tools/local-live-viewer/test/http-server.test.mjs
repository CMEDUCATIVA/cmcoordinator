import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { once } from 'node:events';
import { createLocalServer } from '../lib/http-server.mjs';
import { packet } from './fixtures.mjs';

async function start(t, options) {
  const server = createLocalServer(options);
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  t.after(() => { server.closeViewers(); server.closeAllConnections(); server.close(); });
  return `http://127.0.0.1:${server.address().port}`;
}
const post = (base, value, headers = {}) => fetch(base + '/api/sync', {
  method: 'POST', headers: { 'Content-Type': 'application/json', ...headers }, body: JSON.stringify(value)
});

test('API local sirve escena, rechaza orígenes externos y tipo de contenido', async t => {
  const base = await start(t);
  assert.equal((await post(base, packet())).status, 200);
  assert.equal((await (await fetch(base + '/api/state')).json()).elements.length, 1);
  assert.equal((await post(base, packet(), { Origin: 'https://externo.example' })).status, 403);
  assert.equal((await post(base, packet(), { 'Content-Type': 'text/plain' })).status, 415);
  assert.equal((await fetch(base + '/')).status, 200);
  assert.equal((await fetch(base + '/vendor/three/build/three.module.js')).status, 200);
  assert.equal((await fetch(base + '/%2e%2e%2fpackage.json')).status, 404);
});

test('Host ajeno y carga excesiva son rechazados', async t => {
  const base = await start(t, { maxBytes: 64 });
  assert.equal((await post(base, packet())).status, 413);
  const status = await new Promise(resolve => {
    http.get(base + '/api/status', { headers: { Host: 'host-ajeno.example' } }, response => {
      response.resume(); resolve(response.statusCode);
    });
  });
  assert.equal(status, 403);
});

test('SSE conserva conexión con snapshots grandes y un visor nuevo recupera el estado', async t => {
  const base = await start(t);
  const large = packet();
  for (let i = 0; i < 64; i++) large.updated[0].properties[`Dato ${i}`] = 'x'.repeat(1800);
  await post(base, large);
  const response = await fetch(base + '/events');
  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let pending = '';
  async function next() {
    while (!pending.includes('\n\n')) {
      const chunk = await reader.read();
      assert.equal(chunk.done, false);
      pending += decoder.decode(chunk.value, { stream: true });
    }
    const boundary = pending.indexOf('\n\n');
    const line = pending.slice(0, boundary);
    pending = pending.slice(boundary + 2);
    return JSON.parse(line.slice(6));
  }
  assert.equal((await next()).state.elements.length, 1);
  await post(base, packet({ sequence: 2, baseSequence: 1, reset: false, updated: [], removed: ['element-1'] }));
  assert.equal((await next()).packet.removed.length, 1);
  await reader.cancel();
  const late = await fetch(base + '/events');
  const lateReader = late.body.getReader();
  const data = decoder.decode((await lateReader.read()).value);
  assert.equal(JSON.parse(data.slice(6).trim()).state.elements.length, 0);
  await lateReader.cancel();
});
