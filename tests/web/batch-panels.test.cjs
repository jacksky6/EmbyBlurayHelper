// The three library tasks share one component; their controls must stay identical.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { JSDOM } = require(path.resolve(process.argv[2] || 'artifacts/ui-check/node_modules/jsdom'));
const root = path.resolve(__dirname, '../..');
const web = name => fs.readFileSync(path.join(root, 'src/EmbyBlurayHelper.App/Web', name), 'utf8');
const dom = new JSDOM(web('index.html'), { url: 'http://localhost/#logs', runScripts: 'outside-only' });
const w = dom.window, d = w.document;
w.fetch = async () => { throw new Error('no requests expected'); };
const evaluate = code => vm.runInContext(code, dom.getInternalVMContext());
evaluate(web('batch.js') + '\n' + web('app.js').replace('startConsole().catch(()=>{});', ''));
evaluate(web('media-features.js')); evaluate(web('chapters.js'));
let checks = 0;
const check = (condition, message) => { assert.ok(condition, message); checks++; };
const panels = [['library','page-language'],['feature-batch','page-media-features'],['chapter','page-chapters']];
const text = id => d.getElementById(id).textContent.trim();
for (const [p, page] of panels) {
  check(d.getElementById(page).contains(d.getElementById(p + '-scan')), `${p} panel is on its own page`);
  check(text(p + '-scan') === '检查媒体库' && text(p + '-cancel') === '停止' && text(p + '-progress-refresh') === '刷新进度', `${p} uses the shared button wording`);
  check(text(p + '-fill').startsWith('补全所选媒体库'), `${p} fill button wording`);
  check(d.getElementById(p + '-filter').options.length === 3 && d.getElementById(p + '-details').querySelector('summary').textContent.startsWith('影片明细'), `${p} detail filter and summary`);
  check(text(p + '-task-title') === '尚未检查', `${p} idle state wording`);
}
const counts = panels.map(([p]) => d.querySelectorAll(`[id^="${p}-count-"]:not([id$="-foot"])`).length);
check(counts.every(n => n === 4), 'every panel shows four count cards');
const ids = [...d.querySelectorAll('[id]')].map(e => e.id);
check(ids.length === new Set(ids).size, 'generated panels do not duplicate element ids');
console.log(`Passed ${checks} shared batch panel checks.`);
dom.window.close();
