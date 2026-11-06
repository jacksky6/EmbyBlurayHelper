'use strict';
(() => {
  let view = null, chosen = new Set(), dirty = false, operation = '', errorMessage = '', revision = 0;
  const typeName = type => ({ movies:'电影', tvshows:'电视剧', music:'音乐', musicvideos:'音乐视频', homevideos:'家庭视频', books:'图书', mixed:'混合内容' })[type] || (type || '未指定类型');
  function pluginUpdateUrl() {
    try {
      const url = new URL(configuration.embyUrl);
      if (!['http:','https:'].includes(url.protocol)) return '';
      if (['localhost','127.0.0.1','::1','[::1]','0.0.0.0','[::]'].includes(url.hostname)) url.hostname = location.hostname;
      url.username = url.password = url.search = url.hash = '';
      url.pathname = url.pathname.replace(/\/?$/, '/') + 'web/index.html';
      url.hash = '!/configurationpage?name=EmbyBlurayHelperUpdate';
      return url.href;
    } catch { return ''; }
  }
  function render() {
    const busy = !!operation, save = $('#scope-save');
    save.disabled = busy || !view?.pluginReady || (!dirty && view.synchronized);
    save.textContent = operation === 'save' ? '正在保存…' : operation === 'read' ? '正在读取…' : view && !view.pluginReady ? '需要更新插件' : view && !dirty && view.synchronized ? '已保存' : '保存处理范围';
    save.setAttribute('aria-busy', String(operation === 'save'));
    $('#scope-refresh').setAttribute('aria-busy', String(operation === 'read'));
    $('#library-scope-panel').setAttribute('aria-busy', String(busy));
    $('#scope-refresh').disabled = busy;
    for (const id of ['scope-select-all','scope-select-none']) $('#' + id).disabled = busy || !view;
    $('#scope-dirty').hidden = !dirty;
    $('#scope-upgrade').hidden = !view || view.pluginReady;
    const updateUrl = pluginUpdateUrl(), updateLink = $('#scope-plugin-update');
    updateLink.hidden = !view || view.pluginReady || !updateUrl;
    if (updateUrl) updateLink.href = updateUrl; else updateLink.removeAttribute('href');
    $('#scope-save-state').textContent = errorMessage || (operation === 'save' ? '正在同步到 Emby 并确认保存结果，请稍候。' : operation === 'read' ? '正在读取 Emby 媒体库与插件状态…' : !view ? '请先读取媒体库和插件状态。' : !view.pluginReady ? '尚未保存。请先更新插件、重启 Emby，再重新读取。' : dirty ? '更改尚未生效，保存后应用到检查、批量和自动补全。' : !view.synchronized ? '当前范围尚未同步，请点击「保存处理范围」。' : '当前处理范围已保存并与 Emby 同步。');
    $('#scope-save-state').classList.toggle('scope-error', !!errorMessage);
    if (!view) return;
    const allowed = view.libraries.filter(l => chosen.has(l.id)).length;
    $('#scope-status').textContent = `${view.libraries.length} 个媒体库 · 允许 ${allowed} 个 · 排除 ${view.libraries.length - allowed} 个`;
    $('#scope-message').textContent = view.message + (view.pluginReady && !view.synchronized ? ' 当前尚未同步到 Emby；保存处理范围后生效。' : '');
    $('#scope-libraries').innerHTML = view.libraries.map(l => `<label class="scope-library ${chosen.has(l.id) ? '' : 'excluded'}"><input type="checkbox" data-scope-library="${escapeHtml(l.id)}" ${chosen.has(l.id) ? 'checked' : ''} ${busy ? 'disabled' : ''}><span><strong>${escapeHtml(l.name)}</strong><small>${escapeHtml(typeName(l.collectionType))} · ID ${escapeHtml(l.id)}</small></span><span class="tag ${chosen.has(l.id) ? '' : 'off'}">${chosen.has(l.id) ? '允许处理' : '已排除'}</span></label>`).join('') || '<p class="field-help">Emby 尚未返回媒体库。</p>';
  }
  function summary(error) {
    $('#lib-scope-summary').textContent = error ? '全局处理范围读取失败，请检查全局设置。' : view
      ? `全局范围：允许 ${view.allowedCount} / ${view.libraries.length} 个媒体库${view.excludedCount ? ` · 已排除 ${view.excludedCount} 个` : ''} · 新增库默认允许`
      : '全局处理范围：新增媒体库默认允许';
  }
  async function refresh(reset = false) {
    const rev = ++revision;
    operation = 'read'; errorMessage = ''; render();
    $('#scope-message').textContent = '正在读取 Emby 媒体库与插件状态…';
    try {
      const result = await api('library-scope');
      if (rev !== revision) return;
      if (reset || !dirty || view?.serverId !== result.serverId) { chosen = new Set(result.libraries.filter(l => l.allowed).map(l => l.id)); dirty = false; }
      view = result; summary();
    } catch (error) {
      if (rev !== revision) return;
      view = null; chosen.clear(); dirty = false;
      $('#scope-status').textContent = '未读取媒体库'; $('#scope-libraries').replaceChildren();
      errorMessage = error.message;
      $('#scope-message').textContent = error.message; summary(error);
    } finally { if (rev === revision) { operation = ''; render(); } }
  }
  function changed() {
    errorMessage = ''; dirty = view.libraries.some(l => l.allowed !== chosen.has(l.id)); render();
  }
  $('#scope-libraries').addEventListener('change', event => {
    const id = event.target.dataset.scopeLibrary; if (!id || !view || operation) return;
    event.target.checked ? chosen.add(id) : chosen.delete(id); changed();
  });
  $('#scope-select-all').onclick = () => { if (view && !operation) { chosen = new Set(view.libraries.map(l => l.id)); changed(); } };
  $('#scope-select-none').onclick = () => { if (view && !operation) { chosen.clear(); changed(); } };
  $('#scope-refresh').onclick = () => refresh(true);
  $('#scope-save').onclick = async () => {
    if (!view?.pluginReady || operation || (!dirty && view.synchronized)) return;
    const rev = ++revision;
    operation = 'save'; errorMessage = ''; render();
    try {
      const saved = await api('library-scope', { serverId:view.serverId, excludedLibraryIds:view.libraries.filter(l => !chosen.has(l.id)).map(l => l.id) });
      if (rev !== revision) return;
      view = saved; chosen = new Set(saved.libraries.filter(l => l.allowed).map(l => l.id)); dirty = false;
      summary(); window.dispatchEvent(new Event('helper:scope-saved'));
      toast('处理范围已保存，检查、批量任务和新片自动补全共用此范围。请重新检查媒体库。');
    } catch (error) { if (rev === revision) { errorMessage = '保存失败：' + error.message; toast(errorMessage, true); } }
    finally { if (rev === revision) { operation = ''; render(); } }
  };
  $('#lib-change-scope').onclick = () => { navigate('connection'); $('#library-scope-panel').scrollIntoView({ block:'start', behavior:'smooth' }); };
  $('#refresh-connection').addEventListener('click', () => refresh());
  $('#lib-refresh').addEventListener('click', () => refresh());
  window.addEventListener('helper:navigate', () => {
    if (configuration && !$('#console').hidden && ['#connection','#library'].includes(location.hash)) refresh();
  });
  window.addEventListener('helper:connection-saved', () => { view = null; dirty = false; refresh(true); });
  window.addEventListener('helper:logout', () => { revision++; operation = errorMessage = ''; dirty = false; view = null; chosen.clear(); $('#scope-libraries').replaceChildren(); render(); summary(); });
})();
