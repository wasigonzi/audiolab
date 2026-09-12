import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { after, before, test } from 'node:test';

const uploadsDir = await fs.mkdtemp(path.join(os.tmpdir(), 'audiolab-test-'));
process.env.UPLOADS_DIR = uploadsDir;

const { createApp } = await import('../src/app.js');

let server;
let baseUrl;

before(async () => {
  const app = createApp();
  server = app.listen(0, '127.0.0.1');
  await new Promise((resolve) => server.once('listening', resolve));
  baseUrl = `http://127.0.0.1:${server.address().port}`;
});

after(async () => {
  await new Promise((resolve) => server.close(resolve));
  await fs.rm(uploadsDir, { recursive: true, force: true });
});

test('GET /api/health responde ok', async () => {
  const res = await fetch(`${baseUrl}/api/health`);
  assert.equal(res.status, 200);
  const body = await res.json();
  assert.equal(body.status, 'ok');
});

test('la lista empieza vacía', async () => {
  const res = await fetch(`${baseUrl}/api/audio`);
  assert.equal(res.status, 200);
  assert.deepEqual(await res.json(), { count: 0, items: [] });
});

test('sube, lista, descarga y borra un audio', async () => {
  const bytes = new Uint8Array([0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0]);
  const form = new FormData();
  form.append('file', new File([bytes], 'muestra.wav', { type: 'audio/wav' }));

  const upload = await fetch(`${baseUrl}/api/audio`, { method: 'POST', body: form });
  assert.equal(upload.status, 201);
  const created = await upload.json();
  assert.equal(created.sizeBytes, bytes.length);

  const list = await (await fetch(`${baseUrl}/api/audio`)).json();
  assert.equal(list.count, 1);
  assert.equal(list.items[0].id, created.id);

  const download = await fetch(`${baseUrl}${created.url}`);
  assert.equal(download.status, 200);
  assert.equal(new Uint8Array(await download.arrayBuffer()).length, bytes.length);

  const removed = await fetch(`${baseUrl}${created.url}`, { method: 'DELETE' });
  assert.equal(removed.status, 204);
  assert.equal((await (await fetch(`${baseUrl}/api/audio`)).json()).count, 0);
});

test('rechaza un tipo de archivo no soportado', async () => {
  const form = new FormData();
  form.append('file', new File(['hola'], 'notas.txt', { type: 'text/plain' }));

  const res = await fetch(`${baseUrl}/api/audio`, { method: 'POST', body: form });
  assert.equal(res.status, 415);
});

test('una ruta desconocida devuelve 404 en JSON', async () => {
  const res = await fetch(`${baseUrl}/api/nope`);
  assert.equal(res.status, 404);
  assert.match((await res.json()).error, /no encontrada/);
});
