# audiolab

Servidor HTTP en Node + Express para subir, listar, reproducir y borrar archivos de audio.

## Requisitos

- Node.js >= 20.6

## Instalación

```bash
npm install
```

## Iniciar el servidor

```bash
npm run dev    # con recarga automática (node --watch)
npm start      # modo normal
```

Por defecto queda en <http://localhost:3000>, que sirve una página de prueba para
subir y reproducir audios.

## Variables de entorno

| Variable           | Por defecto           | Descripción                          |
| ------------------ | --------------------- | ------------------------------------ |
| `PORT`             | `3000`                | Puerto de escucha                    |
| `HOST`             | `0.0.0.0`             | Interfaz de escucha                  |
| `UPLOADS_DIR`      | `./storage/uploads`   | Carpeta donde se guardan los audios  |
| `MAX_UPLOAD_BYTES` | `52428800` (50 MB)    | Tamaño máximo por archivo            |

## API

| Método   | Ruta              | Descripción                                        |
| -------- | ----------------- | -------------------------------------------------- |
| `GET`    | `/api/health`     | Estado del servidor y uptime                       |
| `GET`    | `/api/audio`      | Lista los audios subidos                           |
| `POST`   | `/api/audio`      | Sube un audio (`multipart/form-data`, campo `file`) |
| `GET`    | `/api/audio/:id`  | Descarga o reproduce un audio                      |
| `DELETE` | `/api/audio/:id`  | Borra un audio                                     |

Ejemplo:

```bash
curl -F file=@muestra.wav http://localhost:3000/api/audio
curl http://localhost:3000/api/audio
```

Formatos aceptados: mp3, mp4/aac, ogg, wav, webm y flac. Un tipo no soportado
devuelve `415`; un archivo demasiado grande, `413`.

## Tests

```bash
npm test
```
