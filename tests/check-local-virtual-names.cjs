'use strict';
// Read-only hosted check: no settings changes, scans or playback-state writes.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const settings = JSON.parse(fs.readFileSync(path.resolve(__dirname, '../artifacts/app/win-x64/data/settings.json'), 'utf8'));
assert.equal(new URL(settings.embyUrl).hostname, '127.0.0.1');
assert.equal(new URL(settings.embyUrl).port, '8096');
const proxy = 'http://127.0.0.1:8097';
const headers = { 'X-Emby-Token': settings.embyApiKey, 'User-Agent': 'Infuse-Direct/8.5.9' };
const filename = value => value.split(/[\\/]/).filter(Boolean).at(-1);
const dispositionName = response => {
  const value = response.headers.get('content-disposition') || '';
  assert.match(value, /^inline;/, 'inline media disposition');
  assert.ok([...value].every(c => c.charCodeAt(0) >= 32 && c.charCodeAt(0) <= 126), 'ASCII-only HTTP header');
  const match = /filename\*=utf-8''([^;]+)/i.exec(value);
  assert.ok(match, 'complete UTF-8 filename supplied');
  return decodeURIComponent(match[1]);
};
(async () => {
  const results = [];
  for (const id of ['12', '1886', '17']) {
    const route = '/emby/Items?Ids=' + id + '&Fields=Path,MediaSources,Chapters';
    const originalResponse = await fetch(settings.embyUrl + route, { headers });
    assert.equal(originalResponse.status, 200);
    const original = await originalResponse.json();
    const item = original.Items[0]; const source = item.MediaSources[0];
    const wrappedResponse = await fetch(proxy + route, { headers });
    assert.equal(wrappedResponse.status, 200);
    const wrapped = await wrappedResponse.json();
    const presentedItem = wrapped.Items[0]; const presented = presentedItem.MediaSources.find(s => s.Id === source.Id);
    const feature = presented.Container === 'mkv' ? 'P81.mkv' : 'Chapters.iso';
    assert.ok(['mkv', 'blurayiso'].includes(presented.Container), 'eligible virtual format');
    const release = filename(source.Path).replace(/\.iso$/i, '');
    const expected = release + '.EmbyBlurayHelper.' + feature;
    assert.equal(filename(presented.Path), expected, 'original release/group retained in virtual path');
    assert.equal(presented.Name, expected, 'media-source name matches the virtual file');
    assert.equal(presentedItem.Name, item.Name, 'movie title unchanged');
    assert.equal(presentedItem.Path, presented.Path, 'listing and source paths agree');
    const ordinary = await fetch(proxy + route, { headers: { ...headers, 'User-Agent': 'Emby-Regression-Test' } });
    assert.deepEqual(await ordinary.json(), original, 'other clients retain the original metadata');
    let streamUrl = new URL(presented.DirectStreamUrl || '/Videos/' + id + '/stream?Static=true&MediaSourceId=' + encodeURIComponent(source.Id), proxy);
    let head = await fetch(streamUrl, { method: 'HEAD', headers, redirect: 'manual' });
    if (head.status === 307) {
      streamUrl = new URL(head.headers.get('location'), proxy);
      assert.equal(streamUrl.origin, proxy);
      head = await fetch(streamUrl, { method: 'HEAD', headers });
    }
    assert.equal(head.status, 200);
    assert.equal(dispositionName(head), expected, 'HEAD filename agrees with metadata');
    assert.equal(Number(head.headers.get('content-length')), presented.Size, 'size unchanged');
    const isMkv = presented.Container === 'mkv';
    const range = isMkv ? 'bytes=0-63' : 'bytes=32768-32774';
    const part = await fetch(streamUrl, { headers: { ...headers, Range: range } });
    assert.equal(part.status, 206);
    assert.equal(dispositionName(part), expected, 'Range response has the same complete filename');
    assert.equal(part.headers.get('etag'), head.headers.get('etag'), 'stable media validator');
    const bytes = Buffer.from(await part.arrayBuffer());
    if (isMkv) assert.equal(bytes.subarray(0, 4).toString('hex'), '1a45dfa3', 'real MKV header');
    else assert.equal(bytes.subarray(1, 6).toString('ascii'), 'BEA01', 'real UDF recognition sequence');
    results.push({ itemId: id, title: item.Name, filename: expected, format: presented.Container, head: head.status, range: part.status });
  }
  console.log(JSON.stringify({ passed: true, results }, null, 2));
})().catch(error => { console.error('Virtual filename check failed: ' + error.message); process.exitCode = 1; });
