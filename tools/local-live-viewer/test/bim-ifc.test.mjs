import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir, homedir } from 'node:os';
import { join, resolve, sep } from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { RevisionStore, digest } from '../lib/revision-store.mjs';
import { validatePacket } from '../lib/protocol.mjs';
import { IfcExports } from '../lib/ifc-exports.mjs';
import { bimPacket } from './bim-fixture.mjs';
import { createLocalServer } from '../lib/http-server.mjs';
function commit(store, packet, jobId = 'bim-job', baseRevision = null) {
  const bytes = Buffer.from(JSON.stringify(packet));
  const header = { ...packet, protocol: 2, jobId, baseRevision, byteLength: bytes.length, chunkCount: 1, payloadHash: digest(bytes) };
  delete header.updated; delete header.removed; delete header.bim;
  store.begin(header); store.chunk(jobId, 0, bytes, digest(bytes)); store.commit(jobId);
}
test('BIM conserva tipos/unidades/materiales y snapshot fijo durante delta y reinicio', async () => {
  const folder = await mkdtemp(join(tmpdir(), 'coordinator-bim-'));
  let store;
  try {
    store = new RevisionStore(join(folder, 'test.sqlite'));
    const packet = bimPacket(); commit(store, packet);
    const fixed = store.exportSnapshot('bim-job');
    packet.bim.levels.level.name = 'Nivel actualizado';
    commit(store, { ...packet, sequence: 2, reset: false, updated: [] }, 'bim-next', 'bim-job');
    assert.equal(fixed.bim.levels.level.name, 'Nivel 1');
    assert.throws(() => store.exportSnapshot('bim-job'), { status: 409 });
    store.close(); store = new RevisionStore(join(folder, 'test.sqlite'));
    const result = store.exportSnapshot('bim-next');
    assert.equal(result.bim.levels.level.name, 'Nivel actualizado');
    assert.equal(result.elements[0].bim.parameters[0].siValue, 3);
    assert.equal(result.elements[0].meshes[0].materialId, 'material');
    const manifest = store.manifest('bim-next');
    const snapshot = JSON.parse(Buffer.concat(Array.from({ length: manifest.partCount }, (_, i) => store.part('bim-next', i).payload)));
    assert.deepEqual(snapshot.bim, packet.bim);
  } finally {
    store?.close(); assert.ok(resolve(folder).startsWith(resolve(tmpdir()) + sep + 'coordinator-bim-'));
    await rm(folder, { recursive: true, force: true });
  }
});
test('BIM rechaza duplicación de IDs de parámetro y valores numéricos inválidos', () => {
  const packet = bimPacket(); packet.updated[0].bim.parameters.push(packet.updated[0].bim.parameters[0]);
  assert.throws(() => validatePacket(packet), { status: 422 });
  packet.updated[0].bim.parameters.pop(); packet.updated[0].bim.parameters[0].value = '3';
  assert.throws(() => validatePacket(packet), { status: 422 });
});
test('IFC real conserva geometría, clase, GUID, nivel, tipo, material y propiedades tipadas', async () => {
  const folder = await mkdtemp(join(tmpdir(), 'coordinator-ifc-'));
  const store = new RevisionStore();
  try {
    const packet = bimPacket();
    packet.updated[0].meshes.push({ ...packet.updated[0].meshes[0], positions: [0,0,0, 1,0,0, 0,1,0], indices: [0,1,2] });
    commit(store, packet);
    const exports = new IfcExports(folder);
    const job = await exports.start(store.exportSnapshot('bim-job'));
    const fixedRevision = job.revision;
    const timeout = Date.now() + 60000;
    let result;
    do { await new Promise(resolve => setTimeout(resolve, 100)); result = await exports.get(job.id); }
    while (result.state === 'running' && Date.now() < timeout);
    assert.equal(result.state, 'completed', result.error);
    assert.equal(result.revision, fixedRevision); assert.equal(result.report.validationErrors, 0);
    const path = join(folder, job.id, 'model.ifc');
    const python = process.env.COORDINATOR_PYTHON ?? join(process.env.LOCALAPPDATA ?? homedir(), 'Cmcoordinator/Runtime/Scripts/python.exe');
    const { stdout } = await promisify(execFile)(python, ['ifc/verify.py', path], { windowsHide: true });
    const check = JSON.parse(stdout);
    assert.equal(check.elements, 1); assert.equal(check.triangles, 13); assert.equal(check.guid, '0ABCDEFGHIJKLMNOPQRSTU');
    assert.equal(check.height, 3); assert.equal(check.level, 'Nivel 1'); assert.equal(check.type, 'Muro 200');
    assert.equal(check.materials, 1); assert.equal(check.codes, 'Ñ-001');
    assert.equal(check.boundsMatchUnitCube, true);
    assert.equal(check.materialBoolean, true);
    assert.ok((await readFile(path, 'utf8')).includes('IFC4'));
    assert.equal((await new IfcExports(folder).get(job.id)).state, 'completed');
  } finally {
    store.close(); assert.ok(resolve(folder).startsWith(resolve(tmpdir()) + sep + 'coordinator-ifc-'));
    await rm(folder, { recursive: true, force: true });
  }
});

test('HTTP recibe revisión BIM, genera y descarga IFC/reporte con alcance fijo', async () => {
  const folder = await mkdtemp(join(tmpdir(), 'coordinator-http-ifc-'));
  const server = createLocalServer({ dataPath: join(folder, 'receiver.sqlite') });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  const json = async (path, value) => fetch(base + path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(value) });
  try {
    const packet = bimPacket(), bytes = Buffer.from(JSON.stringify(packet));
    const header = { protocol: 2, jobId: 'http-bim', sessionId: packet.sessionId, sessionStartedAt: packet.sessionStartedAt,
      sourceId: packet.sourceId, title: packet.title, view: packet.view, sequence: 1, reset: true, baseRevision: null,
      byteLength: bytes.length, chunkCount: 1, payloadHash: digest(bytes) };
    assert.equal((await json('/api/v2/jobs', header)).status, 200);
    assert.equal((await fetch(base + '/api/v2/jobs/http-bim/chunks/0', { method: 'POST',
      headers: { 'Content-Type': 'application/octet-stream', 'X-Chunk-Sha256': digest(bytes) }, body: bytes })).status, 200);
    assert.equal((await json('/api/v2/jobs/http-bim/commit', {})).status, 200);
    assert.equal((await json('/api/v2/ifc/exports', { revisionId: 'old' })).status, 409);
    const response = await json('/api/v2/ifc/exports', { revisionId: 'http-bim' });
    assert.equal(response.status, 202); let job = await response.json();
    const timeout = Date.now() + 60000;
    while (job.state === 'running' && Date.now() < timeout) {
      await new Promise(resolve => setTimeout(resolve, 100)); job = await (await fetch(`${base}/api/v2/ifc/exports/${job.id}`)).json();
    }
    assert.equal(job.state, 'completed', job.error);
    const file = await fetch(`${base}/api/v2/ifc/exports/${job.id}/model.ifc`);
    assert.equal(file.status, 200); assert.ok(file.headers.get('content-disposition').includes('attachment'));
    assert.ok((await file.text()).includes('IFC4'));
    const report = await (await fetch(`${base}/api/v2/ifc/exports/${job.id}/report.json`)).json();
    assert.equal(report.revision, 'http-bim'); assert.equal(report.validationErrors, 0);
  } finally {
    await new Promise(resolve => server.close(resolve));
    assert.ok(resolve(folder).startsWith(resolve(tmpdir()) + sep + 'coordinator-http-ifc-'));
    await rm(folder, { recursive: true, force: true });
  }
});
