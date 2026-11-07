// Verify the real prepared sample bytes, timestamps, decoded RPU and HTTP ranges.
// Credentials are read locally and sent only in headers; never printed/saved.
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { readFile, open, writeFile } from 'node:fs/promises';
import path from 'node:path';

const exec = promisify(execFile);
const root = path.resolve(process.argv[2] ?? 'artifacts/checks/dv-p81-20261107');
const offline = process.argv.includes('--offline');
const experiment = JSON.parse(await readFile(path.join(root, 'experiment.json'), 'utf8'));
const registration = offline ? null : JSON.parse(await readFile(path.join(root, 'emby-registration.json'), 'utf8'));
const settings = offline ? null : JSON.parse(await readFile('artifacts/app/win-x64/data/settings.json', 'utf8'));
if (settings) assert.equal(new URL(settings.embyUrl).hostname, '127.0.0.1', 'Local test server only.');
const ffmpeg = experiment.steps.find(s => s.label === '01-versions-ffmpeg').executable;
const ffprobe = experiment.steps.find(s => s.label === '06-source-probe').executable;
const report = { checkedUtc: new Date().toISOString(), checks: [] };
const headers = { 'X-Emby-Token': settings?.embyApiKey ?? '', 'User-Agent': 'Infuse-Direct/8 DVHelperLocalTest' };
async function command(executable, args) {
  const result = await exec(executable, args, { windowsHide: true, maxBuffer: 16 * 1024 * 1024 });
  return result.stdout;
}
async function packetTimes(file, selector) {
  const result = JSON.parse(await command(ffprobe, ['-v', 'error', '-select_streams', selector,
    '-show_packets', '-show_entries', 'packet=pts_time', '-of', 'json', file]));
  return result.packets.map(p => p.pts_time);
}
async function audioHash(file) {
  return (await command(ffmpeg, ['-nostdin', '-hide_banner', '-loglevel', 'error', '-i', file,
    '-map', '0:a:0', '-c', 'copy', '-f', 'hash', '-hash', 'sha256', '-'])).trim();
}
function compareTimes(actual, expected, toleranceSeconds, label) {
  assert.equal(actual.length, expected.length, `${label} packet count changed.`);
  let maximum = 0;
  for (let i = 0; i < expected.length; i++) {
    const delta = Math.abs(Number(actual[i]) - Number(expected[i]));
    assert.ok(Number.isFinite(delta) && delta <= toleranceSeconds + 1e-9,
      `${label} packet ${i}: ${actual[i]} vs ${expected[i]} (limit ${toleranceSeconds} s)`);
    maximum = Math.max(maximum, delta);
  }
  return maximum * 1000;
}
try {
  const source = path.join(root, 'source-dual.mkv');
  const originalAudioHash = await audioHash(source);
  const videoTimes = await packetTimes(source, 'v:0');
  const audioTimes = await packetTimes(source, 'a:0');
  for (const sample of experiment.samples) {
    assert.equal(await audioHash(sample.file), originalAudioHash, 'Compressed audio packets changed.');
    compareTimes(await packetTimes(sample.file, 'v:0'), videoTimes, 0, 'Video PTS');
    // Both tracks receive explicit original timestamps during remux.
    const audioDeltaMs = compareTimes(await packetTimes(sample.file, 'a:0'), audioTimes, 0, 'Audio PTS');
    report.maximumAudioPtsDeltaMs = Math.max(report.maximumAudioPtsDeltaMs ?? 0, audioDeltaMs);
    const video = sample.probe.streams.find(s => s.codec_type === 'video');
    const dv = video.side_data_list?.find(s => s.side_data_type === 'DOVI configuration record');
    if (sample.name.includes('P81')) {
      assert.equal(dv.dv_profile, 8); assert.equal(dv.dv_bl_signal_compatibility_id, 1);
      assert.equal(dv.rpu_present_flag, 1); assert.equal(dv.bl_present_flag, 1); assert.equal(dv.el_present_flag, 0);
      const decoded = JSON.parse(await command(ffprobe, ['-v', 'error', '-select_streams', 'v:0',
        '-read_intervals', '%+0.2', '-show_frames', '-show_entries', 'frame=pts_time:frame_side_data', '-of', 'json', sample.file]));
      const metadata = decoded.frames.flatMap(f => f.side_data_list ?? []).filter(s => s.side_data_type === 'Dolby Vision Metadata');
      assert.ok(metadata.length > 0, 'No actual decoded RPU metadata.');
      assert.ok(metadata.every(m => m.disable_residual_flag === 1 && m.nlq_method_idc === -1));
      report.decodedRpuFrames = metadata.length;
    } else if (sample.name.includes('P7 FEL')) {
      assert.equal(dv.dv_profile, 7); assert.equal(dv.el_present_flag, 1);
    } else assert.equal(dv, undefined);
    if (offline) { console.log(`PASS ${sample.name} (audio, exact PTS, DV metadata)`); continue; }
    const item = registration.items.find(i => i.Path === sample.file);
    assert.ok(item, 'Test movie was not registered with Emby.');
    const file = await open(sample.file, 'r');
    try {
      for (const base of ['http://127.0.0.1:8096', 'http://127.0.0.1:8097']) {
        const url = `${base}/emby/Videos/${item.Id}/stream.mkv?Static=true&MediaSourceId=${encodeURIComponent(item.MediaSources[0].Id)}`;
        const head = await fetch(url, { method: 'HEAD', headers });
        assert.equal(head.status, 200); assert.equal(Number(head.headers.get('content-length')), sample.bytes);
        for (const start of [0, 2048, Math.floor(sample.bytes / 2), sample.bytes - 4096]) {
          const end = start + 4095;
          const response = await fetch(url, { headers: { ...headers, Range: `bytes=${start}-${end}` } });
          assert.equal(response.status, 206);
          assert.equal(response.headers.get('content-range'), `bytes ${start}-${end}/${sample.bytes}`);
          const actual = Buffer.from(await response.arrayBuffer());
          const expected = Buffer.alloc(4096);
          const read = await file.read(expected, 0, 4096, start);
          assert.equal(read.bytesRead, 4096); assert.deepEqual(actual, expected);
          report.checks.push({ movie: item.Id, port: new URL(base).port, start, bytes: actual.length });
        }
      }
    } finally { await file.close(); }
    console.log(`PASS ${item.Id}: ${sample.name} (audio, PTS, DV flags, HEAD, overlapping/middle/end ranges)`);
  }
  report.audioHash = originalAudioHash;
  report.videoPackets = videoTimes.length;
  report.audioPackets = audioTimes.length;
  report.status = 'passed';
} catch (error) {
  report.status = 'failed'; report.error = String(error); throw error;
} finally {
  await writeFile(path.join(root, offline ? 'verification-offline.json' : 'verification.json'), JSON.stringify(report, null, 2));
}
