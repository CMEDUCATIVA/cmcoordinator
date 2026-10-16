// A single geometry/upload unit is indivisible; yield between units and cap work per frame.
export class RenderQueue {
  constructor({ frame = callback => globalThis.requestAnimationFrame(callback),
    cancelFrame = handle => globalThis.cancelAnimationFrame(handle),
    now = () => performance.now(), budgetMs = 6 } = {}) {
    Object.assign(this, { frame, cancelFrame, now, budgetMs });
  }
  run(items, visit, { signal, progress = () => {} } = {}) {
    return new Promise((resolve, reject) => {
      let index = 0, handle, finished = false;
      const finish = error => {
        if (finished) return; finished = true;
        this.cancelFrame(handle); signal?.removeEventListener('abort', abort);
        items = []; visit = null;
        if (error) reject(error); else resolve();
      };
      const abort = () => finish(new DOMException('Revisión reemplazada', 'AbortError'));
      const step = () => {
        if (signal?.aborted) return abort();
        const start = this.now(); let count = 0;
        try {
          while (index < items.length && count < 12 && (count === 0 || this.now() - start < this.budgetMs)) {
            visit(items[index++]); count++;
          }
          progress(index, items.length);
          if (index === items.length) finish(); else handle = this.frame(step);
        } catch (error) { finish(error); }
      };
      if (signal?.aborted) return abort();
      signal?.addEventListener('abort', abort, { once: true });
      handle = this.frame(step);
    });
  }
}
