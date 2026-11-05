'use strict';
// Batch interaction stays separate from connection settings and language tasks.
const featureBatch = BatchPanel({
  prefix:'feature-batch', mount:'feature-batch', page:'media-features', api:'media-features/batch', noun:'规格', progress:true,
  recheck:'重新核验已完成的原盘',
  counts:[
    { key:'discs', label:'原盘影片', idle:'BDMV / ISO', value:t => t.summary.discs, foot:t => `BDMV ${t.summary.bdmv} 部 · ISO ${t.summary.iso} 部` },
    { key:'verified', label:'批量已核验', idle:'包括样本未确认新增规格的影片', value:t => t.summary.verified, foot:() => '包括样本未确认新增规格的影片' },
    { key:'pending', label:'待核验', idle:'尚无有效的批量核验记录', value:t => t.summary.pending, foot:() => '尚无有效的批量核验记录' },
    { key:'completed', label:'本次成功', idle:'逐源探测并读回确认', value:t => t.operation === 'fill' ? t.completed : '—', foot:t => t.operation === 'fill' ? `失败 ${t.failed} · 跳过 ${t.skipped}` : '逐源探测并读回确认' }],
  columns:[
    { label:'电影', cell:l => `${l.moviesChecked}${l.scanComplete ? '' : ' · 检查中'}` },
    { label:'BDMV / ISO', cell:l => l.discs }, { label:'已核验', cell:l => l.verified }, { label:'待核验', cell:l => l.pending }],
  itemColumns:['原盘版本 / 规格','核验状态'], pendingLabel:'待核验', issuesLabel:'失败 / 未核验', tableClass:'feature-batch-table',
  pending: i => !i.verified, issue: i => ['failed','unverified'].includes(i.result),
  cells: i => [i.sources.map(s => `<small><b>${escapeHtml(s.kind)}</b> · ${escapeHtml(s.details)}${s.verifiedAt ? ' · 核验于 ' + escapeHtml(new Date(s.verifiedAt).toLocaleString('zh-CN',{hour12:false})) : ''}</small>`).join(''),
    `<span class="tag ${i.verified ? '' : 'off'}">${i.verified ? '批量已核验' : '待核验'}</span>`],
  results:{ pending:'等待核验', completed:'已核验成功', unverified:'尚未核验' },
  ready: () => mediaFeatureReady,
  note: () => !mediaFeatureReady ? '检查只读取 Emby 信息；实际补全前请开启上方规格识别并确认接入。' : '默认只核验未完成的 BDMV / ISO；需要重新处理时可勾选「重新核验已完成」。',
  refreshOn:['helper:media-features']
});
if (configuration && featureBatch.visible()) featureBatch.refresh().catch(() => {});
