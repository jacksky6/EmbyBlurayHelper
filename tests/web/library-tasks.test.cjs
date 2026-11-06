// Media library processing: one library choice, ordered completions, server-side chain.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { JSDOM } = require(path.resolve(process.argv[2] || 'artifacts/ui-check/node_modules/jsdom'));
const root = path.resolve(__dirname, '../..');
const web = name => fs.readFileSync(path.join(root, 'src/EmbyBlurayHelper.App/Web', name), 'utf8');
const dom = new JSDOM(web('index.html'), { url: 'http://localhost/#library', runScripts: 'outside-only', pretendToBeVisual: true });
const w = dom.window, d = w.document, posts = [], timers = new Map();
let checks = 0, next = 1;
const check = (condition, message) => { assert.ok(condition, message); checks++; };
const libs = [{ id:'a', name:'Lib <A>', moviesChecked:3, scanComplete:true }, { id:'b', name:'B', moviesChecked:2, scanComplete:true }];
const plan = (id, extra) => ({ id, status:'ready', active:false, scanComplete:true, connectionMatches:true, ...extra });
const state = {
  'library-tasks': { status:'idle', active:false, steps:[], libraryIds:[] },
  'library-language': plan('L', { moviesChecked:5, summary:{ blurayMovies:3, uhdMovies:2, completeMovies:1, missingMovies:2, unknownMovies:0 },
    libraries: libs.map(l => ({ ...l, blurayMovies:2, pendingMovies: l.id === 'a' ? 2 : 1 })),
    items:[{ id:'s', name:'Shared <m>', libraryIds:['a','b'], classification:'missing', missingAudio:1, missingSubtitles:0, sources:[{}] },
      { id:'x', name:'A only', libraryIds:['a'], classification:'missing', missingAudio:0, missingSubtitles:2, sources:[{ isIso:true }] },
      { id:'y', name:'B only', libraryIds:['b'], classification:'complete', sources:[{}] }] }),
  'media-features/batch': plan('F', { summary:{ discs:3, verified:1, pending:2 }, libraries: libs.map(l => ({ ...l, discs:2, pending:1 })),
    items:[{ id:'s', name:'Shared <m>', libraryIds:['a','b'], verified:false }, { id:'x', name:'A only', libraryIds:['a'], verified:true }, { id:'y', name:'B only', libraryIds:['b'], verified:false }] }),
  'chapters/batch': plan('C', { summary:{ discs:3, verified:1, pending:2, preserved:0 }, libraries: libs.map(l => ({ ...l, discs:2, pending:1 })),
    items:[{ id:'s', name:'Shared <m>', libraryIds:['a','b'], pending:true }, { id:'x', name:'A only', libraryIds:['a'], pending:false, result:'completed' }, { id:'y', name:'B only', libraryIds:['b'], pending:true }] }),
  'media-features': { enabled:false },
  'chapters': { ready:true, enabled:true, automatic:true },
  'probe/status': { state:'connected', languagePatchEnabled:true, canRestore:true },
  'iso/status': { state:'ready', canProbe:true, pluginState:'ready' }
};
w.setTimeout = (fn, ms) => { const id = next++; timers.set(id, { fn, ms }); return id; }; w.clearTimeout = id => timers.delete(id);
w.fetch = async (url, options) => {
  const route = url.replace(/^\/api\//, ''), json = body => ({ status:200, ok:true, headers:{ get:() => 'application/json' }, json: async () => structuredClone(body) });
  if (options?.method === 'POST') {
    posts.push({ route, body: JSON.parse(options.body) });
    if (route === 'library-tasks/fill') {
      state['library-tasks'] = { status:'running', active:true, current:'language', libraryIds:['a','b'], steps:[{ task:'language', status:'running', message:'正在补全' }, { task:'chapters', status:'waiting', message:'' }] };
      state['library-language'] = { ...state['library-language'], status:'filling', active:true, planned:2, processed:1, currentName:'Shared <m>' };
    }
    if (route === 'library-tasks/scan') return json({ results:{ language:'started', features:'started', chapters:'章节任务正在运行' } });
    return json(state['library-tasks']);
  }
  if (route in state) return json(state[route]);
  throw new Error('unexpected ' + url);
};
const evaluate = code => vm.runInContext(code, dom.getInternalVMContext());
evaluate(web('app.js').replace('startConsole().catch(()=>{});', ''));
evaluate("configuration={};$('#console').hidden=false;");
evaluate(web('library.js'));
const settle = async () => { for (let i = 0; i < 12; i++) await new Promise(r => setImmediate(r)); };
const step = key => d.querySelector(`.lib-step input[data-step="${key}"]`).closest('.lib-step');
const fill = () => d.querySelector('#lib-fill');
(async () => {
  w.dispatchEvent(new w.Event('helper:navigate')); await settle();
  check(d.querySelectorAll('#lib-groups tr').length === 2 && d.querySelector('#lib-groups').textContent.includes('Lib <A>') && !d.querySelector('#lib-groups a'), 'libraries from all three checks are merged and escaped');
  check(d.querySelector('#lib-select-all').checked, 'all libraries are selected after a new check');
  check(step('language').textContent.includes('2 部待处理') && d.querySelector('[data-step="language"]').checked, 'language counts each movie once across libraries and is chosen');
  check(step('features').textContent.includes('条件未满足') && d.querySelector('[data-step="features"]').disabled && !d.querySelector('[data-step="features"]').checked, 'a step whose feature is off cannot be chosen');
  check(step('features').textContent.includes('音视频规格'), 'the reason names the settings page');
  check(fill().textContent === '补全所选内容（语言与时长 → 原盘章节，共 4 项）' && !fill().disabled, 'fill shows the order and total');
  const b = d.querySelector('#lib-groups [data-library="b"]'); b.checked = false; b.dispatchEvent(new w.Event('change', { bubbles:true }));
  check(fill().textContent.includes('共 3 项') && d.querySelector('#lib-select-all').indeterminate, 'library choice scopes every step');
  b.checked = true; d.querySelector('#lib-groups [data-library="b"]').checked = true; d.querySelector('#lib-groups [data-library="b"]').dispatchEvent(new w.Event('change', { bubbles:true }));
  evaluate("isoStatus={state:'blocked',canProbe:false};window.dispatchEvent(new Event('helper:status'));");
  check(step('language').textContent.includes('1 部 ISO') && step('language').textContent.includes('BDMV 照常补全'), 'blocked ISO is explained; BDMV still runs');
  evaluate("isoStatus={state:'ready',canProbe:true};window.dispatchEvent(new Event('helper:status'));");
  const again = d.querySelector('[data-again="chapters"]'); again.checked = true; again.dispatchEvent(new w.Event('change', { bubbles:true }));
  check(fill().textContent.includes('共 5 项'), 'recheck makes completed chapters eligible');
  check(d.querySelector('#lib-rows').textContent.includes('Shared <m>') && d.querySelectorAll('#lib-rows tr').length === 3, 'details merge movies across tasks');
  await fill().onclick(); await settle();
  const sent = posts.find(p => p.route === 'library-tasks/fill').body;
  check(sent.libraryIds.join(',') === 'a,b' && sent.steps.map(s => `${s.task}:${s.planId}:${s.includeVerified}`).join(',') === 'language:L:false,chapters:C:true', 'fill sends the scope and the ordered steps with their plans');
  check(fill().disabled && [...d.querySelectorAll('#lib-groups input')].every(i => i.disabled) && !d.querySelector('#lib-cancel').hidden, 'the running chain locks the page');
  check(d.querySelector('#lib-status strong').textContent === '正在补全：语言与时长' && d.querySelector('#lib-status p').textContent.includes('1 / 2 部'), 'progress shows the current step');
  check([...timers.values()].some(t => t.ms === 2000), 'visible running chain polls progress');
  w.history.replaceState(null, '', '#logs'); w.dispatchEvent(new w.Event('helper:navigate'));
  check(![...timers.values()].some(t => t.ms === 2000), 'leaving the page stops polling');
  w.history.replaceState(null, '', '#library'); w.dispatchEvent(new w.Event('helper:navigate')); await settle();
  await d.querySelector('#lib-cancel').onclick(); await settle();
  check(posts.some(p => p.route === 'library-tasks/cancel'), 'stop goes to the chain');
  state['library-tasks'] = { status:'completed', active:false, steps:[], libraryIds:[], message:'语言与时长：完成；原盘章节：完成。' };
  state['library-language'] = { ...state['library-language'], status:'completed', active:false };
  state['chapters/batch'] = { ...state['chapters/batch'], connectionMatches:false };
  await d.querySelector('#lib-progress-refresh').onclick(); await settle();
  check(step('chapters').textContent.includes('需要重新检查') && d.querySelector('[data-step="chapters"]').disabled, 'a stale plan asks for a new check');
  check(d.querySelector('#lib-status strong').textContent === '上次补全已结束', 'finished chain is reported');
  await d.querySelector('#lib-scan').onclick(); await settle();
  check(posts.some(p => p.route === 'library-tasks/scan') && d.querySelector('#toast').textContent.includes('部分检查未启动'), 'one check for all three; failures are reported');
  w.history.replaceState(null, '', '#chapters'); w.dispatchEvent(new w.Event('helper:navigate')); await settle();
  check(d.querySelector('#summary-chapters').textContent.includes('上次检查：3 部原盘'), 'feature pages show their last check and link here');
  console.log(`Passed ${checks} media library task Web checks.`);
})().catch(error => { console.error(error); process.exitCode = 1; }).finally(() => w.close());
