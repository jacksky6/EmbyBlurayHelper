using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using DiscUtils.Udf;
using EmbyBlurayHelper.Proxy;
using EmbyBlurayHelper.Proxy.Chapters;

internal static class RemoteDiscCheck
{
    internal static async Task Run(JsonElement original,DiscChapterPresentation presentation,string itemId)
    {
        using var settings=JsonDocument.Parse(File.ReadAllText("artifacts/app/win-x64/data/settings.json"));
        using var client=new HttpClient{BaseAddress=new Uri("http://127.0.0.1:8097"),Timeout=TimeSpan.FromSeconds(30)};
        client.DefaultRequestHeaders.Add("X-Emby-Token",settings.RootElement.GetProperty("embyApiKey").GetString());
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Infuse-Direct/8.5.9");
        using var playback=JsonDocument.Parse(await client.GetStringAsync("/Items/"+itemId+"/PlaybackInfo"));
        var source=playback.RootElement.GetProperty("MediaSources")[0];
        if(source.GetProperty("Container").GetString()!="blurayiso")throw new Exception("Infuse did not get ISO presentation.");
        using(var nativePlayback=JsonDocument.Parse(await client.GetStringAsync(settings.RootElement.GetProperty("embyUrl").GetString()!.TrimEnd('/')+"/Items/"+itemId+"/PlaybackInfo")))
        {
            var native=nativePlayback.RootElement.GetProperty("MediaSources")[0];
            var allowed=new HashSet<string>(["Container","Size","Path","Name","SupportsDirectPlay","SupportsDirectStream"]);
            var expectedFields=JsonNode.Parse(native.GetRawText())!.AsObject();
            var actualFields=JsonNode.Parse(source.GetRawText())!.AsObject();
            foreach(var name in allowed){expectedFields.Remove(name);actualFields.Remove(name);}
            if(!JsonNode.DeepEquals(expectedFields,actualFields))
                throw new Exception("Virtual ISO presentation changed native tracks, chapters, duration or reader hints.");
            var nativePath=native.GetProperty("Path").GetString()!;
            var named=VirtualMediaName.Chapters(nativePath);
            if(source.GetProperty("Path").GetString()!=named.Path||source.GetProperty("Name").GetString()!=named.FileName)
                throw new Exception("Virtual ISO presentation did not preserve the original release name.");
            Console.WriteLine("PASS: virtual ISO preserves native media-source fields, tracks, chapters, duration and reader hints; path separators match the source.");
        }
        var canonical="/Videos/"+itemId+"/stream?Static=true&MediaSourceId="+source.GetProperty("Id").GetString();
        using var discoveryHandler=new HttpClientHandler{AllowAutoRedirect=false};
        using var discovery=new HttpClient(discoveryHandler){BaseAddress=client.BaseAddress};
        discovery.DefaultRequestHeaders.Add("X-Emby-Token",settings.RootElement.GetProperty("embyApiKey").GetString());
        discovery.DefaultRequestHeaders.UserAgent.ParseAdd("Infuse-Direct/8.5.9");
        using var discoveryRequest=new HttpRequestMessage(HttpMethod.Head,canonical);
        using var discoveryResponse=await discovery.SendAsync(discoveryRequest,HttpCompletionOption.ResponseHeadersRead);
        var url=discoveryResponse.Headers.Location?.OriginalString;
        if(discoveryResponse.StatusCode!=HttpStatusCode.TemporaryRedirect||url is null||!url.Contains("HelperDisc="+presentation.Signature,StringComparison.Ordinal))
            throw new Exception("Canonical ISO discovery did not provide a fingerprinted stream endpoint.");
        var length=source.GetProperty("Size").GetInt64();
        if(length!=presentation.Image.Length)throw new Exception("HTTP image length differs.");
        using(var listing=JsonDocument.Parse(await client.GetStringAsync("/Items?Ids="+itemId+"&Fields=MediaSources,Path,Chapters")))
        {
            var item=listing.RootElement.GetProperty("Items")[0];
            var listedSource=item.GetProperty("MediaSources")[0];
            if(listedSource.GetProperty("Container").GetString()!="blurayiso"||item.GetProperty("Container").GetString()!="blurayiso"||item.GetProperty("Path").GetString()!=listedSource.GetProperty("Path").GetString())throw new Exception("Library listing and playback source disagree about virtual ISO format or path.");
        }
        // Library syncs request varying fields. Every shape that names the
        // format must name the same ISO file the stream route serves.
        foreach(var fields in new[]{"Path,Container","Path,MediaSources","Path,MediaStreams","Path,Container,Chapters"})
        using(var listing=JsonDocument.Parse(await client.GetStringAsync("/Items?Ids="+itemId+"&Fields="+fields)))
        {
            var item=listing.RootElement.GetProperty("Items")[0];
            var sizes=new List<long>();if(item.TryGetProperty("Size",out var size))sizes.Add(size.GetInt64());
            if(item.GetProperty("Container").GetString()!="blurayiso"||item.GetProperty("Path").GetString()!=VirtualMediaName.Chapters(original.GetProperty("Path").GetString()!).Path)
                throw new Exception("Library listing with Fields="+fields+" still advertises the BDMV folder.");
            if(item.TryGetProperty("MediaSources",out var listed))
                foreach(var s in listed.EnumerateArray()){if(s.GetProperty("Container").GetString()!="blurayiso")throw new Exception("Listed source with Fields="+fields+" is not ISO.");sizes.Add(s.GetProperty("Size").GetInt64());}
            if(sizes.Any(s=>s!=presentation.Image.Length))throw new Exception("Library listing with Fields="+fields+" reports a size other than the served image.");
        }
        Console.WriteLine("PASS: library listings with and without media sources or chapters all advertise the served virtual ISO.");
        using(var posted=await client.PostAsync("/Items/"+itemId+"/PlaybackInfo",new StringContent("{\"IsPlayback\":false}",System.Text.Encoding.UTF8,"application/json")))
        using(var json=JsonDocument.Parse(await posted.Content.ReadAsStringAsync()))
            if(json.RootElement.GetProperty("MediaSources")[0].GetProperty("Container").GetString()!="blurayiso")throw new Exception("POST playback metadata not rewritten.");
        using(var request=new HttpRequestMessage(HttpMethod.Get,"/Videos/"+itemId+"/stream?Static=true&MediaSourceId="+source.GetProperty("Id").GetString()))
        {
            request.Headers.Range=new RangeHeaderValue(16*2048,16*2048+6);using var response=await client.SendAsync(request);
            var bytes=await response.Content.ReadAsByteArrayAsync();
            if(response.StatusCode!=HttpStatusCode.PartialContent||System.Text.Encoding.ASCII.GetString(bytes,1,5)!="BEA01")throw new Exception("Infuse canonical stream URL did not return the ISO view.");
        }
        using(var handler=new HttpClientHandler{AllowAutoRedirect=false})
        using(var redirectClient=new HttpClient(handler){BaseAddress=client.BaseAddress})
        {
            redirectClient.DefaultRequestHeaders.Add("X-Emby-Token",settings.RootElement.GetProperty("embyApiKey").GetString());
            redirectClient.DefaultRequestHeaders.UserAgent.ParseAdd("Infuse-Direct/8.5.9");
            foreach(var prefix in new[]{"","/emby"})
            foreach(var method in new[]{HttpMethod.Get,HttpMethod.Head})
            {
                using var request=new HttpRequestMessage(method,prefix+"/Videos/"+itemId+"/stream?Static=true&MediaSourceId="+source.GetProperty("Id").GetString());
                request.Headers.Range=new RangeHeaderValue(16*2048,16*2048+6);
                using var response=await redirectClient.SendAsync(request);
                var location=response.Headers.Location?.OriginalString;
                if(response.StatusCode!=HttpStatusCode.TemporaryRedirect||location is null||
                    !location.StartsWith(prefix+"/Videos/"+itemId+"/stream.iso?",StringComparison.Ordinal)||
                    !location.Contains("HelperDisc="+presentation.Signature,StringComparison.Ordinal)||
                    response.Content.Headers.ContentLength!=0)
                    throw new Exception("Canonical stream must redirect to the same-origin ISO endpoint without streaming image bytes.");
            }
            using var invalid=new HttpRequestMessage(HttpMethod.Get,"/Videos/"+itemId+"/stream?Static=true&MediaSourceId="+source.GetProperty("Id").GetString()+"&HelperDisc="+new string('0',64));
            using var stale=await redirectClient.SendAsync(invalid);
            if((int)stale.StatusCode!=409||stale.Headers.Location is not null)throw new Exception("Stale fingerprint was redirected instead of rejected.");
            Console.WriteLine("PASS: GET/HEAD canonical stream redirects to explicit ISO, including /emby prefix; stale signature rejected before redirect.");
        }
        using(var request=new HttpRequestMessage(HttpMethod.Head,url))
        using(var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead))
            if(response.StatusCode!=HttpStatusCode.OK||response.Content.Headers.ContentLength!=length)throw new Exception("HEAD length invalid.");
        using(var stream=new RangeStream(client,url,length))
        using(var udf=new UdfReader(stream))
        {
            ChapterRoundTripCheck.Run(udf,original,presentation);
            var root=original.GetProperty("Path").GetString()!;
            foreach(var name in udf.GetFiles(@"\BDMV\STREAM"))
            {
                using var remote=udf.OpenFile(name,FileMode.Open,FileAccess.Read);
                using var local=File.OpenRead(Path.Combine(root,name.TrimStart('\\').Replace('\\',Path.DirectorySeparatorChar)));
                foreach(var offset in new[]{0,remote.Length/2/192*192,(remote.Length-192)/192*192})
                {
                    remote.Position=local.Position=offset;var a=new byte[192];var b=new byte[192];remote.ReadExactly(a);local.ReadExactly(b);
                    if(!a.SequenceEqual(b))throw new Exception("HTTP movie payload differs at "+offset);
                }
            }
            Console.WriteLine($"PASS: HTTP virtual UDF navigation, chapter round-trip, and original media bytes at first/middle/end; {stream.Requests} bounded ranges.");
        }
        foreach(var range in new[]{"bytes=-2048","bytes=0-0","bytes=500000-501023"})
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,url);request.Headers.TryAddWithoutValidation("Range",range);
            using var response=await client.SendAsync(request);if(response.StatusCode!=HttpStatusCode.PartialContent)throw new Exception("Valid range rejected: "+range);
        }
        foreach(var range in new[]{"bytes="+length+"-","bytes=0-1,3-4","bytes=-0"})
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,url);request.Headers.TryAddWithoutValidation("Range",range);
            using var response=await client.SendAsync(request);if((int)response.StatusCode!=416)throw new Exception("Invalid range accepted: "+range);
        }
        using(var request=new HttpRequestMessage(HttpMethod.Get,url.Replace(presentation.Signature,new string('0',64))))
        using(var response=await client.SendAsync(request))if((int)response.StatusCode!=409)throw new Exception("Stale signature accepted.");
        using(var unauthed=new HttpClient{BaseAddress=client.BaseAddress})
        using(var response=await unauthed.GetAsync(url))if(response.StatusCode!=HttpStatusCode.Unauthorized)throw new Exception("Unauthenticated image access allowed.");
        using(var request=new HttpRequestMessage(HttpMethod.Head,url))
        {
            request.Headers.Range=new RangeHeaderValue(0,99);request.Headers.IfRange=new RangeConditionHeaderValue(new EntityTagHeaderValue("\"stale\""));
            using var response=await client.SendAsync(request);if(response.StatusCode!=HttpStatusCode.OK||response.Content.Headers.ContentLength!=length)throw new Exception("If-Range mismatch did not return full representation.");
        }
        using(var request=new HttpRequestMessage(HttpMethod.Get,"/Items/"+itemId+"/PlaybackInfo"))
        {
            request.Headers.UserAgent.ParseAdd("Emby-Web-Test/1.0");using var response=await client.SendAsync(request);
            using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if(json.RootElement.GetProperty("MediaSources")[0].GetProperty("Container").GetString()!="bluray")throw new Exception("Non-Infuse metadata was changed.");
        }
        Console.WriteLine("PASS: HEAD, suffix and bounded ranges, 416, stale fingerprint 409, authentication 401, If-Range, and non-Infuse passthrough.");
    }
    private sealed class RangeStream(HttpClient client,string url,long length):Stream
    {
        internal int Requests;
        private long position;
        public override bool CanRead=>true;public override bool CanSeek=>true;public override bool CanWrite=>false;public override long Length=>length;
        public override long Position{get=>position;set=>position=value;}
        public override int Read(byte[] buffer,int offset,int count)
        {
            count=(int)Math.Min(count,Math.Max(0,length-position));if(count==0)return 0;
            using var request=new HttpRequestMessage(HttpMethod.Get,url);request.Headers.Range=new RangeHeaderValue(position,position+count-1);
            using var response=client.Send(request);Requests++;
            if(response.StatusCode!=HttpStatusCode.PartialContent||response.Content.Headers.ContentRange?.From!=position||response.Content.Headers.ContentRange.To!=position+count-1)throw new Exception("Incorrect HTTP range response.");
            using var body=response.Content.ReadAsStream();body.ReadExactly(buffer.AsSpan(offset,count));position+=count;return count;
        }
        public override long Seek(long offset,SeekOrigin origin)=>Position=(origin switch{SeekOrigin.Begin=>0,SeekOrigin.Current=>position,SeekOrigin.End=>length,_=>throw new ArgumentOutOfRangeException(nameof(origin))})+offset;
        public override void Flush(){}public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
}
