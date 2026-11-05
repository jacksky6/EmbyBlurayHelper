'use strict';
const $ = s => document.querySelector(s);
const $$ = s => [...document.querySelectorAll(s)];
let configuration = null, logEntries = [], logLevel = 'all', pollTimer, toastTimer;
let probeStatus = null, probeLoading = false, probeRevision = 0, probeCheckedAt = 0, probeRequest = null;
let probeChanging = false, settingsSaving = false;
let logRevision = 0, logsClearing = false;
let multiClipStatus = null, multiClipChanging = false, multiClipRevision = 0;
let isoStatus = null, isoLoading = false, isoRequest = null, isoRevision = 0;
const labels = { overview:'概览', language:'语言与时长', 'media-features':'音视频规格', chapters:'章节与截图', infuse:'播放增强', diagnostics:'单片检查', logs:'运行日志', connection:'连接与账号' };
const groups = { overview:'工作空间', language:'Emby 媒体库', 'media-features':'Emby 媒体库', chapters:'Emby 媒体库', infuse:'Infuse 播放', diagnostics:'工具', logs:'工具', connection:'设置' };
let mediaFeatureBusy = false, mediaFeatureReady = false;
// Overview-only snapshots; each page keeps its own live state.
let overview = {}, overviewRequest = null, infuseChapters = null;
const escapeHtml = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
function toast(message, failed = false) { const el = $('#toast'); el.textContent = message; el.classList.toggle('failed', failed); el.hidden = false; clearTimeout(toastTimer); toastTimer = setTimeout(() => el.hidden = true, 5000); }
async function api(path, body) {
  const response = await fetch('/api/' + path, { method: body === undefined ? 'GET' : 'POST', headers: { 'Content-Type':'application/json', 'X-Helper-Request':'1' }, ...(body === undefined ? {} : { body: JSON.stringify(body) }) });
  if (response.status === 401) { showLogin(); throw new Error(path === 'login' ? '用户名或密码不正确。' : '请先登录管理界面。'); }
  if (response.status === 429) throw new Error('尝试次数过多，请稍后再试。');
  const result = response.headers.get('content-type')?.includes('json') ? await response.json() : {};
  if (!response.ok) throw new Error(result.error || '请求未完成，请查看运行日志。');
  return result;
}
function showLogin() { clearInterval(pollTimer); isoRevision++; isoStatus = null; isoRequest = null; isoLoading = false; probeRevision++; probeStatus = null; probeRequest = null; probeCheckedAt = 0; mediaFeatureReady = false; $('#account-password').value = ''; $('#console').hidden = true; $('#login').hidden = false; window.dispatchEvent(new Event('helper:logout')); }
function navigate(page) {
  if (page === 'features') page = 'language';
  if (!labels[page]) page = 'overview';
  $$('.page').forEach(el => el.hidden = el.id !== 'page-' + page);
  $$('.nav-item').forEach(el => el.classList.toggle('active', el.dataset.page === page));
  $('#page-breadcrumb').textContent = labels[page];
  $('#page-group').textContent = groups[page];
  document.title = labels[page] + ' · EmbyBlurayHelper';
  if (location.hash !== '#' + page) history.replaceState(null, '', '#' + page);
  if (configuration && !$('#console').hidden && ['overview','language'].includes(page)) refreshProbeStatus().catch(() => {});
  if (configuration && !$('#console').hidden && ['overview','infuse'].includes(page)) { refreshMultiClipStatus().catch(() => {}); refreshInfuseChapters().catch(() => {}); }
  if (configuration && !$('#console').hidden && page === 'overview') loadOverview().catch(() => {});
  if (configuration && !$('#console').hidden && page === 'language') refreshIsoStatus().catch(() => {});
  if (configuration && !$('#console').hidden && page === 'media-features') refreshMediaFeatures().catch(error => toast(error.message,true));
  window.dispatchEvent(new Event('helper:navigate'));
}
function fillSettings(result) {
  configuration = result.settings;
  $('#emby-url').value = configuration.embyUrl;
  $('#proxy-url').value = configuration.proxyUrl;
  $('#management-url').value = configuration.managementUrl;
  $('#probe-path').value = configuration.upstreamProbePath;
  $('#enable-probe').checked = configuration.enableProbeLanguagePatch;
  $('#enable-infuse').checked = configuration.enableInfuseLanguagePatch;
  $('#enable-multiclip').checked = !!configuration.enableInfuseMultiClipPlayback;
  $('#enable-media-features').checked = !!configuration.enableMediaFeatures;
  $('#feature-probe-path').value = configuration.featureProbePath || '';
  $('#api-key').value = configuration.embyApiKey;
  $('#key-help').textContent = result.hasApiKey ? '已显示保存的 API key，可直接修改；清空后保存可移除。' : '可在 Emby 控制台 → 高级 → API 密钥中创建。';
  $('#executable-path').textContent = result.executablePath;
  $('#data-path').textContent = result.dataDirectory;
  $('#restart-banner').hidden = !result.restartRequired;
  featureState();
}
function featureState() {
  renderToggles();
  renderProbeStatus();
  renderMultiClipStatus();
}
function setStatus(box, state, title, copy) {
  box.dataset.state = state;
  box.querySelector('strong').textContent = title;
  if (copy !== undefined) box.querySelector('p').textContent = copy;
}
function tag(el, text, on) { el.textContent = text; el.classList.toggle('off', !on); }
// Switches that are plain settings: show the saved value, never unsaved input.
function renderToggles() {
  if (!configuration) return;
  const language = !!configuration.enableInfuseLanguagePatch, chapters = !!configuration.enableInfuseChapters;
  // While a save is in flight, keep what the user just clicked.
  if (!settingsSaving) { $('#enable-infuse').checked = language; $('#enable-infuse-chapters').checked = chapters; }
  for (const toggle of [$('#enable-infuse'),$('#enable-infuse-chapters')]) toggle.disabled = settingsSaving;
  setStatus($('#infuse-language-status'), language ? 'on' : 'off', language ? '已启用' : '已关闭',
    language ? 'Infuse 静态播放 UHD 原盘时，按原盘语言补写缺失的音轨和字幕标签。' : '开启后，Infuse 播放 UHD 原盘时按原盘语言补写音轨和字幕标签。');
  tag($('#infuse-state'), language ? '已启用' : '已关闭', language);
  const unavailable = infuseChapters?.unavailable || [];
  setStatus($('#infuse-chapters-status'), chapters ? 'on' : 'off', chapters ? '已启用' : '已关闭',
    !chapters ? '默认关闭。开启后，带 Emby 章节的 BDMV 在 Infuse 中以虚拟 ISO 呈现。'
    : !infuseChapters ? '正在读取 Infuse 已同步的原盘记录。'
    : `${infuseChapters.available} 部原盘以虚拟 ISO 提供章节${unavailable.length ? ` · ${unavailable.length} 部保留原播放方式` : ''}。数字来自 Infuse 已同步过的媒体库。`);
  $('#infuse-chapters-state').classList.toggle('off', !chapters);
  tag($('#infuse-chapters-tag'), chapters ? '已启用' : '已关闭', chapters);
  $('#chapter-infuse-link').textContent = chapters ? '当前已启用' : '当前已关闭';
  $('#infuse-proxy-address').textContent = proxyAddress();
  $('#infuse-chapters-unavailable').hidden = !chapters || !unavailable.length;
  $('#infuse-chapters-unavailable-count').textContent = unavailable.length ? unavailable.length + ' 部' : '';
  $('#infuse-chapters-unavailable-rows').innerHTML = unavailable.map(u => `<tr><td><strong>${escapeHtml(u.name)}</strong><small>ID ${escapeHtml(u.itemId)}</small></td><td>${escapeHtml(u.reason)}</td></tr>`).join('');
  if (!$('#page-overview').hidden) renderOverview();
}
async function refreshInfuseChapters() { infuseChapters = await api('infuse/chapters'); renderToggles(); }
// Save one card's fields over the latest saved settings, so unsaved input
// elsewhere on the page is neither saved nor overwritten.
async function saveSetting(patch) {
  settingsSaving = true; renderToggles(); renderProbeStatus(); renderMultiClipStatus();
  try {
    const latest = await api('settings');
    await api('settings', { ...latest.settings, ...patch });
    const saved = await api('settings');
    configuration = saved.settings;
    $('#restart-banner').hidden = !saved.restartRequired;
    return saved;
  } finally { settingsSaving = false; featureState(); }
}
function showSaved(fields) {
  const map = { 'emby-url':'embyUrl', 'api-key':'embyApiKey', 'probe-path':'upstreamProbePath', 'proxy-url':'proxyUrl', 'management-url':'managementUrl' };
  for (const id of fields) $('#' + id).value = configuration[map[id]];
}
async function saveServer() {
  await saveSetting({ embyUrl:$('#emby-url').value.trim().replace(/\/$/,''), embyApiKey:$('#api-key').value.trim(), upstreamProbePath:$('#probe-path').value.trim() });
  showSaved(['emby-url','api-key','probe-path']);
  probeRevision++; probeStatus = null; isoRevision++; isoStatus = null; isoRequest = null;
  await Promise.all([refreshProbeStatus(true),refreshMultiClipStatus(),refreshIsoStatus()]);
}
async function toggleSetting(input, key, onText, offText) {
  const enabled = input.checked;
  try { await saveSetting({ [key]: enabled }); toast(enabled ? onText : offText); }
  catch (error) { input.checked = !enabled; toast(error.message, true); }
}
function renderMultiClipStatus() {
  const enabled = multiClipStatus?.enabled ?? !!configuration?.enableInfuseMultiClipPlayback;
  const state = multiClipChanging ? 'changing' : !enabled ? 'off' : multiClipStatus?.readerAvailable ? 'on' : 'pending';
  const title = ({changing:'正在保存',off:'已关闭',on:'已开启 · 按需处理',pending:'已开启 · 等待配置'})[state];
  $('#multiclip-state').textContent = title; $('#multiclip-state').classList.toggle('off',state !== 'on');
  setStatus($('#multiclip-status'), ({changing:'warn',off:'off',on:'on',pending:'warn'})[state], title,
    multiClipChanging ? '正在保存开关，稍后生效。' : multiClipStatus?.message || '默认关闭，可与语言补写分别开启或关闭。');
  const toggle = $('#enable-multiclip'); if (!multiClipChanging) toggle.checked = enabled;
  toggle.disabled = multiClipChanging || settingsSaving || probeChanging;
}
async function refreshMultiClipStatus() {
  if (multiClipChanging) return;
  const revision = ++multiClipRevision, result = await api('infuse/multiclip');
  if (revision !== multiClipRevision || multiClipChanging) return;
  multiClipStatus = result;
  configuration.enableInfuseMultiClipPlayback = multiClipStatus.enabled;
  renderMultiClipStatus();
}
async function setMultiClipEnabled(enabled) {
  if (multiClipChanging || settingsSaving || probeChanging) return;
  multiClipChanging = true; multiClipRevision++; renderMultiClipStatus(); renderProbeStatus();
  try {
    multiClipStatus = await api('infuse/multiclip',{enabled});
    configuration.enableInfuseMultiClipPlayback = multiClipStatus.enabled;
    toast(multiClipStatus.message);
  } catch(error) {
    toast(error.message,true);
    try { multiClipStatus = await api('infuse/multiclip'); configuration.enableInfuseMultiClipPlayback = multiClipStatus.enabled; } catch {}
  } finally { multiClipChanging = false; renderMultiClipStatus(); renderProbeStatus(); }
}
function renderProbeStatus() {
  const state = probeChanging ? 'changing' : probeLoading ? 'checking' : probeStatus?.state || 'unknown';
  const enabled = probeStatus?.languagePatchEnabled ?? configuration?.enableProbeLanguagePatch;
  const title = ({changing:'正在应用开关设置',checking:'正在检查',connected:'已接入 Emby',disconnected:'未接入 Emby',other:'正在使用其他探测器',unknown:'无法确认接入状态'})[state];
  const copy = ({changing:'正在切换探测器并核对 Emby 配置，请稍候。',checking:'正在读取 Emby 的实际探测器配置。',
    connected:enabled ? '语言补全已启用。关闭上方开关会恢复接入前的探测器配置。' : '旧配置仍接入本工具，但语言补全已暂停。可打开开关启用，或恢复原探测器。',
    disconnected:'当前使用原探测器配置。打开上方开关会自动备份配置、接入本工具并启用语言补全。',
    other:'Emby 当前使用其他自定义探测器。打开开关时会提示确认，并在切换前备份配置。',
    unknown:'尚未确认 Emby 的实际探测器配置，请检查连接后刷新。'})[state];
  $('#probe-integration').dataset.state = state;
  $('#probe-integration-title').textContent = title;
  $('#probe-integration-copy').textContent = state === 'unknown' && probeStatus?.message ? probeStatus.message : copy;
  $('#probe-status-dot').className = 'status-dot' + (state === 'connected' ? (enabled ? '' : ' paused') : ' neutral');
  const patch = $('#probe-patch-state'); patch.hidden = state !== 'connected'; patch.textContent = enabled ? '语言补全已启用' : '语言补全已暂停'; patch.classList.toggle('paused', !enabled);
  const badge = $('#probe-state'); badge.textContent = state === 'connected' ? (enabled ? '已接入 · 补全已启用' : '已接入 · 补全已暂停') : title;
  badge.classList.toggle('off', state !== 'connected' || !enabled);
  const path = $('#current-probe-path'); path.hidden = !['connected','other'].includes(state) || !probeStatus?.currentProbePath; path.textContent = probeStatus?.currentProbePath || '';
  const warning = $('#probe-status-warning'); warning.hidden = !probeStatus?.message || ['unknown','checking','changing'].includes(state); warning.textContent = probeStatus?.message || '';
  $('#probe-checked-at').textContent = probeLoading ? '正在核对实时配置…' : probeCheckedAt ? '上次检查 ' + new Date(probeCheckedAt).toLocaleTimeString('zh-CN',{hour12:false}) : '尚未检查';
  const recover = $('#recover-probe');
  recover.hidden = state !== 'connected' || enabled || !probeStatus?.canRestore;
  $('#probe-connection').hidden = state !== 'unknown';
  for (const button of [recover,$('#refresh-probe')]) button.disabled = probeLoading || probeChanging || settingsSaving || button.dataset.busy === '1';
  const toggle = $('#enable-probe');
  if (!probeChanging) toggle.checked = probeStatus?.state === 'connected' && !!enabled;
  toggle.disabled = probeLoading || probeChanging || settingsSaving || !probeStatus || probeStatus.state === 'unknown' ||
    (probeStatus.state === 'connected' && enabled ? !probeStatus.canRestore : probeStatus.state !== 'connected' && !probeStatus.canApply);
  for (const button of [$('#test-emby'),$('#check-probe'),$('#discover-probe'),$('#save-addresses')]) button.disabled = probeChanging || settingsSaving || multiClipChanging || button.dataset.busy === '1';
  $('#enable-probe').disabled ||= multiClipChanging;
  $('#enable-multiclip').disabled = multiClipChanging || settingsSaving || probeChanging;
  renderLibraryTask();
  if (!$('#page-overview').hidden) renderOverview();
}
async function refreshProbeStatus(force = false) {
  if (!force && probeRequest) return probeRequest;
  const revision = ++probeRevision;
  probeLoading = true; renderProbeStatus();
  const request = (async () => {
    try { const result = await api('probe/status'); if (revision === probeRevision) { probeStatus = result; if (configuration && result.state !== 'unknown') configuration.enableProbeLanguagePatch = result.languagePatchEnabled; } }
    catch (error) { if (revision === probeRevision) probeStatus = {state:'unknown',message:error.message}; }
    finally { if (revision === probeRevision) { probeLoading = false; probeRequest = null; probeCheckedAt = Date.now(); renderProbeStatus(); $('#connection-pill').innerHTML = probeStatus?.state === 'unknown' ? '<span class="status-dot neutral"></span>连接需检查' : '<span class="status-dot"></span>Emby 已连接'; } }
  })();
  probeRequest = request;
  return request;
}
async function setProbeEnabled(enabled) {
  if (probeChanging) return;
  probeChanging = true; renderProbeStatus();
  try {
    if (enabled && probeStatus?.state === 'other' && !await confirm('替换当前自定义探测器？','开启语言补全会将 Emby 探测器切换为本工具，并保存原探测器配置。关闭开关时恢复接入前的配置。')) return;
    const result = await api('probe/toggle',{enabled});
    configuration.enableProbeLanguagePatch = result.enabled;
    toast(result.message);
  } catch (error) { toast(error.message,true); }
  finally { await refreshProbeStatus(true); if (probeStatus && probeStatus.state !== 'unknown') configuration.enableProbeLanguagePatch = probeStatus.languagePatchEnabled; probeChanging = false; renderProbeStatus(); }
}
function size(bytes) { if (bytes < 1024) return [bytes,'B']; if (bytes < 1048576) return [(bytes/1024).toFixed(1),'KB']; if (bytes < 1073741824) return [(bytes/1048576).toFixed(1),'MB']; return [(bytes/1073741824).toFixed(2),'GB']; }
function renderLogs() {
  const rows = [...logEntries].reverse().filter(e => logLevel === 'all' || e.level === logLevel);
  $('#log-rows').innerHTML = rows.map(e => `<tr><td>${new Date(e.time).toLocaleTimeString('zh-CN',{hour12:false})}</td><td><span class="level ${escapeHtml(e.level)}">${({Information:'信息',Warning:'警告',Error:'错误'})[e.level] || escapeHtml(e.level)}</span></td><td>${escapeHtml(e.source)}</td><td>${escapeHtml(e.message)}</td></tr>`).join('');
  $('#logs-empty').hidden = rows.length !== 0;
  $('#log-count').textContent = rows.length + ' 条记录';
  // Overview shows outcomes, not per-request proxy traces or host startup lines.
  const meaningful = e => e.level !== 'Information' || ['LibraryLanguage','MediaFeatureBatch','MediaFeatures','NativeChapters','Probe','MultiClip','Settings','Account'].includes(e.source);
  $('#recent-activity').innerHTML = [...logEntries].reverse().filter(meaningful).slice(0,5).map(e => `<div class="activity-row"><span class="event-mark"><svg><use href="#${e.level === 'Information' ? 'check' : 'activity'}"/></svg></span><span class="event-message" title="${escapeHtml(e.message)}">${escapeHtml(e.message)}</span><time>${new Date(e.time).toLocaleTimeString('zh-CN',{hour12:false})}</time></div>`).join('') || '<p class="empty">暂无活动</p>';
}
async function refresh() {
  const revision = logRevision;
  const [status,logs] = await Promise.all([api('status'),api('logs')]);
  $('#stat-requests').textContent = status.requests.toLocaleString(); $('#stat-active').textContent = status.active;
  $('#stat-patched').textContent = status.patchedTables.toLocaleString();
  const [number,unit] = size(status.patchedBytes); $('#stat-bytes').textContent = `${number} ${unit}`;
  $('#stat-memory').textContent = `${status.memoryMiB} MB`;
  const minutes = Math.max(0,Math.floor((Date.now() - new Date(status.started))/60000));
  $('#stat-uptime').textContent = minutes < 60 ? `已运行 ${minutes} 分钟` : `已运行 ${Math.floor(minutes/60)} 小时 ${minutes%60} 分钟`;
  $('#platform').textContent = status.os + ' · ' + status.architecture;
  if (!logsClearing && revision === logRevision) { logEntries = logs; renderLogs(); }
}
// Read-only snapshots from each page's last check; nothing here scans media.
async function loadOverview() {
  if (overviewRequest) return overviewRequest;
  const get = path => api(path).catch(error => ({ error: error.message }));
  overviewRequest = (async () => {
    try {
      const [language, features, featureBatch, chapters, chapterBatch, iso] = await Promise.all(['library-language','media-features','media-features/batch','chapters','chapters/batch','iso/status'].map(get));
      overview = { loaded:true, language, features, featureBatch, chapters, chapterBatch, iso };
      renderOverview();
    } finally { overviewRequest = null; }
  })();
  return overviewRequest;
}
function proxyAddress() {
  let port = '8097'; try { port = new URL(configuration.proxyUrl).port || '80'; } catch {}
  const host = ['localhost','127.0.0.1','::1','[::1]'].includes(location.hostname) ? '本机 IP' : location.hostname;
  return `http://${host}:${port}`;
}
function renderOverview() {
  if (!configuration) return;
  const { language, features, featureBatch, chapters, chapterBatch, iso } = overview;
  const probe = probeStatus?.state, plugin = iso?.pluginState;
  const needsSetup = !configuration.embyApiKey || probe === 'unknown';
  $('#setup-notice').hidden = !needsSetup;
  const hero = $('#hero-action'); hero.dataset.goto = needsSetup ? 'connection' : 'language'; hero.firstChild.textContent = needsSetup ? '连接 Emby ' : '查看媒体库 ';
  const dot = (id, state) => $('#' + id).className = 'status-dot' + (state === 'on' ? '' : state === 'warn' ? ' failed' : ' neutral');
  const emby = !configuration.embyApiKey ? '未配置' : !probe ? '读取中' : probe === 'unknown' ? '需要检查' : '已连接';
  $('#int-emby').textContent = emby; dot('int-emby-dot', emby === '已连接' ? 'on' : emby === '读取中' ? 'off' : 'warn');
  $('#int-probe').textContent = ({connected: probeStatus?.languagePatchEnabled ? '已接入 · 语言补全已启用' : '已接入 · 语言补全已暂停', disconnected:'未接入 · 使用原探测器', other:'其他自定义探测器', unknown:'无法确认'})[probe] || '读取中';
  dot('int-probe-dot', probe === 'connected' ? 'on' : ['unknown','other'].includes(probe) ? 'warn' : 'off');
  $('#int-plugin').textContent = (!iso ? '读取中' : ({ready:'已加载','not-loaded':'未加载',outdated:'版本过旧'})[plugin] || '无法确认') + (iso?.pluginVersion ? ' · ' + iso.pluginVersion : '');
  dot('int-plugin-dot', plugin === 'ready' ? 'on' : !iso ? 'off' : 'warn');
  $('#int-proxy').textContent = proxyAddress(); dot('int-proxy-dot','on');
  const s = language?.summary, checked = !!s && (!!language.checkedAt || language.moviesChecked > 0);
  const pendingLanguage = checked ? s.missingMovies + s.unknownMovies : 0;
  $('#ov-discs').textContent = checked ? s.blurayMovies : '—';
  $('#ov-discs-foot').textContent = checked ? `${language.moviesChecked} 部电影 · ${s.uhdMovies} 部 UHD` : '尚未检查媒体库';
  $('#ov-language').textContent = checked ? `${s.completeMovies} / ${s.blurayMovies}` : '—';
  $('#ov-language-foot').textContent = checked ? (pendingLanguage ? `${pendingLanguage} 部待补全` : '全部完整') : '在「语言与时长」检查媒体库';
  const c = chapterBatch?.id ? chapterBatch.summary : null, f = featureBatch?.id ? featureBatch.summary : null;
  $('#ov-chapters').textContent = c ? `${c.verified + c.preserved} / ${c.discs}` : '—';
  $('#ov-chapters-foot').textContent = c ? (c.pending ? `${c.pending} 部待补全` : '全部已有章节') : '在「章节与截图」检查媒体库';
  $('#ov-features').textContent = f ? `${f.verified} / ${f.discs}` : '—';
  $('#ov-features-foot').textContent = f ? (f.pending ? `${f.pending} 部待核验` : '全部已核验') : '在「音视频规格」检查媒体库';
  const featureOn = !!features && !features.error && features.enabled && features.automatic === true && features.probeAccessible && features.probeConnected;
  tag($('#features-state'), !features || features.error ? '无法确认' : featureOn ? '已启用 · 自动识别' : features.enabled ? '需要检查' : '已关闭', featureOn);
  const chapterOn = !!chapters && !chapters.error && chapters.ready && chapters.enabled && chapters.automatic;
  tag($('#chapters-state'), !chapters || chapters.error ? '无法确认' : chapterOn ? '已启用 · 自动补全' : !chapters.ready ? '等待插件' : chapters.enabled !== chapters.automatic ? '需要检查' : '已关闭', chapterOn);
  const thumbs = chapters?.thumbnails;
  tag($('#thumbnails-state'), !thumbs ? '无法确认' : !thumbs.available ? '等待插件 0.1.6' : thumbs.worker?.state === 'Running' ? '截图任务运行中' : '跟随 Emby 设置', !!thumbs?.available);
  const items = [], add = (level, text, page) => items.push({ level, text, page });
  if (!configuration.embyApiKey) add('warn','尚未保存 Emby API key，大部分功能无法使用。','connection');
  else if (probe === 'unknown') add('warn', probeStatus?.message || '无法读取 Emby 状态，请检查连接。','connection');
  if (configuration.enableMediaFeatures && probe && probe !== 'connected' && probe !== 'unknown') add('warn','「音视频规格」识别已开启，但 Emby 探测器未接入，新片不会自动识别。','language');
  if (['not-loaded','outdated'].includes(plugin)) add('warn',`Emby 插件${plugin === 'outdated' ? '版本过旧' : '未加载'}：ISO 补全、原盘章节和截图不可用。安装随包 DLL 并重启 Emby。`,'chapters');
  if (chapters && !chapters.error && chapters.ready && chapters.enabled !== chapters.automatic) add('warn','原盘章节开关与 Emby 插件不同步，请在「章节与截图」重新开关一次。','chapters');
  for (const [task, name, page] of [[language,'语言与时长','language'],[chapterBatch,'章节','chapters'],[featureBatch,'规格','media-features']])
    if (['failed','interrupted'].includes(task?.status)) add('warn',`上次${name}任务未完成，可在对应页面继续。`, page);
  if (pendingLanguage) add('info',`${pendingLanguage} 部原盘的语言或时长待补全。`,'language');
  if (c?.pending) add('info',`${c.pending} 部原盘的章节待补全。`,'chapters');
  if (f?.pending) add('info',`${f.pending} 部原盘的规格待核验。`,'media-features');
  const unavailable = infuseChapters?.unavailable || [];
  if (configuration.enableInfuseChapters && unavailable.length) add('info',`${unavailable.length} 部原盘在 Infuse 中保留原播放方式（${unavailable.slice(0,2).map(u => u.name).join('、')}${unavailable.length > 2 ? ' 等' : ''}）。`,'infuse');
  $('#attention-list').innerHTML = !overview.loaded ? '<p class="empty">正在读取状态</p>'
    : items.map(i => `<div class="attention-row ${i.level}"><span class="event-mark"><svg><use href="#${i.level === 'warn' ? 'alert' : 'activity'}"/></svg></span><span>${escapeHtml(i.text)}</span><button class="text-button accent" data-attention="${i.page}">${escapeHtml(labels[i.page])} →</button></div>`).join('')
      || '<p class="empty">暂无需要处理的事项</p>';
}
async function testEmby() {
  try { const result = await api('emby/test',{});
    $('#connection-pill').innerHTML = '<span class="status-dot"></span>Emby 已连接';
    const text = `${result.name || 'Emby Server'} · ${result.version || '未知版本'}\n` + (result.apiAuthenticated ? `API 权限验证成功\n当前自定义探测器：${result.probeEnabled ? result.probePath || '已启用' : '未启用'}` : '服务器可达；保存 API key 后可配置探测器。');
    $('#connection-result').hidden = false; $('#connection-result').classList.remove('failed'); $('#connection-result').textContent = text;
    return result;
  } catch (error) { $('#connection-pill').innerHTML = '<span class="status-dot failed"></span>连接需检查'; $('#connection-result').hidden=false; $('#connection-result').classList.add('failed'); $('#connection-result').textContent=error.message; throw error; }
}
async function startConsole() {
  const [settings,account] = await Promise.all([api('settings'),api('account')]);
  fillSettings(settings); $('#account-username').value = account.username; $('#username').value = account.username;
  $('#login').hidden = true; $('#console').hidden = false;
  navigate(location.hash.slice(1)); await refresh(); clearInterval(pollTimer);
  pollTimer = setInterval(() => { if (!document.hidden) refresh().catch(() => {}); },5000);
  if (!['overview','language','infuse'].includes(location.hash.slice(1))) refreshProbeStatus().catch(() => {});
}
function action(selector, handler) { $(selector).addEventListener('click', async event => { const button = event.currentTarget; button.dataset.busy = '1'; button.disabled = true; try { await handler(); } catch(error) { toast(error.message,true); } finally { delete button.dataset.busy; button.disabled = false; if (configuration) renderProbeStatus(); } }); }
function confirm(title,copy,confirmText = '确认应用') { return new Promise(resolve => { const dialog = $('#confirm-dialog'); $('#dialog-title').textContent = title; $('#dialog-copy').textContent=copy; $('#dialog-confirm').textContent=confirmText; $('#dialog-confirm').classList.toggle('danger',confirmText === '清除全部'); dialog.showModal(); const done = value => { dialog.close(); resolve(value); }; $('#dialog-confirm').onclick=()=>done(true); $('#dialog-cancel').onclick=()=>done(false); dialog.oncancel=()=>done(false); }); }
$('#login-form').addEventListener('submit',async event => { event.preventDefault(); const button = $('#login-form button'); button.disabled=true; $('#login-error').textContent=''; try { await api('login',{username:$('#username').value,password:$('#password').value}); $('#password').value=''; await startConsole(); } catch(error) { $('#login-error').textContent=error.message; } finally { button.disabled=false; } });
$('#account-form').addEventListener('submit',async event => { event.preventDefault(); const button=$('#save-account'); button.disabled=true; try { const username=$('#account-username').value.trim(); const result=await api('account',{username,password:$('#account-password').value}); $('#username').value=username; $('#password').value=''; $('#login-error').textContent=''; showLogin(); $('#password').focus(); toast(result.message); } catch(error) { toast(error.message,true); } finally { button.disabled=false; } });
$$('[data-page]').forEach(el => el.onclick = () => navigate(el.dataset.page));
$$('[data-goto]').forEach(el => el.onclick = () => navigate(el.dataset.goto));
window.addEventListener('hashchange',()=>navigate(location.hash.slice(1)));
$$('[data-level]').forEach(el => el.onclick = () => { logLevel=el.dataset.level; $$('[data-level]').forEach(b=>b.classList.toggle('selected',b===el)); renderLogs(); });
action('#logout',async()=>{await api('logout',{});showLogin();});
action('#clear-logs',async()=>{if(!await confirm('清除全部运行日志？','将删除本工具保存的全部运行日志，包括历史日志和当前页面中的记录，清除后无法恢复。配置和运行统计保持不变。','清除全部'))return;logsClearing=true;logRevision++;try{const result=await api('logs/clear',{});logEntries=[];renderLogs();toast(result.message);}finally{logsClearing=false;logRevision++;}});
action('#refresh-status',async()=>{await Promise.all([refresh(),refreshProbeStatus(true),refreshMultiClipStatus(),refreshInfuseChapters()]);await loadOverview(true);await testEmby();toast('状态已更新。');});
action('#refresh-connection',async()=>{await refreshProbeStatus(true);await testEmby();toast('连接状态已更新。');});
action('#refresh-language',async()=>{isoRevision++;isoStatus=null;isoRequest=null;await Promise.all([refreshProbeStatus(true),languageBatch.refresh(),refreshIsoStatus()]);toast('状态已更新。');});
action('#refresh-infuse',async()=>{const saved=await api('settings');configuration=saved.settings;featureState();await Promise.all([refreshMultiClipStatus(),refreshInfuseChapters()]);toast('状态已更新。');});
action('#refresh-probe',async()=>{await refreshProbeStatus(true);});
$('#enable-probe').addEventListener('change',event=>setProbeEnabled(event.currentTarget.checked));
$('#enable-multiclip').addEventListener('change',event=>setMultiClipEnabled(event.currentTarget.checked));
// Plain switches save immediately; only text inputs have a save button in their card.
$('#enable-infuse').addEventListener('change',event=>toggleSetting(event.currentTarget,'enableInfuseLanguagePatch','Infuse UHD 语言补写已开启。','Infuse UHD 语言补写已关闭。'));
$('#enable-infuse-chapters').addEventListener('change',event=>toggleSetting(event.currentTarget,'enableInfuseChapters','Infuse 原盘章节已开启。请在 Infuse 中强制退出再打开，让它重新同步媒体库。','Infuse 原盘章节已关闭。请在 Infuse 中强制退出再打开，让它重新同步媒体库。'));
action('#save-addresses',async()=>{await saveSetting({proxyUrl:$('#proxy-url').value.trim().replace(/\/$/,''),managementUrl:$('#management-url').value.trim().replace(/\/$/,'')});showSaved(['proxy-url','management-url']);toast($('#restart-banner').hidden ? '访问地址已保存。' : '访问地址已保存，重新启动本工具后生效。');});
action('#test-emby',async()=>{await saveServer();const result=await testEmby();if(!$('#probe-path').value && result.originalProbePath && result.originalProbeAccessible){$('#probe-path').value=result.originalProbePath;await saveServer();showProbeDiscovery(`已自动获取并保存原版 ffprobe 路径：${result.originalProbePath}\n点击「检查 ffprobe」验证运行。`);}else if(!$('#probe-path').value){showProbeDiscovery(result.originalProbePath ? `Emby 返回：${result.originalProbePath}\n本工具无法访问此文件，请检查主机或容器路径。` : '此 Emby 版本未返回原版路径，请检测本机或参考下方路径手动填写。',true);}toast('配置已保存，Emby 连接正常。');});
function showProbeDiscovery(message, failed = false) { const el=$('#probe-discovery-result'); el.hidden=false; el.classList.toggle('failed',failed); el.textContent=message; }
action('#discover-probe',async()=>{try {const result=await api('probe/discover',{embyUrl:$('#emby-url').value.trim().replace(/\/$/,''),embyApiKey:$('#api-key').value.trim()});if(result.path && result.accessible){$('#probe-path').value=result.path;showProbeDiscovery(result.message+'\n已填入，点击「保存并测试连接」保存。');}else showProbeDiscovery(result.message+(result.path?'\nEmby 返回：'+result.path:''),true);}catch(error){showProbeDiscovery(error.message,true);throw error;}});
action('#check-probe',async()=>{await saveServer();const result=await api('probe/check',{});$('#connection-result').hidden=false;$('#connection-result').classList.remove('failed');$('#connection-result').textContent=result.message;toast('原版 ffprobe 检查通过。');});
action('#detect',async()=>{const result=await api('detect',{});if(result.probePaths.length){$('#probe-path').value=result.probePaths[0];toast('已找到原版 ffprobe，保存后生效。');}else toast('未在常见位置找到 ffprobe，可手动填写完整路径。');$('#connection-result').hidden=false;$('#connection-result').classList.remove('failed');$('#connection-result').textContent=`运行平台：${result.platform}\n`+(result.probePaths.length?'检测到：\n'+result.probePaths.join('\n'):'未检测到默认位置的原版 ffprobe。');});
action('#recover-probe',()=>setProbeEnabled(false));
function languageName(code) {
  const names = { eng:'英语', zho:'中文', chi:'中文', jpn:'日语', kor:'韩语', fra:'法语', fre:'法语', spa:'西班牙语', deu:'德语', ger:'德语', ita:'意大利语', por:'葡萄牙语', rus:'俄语', tha:'泰语', vie:'越南语', ara:'阿拉伯语', hin:'印地语', nld:'荷兰语', dut:'荷兰语', swe:'瑞典语', dan:'丹麦语', nor:'挪威语', fin:'芬兰语', pol:'波兰语', ces:'捷克语', cze:'捷克语', tur:'土耳其语', ell:'希腊语', gre:'希腊语', hun:'匈牙利语', ron:'罗马尼亚语', rum:'罗马尼亚语', ind:'印度尼西亚语', msa:'马来语', may:'马来语', heb:'希伯来语', ukr:'乌克兰语', yue:'粤语', cmn:'普通话' };
  if (names[code]) return names[code];
  try { return new Intl.DisplayNames(['zh-CN'], {type:'language'}).of(code) || code; } catch { return code; }
}
function renderDiscSource(source) {
  const tracks = source.languages || [];
  const groups = [['Audio','音轨','lavender'],['Subtitle','字幕','mint'],['Unknown','类型待确认','neutral']]
    .map(([type,title,color]) => ({type,title,color,tracks:tracks.filter(track => (['Audio','Subtitle'].includes(track.type) ? track.type : 'Unknown') === type)}));
  const sections = groups.filter(group => group.tracks.length).map(group => `<section class="track-group ${group.color}" data-type="${group.type}" aria-label="${group.title}"><div class="track-group-heading"><h4>${group.title}</h4><span class="track-count">${group.tracks.length}</span></div><div class="language-chips">${group.tracks.map(track => `<span class="language-chip"><span class="track-language">${escapeHtml(languageName(track.language))}</span><span class="track-code">${escapeHtml(track.language)}</span><span class="track-pid">${escapeHtml(track.pid)}</span></span>`).join('')}</div></section>`).join('');
  const counts = groups.filter(group => group.tracks.length).map(group => `${group.tracks.length} 条${group.title}`).join(' · ');
  const state = source.isIso ? ['','ISO 文件'] : source.readable ? ['','目录可读取'] : ['off','目录不可读取'];
  const empty = source.isIso ? 'ISO 的音轨与字幕语言由 Emby 探测器读取，见上方「语言与时长」。' : '未获得符合 UHD 补写条件的语言映射。请检查影片类型、目录和播放列表。';
  return `<div class="disc-source"><div class="disc-source-heading"><span class="tag ${state[0]}">${state[1]}</span><span class="disc-track-summary">${escapeHtml(counts)}</span></div><code>${escapeHtml(source.path)}</code>${sections || `<p class="disc-empty">${empty}</p>`}</div>`;
}
// One movie across every feature: the live disc read plus each page's last
// saved check. Only the explicit spec action below writes to Emby.
let inspection = null;
action('#inspect',async()=>{
  const itemId=$('#item-id').value.trim(), out=$('#disc-result');
  // Never leave the previous movie on screen while the next one is read.
  out.hidden=false; out.innerHTML='<p class="muted">正在读取这部影片的信息…</p>';
  try {
    const disc=await api('disc/check',{itemId});
    const get=path=>api(path).catch(error=>({error:error.message}));
    const [language,features,chapters,infuse,featureStatus]=await Promise.all([get('library-language'),get('media-features/batch'),get('chapters/batch'),get('infuse/chapters/'+encodeURIComponent(itemId)),get('media-features')]);
    inspection={itemId,disc,language,features,chapters,infuse,featureReady:!featureStatus.error && featureStatus.enabled && featureStatus.automatic === true && featureStatus.probeAccessible && featureStatus.probeConnected};
    renderInspection();
  } catch (error) { out.innerHTML=`<p class="error">${escapeHtml(error.message)}</p>`; throw error; }
});
function renderInspection() {
  const { itemId, disc, language, features, chapters, infuse, featureReady } = inspection;
  const find = snapshot => snapshot?.items?.find(i => i.id === itemId);
  const missing = page => `<p class="muted">不在上次的媒体库检查结果中。普通 MKV / MP4 不处理；原盘请先在「${labels[page]}」检查媒体库。</p>`;
  const card = (title, page, body) => `<section class="inspect-card"><div class="inspect-card-heading"><h4>${title}</h4><button class="text-button accent" data-goto-inspect="${page}">${labels[page]} →</button></div>${body}</section>`;
  const l = find(language);
  const languageBody = !l ? missing('language') : `<p><span class="tag ${l.classification === 'complete' ? '' : 'off'}">${({complete:'信息完整',missing:'语言缺失',unknown:'信息不足',unsupported:'暂不支持'})[l.classification] || '待确认'}</span></p><p>时长：${renderLibraryDurations(l)}</p><p>缺失：音轨 ${l.missingAudio} · 字幕 ${l.missingSubtitles}</p>`;
  const f = find(features), isDisc = disc.sources.some(s => s.isIso || s.container === 'bluray');
  const probeHint = !isDisc ? '只处理 BDMV 与 Blu-ray ISO。' : !featureReady ? '需要先在「音视频规格」开启规格识别。' : '';
  const featureBody = (!f ? missing('media-features') : f.sources.map(s => `<p><b>${escapeHtml(s.kind)}</b> · ${escapeHtml(s.details)}</p>`).join('') + `<p><span class="tag ${f.verified ? '' : 'off'}">${f.verified ? '已核验' : '待核验'}</span></p>`)
    + `<div class="form-actions"><button class="button secondary" id="inspect-probe-features" ${probeHint ? 'disabled' : ''}>识别并保存规格</button></div>${probeHint ? `<p class="field-help">${probeHint}</p>` : ''}<pre class="result" id="inspect-feature-result" hidden></pre>`;
  const c = find(chapters), stateNames = { empty:'缺少章节', generated:'等间隔生成', existing:'已有章节，保留', managed:'原生章节已核验', error:'需重试' };
  const chapterBody = !c ? missing('chapters') : c.sources.map(s => `<p><b>${escapeHtml(s.kind)}</b> · ${s.count} 个章节 · ${escapeHtml(stateNames[s.state] || s.state)}${s.playlist ? ' · ' + escapeHtml(s.playlist) : ''}</p>`).join('');
  const records = new Map((infuse?.sources || []).map(s => [s.sourceId, s]));
  const infuseBody = (infuse?.enabled === false ? '<p class="muted">「Infuse 原盘章节」已关闭，Infuse 按原方式播放。</p>' : '') + disc.sources.map((s, index) => {
    const name = disc.sources.length > 1 ? `版本 ${index + 1} · ` : '', r = records.get(s.id);
    const text = s.isIso ? '真实 ISO，Infuse 直接读取原盘' : s.container !== 'bluray' ? '非原盘，按原方式播放'
      : r?.iso ? `虚拟 ISO · ${r.chapters} 个章节 · ${escapeHtml(r.playlist || '')}` : r?.reason ? `保留原播放方式：${escapeHtml(r.reason)}` : 'Infuse 尚未同步这部影片';
    return `<p>${name}${text}</p>`;
  }).join('');
  $('#disc-result').hidden = false;
  $('#disc-result').innerHTML = `<h3>${escapeHtml(disc.name)} <small>ID ${escapeHtml(itemId)}</small></h3>
   <div class="inspect-grid">${card('语言与时长','language',languageBody)}${card('音视频规格','media-features',featureBody)}${card('原盘章节','chapters',chapterBody)}${card('Infuse 呈现','infuse',infuseBody)}</div>
   <h4 class="inspect-subtitle">原盘音轨与字幕</h4>${disc.sources.map(renderDiscSource).join('')}`;
}
$('#disc-result').addEventListener('click', async event => {
  const page = event.target.closest('[data-goto-inspect]')?.dataset.gotoInspect;
  if (page) { navigate(page); return; }
  const button = event.target.closest('#inspect-probe-features'); if (!button) return;
  const output = $('#inspect-feature-result'); button.disabled = true; output.hidden = false; output.textContent = '正在读取所选正片并保存已确认的规格…';
  try { const result = await api('media-features/probe',{itemId:inspection.itemId}); output.textContent = result.name + '\n\n' + result.streams.map(s => `#${s.Index} · ${s.Codec.toUpperCase()} · ${s.Title || s.Profile || '基础规格保留'}${s.Language ? ' · ' + s.Language : ''}`).join('\n'); toast('规格已保存到 Emby。'); }
  catch (error) { output.textContent = error.message; toast(error.message, true); }
  finally { button.disabled = false; }
});

