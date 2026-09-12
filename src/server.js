import fs from 'node:fs/promises';
import { createApp } from './app.js';
import { config } from './config.js';

await fs.mkdir(config.uploadsDir, { recursive: true });

const app = createApp();
const server = app.listen(config.port, config.host, () => {
  const { address, port } = server.address();
  const shown = address === '0.0.0.0' || address === '::' ? 'localhost' : address;
  console.log(`audiolab escuchando en http://${shown}:${port}`);
  console.log(`Subidas en: ${config.uploadsDir}`);
});

for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => {
    console.log(`\n${signal} recibido, cerrando servidor...`);
    server.close(() => process.exit(0));
  });
}
