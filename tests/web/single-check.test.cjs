// The single-movie check reads every feature's last saved result for one movie.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { JSDOM } = require(path.resolve(process.argv[2] || 'artifacts/ui-check/node_modules/jsdom'));
const root = path.resolve(__dirname, '../..');
const web = name => fs.readFileSync(path.join(root, 'src/EmbyBlurayHelper.App/Web', name), 'utf8');
const dom = new JSDOM(web('index.html'), { url: 'http://localhost/#diagnostics', runScripts: 'outside-only' });
const w = dom.window, d = w.document, posts = [];
let checks = 0;
const check = (condition, message) => { assert.ok(condition, message); checks++; };
const disc = { name: '影片 <x>', sources: [
  { id: 'bd', container: 'bluray', isIso: false, path: '/m/BD', readable: true, languages: [{ pid: '0x1100', language: 'eng', type: 'Audio' }] },
  { id: 'iso', container: 'blurayiso', isIso: true, path: '/m/a.iso', readable: false, languages: null },
  { id: 'mkv', container: 'mkv', isIso: false, path: '/m/a.mkv', readable: false, languages: null }] };
const routes = {
  'library-language': { items: [{ id: '7', classification: 'missing', missingAudio: 1, missingSubtitles: 0, sources: [{ runTimeTicks: 36000000000 }] }] },
  'media-features/batch': { items: [] },
  'chapters/batch': { items: [{ id: '7', sources: [{ kind: 'BDMV', count: 24, state: 'managed', playlist: '00800.mpls' }] }] },
  'infuse/chapters/7': { enabled: true, sources: [{ sourceId: 'bd', iso: true, chapters: 24, playlist: '00800.mpls' }] },
  'media-features': { enabled: true, automatic: true, probeAccessible: true, probeConnected: true }
};
w.fetch = async (url, options) => {
  const json = body => ({ status: 200, ok: true, headers: { get: () => 'application/json' }, json: async () => structuredClone(body) });
  const route = url.replace(/^\/api\//, '');
  if (options?.method === 'POST') { posts.push({ route, body: JSON.parse(options.body) }); return json(route === 'disc/check' ? disc : { name: '影片', streams: [{ Index: 1, Codec: 'truehd', Title: 'Atmos' }] }); }
  if (route in routes) return json(routes[route]);
  throw new Error('unexpected ' + url);
};
const evaluate = code => vm.runInContext(code, dom.getInternalVMContext());
evaluate(web('batch.js') + '\n' + web('app.js').replace('startConsole().catch(()=>{});', ''));
evaluate("configuration={};");
const settle = async () => { for (let i = 0; i < 8; i++) await new Promise(r => setTimeout(r, 0)); };
(async () => {
  d.querySelector('#item-id').value = '7';
  d.querySelector('#inspect').click(); await settle();
  const text = d.querySelector('#disc-result').textContent;
  check(d.querySelectorAll('.inspect-card').length === 4, 'four feature cards');
  check(text.includes('影片 <x>') && !d.querySelector('#disc-result x'), 'movie name is escaped');
  check(/语言缺失/.test(text) && text.includes('01:00:00'), 'language card reads the saved check');
  check(/不在上次的媒体库检查结果中/.test(text), 'missing snapshot entry is explained');
  check(text.includes('24 个章节') && text.includes('原生章节已核验'), 'chapter card reads the saved check');
  check(text.includes('虚拟 ISO · 24 个章节') && text.includes('真实 ISO，Infuse 直接读取原盘') && text.includes('非原盘，按原方式播放'), 'Infuse state per source');
  // Only the MKV is an unreadable folder; the ISO is labelled as a file.
  check(text.includes('ISO 文件') && d.querySelectorAll('.disc-source .tag.off').length === 1, 'ISO is not reported as an unreadable folder');
  check(posts.length === 1 && posts[0].route === 'disc/check', 'checking does not write anything');
  d.querySelector('#inspect-probe-features').click(); await settle();
  check(posts.length === 2 && posts[1].route === 'media-features/probe' && posts[1].body.itemId === '7', 'spec action writes only when clicked');
  check(d.querySelector('#inspect-feature-result').textContent.includes('TRUEHD · Atmos'), 'spec result is shown in the card');
  routes['media-features'] = { enabled: false };
  d.querySelector('#inspect').click();
  check(/正在读取/.test(d.querySelector('#disc-result').textContent), 'previous movie is cleared while the next one loads');
  await settle();
  check(d.querySelector('#inspect-probe-features').disabled, 'spec action needs spec recognition enabled');
  routes['media-features'] = { enabled: true, automatic: true, probeAccessible: true, probeConnected: true };
  disc.sources = [disc.sources[2]];
  d.querySelector('#inspect').click(); await settle();
  check(d.querySelector('#inspect-probe-features').disabled && /只处理 BDMV 与 Blu-ray ISO/.test(d.querySelector('#disc-result').textContent), 'spec action is not offered for MKV');
  console.log(`Passed ${checks} single-check Web checks.`);
  dom.window.close();
})().catch(error => { console.error(error); process.exitCode = 1; });
