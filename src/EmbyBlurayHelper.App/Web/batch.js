'use strict';
// One batch workflow for every library task: check libraries, choose a scope,
// fill, follow progress and read per-movie results. Pages only describe what
// differs (counts, columns, readiness). Loaded before app.js; uses its helpers.
function BatchPanel(c) {
  const p = c.prefix, el = id => document.getElementById(p + '-' + id);
  let task = null, timer, request = null, revision = 0, busy = false, plan, initialized = false, selected = new Set(), page = 0;
  const results = { pending:'待处理', running:'处理中', completed:'已完成', partial:'仍有缺失', failed:'失败', unverified:'未核验', skipped:'已跳过', ...c.results };
  const titles = { idle:'尚未检查', scanning:'正在检查媒体库', ready:'检查完成', filling:'正在补全' + c.noun, cancelling:'正在停止任务',
    cancelled:'任务已停止', interrupted:'上次任务中断', completed:'补全任务结束', failed:'任务未完成' };
  const time = value => new Date(value).toLocaleString('zh-CN', { hour12:false });
  document.getElementById(c.mount).innerHTML = `
   <div class="library-counts">${c.counts.map(k => `<div><span>${k.label}</span><strong id="${p}-count-${k.key}">—</strong><small id="${p}-count-${k.key}-foot">${k.idle}</small></div>`).join('')}</div>
   <div class="library-scope" id="${p}-scope" hidden>
    <div class="library-scope-heading"><div><h3>选择媒体库</h3><p>跨库影片只处理一次；可全选，也可仅处理部分媒体库。</p></div><label class="library-select-all"><input type="checkbox" id="${p}-select-all">全选媒体库</label></div>
    <div class="log-table-wrap"><table class="log-table library-scope-table"><thead><tr><th>媒体库</th>${c.columns.map(x => `<th>${x.label}</th>`).join('')}${c.progress ? '<th>本次进度</th>' : ''}</tr></thead><tbody id="${p}-groups"></tbody></table></div>
    <p class="field-help" id="${p}-selection"></p>
   </div>
   ${c.recheck ? `<label class="feature-batch-recheck"><input id="${p}-recheck" type="checkbox">${c.recheck}</label>` : ''}
   <div class="form-actions library-actions"><button id="${p}-scan" class="button secondary"><svg><use href="#search"/></svg>检查媒体库</button><button id="${p}-fill" class="button primary" disabled>补全所选媒体库</button><button id="${p}-cancel" class="button danger" hidden>停止</button><button id="${p}-progress-refresh" class="text-button accent"><svg><use href="#refresh"/></svg>刷新进度</button></div>
   <p class="field-help" id="${p}-note" hidden></p>
   <div class="library-progress" id="${p}-progress" data-state="idle">
    <div class="library-progress-heading"><strong id="${p}-task-title">尚未检查</strong><span id="${p}-task-percent"></span></div>
    <progress id="${p}-task-bar" max="1" value="0" aria-label="${c.noun}任务进度" hidden></progress>
    <p id="${p}-task-message" role="status" aria-live="polite">先检查媒体库，再选择需要补全的原盘。</p><p class="library-current" id="${p}-current" hidden></p>
    <div class="library-task-foot"><span id="${p}-task-counts"></span><span id="${p}-task-time"></span></div>
   </div>
   <p class="error" id="${p}-error" role="alert" hidden></p>
   <details class="library-details" id="${p}-details" hidden><summary>影片明细 <span id="${p}-detail-count"></span></summary>
    <div class="library-table-toolbar"><label for="${p}-filter">显示所选库</label><select id="${p}-filter"><option value="all">全部原盘</option><option value="pending">${c.pendingLabel}</option><option value="issues">${c.issuesLabel}</option></select><span id="${p}-table-count"></span></div>
    <div class="log-table-wrap"><table class="log-table library-table ${c.tableClass || ''}"><thead><tr><th>影片</th>${c.itemColumns.map(x => `<th>${x}</th>`).join('')}<th>任务结果</th></tr></thead><tbody id="${p}-rows"></tbody></table></div>
    <div class="library-pagination"><button class="button secondary" id="${p}-prev">上一页</button><span id="${p}-page"></span><button class="button secondary" id="${p}-next">下一页</button></div>
   </details>`;
  const visible = () => !document.hidden && !!configuration && !$('#console').hidden && location.hash === '#' + c.page;
  const chosen = () => (task?.items || []).filter(i => (i.libraryIds || []).some(id => selected.has(id)));
  const again = () => !!c.recheck && el('recheck').checked;
  const eligible = () => chosen().filter(i => again() || c.pending(i));
  const filling = () => task?.operation ? task.operation === 'fill' : task?.status !== 'scanning' && task?.planned > 0;
  const scopeCount = () => (task?.libraries || []).filter(l => selected.has(l.id)).length;
  function fillPlan() {
    const items = eligible(), extra = c.plan?.(items) || {}, count = extra.runnable ?? items.length;
    const resumable = filling() && !task?.active && ['cancelled','interrupted','failed'].includes(task?.status);
    return { count, label: extra.label ?? `${resumable ? '继续补全所选' : '补全所选媒体库'}${count ? `（${count} 部）` : ''}`, note: extra.note || '' };
  }
  const allowed = f => !busy && !task?.active && !!task?.canFill && scopeCount() > 0 && f.count > 0 && (c.ready ? !!c.ready() : true);
  function schedule() { clearTimeout(timer); if (visible() && task?.active && !busy) timer = setTimeout(() => refresh().catch(() => {}), 2000); }
  function showError(message) { el('error').textContent = message; el('error').hidden = false; }
  function render() {
    const active = !!task?.active, locked = busy || active, groups = task?.libraries || [];
    if (plan !== task?.id) { plan = task?.id; initialized = false; selected = new Set(); page = 0; }
    if (groups.length && (!initialized || task.status === 'scanning')) {
      selected = new Set(task.status !== 'scanning' && task.selectedLibraryIds?.length ? task.selectedLibraryIds : groups.map(l => l.id)); initialized = true;
    }
    if (active && filling()) { selected = new Set(task.selectedLibraryIds); if (c.recheck && task.includeVerified !== undefined) el('recheck').checked = task.includeVerified; }
    const checked = c.checked ? c.checked(task) : !!task?.id;
    for (const k of c.counts) {
      el('count-' + k.key).textContent = checked ? k.value(task) : '—';
      el('count-' + k.key + '-foot').textContent = checked && k.foot ? k.foot(task) : k.idle;
    }
    el('scope').hidden = groups.length === 0;
    const all = el('select-all'), count = scopeCount();
    all.checked = groups.length > 0 && count === groups.length; all.indeterminate = count > 0 && count < groups.length; all.disabled = locked;
    el('groups').innerHTML = groups.map(l => `<tr><td><label><input type="checkbox" data-library="${escapeHtml(l.id)}" ${selected.has(l.id) ? 'checked' : ''} ${locked ? 'disabled' : ''}><span>${escapeHtml(l.name)}</span></label></td>${c.columns.map(x => `<td>${x.cell(l)}</td>`).join('')}${c.progress ? `<td>${l.taskPlanned ? `${l.taskProcessed} / ${l.taskPlanned}` : '—'}</td>` : ''}</tr>`).join('');
    el('selection').textContent = `已选 ${count} / ${groups.length} 个媒体库 · 待处理 ${eligible().length} 部（跨库影片只计一次）。${filling() && task.scope ? '本次任务范围：' + task.scope + '。' : ''}`;
    const f = fillPlan();
    el('scan').disabled = locked || (c.canScan ? !c.canScan() : false);
    el('fill').disabled = !allowed(f); el('fill').textContent = f.label;
    el('cancel').hidden = !active; el('cancel').disabled = busy || task?.status === 'cancelling';
    el('progress-refresh').disabled = busy;
    if (c.recheck) el('recheck').disabled = locked;
    const note = [task?.id && task.connectionMatches === false ? 'Emby 连接或配置已变化，请重新检查媒体库。' : c.note?.(task) || '', f.note].filter(Boolean).join(' ');
    el('note').textContent = note; el('note').hidden = !note;
    el('progress').dataset.state = task?.status || 'idle';
    el('task-title').textContent = titles[task?.status] || titles.idle;
    el('task-message').textContent = task?.message || '先检查媒体库，再选择需要补全的原盘。';
    const fill = filling(), total = fill ? task?.planned || 0 : groups.length, done = fill ? task?.processed || 0 : groups.filter(l => l.scanComplete).length;
    const bar = el('task-bar'); bar.hidden = !task?.id;
    if (active && !total) bar.removeAttribute('value'); else { bar.max = Math.max(1, total); bar.value = Math.min(done, total); }
    el('task-percent').textContent = total ? `${done} / ${total}${fill ? ' 部' : ' 个媒体库'}` : '';
    const current = task?.items?.find(i => i.id === task.currentItemId);
    const elapsed = current?.attemptedAt ? ` · 已用 ${Math.max(0, Math.floor((Date.now() - new Date(current.attemptedAt)) / 1000))} 秒` : '';
    el('current').hidden = !active || !task?.currentName;
    el('current').textContent = task?.currentName ? `当前：${task.currentName}${current?.message ? ' · ' + current.message : ''}${elapsed}` : '';
    el('task-counts').textContent = fill && task?.planned ? (c.tally ? c.tally(task) : `成功 ${task.completed} · 失败 ${task.failed} · 跳过 ${task.skipped}`) : '进度仅在任务运行且此页面可见时自动刷新';
    const at = task?.finishedAt || task?.startedAt;
    el('task-time').textContent = at ? `${task.finishedAt ? '结束' : '开始'}于 ${time(at)}` : '';
    if (task?.persistenceWarning) showError(task.persistenceWarning);
    el('details').hidden = !task?.items?.length;
    el('detail-count').textContent = task?.items?.length ? task.items.length + ' 部' : '';
    renderRows();
  }
  function renderRows() {
    const filter = el('filter').value;
    const rows = chosen().filter(i => filter === 'pending' ? c.pending(i) : filter === 'issues' ? c.issue(i) : true);
    const pages = Math.max(1, Math.ceil(rows.length / 30)); page = Math.max(0, Math.min(page, pages - 1));
    const names = new Map((task?.libraries || []).map(l => [l.id, l.name]));
    el('rows').innerHTML = rows.slice(page * 30, page * 30 + 30).map(i => `<tr><td><strong>${escapeHtml(i.name)}</strong><small>ID ${escapeHtml(i.id)}${c.meta ? ' · ' + c.meta(i) : ''} · ${escapeHtml((i.libraryIds || []).map(id => names.get(id)).filter(Boolean).join('、'))}</small></td>${c.cells(i).map(x => `<td>${x}</td>`).join('')}<td><strong class="library-result ${escapeHtml(i.result)}">${results[i.result] || '—'}</strong><small>${escapeHtml(i.message)}${i.verifiedAt ? ' · 核验于 ' + escapeHtml(time(i.verifiedAt)) : ''}</small></td></tr>`).join('')
      || `<tr><td colspan="${c.itemColumns.length + 2}" class="empty">此范围没有影片</td></tr>`;
    el('table-count').textContent = rows.length + ' 部';
    el('page').textContent = `${page + 1} / ${pages}`; el('prev').disabled = page === 0; el('next').disabled = page >= pages - 1;
  }
  async function refresh() {
    if (request) return request;
    const version = revision;
    const pending = (async () => {
      try {
        const result = await api(c.api);
        if (version !== revision) return;
        const scanFinished = task?.status === 'scanning' && !result.active;
        task = result; el('error').hidden = true; render();
        if (scanFinished) c.onScanFinished?.();
      } catch (error) { if (version === revision) showError(error.message); throw error; }
      finally { if (request === pending) request = null; schedule(); }
    })();
    request = pending;
    return pending;
  }
  async function change(operation) {
    if (busy || operation === 'fill' && !allowed(fillPlan())) return;
    busy = true; revision++; clearTimeout(timer); render();
    try {
      if (operation === 'fill' && c.beforeFill && !await c.beforeFill(eligible())) return;
      const body = operation === 'fill' ? { planId: task.id, libraryIds: [...selected], ...(c.recheck ? { includeVerified: again() } : {}) } : {};
      task = await api(c.api + '/' + operation, body);
      el('error').hidden = true; page = 0;
      toast(operation === 'scan' ? '已开始检查媒体库。' : operation === 'fill' ? '补全任务已启动，可在此查看进度。' : '正在停止任务。');
    } catch (error) { showError(error.message); toast(error.message, true); }
    finally { busy = false; render(); schedule(); }
  }
  el('scan').onclick = () => change('scan'); el('fill').onclick = () => change('fill'); el('cancel').onclick = () => change('cancel');
  el('progress-refresh').onclick = () => refresh().catch(error => toast(error.message, true));
  el('select-all').onchange = event => { selected = new Set(event.currentTarget.checked ? (task?.libraries || []).map(l => l.id) : []); page = 0; render(); };
  el('groups').onchange = event => { const id = event.target.dataset.library; if (!id) return; event.target.checked ? selected.add(id) : selected.delete(id); page = 0; render(); };
  if (c.recheck) el('recheck').onchange = render;
  el('filter').onchange = () => { page = 0; renderRows(); };
  el('prev').onclick = () => { page--; renderRows(); }; el('next').onclick = () => { page++; renderRows(); };
  window.addEventListener('helper:navigate', () => { clearTimeout(timer); if (visible()) refresh().catch(() => {}); });
  document.addEventListener('visibilitychange', () => { clearTimeout(timer); if (visible()) refresh().catch(() => {}); });
  window.addEventListener('helper:logout', () => { clearTimeout(timer); revision++; request = null; task = null; selected = new Set(); initialized = false; plan = undefined; });
  for (const name of c.refreshOn || []) window.addEventListener(name, render);
  render();
  return { refresh, render, schedule, change, visible, get task() { return task; }, set task(value) { task = value; } };
}
