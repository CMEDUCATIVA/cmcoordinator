import { validateElementBim, validateContextBim } from './bim-schema.mjs';

export class ProtocolError extends Error {
  constructor(message, status = 400) { super(message); this.status = status; }
}

const requireValue = (condition, message) => {
  if (!condition) throw new ProtocolError(message);
};
const text = (value, limit = 1024) => typeof value === 'string' && value.length > 0 && value.length <= limit;

export function validatePacket(value) {
  requireValue(value && typeof value === 'object' && !Array.isArray(value), 'Objeto JSON requerido');
  requireValue(value.protocol === 1, 'Versión de protocolo no admitida');
  requireValue(text(value.sessionId, 80) && /^[\w-]+$/.test(value.sessionId), 'Sesión inválida');
  requireValue(Number.isSafeInteger(value.sessionStartedAt) && value.sessionStartedAt > 0, 'Inicio de sesión inválido');
  requireValue(text(value.sourceId, 1024), 'Documento de origen requerido');
  requireValue(text(value.title, 512) && text(value.view, 512), 'Documento/vista inválidos');
  requireValue(Number.isSafeInteger(value.sequence) && value.sequence > 0, 'Secuencia inválida');
  requireValue(Number.isSafeInteger(value.baseSequence) && value.baseSequence >= 0, 'Base inválida');
  requireValue(typeof value.reset === 'boolean', 'Reset requerido');
  requireValue(Array.isArray(value.updated) && value.updated.length <= 20000, 'Elementos inválidos');
  requireValue(Array.isArray(value.removed) && value.removed.length <= 20000, 'Bajas inválidas');
  const ids = new Set();
  let vertices = 0;
  const updated = value.updated.map(element => {
    requireValue(element && text(element.id) && !ids.has(element.id), 'Identificador duplicado/inválido');
    ids.add(element.id);
    requireValue(text(element.elementId, 80) && text(element.name, 512) && text(element.category, 512), 'Datos del elemento inválidos');
    requireValue(Array.isArray(element.meshes) && element.meshes.length <= 10000, 'Mallas inválidas');
    requireValue(element.properties && typeof element.properties === 'object' && !Array.isArray(element.properties), 'Propiedades inválidas');
    const properties = Object.entries(element.properties);
    requireValue(properties.length <= 256 && properties.every(([k, v]) => text(k, 512) && typeof v === 'string' && v.length <= 2048), 'Propiedad demasiado grande');
    const meshes = element.meshes.map(mesh => {
      requireValue(mesh && Array.isArray(mesh.positions) && mesh.positions.length % 3 === 0, 'Vértices inválidos');
      requireValue(mesh.positions.length >= 9 && mesh.positions.every(n => Number.isFinite(n) && Math.abs(n) <= 1e9), 'Coordenada inválida');
      const count = mesh.positions.length / 3;
      vertices += count;
      requireValue(vertices <= 750000, 'Demasiados vértices');
      requireValue(Array.isArray(mesh.indices) && mesh.indices.length >= 3 && mesh.indices.length % 3 === 0, 'Triángulos inválidos');
      requireValue(mesh.indices.every(n => Number.isSafeInteger(n) && n >= 0 && n < count), 'Índice fuera de rango');
      requireValue(typeof mesh.color === 'string' && /^#[0-9a-f]{6}$/i.test(mesh.color), 'Color inválido');
      requireValue(Number.isFinite(mesh.opacity) && mesh.opacity >= 0 && mesh.opacity <= 1, 'Opacidad inválida');
      return { positions: [...mesh.positions], indices: [...mesh.indices], color: mesh.color, opacity: mesh.opacity,
        ...(typeof mesh.materialId === 'string' ? { materialId: mesh.materialId } : {}) };
    });
    return { id: element.id, elementId: element.elementId, name: element.name, category: element.category,
      properties: Object.fromEntries(properties), meshes,
      ...(element.bim != null ? { bim: validateElementBim(element.bim) } : {}) };
  });
  const removed = new Set();
  for (const id of value.removed) {
    requireValue(text(id) && !ids.has(id) && !removed.has(id), 'Baja duplicada o también actualizada');
    removed.add(id);
  }
  requireValue(!value.reset || removed.size === 0, 'Una base completa no puede incluir bajas');
  const bim = validateContextBim(value.bim);
  if (bim) requireValue(updated.every(e => e.bim), 'Falta semántica BIM en elementos de revisión enriquecida');
  return { protocol: 1, sessionId: value.sessionId, sessionStartedAt: value.sessionStartedAt, sourceId: value.sourceId, title: value.title,
    view: value.view, sequence: value.sequence, baseSequence: value.baseSequence,
    reset: value.reset, updated, removed: [...removed], ...(bim ? { bim } : {}) };
}
