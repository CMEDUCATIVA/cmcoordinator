import { spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdir, writeFile, readFile, stat } from 'node:fs/promises';
import { createReadStream } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { homedir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { ProtocolError } from './protocol.mjs';
import { readBody } from './revision-api.mjs';

const codeRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
export class IfcExports {
  constructor(directory) { this.directory = directory; this.jobs = new Map(); this.active = null; }
  async start(snapshot) {
    if (!this.directory) throw new ProtocolError('Exportación requiere almacenamiento local persistente', 422);
    if (this.active) throw new ProtocolError('Ya se está generando un IFC. Espera a que termine.', 409);
    if (snapshot.bim?.schema !== 2 || snapshot.elements.some(e => !e.bim))
      throw new ProtocolError('Abre el modelo y sincroniza con Cmcoordinator para capturar sus datos BIM.', 422);
    const id = randomUUID();
    const job = { id, revision: snapshot.revisionId, state: 'preparing', createdAt: new Date().toISOString(), elements: snapshot.elements.length };
    const folder = join(this.directory, id);
    this.active = id; this.jobs.set(id, job);
    try {
      await mkdir(folder, { recursive: true });
      await writeFile(join(folder, 'snapshot.json'), JSON.stringify(snapshot));
      job.state = 'running'; await this.save(job);
      this.execute(job, folder).catch(() => {});
      return { ...job };
    } catch (error) { this.active = null; this.jobs.delete(id); throw error; }
  }
  async execute(job, folder) {
    try {
      const python = process.env.COORDINATOR_PYTHON ?? join(process.env.LOCALAPPDATA ?? homedir(), 'Cmcoordinator', 'Runtime', 'Scripts', 'python.exe');
      await new Promise((resolveRun, reject) => {
        const child = spawn(python, [join(codeRoot, 'ifc/export.py'), join(folder, 'snapshot.json'), join(folder, 'model.ifc'), join(folder, 'report.json')],
          { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], shell: false });
        let errors = '', output = '';
        const timeout = setTimeout(() => { child.kill(); reject(new Error('La generación IFC superó 5 minutos. Reduce el alcance y revisa el diagnóstico.')); }, 300000);
        child.stdout.on('data', value => { output = (output + value).slice(-8192); });
        child.stderr.on('data', value => { errors = (errors + value).slice(-8192); });
        child.once('error', error => { clearTimeout(timeout); reject(error); });
        child.once('close', code => { clearTimeout(timeout); code === 0 ? resolveRun() : reject(new Error(errors || output || 'Falló el generador IFC')); });
      });
      const report = JSON.parse(await readFile(join(folder, 'report.json'), 'utf8'));
      job.state = 'completed'; job.report = report; job.bytes = (await stat(join(folder, 'model.ifc'))).size;
    } catch (error) {
      job.state = 'failed'; job.error = error.code === 'ENOENT' ? 'No está instalado el runtime IFC de Cmcoordinator.' : error.message;
    } finally { job.finishedAt = new Date().toISOString(); this.active = null; await this.save(job); }
    while (this.jobs.size > 32) this.jobs.delete(this.jobs.keys().next().value);
  }
  async save(job) { await writeFile(join(this.directory, job.id, 'job.json'), JSON.stringify(job)); }
  async get(id) {
    if (!/^[\da-f-]{36}$/.test(id)) throw new ProtocolError('Exportación no encontrada', 404);
    if (this.jobs.has(id)) return this.jobs.get(id);
    try {
      const job = JSON.parse(await readFile(join(this.directory, id, 'job.json'), 'utf8'));
      if (job.state === 'running' || job.state === 'preparing') return { ...job, state: 'failed', error: 'La generación se interrumpió al reiniciar. Puedes generar otra revisión.' };
      return job;
    } catch { throw new ProtocolError('Exportación no encontrada', 404); }
  }
  async api({ request, response, url, store, json }) {
    const prefix = '/api/v2/ifc/exports';
    if (!url.pathname.startsWith(prefix)) return false;
    if (request.method === 'POST' && url.pathname === prefix) {
      if (!request.headers['content-type']?.startsWith('application/json')) throw new ProtocolError('JSON requerido', 415);
      const { revisionId } = await readBody(request, 1024);
      json(202, await this.start(store.exportSnapshot(revisionId))); return true;
    }
    const match = url.pathname.match(/^\/api\/v2\/ifc\/exports\/([\da-f-]{36})(?:\/(model\.ifc|report\.json))?$/);
    if (request.method !== 'GET' || !match) throw new ProtocolError('Ruta de exportación no encontrada', 404);
    const job = await this.get(match[1]);
    if (!match[2]) { json(200, job); return true; }
    if (job.state !== 'completed') throw new ProtocolError('El archivo todavía no está disponible', 409);
    const path = join(this.directory, match[1], match[2]);
    const file = await stat(path);
    response.writeHead(200, { 'Content-Type': match[2].endsWith('.ifc') ? 'application/x-step' : 'application/json',
      'Content-Length': file.size, 'Content-Disposition': `attachment; filename="Cmcoordinator-${job.revision.slice(0, 8)}-${match[2]}"` });
    createReadStream(path).on('error', () => response.destroy()).pipe(response); return true;
  }
}
