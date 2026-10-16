import { ProtocolError } from './protocol.mjs';

const check = (condition, message) => { if (!condition) throw new ProtocolError(message, 422); };
const record = value => value && typeof value === 'object' && !Array.isArray(value);
function finiteTree(value, depth = 0) {
  check(depth < 16, 'Metadatos BIM demasiado profundos');
  if (value == null || typeof value === 'boolean') return;
  if (typeof value === 'number') { check(Number.isFinite(value), 'Número BIM no finito'); return; }
  if (typeof value === 'string') { check(value.length <= 65536, 'Texto BIM fuera del límite'); return; }
  check(typeof value === 'object', 'Valor BIM no admitido');
  check(Object.keys(value).length <= 20000, 'Colección BIM fuera del límite');
  for (const item of Object.values(value)) finiteTree(item, depth + 1);
}
function parameters(values) {
  check(Array.isArray(values) && values.length <= 4096, 'Lista de parámetros incompleta o fuera del límite');
  const ids = new Set();
  for (const p of values) {
    check(record(p) && typeof p.id === 'string' && typeof p.name === 'string' && !ids.has(p.id), 'Identidad de parámetro inválida/duplicada');
    ids.add(p.id);
    check(['String', 'Integer', 'Double', 'ElementId', 'None'].includes(p.storage), 'Tipo de parámetro no admitido');
    check(typeof p.hasValue === 'boolean', 'Estado de parámetro requerido');
    if (p.hasValue && p.storage === 'Double') check(typeof p.value === 'number' && Number.isFinite(p.value), 'Double BIM inválido');
    if (p.hasValue && p.storage === 'Integer') check(Number.isSafeInteger(p.value), 'Integer BIM inválido');
  }
}
export function validateElementBim(value) {
  if (value == null) return undefined;
  finiteTree(value);
  check(record(value) && typeof value.documentId === 'string' && /^Ifc[A-Za-z]+$/.test(value.ifcClass ?? ''), 'Identidad/clase BIM requerida');
  parameters(value.parameters);
  return structuredClone(value);
}
export function validateContextBim(value) {
  if (value == null) return undefined;
  finiteTree(value);
  check(record(value) && value.schema === 2 && value.scope === 'visible-3d' && typeof value.scopeId === 'string', 'Esquema/alcance BIM no admitido');
  check(Array.isArray(value.documents) && value.documents.length > 0 && value.documents.length <= 128, 'Documentos BIM requeridos');
  for (const doc of value.documents) { check(typeof doc.id === 'string', 'Documento BIM inválido'); parameters(doc.parameters); }
  for (const kind of ['levels', 'types', 'materials']) check(record(value[kind]), 'Catálogo BIM requerido: ' + kind);
  for (const item of Object.values(value.types)) parameters(item.parameters);
  for (const item of Object.values(value.materials)) parameters(item.parameters);
  return structuredClone(value);
}
