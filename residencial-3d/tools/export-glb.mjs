/**
 * Exporta la escena de index.html a un archivo .glb.
 *
 * La geometría del modelo es procedural y sólo existe una vez que la página
 * corre, así que la exportación abre index.html en Chromium headless, hornea
 * las mallas instanciadas (árboles y vehículos) en geometría real —glTF no
 * lleva instancias de forma portátil— y usa GLTFExporter de three.
 *
 *   npm install            (dentro de tools/)
 *   node export-glb.mjs [--out ../dist/complejo-en-herradura.glb] [--radio 240]
 *
 * --radio N  descarta el arbolado a más de N metros del centro del predio
 *            (0 = sin recorte). El monte lejano es puro decorado y se lleva
 *            la mayor parte del peso, así que por defecto se recorta.
 * --terreno N  recorta el terreno a un cuadrado de ±N metros alrededor del
 *            predio (por defecto radio × 1.5). El plano completo mide 1.8 km.
 */
import { chromium } from 'playwright';
import { fileURLToPath } from 'node:url';
import { dirname, resolve, join } from 'node:path';
import { readFileSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';

const here = dirname(fileURLToPath(import.meta.url));
const argv = process.argv.slice(2);
const arg = (n, d) => { const i = argv.indexOf(n); return i >= 0 ? argv[i + 1] : d; };

const out = resolve(here, arg('--out', '../dist/complejo-en-herradura.glb'));
const radio = Number(arg('--radio', '240'));
const terreno = Number(arg('--terreno', String((radio || 240) * 1.5)));

const threePath = join(here, 'node_modules/three/build/three.min.js');
const expPath = join(here, 'node_modules/three/examples/js/exporters/GLTFExporter.js');
const utilPath = join(here, 'node_modules/three/examples/js/utils/BufferGeometryUtils.js');
for (const p of [threePath, expPath, utilPath]) {
  if (!existsSync(p)) { console.error('Falta ' + p + '\nEjecuta "npm install" dentro de tools/.'); process.exit(1); }
}

// index.html carga three desde CDN; para exportar sin red usamos la copia local
const page_html = readFileSync(join(here, '../index.html'), 'utf8')
  .replace(/<script src="https:\/\/cdnjs[^"]*"><\/script>/,
    '<script src="three.min.js"></script><script src="BufferGeometryUtils.js"></script><script src="GLTFExporter.js"></script>');
const work = join(here, '.export');
mkdirSync(work, { recursive: true });
writeFileSync(join(work, 'index.html'), page_html);
for (const [src, name] of [[threePath, 'three.min.js'], [expPath, 'GLTFExporter.js'], [utilPath, 'BufferGeometryUtils.js']])
  writeFileSync(join(work, name), readFileSync(src));

const browser = await chromium.launch({
  executablePath: process.env.CHROMIUM_PATH || undefined,
  args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader']
});
const page = await browser.newPage({ viewport: { width: 1024, height: 700 }, acceptDownloads: true });
page.on('pageerror', e => console.error('error en la página:', e.message));
await page.goto('file://' + join(work, 'index.html'));
await page.waitForFunction(() => !!window.__modelo, null, { timeout: 90000 });

