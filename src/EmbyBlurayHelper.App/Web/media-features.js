'use strict';
// Batch interaction stays separate from connection settings and language tasks.
(() => {
  let task = null, timer, request = null, revision = 0, changing = false, selectionPlan = '', page = 0;
  let selected = new Set(), initialized = false;
  const visible = () => !document.hidden && !$('#console').hidden && location.hash === '#media-features';
  const chosen = () => (task?.items || []).filter(i => i.libraryIds.some(id => selected.has(id)));
  function schedule() {
    clearTimeout(timer);
    if (visible() && task?.active && !changing) timer = setTimeout(() => refresh().catch(() => {}), 2000);
  }
  async function refresh() {
    if (request) return request;
    const version = revision;
    const pending = (async () => {
      try {
        const result = await api('media-features/batch');
        if (version === revision) { task = result; $('#feature-batch-error').hidden = true; render(); }
      } catch (error) {
        if (version === revision) { $('#feature-batch-error').textContent = error.message; $('#feature-batch-error').hidden = false; }
        throw error;
      } finally { if (request === pending) request = null; schedule(); }
    })();
    request = pending;
    return pending;
  }
  function render() {
    const active = !!task?.active, locked = active || changing, groups = task?.libraries || [];
    if (selectionPlan !== task?.id) { selectionPlan = task?.id; initialized = false; selected = new Set(); page = 0; }
    if (groups.length && (!initialized || task?.status === 'scanning')) {
      selected = new Set(task?.operation === 'fill' && task.selectedLibraryIds.length ? task.selectedLibraryIds : groups.map(l => l.id)); initialized = true;
    }
    if (active && task?.operation === 'fill') { selected = new Set(task.selectedLibraryIds); $('#feature-batch-recheck').checked = task.includeVerified; }
    const again = $('#feature-batch-recheck').checked;
    const pending = chosen().filter(i => again || !i.verified).length;
    $('#feature-batch-scan').disabled = locked;
    $('#feature-batch-fill').disabled = locked || !task?.canFill || !pending || !mediaFeatureReady;
    $('#feature-batch-fill').textContent = `${task?.operation === 'fill' && !active ? '继续补全所选' : '补全所选媒体库'}${pending ? `（${pending} 部）` : ''}`;
    $('#feature-batch-cancel').hidden = !active;
    $('#feature-batch-cancel').disabled = changing || task?.status === 'cancelling';
    $('#feature-batch-refresh').disabled = changing;
    $('#feature-batch-recheck').disabled = locked;
    $('#feature-batch-scope').hidden = groups.length === 0;
    const all = $('#feature-batch-select-all'), selectedCount = groups.filter(l => selected.has(l.id)).length;
    all.checked = groups.length > 0 && selectedCount === groups.length;
    all.indeterminate = selectedCount > 0 && selectedCount < groups.length; all.disabled = locked;
    $('#feature-batch-groups').innerHTML = groups.map(l => `<tr><td><label><input type="checkbox" data-feature-library="${escapeHtml(l.id)}" ${selected.has(l.id) ? 'checked' : ''} ${locked ? 'disabled' : ''}><span>${escapeHtml(l.name)}</span></label></td><td>${l.moviesChecked}${l.scanComplete ? '' : ' · 检查中'}</td><td>${l.discs}</td><td>${l.verified}</td><td>${l.pending}</td><td>${l.taskPlanned ? `${l.taskProcessed} / ${l.taskPlanned}` : '—'}</td></tr>`).join('');
    $('#feature-batch-selection').textContent = `已选 ${selectedCount} / ${groups.length} 个媒体库 · 本次 ${pending} 部原盘（按影片去重）。`;
    const count = task?.summary;
    $('#feature-batch-disc-count').textContent = task?.id ? count.discs : '—';
    $('#feature-batch-verified-count').textContent = task?.id ? count.verified : '—';
    $('#feature-batch-pending-count').textContent = task?.id ? count.pending : '—';
    $('#feature-batch-formats').textContent = count ? `BDMV ${count.bdmv} 部 · ISO ${count.iso} 部` : 'BDMV / ISO';
    $('#feature-batch-completed-count').textContent = task?.operation === 'fill' ? task.completed : '—';
    $('#feature-batch-failures').textContent = task?.operation === 'fill' ? `失败 ${task.failed} · 跳过 ${task.skipped}` : '逐源探测并读回确认';
    $('#feature-batch-prerequisite').textContent = task?.id && !task.connectionMatches ? '连接、引擎或配置已变更，请重新检查媒体库。'
      : !mediaFeatureReady ? '检查只读取 Emby 信息；实际补全前请开启上方规格识别并确认接入。'
      : '默认只核验未完成的 BDMV / ISO；需要重新处理时可勾选「重新核验已完成」。';
    const states = { idle:'尚未检查', scanning:'正在检查媒体库', ready:'检查完成', filling:'正在补全规格', cancelling:'正在停止任务', cancelled:'任务已停止', interrupted:'上次任务中断', completed:'规格任务结束', failed:'任务未完成' };
    $('#feature-batch-title').textContent = states[task?.status] || states.idle;
    $('#feature-batch-progress').dataset.state = task?.status || 'idle';
    const total = task?.operation === 'fill' ? task.planned : groups.length;
    const done = task?.operation === 'fill' ? task.processed : groups.filter(l => l.scanComplete).length;
    const bar = $('#feature-batch-bar'); bar.hidden = !task?.id;
    if (active && !total) bar.removeAttribute('value'); else { bar.max = Math.max(1, total); bar.value = Math.min(done, total); }
    $('#feature-batch-percent').textContent = total ? `${done} / ${total}${task?.operation === 'fill' ? ' 部' : ' 个媒体库'}` : '';
    const current = task?.items.find(i => i.id === task.currentItemId);
    $('#feature-batch-message').textContent = (task?.message || '先检查媒体库，再选择需要补全规格的原盘。') +
      (active && current?.message ? ` ${current.message}` : '');
    $('#feature-batch-counts').textContent = task?.operation === 'fill' ? `成功 ${task.completed} · 失败 ${task.failed} · 跳过 ${task.skipped}` : '任务运行且此页面可见时刷新进度';
    const at = task?.finishedAt || task?.startedAt;
    $('#feature-batch-time').textContent = at ? `${task.finishedAt ? '结束' : '开始'}于 ${new Date(at).toLocaleString('zh-CN', {hour12:false})}` : '';
    if (task?.persistenceWarning) { $('#feature-batch-error').hidden = false; $('#feature-batch-error').textContent = task.persistenceWarning; }
    $('#feature-batch-details').hidden = !task?.items.length;
    $('#feature-batch-detail-count').textContent = task?.items.length ? task.items.length + ' 部' : '';
    renderRows();
  }
  function renderRows() {
    const filter = $('#feature-batch-filter').value;
    const rows = chosen().filter(i => filter === 'pending' ? !i.verified : filter === 'issues' ? ['failed','unverified'].includes(i.result) : true);
    const pages = Math.max(1, Math.ceil(rows.length / 30)); page = Math.max(0, Math.min(page, pages - 1));
    const states = { pending:'等待核验', running:'处理中', completed:'已核验成功', failed:'失败', unverified:'尚未核验', skipped:'已跳过' };
    $('#feature-batch-rows').innerHTML = rows.slice(page * 30, page * 30 + 30).map(i => `<tr><td><strong>${escapeHtml(i.name)}</strong><small>ID ${escapeHtml(i.id)} · ${escapeHtml((task.libraries || []).filter(l => i.libraryIds.includes(l.id)).map(l => l.name).join('、'))}</small></td><td>${i.sources.map(s => `<small><b>${escapeHtml(s.kind)}</b> · ${escapeHtml(s.details)}${s.verifiedAt ? ' · 核验于 ' + escapeHtml(new Date(s.verifiedAt).toLocaleString('zh-CN',{hour12:false})) : ''}</small>`).join('')}</td><td><span class="tag ${i.verified ? '' : 'off'}">${i.verified ? '批量已核验' : '待核验'}</span></td><td><strong class="library-result ${escapeHtml(i.result)}">${states[i.result] || '—'}</strong><small>${escapeHtml(i.message)}</small></td></tr>`).join('') || '<tr><td colspan="4" class="empty">此范围没有影片</td></tr>';
    $('#feature-batch-table-count').textContent = rows.length + ' 部';
    $('#feature-batch-page').textContent = `${page + 1} / ${pages}`;
    $('#feature-batch-prev').disabled = page === 0; $('#feature-batch-next').disabled = page === pages - 1;
  }
  async function change(operation) {
    if (changing) return;
    if (operation === 'fill' && (!task?.canFill || !selected.size || !mediaFeatureReady || !chosen().some(i => $('#feature-batch-recheck').checked || !i.verified))) return;
    changing = true; revision++; clearTimeout(timer); render();
    try {
      task = await api('media-features/batch/' + operation, operation === 'fill' ? {planId:task.id, libraryIds:[...selected], includeVerified:$('#feature-batch-recheck').checked} : {});
      $('#feature-batch-error').hidden = true;
      toast(operation === 'scan' ? '已开始检查 BDMV / ISO。' : operation === 'fill' ? '规格任务已启动。' : '正在停止规格任务。');
    } catch (error) { $('#feature-batch-error').textContent = error.message; $('#feature-batch-error').hidden = false; toast(error.message, true); }
    finally { changing = false; render(); schedule(); }
  }
  $('#feature-batch-scan').onclick = () => change('scan');
  $('#feature-batch-fill').onclick = () => change('fill');
  $('#feature-batch-cancel').onclick = () => change('cancel');
  $('#feature-batch-refresh').onclick = () => refresh().catch(error => toast(error.message, true));
  $('#feature-batch-select-all').onchange = event => { selected = new Set(event.currentTarget.checked ? (task?.libraries || []).map(l => l.id) : []); page = 0; render(); };
  $('#feature-batch-groups').onchange = event => { const id = event.target.dataset.featureLibrary; if (!id) return;
    event.target.checked ? selected.add(id) : selected.delete(id); page = 0; render(); };
  $('#feature-batch-recheck').onchange = render;
  $('#feature-batch-filter').onchange = () => { page = 0; renderRows(); };
  $('#feature-batch-prev').onclick = () => { page--; renderRows(); };
  $('#feature-batch-next').onclick = () => { page++; renderRows(); };
  window.addEventListener('helper:navigate', () => { clearTimeout(timer); if (visible()) refresh().catch(() => {}); });
  window.addEventListener('helper:media-features', render);
  window.addEventListener('helper:logout', () => { clearTimeout(timer); revision++; request = null; task = null; selected.clear(); initialized = false; });
  document.addEventListener('visibilitychange', () => { clearTimeout(timer); if (visible()) refresh().catch(() => {}); });
  if (configuration && visible()) refresh().catch(() => {});
})();
