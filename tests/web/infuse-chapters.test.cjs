const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { JSDOM } = require(path.resolve('artifacts/ui-check/node_modules/jsdom'));
const root = path.resolve(__dirname, '../..');
const dom = new JSDOM(fs.readFileSync(path.join(root, 'src/EmbyBlurayHelper.App/Web/index.html'), 'utf8'), { url: 'http://localhost/#infuse', runScripts: 'outside-only' });
const w = dom.window, d = w.document;
let checks = 0;
const check = (condition, message) => { assert.ok(condition, message); checks++; };
let server = { embyUrl: 'http://localhost:8096', proxyUrl: 'http://0.0.0.0:8097', managementUrl: 'http://localhost:8099', upstreamProbePath: '/original/ffprobe', embyApiKey: 'fixture', enableProbeLanguagePatch: true, enableInfuseLanguagePatch: true, enableInfuseMultiClipPlayback: true, enableInfuseChapters: false };
const posts = [];
let chapterSummary = { enabled: false, available: 0, unavailable: [] };
w.fetch = async (url, options) => {
  const json = body => ({ status: 200, ok: true, headers: { get: () => 'application/json' }, json: async () => structuredClone(body) });
  if (url.endsWith('/api/settings') && options?.method === 'POST') { const body = JSON.parse(options.body); posts.push(body); server = body; return json({ restartRequired: false }); }
  if (url.endsWith('/api/settings')) return json({ settings: server, hasApiKey: true, restartRequired: false, executablePath: '/helper', dataDirectory: '/data' });
  if (url.endsWith('/api/infuse/chapters')) return json(chapterSummary);
  return json({});
};
vm.runInContext(fs.readFileSync(path.join(root, 'src/EmbyBlurayHelper.App/Web/app.js'), 'utf8').replace('startConsole().catch(()=>{});', ''), dom.getInternalVMContext());
const settle = () => new Promise(resolve => setTimeout(resolve, 0));
(async () => {
  w.fillSettings({ settings: server, hasApiKey: true, executablePath: '/helper', dataDirectory: '/data' });
  check(d.querySelector('#enable-infuse-chapters').checked === false, 'switch shows the saved value');
  check(/已关闭/.test(d.querySelector('#infuse-chapters-state').textContent), 'state is shown as off');
  check(!d.querySelector('#save-infuse-settings') && !d.querySelector('#save-features'), 'no page-level save button remains');
  // Unsaved input in another card must not be saved by a switch.
  d.querySelector('#emby-url').value = 'http://typed-but-not-saved:8096';
  const toggle = d.querySelector('#enable-infuse-chapters');
  toggle.checked = true; toggle.dispatchEvent(new w.Event('change'));
  for (let i = 0; i < 5; i++) await settle();
  check(posts.length === 1 && posts[0].enableInfuseChapters === true, 'switch saves immediately');
  check(posts[0].embyUrl === 'http://localhost:8096', 'switch keeps the saved Emby address, not unsaved input');
  check(posts[0].enableInfuseLanguagePatch === true && posts[0].enableInfuseMultiClipPlayback === true, 'other switches keep their saved values');
  check(d.querySelector('#emby-url').value === 'http://typed-but-not-saved:8096', 'unsaved input on another card is left as typed');
  check(d.querySelector('#infuse-chapters-state').textContent === '已启用', 'state follows the saved value');
  check(!d.querySelector('#infuse-chapters-state').classList.contains('off'), 'enabled state is not styled off');
  check(/强制退出再打开/.test(d.querySelector('#toast').textContent), 'toast reminds to resync Infuse');
  chapterSummary = { enabled: true, available: 12, unavailable: [{ itemId: '11', name: '剪刀手爱德华', reason: '无法匹配 Emby 正片版本，保留原播放方式。' }] };
  await w.refreshInfuseChapters();
  check(/12 部原盘以虚拟 ISO 提供章节/.test(d.querySelector('#infuse-chapters-copy').textContent), 'available count is shown');
  check(!d.querySelector('#infuse-chapters-unavailable').hidden && d.querySelector('#infuse-chapters-unavailable-rows').textContent.includes('剪刀手爱德华'), 'unresolved discs are listed with the reason');
  const panel = d.querySelector('#infuse-chapters-panel').textContent;
  check(/默认关闭/.test(panel) && /真实 ISO 暂保持原样/.test(panel), 'default and real ISO behavior are stated');
  check(/强制退出再打开/.test(panel) && /章节与截图/.test(panel) && /同步媒体库不访问硬盘/.test(panel), 'resync, chapter source and no-disk sync are stated');
  check(!/重新打开影片即可恢复/.test(panel), 'the incorrect reopen claim is gone');
  // Opened on this machine: a phone cannot use localhost, so the hint asks for the LAN IP.
  check(d.querySelector('#infuse-proxy-address').textContent === 'http://本机 IP:8097', 'proxy address shown for Infuse');
  check(!!d.querySelector('#page-infuse #infuse-chapters-panel'), 'chapter panel is on the Infuse page');
  console.log(`Passed ${checks} Infuse chapter UI checks.`);
  dom.window.close();
})().catch(error => { console.error(error); process.exitCode = 1; });
