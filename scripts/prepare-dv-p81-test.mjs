// Isolated, finite DV playback experiment. Does not change Helper/Emby settings.
// Requires portable FFmpeg, MKVToolNix and dovi_tool; never encodes video/audio.
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { createReadStream, createWriteStream } from 'node:fs';
import { access, mkdir, readFile, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { once } from 'node:events';

const args = process.argv.slice(2);
const options = {};
for (let i = 0; i < args.length; i += 2) {
  if (!args[i].startsWith('--') || args[i + 1] === undefined) throw new Error('Expected --name value pairs.');
  options[args[i].slice(2)] = args[i + 1];
}
for (const name of ['input', 'output', 'ffmpeg', 'ffprobe', 'mkvmerge', 'mkvextract', 'dovi']) {
  if (!options[name]) throw new Error(`Missing --${name}`);
}
const seconds = Number(options.seconds ?? 60);
const start = Number(options.start ?? 180);
if (!(seconds >= 10 && seconds <= 120) || !(start >= 0)) throw new Error('Limit test to 10–120 seconds.');
for (const name of ['input', 'ffmpeg', 'ffprobe', 'mkvmerge', 'mkvextract', 'dovi']) await access(options[name]);
const root = path.resolve(options.output);
await mkdir(root, { recursive: true });
// A new output directory is required: never overwrite an earlier experiment.
const marker = path.join(root, 'experiment.json');
try { await access(marker); throw new Error(`Experiment already exists: ${root}`); }
catch (error) { if (error.code !== 'ENOENT') throw error; }
const experiment = { createdUtc: new Date().toISOString(), input: options.input, start, seconds, steps: [], status: 'running' };
await writeFile(marker, JSON.stringify(experiment, null, 2), { flag: 'wx' });

async function run(label, executable, argv, capture = false) {
  const log = createWriteStream(path.join(root, `${label}.log`), { flags: 'wx' });
  const begin = performance.now();
  let output = '';
  const child = spawn(executable, argv, { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  const completion = once(child, 'close');
  child.stdout.on('data', chunk => { log.write(chunk); if (capture) output += chunk.toString('utf8'); });
  child.stderr.on('data', chunk => log.write(chunk));
  const [exitCode] = await completion;
  log.end();
  await once(log, 'finish');
  const step = { label, executable, arguments: argv, exitCode, wallSeconds: (performance.now() - begin) / 1000 };
  experiment.steps.push(step);
  await writeFile(marker, JSON.stringify(experiment, null, 2));
  // MKVToolNix exit 1 means a warning; keep logs but accept only clean results.
  if (exitCode !== 0) throw new Error(`${label} exited ${exitCode}; see ${label}.log`);
  console.log(`${label}: ${step.wallSeconds.toFixed(2)} s`);
  return output;
}

// Hash only the original coded base-layer picture NALs, excluding metadata and
// start-code padding. Equality proves the picture was copied, not re-encoded.
async function inspectHevc(file) {
  const hash = createHash('sha256');
  const types = {};
  let pending = Buffer.alloc(0), count = 0;
  function accept(nal) {
    while (nal.length && nal[nal.length - 1] === 0) nal = nal.subarray(0, -1);
    if (nal.length < 2) return;
    const type = (nal[0] >> 1) & 63;
    const layer = ((nal[0] & 1) << 5) | (nal[1] >> 3);
    const key = `${type}:${layer}`;
    types[key] = (types[key] ?? 0) + 1;
    if (type <= 31 && layer === 0) {
      const length = Buffer.allocUnsafe(4);
      length.writeUInt32BE(nal.length);
      hash.update(length); hash.update(nal); count++;
    }
  }
  const prefix = Buffer.from([0, 0, 1]);
  for await (const chunk of createReadStream(file, { highWaterMark: 1024 * 1024 })) {
    pending = Buffer.concat([pending, chunk]);
    let first = pending.indexOf(prefix);
    if (first < 0) { if (pending.length > 128 * 1024 * 1024) throw new Error('Invalid HEVC start codes.'); continue; }
    let current = first;
    for (;;) {
      const next = pending.indexOf(prefix, current + 3);
      if (next < 0) break;
      accept(pending.subarray(current + 3, next));
      current = next;
    }
    pending = pending.subarray(current);
    if (pending.length > 128 * 1024 * 1024) throw new Error('Oversized HEVC NAL.');
  }
  const finalStart = pending.indexOf(prefix);
  if (finalStart >= 0) accept(pending.subarray(finalStart + 3));
  return { pictureNals: count, pictureSha256: hash.digest('hex'), types };
}

try {
  const source = path.join(root, 'source-dual.mkv');
  const bl = path.join(root, 'base.hevc'), el = path.join(root, 'enhancement.hevc');
  const timestamps = path.join(root, 'timestamps.txt');
  const audioTimestamps = path.join(root, 'audio-timestamps.txt');
  const p7 = path.join(root, 'p7.hevc'), p81 = path.join(root, 'p81.hevc');
  await run('01-versions-ffmpeg', options.ffmpeg, ['-version']);
  await run('02-versions-mkvmerge', options.mkvmerge, ['--version']);
  await run('03-versions-dovi', options.dovi, ['--version']);
  // Seek at input, then copy only one finite excerpt with both BL and EL and
  // the first audio track. All variants use the same excerpt and audio packets.
  await run('04-source-excerpt', options.ffmpeg, ['-nostdin', '-hide_banner', '-loglevel', 'warning',
    '-ss', String(start), '-i', options.input, '-t', String(seconds), '-map', '0:v:0', '-map', '0:v:1',
    '-map', '0:a:0', '-c', 'copy', '-map_chapters', '-1', '-n', source]);
  const identity = JSON.parse(await run('05-source-identify', options.mkvmerge, ['-J', source], true));
  const videos = identity.tracks.filter(t => t.type === 'video');
  const audio = identity.tracks.filter(t => t.type === 'audio');
  if (videos.length !== 2 || !videos.every(t => t.codec.includes('HEVC'))) throw new Error('Expected exactly two HEVC video tracks.');
  if (audio.length !== 1) throw new Error('Expected exactly one copied audio track.');
  const fps = JSON.parse(await run('06-source-probe', options.ffprobe, ['-v', 'error', '-select_streams', 'v:0',
    '-show_entries', 'stream=r_frame_rate', '-of', 'json', source], true)).streams[0].r_frame_rate;
  if (fps !== '24000/1001' && fps !== '24/1') throw new Error(`Unverified test frame rate: ${fps}`);
  await run('07-extract', options.mkvextract, [source, 'tracks', `${videos[0].id}:${bl}`, `${videos[1].id}:${el}`,
    'timestamps_v2', `${videos[0].id}:${timestamps}`, `${audio[0].id}:${audioTimestamps}`]);
  await run('08-p7-mux', options.dovi, ['mux', '--bl', bl, '--el', el, '-o', p7]);
  await run('09-p81-convert', options.dovi, ['-m', '2', 'mux', '--bl', bl, '--el', el, '--discard', '-o', p81]);
  await run('10-p7-rpu', options.dovi, ['extract-rpu', '-i', p7, '-o', path.join(root, 'p7.rpu')]);
  await run('11-p81-rpu', options.dovi, ['extract-rpu', '-i', p81, '-o', path.join(root, 'p81.rpu')]);
  await run('12-p7-info', options.dovi, ['info', '-i', path.join(root, 'p7.rpu'), '--summary']);
  await run('13-p81-info', options.dovi, ['info', '-i', path.join(root, 'p81.rpu'), '--summary']);
  const inspections = {};
  for (const [name, file] of [['base', bl], ['p7', p7], ['p81', p81]]) inspections[name] = await inspectHevc(file);
  if (!inspections.base.pictureNals || !inspections.p81.types['62:0'] || inspections.p81.types['63:0'])
    throw new Error('Expected BL pictures + RPU and no enhancement layer in P8.1.');
  for (const name of ['p7', 'p81']) {
    if (inspections[name].pictureSha256 !== inspections.base.pictureSha256 || inspections[name].pictureNals !== inspections.base.pictureNals)
      throw new Error(`${name} base-layer picture bytes changed.`);
  }
  experiment.hevc = inspections;
  experiment.samples = [];
  for (const [name, video] of [['DV Test 01 - Original P7 FEL', p7], ['DV Test 02 - Converted P81', p81], ['DV Test 03 - Base HDR10', bl]]) {
    const directory = path.join(root, 'media', name);
    await mkdir(directory, { recursive: true });
    const file = path.join(directory, `${name}.mkv`);
    await run(`14-${name.slice(8, 10)}-mkv`, options.mkvmerge, ['-o', file, '--title', name,
      '--timestamps', `0:${timestamps}`, '--default-duration', `0:${fps}fps`, video,
      '--timestamps', `${audio[0].id}:${audioTimestamps}`, '--no-video', source]);
    const probe = JSON.parse(await run(`15-${name.slice(8, 10)}-probe`, options.ffprobe,
      ['-v', 'error', '-show_streams', '-show_format', '-of', 'json', file], true));
    experiment.samples.push({ name, file, bytes: (await stat(file)).size, probe });
  }
  experiment.status = 'ready-for-device-test';
  console.log(`READY: ${path.join(root, 'media')}`);
} catch (error) {
  experiment.status = 'failed'; experiment.error = String(error); throw error;
} finally {
  await writeFile(marker, JSON.stringify(experiment, null, 2));
}
