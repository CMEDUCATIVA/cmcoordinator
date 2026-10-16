import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve, sep } from 'node:path';
import { randomUUID } from 'node:crypto';
import { RevisionStore, PART_BYTES, digest } from '../lib/revision-store.mjs';
import { packet, cube } from './fixtures.mjs';

function prepare(value, baseRevision = null) {
  const bytes = Buffer.from(JSON.stringify(value));
  return { bytes, header: { protocol: 2, jobId: randomUUID(), sessionId: value.sessionId,
    sessionStartedAt: value.sessionStartedAt, sourceId: value.sourceId, title: value.title, view: value.view,
    baseRevision, reset: value.reset, sequence: value.sequence, byteLength: bytes.length,
    chunkCount: Math.ceil(bytes.length / PART_BYTES), payloadHash: digest(bytes) } };
}
function upload(store, work, from = 0) {
  for (let i = from; i < work.header.chunkCount; i++) {
    const part = work.bytes.subarray(i * PART_BYTES, (i + 1) * PART_BYTES);
    store.chunk(work.header.jobId, i, part, digest(part));
  }
}
function snapshot(store, revision) {
  const info = store.manifest(revision), parts = [];
  for (let i = 0; i < info.partCount; i++) parts.push(Buffer.from(store.part(revision, i).payload));
  const bytes = Buffer.concat(parts); assert.equal(digest(bytes), info.payloadHash);
  return JSON.parse(bytes.toString('utf8'));
}

test('staging durable sobrevive reinicio, conserva bloques y confirma de forma idempotente', t => {
  const directory = mkdtempSync(join(tmpdir(), 'cmp-revision-test-'));
  t.after(() => {
    assert.ok(resolve(directory).startsWith(resolve(tmpdir()) + sep + 'cmp-revision-test-'));
    rmSync(directory, { recursive: true, force: true });
  });
  const path = join(directory, 'store.sqlite');
  const elements = Array.from({ length: 400 }, (_, i) => ({ ...cube(`cube-${i}`), properties: { dato: 'ñ'.repeat(1000) } }));
  const work = prepare(packet({ updated: elements }));
  assert.ok(work.header.chunkCount > 1);
  let store = new RevisionStore(path);
  store.begin(work.header);
  const first = work.bytes.subarray(0, PART_BYTES);
  store.chunk(work.header.jobId, 0, first, digest(first));
  assert.throws(() => store.commit(work.header.jobId), { status: 409 });
  assert.equal(store.status().revisionId, undefined);
  store.close(); store = new RevisionStore(path);
  assert.deepEqual(store.begin(work.header).received, [0]);
  assert.equal(store.chunk(work.header.jobId, 0, first, digest(first)).duplicate, true);
  upload(store, work, 1); store.commit(work.header.jobId);
  assert.equal(snapshot(store, work.header.jobId).elements.length, 400);
  assert.equal(store.commit(work.header.jobId).duplicate, true);
  store.close(); store = new RevisionStore(path);
  assert.equal(store.status().revisionId, work.header.jobId);
  assert.equal(store.begin(work.header).committed, true);
  store.close();
});

test('delta conserva revisión visible hasta commit y ofrece partes incrementales pequeñas', () => {
  const store = new RevisionStore();
  try {
    const first = prepare(packet({ updated: [cube('a'), cube('b'), cube('c')] }));
    store.begin(first.header); upload(store, first); store.commit(first.header.jobId);
    const second = prepare(packet({ reset: false, sequence: 2, updated: [cube('d')], removed: ['b'] }), first.header.jobId);
    store.begin(second.header); upload(store, second);
    assert.equal(snapshot(store, first.header.jobId).elements.length, 3);
    assert.equal(store.status().revisionId, first.header.jobId);
    store.commit(second.header.jobId);
    assert.deepEqual(snapshot(store, second.header.jobId).elements.map(e => e.id), ['a', 'c', 'd']);
    const manifest = store.manifest();
    const delta = JSON.parse(Buffer.from(store.part(second.header.jobId, 0, 'delta').payload));
    assert.deepEqual(delta.removed, ['b']); assert.equal(delta.elements.length, 1);
    assert.ok(manifest.delta.byteLength < manifest.byteLength);
    assert.equal(store.status().viewerRevision, null);
    store.acknowledgeViewer(second.header.jobId);
    assert.equal(store.status().viewerRevision, second.header.jobId);
    assert.throws(() => store.acknowledgeViewer(first.header.jobId), { status: 409 });
  } finally { store.close(); }
});

test('checksum, manifiesto y base inválidos no alteran escena confirmada', () => {
  const store = new RevisionStore();
  try {
    const work = prepare(packet()); store.begin(work.header);
    assert.throws(() => store.chunk(work.header.jobId, 0, work.bytes, 'f'.repeat(64)), { status: 422 });
    upload(store, work); store.commit(work.header.jobId);
    assert.throws(() => store.begin(prepare(packet({ sequence: 2 })).header), { status: 409 });
    const invalid = packet({ sequence: 2, reset: false }); invalid.updated[0].meshes[0].indices[0] = 999;
    const bad = prepare(invalid, work.header.jobId); store.begin(bad.header); upload(store, bad);
    assert.throws(() => store.commit(bad.header.jobId));
    assert.equal(store.status().revisionId, work.header.jobId);
    assert.equal(snapshot(store, work.header.jobId).elements.length, 1);
  } finally { store.close(); }
});

test('escena acumulada y sesiones antiguas no eluden los límites con bloques pequeños', () => {
  const store = new RevisionStore();
  try {
    const first = prepare(packet()); store.begin(first.header); upload(store, first); store.commit(first.header.jobId);
    const old = prepare(packet({ sessionStartedAt: 1 }), first.header.jobId);
    assert.throws(() => store.begin(old.header), { status: 403 });
    const huge = prepare(packet({ sequence: 2, updated: Array.from({ length: 20001 }, (_, i) => cube(`x-${i}`)) }), first.header.jobId);
    store.begin(huge.header); upload(store, huge);
    assert.throws(() => store.commit(huge.header.jobId));
    assert.equal(store.status().count, 1);
  } finally { store.close(); }
});
