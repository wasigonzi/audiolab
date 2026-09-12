import crypto from 'node:crypto';
import fs from 'node:fs/promises';
import path from 'node:path';
import { Router } from 'express';
import multer from 'multer';
import { config } from '../config.js';

const storage = multer.diskStorage({
  destination: (req, file, cb) => cb(null, config.uploadsDir),
  filename: (req, file, cb) => {
    const ext = path.extname(file.originalname).toLowerCase().slice(0, 10);
    cb(null, `${Date.now()}-${crypto.randomUUID()}${ext}`);
  },
});

const upload = multer({
  storage,
  limits: { fileSize: config.maxUploadBytes, files: 1 },
  fileFilter: (req, file, cb) => {
    if (config.allowedMimeTypes.includes(file.mimetype)) return cb(null, true);
    const err = new Error(`Tipo de archivo no soportado: ${file.mimetype}`);
    err.status = 415;
    cb(err);
  },
});

// Solo aceptamos los nombres que el propio servidor genera, para que un id de
// la petición nunca pueda escapar del directorio de subidas.
const idPattern = /^[0-9]+-[0-9a-f-]{36}(\.[A-Za-z0-9]{1,10})?$/;

function resolveUpload(id) {
  if (!idPattern.test(id)) return null;
  return path.join(config.uploadsDir, id);
}

export const audioRouter = Router();

audioRouter.get('/audio', async (req, res, next) => {
  try {
    const entries = await fs.readdir(config.uploadsDir, { withFileTypes: true });
    const items = await Promise.all(
      entries
        .filter((entry) => entry.isFile() && idPattern.test(entry.name))
        .map(async (entry) => {
          const stat = await fs.stat(path.join(config.uploadsDir, entry.name));
          return {
            id: entry.name,
            sizeBytes: stat.size,
            uploadedAt: stat.mtime.toISOString(),
            url: `/api/audio/${entry.name}`,
          };
        }),
    );
    items.sort((a, b) => b.uploadedAt.localeCompare(a.uploadedAt));
    res.json({ count: items.length, items });
  } catch (err) {
    next(err);
  }
});

audioRouter.post('/audio', upload.single('file'), (req, res) => {
  if (!req.file) {
    return res.status(400).json({ error: 'Falta el archivo en el campo "file".' });
  }
  res.status(201).json({
    id: req.file.filename,
    originalName: req.file.originalname,
    mimeType: req.file.mimetype,
    sizeBytes: req.file.size,
    url: `/api/audio/${req.file.filename}`,
  });
});

audioRouter.get('/audio/:id', (req, res, next) => {
  const filePath = resolveUpload(req.params.id);
  if (!filePath) return res.status(404).json({ error: 'Audio no encontrado.' });

  res.sendFile(filePath, (err) => {
    if (!err) return;
    if (err.code === 'ENOENT') return res.status(404).json({ error: 'Audio no encontrado.' });
    next(err);
  });
});

audioRouter.delete('/audio/:id', async (req, res, next) => {
  const filePath = resolveUpload(req.params.id);
  if (!filePath) return res.status(404).json({ error: 'Audio no encontrado.' });

  try {
    await fs.unlink(filePath);
    res.status(204).end();
  } catch (err) {
    if (err.code === 'ENOENT') return res.status(404).json({ error: 'Audio no encontrado.' });
    next(err);
  }
});
