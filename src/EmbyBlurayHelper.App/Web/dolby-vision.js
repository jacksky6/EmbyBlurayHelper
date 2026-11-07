'use strict';
(() => {
  let status = null, busy = false, pending = null, dirty = false, revision = 0;
  const visible = () => !$('#console').hidden && location.hash === '#infuse';
  const ids = ['dv-session-cache','dv-global-cache','dv-ffmpeg-path','dv-library-path'];
  function render() {
    const enabled = !!configuration?.enableInfuseDolbyVision;
    if (!busy) $('#enable-dolby-vision').checked = enabled;
    for (const id of ['enable-dolby-vision','dv-save','dv-check', ...ids]) $('#' + id).disabled = busy || settingsSaving;
    const available = !!status?.available;
    setStatus($('#dolby-vision-status'), busy ? 'warn' : !enabled ? 'off' : available ? 'on' : 'warn',
      busy ? '正在保存或检查' : !enabled ? '已关闭' : available ? '已开启 · Infuse P7 按需转换' : '已开启 · 工具尚未就绪',
      busy ? '请稍候，完成后更新状态。' : !enabled ? '仅在开启后处理 Infuse 的 P7 UHD 原盘。其他客户端按原方式播放。'
      : !available ? '请展开「缓存与工具设置」检查 FFmpeg 和 libdovi 路径。'
      : `媒体缓存 ${status.cache?.cachedMiB ?? 0} MiB · ${status.cache?.fragments ?? 0} 个片段 · ${status.cache?.processing ?? 0} 个处理中。仅在进入页面、保存或手动刷新时读取状态。`);
    if (configuration && !busy && !dirty) {
      $('#dv-session-cache').value = String(configuration.dolbyVisionCacheMiB ?? 512);
      $('#dv-global-cache').value = String(configuration.dolbyVisionGlobalCacheMiB ?? 1024);
      $('#dv-ffmpeg-path').value = configuration.dolbyVisionFfmpegPath || '';
      $('#dv-library-path').value = configuration.dolbyVisionLibraryPath || '';
    }
    $('#dv-effective-ffmpeg').textContent = '当前使用：' + (status?.ffmpegPath || '尚未发现，请先配置 Emby 原版 ffprobe 路径。');
    $('#dv-effective-library').textContent = '当前使用：' + (status?.libraryPath || '等待读取');
  }
  async function refresh() {
    if (pending) return pending;
    const version = revision;
    pending = (async () => { const result = await api('infuse/dolby-vision'); if (version === revision) { status = result; render(); } })();
    try { await pending; } finally { pending = null; }
  }
  async function save(patch, message) {
    if (busy) return;
    busy = true; render();
    try { await saveSetting(patch); await refresh(); toast(message); }
    catch (error) { toast(error.message, true); }
    finally { busy = false; render(); }
  }
  $('#enable-dolby-vision').onchange = () => save({ enableInfuseDolbyVision: $('#enable-dolby-vision').checked },
    '杜比视界开关已保存。请在 Infuse 中强制退出再打开并重新同步媒体库。');
  $('#dv-save').onclick = async () => {
    await save({ dolbyVisionCacheMiB: Number($('#dv-session-cache').value), dolbyVisionGlobalCacheMiB: Number($('#dv-global-cache').value),
      dolbyVisionFfmpegPath: $('#dv-ffmpeg-path').value.trim(), dolbyVisionLibraryPath: $('#dv-library-path').value.trim() }, '杜比视界设置已保存。正在播放时修改这些设置会停止当前转换，请重新打开影片。');
    // Keep failed/unsaved values available for correction.
    if (Number($('#dv-session-cache').value) === configuration?.dolbyVisionCacheMiB && Number($('#dv-global-cache').value) === configuration?.dolbyVisionGlobalCacheMiB &&
        $('#dv-ffmpeg-path').value.trim() === configuration?.dolbyVisionFfmpegPath && $('#dv-library-path').value.trim() === configuration?.dolbyVisionLibraryPath) dirty = false;
  };
  $('#dv-check').onclick = async () => {
    if (busy) return; busy = true; render();
    try { const result = await api('infuse/dolby-vision/check', {}); toast('libdovi 可加载；' + result.version); await refresh(); }
    catch (error) { toast(error.message, true); }
    finally { busy = false; render(); }
  };
  ids.forEach(id => $('#' + id).addEventListener('input', () => { dirty = true; }));
  $('#refresh-infuse').addEventListener('click', () => refresh().catch(e => toast(e.message, true)));
  window.addEventListener('helper:navigate', () => { if (visible()) refresh().catch(e => toast(e.message, true)); });
  window.addEventListener('helper:connection-saved', () => { status = null; if (visible()) refresh().catch(() => {}); });
  window.addEventListener('helper:logout', () => { revision++; status = null; dirty = false; });
  if (visible() && configuration) refresh().catch(() => {});
})();
