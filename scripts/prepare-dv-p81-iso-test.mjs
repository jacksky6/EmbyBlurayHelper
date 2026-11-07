// Finite, local-only experiment: preserve the verified P8.1 video/audio and
// author UHD Blu-ray M2TS/navigation/UDF without changing production routing.
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { createReadStream, createWriteStream } from 'node:fs';
import { access, appendFile, mkdir, readFile, stat, writeFile } from 'node:fs/promises';
import { pipeline } from 'node:stream/promises';
import path from 'node:path';

const args = process.argv.slice(2);
const options = {};
for (let i = 0; i < args.length; i += 2) {
  if (!args[i].startsWith('--') || args[i + 1] === undefined) throw new Error('Expected --name value pairs.');
  options[args[i].slice(2)] = args[i + 1];
}
for (const name of ['source', 'output', 'tsmuxer']) if (!options[name]) throw new Error(`Missing --${name}`);
const sourceRoot = path.resolve(options.source);
const original = JSON.parse(await readFile(path.join(sourceRoot, 'experiment.json'), 'utf8'));
const verified = JSON.parse(await readFile(path.join(sourceRoot, 'verification-offline.json'), 'utf8'));
if (original.status !== 'ready-for-device-test' || verified.status !== 'passed') throw new Error('Use a previously verified finite P8.1 sample.');
const source = original.samples.find(s => s.name === 'DV Test 02 - Converted P81');
if (!source || source.probe.format.duration > 120) throw new Error('P8.1 sample must be at most 120 seconds.');
const root = path.resolve(options.output);
const tsmuxer = path.resolve(options.tsmuxer);
await access(tsmuxer); await access(source.file);
await mkdir(root, { recursive: true });
const marker = path.join(root, 'experiment.json');
const experiment = { createdUtc: new Date().toISOString(), sourceExperiment: sourceRoot, source: source.file,
  sourceBytes: (await stat(source.file)).size, steps: [], status: 'preparing' };
await writeFile(marker, JSON.stringify(experiment, null, 2), { flag: 'wx' });
const ffprobe = original.steps.find(s => s.label === '06-source-probe').executable;
const ffmpeg = original.steps.find(s => s.label === '01-versions-ffmpeg').executable;
experiment.ffprobe = ffprobe; experiment.ffmpeg = ffmpeg;

async function run(label, executable, argv, capture = false) {
  const start = performance.now();
  const log = createWriteStream(path.join(root, `${label}.log`), { flags: 'wx' });
  let output = '';
  const child = spawn(executable, argv, { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  const completion = once(child, 'close');
  child.stdout.on('data', chunk => { log.write(chunk); if (capture) output += chunk.toString('utf8'); });
  child.stderr.on('data', chunk => log.write(chunk));
  const [exitCode] = await completion;
  log.end(); await once(log, 'finish');
  experiment.steps.push({ label, executable, arguments: argv, exitCode, wallSeconds: (performance.now() - start) / 1000 });
  await writeFile(marker, JSON.stringify(experiment, null, 2));
  if (exitCode !== 0) throw new Error(`${label} failed (${exitCode}); see log.`);
  console.log(`${label}: ${experiment.steps.at(-1).wallSeconds.toFixed(3)} s`);
  return output;
}

try {
  await run('01-tsmuxer-identify', tsmuxer, [source.file]);
  // tsMuxer accepts decimal fps, not FFmpeg's rational syntax. Read video
  // from the already verified Annex-B elementary stream to avoid its older
  // Matroska DV importer rewriting codec-configuration NALs.
  const elementary = path.join(root, 'p81-eos.hevc');
  await pipeline(createReadStream(path.join(sourceRoot, 'p81.hevc')), createWriteStream(elementary, { flags: 'wx' }));
  // The finite extraction ends immediately after the last RPU. Explicit EOS
  // and EOB NALs let tsMuxer flush the final picture with a proper PES PTS.
  await appendFile(elementary, Buffer.from([0, 0, 0, 1, 0x48, 1, 0x80, 0, 0, 0, 1, 0x4a, 1, 0x80]));
  const common = `V_MPEGH/ISO/HEVC, "${elementary}", fps=23.976\nA_DTS, "${source.file}", track=2, lang=eng\n`;
  // Author new CLPI/MPLS. Reusing the original disc indices after remux would
  // leave every byte offset incorrect.
  const bdMeta = path.join(root, 'p81-bluray.meta');
  await writeFile(bdMeta, 'MUXOPT --blu-ray-v3 --vbr --label="DV_P81_ISO_TEST" --custom-chapters=00:00:00.000;00:00:20.000;00:00:40.000\n' + common, { flag: 'wx' });
  const directory = path.join(root, 'authored');
  await run('02-author-uhd-folder', tsmuxer, [bdMeta, directory]);
  const native = path.join(directory, 'BDMV', 'STREAM', '00000.m2ts');
  const nativeProbe = JSON.parse(await run('03-native-m2ts-probe', ffprobe,
    ['-v', 'error', '-show_streams', '-show_format', '-of', 'json', native], true));
  const tsMeta = path.join(root, 'p81-transport.meta');
  await writeFile(tsMeta, 'MUXOPT --vbr --new-audio-pes\n' + common, { flag: 'wx' });
  // Generic transport output emits a DoVi PMT descriptor. UHD authoring mode
  // intentionally suppresses it in tsMuxer 2.7.0, so retain this control.
  const transport = path.join(root, 'p81-generic.m2ts');
  await run('04-generic-m2ts', tsmuxer, [tsMeta, transport]);
  const transportProbe = JSON.parse(await run('05-generic-m2ts-probe', ffprobe,
    ['-v', 'error', '-show_streams', '-show_format', '-of', 'json', transport], true));
  experiment.authoredDirectory = directory;
  experiment.nativeM2ts = native;
  experiment.genericM2ts = transport;
  experiment.nativeProbe = nativeProbe;
  experiment.genericProbe = transportProbe;
  for (const probe of [nativeProbe, transportProbe]) {
    const video = probe.streams.find(s => s.codec_type === 'video');
    if (video?.width !== 3840 || video?.height !== 2160 || !(Number(probe.format.duration) > 50))
      throw new Error('Muxed sample is not a valid UHD excerpt; do not publish it.');
  }
  experiment.status = 'authored-for-iso-inspection';
} catch (error) {
  experiment.status = 'failed'; experiment.error = String(error); throw error;
} finally { await writeFile(marker, JSON.stringify(experiment, null, 2)); }
