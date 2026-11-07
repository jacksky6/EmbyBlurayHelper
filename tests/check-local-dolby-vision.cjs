'use strict';
// Explicit local integration test. Reads credentials without logging them,
// enables the feature for the test, and restores its saved state unless asked
// to leave it enabled for a subsequent Apple TV test.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { promisify } = require('node:util');
const execFile = promisify(require('node:child_process').execFile);

(async () => {
  const root = path.resolve(__dirname, '..');
  const directory = path.join(root, 'artifacts/app/win-x64/data');
  const account = JSON.parse(fs.readFileSync(path.join(directory, 'account.json'), 'utf8'));
  const saved = JSON.parse(fs.readFileSync(path.join(directory, 'settings.json'), 'utf8'));
  const itemOption = process.argv.indexOf('--item');
  const itemId = itemOption < 0 ? '12' : process.argv[itemOption + 1];
  assert.match(itemId || '', /^\d+$/, 'numeric local item ID required');
  assert.equal(new URL(saved.embyUrl).hostname, '127.0.0.1', 'local test Emby only');
  const management = 'http://127.0.0.1:8099';
  const proxy = 'http://127.0.0.1:8097';
  const login = await fetch(management + '/api/login', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Helper-Request': '1' }, body: JSON.stringify(account) });
  assert.equal(login.status, 200, 'management login');
  const cookie = login.headers.getSetCookie().map(c => c.split(';')[0]).join('; ');
  async function api(route, input) {
    const response = await fetch(management + '/api/' + route, { method: input === undefined ? 'GET' : 'POST', headers: { Cookie: cookie, 'Content-Type': 'application/json', 'X-Helper-Request': '1' }, ...(input === undefined ? {} : { body: JSON.stringify(input) }) });
    assert.equal(response.status, 200, 'management ' + route);
    return response.json();
  }
  const headers = { 'X-Emby-Token': saved.embyApiKey, 'User-Agent': 'Infuse-Direct/8.5.9' };
  let enabled = false, passed = false;
  try {
    const dependencies = await api('infuse/dolby-vision/check', {});
    console.log('Native dependency ready; ' + dependencies.version);
    const settings = (await api('settings')).settings;
    await api('settings', { ...settings, enableInfuseDolbyVision: true }); enabled = true;
    assert.equal((await api('infuse/dolby-vision')).available, true);
    const route = '/emby/Items?Ids=' + itemId + '&Fields=MediaSources,Chapters';
    const original = await (await fetch(saved.embyUrl + route, { headers })).json();
    const rewrittenResponse = await fetch(proxy + route, { headers });
    assert.equal(rewrittenResponse.status, 200);
    const rewritten = await rewrittenResponse.json();
    const source = rewritten.Items[0].MediaSources[0];
    assert.equal(source.Container, 'mkv', 'hosted metadata interception');
    const streamUrl = new URL(source.DirectStreamUrl, proxy).href;
    const head = await fetch(streamUrl, { method: 'HEAD', headers });
    assert.equal(head.status, 200); assert.equal(Number(head.headers.get('content-length')), source.Size);
    const ordinaryResponse = await fetch(proxy + route, { headers: { ...headers, 'User-Agent': 'Emby-Regression-Test' } });
    assert.deepEqual(await ordinaryResponse.json(), original, 'non-Infuse source metadata unchanged');
    const sample = await fetch(streamUrl, { headers: { ...headers, Range: 'bytes=0-63' } });
    assert.equal(sample.status, 206); assert.equal((await sample.arrayBuffer()).byteLength, 64);
    const denied = await fetch(streamUrl, { headers: { ...headers, 'X-Emby-Token': 'invalid-local-fixture', Range: 'bytes=0-63' } });
    assert.ok([401, 403].includes(denied.status), 'real hosted cache hit still authenticates');
    const ffprobe = saved.featureProbePath || saved.upstreamProbePath;
    const authArgs = ['-user_agent', headers['User-Agent'], '-headers', 'X-Emby-Token: ' + saved.embyApiKey + '\r\n'];
    const allTracks = await execFile(ffprobe, ['-v', 'error', ...authArgs, '-read_intervals', '%+#1', '-show_streams', '-of', 'json', streamUrl], { timeout: 90000, maxBuffer: 8 * 1024 * 1024, windowsHide: true });
    const tracks = JSON.parse(allTracks.stdout).streams;
    const internalStreams = original.Items[0].MediaSources[0].MediaStreams.filter(s => !s.IsExternal && ['Audio', 'Subtitle'].includes(s.Type));
    const languageTracks = tracks.filter(s => ['audio', 'subtitle'].includes(s.codec_type));
    assert.equal(languageTracks.length, internalStreams.length, 'all original audio/subtitle tracks retained');
    const aliases = { chi: 'zho', fre: 'fra', ger: 'deu', dut: 'nld', cze: 'ces', rum: 'ron', slo: 'slk', gre: 'ell', ice: 'isl', baq: 'eus', may: 'msa', per: 'fas', wel: 'cym', tib: 'bod', arm: 'hye', alb: 'sqi', bur: 'mya' };
    const normalize = language => aliases[language] || language;
    const expectedLanguages = internalStreams.filter(s => s.Language && s.Language !== 'und').map(s => s.Type.toLowerCase() + ':' + normalize(s.Language));
    const actualLanguages = languageTracks.map(s => s.codec_type + ':' + normalize(s.tags?.language));
    if (process.argv.includes('--inspect')) console.log(JSON.stringify({ original: internalStreams.map(s => ({ index: s.Index, type: s.Type, language: s.Language, pid: s.Pid })), actual: languageTracks.map(s => ({ index: s.index, type: s.codec_type, language: s.tags?.language })) }));
    for (const expected of new Set(expectedLanguages)) assert.ok(actualLanguages.filter(s => s === expected).length >= expectedLanguages.filter(s => s === expected).length, 'known source language retained: ' + expected);
    console.log('Hosted tracks: ' + tracks.filter(s => s.codec_type === 'video').length + ' video; ' + languageTracks.length + ' audio/subtitle tracks; ' + expectedLanguages.length + ' known source languages retained.');
    const probeResults = [];
    const runtimeSeconds = original.Items[0].RunTimeTicks / 1e7;
    for (const time of [0, 180, Math.min(3600, Math.floor(runtimeSeconds / 2)), Math.max(0, Math.floor(runtimeSeconds - 10))]) {
      const args = ['-v', 'error', '-user_agent', headers['User-Agent'], '-headers', 'X-Emby-Token: ' + saved.embyApiKey + '\r\n',
        '-read_intervals', time + '%+1', '-select_streams', 'v:0', '-show_packets', '-show_streams', '-show_format', '-show_chapters', '-of', 'json', streamUrl];
      const result = await execFile(ffprobe, args, { timeout: 90000, maxBuffer: 8 * 1024 * 1024, windowsHide: true });
      const parsed = JSON.parse(result.stdout);
      const video = parsed.streams.find(s => s.codec_type === 'video');
      const dovi = video.side_data_list?.find(s => s.side_data_type === 'DOVI configuration record');
      assert.equal(dovi?.dv_profile, 8, 'real HTTP video profile');
      assert.equal(dovi.dv_bl_signal_compatibility_id, 1, 'HDR10-compatible P8.1');
      assert.equal(dovi.el_present_flag, 0); assert.equal(dovi.rpu_present_flag, 1);
      assert.ok(Math.abs(Number(parsed.format.duration) - original.Items[0].RunTimeTicks / 1e7) < .01, 'full source duration');
      const first = Number(parsed.packets[0]?.pts_time);
      assert.ok(Number.isFinite(first) && first <= time + 1 && first > time - 8, 'indexed HTTP seek at ' + time);
      probeResults.push({ requestedSeconds: time, firstPacketSeconds: first, packets: parsed.packets.length, profile: dovi.dv_profile, compatibility: dovi.dv_bl_signal_compatibility_id,
        duration: Number(parsed.format.duration), chapters: parsed.chapters.length });
      console.log('Hosted ffprobe passed at ' + time + 's; first packet ' + first + 's; real Profile 8.1.');
    }
    if (process.argv.includes('--continuous')) {
      const ffmpeg = path.join(path.dirname(saved.upstreamProbePath), 'ffmpeg.exe');
      await execFile(ffmpeg, ['-hide_banner', '-loglevel', 'error', '-nostdin', ...authArgs, '-analyzeduration', '1000000', '-probesize', '2000000', '-re', '-i', streamUrl, '-t', '30', '-map', '0:v:0', '-map', '0:a:0?', '-sn', '-c', 'copy', '-f', 'null', '-'], { timeout: 60000, maxBuffer: 2 * 1024 * 1024, windowsHide: true });
      console.log('30 seconds of paced continuous HTTP playback passed with stream copy across multiple segment boundaries.');
    }
    await new Promise(resolve => setTimeout(resolve, 1500));
    const final = await api('infuse/dolby-vision');
    assert.equal(final.cache.processing, 0, 'no conversion continues after reader exit');
    console.log('On-demand cache after readers exited: ' + JSON.stringify(final.cache));
    fs.mkdirSync(path.join(root, 'artifacts/checks/dv-streaming'), { recursive: true });
    fs.writeFileSync(path.join(root, 'artifacts/checks/dv-streaming/hosted-verification-' + itemId + '.json'), JSON.stringify({ date: new Date().toISOString(), itemId, results: probeResults, audioSubtitleTracks: languageTracks.length, continuousSeconds: process.argv.includes('--continuous') ? 30 : 0, cache: final.cache, passed: true }, null, 2));
    passed = true;
  } finally {
    if (enabled && (!passed || !process.argv.includes('--leave-enabled'))) {
      const current = (await api('settings')).settings;
      await api('settings', { ...current, enableInfuseDolbyVision: !!saved.enableInfuseDolbyVision });
      console.log('Restored previous Dolby Vision switch state.');
    }
  }
})().catch(error => {
  // Child-process errors contain their command line; do not print that because
  // the intentional HTTP authentication header is part of the command.
  console.error(error.cmd ? 'Media check failed (' + (error.code ?? error.signal ?? 'timeout') + '): ' + (error.stderr || '').slice(0, 1000) : error.message);
  process.exitCode = 1;
});
