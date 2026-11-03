// Read-only experiment: verify that Emby's bundled libbluray can expose playlist
// bytes with efficient seeks, without demuxing, remuxing, or writing media files.
const fs = require('node:fs');
const path = require('node:path');
const {spawn} = require('node:child_process');
const {createHash} = require('node:crypto');
const [ffmpeg,root,playlist='801'] = process.argv.slice(2);
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
async function read(offset,length) {
  const started=Date.now();
  const child=spawn(ffmpeg,['-hide_banner','-v','error','-nostdin','-playlist',playlist,
    '-f','data','-skip_initial_bytes',String(offset),'-i','bluray:'+root,
    '-map','0:0','-c','copy','-f','data','pipe:1'],{windowsHide:true});
  const chunks=[];let count=0,stderr='';
  const finished=new Promise((resolve,reject)=>{child.on('error',reject);child.on('close',resolve);});
  const timeout=setTimeout(()=>child.kill(),30000);
  child.stderr.on('data',bytes=>stderr=(stderr+bytes).slice(-4000));
  child.stdout.on('data',bytes=>{
    if(count<length){const part=bytes.subarray(0,length-count);chunks.push(part);count+=part.length;}
    if(count===length)child.kill();
  });
  await finished;clearTimeout(timeout);
  if(count!==length)throw new Error(`Read ${offset}+${length}: received ${count}: ${stderr}`);
  const data=Buffer.concat(chunks);
  console.log(JSON.stringify({offset,length,milliseconds:Date.now()-started,sha256:hash(data)}));
  return data;
}
(async()=>{
  const playlistBytes=fs.readFileSync(path.join(root,'BDMV','PLAYLIST',playlist.padStart(5,'0')+'.mpls'));
  let cursor=playlistBytes.readUInt32BE(8)+10;
  const clipCount=playlistBytes.readUInt16BE(cursor-4),clips=[];
  for(let index=0;index<clipCount;index++){
    const size=playlistBytes.readUInt16BE(cursor);cursor+=2;
    clips.push({name:playlistBytes.toString('ascii',cursor,cursor+5),
      in:playlistBytes.readUInt32BE(cursor+12)/45000,out:playlistBytes.readUInt32BE(cursor+16)/45000});
    cursor+=size;
  }
  console.log(JSON.stringify({playlist,clips}));
  const first=await read(0,262144);
  const overlap=await read(6144,196608);
  if(!overlap.equals(first.subarray(6144,6144+overlap.length)))throw new Error('Overlapping seeks returned inconsistent bytes');
  const size=clips.reduce((total,clip)=>total+fs.statSync(path.join(root,'BDMV','STREAM',clip.name+'.m2ts')).size,0);
  const tail=Math.floor((size-262144)/6144)*6144;
  await read(tail,Math.min(262144,size-tail));
  console.log('Passed: playlist reads and overlapping/tail seeks; no media files written.');
})().catch(error=>{console.error(error);process.exitCode=1;});
