import test from 'node:test';
import assert from 'node:assert/strict';
import { SceneStore } from '../lib/scene-store.mjs';
import { SceneState } from '../public/scene-state.js';
import { packet, cube } from './fixtures.mjs';

test('base real de 12 triángulos, modificación y eliminación sin geometría obsoleta', () => {
  const store = new SceneStore();
  store.apply(packet());
  assert.equal(store.snapshot().elements[0].meshes[0].indices.length / 3, 12);
  const moved = cube();
  moved.meshes[0].positions = moved.meshes[0].positions.map((value, i) => value + (i % 3 === 0 ? 5 : 0));
  store.apply(packet({ sequence: 2, baseSequence: 1, reset: false, updated: [moved, cube('element-2')] }));
  assert.equal(store.snapshot().elements.length, 2);
  assert.equal(store.snapshot().elements[0].meshes[0].positions[0], 5);
  store.apply(packet({ sequence: 3, baseSequence: 2, reset: false, updated: [], removed: ['element-1', 'element-2'] }));
  assert.equal(store.snapshot().elements.length, 0);
});

test('reintentos idempotentes y rechazo de secuencia/base incorrectas', () => {
  const store = new SceneStore();
  const first = packet();
  store.apply(first);
  assert.equal(store.apply(first).duplicate, true);
  assert.throws(() => store.apply(packet({ title: 'Cambio con secuencia repetida' })), { status: 409 });
  assert.throws(() => store.apply(packet({ reset: false, sequence: 3, baseSequence: 0 })), { status: 409 });
  assert.equal(store.status().sequence, 1);
});

test('reinicio requiere base y una sesión anterior no reemplaza al nuevo documento', () => {
  const store = new SceneStore();
  assert.throws(() => store.apply(packet({ reset: false, sequence: 5, baseSequence: 4 })), { status: 409 });
  store.apply(packet({ sequence: 6 }));
  store.apply(packet({ sessionId: 'session-2', sessionStartedAt: 2000, sourceId: 'doc-2', updated: [] }));
  assert.equal(store.status().count, 0);
  assert.throws(() => store.apply(packet({ sequence: 7 })), { status: 403 });
  assert.equal(store.status().sourceId, 'doc-2');
  assert.throws(() => store.apply(packet({ sessionId: 'late-unknown-session', sessionStartedAt: 1500 })), { status: 403 });
});

test('el límite acumulado de escena no se elude mediante deltas pequeños', () => {
  const store = new SceneStore({ maxSceneBytes: 800 });
  store.apply(packet());
  assert.throws(() => store.apply(packet({ sequence: 2, baseSequence: 1, reset: false,
    updated: [cube('element-2'), cube('element-3')] })), { status: 413 });
  assert.equal(store.snapshot().elements.length, 1);
  assert.equal(store.status().sequence, 1);
});

test('paquetes inválidos no dejan escrituras parciales', () => {
  const store = new SceneStore();
  store.apply(packet());
  for (const mutate of [
    p => { p.updated[0].meshes[0].indices[0] = 99; },
    p => { p.updated[0].meshes[0].positions[0] = Infinity; },
    p => { p.updated[0].meshes[0].opacity = -1; },
    p => { p.updated.push(p.updated[0]); },
    p => { p.removed = ['element-1']; },
    p => { p.sequence = Number.MAX_SAFE_INTEGER + 1; }
  ]) {
    const invalid = packet({ sequence: 2 });
    mutate(invalid);
    assert.throws(() => store.apply(invalid), { status: 400 });
    assert.equal(store.status().sequence, 1);
  }
});

test('estado del navegador detecta huecos y conserva selección por identidad', () => {
  const state = new SceneState();
  state.snapshot({ sessionId: 'session-1', sequence: 1, elements: [cube()] });
  assert.equal(state.patch(packet({ reset: false, sequence: 3, baseSequence: 2 })), false);
  assert.equal(state.sequence, 1);
  assert.equal(state.patch(packet({ reset: false, sequence: 2, baseSequence: 1, updated: [], removed: ['element-1'] })), true);
  assert.equal(state.elements.size, 0);
  state.snapshot({ sessionId: 'session-2', sequence: 1, elements: [cube('other-doc')] });
  assert.equal(state.elements.has('element-1'), false);
});
