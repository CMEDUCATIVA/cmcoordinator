import { mkdir, stat, rename, appendFile } from 'node:fs/promises';
import { join } from 'node:path';

export function createSyncLog(directory) {
  let chain = Promise.resolve(), pending = 0;
  return (stage, code, fields = {}) => {
    if (!directory || pending >= 256) return;
    pending++;
    chain = chain.then(async () => {
      await mkdir(directory, { recursive: true });
      const path = join(directory, 'receiver.jsonl');
      const size = await stat(path).then(value => value.size).catch(() => 0);
      if (size > 5 * 1024 * 1024) for (let i = 4; i >= 1; i--)
        await rename(i === 1 ? path : `${path}.${i - 1}`, `${path}.${i}`).catch(() => {});
      await appendFile(path, JSON.stringify({ timestampUtc: new Date().toISOString(), stage, code, protocol: 2, ...fields }) + '\n');
    }).catch(() => { /* Diagnostic disk errors must not create an unhandled server rejection. */ }).finally(() => { pending--; });
  };
}
