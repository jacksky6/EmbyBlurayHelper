'use strict';
// Explicit local integration check. No ItemId is supplied, so no movie's
// play count, watched state or resume position is changed. Credentials stay
// in memory; only sanitized status/state results are printed.
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');

const settings = JSON.parse(fs.readFileSync(path.resolve(__dirname, '../artifacts/app/win-x64/data/settings.json'), 'utf8'));
const upstream = new URL(settings.embyUrl);
assert.equal(upstream.hostname, '127.0.0.1'); assert.equal(upstream.port, '8096');
const deviceId = 'helper-playstate-check-' + crypto.randomUUID();
const headers = {
  'X-Emby-Token': settings.embyApiKey,
  'X-Emby-Authorization': `MediaBrowser Client="Helper-Playstate-Check", Device="Local diagnostic", DeviceId="${deviceId}", Version="1.0"`,
  'User-Agent': 'Helper-Playstate-Check/1.0'
};
let checks = 0;
function check(condition, message) { assert.ok(condition, message); checks++; }
function request(port, route, body, chunked = false) {
  const bytes = body === undefined ? null : Buffer.from(JSON.stringify(body));
  return new Promise((resolve, reject) => {
    const req = http.request({ hostname: '127.0.0.1', port, path: route, method: bytes ? 'POST' : 'GET', headers: {
      ...headers, ...(bytes ? { 'Content-Type': 'application/json', ...(chunked ? { 'Transfer-Encoding': 'chunked' } : { 'Content-Length': bytes.length }) } : {})
    } }, response => {
      const chunks = []; let length = 0;
      response.on('error', reject);
      response.on('data', chunk => { length += chunk.length; if (length > 4 * 1024 * 1024) response.destroy(new Error('Diagnostic response too large')); else chunks.push(chunk); });
      response.on('end', () => resolve({ status: response.statusCode, body: Buffer.concat(chunks).toString('utf8') }));
    });
    req.setTimeout(15000, () => req.destroy(new Error('Diagnostic request timed out')));
    req.on('error', reject);
    if (chunked && bytes) { const half = Math.floor(bytes.length / 2); req.write(bytes.subarray(0, half)); req.end(bytes.subarray(half)); }
    else req.end(bytes);
  });
}
async function state() {
  const result = await request(8096, '/emby/Sessions');
  check(result.status === 200, 'read diagnostic session');
  const session = JSON.parse(result.body).find(s => s.DeviceId === deviceId);
  check(!!session, 'isolated diagnostic session exists');
  return session.PlayState;
}

(async () => {
  const results = [];
  const cases = [{ label: 'direct reference', port: 8096, prefix: '', chunked: false }];
  for (const prefix of ['', '/emby']) for (const chunked of [false, true])
    cases.push({ label: `proxy ${prefix || '/'} ${chunked ? 'chunked' : 'content-length'}`, port: 8097, prefix, chunked });
  for (const test of cases) {
    const id = crypto.randomUUID().replaceAll('-', '');
    const route = test.prefix + '/Sessions/Playing';
    const start = { PlaySessionId: id, CanSeek: true, PlayMethod: 'DirectStream', PositionTicks: 123456789, IsPaused: false, IsMuted: false, VolumeLevel: 37 };
    let stopped = false;
    try {
      const response = await request(test.port, route, start, test.chunked);
      check(response.status === 204, test.label + ': playback start accepted');
      const started = await state();
      check(started.PositionTicks === start.PositionTicks && started.CanSeek === true && started.IsPaused === false && started.VolumeLevel === 37,
        test.label + ': Emby read the actual start JSON fields');
      const progress = { ...start, PositionTicks: 987654321, IsPaused: true, VolumeLevel: 42 };
      const progressed = await request(test.port, route + '/Progress', progress, test.chunked);
      check(progressed.status === 204, test.label + ': progress accepted');
      const updated = await state();
      check(updated.PositionTicks === progress.PositionTicks && updated.IsPaused === true && updated.VolumeLevel === 42,
        test.label + ': Emby updated position and paused state');
      const stop = await request(test.port, route + '/Stopped', { PlaySessionId: id, PositionTicks: progress.PositionTicks }, test.chunked);
      check(stop.status === 204, test.label + ': stop accepted');
      const ended = await state();
      check(ended.PositionTicks == null, test.label + ': stop removed the diagnostic play session');
      stopped = true;
      results.push({ test: test.label, start: response.status, progress: progressed.status, stop: stop.status, actualStateVerified: true });
    } finally {
      if (!stopped) await request(8096, '/Sessions/Playing/Stopped', { PlaySessionId: id, PositionTicks: 0 }).catch(() => {});
    }
  }
  console.log(JSON.stringify({ checks, results, note: 'No library item was used or changed' }, null, 2));
})().catch(error => { console.error('Playback report check failed: ' + error.message); process.exitCode = 1; });
