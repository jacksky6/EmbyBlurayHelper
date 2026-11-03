'use strict';
(() => {
  let status = null, task = null, selected = new Set(), selectionId = '', selectionInitialized = false, timer, pending, revision = 0, busy = false, page = 0;
  const visible = () => !document.hidden && !$('#console').hidden && location.hash === '#chapters';
  const chosen = () => (task?.items || []).filter(i => i.libraryIds.some(id => selected.has(id)));
  const ready = () => status?.ready && status?.enabled && status?.automatic;
  const stateNames = { empty:'缺少章节', generated:'等间隔生成', existing:'已有章节，保留', managed:'原生章节已核验', error:'需重试' };
  function schedule() { clearTimeout(timer); const thumbnailsActive = status?.thumbnails?.worker?.state === 'Running'; if (visible() && (task?.active || thumbnailsActive) && !busy) timer = setTimeout(() => refresh(thumbnailsActive).catch(() => {}), 2000); }
  function render() {
    const active = !!task?.active, locked = busy || active, groups = task?.libraries || [];
    $('#chapter-enabled').disabled = busy;
    if (!busy) $('#chapter-enabled').checked = !!status?.enabled;
    $('#chapter-status-title').textContent = busy ? '正在保存' : !status?.ready ? '等待安装或连接' : status?.enabled && status?.automatic ? '已开启 · 新片自动补全' : status?.enabled !== status?.automatic ? '配置状态不一致' : '已关闭';
    $('#chapter-status-copy').textContent = status?.error || (status?.enabled !== status?.automatic ? '工具与 Emby 插件开关不同步，请重新保存章节设置。' : status?.automatic ? `已确认插件接入 · ${status.checkedItems} 个原盘媒体源已保存。` : '开启后处理新入库原盘；已有影片请在下方选择媒体库。');
    $('#chapter-save').disabled = busy;
    const thumbnails = status?.thumbnails, worker = thumbnails?.worker;
    $('#chapter-thumbnail-state').textContent = !thumbnails?.available ? '截图接入尚未确认。需要插件 0.1.6 和新版 Helper；更新后刷新状态。' : worker?.state === 'Running' ? `Emby 原盘截图任务运行中 · ${Math.round(worker.progress || 0)}%` : worker?.lastStatus === 'Failed' ? `上次截图任务失败：${worker.error || '请查看 Emby 的 DiscThumbnails 日志，可重跑任务补齐缺失图片。'}` : worker?.lastStatus?.startsWith('Cancel') ? '原盘截图任务已停止 · 已完成图片保留，可重跑任务补齐缺失图片。' : '已接入 Emby 截图设置 · ' + (worker?.lastStatus === 'Completed' ? '上次原盘截图任务已完成。' : '等待 Emby 扫描或提取任务触发。');
    $('#chapter-thumbnail-libraries').innerHTML = (thumbnails?.libraries || []).map(l => `<tr><td>${escapeHtml(l.name)}</td><td>${({never:'从不',task:'作为计划任务',scanandtask:'计划任务及添加媒体时'})[l.mode] || '未知'}</td><td>${l.interval === -1 ? '章节标记' : `${Number(l.interval)} 秒预览`}</td><td>${l.interval !== -1 && l.saveLocal ? '媒体旁' : 'Emby 元数据目录'}</td></tr>`).join('');
    if (selectionId !== task?.id) { selectionId = task?.id; selectionInitialized = false; selected = new Set(); page = 0; }
    if (!selectionInitialized && groups.length) { selected = new Set(task?.selectedLibraryIds?.length ? task.selectedLibraryIds : groups.map(l => l.id)); selectionInitialized = true; }
    if (task?.status === 'scanning') groups.forEach(l => selected.add(l.id));
    $('#chapter-total').textContent = task?.id ? task.summary.discs : '—';
    $('#chapter-verified').textContent = task?.id ? task.summary.verified : '—';
    $('#chapter-pending').textContent = task?.id ? task.summary.pending : '—';
    $('#chapter-preserved').textContent = task?.id ? task.summary.preserved : '—';
    const all = $('#chapter-select-all'), selectedCount = groups.filter(l => selected.has(l.id)).length;
    all.checked = !!groups.length && selectedCount === groups.length; all.indeterminate = selectedCount > 0 && selectedCount < groups.length; all.disabled = locked;
    $('#chapter-scope').hidden = !groups.length;
    $('#chapter-groups').innerHTML = groups.map(l => `<tr><td><label><input type="checkbox" data-chapter-library="${escapeHtml(l.id)}" ${selected.has(l.id) ? 'checked' : ''} ${locked ? 'disabled' : ''}><span>${escapeHtml(l.name)}</span></label></td><td>${l.moviesChecked}${l.scanComplete ? '' : ' · 检查中'}</td><td>${l.discs}</td><td>${l.verified}</td><td>${l.pending}</td><td>${l.preserved}</td></tr>`).join('');
    const count = chosen().filter(i => $('#chapter-recheck').checked || i.pending).length;
    $('#chapter-scan').disabled = locked || !status?.ready;
    $('#chapter-fill').disabled = locked || !ready() || !task?.canFill || !selectedCount || !count;
    $('#chapter-fill').textContent = `补全所选媒体库${count ? `（${count} 部）` : ''}`;
    $('#chapter-cancel').hidden = !active; $('#chapter-cancel').disabled = busy || task?.status === 'cancelling'; $('#chapter-recheck').disabled = locked;
    const states = { idle:'尚未检查', scanning:'正在检查媒体库', ready:'检查完成', filling:'正在补全原生章节', completed:'章节任务结束', cancelled:'任务已停止', interrupted:'上次任务中断', failed:'任务未完成', cancelling:'正在停止' };
    $('#chapter-task-title').textContent = states[task?.status] || states.idle;
    $('#chapter-message').textContent = task?.message || '先检查媒体库，再选择需要补全的原盘。';
    $('#chapter-progress-count').textContent = task?.planned ? `${task.processed} / ${task.planned} 部` : '';
    $('#chapter-task-counts').textContent = task?.planned ? `成功 ${task.completed} · 失败 ${task.failed} · 保留 / 跳过 ${task.skipped}` : '仅任务运行且页面可见时刷新进度';
    const bar = $('#chapter-bar'); bar.hidden = !active && !task?.planned;
    if (active && task.status === 'scanning') bar.removeAttribute('value'); else { bar.max = Math.max(1, task?.planned || 1); bar.value = task?.processed || 0; }
    if (task?.persistenceWarning) showError(task.persistenceWarning);
    $('#chapter-details').hidden = !task?.items.length;
    const rows = chosen(), pages = Math.max(1, Math.ceil(rows.length / 30)); page = Math.max(0, Math.min(page, pages - 1));
    $('#chapter-rows').innerHTML = rows.slice(page * 30, page * 30 + 30).map(i => `<tr><td><strong>${escapeHtml(i.name)}</strong><small>ID ${escapeHtml(i.id)}</small></td><td>${i.sources.map(s => `<small><b>${escapeHtml(s.kind)}</b> · ${s.count} 个章节 · ${escapeHtml(stateNames[s.state] || s.state)}${s.playlist ? ' · ' + escapeHtml(s.playlist) : ''}</small>`).join('')}</td><td><strong class="library-result ${escapeHtml(i.result)}">${({completed:'已完成',failed:'失败',running:'处理中',skipped:'已保留',pending:'待处理'})[i.result] || '—'}</strong><small>${escapeHtml(i.message)}</small></td></tr>`).join('') || '<tr><td colspan="3" class="empty">所选范围没有原盘</td></tr>';
    $('#chapter-page').textContent = `${page + 1} / ${pages}`; $('#chapter-prev').disabled = page === 0; $('#chapter-next').disabled = page === pages - 1;
  }
  function showError(message) { $('#chapter-error').textContent = message; $('#chapter-error').hidden = false; }
  async function refresh(includeStatus = true) {
    if (pending) return pending;
    const version = revision;
    const request = (async () => {
      try {
        const results = await Promise.all([api('chapters/batch'), includeStatus ? api('chapters') : Promise.resolve(status)]);
        if (version !== revision) return;
        [task, status] = results; $('#chapter-error').hidden = true;
        if (includeStatus && document.activeElement !== $('#chapter-reader-path')) $('#chapter-reader-path').value = status.readerPath || status.suggestedReaderPath || '';
        render();
      } catch (error) { if (version === revision) showError(error.message); throw error; }
      finally { if (pending === request) pending = null; schedule(); }
    })();
    pending = request; return pending;
  }
  async function configure() {
    if (busy) return;
    const enabled = $('#chapter-enabled').checked, readerPath = $('#chapter-reader-path').value.trim(); busy = true; revision++; render();
    try { status = await api('chapters', {enabled, readerPath}); toast(enabled ? '原盘章节已开启。' : '已关闭，保存的章节保留。'); }
    catch (error) { showError(error.message); toast(error.message, true); }
    finally { busy = false; render(); schedule(); }
  }
  async function change(operation) {
    if (busy) return;
    if (operation === 'fill' && (!ready() || !task?.canFill || !selected.size || !chosen().some(i => $('#chapter-recheck').checked || i.pending))) return;
    busy = true; revision++; clearTimeout(timer); render();
    try { task = await api('chapters/batch/' + operation, operation === 'fill' ? {planId:task.id, libraryIds:[...selected], includeVerified:$('#chapter-recheck').checked} : {}); $('#chapter-error').hidden = true; }
    catch (error) { showError(error.message); toast(error.message, true); }
    finally { busy = false; render(); schedule(); }
  }
  $('#chapter-enabled').onchange = configure; $('#chapter-save').onclick = configure;
  $('#chapter-refresh').onclick = () => refresh().catch(error => toast(error.message, true));
  $('#chapter-scan').onclick = () => change('scan'); $('#chapter-fill').onclick = () => change('fill'); $('#chapter-cancel').onclick = () => change('cancel');
  $('#chapter-select-all').onchange = e => { selected = new Set(e.target.checked ? (task?.libraries || []).map(l => l.id) : []); page = 0; render(); };
  $('#chapter-groups').onchange = e => { const id = e.target.dataset.chapterLibrary; if (!id) return; e.target.checked ? selected.add(id) : selected.delete(id); page = 0; render(); };
  $('#chapter-recheck').onchange = render; $('#chapter-prev').onclick = () => { page--; render(); }; $('#chapter-next').onclick = () => { page++; render(); };
  window.addEventListener('helper:navigate', () => { clearTimeout(timer); if (visible()) refresh().catch(() => {}); });
  document.addEventListener('visibilitychange', () => { clearTimeout(timer); if (visible()) refresh().catch(() => {}); });
  window.addEventListener('helper:logout', () => { clearTimeout(timer); revision++; status = task = pending = null; selected.clear(); selectionId = ''; selectionInitialized = false; });
})();