async function refreshIsoStatus() {
  if (isoRequest) return isoRequest;
  const revision = isoRevision;
  isoLoading = true; renderLibraryTask();
  isoRequest = (async()=>{
    try { const result = await api('iso/status'); if (revision === isoRevision) isoStatus = result; }
    catch(error) { if (revision === isoRevision) isoStatus = {state:'blocked',canProbe:false,pluginState:'unknown',probeState:'unknown',message:error.message}; }
    finally { if (revision === isoRevision) { isoLoading = false; isoRequest = null; renderLibraryTask(); } }
  })();
  return isoRequest;
}
function renderIsoStatus() {
  const state = isoLoading ? 'checking' : isoStatus?.state || 'unknown';
  $('#iso-prerequisites').dataset.state = state;
  $('#iso-status-title').textContent = ({checking:'正在检查 ISO 补全条件',ready:'ISO 补全前置检查通过',blocked:'ISO 补全暂不可用',unknown:'尚未检查 ISO 补全条件'})[state];
  $('#iso-status-message').textContent = isoLoading ? '正在核对 Emby 已加载的插件和原版 ffprobe 输入协议，不读取电影。' : isoStatus?.message || '进入此页面或点击「重新检查」。';
  const plugin = {ready:'已加载', 'not-loaded':'未加载 · 请安装并重启 Emby',outdated:'版本能力不足 · 更新后重启',unknown:'状态无法确认'};
  const probe = {ready:'内置 ISO 读取',unsupported:'缺少可用的读取协议',unknown:'能力未确认'};
  $('#iso-plugin-status').textContent = `插件 · ${isoLoading ? '检查中' : plugin[isoStatus?.pluginState] || '待检查'}${isoStatus?.pluginVersion ? ' · ' + isoStatus.pluginVersion : ''}`;
  $('#iso-probe-status').textContent = `ffprobe · ${isoLoading ? '检查中' : probe[isoStatus?.probeState] || '待检查'}`;
  $('#iso-plugin-status').title = isoStatus?.pluginMessage || '';
  $('#iso-probe-status').title = isoStatus?.probeMessage || '';
  $('#iso-checked-at').textContent = `${isoStatus?.checkedAt ? '检查于 ' + new Date(isoStatus.checkedAt).toLocaleString('zh-CN',{hour12:false}) + '。' : ''}安装或更新 DLL 并重启 Emby 后，点击「重新检查」。BDMV 补全不依赖 ISO 插件。`;
  $('#refresh-iso').disabled = isoLoading;
}
function formatLibraryRuntime(ticks) {
  if (!Number.isFinite(ticks) || ticks <= 0) return '未识别';
  const seconds = Math.floor(ticks / 10000000);
  return [Math.floor(seconds / 3600),Math.floor(seconds / 60) % 60,seconds % 60].map(n=>String(n).padStart(2,'0')).join(':');
}
function renderLibraryDurations(item) {
  const sources = item.sources || [];
  if (!sources.length) return '未识别';
  return sources.map((s,index)=>`<span>${sources.length > 1 ? `版本 ${index+1} · ` : ''}${formatLibraryRuntime(s.runTimeTicks)}</span>`).join('<br>');
}
const languagePending = i => ['missing','unknown'].includes(i.classification);
const probeReady = () => probeStatus?.state === 'connected' && probeStatus.languagePatchEnabled && !probeChanging && !probeLoading;
const languageBatch = BatchPanel({
  prefix:'library', mount:'library-batch', page:'language', api:'library-language', noun:'语言与时长', progress:true,
  checked: task => !!task?.checkedAt || task?.moviesChecked > 0,
  counts:[
    { key:'discs', label:'原盘影片', idle:'尚未检查媒体库', value:t => t.summary.blurayMovies, foot:t => `${t.moviesChecked} 部电影 · ${t.summary.uhdMovies} 部 UHD` },
    { key:'complete', label:'信息完整', idle:'轨道语言及时长均完整', value:t => t.summary.completeMovies, foot:() => '轨道语言及时长均完整' },
    { key:'missing', label:'语言缺失', idle:'音轨与字幕分别统计', value:t => t.summary.missingMovies, foot:t => `音轨 ${t.summary.missingAudio} 条 · 字幕 ${t.summary.missingSubtitles} 条` },
    { key:'unknown', label:'信息不足', idle:'需要重新探测后确认', value:t => t.summary.unknownMovies, foot:t => `${t.summary.missingRuntimeMovies || 0} 部缺少时长` }],
  columns:[
    { label:'电影', cell:l => `${l.moviesChecked}${l.scanComplete ? '' : ' / ' + l.moviesTotal}` },
    { label:'原盘', cell:l => l.blurayMovies }, { label:'信息完整', cell:l => l.completeMovies },
    { label:'语言缺失', cell:l => `${l.missingMovies}<small>音轨 ${l.missingAudio} · 字幕 ${l.missingSubtitles}</small>` },
    { label:'信息不足', cell:l => l.unknownMovies }],
  itemColumns:['信息状态','时长','缺失语言'], pendingLabel:'待补全', issuesLabel:'失败 / 仍有缺失',
  pending: languagePending, issue: i => ['failed','partial','unverified'].includes(i.result),
  meta: i => escapeHtml(i.format),
  cells: i => [`<span class="tag ${i.classification === 'complete' ? '' : 'off'}">${({complete:'信息完整',missing:'语言缺失',unknown:'信息不足',unsupported:'暂不支持'})[i.classification] || '待确认'}</span>`,
    renderLibraryDurations(i), `音轨 ${i.missingAudio} · 字幕 ${i.missingSubtitles}`],
  results:{ pending:'等待补全', completed:'已核验成功' },
  tally: t => `成功 ${t.completed} · 仍有缺失 ${t.partial} · 失败 / 未核验 ${t.failed} · 跳过 ${t.skipped}`,
  ready: () => probeReady() && !settingsSaving,
  // ISO needs the plugin and a readable ffprobe; BDMV in the same scope can still run.
  plan: items => {
    const iso = items.filter(i => i.sources?.some(s => s.isIso)).length, blocked = iso > 0 && (!isoStatus?.canProbe || isoLoading);
    return blocked ? { runnable: items.length - iso, label:`仅补全 BDMV（${items.length - iso} 部）`,
      note:`所选范围有 ${iso} 部 ISO 暂不提交探测，请先解决上方 ISO 前置检查提示。${items.length - iso ? `可先补全其余 ${items.length - iso} 部 BDMV。` : ''}` } : {};
  },
  note: task => !probeReady() ? '检查只读取 Emby 已保存的信息；补全前请开启上方「Emby 探测语言补全」并确认已接入。'
    : task?.scanComplete ? '计划包含语言缺失、时长缺失和信息不足的原盘；按所选媒体库补全。' : '先检查媒体库，再按计划补全。语言及时长完整的原盘会自动跳过。',
  beforeFill: async items => {
    if (!items.some(i => i.sources?.some(s => s.isIso))) return true;
    await refreshIsoStatus();
    if (isoStatus?.canProbe || items.some(i => !i.sources?.some(s => s.isIso))) return true;
    toast(isoStatus?.message || 'ISO 补全前置检查未通过。', true); return false;
  },
  onScanFinished: () => refreshIsoStatus().catch(() => {})
});
function renderLibraryTask() { renderIsoStatus(); languageBatch.render(); }
$('#refresh-iso').addEventListener('click',()=>refreshIsoStatus());
async function refreshMediaFeatures() {
  if (mediaFeatureBusy) return;
  const result = await api('media-features');
  configuration.enableMediaFeatures = result.enabled;
  configuration.featureProbePath = result.probePath;
  $('#enable-media-features').checked = result.enabled;
  $('#feature-track-titles').checked = !!result.originalTrackTitles;
  $('#feature-probe-path').value = result.probePath || '';
  const ready = result.enabled && result.automatic === true && result.probeAccessible && result.probeConnected;
  mediaFeatureReady = ready;
  setStatus($('#media-feature-status'), ready ? 'on' : result.enabled ? 'warn' : 'off', ready ? '已启用 · 新影片自动识别' : result.enabled ? '配置需要检查' : '已关闭',
    result.error || (ready ? `Emby 扫描扩展已开启，已自动核验 ${result.checkedItems} 部影片。已有影片可在下方按媒体库批量补全或单独核验。` : '配置独立增强 ffprobe 后打开开关。新影片随入库扫描自动处理，无需逐片点击。'));
  window.dispatchEvent(new Event('helper:media-features'));
}
async function configureMediaFeatures() {
  if (mediaFeatureBusy) return;
  mediaFeatureBusy = true;
  $('#enable-media-features').disabled = true;
  try {
    await api('media-features',{enabled:$('#enable-media-features').checked,probePath:$('#feature-probe-path').value.trim(),originalTrackTitles:$('#feature-track-titles').checked});
    toast('音视频规格设置已保存。');
  } finally {
    mediaFeatureBusy = false;
    $('#enable-media-features').disabled = false;
    await refreshMediaFeatures();
    await refreshProbeStatus(true);
  }
}
$('#enable-media-features').addEventListener('change',()=>configureMediaFeatures().catch(error=>toast(error.message,true)));
$('#feature-track-titles').addEventListener('change',()=>configureMediaFeatures().catch(error=>toast(error.message,true)));
$('#attention-list').addEventListener('click',event=>{const page=event.target.closest('[data-attention]')?.dataset.attention;if(page)navigate(page);});
action('#save-media-features',configureMediaFeatures);
action('#refresh-media-features',refreshMediaFeatures);
action('#check-feature-probe',async()=>{const result=await api('media-features/check',{probePath:$('#feature-probe-path').value.trim()});$('#feature-probe-result').hidden=false;$('#feature-probe-result').textContent=result.version;});
startConsole().catch(()=>{});
