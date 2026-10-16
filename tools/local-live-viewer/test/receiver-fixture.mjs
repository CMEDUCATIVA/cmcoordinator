import { createLocalServer } from '../lib/http-server.mjs';

const server = createLocalServer({ dataPath: process.argv[2] });
server.listen(Number(process.argv[3] ?? 0), '127.0.0.1', () => console.log(JSON.stringify({ port: server.address().port })));
process.stdin.once('data', () => {
  server.closeAllConnections();
  server.close(() => process.stdin.destroy());
});
