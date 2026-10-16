import test from 'node:test';
import assert from 'node:assert/strict';
import { RenderQueue } from '../public/render-queue.js';

function frames() {
  const callbacks = new Map(); let id = 0, clock = 0;
  const queue = new RenderQueue({ frame: callback => { callbacks.set(++id, callback); return id; },
    cancelFrame: key => callbacks.delete(key), now: () => clock, budgetMs: 6 });
  return { queue, tick: () => { const [key, callback] = callbacks.entries().next().value; callbacks.delete(key); callback(); },
    cost: () => { clock += 4; }, pending: () => callbacks.size };
}
test('render se reparte entre frames y no excede el presupuesto entre unidades', async () => {
  const f = frames(), seen = [];
  const done = f.queue.run([1,2,3,4,5], item => { seen.push(item); f.cost(); });
  f.tick(); assert.deepEqual(seen, [1,2]);
  f.tick(); assert.deepEqual(seen, [1,2,3,4]);
  f.tick(); await done; assert.deepEqual(seen, [1,2,3,4,5]); assert.equal(f.pending(), 0);
});
test('cancelar revisión libera el frame pendiente y evita uploads obsoletos', async () => {
  const f = frames(), abort = new AbortController(), seen = [];
  const done = f.queue.run([1,2,3], item => seen.push(item), { signal: abort.signal });
  abort.abort(); await assert.rejects(done, { name: 'AbortError' });
  assert.deepEqual(seen, []); assert.equal(f.pending(), 0);
});

test('APIs de frames predeterminadas conservan receptor global al dibujar, terminar y cancelar', async () => {
  const names = ['requestAnimationFrame', 'cancelAnimationFrame'];
  const originals = names.map(name => Object.getOwnPropertyDescriptor(globalThis, name));
  const callbacks = new Map(); let id = 0, cancellations = 0;
  Object.defineProperty(globalThis, names[0], { configurable: true, value: function (callback) {
    if (this !== globalThis) throw new TypeError('Illegal invocation: requestAnimationFrame');
    callbacks.set(++id, callback); return id;
  } });
  Object.defineProperty(globalThis, names[1], { configurable: true, value: function (handle) {
    if (this !== globalThis) throw new TypeError('Illegal invocation: cancelAnimationFrame');
    callbacks.delete(handle); cancellations++;
  } });
  const tick = () => {
    const [handle, callback] = callbacks.entries().next().value;
    callbacks.delete(handle); callback();
  };
  try {
    const queue = new RenderQueue({ now: () => 0 }), seen = [];
    const items = Array.from({ length: 25 }, (_, i) => i);
    const done = queue.run(items, item => seen.push(item));
    // Await an early rejection too, so a receiver failure reports the actual TypeError.
    if (callbacks.size === 0) await done;
    tick(); assert.equal(seen.length, 12);
    tick(); assert.equal(seen.length, 24);
    tick(); await done;
    assert.deepEqual(seen, items); assert.equal(callbacks.size, 0);

    const empty = queue.run([], () => assert.fail('Empty cleanup must not visit items'));
    tick(); await empty;
    const abort = new AbortController();
    const pending = queue.run([1], () => assert.fail('Cancelled work must not run'), { signal: abort.signal });
    const rejected = assert.rejects(pending, { name: 'AbortError' });
    abort.abort(); await rejected;
    assert.equal(callbacks.size, 0); assert.equal(cancellations, 3);
  } finally {
    for (const [index, name] of names.entries()) {
      if (originals[index]) Object.defineProperty(globalThis, name, originals[index]);
      else delete globalThis[name];
    }
  }
});