const [download, stats] = await Promise.all([
  page.waitForEvent('download', { timeout: 300000 }),
  page.evaluate(async ({ radio, terreno }) => {
    const M = window.__modelo;

    // El terreno es un plano de 1.8 km; recorta las caras fuera del cuadrado
    // pedido y compacta los vértices que quedan sin usar.
    function crop(mesh, r) {
      const g = mesh.geometry, idx = g.index, pos = g.attributes.position, col = g.attributes.color;
      const nor = g.attributes.normal, map = new Map(), P = [], N = [], C = [], I = [];
      const push = (v) => {
        let n = map.get(v);
        if (n === undefined) {
          n = P.length / 3; map.set(v, n);
          P.push(pos.getX(v), pos.getY(v), pos.getZ(v));
          N.push(nor.getX(v), nor.getY(v), nor.getZ(v));
          if (col) C.push(col.getX(v), col.getY(v), col.getZ(v));
        }
        return n;
      };
      for (let i = 0; i < idx.count; i += 3) {
        const a = idx.getX(i), b = idx.getX(i + 1), c = idx.getX(i + 2);
        const mx = (pos.getX(a) + pos.getX(b) + pos.getX(c)) / 3;
        const mz = (pos.getZ(a) + pos.getZ(b) + pos.getZ(c)) / 3;
        if (Math.abs(mx) > r || Math.abs(mz) > r) continue;
        I.push(push(a), push(b), push(c));
      }
      const out = new THREE.BufferGeometry();
      out.setAttribute('position', new THREE.Float32BufferAttribute(P, 3));
      out.setAttribute('normal', new THREE.Float32BufferAttribute(N, 3));
      if (col) out.setAttribute('color', new THREE.Float32BufferAttribute(C, 3));
      out.setIndex(I);
      mesh.geometry = out;
    }

    // hornea una InstancedMesh en una sola geometría, con el color de
    // instancia pasado a COLOR_0 cuando existe
    function bake(im, radio) {
      im.updateMatrixWorld(true);
      const src = im.geometry, n = src.attributes.position.count;
      const m = new THREE.Matrix4(), keep = [];
      for (let i = 0; i < im.count; i++) {
        im.getMatrixAt(i, m);
        if (radio > 0 && Math.hypot(m.elements[12], m.elements[14]) > radio) continue;
        keep.push({ m: m.clone().premultiply(im.matrixWorld), i });
      }
      const total = keep.length * n, hasCol = !!im.instanceColor;
      const pos = new Float32Array(total * 3), nor = new Float32Array(total * 3);
      const uv = new Float32Array(total * 2), cl = hasCol ? new Float32Array(total * 3) : null;
      const sp = src.attributes.position.array, sn = src.attributes.normal.array;
      const su = src.attributes.uv ? src.attributes.uv.array : null;
      const v = new THREE.Vector3(), nm = new THREE.Matrix3();
      let o = 0;
      for (const k of keep) {
        nm.getNormalMatrix(k.m);
        for (let j = 0; j < n; j++) {
          v.set(sp[j * 3], sp[j * 3 + 1], sp[j * 3 + 2]).applyMatrix4(k.m);
          pos[(o + j) * 3] = v.x; pos[(o + j) * 3 + 1] = v.y; pos[(o + j) * 3 + 2] = v.z;
          v.set(sn[j * 3], sn[j * 3 + 1], sn[j * 3 + 2]).applyMatrix3(nm).normalize();
          nor[(o + j) * 3] = v.x; nor[(o + j) * 3 + 1] = v.y; nor[(o + j) * 3 + 2] = v.z;
          if (su) { uv[(o + j) * 2] = su[j * 2]; uv[(o + j) * 2 + 1] = su[j * 2 + 1]; }
          if (hasCol) {
            const c = im.instanceColor.array;
            cl[(o + j) * 3] = c[k.i * 3]; cl[(o + j) * 3 + 1] = c[k.i * 3 + 1]; cl[(o + j) * 3 + 2] = c[k.i * 3 + 2];
          }
        }
        o += n;
      }
      const g = new THREE.BufferGeometry();
      g.setAttribute('position', new THREE.BufferAttribute(pos, 3));
      g.setAttribute('normal', new THREE.BufferAttribute(nor, 3));
      g.setAttribute('uv', new THREE.BufferAttribute(uv, 2));
      if (hasCol) g.setAttribute('color', new THREE.BufferAttribute(cl, 3));
      const mat = im.material.clone();
      mat.vertexColors = hasCol;
      return { mesh: new THREE.Mesh(g, mat), count: keep.length };
    }

    const root = new THREE.Group();
    root.name = 'ComplejoEnHerradura';

    M.terrain.name = 'Terreno';
    if (terreno > 0) crop(M.terrain, terreno);
    root.add(M.terrain);

    M.site.name = 'Conjunto';
    for (const c of M.site.children) c.name = 'Conjunto_' + c.name;
    root.add(M.site);

    for (const o of M.scene.children.slice()) {
      if (o === M.sky || o.isLight || o === root) continue;
      if (o.isInstancedMesh || o === M.terrain || o === M.site) continue;
      if (o.isMesh) { if (!o.name) o.name = 'Vegetacion'; root.add(o); }
    }

    const baked = [];
    for (const it of M.instanced) {
      const b = bake(it.m, radio);
      b.mesh.name = it.name;
      root.add(b.mesh);
      baked.push(it.name + '=' + b.count);
    }

    // El modelo no usa texturas: las UV son bytes muertos. Soldar vértices
    // además indexa la geometría, que sale de la fusión sin índices.
    let before = 0, after = 0;
    root.traverse(o => {
      if (!o.isMesh) return;
      const g = o.geometry;
      before += g.attributes.position.count;
      g.deleteAttribute('uv');
      if (!g.index) {
        const w = THREE.BufferGeometryUtils.mergeVertices(g, 1e-4);
        if (w && w.attributes.position.count < g.attributes.position.count) o.geometry = w;
      }
      after += o.geometry.attributes.position.count;
      if (o.material.map === null) o.material.map = undefined;
    });

    let tris = 0, meshes = 0;
    root.traverse(o => {
      if (!o.isMesh) return;
      meshes++;
      const g = o.geometry;
      tris += (g.index ? g.index.count : g.attributes.position.count) / 3;
    });

    const buf = await new Promise((res, rej) => {
      try { new THREE.GLTFExporter().parse(root, res, { binary: true, onlyVisible: true, trs: false }); }
      catch (e) { rej(e); }
    });

    const a = document.createElement('a');
    a.href = URL.createObjectURL(new Blob([buf], { type: 'model/gltf-binary' }));
    a.download = 'complejo-en-herradura.glb';
    document.body.appendChild(a);
    a.click();

    return { meshes, tris: Math.round(tris), bytes: buf.byteLength, baked, verts: [before, after] };
  }, { radio, terreno })
]);

mkdirSync(dirname(out), { recursive: true });
await download.saveAs(out);
await browser.close();

console.log('glb  ->', out);
console.log('mallas', stats.meshes, '· triángulos', stats.tris.toLocaleString('es'),
  '· ' + (stats.bytes / 1048576).toFixed(1) + ' MB');
console.log('vértices', stats.verts[0].toLocaleString('es'), '->', stats.verts[1].toLocaleString('es'),
  '(soldados, sin UV)');
console.log('recortes: arbolado', radio ? radio + ' m' : 'completo', '· terreno',
  terreno ? '±' + terreno + ' m' : 'completo');
console.log('instancias horneadas:', stats.baked.join(', '));
