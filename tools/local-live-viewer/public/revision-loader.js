import * as THREE from 'three';
import { RenderQueue } from './render-queue.js';

export class RevisionLoader {
  constructor(renderer, { progress, applied, error }) {
    Object.assign(this, { renderer, progress, applied, error });
    this.queue = new RenderQueue(); this.elements = new Map(); this.revisionId = null;
  }
  load(manifest) {
    if (manifest.revisionId === this.revisionId) { this.confirm(manifest.revisionId); return; }
    if (this.loadingId === manifest.revisionId) return;
    this.cancel?.();
    this.loadingId = manifest.revisionId;
    const controller = new AbortController();
    const worker = new Worker('/model-worker.js', { type: 'module' });
    const mode = !manifest.reset && this.revisionId === manifest.baseRevision ? 'delta' : 'base';
    const groups = mode === 'delta' ? new Map(this.renderer.groups) : new Map();
    const elements = mode === 'delta' ? new Map(this.elements) : new Map();
    const created = []; let origin, bim, chain = Promise.resolve(), completed = false;
    const started = performance.now();
    const cleanup = () => this.queue.run(created, group => this.renderer.disposeGroup(group)).catch(() => {});
    const cancel = () => {
      controller.abort(); worker.terminate();
      if (!completed) cleanup();
    };
    this.cancel = cancel;
    const fail = message => {
      if (controller.signal.aborted) return;
      cancel(); this.loadingId = null; this.error(message); this.telemetry('VIEWER_LOAD_FAILED', performance.now() - started);
    };
    worker.onerror = () => fail('No se pudo preparar la geometría del visor');
    worker.onmessage = event => {
      if (controller.signal.aborted) return;
      const message = event.data;
      if (message.kind === 'progress') this.progress(message.stage, message.done, message.total);
      if (message.kind === 'error') fail(message.message);
      if (message.kind === 'begin') {
        bim = message.bim;
        origin = new THREE.Vector3(...message.origin);
        for (const id of message.removed) { groups.delete(id); elements.delete(id); }
      }
      if (message.kind === 'elements') {
        chain = chain.then(() => this.queue.run(message.elements, element => {
          const group = this.renderer.build(element, origin); created.push(group);
          groups.set(element.id, group); elements.set(element.id, element);
        }, { signal: controller.signal, progress: (done, total) => this.progress('Preparando visor', done, total) }));
        chain.catch(() => {});
      }
      if (message.kind === 'end') {
        chain.then(async () => {
          if (controller.signal.aborted) return;
          const retired = this.renderer.swap(groups, origin ?? this.renderer.origin);
          completed = true; this.elements = elements; this.revisionId = manifest.revisionId; this.loadingId = null;
          worker.terminate(); this.cancel = null;
          this.queue.run(retired, group => this.renderer.disposeGroup(group)).catch(() => {});
          await this.renderer.afterRender();
          if (controller.signal.aborted || this.revisionId !== manifest.revisionId) return;
          this.applied({ ...manifest, bim, elements: [...elements.values()] });
          this.telemetry('VIEWER_APPLIED', performance.now() - started);
          // Presentation confirmation is separate from durable receiver commit.
          this.confirm(manifest.revisionId);
        }).catch(error => { if (error.name !== 'AbortError') fail(error.message); });
      }
    };
    this.progress('Recibiendo', 0, (mode === 'delta' ? manifest.delta : manifest).partCount);
    worker.postMessage({ manifest, mode, origin: this.renderer.origin.toArray() });
  }
  telemetry(code, durationMs) {
    fetch('/api/v2/telemetry', { method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ code, durationMs: Math.round(durationMs) }) }).catch(() => {});
  }
  async confirm(revisionId) {
    clearTimeout(this.confirmTimer);
    try {
      const response = await fetch('/api/v2/viewer-ack', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ revisionId }) });
      if (!response.ok && response.status !== 409) throw new Error('Confirmación pendiente');
    } catch {
      this.confirmTimer = setTimeout(() => { if (this.revisionId === revisionId) this.confirm(revisionId); }, 3000);
    }
  }
}
