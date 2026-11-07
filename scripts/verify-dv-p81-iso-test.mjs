// Verify finite P8.1 ISO fixtures before publishing them for a device test.
import assert from 'node:assert/strict';
import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { once } from 'node:events';
import { createHash } from 'node:crypto';
import { readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';

const exec = promisify(execFile);
const root = path.resolve(process.argv[2]);
const experiment = JSON.parse(await readFile(path.join(root, 'experiment.json'), 'utf8'));
const inspection = JSON.parse(await readFile(path.join(root, 'iso-inspection.json'), 'utf8'));
const source = JSON.parse(await readFile(path.join(experiment.sourceExperiment, 'experiment.json'), 'utf8'));
const expected = source.hevc.p81;
const ffmpeg = experiment.ffmpeg, ffprobe = experiment.ffprobe;
const report = { createdUtc: new Date().toISOString(), samples: [], status: 'checking' };
async function command(file, args) {
  return (await exec(file, args, { windowsHide: true, maxBuffer: 16 * 1024 * 1024 })).stdout;
}
async function packets(file, selector) {
  return JSON.parse(await command(ffprobe, ['-v', 'quiet', '-select_streams', selector,
    '-show_packets', '-show_entries', 'packet=pts_time,dts_time,size,data_hash', '-show_data_hash', 'sha256', '-of', 'json', file])).packets;
}
async function inspectVideo(file) {
  const child = spawn(ffmpeg, ['-nostdin', '-hide_banner', '-loglevel', 'error', '-i', file,
    '-map', '0:v:0', '-c', 'copy', '-bsf:v', 'hevc_mp4toannexb', '-f', 'hevc', 'pipe:1'],
  { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  const completion = once(child, 'close');
  let stderr = '';
  child.stderr.on('data', b => { if (stderr.length < 4096) stderr += b.toString('utf8'); });
  const picture = createHash('sha256'), rpu = createHash('sha256');
  const types = {}; let pending = Buffer.alloc(0), pictures = 0, rpus = 0;
  const prefix = Buffer.from([0, 0, 1]);
  function accept(nal) {
    while (nal.length && nal.at(-1) === 0) nal = nal.subarray(0, -1);
    if (nal.length < 2) return;
    const type = nal[0] >> 1 & 63, layer = (nal[0] & 1) << 5 | nal[1] >> 3;
    types[`${type}:${layer}`] = (types[`${type}:${layer}`] ?? 0) + 1;
    const length = Buffer.alloc(4); length.writeUInt32BE(nal.length);
    if (type <= 31 && layer === 0) { picture.update(length).update(nal); pictures++; }
    if (type === 62) { rpu.update(length).update(nal); rpus++; }
  }
  for await (const chunk of child.stdout) {
    pending = Buffer.concat([pending, chunk]);
    let current = pending.indexOf(prefix);
    if (current < 0) continue;
    for (;;) {
      const next = pending.indexOf(prefix, current + 3);
      if (next < 0) break;
      accept(pending.subarray(current + 3, next)); current = next;
    }
    pending = pending.subarray(current);
    assert.ok(pending.length < 128 * 1024 * 1024);
  }
  const last = pending.indexOf(prefix);
  if (last >= 0) accept(pending.subarray(last + 3));
  const [code] = await completion;
  assert.equal(code, 0, stderr);
  return { pictures, pictureSha256: picture.digest('hex'), rpus, rpuSha256: rpu.digest('hex'), types };
}
function timing(actual, original, label) {
  assert.equal(actual.length, original.length, `${label} packet count`);
  let missing = 0, maximumDeltaMs = 0;
  const shift = Number(actual[0].pts_time) - Number(original[0].pts_time);
  for (let i = 0; i < actual.length; i++) {
    if (actual[i].pts_time === undefined) { missing++; continue; }
    maximumDeltaMs = Math.max(maximumDeltaMs, Math.abs(Number(actual[i].pts_time) - Number(original[i].pts_time) - shift) * 1000);
  }
  return { shiftSeconds: shift, maximumDeltaMs, missingPts: missing };
}
try {
  const originalVideo = await packets(experiment.source, 'v:0');
  const originalAudio = await packets(experiment.source, 'a:0');
  const originalHevc = await inspectVideo(experiment.source);
  for (const sample of inspection.samples) {
    const hevc = await inspectVideo(sample.transport);
    assert.equal(hevc.pictures, expected.pictureNals, 'Base picture NAL count');
    assert.equal(hevc.pictureSha256, expected.pictureSha256, 'Compressed BL picture bytes');
    assert.equal(hevc.rpus, expected.types['62:0'], 'RPU count');
    assert.equal(hevc.rpuSha256, originalHevc.rpuSha256, 'Actual P8.1 RPU bytes');
    assert.ok(!Object.keys(hevc.types).some(k => k === '63:0' || !k.endsWith(':0')), 'Unexpected EL NAL');
    const rawVideo = await packets(sample.transport, 'v:0'), audio = await packets(sample.transport, 'a:0');
    // tsMuxer flushes EOB as a separate 7-byte packet. Identify that exact NAL
    // by payload hash; it has no picture/RPU and requires no presentation PTS.
    const eobHash = 'SHA256:' + createHash('sha256').update(Buffer.from([0, 0, 0, 1, 0x4a, 1, 0x80])).digest('hex');
    const video = rawVideo.filter(p => !(p.size === '7' && p.data_hash === eobHash));
    assert.equal(rawVideo.length - video.length, 1, 'Expected exactly one terminal EOB packet');
    assert.equal(audio.length, originalAudio.length, 'Audio packet count');
    assert.ok(audio.every((p, i) => p.data_hash === originalAudio[i].data_hash), 'Compressed audio payload changed');
    const videoTiming = timing(video, originalVideo, 'video'), audioTiming = timing(audio, originalAudio, 'audio');
    const avShiftMs = Math.abs(videoTiming.shiftSeconds - audioTiming.shiftSeconds) * 1000;
    const probe = JSON.parse(await command(ffprobe, ['-v', 'quiet', '-show_streams', '-show_format', '-of', 'json', sample.transport]));
    const dv = probe.streams.find(s => s.codec_type === 'video').side_data_list?.find(s => s.side_data_type === 'DOVI configuration record');
    if (sample.name.includes('Signaled')) {
      assert.equal(dv.dv_profile, 8); assert.equal(dv.dv_bl_signal_compatibility_id, 1);
      assert.equal(dv.rpu_present_flag, 1); assert.equal(dv.el_present_flag, 0); assert.equal(dv.bl_present_flag, 1);
    } else assert.equal(dv, undefined, 'Native control unexpectedly has a DoVi PMT descriptor');
    const decoded = JSON.parse(await command(ffprobe, ['-v', 'quiet', '-select_streams', 'v:0', '-read_intervals', '%+0.15',
      '-show_frames', '-show_entries', 'frame=pts_time:frame_side_data', '-of', 'json', sample.transport]));
    const rpuFrames = decoded.frames.flatMap(f => f.side_data_list ?? []).filter(s => s.side_data_type === 'Dolby Vision Metadata');
    assert.ok(rpuFrames.length > 0 && rpuFrames.every(f => f.disable_residual_flag === 1 && f.nlq_method_idc === -1), 'Actual decoded P8.1 RPU');
    const row = { name: sample.name, file: sample.file, transport: sample.transport, hevc,
      videoPackets: video.length, audioPackets: audio.length, videoTiming, audioTiming, avShiftMs, dv, decodedRpuFrames: rpuFrames.length };
    report.samples.push(row);
    assert.ok(videoTiming.maximumDeltaMs <= 1 && audioTiming.maximumDeltaMs <= 1 && avShiftMs <= 1,
      `Timing mismatch: ${JSON.stringify({videoTiming, audioTiming, avShiftMs})}`);
    assert.equal(videoTiming.missingPts, 0, 'Missing video PTS');
    assert.equal(audioTiming.missingPts, 0, 'Missing audio PTS');
    console.log(`PASS ${sample.name}: ${hevc.pictures} picture NALs, ${hevc.rpus} identical P8.1 RPUs; ${audio.length} identical audio packets; maximum video PTS delta ${videoTiming.maximumDeltaMs.toFixed(3)} ms.`);
  }
  report.status = 'passed-offline-ready-for-local-registration';
} catch (error) { report.status = 'failed'; report.error = String(error); throw error; }
finally { await writeFile(path.join(root, 'verification-offline.json'), JSON.stringify(report, null, 2)); }
