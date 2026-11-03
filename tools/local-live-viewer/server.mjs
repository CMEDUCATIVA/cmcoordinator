import { createLocalServer } from './lib/http-server.mjs';
import { join } from 'node:path';
import { homedir } from 'node:os';

const dataPath = process.env.COORDINATOR_LIVE_DATA ?? join(process.env.LOCALAPPDATA ?? homedir(), 'Cmcoordinator', 'LiveSync', 'receiver.sqlite');
// Cmcoordinator starts it with the port of its Revit version (2022 → 5272 … 2027 → 5277); by hand, 5257.
const port = Number(process.env.COORDINATOR_LIVE_PORT) || 5257;
const server = createLocalServer({ dataPath });
server.on('error', error => {
  console.error(error.code === 'EADDRINUSE' ? `El puerto ${port} ya está ocupado.` : error.message);
  process.exitCode = 1;
});
server.listen(port, '127.0.0.1', () => console.log(`Visor local Revit: http://127.0.0.1:${port}`));
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => {
  server.closeViewers();
  server.close();
});
