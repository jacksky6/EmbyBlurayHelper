// Usage: node tests/web/library-language.test.cjs <directory containing installed jsdom>
// Navigation structure and the language page's own status; batch work is in library-tasks.test.cjs.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { JSDOM } = require(path.resolve(process.argv[2] || 'artifacts/ui-check/node_modules/jsdom'));
const root = path.resolve(__dirname, '../..');
const dom = new JSDOM(fs.readFileSync(path.join(root,'src/EmbyBlurayHelper.App/Web/index.html'),'utf8'), {
  url:'http://127.0.0.1/#language', runScripts:'outside-only', pretendToBeVisual:true
});
const w = dom.window, d = w.document, calls = [];
let isoFixture = {state:'blocked',canProbe:false,pluginState:'not-loaded',probeState:'ready',message:'Emby 未加载 ISO 插件，请安装 DLL 并重启 Emby。'};
w.fetch = async url => {
  calls.push(url);
  if(url.endsWith('/iso/status')) return {status:200,ok:true,headers:{get:()=> 'application/json'},json:async()=>structuredClone(isoFixture)};
  throw new Error('Unexpected request '+url);
};
vm.runInContext(fs.readFileSync(path.join(root,'src/EmbyBlurayHelper.App/Web/app.js'),'utf8').replace('startConsole().catch(()=>{});',''),dom.getInternalVMContext());
let checks = 0;
const check = (condition,message) => {assert.ok(condition,message);checks++;};
const evaluate = code => vm.runInContext(code,dom.getInternalVMContext());
const navPages=[...d.querySelectorAll('.sidebar [data-page]')].map(button=>button.dataset.page);
check(navPages.join(',')==='overview,library,language,media-features,chapters,infuse,diagnostics,logs,connection','navigation order follows the Emby / Infuse / tools / settings groups');
check([...d.querySelectorAll('.sidebar nav .nav-caption')].map(c=>c.textContent).join('|')==='工作空间|Emby 媒体库|Infuse 播放|工具|设置','navigation groups are labelled');
check(d.querySelectorAll('.sidebar [role="tab"]').length===0,'feature navigation has no secondary tabs');
check(d.querySelector('#page-language').contains(d.querySelector('#enable-probe')) && !d.querySelector('#page-language #library-scan'),'language page keeps the probe switch; batch work moved to the library page');
check(d.querySelector('#page-library').contains(d.querySelector('#lib-scan')) && d.querySelector('#page-library').contains(d.querySelector('#lib-fill')),'library page holds the combined workflow');
check(d.querySelector('#page-infuse').contains(d.querySelector('#enable-infuse')) && d.querySelector('#page-infuse').contains(d.querySelector('#enable-multiclip')),'Infuse page contains both playback settings');
check(d.querySelector('#page-connection').contains(d.querySelector('#management-url')) && d.querySelector('#page-connection').contains(d.querySelector('#account-form')),'listener and account settings stay on the connection page');
evaluate("navigate('infuse')");
check(!d.querySelector('#page-infuse').hidden && d.querySelector('#page-language').hidden,'Infuse navigation opens its own page');
check(d.querySelector('#page-group').textContent==='Infuse 播放' && d.querySelector('#page-breadcrumb').textContent==='播放增强','breadcrumb shows group and page');
evaluate("navigate('library')");
check(!d.querySelector('#page-library').hidden && d.querySelector('#page-group').textContent==='Emby 媒体库','library page is in the Emby group');
evaluate("navigate('features')");
check(w.location.hash==='#language','legacy features route redirects to the language page');
(async()=>{
  evaluate("configuration={};$('#console').hidden=false;");
  await evaluate('refreshIsoStatus()');
  check(d.querySelector('#iso-prerequisites').dataset.state==='blocked' && d.querySelector('#iso-status-message').textContent.includes('重启 Emby'),'missing DLL or restart requirement is shown');
  isoFixture={state:'blocked',canProbe:false,pluginState:'ready',pluginVersion:'0.1.6',probeState:'unsupported',message:'原版 ffprobe 缺少 bluray 输入协议，重启不能解决。'};
  evaluate('isoRevision++;isoRequest=null;'); await evaluate('refreshIsoStatus()');
  check(d.querySelector('#iso-plugin-status').textContent.includes('已加载') && d.querySelector('#iso-probe-status').textContent.includes('缺少可用的读取协议'),'loaded plugin is distinguished from a missing native protocol');
  isoFixture={state:'ready',canProbe:true,pluginState:'ready',pluginVersion:'0.1.6',probeState:'ready',message:'ISO 前置检查通过'};
  evaluate('isoRevision++;isoRequest=null;'); await evaluate('refreshIsoStatus()');
  check(d.querySelector('#iso-prerequisites').dataset.state==='ready','manual recheck after restart shows ready');
  const isoCalls=calls.filter(u=>u.endsWith('/iso/status')).length;
  evaluate("probeStatus={state:'connected',languagePatchEnabled:true};renderProbeStatus();");
  check(calls.filter(u=>u.endsWith('/iso/status')).length===isoCalls,'status renders never poll the plugin or native protocols');
  console.log(`Passed ${checks} library Web interaction checks.`);
})().catch(error=>{console.error(error);process.exitCode=1;}).finally(()=>w.close());
