import { createHash } from 'node:crypto';
import { ProtocolError, validatePacket } from './protocol.mjs';

export class SceneStore {
  #elements = new Map();
  #metadata = null;
  #digest = null;
  #retired = new Set();
  #sizes = new Map();
  #maxSceneBytes;

  constructor({ maxSceneBytes = 32 * 1024 * 1024 } = {}) { this.#maxSceneBytes = maxSceneBytes; }

  status() { return this.#metadata ? { ...this.#metadata, count: this.#elements.size } : { sessionId: null, sequence: 0, count: 0 }; }
  snapshot() { return { ...this.status(), elements: [...this.#elements.values()] }; }

  apply(input) {
    const packet = validatePacket(input);
    const current = this.#metadata;
    const sameSession = current?.sessionId === packet.sessionId;
    const digest = createHash('sha256').update(JSON.stringify(packet)).digest('hex');
    if (sameSession && packet.sequence === current.sequence && digest === this.#digest)
      return { duplicate: true, packet };
    if (this.#retired.has(packet.sessionId))
      throw new ProtocolError('La sesión fue reemplazada; inicia otra desde Revit', 403);
    if (current && ((!sameSession && packet.sessionStartedAt <= current.sessionStartedAt) ||
        (sameSession && packet.sessionStartedAt !== current.sessionStartedAt)))
      throw new ProtocolError('Una sesión antigua no puede reemplazar el modelo actual', 403);
    if (!packet.reset && (!sameSession || packet.sourceId !== current.sourceId || packet.baseSequence !== current.sequence))
      throw new ProtocolError('Se necesita una base completa', 409);
    if (sameSession && packet.sequence <= current.sequence)
      throw new ProtocolError('Secuencia anterior o reutilizada', 409);
    const next = packet.reset ? new Map() : new Map(this.#elements);
    const sizes = packet.reset ? new Map() : new Map(this.#sizes);
    for (const id of packet.removed) { next.delete(id); sizes.delete(id); }
    for (const element of packet.updated) {
      next.set(element.id, element);
      sizes.set(element.id, Buffer.byteLength(JSON.stringify(element)));
    }
    if ([...sizes.values()].reduce((sum, value) => sum + value, 0) > this.#maxSceneBytes)
      throw new ProtocolError('La escena excede el límite de memoria; reduce la vista', 413);
    if (next.size > 20000) throw new ProtocolError('Límite de 20.000 elementos excedido', 413);
    let vertices = 0;
    for (const element of next.values()) for (const mesh of element.meshes) vertices += mesh.positions.length / 3;
    if (vertices > 750000) throw new ProtocolError('La escena excede 750.000 vértices', 413);
    if (current && !sameSession) {
      this.#retired.add(current.sessionId);
      // This is a single-producer example. Bound retained session IDs as well as geometry.
      if (this.#retired.size > 1000) this.#retired.delete(this.#retired.values().next().value);
    }
    this.#elements = next;
    this.#sizes = sizes;
    this.#digest = digest;
    this.#metadata = { sessionId: packet.sessionId, sessionStartedAt: packet.sessionStartedAt, sourceId: packet.sourceId,
      title: packet.title, view: packet.view, sequence: packet.sequence,
      updatedAt: new Date().toISOString(), units: 'm', upAxis: 'Z', ...(packet.bim ? { bim: packet.bim } : {}) };
    return { duplicate: false, packet };
  }
}
