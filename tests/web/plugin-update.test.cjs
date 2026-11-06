const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { JSDOM } = require(path.resolve(process.argv[2] || 'artifacts/ui-check/node_modules/jsdom'));
const root = path.resolve(__dirname, '../../src/EmbyBlurayHelper.EmbyPlugin/Web');
const dom = new JSDOM(fs.readFileSync(path.join(root, 'update.html'), 'utf8'), { runScripts:'outside-only', url:'http://emby/web/' });
const w = dom.window, view = w.document.querySelector('#blurayHelperUpdatePage');
const element = name => view.querySelector(`[data-update="${name}"]`);
const calls = [];
let controller, failure = false, checks = 0;
let state = { loadedVersion:'0.1.7', installedVersion:'0.1.7', availableVersion:'', sourceUrl:'http://127.0.0.1:8099', canInstall:false, restartRequired:false, message:'Click check.' };
const check = (condition, message) => { assert.ok(condition, message); checks++; };
w.define = (_deps, factory) => { controller = factory(); };
w.ApiClient = {
  getUrl: route => '/' + route,
  ajax: async options => {
    const route = options.url.replace('/BlurayHelper/PluginUpdate','');
    const body = options.data ? JSON.parse(options.data) : undefined;
    calls.push({route, method:options.type, body});
    if (failure) throw {responseText:JSON.stringify({Message:'<img src=x onerror=alert(1)> denied'})};
    if (route === '/Source') { state.sourceUrl = body.HelperUrl; state.canInstall = false; }
    if (route === '/Check') state = { ...state, availableVersion:'0.1.8', sha256:'a'.repeat(64), canInstall:true, message:'Update available.', checkedAt:'2026-11-06T00:00:00Z' };
    if (route === '/Install') state = { ...state, installedVersion:'0.1.8', restartRequired:true, canInstall:false, message:'Restart Emby Server.' };
    return structuredClone(state);
  }
};
w.eval(fs.readFileSync(path.join(root, 'update.js'), 'utf8'));
new controller(view);
const settle = async () => { for (let i = 0; i < 12; i++) await new Promise(resolve => setImmediate(resolve)); };
(async () => {
  view.dispatchEvent(new w.Event('viewshow')); await settle();
  check(calls.length === 1 && calls[0].method === 'GET' && calls[0].route === '', 'entering the page reads local status without checking or installing');
  check(element('loaded').textContent === '0.1.7' && element('installed').textContent === '0.1.7' && element('install').disabled, 'loaded and installed versions are shown with install initially disabled');
  element('source').value = 'http://helper:8099';
  element('form').dispatchEvent(new w.Event('submit', {cancelable:true}));
  check(element('refresh').disabled && element('source').disabled, 'actions are locked during the check');
  await settle();
  check(calls[1].route === '/Source' && calls[1].body.HelperUrl === 'http://helper:8099' && calls[2].route === '/Check', 'saving and checking use authenticated Emby APIs, not browser-to-Helper requests');
  check(element('available').textContent === '0.1.8' && !element('install').disabled, 'verified update enables installation');
  element('install').click(); await settle();
  const install = calls.find(call => call.route === '/Install');
  check(install.body.Version === '0.1.8' && install.body.Sha256 === 'a'.repeat(64), 'install is pinned to the checked version and digest');
  check(element('loaded').textContent === '0.1.7' && element('installed').textContent === '0.1.8', 'install does not pretend the loaded version changed');
  check(element('status').textContent.includes('Restart') && element('install').disabled, 'pending restart is explicit and repeated install is disabled');
  check(calls.every(call => !/restart/i.test(call.route)), 'page never restarts Emby automatically');
  element('refresh').click(); await settle();
  check(calls.at(-1).route === '' && calls.at(-1).method === 'GET', 'manual refresh only reads status');
  failure = true;
  element('form').dispatchEvent(new w.Event('submit', {cancelable:true})); await settle();
  check(!element('error').hidden && element('error').textContent.includes('<img') && element('error').querySelector('img') === null, 'server errors are readable text, never executable HTML');
  check(element('install').disabled && !element('refresh').disabled, 'failed operations clear install eligibility and unlock retry');
  const before = calls.length;
  view.dispatchEvent(new w.Event('viewdestroy'));
  element('refresh').click(); await settle();
  check(calls.length === before, 'destroyed pages remove action handlers');
  console.log(`Passed ${checks} plugin update Web checks.`);
})().catch(error => { console.error(error); process.exitCode = 1; }).finally(() => w.close());
