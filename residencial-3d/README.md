# Complejo en Herradura — modelo 3D

Reconstrucción volumétrica en 3D, a partir de una fotografía aérea, de un
conjunto de vivienda multifamiliar: cuatro bloques dispuestos en herradura
alrededor de un patio de estacionamiento con isla central de área comunal.

`index.html` es autocontenido (una sola página, sin build). Ábrelo en el
navegador o sírvelo estáticamente; la única dependencia externa es three.js
r128, cargado desde cdnjs.

## Qué modela

| Elemento | Detalle |
|---|---|
| Bloques A–D | 2 y 3 niveles, corredores-balcón, torres de escalera, parapetos, cisternas y equipos en azotea |
| Isla central | centro comunal con techo a dos aguas, cancha techada, parque infantil, edificio de administración, pérgola, bancos |
| Vialidad | lazo de asfalto con acera y bordillo, ~103 plazas marcadas, plazas accesibles, rampa de acceso |
| Calle | calzada con marcas, contén, aceras, muro perimetral, postes del tendido con cables |
| Entorno | terreno con ladera boscosa, ~1 500 árboles instanciados, palmas |

## Escala

Las dimensiones se estiman de la foto tomando el ancho de crujía (6.0 m) y la
altura de entrepiso (2.9 m) como referencia. Las cifras del panel (unidades,
plazas, huella) se cuentan sobre la geometría generada, no están escritas a mano.

## Archivo .glb

`dist/complejo-en-herradura.glb` es la misma escena exportada a glTF binario
(métrico, Y arriba, ~101 000 triángulos, 5.4 MB). Abre en Blender, Godot,
Unity, Windows 3D Viewer, `gltf-viewer`, etc.

La geometría es procedural y sólo existe cuando la página corre, así que la
exportación abre `index.html` en Chromium headless y vuelca la escena:

```sh
cd tools && npm install
node export-glb.mjs                      # opciones por defecto
node export-glb.mjs --radio 0 --terreno 0 # escena completa (~47 MB)
```

Al exportar se hornean las mallas instanciadas (glTF no lleva instancias de
forma portátil), se sueldan vértices, se descartan las UV —el modelo no usa
texturas— y se recortan el arbolado lejano y el plano de terreno, que no
aportan nada al conjunto y se llevaban el 90 % del peso.

El .glb no incluye luces ni cielo: son del visor. Los colores van en
`baseColorFactor` lineal y el terreno lleva su degradado en `COLOR_0`.

## Controles

- Arrastrar: orbitar · Shift o botón derecho: desplazar · Rueda o pellizco: acercar
- Vistas: Aérea · Planta · Patio · Bloque A · Calle
- Deslizador de hora solar (06:00–19:00): cambia sol, sombras, cielo y bruma
- Rótulos y órbita automática conmutables

## Implementación

- three.js r128 (UMD). Órbita de cámara y fusión de geometrías escritas a mano,
  sin complementos.
- Toda la geometría es procedural. Se acumula por material y se fusiona en una
  malla por material, de modo que la escena completa son ~30 llamadas de dibujo;
  árboles y vehículos usan `InstancedMesh`.
- El cielo es un shader propio: degradado según la altura del sol más nubes fBm.
- Terreno: malla desplazada por ruido, enmascarada para quedar plana en el predio
  y elevarse en ladera detrás del conjunto.
