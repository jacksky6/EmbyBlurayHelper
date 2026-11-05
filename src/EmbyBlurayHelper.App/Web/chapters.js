'use strict';
(() => {
  let status = null, timer, pending, revision = 0, busy = false;
  const visible = () => !document.hidden && !$('#console').hidden && location.hash === '#chapters';
  const ready = () => status?.ready && status?.enabled && status?.automatic;
  const stateNames = { empty:'缺少章节', generated:'等间隔生成', existing:'已有章节，保留', managed:'原生章节已核验', error:'需重试' };
  const batch = BatchPanel({
    prefix:'chapter', mount:'chapter-batch', page:'chapters', api:'chapters/batch', noun:'章节',
    recheck:'重新核验已完成的原盘（仍保留自定义章节）',
    counts:[
      { key:'discs', label:'原盘影片', idle:'仅 BDMV / Blu-ray ISO', value:t => t.summary.discs },
      { key:'verified', label:'已补全', idle:'由工具保存并读回核验', value:t => t.summary.verified },
      { key:'pending', label:'待补全', idle:'章节缺失或等间隔生成', value:t => t.summary.pending },
      { key:'preserved', label:'已有章节', idle:'非等间隔或自定义，保留', value:t => t.summary.preserved }],
    columns:[
      { label:'电影', cell:l => `${l.moviesChecked}${l.scanComplete ? '' : ' · 检查中'}` },
      { label:'原盘', cell:l => l.discs }, { label:'已补全', cell:l => l.verified }, { label:'待补全', cell:l => l.pending }, { label:'已有章节', cell:l => l.preserved }],
    itemColumns:['原盘与章节'], pendingLabel:'待补全', issuesLabel:'失败',
    pending: i => i.pending, issue: i => i.result === 'failed',
    cells: i => [i.sources.map(s => `<small><b>${escapeHtml(s.kind)}</b> · ${s.count} 个章节 · ${escapeHtml(stateNames[s.state] || s.state)}${s.playlist ? ' · ' + escapeHtml(s.playlist) : ''}</small>`).join('')],
    results:{ skipped:'已保留' },
    ready: () => ready(), canScan: () => !!status?.ready,
    note: () => !status?.ready ? '需要 Emby 插件 0.1.5 或更新版本；安装并重启 Emby 后刷新状态。'
      : !ready() ? '检查只读取 Emby 信息；补全前请开启上方「原盘章节补全」。' : '默认只补全缺失或等间隔生成的章节，已有自定义章节保留。'
  });
  // Thumbnail work runs inside Emby; follow it only while it is running.
  function schedule() { clearTimeout(timer); if (visible() && status?.thumbnails?.worker?.state === 'Running' && !busy) timer = setTimeout(() => refresh().catch(() => {}), 2000); }
  function render() {
    $('#chapter-enabled').disabled = busy;
    if (!busy) $('#chapter-enabled').checked = !!status?.enabled;
    $('#chapter-status').dataset.state = busy || !status?.ready || status?.enabled !== status?.automatic ? 'warn' : status?.enabled ? 'on' : 'off';
    $('#chapter-status-title').textContent = busy ? '正在保存' : !status?.ready ? '等待安装或连接' : status?.enabled && status?.automatic ? '已开启 · 新片自动补全' : status?.enabled !== status?.automatic ? '配置状态不一致' : '已关闭';
    $('#chapter-status-copy').textContent = status?.error || (status?.enabled !== status?.automatic ? '工具与 Emby 插件开关不同步，请关闭再打开上方开关。' : status?.automatic ? `已确认插件接入 · ${status.checkedItems} 个原盘媒体源已保存。` : '开启后处理新入库原盘；已有影片请在下方选择媒体库。');
    $('#chapter-save').disabled = busy;
    const thumbnails = status?.thumbnails, worker = thumbnails?.worker;
    $('#chapter-thumbnail-state').textContent = !thumbnails?.available ? '截图接入尚未确认。需要插件 0.1.6 和新版 Helper；更新后刷新状态。' : worker?.state === 'Running' ? `Emby 原盘截图任务运行中 · ${Math.round(worker.progress || 0)}%` : worker?.lastStatus === 'Failed' ? `上次截图任务失败：${worker.error || '请查看 Emby 的 DiscThumbnails 日志，可重跑任务补齐缺失图片。'}` : worker?.lastStatus?.startsWith('Cancel') ? '原盘截图任务已停止 · 已完成图片保留，可重跑任务补齐缺失图片。' : '已接入 Emby 截图设置 · ' + (worker?.lastStatus === 'Completed' ? '上次原盘截图任务已完成。' : '等待 Emby 扫描或提取任务触发。');
    $('#chapter-thumbnail-libraries').innerHTML = (thumbnails?.libraries || []).map(l => `<tr><td>${escapeHtml(l.name)}</td><td>${({never:'从不',task:'作为计划任务',scanandtask:'计划任务及添加媒体时'})[l.mode] || '未知'}</td><td>${l.interval === -1 ? '章节标记' : `${Number(l.interval)} 秒预览`}</td><td>${l.interval !== -1 && l.saveLocal ? '媒体旁' : 'Emby 元数据目录'}</td></tr>`).join('');
    batch.render();
  }
  async function refresh() {
    if (pending) return pending;
    const version = revision;
    const request = (async () => {
      try {
        const result = await api('chapters');
        if (version !== revision) return;
        status = result;
        if (document.activeElement !== $('#chapter-reader-path')) $('#chapter-reader-path').value = status.readerPath || status.suggestedReaderPath || '';
        render();
      } catch (error) { if (version === revision) toast(error.message, true); throw error; }
      finally { if (pending === request) pending = null; schedule(); }
    })();
    pending = request; return pending;
  }
  const refreshAll = () => Promise.all([refresh(), batch.refresh()]);
  async function configure() {
    if (busy) return;
    const enabled = $('#chapter-enabled').checked, readerPath = $('#chapter-reader-path').value.trim(); busy = true; revision++; render();
    try { status = await api('chapters', {enabled, readerPath}); toast(enabled ? '原盘章节已开启。' : '已关闭，保存的章节保留。'); }
    catch (error) { toast(error.message, true); }
    finally { busy = false; render(); schedule(); }
  }
  $('#chapter-enabled').onchange = configure; $('#chapter-save').onclick = configure;
  $('#chapter-refresh').onclick = () => refreshAll().catch(error => toast(error.message, true));
  window.addEventListener('helper:navigate', () => { clearTimeout(timer); if (visible()) refresh().catch(() => {}); });
  document.addEventListener('visibilitychange', () => { clearTimeout(timer); if (visible()) refresh().catch(() => {}); });
  window.addEventListener('helper:logout', () => { clearTimeout(timer); revision++; status = pending = null; });
})();
