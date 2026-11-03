// Isolated helper against an existing local Emby. Never changes Emby settings,
// the running helper, user playback state, or any media files.
const fs=require('node:fs'),os=require('node:os'),path=require('node:path');
const {spawn}=require('node:child_process');
const net=require('node:net');
const [executable,settingsFile,item='5']=process.argv.slice(2);
const assert=(condition,message)=>{if(!condition)throw new Error(message);};
const port=()=>new Promise(resolve=>{const server=net.createServer();server.listen(0,'127.0.0.1',()=>{const number=server.address().port;server.close(()=>resolve(number));});});
(async()=>{
  const saved=JSON.parse(fs.readFileSync(settingsFile,'utf8').replace(/^\uFEFF/,''));
  const directory=fs.mkdtempSync(path.join(os.tmpdir(),'bluray-multiclip-live-'));
  const management=await port(),proxy=await port();
  fs.writeFileSync(path.join(directory,'settings.json'),JSON.stringify({...saved,managementUrl:`http://127.0.0.1:${management}`,proxyUrl:`http://127.0.0.1:${proxy}`,enableProbeLanguagePatch:false,enableInfuseLanguagePatch:true,enableInfuseMultiClipPlayback:false}));
  const child=spawn(path.resolve(executable),['--serve','--data-dir',directory],{windowsHide:true,stdio:'ignore'});
  const headers={'X-Emby-Token':saved.embyApiKey,'User-Agent':'Infuse-Direct/8.5.9'};
  const requestPath=`/Videos/${encodeURIComponent(item)}/stream?MediaSourceId=mediasource_${encodeURIComponent(item)}&Static=true`;
  const url=`http://127.0.0.1:${proxy}`+requestPath;
  let checks=0;
  const check=(condition,message)=>{assert(condition,message);checks++;console.log('PASS '+message);};
  try{
    for(let attempt=0;;attempt++){
      try{if((await fetch(`http://127.0.0.1:${management}`)).ok)break;}catch{}
      if(attempt===100)throw new Error('Isolated helper failed to start');
      await new Promise(resolve=>setTimeout(resolve,100));
    }
    const original=await fetch(saved.embyUrl+requestPath,{method:'HEAD',headers});
    check([404,500].includes(original.status),`original Emby reproduces static-stream failure (${original.status})`);
    check((await fetch(url,{method:'HEAD',headers})).status===original.status,'default-off multi-clip feature preserves original behavior');
    const managementUrl=`http://127.0.0.1:${management}/api/`;
    const login=await fetch(managementUrl+'login',{method:'POST',headers:{'Content-Type':'application/json','X-Helper-Request':'1'},body:JSON.stringify({username:'admin',password:'123456'})});
    const cookie=login.headers.getSetCookie().map(value=>value.split(';')[0]).join('; ');
    const manage=async(endpoint,data)=>{
      const result=await fetch(managementUrl+endpoint,{method:data===undefined?'GET':'POST',headers:{Cookie:cookie,'Content-Type':'application/json','X-Helper-Request':'1'},...(data===undefined?{}:{body:JSON.stringify(data)})});
      assert(result.ok,'Isolated management request failed');return result.json();
    };
    const enabled=await manage('infuse/multiclip',{enabled:true});
    check(enabled.enabled && enabled.readerAvailable && enabled.reader==='built-in','independent switch enables the built-in reader immediately');
    let response=await fetch(url,{method:'HEAD',headers});
    check(response.status===200,'multi-clip HEAD returns 200');
    const size=Number(response.headers.get('content-length'));
    check(size>0 && response.headers.get('accept-ranges')==='bytes','real playlist length and byte-range support');
    const range=async(start,end)=>{
      const result=await fetch(url,{headers:{...headers,Range:`bytes=${start}-${end}`}});
      check(result.status===206 && result.headers.get('content-range')===`bytes ${start}-${Math.min(end,size-1)}/${size}`,'correct Range headers '+start);
      const bytes=Buffer.from(await result.arrayBuffer());
      check(bytes.length===Math.min(end,size-1)-start+1,'exact Range body '+start);
      return bytes;
    };
    const first=await range(0,499999),overlap=await range(123,250122);
    check(overlap.equals(first.subarray(123,250123)),'overlapping unaligned requests return identical patched bytes');
    check(first[4]===0x47 && first[196]===0x47,'M2TS transport packets preserved');
    if(!first.includes(Buffer.from('eng'))){
      for(const name of fs.readdirSync(path.join(directory,'logs')))console.log(fs.readFileSync(path.join(directory,'logs',name),'utf8'));
      const sections=[];
      for(let at=4;at+188<=first.length;at+=192){
        if(!(first[at+1]&0x40))continue;
        let payload=at+4;if(((first[at+3]>>4)&3)===3)payload+=1+first[at+4];
        if(payload>=at+188)continue;
        const begin=payload+1+first[payload];if(first[begin]!==2)continue;
        const size=3+((first[begin+1]&15)<<8)+first[begin+2];
        const pid=((first[at+1]&31)<<8)+first[at+2];
        const parts=[first.subarray(begin,at+188)];let capacity=parts[0].length;
        for(let next=at+192;capacity<size && next+188<=first.length;next+=192){
          if((((first[next+1]&31)<<8)+first[next+2])!==pid)continue;
          let start=next+4;if(((first[next+3]>>4)&3)===3)start+=1+first[next+4];
          parts.push(first.subarray(start,next+188));capacity+=parts.at(-1).length;
        }
        sections.push({pid,size,capacity,hex:Buffer.concat(parts).subarray(0,size).toString('hex')});if(sections.length===1)break;
      }
      console.log(JSON.stringify(sections));
    }
    check(first.includes(Buffer.from('eng')),'audio language descriptors retained in multi-clip output');
    check(first.includes(Buffer.from('jpn')),'subtitle language descriptors retained in multi-clip output');
    const source=(await (await fetch(saved.embyUrl+`/emby/Items/${encodeURIComponent(item)}/PlaybackInfo`,{headers})).json()).MediaSources[0];
    const bdmv=path.join(source.Path,'BDMV');
    // The regression movie branches at these two offsets. Read across each
    // boundary without downloading any of the preceding media.
    if(item==='5'){
      let boundary=fs.statSync(path.join(bdmv,'STREAM','00055.m2ts')).size;
      await range(boundary-100000,boundary+100000);
      boundary+=fs.statSync(path.join(bdmv,'STREAM','00056.m2ts')).size;
      await range(boundary-100000,boundary+100000);
    }
    const tail=await range(size-4096,size-1);
    response=await fetch(url,{headers:{...headers,Range:'bytes=-4096'}});
    check(response.status===206 && Buffer.from(await response.arrayBuffer()).equals(tail),'suffix ranges match the same tail bytes');
    response=await fetch(url,{headers:{...headers,Range:`bytes=${size}-`}});
    check(response.status===416 && response.headers.get('content-range')===`bytes */${size}`,'unsatisfiable range returns 416');
    response=await fetch(url,{method:'HEAD',headers:{...headers,'X-Emby-Token':'invalid-fixture'}});
    check(response.status===401 || response.status===403,'invalid token cannot read local media');
    response=await fetch(url,{method:'HEAD',headers:{...headers,'User-Agent':'ordinary-client'}});
    check(response.status===original.status,'other players keep their original behavior');
    const before=Date.now();
    response=await fetch(url,{headers});
    const reader=response.body.getReader();await reader.read();await reader.cancel();
    check(Date.now()-before<10000,'whole-title request starts promptly and can be cancelled');
    const probed=await new Promise((resolve,reject)=>{
      const probe=spawn(saved.upstreamProbePath,['-v','error','-user_agent','Infuse-Direct/8.5.9',
        '-headers',`X-Emby-Token: ${saved.embyApiKey}\r\n`,'-probesize','2000000','-analyzeduration','1000000',
        '-show_entries','format=size,duration:stream=codec_name,codec_type:stream_tags=language','-of','json',url],{windowsHide:true});
      let output='';probe.stdout.on('data',bytes=>output+=bytes);probe.stderr.resume();
      const timeout=setTimeout(()=>{probe.kill();reject(new Error('HTTP probe timed out'));},30000);
      probe.on('error',reject);probe.on('close',code=>{clearTimeout(timeout);code===0?resolve(JSON.parse(output)):reject(new Error('HTTP probe failed '+code));});
    });
    check(probed.streams.some(stream=>stream.codec_name==='hevc'),'Emby ffprobe recognizes HEVC through HTTP');
    check(probed.streams.some(stream=>stream.codec_name==='truehd'),'lossless TrueHD track survives the multi-clip reader');
    check(probed.streams.filter(stream=>stream.codec_type==='subtitle').every(stream=>stream.tags?.language),'all subtitle tracks have language tags through HTTP');
    const config=(await manage('settings')).settings;
    await manage('settings',{...config,enableInfuseLanguagePatch:false});
    const unpatched=await range(0,499999);
    check(!unpatched.equals(first) && !unpatched.includes(Buffer.from('eng')),'multi-clip playback works with language patch disabled');
    const noTools=(await manage('settings')).settings;
    await manage('settings',{...noTools,upstreamProbePath:''});
    check((await fetch(url,{method:'HEAD',headers})).status===200,'built-in playback works without any native probe or ffmpeg path');
    const raw=await range(0,499999);
    check(raw.equals(unpatched),'removing native tools does not change raw title bytes');
    if(item==='5'){
      const ffmpeg=path.join(path.dirname(saved.upstreamProbePath),process.platform==='win32'?'ffmpeg.exe':'ffmpeg');
      const boundaries=[59423846400,59474276352];
      for(const offset of [0,...boundaries.map(n=>n-6144),Math.floor((size-65536)/6144)*6144]){
        const length=Math.min(65536,size-offset);
        // libbluray byte seeks can round back to access points. Only the
        // beginning is a reference; exact Range truth comes from clip files.
        const reference=offset===0?await new Promise((resolve,reject)=>{
          const child=spawn(ffmpeg,['-hide_banner','-v','error','-nostdin','-playlist','801','-f','data','-raw_packet_size','6144',
            '-skip_initial_bytes',String(offset),'-i','bluray:'+source.Path,'-map','0:0','-c','copy','-f','data','-flush_packets','1','pipe:1'],{windowsHide:true});
          let chunks=[],count=0,errors='';
          const timeout=setTimeout(()=>{child.kill();reject(new Error('Native reference reader timed out'));},15000);
          child.stderr.on('data',b=>errors=(errors+b).slice(-2000));
          child.stdout.on('data',b=>{const part=b.subarray(0,length-count);chunks.push(part);count+=part.length;if(count===length)child.kill();});
          child.on('error',reject);child.on('close',()=>{clearTimeout(timeout);count===length?resolve(Buffer.concat(chunks)):reject(new Error('Native reference failed: '+errors));});
        }):null;
        const managed=await range(offset,offset+length-1);
        const files=['00055','00056','00058'].map(id=>path.join(bdmv,'STREAM',id+'.m2ts'));
        let remaining=length,position=offset;const originals=[];
        for(const filename of files){const size=fs.statSync(filename).size;if(position>=size){position-=size;continue;}
          const chunk=Buffer.alloc(Math.min(remaining,size-position)),handle=fs.openSync(filename,'r');
          try{assert(fs.readSync(handle,chunk,0,chunk.length,position)===chunk.length,'source read was truncated');}finally{fs.closeSync(handle);}
          originals.push(chunk);remaining-=chunk.length;position=0;if(!remaining)break;
        }
        check(managed.equals(Buffer.concat(originals)),'built-in output preserves every original packet at '+offset);
        if(!reference)continue;
        // libbluray optionally marks some HD audio core packets as null PID.
        // The built-in reader retains those original packets too. Every byte
        // outside that native PID filter must be identical, including ATS,
        // payloads and file positions at the start of the title.
        let same=true;
        for(let at=0;at<managed.length;at++)if(managed[at]!==reference[at]){
          const packet=at-at%192;
          if(![5,6].includes(at%192) || reference[packet+4]!==0x47 ||
             ((reference[packet+5]&31)<<8|reference[packet+6])!==0x1fff){same=false;break;}
        }
        check(same,'libbluray confirms packet layout and media payloads at '+offset);
      }
    }
    await manage('infuse/multiclip',{enabled:false});
    check((await fetch(url,{method:'HEAD',headers})).status===original.status,'disabling multi-clip restores the upstream response immediately');
    console.log(`Passed ${checks} live multi-clip checks; ${size} byte title served on demand without output media files.`);
  }finally{
    child.kill();await new Promise(resolve=>child.exitCode!==null?resolve():child.once('exit',resolve));
    fs.rmSync(directory,{recursive:true,force:true});
  }
})().catch(error=>{console.error(error);process.exitCode=1;});
