import { DatabaseSync } from 'node:sqlite';
import { createHash, randomUUID } from 'node:crypto';
import { mkdirSync } from 'node:fs';
import { dirname } from 'node:path';
import { validatePacket, ProtocolError } from './protocol.mjs';

export const digest = bytes => createHash('sha256').update(bytes).digest('hex');
export const PART_BYTES = 512 * 1024;
const MAX_BYTES = 32 * 1024 * 1024;
const fail = (condition, message, status = 400) => { if (!condition) throw new ProtocolError(message, status); };
const id = value => typeof value === 'string' && /^[a-zA-Z0-9_-]{1,80}$/.test(value);

export class RevisionStore {
  #db;
  #viewerRevision = null;
  constructor(path = ':memory:') {
    if (path !== ':memory:') mkdirSync(dirname(path), { recursive: true });
    this.#db = new DatabaseSync(path);
    this.#db.exec(`PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA busy_timeout=3000; PRAGMA max_page_count=131072;
      CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY,value TEXT);
      CREATE TABLE IF NOT EXISTS jobs (id TEXT PRIMARY KEY,header TEXT NOT NULL,committed INTEGER NOT NULL DEFAULT 0);
      CREATE TABLE IF NOT EXISTS chunks (job TEXT,idx INTEGER,payload BLOB NOT NULL,hash TEXT NOT NULL,PRIMARY KEY(job,idx));
      CREATE TABLE IF NOT EXISTS elements (id TEXT PRIMARY KEY,json TEXT NOT NULL,bytes INTEGER NOT NULL,vertices INTEGER NOT NULL);
      CREATE TABLE IF NOT EXISTS revisions (id TEXT PRIMARY KEY,manifest TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS parts (revision TEXT,kind TEXT,idx INTEGER,payload BLOB NOT NULL,hash TEXT NOT NULL,PRIMARY KEY(revision,kind,idx));`);
    if (!this.#get('epoch')) this.#set('epoch', randomUUID());
  }
  #get(key) { return this.#db.prepare('SELECT value FROM meta WHERE key=?').get(key)?.value; }
  #set(key, value) { this.#db.prepare('INSERT INTO meta VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value').run(key, value); }
  #atomic(action) {
    this.#db.exec('BEGIN IMMEDIATE');
    try { const value = action(); this.#db.exec('COMMIT'); return value; }
    catch (error) { this.#db.exec('ROLLBACK'); throw error; }
  }
  status() {
    const current = JSON.parse(this.#get('current') ?? 'null');
    return { ...current, receiverEpoch: this.#get('epoch'), viewerRevision: this.#viewerRevision };
  }
  exportSnapshot(revision) {
    const current = this.status();
    fail(revision && revision === current.revisionId, 'La revisión cambió; sincroniza y vuelve a generar IFC', 409);
    return { ...current, bim: JSON.parse(this.#get('bim') ?? 'null'),
      elements: this.#db.prepare('SELECT json FROM elements ORDER BY id').all().map(row => JSON.parse(row.json)) };
  }
  manifest(revision = this.status().revisionId, materialize = true) {
    const row = this.#db.prepare('SELECT manifest FROM revisions WHERE id=?').get(revision ?? '');
    fail(row, 'Revisión no disponible', 404);
    const manifest = JSON.parse(row.manifest);
    if (!manifest.baseReady && materialize) {
      const current = this.status();
      fail(current.revisionId === revision, 'La revisión cambió; solicita la actual', 409);
      const json = this.#db.prepare('SELECT json FROM elements ORDER BY id').all().map(r => r.json).join(',');
      const bytes = Buffer.from(JSON.stringify({ ...current, bim: JSON.parse(this.#get('bim') ?? 'null') }).slice(0, -1) + ',"elements":[' + json + ']}');
      Object.assign(manifest, { baseReady: true, byteLength: bytes.length, partCount: Math.ceil(bytes.length / PART_BYTES), payloadHash: digest(bytes) });
      this.#atomic(() => {
        this.#writeParts(revision, 'base', bytes);
        this.#db.prepare('UPDATE revisions SET manifest=? WHERE id=?').run(JSON.stringify(manifest), revision);
      });
    }
    return manifest;
  }
  #writeParts(revision, kind, content) {
    const statement = this.#db.prepare('INSERT INTO parts VALUES(?,?,?,?,?)');
    for (let i = 0; i < Math.ceil(content.length / PART_BYTES); i++) {
      const payload = content.subarray(i * PART_BYTES, (i + 1) * PART_BYTES); statement.run(revision, kind, i, payload, digest(payload));
    }
  }
  part(revision, index, kind = 'base') {
    fail(kind === 'base' || kind === 'delta', 'Tipo de parte inválido');
    const row = this.#db.prepare('SELECT payload,hash FROM parts WHERE revision=? AND kind=? AND idx=?').get(revision, kind, index);
    fail(row, 'Parte no disponible', 404); return row;
  }
  acknowledgeViewer(revision) {
    fail(revision === this.status().revisionId, 'El visor presentó una revisión anterior', 409);
    this.#viewerRevision = revision; return { ok: true };
  }
  begin(header) {
    fail(header?.protocol === 2 && id(header.jobId) && id(header.sessionId), 'Trabajo/protocolo inválido');
    fail(typeof header.sourceId === 'string' && header.sourceId.length > 0 && header.sourceId.length <= 1024, 'Origen inválido');
    fail(typeof header.title === 'string' && header.title.length <= 512 && typeof header.view === 'string' && header.view.length <= 512, 'Modelo/vista inválidos');
    fail(Number.isSafeInteger(header.sessionStartedAt) && header.sessionStartedAt > 0 && Number.isSafeInteger(header.sequence) && header.sequence > 0, 'Secuencia inválida');
    fail(typeof header.reset === 'boolean' && (header.baseRevision === null || id(header.baseRevision)), 'Base inválida');
    fail(Number.isSafeInteger(header.byteLength) && header.byteLength > 0 && header.byteLength <= MAX_BYTES, 'Trabajo demasiado grande', 413);
    fail(header.chunkCount === Math.ceil(header.byteLength / PART_BYTES) && /^[a-f0-9]{64}$/.test(header.payloadHash ?? ''), 'Manifiesto inválido');
    const serialized = JSON.stringify(header);
    const existing = this.#db.prepare('SELECT header,committed FROM jobs WHERE id=?').get(header.jobId);
    if (existing) {
      fail(existing.header === serialized, 'El trabajo es inmutable', 409);
      return { committed: !!existing.committed, received: this.#received(header.jobId) };
    }
    const current = this.status();
    this.#checkBase(header, current);
    for (const stale of this.#db.prepare('SELECT id,header FROM jobs WHERE committed=0').all()) {
      const old = JSON.parse(stale.header);
      if (old.baseRevision !== (current.revisionId ?? null) || old.sessionStartedAt < header.sessionStartedAt) {
        this.#db.prepare('DELETE FROM chunks WHERE job=?').run(stale.id);
        this.#db.prepare('DELETE FROM jobs WHERE id=?').run(stale.id);
      }
    }
    fail(this.#db.prepare('SELECT count(*) count FROM jobs WHERE committed=0').get().count < 4, 'Demasiados trabajos pendientes', 429);
    this.#db.prepare('INSERT INTO jobs(id,header) VALUES(?,?)').run(header.jobId, serialized);
    return { committed: false, received: [] };
  }
  #checkBase(header, current) {
    fail((header.baseRevision ?? null) === (current.revisionId ?? null), 'La base cambió', 409);
    fail(!current.sessionStartedAt || header.sessionStartedAt >= current.sessionStartedAt, 'Otra sesión más reciente gobierna el visor', 403);
    fail(header.reset || (current.sourceId === header.sourceId && current.revisionId), 'Se requiere base completa', 409);
    if (current.sessionStartedAt === header.sessionStartedAt)
      fail(current.sessionId === header.sessionId, 'Identidad de sesión incompatible', 403);
  }
  #job(jobId) {
    const row = this.#db.prepare('SELECT * FROM jobs WHERE id=?').get(jobId);
    fail(row, 'Trabajo no encontrado', 404); return { ...row, value: JSON.parse(row.header) };
  }
  #received(jobId) { return this.#db.prepare('SELECT idx FROM chunks WHERE job=? ORDER BY idx').all(jobId).map(r => r.idx); }
  chunk(jobId, index, bytes, hash) {
    const job = this.#job(jobId), header = job.value;
    fail(!job.committed, 'Trabajo ya confirmado', 409);
    fail(Number.isInteger(index) && index >= 0 && index < header.chunkCount, 'Índice de bloque inválido');
    const expected = index === header.chunkCount - 1 ? header.byteLength - index * PART_BYTES : PART_BYTES;
    fail(bytes.length === expected && bytes.length <= PART_BYTES, 'Tamaño de bloque inválido', 413);
    fail(hash === digest(bytes), 'Checksum de bloque inválido', 422);
    const old = this.#db.prepare('SELECT hash FROM chunks WHERE job=? AND idx=?').get(jobId, index);
    if (old) fail(old.hash === hash, 'Bloque inmutable', 409);
    else this.#db.prepare('INSERT INTO chunks VALUES(?,?,?,?)').run(jobId, index, bytes, hash);
    return { ok: true, index, duplicate: !!old };
  }
  commit(jobId) {
    const job = this.#job(jobId), header = job.value;
    if (job.committed) return { ok: true, revisionId: jobId, duplicate: true };
    this.#checkBase(header, this.status());
    const rows = this.#db.prepare('SELECT payload FROM chunks WHERE job=? ORDER BY idx').all(jobId);
    fail(rows.length === header.chunkCount, 'Faltan bloques', 409);
    const bytes = Buffer.concat(rows.map(row => Buffer.from(row.payload)));
    fail(bytes.length === header.byteLength && digest(bytes) === header.payloadHash, 'Checksum de revisión inválido', 422);
    let input;
    try { input = JSON.parse(bytes.toString('utf8')); } catch { throw new ProtocolError('JSON de revisión inválido', 422); }
    const packet = validatePacket(input);
    for (const key of ['sourceId', 'title', 'view', 'sessionId', 'sessionStartedAt', 'reset', 'sequence'])
      fail(packet[key] === header[key], 'Cabecera y contenido no coinciden', 422);
    const totals = header.reset ? { count: 0, vertices: 0, bytes: 0 } : this.#db.prepare('SELECT count(*) count,coalesce(sum(bytes),0) bytes,coalesce(sum(vertices),0) vertices FROM elements').get();
    const oldElement = this.#db.prepare('SELECT bytes,vertices FROM elements WHERE id=?');
    const subtract = key => {
      const old = header.reset ? null : oldElement.get(key);
      if (old) { totals.count--; totals.bytes -= old.bytes; totals.vertices -= old.vertices; }
    };
    for (const key of packet.removed) subtract(key);
    const encoded = [];
    for (const element of packet.updated) {
      subtract(element.id);
      const json = JSON.stringify(element), size = Buffer.byteLength(json), vertices = element.meshes.reduce((n, mesh) => n + mesh.positions.length / 3, 0);
      totals.count++; totals.bytes += size; totals.vertices += vertices; encoded.push([element.id, json, size, vertices]);
    }
    fail(totals.count <= 20000 && totals.vertices <= 750000 && totals.bytes <= MAX_BYTES, 'Límite acumulado de escena excedido', 413);
    const metadata = { revisionId: jobId, sourceId: packet.sourceId, title: packet.title, view: packet.view,
      sessionId: packet.sessionId, sessionStartedAt: packet.sessionStartedAt, sequence: packet.sequence,
      count: totals.count, updatedAt: new Date().toISOString(), units: 'm', upAxis: 'Z' };
    const delta = Buffer.from(JSON.stringify({ ...metadata, bim: packet.bim ?? null, elements: packet.updated, removed: packet.removed }));
    const manifest = { ...metadata, baseRevision: header.baseRevision, reset: header.reset,
      baseReady: false, partCount: 0, byteLength: 0, payloadHash: null,
      delta: { partCount: Math.ceil(delta.length / PART_BYTES), byteLength: delta.length, payloadHash: digest(delta) } };
    return this.#atomic(() => {
      if (header.reset) this.#db.exec('DELETE FROM elements');
      const remove = this.#db.prepare('DELETE FROM elements WHERE id=?');
      for (const key of packet.removed) remove.run(key);
      const insert = this.#db.prepare('INSERT INTO elements VALUES(?,?,?,?) ON CONFLICT(id) DO UPDATE SET json=excluded.json,bytes=excluded.bytes,vertices=excluded.vertices');
      for (const row of encoded) insert.run(...row);
      this.#db.prepare('INSERT INTO revisions VALUES(?,?)').run(jobId, JSON.stringify(manifest));
      this.#writeParts(jobId, 'delta', delta);
      this.#set('current', JSON.stringify(metadata));
      this.#set('bim', JSON.stringify(packet.bim ?? null));
      this.#db.prepare('UPDATE jobs SET committed=1 WHERE id=?').run(jobId);
      // Old unfinished work targets an obsolete base and cannot be committed anymore.
      this.#db.prepare('DELETE FROM chunks WHERE job IN (SELECT id FROM jobs WHERE committed=0) OR job=?').run(jobId);
      this.#db.exec('DELETE FROM jobs WHERE committed=0');
      this.#db.exec('DELETE FROM parts WHERE revision NOT IN (SELECT id FROM revisions ORDER BY rowid DESC LIMIT 2)');
      this.#db.exec('DELETE FROM revisions WHERE id NOT IN (SELECT id FROM revisions ORDER BY rowid DESC LIMIT 2)');
      this.#db.exec('DELETE FROM jobs WHERE id NOT IN (SELECT id FROM jobs ORDER BY rowid DESC LIMIT 100)');
      return { ok: true, revisionId: jobId, duplicate: false };
    });
  }
  close() { this.#db.close(); }
}
