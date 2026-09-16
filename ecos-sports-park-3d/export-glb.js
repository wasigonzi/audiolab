const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');
const dir = __dirname;

(async () => {
  const src = fs.readFileSync('/home/user/audiolab/ecos-sports-park-3d/index.html','utf8')
    .replace('https://cdnjs.cloudflare.com/ajax/libs/three.js/r128/three.min.js',
             'file://' + path.join(dir,'node_modules/three/build/three.min.js'));
  const page404 = path.join(dir,'export-page.html');
  fs.writeFileSync(page404,
    '<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head><body style="margin:0">'+src+'</body></html>');

  const b = await chromium.launch({
    executablePath:'/opt/pw-browsers/chromium-1194/chrome-linux/chrome',
    args:['--use-gl=swiftshader','--enable-unsafe-swiftshader','--no-sandbox']
  });
  const p = await b.newPage({viewport:{width:1024,height:640}});
  p.on('pageerror',e=>console.log('PAGEERROR:',e.message));
  await p.goto('file://'+page404);
  await p.waitForFunction(() => !!window.ECOS_PARK, null, {timeout:60000});
  await p.addScriptTag({path: path.join(dir,'node_modules/three/examples/js/exporters/GLTFExporter.js')});

  const b64 = await p.evaluate(() => new Promise((resolve, reject) => {
    const {world} = window.ECOS_PARK;

    // trim the huge context plane and drop the distant backdrop: the glb is the park itself
    world.traverse(o => {
      if (o.userData && o.userData.bg) o.visible = false;
      if (o.userData && o.userData.ground) o.scale.set(0.34, 0.34, 1);
    });

    // bake flat shading into geometry so viewers that ignore the material flag still get facets
    world.traverse(o => {
      if (o.isMesh) {
        const mats = Array.isArray(o.material) ? o.material : [o.material];
        if (mats.some(m => m && m.flatShading)) {
          if (o.geometry.index) o.geometry = o.geometry.toNonIndexed();
          o.geometry.computeVertexNormals();
        }
      }
    });

    const exporter = new THREE.GLTFExporter();
    exporter.parse(world, (buf) => {
      const bytes = new Uint8Array(buf);
      let s = '';
      const chunk = 0x8000;
      for (let i = 0; i < bytes.length; i += chunk) {
        s += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
      }
      resolve(btoa(s));
    }, {binary: true, onlyVisible: true, truncateDrawRange: false});
    setTimeout(() => reject(new Error('export timeout')), 180000);
  }));

  const out = '/home/user/audiolab/ecos-sports-park-3d/ecos-sports-park.glb';
  fs.writeFileSync(out, Buffer.from(b64,'base64'));
  console.log('written', out, fs.statSync(out).size, 'bytes');
  await b.close();
})();

/* Usage:
 *   npm i three@0.128.0 playwright@1.49.1
 *   node export-glb.js
 * Builds the scene in headless Chromium (so the canvas textures bake in) and
 * writes ecos-sports-park.glb next to index.html.
 */
