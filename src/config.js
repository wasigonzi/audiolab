import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const rootDir = path.resolve(here, '..');

export const config = {
  port: Number(process.env.PORT ?? 3000),
  host: process.env.HOST ?? '0.0.0.0',
  rootDir,
  publicDir: path.join(rootDir, 'public'),
  uploadsDir: process.env.UPLOADS_DIR ?? path.join(rootDir, 'storage', 'uploads'),
  // Límite por archivo subido, en bytes (50 MB por defecto).
  maxUploadBytes: Number(process.env.MAX_UPLOAD_BYTES ?? 50 * 1024 * 1024),
  allowedMimeTypes: [
    'audio/mpeg',
    'audio/mp4',
    'audio/aac',
    'audio/ogg',
    'audio/wav',
    'audio/x-wav',
    'audio/webm',
    'audio/flac',
    'audio/x-flac',
  ],
};
