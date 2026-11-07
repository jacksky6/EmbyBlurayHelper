// Exercise the existing Emby and Helper ports, never expose a new listener.
import assert from 'node:assert/strict';
import { readFile, open, writeFile } from 'node:fs/promises';
import path from 'node:path';

const root = path.resolve(process.argv[2]);
const registration = JSON.parse(await readFile(path.join(root, 'emby-registration.json'), 'utf8'));
const settings = JSON.parse(await readFile('artifacts/app/win-x64/data/settings.json', 'utf8'));
assert.ok(new URL(settings.embyUrl).hostname === '127.0.0.1', 'Local test server only');
const headers = { 'X-Emby-Token': settings.embyApiKey, 'User-Agent': 'Infuse-Direct/8.5.9 DVHelperIsoTest' };
const report = { checkedUtc: new Date().toISOString(), checks: [], status: 'checking' };
try {
  for (const item of registration.items) {
    const source = item.MediaSources[0];
    const isIso = item.Path.toLowerCase().endsWith('.iso');
    const file = await open(item.Path, 'r');
    try {
      const size = (await file.stat()).size;
      for (const port of [8096, 8097]) {
        const base = `http://127.0.0.1:${port}/emby`;
        const metadata = await fetch(`${base}/Items?Ids=${item.Id}&Fields=MediaSources,Chapters`, { headers });
        assert.equal(metadata.status, 200);
        const shown = (await metadata.json()).Items[0].MediaSources[0];
        assert.ok(isIso ? shown.Path.toLowerCase().endsWith('.iso') : shown.Container === 'mkv', 'Unexpected DV auto-conversion');
        assert.ok(!(shown.DirectStreamUrl ?? '').includes('HelperDv='), 'P8.1 sample must not take P7 conversion path');
        const url = `${base}/Videos/${item.Id}/stream.${isIso ? 'iso' : 'mkv'}?Static=true&MediaSourceId=${encodeURIComponent(source.Id)}`;
        const head = await fetch(url, { method: 'HEAD', headers });
        assert.equal(head.status, 200); assert.equal(Number(head.headers.get('content-length')), size);
        // Include optical navigation, the first M2TS payload, middle and tail.
        for (const start of [0, 32768, 65536, 32 * 1024 * 1024, Math.floor(size / 2), size - 4096]) {
          const response = await fetch(url, { headers: { ...headers, Range: `bytes=${start}-${start + 4095}` } });
          assert.equal(response.status, 206);
          assert.equal(response.headers.get('content-range'), `bytes ${start}-${start + 4095}/${size}`);
          const actual = Buffer.from(await response.arrayBuffer());
          const expected = Buffer.alloc(4096);
          const result = await file.read(expected, 0, 4096, start);
          assert.equal(result.bytesRead, 4096); assert.deepEqual(actual, expected);
          report.checks.push({ itemId: item.Id, port, start, bytes: actual.length });
        }
      }
      console.log(`PASS ${item.Id}: ${item.Name}; unchanged source routing; HEAD and 12 HTTP Range reads byte-identical.`);
    } finally { await file.close(); }
  }
  report.status = 'passed-awaiting-infuse-device-verification';
} catch (error) { report.status = 'failed'; report.error = String(error); throw error; }
finally { await writeFile(path.join(root, 'verification-http.json'), JSON.stringify(report, null, 2)); }
