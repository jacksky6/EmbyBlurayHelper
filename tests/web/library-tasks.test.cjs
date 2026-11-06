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
  'library-language': plan('L', { moviesChecked:5, summary:{ blurayMovies:3, uhdMovies:2, fullHdMovies:1, unclassifiedMovies:0, completeMovies:1, missingMovies:2, unknownMovies:0 },
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
const step = key => d.querySelector(`.lib-fill-row input[data-step="${key}"]`).closest('.lib-fill-row');
const fill = () => d.querySelector('#lib-fill');
(async () => {
  w.dispatchEvent(new w.Event('helper:navigate')); await settle();
  check(d.querySelector('#lib-action-summary').textContent === '5 部电影 · 3 部蓝光原盘\n其中 UHD 蓝光 2 部 · 1080P 蓝光 1 部', 'summary starts with the movie total and breaks down Blu-ray resolution counts');
  check(d.querySelectorAll('#lib-action-libs label').length === 2 && d.querySelector('#lib-action-libs').textContent.includes('Lib <A>') && !d.querySelector('#lib-action-libs a'), 'libraries from all three checks are merged and escaped');
  check([...d.querySelectorAll('#lib-action-libs input[data-library]')].every(i => i.checked), 'all libraries are selected after a new check');
  check(step('language').textContent.includes('2 部待处理') && d.querySelector('[data-step="language"]').checked, 'language counts each movie once across libraries and is chosen');
  check([...d.querySelectorAll('#lib-fill-rows [data-again]')].map(i => i.dataset.again).join(',') === 'language,features,chapters', 'all three completions offer the same include-verified option');
  const languageAgain = d.querySelector('[data-again="language"]');
  check(!languageAgain.checked && !languageAgain.disabled, 'language recheck is opt-in after a valid check');
  languageAgain.checked = true; languageAgain.dispatchEvent(new w.Event('change', { bubbles:true }));
  check(step('language').textContent.includes('3 部待处理'), 'language recheck includes complete movies and deduplicates shared movies');
  d.querySelector('[data-again="language"]').checked = false; d.querySelector('[data-again="language"]').dispatchEvent(new w.Event('change', { bubbles:true }));
  check(step('language').textContent.includes('2 部待处理'), 'turning language recheck off restores the missing-only scope');
  check(step('features').textContent.includes('条件未满足') && d.querySelector('[data-step="features"]').disabled && !d.querySelector('[data-step="features"]').checked, 'a step whose feature is off cannot be chosen');
  check(step('features').textContent.includes('音视频规格'), 'the reason names the settings page');
  check(fill().textContent === '开始补全' && !fill().disabled, 'fill shows enabled with eligible steps');
  const b = d.querySelector('#lib-action-libs [data-library="b"]'); b.checked = false; b.dispatchEvent(new w.Event('change', { bubbles:true }));
  check(!fill().disabled && d.querySelector('#lib-select-all-link')?.textContent === '全选', 'library choice scopes every step');
  b.checked = true; d.querySelector('#lib-action-libs [data-library="b"]').checked = true; d.querySelector('#lib-action-libs [data-library="b"]').dispatchEvent(new w.Event('change', { bubbles:true }));
  evaluate("isoStatus={state:'blocked',canProbe:false};window.dispatchEvent(new Event('helper:status'));");
  check(step('language').textContent.includes('1 部 ISO') && step('language').textContent.includes('BDMV 照常补全'), 'blocked ISO is explained; BDMV still runs');
  evaluate("isoStatus={state:'ready',canProbe:true};window.dispatchEvent(new Event('helper:status'));");
  const again = d.querySelector('[data-again="chapters"]'); again.checked = true; again.dispatchEvent(new w.Event('change', { bubbles:true }));
  check(!fill().disabled && d.querySelector('[data-again="chapters"]').checked, 'recheck makes completed chapters eligible and fill stays enabled');
  d.querySelector('[data-again="language"]').checked = true; d.querySelector('[data-again="language"]').dispatchEvent(new w.Event('change', { bubbles:true }));
  check(d.querySelector('#lib-rows').textContent.includes('Shared <m>') && d.querySelectorAll('#lib-rows tr').length === 3, 'details merge movies across tasks');
  await fill().onclick(); await settle();
  const sent = posts.find(p => p.route === 'library-tasks/fill').body;
  check(sent.libraryIds.join(',') === 'a,b' && sent.steps.map(s => `${s.task}:${s.planId}:${s.includeVerified}`).join(',') === 'language:L:true,chapters:C:true', 'fill sends the scope, ordered plans and language recheck option');
  check(fill().disabled && [...d.querySelectorAll('#lib-action-libs input')].every(i => i.disabled) && !d.querySelector('#lib-cancel').hidden, 'the running chain locks the page');
  check([...d.querySelectorAll('#lib-fill-rows [data-again]')].every(i => i.disabled), 'a running chain also locks every recheck option');
  check(d.querySelector('#lib-progress-title').textContent === '语言与时长' && d.querySelector('#lib-progress-count').textContent === '1 / 2', 'progress shows the current step');
  check([...timers.values()].some(t => t.ms === 2000), 'visible running chain polls progress');
  w.history.replaceState(null, '', '#logs'); w.dispatchEvent(new w.Event('helper:navigate'));
  check(![...timers.values()].some(t => t.ms === 2000), 'leaving the page stops polling');
  w.history.replaceState(null, '', '#library'); w.dispatchEvent(new w.Event('helper:navigate')); await settle();
  await d.querySelector('#lib-cancel').onclick(); await settle();
  check(posts.some(p => p.route === 'library-tasks/cancel'), 'stop goes to the chain');
  state['library-tasks'] = { status:'completed', active:false, steps:[], libraryIds:[], message:'语言与时长：完成；原盘章节：完成。' };
  state['library-language'] = { ...state['library-language'], status:'completed', active:false };
  state['chapters/batch'] = { ...state['chapters/batch'], connectionMatches:false };
  await d.querySelector('#lib-refresh').onclick(); await settle();
  check(step('chapters').textContent.includes('需要重新检查') && d.querySelector('[data-step="chapters"]').disabled, 'a stale plan asks for a new check');
  check(d.querySelector('#lib-progress-title').textContent === '补全已完成', 'finished chain is reported');
  await d.querySelector('#lib-scan').onclick(); await settle();
  check(posts.some(p => p.route === 'library-tasks/scan') && d.querySelector('#toast').textContent.includes('部分检查未启动'), 'one check for all three; failures are reported');
  w.history.replaceState(null, '', '#chapters'); w.dispatchEvent(new w.Event('helper:navigate')); await settle();
  check(d.querySelector('#summary-chapters').textContent.includes('请重新检查媒体库'), 'feature pages do not present stale scope totals as current');
  state['library-language'].items.forEach(i => { i.classification = 'complete'; });
  Object.assign(state['library-language'].summary, { uhdMovies:1, fullHdMovies:1, unclassifiedMovies:1 });
  w.history.replaceState(null, '', '#library'); w.dispatchEvent(new w.Event('helper:navigate')); await settle();
  check(d.querySelector('#lib-action-summary').textContent === '5 部电影 · 3 部蓝光原盘\n其中 UHD 蓝光 1 部 · 1080P 蓝光 1 部 · 规格未识别 1 部', 'unknown specifications are shown separately instead of counted as 1080P');
  d.querySelector('[data-again="language"]').checked = false; d.querySelector('[data-again="language"]').dispatchEvent(new w.Event('change', { bubbles:true }));
  check(step('language').textContent.includes('无需处理') && d.querySelector('[data-step="language"]').disabled, 'complete-only libraries still skip language work by default');
  d.querySelector('[data-again="language"]').checked = true; d.querySelector('[data-again="language"]').dispatchEvent(new w.Event('change', { bubbles:true }));
  check(step('language').textContent.includes('3 部待处理') && !d.querySelector('[data-step="language"]').disabled && !fill().disabled, 'language recheck makes an already complete library runnable');
  for (const key of ['library-language','media-features/batch','chapters/batch']) state[key].connectionMatches = false;
  await d.querySelector('#lib-refresh').onclick(); await settle();
  check(d.querySelectorAll('#lib-action-libs [data-library]').length === 0 && d.querySelectorAll('#lib-rows tr').length <= 1 && !d.querySelector('#lib-rows').textContent.includes('Shared <m>'), 'old global scope cannot leak excluded libraries or movie rows');
  check(d.querySelector('#lib-action-summary').textContent.includes('请重新检查媒体库'), 'invalidated scope asks for a new check instead of showing stale totals');
  console.log(`Passed ${checks} media library task Web checks.`);
})().catch(error => { console.error(error); process.exitCode = 1; }).finally(() => w.close());
