const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { JSDOM } = require(process.argv[2] || path.resolve('artifacts/ui-check/node_modules/jsdom'));
const root = path.resolve(__dirname, '../..');
const dom = new JSDOM(fs.readFileSync(path.join(root, 'src/EmbyBlurayHelper.App/Web/index.html'), 'utf8'), { url: 'http://localhost/#infuse', runScripts: 'outside-only' });
const w = dom.window, d = w.document; let count = 0;
const check = (value, message) => { assert.ok(value, message); count++; };
let server = { embyUrl:'http://localhost:8096',proxyUrl:'http://localhost:8097',managementUrl:'http://localhost:8099',upstreamProbePath:'/original/ffprobe',embyApiKey:'fixture',enableProbeLanguagePatch:true,
  enableInfuseLanguagePatch:true,enableInfuseChapters:true,enableInfuseDolbyVision:false,dolbyVisionCacheMiB:512,dolbyVisionGlobalCacheMiB:1024,dolbyVisionFfmpegPath:'',dolbyVisionLibraryPath:'' };
const posts = []; let reject = false, requests = 0;
w.fetch = async (url, options) => {
  requests++;
  const result = body => ({status:200,ok:true,headers:{get:()=> 'application/json'},json:async()=>structuredClone(body)});
  if (url.endsWith('/api/settings') && options?.method === 'POST') {
    const value = JSON.parse(options.body); posts.push(value);
    if (reject) return {status:400,ok:false,headers:{get:()=> 'application/json'},json:async()=>({error:'fixture native dependency missing'})};
    server = value; return result({restartRequired:false});
  }
  if (url.endsWith('/api/settings')) return result({settings:server,executablePath:'/helper',dataDirectory:'/data'});
  if (url.endsWith('/api/infuse/dolby-vision')) return result({enabled:server.enableInfuseDolbyVision,available:true,ffmpegPath:'/original/ffmpeg',libraryPath:'/helper/tools/libdovi.so',cache:{cachedMiB:48,fragments:1,processing:0}});
  if (url.endsWith('/api/infuse/dolby-vision/check')) return result({version:'ffmpeg fixture'});
  return result({});
};
const settle = async () => { for (let n = 0; n < 12; n++) await new Promise(resolve => setTimeout(resolve, 0)); };
(async () => {
  vm.runInContext(fs.readFileSync(path.join(root,'src/EmbyBlurayHelper.App/Web/app.js'),'utf8').replace('startConsole().catch(()=>{});',''),dom.getInternalVMContext());
  w.fillSettings({settings:server,executablePath:'/helper',dataDirectory:'/data'}); d.querySelector('#console').hidden=false;
  vm.runInContext(fs.readFileSync(path.join(root,'src/EmbyBlurayHelper.App/Web/dolby-vision.js'),'utf8'),dom.getInternalVMContext()); await settle();
  check(!d.querySelector('#enable-dolby-vision').checked, 'new feature defaults off');
  check(d.querySelector('#infuse-disc-container').value === 'iso', 'existing configuration keeps ISO by default');
  const container = d.querySelector('#infuse-disc-container'); container.value='mkv'; container.dispatchEvent(new w.Event('change')); await settle();
  check(server.infuseDiscContainer === 'mkv' && !server.enableInfuseDolbyVision, 'ordinary remux saves without enabling DV');
  reject=true; container.value='iso'; container.dispatchEvent(new w.Event('change')); await settle();
  check(container.value==='mkv' && !container.disabled, 'failed format save restores actual format and unlocks controls'); reject=false;
  check(d.querySelector('#dv-session-cache').value === '512' && d.querySelector('#dv-global-cache').value === '1024', '512 MiB / 1 GiB defaults shown');
  d.querySelector('#emby-url').value = 'http://unsaved:8096';
  const toggle = d.querySelector('#enable-dolby-vision'); toggle.checked=true; toggle.dispatchEvent(new w.Event('change')); await settle();
  check(server.enableInfuseDolbyVision && server.enableInfuseChapters && server.enableInfuseLanguagePatch, 'switch saves independently of stable features');
  check(server.embyUrl === 'http://localhost:8096' && d.querySelector('#emby-url').value === 'http://unsaved:8096', 'switch preserves other cards unsaved inputs');
  check(/已开启/.test(d.querySelector('#dolby-vision-status strong').textContent), 'saved enabled state shown');
  check(/强制退出/.test(d.querySelector('#toast').textContent), 'Infuse resync reminder shown');
  reject=true; toggle.checked=false; toggle.dispatchEvent(new w.Event('change')); await settle();
  check(toggle.checked && !toggle.disabled, 'failed save restores actual switch and releases busy state');
  check(/fixture native/.test(d.querySelector('#toast').textContent), 'save failure is explained'); reject=false;
  const cache = d.querySelector('#dv-session-cache'); cache.value='1024'; cache.dispatchEvent(new w.Event('input'));
  d.querySelector('#dv-save').click(); await settle();
  check(server.dolbyVisionCacheMiB===1024 && !d.querySelector('#dv-save').disabled, 'cache save completes and unlocks controls');
  const before = posts.length; d.querySelector('#dv-check').click(); await settle();
  check(posts.length===before && /ffmpeg fixture/.test(d.querySelector('#toast').textContent), 'dependency check does not change configuration');
  const calls=requests; await new Promise(resolve=>setTimeout(resolve,80)); check(requests===calls, 'no continuous status polling');
  const text=d.querySelector('#dolby-vision-panel').textContent;
  check(/FEL/.test(text)&&/残差/.test(text)&&/不重新编码/.test(text), 'FEL picture tradeoff is visible');
  check(/BDMV \/ ISO/.test(text)&&/仅 Infuse/.test(text)&&/系统文件缓存另占内存/.test(text), 'scope and memory budget limits stated');
  console.log(`Passed ${count} Dolby Vision Web checks.`); dom.window.close();
})().catch(error=>{console.error(error);dom.window.close();process.exitCode=1;});
