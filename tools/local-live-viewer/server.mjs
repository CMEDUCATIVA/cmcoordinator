import { createLocalServer } from './lib/http-server.mjs';
import { join } from 'node:path';
import { homedir } from 'node:os';

const dataPath = process.env.COORDINATOR_LIVE_DATA ?? join(process.env.LOCALAPPDATA ?? homedir(), 'Cmcoordinator', 'LiveSync', 'receiver.sqlite');
const server = createLocalServer({ dataPath });
server.on('error', error => {
  console.error(error.code === 'EADDRINUSE' ? 'El puerto 5257 ya está ocupado.' : error.message);
  process.exitCode = 1;
});
server.listen(5257, '127.0.0.1', () => console.log('Visor local Revit: http://127.0.0.1:5257'));
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => {
  server.closeViewers();
  server.close();
});
