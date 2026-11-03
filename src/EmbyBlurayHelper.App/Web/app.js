'use strict';
const $ = s => document.querySelector(s);
const $$ = s => [...document.querySelectorAll(s)];
let configuration = null, logEntries = [], logLevel = 'all', pollTimer, toastTimer;
let probeStatus = null, probeLoading = false, probeRevision = 0, probeCheckedAt = 0, probeRequest = null;
let probeChanging = false, settingsSaving = false;
let logRevision = 0, logsClearing = false;
let multiClipStatus = null, multiClipChanging = false, multiClipRevision = 0;
let libraryTask = null, libraryTimer, libraryRequest, libraryRevision = 0, libraryChanging = false, libraryPage = 0;
let librarySelectionPlan = '', librarySelectionInitialized = false, selectedLibraryIds = new Set();
let isoStatus = null, isoLoading = false, isoRequest = null, isoRevision = 0;
const labels = { overview:'概览', connection:'Emby 连接', language:'Emby 探测语言补全', 'media-features':'音视频规格', chapters:'原盘章节', infuse:'Infuse 播放', diagnostics:'原盘检查', logs:'运行日志' };
let mediaFeatureBusy = false, mediaFeatureReady = false;
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
function showLogin() { clearInterval(pollTimer); clearTimeout(libraryTimer); libraryRevision++; libraryTask = null; isoRevision++; isoStatus = null; isoRequest = null; isoLoading = false; probeRevision++; probeStatus = null; probeRequest = null; probeCheckedAt = 0; mediaFeatureReady = false; $('#account-password').value = ''; $('#console').hidden = true; $('#login').hidden = false; window.dispatchEvent(new Event('helper:logout')); }
function navigate(page) {
  if (page === 'features') page = 'language';
  if (!labels[page]) page = 'overview';
  $$('.page').forEach(el => el.hidden = el.id !== 'page-' + page);
  $$('.nav-item').forEach(el => el.classList.toggle('active', el.dataset.page === page));
  $('#page-breadcrumb').textContent = labels[page];
  document.title = labels[page] + ' · EmbyBlurayHelper';
  if (location.hash !== '#' + page) history.replaceState(null, '', '#' + page);
  if (configuration && !$('#console').hidden && ['overview','language'].includes(page)) refreshProbeStatus().catch(() => {});
  if (configuration && !$('#console').hidden && ['overview','infuse'].includes(page)) refreshMultiClipStatus().catch(() => {});
  clearTimeout(libraryTimer);
  if (configuration && !$('#console').hidden && page === 'language') { refreshLibraryTask().catch(() => {}); refreshIsoStatus().catch(() => {}); }
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
  $('#enable-infuse-chapters').checked = !!configuration.enableInfuseChapters;
  $('#infuse-chapters-state').textContent = configuration.enableInfuseChapters ? '已启用 · 待客户端验证' : '已关闭';
  $('#infuse-chapters-state').classList.toggle('off', !configuration.enableInfuseChapters);
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
  const el = $('#infuse-state'); el.textContent = configuration.enableInfuseLanguagePatch ? '已启用' : '已关闭'; el.classList.toggle('off', !configuration.enableInfuseLanguagePatch);
  renderProbeStatus();
  renderMultiClipStatus();
}
function renderMultiClipStatus() {
  const enabled = multiClipStatus?.enabled ?? !!configuration?.enableInfuseMultiClipPlayback;
  const state = multiClipChanging ? 'changing' : !enabled ? 'off' : multiClipStatus?.readerAvailable ? 'on' : 'pending';
  const title = ({changing:'正在保存',off:'已关闭',on:'已开启 · 按需处理',pending:'已开启 · 等待配置'})[state];
  $('#multiclip-state').textContent = title; $('#multiclip-state').classList.toggle('off',state !== 'on');
  $('#multiclip-status').dataset.state = state;
  $('#multiclip-status-title').textContent = title;
  $('#multiclip-status-copy').textContent = multiClipChanging ? '正在保存独立开关，稍后生效。' : multiClipStatus?.message || '新功能默认关闭，可与语言补写分别开启或关闭。';
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
  for (const button of [$('#save-features'),$('#save-infuse-settings'),$('#test-emby'),$('#check-probe'),$('#discover-probe')]) button.disabled = probeChanging || settingsSaving || multiClipChanging || button.dataset.busy === '1';
  $('#enable-probe').disabled ||= multiClipChanging;
  $('#enable-multiclip').disabled = multiClipChanging || settingsSaving || probeChanging;
  $('#probe-switch-help').textContent = '此开关立即生效并自动保存，无需点击「保存设置」。关闭会恢复原探测器配置，不会清理媒体库。';
  renderLibraryTask();
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
function collectSettings() {
  return { ...configuration, embyUrl:$('#emby-url').value.trim().replace(/\/$/,''), proxyUrl:$('#proxy-url').value.trim().replace(/\/$/,''), managementUrl:$('#management-url').value.trim().replace(/\/$/,''), upstreamProbePath:$('#probe-path').value.trim(), embyApiKey:$('#api-key').value.trim(), enableInfuseLanguagePatch:$('#enable-infuse').checked, enableInfuseChapters:$('#enable-infuse-chapters').checked };
}
async function save() { settingsSaving = true; renderProbeStatus(); renderMultiClipStatus(); try { await api('settings', collectSettings()); probeRevision++; probeStatus = null; isoRevision++; isoStatus = null; isoRequest = null; fillSettings(await api('settings')); await Promise.all([refreshProbeStatus(true),refreshMultiClipStatus(),refreshIsoStatus()]); } finally { settingsSaving = false; renderProbeStatus(); renderMultiClipStatus(); } }
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
  $('#recent-activity').innerHTML = [...logEntries].reverse().slice(0,3).map(e => `<div class="activity-row"><span class="event-mark"><svg><use href="#${e.level === 'Information' ? 'check' : 'activity'}"/></svg></span><span class="event-message" title="${escapeHtml(e.message)}">${escapeHtml(e.message)}</span><time>${new Date(e.time).toLocaleTimeString('zh-CN',{hour12:false})}</time></div>`).join('') || '<p class="empty">暂无活动</p>';
}
async function refresh() {
  const revision = logRevision;
  const [status,logs] = await Promise.all([api('status'),api('logs')]);
  $('#stat-requests').textContent = status.requests.toLocaleString(); $('#stat-active').textContent = status.active;
  $('#stat-patched').textContent = status.patchedTables.toLocaleString();
  const [number,unit] = size(status.patchedBytes); $('#stat-bytes').innerHTML = `${number} <small>${unit}</small>`;
  $('#stat-memory').innerHTML = `${status.memoryMiB} <small>MB</small>`;
  const minutes = Math.max(0,Math.floor((Date.now() - new Date(status.started))/60000));
  $('#stat-uptime').textContent = minutes < 60 ? `已运行 ${minutes} 分钟` : `已运行 ${Math.floor(minutes/60)} 小时 ${minutes%60} 分钟`;
  $('#platform').textContent = status.os + ' · ' + status.architecture;
  if (!logsClearing && revision === logRevision) { logEntries = logs; renderLogs(); }
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
const saveSettings = async()=>{await save();toast('设置已保存'+($('#restart-banner').hidden ? '，功能开关已生效。' : '，端口变更将在重启后生效。'));};
action('#save-features',saveSettings);
action('#save-infuse-settings',saveSettings);
action('#refresh-status',async()=>{await Promise.all([refresh(),refreshProbeStatus(true)]);await testEmby();toast('状态已更新。');});
action('#refresh-probe',async()=>{await refreshProbeStatus(true);});
$('#enable-probe').addEventListener('change',event=>setProbeEnabled(event.currentTarget.checked));
$('#enable-multiclip').addEventListener('change',event=>setMultiClipEnabled(event.currentTarget.checked));
action('#test-emby',async()=>{await save();const result=await testEmby();if(!$('#probe-path').value && result.originalProbePath && result.originalProbeAccessible){$('#probe-path').value=result.originalProbePath;await save();showProbeDiscovery(`已自动获取并保存原版 ffprobe 路径：${result.originalProbePath}\n点击「检查 ffprobe」验证运行。`);}else if(!$('#probe-path').value){showProbeDiscovery(result.originalProbePath ? `Emby 返回：${result.originalProbePath}\n本工具无法访问此文件，请检查主机或容器路径。` : '此 Emby 版本未返回原版路径，请检测本机或参考下方路径手动填写。',true);}toast('配置已保存，Emby 连接正常。');});
function showProbeDiscovery(message, failed = false) { const el=$('#probe-discovery-result'); el.hidden=false; el.classList.toggle('failed',failed); el.textContent=message; }
action('#discover-probe',async()=>{try {const result=await api('probe/discover',{embyUrl:$('#emby-url').value.trim().replace(/\/$/,''),embyApiKey:$('#api-key').value.trim()});if(result.path && result.accessible){$('#probe-path').value=result.path;showProbeDiscovery(result.message+'\n已填入，点击「保存并测试连接」保存。');}else showProbeDiscovery(result.message+(result.path?'\nEmby 返回：'+result.path:''),true);}catch(error){showProbeDiscovery(error.message,true);throw error;}});
action('#check-probe',async()=>{await save();const result=await api('probe/check',{});$('#connection-result').hidden=false;$('#connection-result').classList.remove('failed');$('#connection-result').textContent=result.message;toast('原版 ffprobe 检查通过。');});
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
  return `<div class="disc-source"><div class="disc-source-heading"><span class="tag ${source.readable?'':'off'}">${source.readable?'目录可读取':'目录不可读取'}</span><span class="disc-track-summary">${escapeHtml(counts)}</span></div><code>${escapeHtml(source.path)}</code>${sections || '<p class="disc-empty">未获得符合 UHD 补写条件的语言映射。请检查影片类型、目录和播放列表。</p>'}</div>`;
}
action('#inspect',async()=>{const result=await api('disc/check',{itemId:$('#item-id').value.trim()});const el=$('#disc-result');el.hidden=false;el.innerHTML=`<h3>${escapeHtml(result.name)}</h3>`+result.sources.map(renderDiscSource).join('');});

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
function scheduleLibraryRefresh() {
  clearTimeout(libraryTimer);
  if (libraryTask?.active && !document.hidden && !$('#console').hidden && location.hash === '#language')
    libraryTimer = setTimeout(() => refreshLibraryTask().catch(() => {}), 2000);
}
async function refreshLibraryTask() {
  if (libraryRequest) return libraryRequest;
  const revision = libraryRevision;
  libraryRequest = (async () => {
    try {
      const result = await api('library-language');
      if (revision === libraryRevision) { const scanFinished = libraryTask?.status === 'scanning' && !result.active; libraryTask = result; $('#library-error').hidden = true; renderLibraryTask(); if (scanFinished) refreshIsoStatus().catch(()=>{}); }
    } catch(error) {
      if (revision === libraryRevision) { $('#library-error').hidden = false; $('#library-error').textContent = error.message; }
      throw error;
    } finally { libraryRequest = null; scheduleLibraryRefresh(); }
  })();
  return libraryRequest;
}
function renderLibraryTask() {
  renderIsoStatus();
  const task = libraryTask, active = !!task?.active, checked = !!task?.checkedAt || task?.moviesChecked > 0;
  if (task && librarySelectionPlan !== task.id) { librarySelectionPlan = task.id; librarySelectionInitialized = false; selectedLibraryIds = new Set(); }
  if (task?.libraries?.length && (!librarySelectionInitialized || task.status === 'scanning')) {
    selectedLibraryIds = new Set(task.operation === 'fill' && task.selectedLibraryIds?.length ? task.selectedLibraryIds : (task.libraries || []).map(l => l.id));
    librarySelectionInitialized = true;
  }
  if (task?.operation === 'fill' && task.active) selectedLibraryIds = new Set(task.selectedLibraryIds);
  const summary = task?.summary, pendingMovies = chosenLibraryMovies().filter(i => ['missing','unknown'].includes(i.classification));
  const pending = pendingMovies.length, isoPending = pendingMovies.filter(i => i.sources?.some(s => s.isIso)).length;
  const isoBlocked = isoPending > 0 && (!isoStatus?.canProbe || isoLoading);
  const eligible = isoBlocked ? pending - isoPending : pending;
  const probeReady = probeStatus?.state === 'connected' && probeStatus.languagePatchEnabled && !probeChanging && !probeLoading;
  $('#library-scan').disabled = active || libraryChanging;
  $('#library-scan').textContent = task?.checkedAt ? '重新检查电影库' : '检查电影库';
  $('#library-fill').disabled = !task?.canFill || !eligible || !probeReady || libraryChanging || settingsSaving;
  $('#library-fill').textContent = isoBlocked ? `仅补全 BDMV（${eligible} 部）` : `${task?.operation === 'fill' && !active ? '继续补全所选' : '补全所选媒体库'}${pending ? `（${pending} 部）` : ''}`;
  $('#library-cancel').hidden = !active;
  $('#library-cancel').disabled = libraryChanging || task?.status === 'cancelling';
  $('#library-refresh').disabled = libraryChanging;
  $('#library-disc-count').textContent = checked ? summary.blurayMovies : '—';
  $('#library-complete-count').textContent = checked ? summary.completeMovies : '—';
  $('#library-missing-count').textContent = checked ? summary.missingMovies : '—';
  $('#library-unknown-count').textContent = checked ? summary.unknownMovies : '—';
  $('#library-runtime-count').textContent = checked ? `${summary.missingRuntimeMovies || 0} 部缺少时长` : '需要重新探测后确认';
  $('#library-movie-count').textContent = task?.moviesChecked ? `${task.moviesChecked} 部电影 · ${summary.uhdMovies} 部 UHD` : '尚未检查电影库';
  $('#library-track-count').textContent = checked ? `音轨 ${summary.missingAudio} 条 · 字幕 ${summary.missingSubtitles} 条` : '音轨与字幕分别统计';
  $('#library-prerequisite').textContent = task && !task.connectionMatches && task.id
    ? 'Emby 连接已变化，请重新检查电影库生成计划。'
    : !probeReady ? '检查只读取 Emby 已保存的信息；补全前请开启上方「Emby 探测语言补全」并确认已接入。'
    : task?.scanComplete ? '计划包含语言缺失、时长缺失和信息不足的原盘；按所选媒体库补全。'
    : '先检查电影库，再按计划补全。语言及时长完整的原盘会自动跳过。';
  if (isoBlocked) $('#library-prerequisite').textContent += ` 所选范围有 ${isoPending} 部 ISO 暂不提交探测，请先解决上方 ISO 前置检查提示。${eligible ? `可先补全其余 ${eligible} 部 BDMV。` : ''}`;
  const titles = {idle:'尚未检查',scanning:'正在检查电影库',ready:'检查完成',filling:'正在补全语言',cancelling:'正在停止任务',cancelled:'任务已停止',interrupted:'上次任务中断',completed:'补全任务结束',failed:'任务未完成'};
  $('#library-progress').dataset.state = task?.status || 'idle';
  $('#library-task-title').textContent = titles[task?.status] || titles.idle;
  $('#library-task-message').textContent = task?.message || '点击「检查电影库」生成补全计划。';
  const filling = task?.operation === 'fill';
  const done = filling ? task?.processed || 0 : task?.librariesChecked || 0;
  const total = filling ? task?.planned || 0 : task?.libraries?.length || 0;
  const bar = $('#library-task-bar'); bar.hidden = !task?.id;
  if (active && !total) bar.removeAttribute('value');
  else { bar.max = Math.max(1,total); bar.value = Math.min(done,total); }
  $('#library-task-percent').textContent = total ? `${done} / ${total}${filling ? ' 部' : ' 个媒体库'}` : '';
  $('#library-current').hidden = !task?.currentName || !active;
  const current = task?.items.find(i => i.id === task.currentItemId);
  const elapsed = current?.attemptedAt ? ` · 已用 ${Math.max(0,Math.floor((Date.now()-new Date(current.attemptedAt))/1000))} 秒` : '';
  $('#library-current').textContent = task?.currentName ? `当前：${task.currentName}${current?.message ? ' · ' + current.message : ''}${elapsed}` : '';
  $('#library-task-counts').textContent = filling ? `成功 ${task.completed} · 仍有缺失 ${task.partial} · 失败 / 未核验 ${task.failed} · 跳过 ${task.skipped}` : '进度仅在任务运行且此页面可见时自动刷新';
  const at = task?.finishedAt || task?.startedAt;
  $('#library-task-time').textContent = at ? `${task.finishedAt ? '结束' : '开始'}于 ${new Date(at).toLocaleString('zh-CN',{hour12:false})}` : '';
  if (task?.persistenceWarning) { $('#library-error').hidden = false; $('#library-error').textContent = task.persistenceWarning; }
  $('#library-details').hidden = !task?.items.length;
  $('#library-detail-count').textContent = task?.items.length ? `${task.items.length} 部` : '';
  renderLibraryGroups();
  renderLibraryRows();
}
function chosenLibraryMovies() { return (libraryTask?.items || []).filter(i => (i.libraryIds || []).some(id => selectedLibraryIds.has(id))); }
function renderLibraryGroups() {
  const groups = libraryTask?.libraries || [], active = libraryTask?.active || libraryChanging;
  $('#library-scope').hidden = groups.length === 0;
  const all = $('#library-select-all');
  const selected = groups.filter(l => selectedLibraryIds.has(l.id));
  all.checked = groups.length > 0 && selected.length === groups.length;
  all.indeterminate = selected.length > 0 && selected.length < groups.length; all.disabled = active;
  $('#library-groups').innerHTML = groups.map(l => `<tr><td><label><input type="checkbox" data-library-id="${escapeHtml(l.id)}" ${selectedLibraryIds.has(l.id) ? 'checked' : ''} ${active ? 'disabled' : ''}><span>${escapeHtml(l.name)}</span></label></td><td>${l.moviesChecked}${l.scanComplete ? '' : ' / ' + l.moviesTotal}</td><td>${l.blurayMovies}</td><td>${l.completeMovies}</td><td>${l.missingMovies}<small>音轨 ${l.missingAudio} · 字幕 ${l.missingSubtitles}</small></td><td>${l.unknownMovies}</td><td>${l.taskPlanned ? l.taskProcessed + ' / ' + l.taskPlanned : '—'}</td></tr>`).join('');
  const pending = chosenLibraryMovies().filter(i=>['missing','unknown'].includes(i.classification)).length;
  $('#library-selection-summary').textContent = `已选 ${selected.length} / ${groups.length} 个媒体库 · 待补全 ${pending} 部（按电影去重）。${libraryTask?.operation === 'fill' && libraryTask.scope ? '本次任务范围：' + libraryTask.scope + '。' : ''}`;
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
function renderLibraryRows() {
  const filter = $('#library-filter').value;
  const rows = chosenLibraryMovies().filter(i => filter === 'pending' ? ['missing','unknown'].includes(i.classification) : filter === 'issues' ? ['failed','partial','unverified'].includes(i.result) : true);
  const pages = Math.max(1,Math.ceil(rows.length / 30));
  libraryPage = Math.max(0,Math.min(libraryPage,pages-1));
  const classification = {complete:'信息完整',missing:'语言缺失',unknown:'信息不足',unsupported:'暂不支持'};
  const result = {pending:'等待补全',running:'处理中',completed:'已核验成功',partial:'仍有缺失',failed:'失败',unverified:'未核验',skipped:'已跳过'};
  $('#library-rows').innerHTML = rows.slice(libraryPage*30,libraryPage*30+30).map(i => `<tr><td><strong>${escapeHtml(i.name)}</strong><small>ID ${escapeHtml(i.id)} · ${escapeHtml(i.format)} · ${escapeHtml((libraryTask.libraries || []).filter(l=>i.libraryIds.includes(l.id)).map(l=>l.name).join('、'))}</small></td><td><span class="tag ${i.classification === 'complete' ? '' : 'off'}">${classification[i.classification] || '待确认'}</span></td><td>${renderLibraryDurations(i)}</td><td>音轨 ${i.missingAudio} · 字幕 ${i.missingSubtitles}</td><td><strong class="library-result ${escapeHtml(i.result)}">${result[i.result] || '—'}</strong><small>${escapeHtml(i.message)}${i.verifiedAt ? ' · 核验于 ' + escapeHtml(new Date(i.verifiedAt).toLocaleString('zh-CN',{hour12:false})) : ''}</small></td></tr>`).join('') || '<tr><td colspan="5" class="empty">此筛选下没有影片</td></tr>';
  $('#library-table-count').textContent = `${rows.length} 部`;
  $('#library-page').textContent = `${libraryPage+1} / ${pages}`;
  $('#library-prev').disabled = libraryPage === 0;
  $('#library-next').disabled = libraryPage >= pages-1;
}
async function changeLibraryTask(operation) {
  if (libraryChanging) return;
  libraryChanging = true; libraryRevision++; clearTimeout(libraryTimer); renderLibraryTask();
  try {
    if (operation === 'fill' && chosenLibraryMovies().some(i => ['missing','unknown'].includes(i.classification) && i.sources?.some(s => s.isIso))) {
      await refreshIsoStatus();
      if (!isoStatus?.canProbe && !chosenLibraryMovies().some(i => ['missing','unknown'].includes(i.classification) && !i.sources?.some(s => s.isIso))) {
        toast(isoStatus?.message || 'ISO 补全前置检查未通过。',true); return;
      }
    }
    libraryTask = await api('library-language/'+operation,operation === 'fill' ? {planId:libraryTask.id,libraryIds:[...selectedLibraryIds]} : {});
    $('#library-error').hidden = true; libraryPage = 0;
    toast(operation === 'scan' ? '已开始检查电影库。' : operation === 'fill' ? '补全任务已启动，可在此查看进度。' : '正在停止任务。');
  } catch(error) { toast(error.message,true); }
  finally { libraryChanging = false; renderLibraryTask(); scheduleLibraryRefresh(); }
}
$('#library-scan').addEventListener('click',()=>changeLibraryTask('scan'));
$('#library-fill').addEventListener('click',()=>changeLibraryTask('fill'));
$('#library-cancel').addEventListener('click',()=>changeLibraryTask('cancel'));
$('#library-refresh').addEventListener('click',()=>refreshLibraryTask().catch(error=>toast(error.message,true)));
$('#refresh-iso').addEventListener('click',()=>refreshIsoStatus());
$('#library-filter').addEventListener('change',()=>{libraryPage=0;renderLibraryRows();});
$('#library-select-all').addEventListener('change',event=>{selectedLibraryIds = new Set(event.currentTarget.checked ? libraryTask.libraries.map(l=>l.id) : []);libraryPage=0;renderLibraryTask();});
$('#library-groups').addEventListener('change',event=>{const id=event.target.dataset.libraryId;if(id){event.target.checked ? selectedLibraryIds.add(id) : selectedLibraryIds.delete(id);libraryPage=0;renderLibraryTask();}});
$('#library-prev').addEventListener('click',()=>{libraryPage--;renderLibraryRows();});
$('#library-next').addEventListener('click',()=>{libraryPage++;renderLibraryRows();});
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
  $('#media-feature-status').dataset.state = ready ? 'connected' : result.enabled ? 'unknown' : 'disconnected';
  $('#media-feature-title').textContent = ready ? '已启用 · 新影片自动识别' : result.enabled ? '配置需要检查' : '已关闭';
  $('#media-feature-dot').className = 'status-dot' + (ready ? '' : ' neutral');
  $('#media-feature-copy').textContent = result.error || (ready ? `Emby 扫描扩展已开启，已自动核验 ${result.checkedItems} 部影片。已有影片可在下方按媒体库批量补充或单独核验。` : '配置独立增强 ffprobe 后打开开关。新影片随入库扫描自动处理，无需逐片点击。');
  $('#probe-media-features').disabled = !ready;
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
action('#save-media-features',configureMediaFeatures);
action('#refresh-media-features',refreshMediaFeatures);
action('#check-feature-probe',async()=>{const result=await api('media-features/check',{probePath:$('#feature-probe-path').value.trim()});$('#feature-probe-result').hidden=false;$('#feature-probe-result').textContent=result.version;});
action('#probe-media-features',async()=>{
  const output=$('#feature-item-result');output.hidden=false;output.textContent='正在读取所选正片并保存已确认的规格…';
  try { const result=await api('media-features/probe',{itemId:$('#feature-item-id').value.trim()}); output.textContent=result.name+'\n\n'+result.streams.map(s=>`#${s.Index} · ${s.Codec.toUpperCase()} · ${s.Title || s.Profile || '基础规格保留'}${s.Language ? ' · '+s.Language : ''}`).join('\n'); }
  catch(error){output.textContent=error.message;throw error;}
});
document.addEventListener('visibilitychange',()=>{clearTimeout(libraryTimer);if(!document.hidden && !$('#console').hidden && location.hash === '#language') refreshLibraryTask().catch(()=>{});});
startConsole().catch(()=>{});
