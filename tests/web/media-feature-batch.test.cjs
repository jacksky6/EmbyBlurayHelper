const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { JSDOM } = require(path.resolve('artifacts/ui-check/node_modules/jsdom'));
const root = path.resolve(__dirname, '../..');
const dom = new JSDOM(fs.readFileSync(path.join(root, 'src/EmbyBlurayHelper.App/Web/index.html'), 'utf8'), {
  url: 'http://127.0.0.1/#media-features', runScripts: 'outside-only', pretendToBeVisual: true
});
const w = dom.window, d = w.document, calls = [], timers = new Map();
let checks = 0, nextTimer = 1;
const check = (condition, message) => { assert.ok(condition, message); checks++; };
const evaluate = code => vm.runInContext(code, dom.getInternalVMContext());
const movie = (id, libraries) => ({id, name:'Movie <'+id+'>', libraryIds:libraries, verified:false, result:'', message:'',
  sources:[{id:'source-'+id, kind:id==='b'?'ISO':'BDMV', details:'当前未显示 Atmos / Dolby Vision'}]});
let fixture = {id:'plan-1', status:'ready', operation:'scan', active:false, scanComplete:true, connectionMatches:true, canFill:true,
  moviesChecked:5, planned:0, processed:0, completed:0, failed:0, skipped:0, message:'检查完成', selectedLibraryIds:[], includeVerified:false,
  summary:{discs:3,verified:0,pending:3,bdmv:2,iso:1},
  libraries:[{id:'a',name:'Library <A>',scanComplete:true,moviesChecked:3,discs:2,verified:0,pending:2,taskPlanned:0,taskProcessed:0},
    {id:'b',name:'Library B',scanComplete:true,moviesChecked:3,discs:2,verified:0,pending:2,taskPlanned:0,taskProcessed:0}],
  items:[movie('shared',['a','b']),movie('a',['a']),movie('b',['b'])]};
w.setTimeout = (fn, ms) => { const id=nextTimer++; timers.set(id,{fn,ms}); return id; };
w.clearTimeout = id => timers.delete(id);
w.fetch = async (url, options) => {
  calls.push({url,options});
  let result=fixture;
  if(url.endsWith('/fill')) {
    const body=JSON.parse(options.body);
    result={...fixture,status:'filling',operation:'fill',active:true,planned:2,selectedLibraryIds:body.libraryIds,includeVerified:body.includeVerified};
  }
  return {status:200,ok:true,headers:{get:()=> 'application/json'},json:async()=>structuredClone(result)};
};
const app=fs.readFileSync(path.join(root,'src/EmbyBlurayHelper.App/Web/app.js'),'utf8');
evaluate(app.replace('startConsole().catch(()=>{});',''));
evaluate("configuration={};mediaFeatureReady=true;$('#console').hidden=false;");
evaluate(fs.readFileSync(path.join(root,'src/EmbyBlurayHelper.App/Web/media-features.js'),'utf8'));
const settle = () => new Promise(resolve => setImmediate(resolve));
(async()=>{
  await settle();
  check(d.querySelector('#page-media-features').contains(d.querySelector('#feature-batch-panel')),'batch lives on the media features page');
  check(d.querySelector('#feature-batch-fill').textContent.includes('3 部'),'shared movie is counted once across selected libraries');
  check(d.querySelector('#feature-batch-disc-count').textContent==='3' && d.querySelector('#feature-batch-formats').textContent.includes('ISO 1'),'format-specific counts are visible');
  check(d.querySelector('#feature-batch-groups').textContent.includes('Library <A>') && !d.querySelector('#feature-batch-groups a'),'library names are escaped');
  check(d.querySelector('#feature-batch-rows').textContent.includes('Movie <shared>') && !d.querySelector('#feature-batch-rows shared'),'movie names are escaped');
  check(![...timers.values()].some(t=>t.ms===2000),'idle plan has no progress polling');
  d.querySelector('#feature-batch-select-all').checked=false; d.querySelector('#feature-batch-select-all').dispatchEvent(new w.Event('change'));
  check(d.querySelector('#feature-batch-fill').disabled,'empty scope cannot launch the batch');
  const before=calls.length; await d.querySelector('#feature-batch-fill').onclick();
  check(calls.length===before,'forced click handler also rejects an empty selection');
  const a=d.querySelector('[data-feature-library="a"]'); a.checked=true; a.dispatchEvent(new w.Event('change',{bubbles:true}));
  check(d.querySelector('#feature-batch-fill').textContent.includes('2 部') && d.querySelector('#feature-batch-select-all').indeterminate,'partial scope updates counts and select-all state');
  check(d.querySelectorAll('#feature-batch-rows tr').length===2,'detail rows follow the chosen library');
  w.dispatchEvent(new w.Event('helper:media-features'));
  check(!d.querySelector('[data-feature-library="b"]').checked,'status refresh preserves the chosen scope');
  await d.querySelector('#feature-batch-fill').onclick();
  const sent=JSON.parse(calls.find(c=>c.url.endsWith('/fill')).options.body);
  check(sent.libraryIds.join(',')==='a' && !sent.includeVerified,'batch sends only selected libraries and defaults to pending sources');
  check([...d.querySelectorAll('[data-feature-library]')].every(el=>el.disabled) && d.querySelector('#feature-batch-recheck').disabled,'active task locks the scope and recheck mode');
  check([...timers.values()].some(t=>t.ms===2000),'visible active task polls progress');
  w.history.replaceState(null,'','#infuse'); w.dispatchEvent(new w.Event('helper:navigate'));
  check(![...timers.values()].some(t=>t.ms===2000),'leaving the page stops progress polling');
  w.history.replaceState(null,'','#media-features'); w.dispatchEvent(new w.Event('helper:navigate')); await settle();
  fixture={...fixture,status:'completed',operation:'fill',planned:2,processed:2,completed:2,selectedLibraryIds:['a'],
    summary:{...fixture.summary,verified:2,pending:1},items:fixture.items.map(i=>i.libraryIds.includes('a')?{...i,verified:true,result:'completed'}:i)};
  await d.querySelector('#feature-batch-refresh').onclick();
  check(![...timers.values()].some(t=>t.ms===2000),'completed batch stops polling');
  check(d.querySelector('#feature-batch-fill').disabled && d.querySelector('#feature-batch-verified-count').textContent==='2','negative but verified results do not remain pending');
  d.querySelector('#feature-batch-recheck').checked=true;d.querySelector('#feature-batch-recheck').dispatchEvent(new w.Event('change'));
  check(!d.querySelector('#feature-batch-fill').disabled && d.querySelector('#feature-batch-fill').textContent.includes('2 部'),'explicit recheck makes completed sources eligible');
  evaluate('mediaFeatureReady=false');w.dispatchEvent(new w.Event('helper:media-features'));
  check(d.querySelector('#feature-batch-fill').disabled,'disabling feature integration also disables batch start');
  console.log(`Passed ${checks} media-feature batch Web checks.`);
})().catch(error=>{console.error(error);process.exitCode=1;}).finally(()=>w.close());
