'use strict';
// Media library processing: one library check, then the chosen completions run
// in order on the server (language and runtime, specs, chapters). Each task keeps
// its own plan; this page merges their last results and drives the pipeline.
(() => {
  let pipeline = null, snaps = {}, featureStatus = null, chapterStatus = null, timer, request = null, revision = 0, busy = false, page = 0;
  let planKey = '', selected = new Set(), chosen = {}, again = {};
  let hideProgressTimer = null;
  const STEPS = [
    { key:'language', name:'语言与时长', page:'language', api:'library-language', pending:i => ['missing','unknown'].includes(i.classification),
      done:i => i.classification === 'complete', missing:i => i.classification === 'missing' ? `缺音轨 ${i.missingAudio} · 字幕 ${i.missingSubtitles}` : i.classification === 'unknown' ? '信息不足' : '暂不支持',
      known:() => !!probeStatus && probeStatus.state !== 'checking', blocked:() => !(probeStatus?.state === 'connected' && probeStatus.languagePatchEnabled) ? '需要在「语言与时长」开启「Emby 探测语言补全」并确认已接入。' : '',
      note:items => { const iso = items.filter(i => i.sources?.some(s => s.isIso)).length; return iso && isoStatus && !isoStatus.canProbe ? `${iso} 部 ISO 因插件或 ffprobe 条件未满足将跳过，其余 BDMV 照常补全。` : ''; } },
    { key:'features', name:'音视频规格', page:'media-features', api:'media-features/batch', recheck:'含已核验的', pending:i => !i.verified, done:i => i.verified, missing:() => '待核验',
      known:() => !!featureStatus, blocked:() => { const f = featureStatus; return !f || f.error ? '无法读取规格识别状态。' : !(f.enabled && f.automatic === true && f.probeAccessible && f.probeConnected) ? '需要在「音视频规格」开启规格识别并配置增强 ffprobe。' : ''; } },
    { key:'chapters', name:'原盘章节', page:'chapters', api:'chapters/batch', recheck:'含已核验的', pending:i => i.pending, done:i => !i.pending, missing:() => '待补全',
      known:() => !!chapterStatus, blocked:() => { const c = chapterStatus; return !c || c.error ? '无法读取原盘章节状态。' : !c.ready ? '需要 Emby 插件 0.1.5 或更新版本。' : !(c.enabled && c.automatic) ? '需要在「章节与截图」开启「原盘章节补全」。' : ''; } }];
  const visible = () => !document.hidden && !!configuration && !$('#console').hidden && location.hash === '#library';
  const running = () => !!pipeline?.active;
  const anyActive = () => running() || STEPS.some(s => snaps[s.key]?.active);
  const valid = s => { const t = snaps[s.key]; return !!t?.id && !t.error && t.scanComplete && t.connectionMatches !== false; };
  const libraries = () => {
    const merged = new Map();
    for (const s of STEPS) for (const l of snaps[s.key]?.libraries || []) {
      const row = merged.get(l.id) || { id:l.id, name:l.name, movies:0, discs:0, pending:{} };
      row.movies = Math.max(row.movies, l.moviesChecked || 0); row.discs = Math.max(row.discs, l.blurayMovies ?? l.discs ?? 0);
      row.pending[s.key] = l.pendingMovies ?? l.pending ?? 0; merged.set(l.id, row);
    }
    return [...merged.values()];
  };
  const inScope = s => (snaps[s.key]?.items || []).filter(i => (i.libraryIds || []).some(id => selected.has(id)));
  const eligible = s => valid(s) ? inScope(s).filter(i => again[s.key] || s.pending(i)) : [];
  const runnable = s => !s.blocked() && eligible(s).length > 0;
  function syncSelection() {
    const groups = libraries(), key = STEPS.map(s => snaps[s.key]?.id || '').join('|');
    if (running()) { selected = new Set(pipeline.libraryIds); return; }
    if (key !== planKey) { planKey = key; selected = new Set(groups.map(l => l.id)); chosen = {}; }
    for (const s of STEPS) if (chosen[s.key] === undefined && valid(s) && s.known()) chosen[s.key] = runnable(s);
  }
  function stepState(s) {
    const step = pipeline?.steps?.find(x => x.task === s.key), t = snaps[s.key];
    if (running() && step) return { state: step.status === 'running' ? 'warn' : step.status === 'completed' ? 'on' : 'off',
      text: ({ running:'正在补全', waiting:'等待执行', completed:'已完成', skipped:'未执行', failed:'未完成', cancelled:'已停止' })[step.status] || step.status, detail: step.message };
    if (t?.active) return { state:'warn', text: t.status === 'scanning' ? '正在检查' : '正在补全', detail: t.message };
    if (!t || t.error) return { state:'off', text:'无法读取', detail: t?.error || '' };
    if (!t.id || !t.scanComplete) return { state:'off', text:'尚未检查', detail:'先点击上方「检查媒体库」。' };
    if (t.connectionMatches === false) return { state:'warn', text:'需要重新检查', detail:'Emby 连接或配置已变化，请重新检查媒体库。' };
    const reason = s.blocked(); if (reason) return { state:'warn', text:'条件未满足', detail: reason };
    const n = eligible(s).length;
    return n ? { state:'on', text:`${n} 部待处理`, detail: s.note?.(eligible(s)) || '' } : { state:'off', text:'无需处理', detail:'所选媒体库没有待处理的原盘。' };
  }
  function render() {
    syncSelection();
    const locked = busy || anyActive(), groups = libraries(), lang = snaps.language?.summary;
    const discs = lang ? lang.blurayMovies : (snaps.features?.summary?.discs ?? null);
    const movies = snaps.language?.moviesChecked ?? null;
    const uhd = lang?.uhdMovies ?? null;
    $('#lib-action-summary').textContent = discs !== null
      ? `${discs} 部原盘 · ${movies ?? '?'} 部电影 · ${uhd ?? '?'} 部 UHD`
      : '尚未检查媒体库';
    const libsEl = $('#lib-action-libs');
    if (groups.length >= 2) {
      libsEl.hidden = false;
      const allSel = groups.every(l => selected.has(l.id));
      libsEl.innerHTML = `<button class="lib-action-libs-toggle" id="lib-select-all-link">${allSel ? '取消全选' : '全选'}</button>`
        + groups.map(l => `<label><input type="checkbox" data-library="${escapeHtml(l.id)}" ${selected.has(l.id) ? 'checked' : ''} ${locked ? 'disabled' : ''}><span>${escapeHtml(l.name)}</span></label>`).join('');
    } else {
      libsEl.hidden = true;
    }
    $('#lib-scan').disabled = locked;
    $('#lib-fill-rows').innerHTML = STEPS.map(s => {
      const st = stepState(s);
      const can = runnable(s) && !locked;
      const recheckDisabled = locked || !valid(s) ? 'disabled' : '';
      return `<div class="lib-fill-row" data-state="${st.state}">` +
        `<input type="checkbox" data-step="${s.key}" ${chosen[s.key] && runnable(s) ? 'checked' : ''} ${can ? '' : 'disabled'}>` +
        `<span class="lib-fill-row-name">${s.name}</span>` +
        `<span class="lib-fill-row-count ${st.state === 'on' ? 'on' : ''}">${escapeHtml(st.text)}</span>` +
        `<button class="text-button" data-goto-step="${s.page}">设置 →</button>` +
        (s.recheck ? `<label class="lib-fill-row-recheck"><input type="checkbox" data-again="${s.key}" ${again[s.key] ? 'checked' : ''} ${recheckDisabled}>${s.recheck}</label>` : '') +
        (st.detail ? `<small class="lib-fill-row-detail">${escapeHtml(st.detail)}</small>` : '') +
        `</div>`;
    }).join('');
    const picked = STEPS.filter(s => chosen[s.key] && runnable(s));
    $('#lib-fill').disabled = locked || !selected.size || !picked.length;
    $('#lib-fill').textContent = '开始补全';
    $('#lib-cancel').hidden = !running();
    $('#lib-cancel').disabled = busy || pipeline?.status === 'cancelling';
    const libFilterEl = $('#lib-lib-filter');
    if (libFilterEl) {
      const curVal = libFilterEl.value;
      libFilterEl.innerHTML = '<option value="">全部媒体库</option>'
        + groups.map(l => `<option value="${escapeHtml(l.id)}" ${curVal === l.id ? 'selected' : ''}>${escapeHtml(l.name)}</option>`).join('');
    }
    const progressRow = $('#lib-progress-row');
    const currentStep = STEPS.find(s => s.key === pipeline?.current);
    const task = currentStep && snaps[currentStep.key];
    if (running()) {
      clearTimeout(hideProgressTimer); hideProgressTimer = null;
      progressRow.hidden = false;
      progressRow.removeAttribute('data-done');
      $('#lib-progress-title').textContent = currentStep?.name || '准备中';
      const processed = task?.processed ?? 0, planned = task?.planned ?? 0;
      $('#lib-progress-count').textContent = planned ? `${processed} / ${planned}` : '';
      const bar = $('#lib-progress-bar');
      bar.hidden = false; bar.value = processed; bar.max = Math.max(1, planned);
      $('#lib-progress-current').textContent = task?.currentName || '';
    } else if ((pipeline?.status === 'completed' || pipeline?.status === 'cancelled') && pipeline?.message) {
      progressRow.hidden = false;
      $('#lib-progress-title').textContent = pipeline.status === 'completed' ? '补全已完成' : '已停止';
      $('#lib-progress-count').textContent = pipeline.message;
      $('#lib-progress-bar').hidden = true;
      $('#lib-progress-current').textContent = '';
      if (!progressRow.dataset.done) {
        progressRow.dataset.done = '1';
        clearTimeout(hideProgressTimer);
        hideProgressTimer = setTimeout(() => { progressRow.hidden = true; }, 8000);
      }
    } else if (!pipeline || pipeline.status === 'idle') {
      if (!hideProgressTimer) progressRow.hidden = true;
    }
    renderRows();
  }
  function cell(s, id) {
    const t = snaps[s.key]; if (!valid(s)) return '<small>未检查</small>';
    const i = t.items.find(x => x.id === id); if (!i) return '<small>—</small>';
    const label = ({ running:'处理中', failed:'失败', partial:'仍有缺失', unverified:'未核验' })[i.result];
    return `<span class="tag ${s.done(i) ? '' : 'off'}">${label || (s.done(i) ? '已完成' : s.missing(i))}</span>${i.message && label ? `<small>${escapeHtml(i.message)}</small>` : ''}`;
  }
  function renderRows() {
    const items = new Map();
    for (const s of STEPS) for (const i of inScope(s)) if (!items.has(i.id)) items.set(i.id, i);
    const filter = $('#lib-filter').value;
    const libFilter = $('#lib-lib-filter')?.value || '';
    const issue = id => STEPS.some(s => ['failed','partial','unverified'].includes(snaps[s.key]?.items?.find(x => x.id === id)?.result));
    const pendingAny = id => STEPS.some(s => { const i = valid(s) && snaps[s.key].items.find(x => x.id === id); return i && s.pending(i); });
    const rows = [...items.values()].filter(i => {
      if (libFilter && !(i.libraryIds || []).includes(libFilter)) return false;
      if (filter === 'pending') return pendingAny(i.id);
      if (filter === 'issues') return issue(i.id);
      return true;
    });
    const pages = Math.max(1, Math.ceil(rows.length / 30)); page = Math.max(0, Math.min(page, pages - 1));
    const hasItems = items.size > 0;
    $('#lib-details').style.display = hasItems ? '' : 'none';
    $('#lib-table-count').textContent = rows.length + ' 部';
    $('#lib-rows').innerHTML = rows.slice(page * 30, page * 30 + 30).map(i => `<tr><td><strong>${escapeHtml(i.name)}</strong><small>ID ${escapeHtml(i.id)}</small></td>${STEPS.map(s => `<td>${cell(s, i.id)}</td>`).join('')}</tr>`).join('')
      || '<tr><td colspan="4" class="empty">此范围没有影片</td></tr>';
    $('#lib-page').textContent = `${page + 1} / ${pages}`; $('#lib-prev').disabled = page === 0; $('#lib-next').disabled = page >= pages - 1;
  }
  function showError(message) { if (message) toast(message, true); }
  function schedule() { clearTimeout(timer); if (visible() && anyActive() && !busy) timer = setTimeout(() => refresh().catch(() => {}), 2000); }
  async function refresh(withStatus = false) {
    if (request) return request;
    const version = revision;
    const pending = (async () => {
      try {
        const get = path => api(path).catch(error => ({ error: error.message }));
        const [p, ...rest] = await Promise.all([get('library-tasks'), ...STEPS.map(s => get(s.api)), ...(withStatus ? [get('media-features'), get('chapters')] : [])]);
        if (version !== revision) return;
        pipeline = p.error ? null : p; STEPS.forEach((s, i) => snaps[s.key] = rest[i]);
        if (withStatus) { featureStatus = rest[3]; chapterStatus = rest[4]; }
        showError(p.error || ''); render();
      } finally { if (request === pending) request = null; schedule(); }
    })();
    request = pending; return pending;
  }
  async function act(path, body, done) {
    if (busy) return;
    busy = true; revision++; clearTimeout(timer); render();
    try { const result = await api('library-tasks/' + path, body); done?.(result); }
    catch (error) { showError(error.message); }
    finally { busy = false; await refresh().catch(() => {}); }
  }
  $('#lib-scan').onclick = () => act('scan', {}, result => {
    const failed = Object.entries(result.results || {}).filter(([, v]) => v !== 'started');
    toast(failed.length ? '部分检查未启动：' + failed.map(([k, v]) => `${STEPS.find(s => s.key === k)?.name}：${v}`).join('；') : '已开始检查媒体库。', failed.length > 0);
  });
  $('#lib-fill').onclick = () => {
    const steps = STEPS.filter(s => chosen[s.key] && runnable(s));
    if (busy || anyActive() || !selected.size || !steps.length) return;
    clearTimeout(hideProgressTimer); hideProgressTimer = null;
    const progressRow = $('#lib-progress-row'); if (progressRow) { progressRow.hidden = true; delete progressRow.dataset.done; }
    return act('fill', { libraryIds: [...selected], steps: steps.map(s => ({ task: s.key, planId: snaps[s.key].id, includeVerified: !!again[s.key] })) }, () => toast('补全任务已启动，可在此查看进度。'));
  };
  $('#lib-cancel').onclick = () => act('cancel', {}, () => toast('正在停止任务。'));
  $('#lib-refresh').onclick = () => Promise.all([refresh(true), refreshProbeStatus(true), refreshIsoStatus()]).catch(error => toast(error.message, true));
  $('#lib-action-libs').addEventListener('change', event => {
    const id = event.target.dataset.library; if (!id) return;
    event.target.checked ? selected.add(id) : selected.delete(id); page = 0; render();
  });
  $('#lib-action-libs').addEventListener('click', event => {
    if (event.target.id === 'lib-select-all-link') {
      const groups = libraries(), allSel = groups.every(l => selected.has(l.id));
      selected = allSel ? new Set() : new Set(groups.map(l => l.id)); page = 0; render();
    }
  });
  $('#lib-fill-rows').addEventListener('change', event => {
    const { step, again: key } = event.target.dataset;
    if (step) chosen[step] = event.target.checked; else if (key) again[key] = event.target.checked; else return;
    render();
  });
  $('#lib-fill-rows').addEventListener('click', event => {
    const target = event.target.closest('[data-goto-step]')?.dataset.gotoStep; if (target) navigate(target);
  });
  $('#lib-filter').addEventListener('change', () => { page = 0; renderRows(); });
  document.getElementById('lib-lib-filter')?.addEventListener('change', () => { page = 0; renderRows(); });
  $('#lib-prev').onclick = () => { page--; renderRows(); }; $('#lib-next').onclick = () => { page++; renderRows(); };
  const summaries = { language:['summary-language','library-language'], 'media-features':['summary-features','media-features/batch'], chapters:['summary-chapters','chapters/batch'] };
  const summaryText = (pg, t) => !t?.id ? '尚未检查媒体库。' : pg === 'language' ? `上次检查：${t.summary.blurayMovies} 部原盘 · ${t.summary.completeMovies} 部信息完整 · ${t.summary.missingMovies + t.summary.unknownMovies} 部待补全。`
    : pg === 'media-features' ? `上次检查：${t.summary.discs} 部原盘 · ${t.summary.verified} 部已核验 · ${t.summary.pending} 部待核验。` : `上次检查：${t.summary.discs} 部原盘 · ${t.summary.verified} 部已补全 · ${t.summary.pending} 部待补全 · ${t.summary.preserved} 部已有章节。`;
  async function loadSummary(pg) {
    const [id, path] = summaries[pg];
    try { const t = await api(path); $('#' + id).textContent = summaryText(pg, t) + (t.active ? ' 任务正在运行。' : ''); } catch (error) { $('#' + id).textContent = error.message; }
  }
  window.addEventListener('helper:navigate', () => {
    clearTimeout(timer);
    const current = location.hash.slice(1);
    if (configuration && !$('#console').hidden && summaries[current]) loadSummary(current);
    if (visible()) { refresh(true).catch(() => {}); refreshProbeStatus().catch(() => {}); refreshIsoStatus().catch(() => {}); }
  });
  document.addEventListener('visibilitychange', () => { clearTimeout(timer); if (visible()) refresh().catch(() => {}); });
  window.addEventListener('helper:status', () => { if (location.hash === '#library') render(); });
  window.addEventListener('helper:logout', () => { clearTimeout(timer); clearTimeout(hideProgressTimer); hideProgressTimer = null; revision++; request = null; pipeline = null; snaps = {}; featureStatus = chapterStatus = null; planKey = ''; selected = new Set(); chosen = {}; again = {}; });
  render();
})();
