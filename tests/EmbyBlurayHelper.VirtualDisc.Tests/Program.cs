using DiscUtils.Udf;
using EmbyBlurayHelper.Proxy.Chapters;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

if(args.Length>0 && args[0]!="--write-fixture")
{
    string sourceJson;
    if(args[0]=="--local")
    {
        using var settings=JsonDocument.Parse(File.ReadAllText("artifacts/app/win-x64/data/settings.json"));
        using var client=new HttpClient();client.DefaultRequestHeaders.Add("X-Emby-Token",settings.RootElement.GetProperty("embyApiKey").GetString());
        using var response=JsonDocument.Parse(await client.GetStringAsync(settings.RootElement.GetProperty("embyUrl").GetString()+"/emby/Items?Ids="+(args.Length>1?args[1]:"12")+"&Fields=MediaSources,Chapters"));
        sourceJson=response.RootElement.GetProperty("Items")[0].GetProperty("MediaSources")[0].GetRawText();
    }
    else sourceJson=File.ReadAllText(args[0]);
    using var doc=JsonDocument.Parse(sourceJson);
    var p=DiscChapterPresentation.Create(doc.RootElement)??throw new Exception("No presentation.");
    using(var header=p.Image.Open())CheckUdf250(header);
    Console.WriteLine(CheckMediaOffset(p.Image));
    using var stream=p.Image.Open();using var udf=new UdfReader(stream);
    var streams=udf.GetFiles(@"\BDMV\STREAM");
    Console.WriteLine(JsonSerializer.Serialize(new{Entries=udf.GetFileSystemEntries(@"\"),Bdmv=udf.GetFileSystemEntries(@"\BDMV"),Streams=streams,Known=udf.FileExists(@"\BDMV\STREAM\00294.m2ts")}));
    foreach(var file in streams){using var payload=udf.OpenFile(file,FileMode.Open,FileAccess.Read);var packet=new byte[192];payload.ReadExactly(packet);if(packet[4]!=0x47)throw new Exception("Bad stream mapping.");}
    if(streams.Length==0)throw new Exception("No streams enumerated.");
    ChapterRoundTripCheck.Run(udf,doc.RootElement,p);
    Console.WriteLine(JsonSerializer.Serialize(new{p.Signature,p.Playlist,p.ChapterCount,p.Image.Length,p.Image.MetadataBytes}));
    if(args.Contains("--native"))await NativePreviewCheck.Run(p,Path.GetFullPath("../embyserver-win-x64-4.10.2.0/ffprobe.exe"));
    if(args.Contains("--generic"))await GenericProbeCheck.Run(p,args.FirstOrDefault(a=>a.StartsWith("--ffprobe="))?["--ffprobe=".Length..]??Path.GetFullPath("../embyserver-win-x64-4.10.2.0/ffprobe.exe"));
    if(args.Contains("--remote"))await RemoteDiscCheck.Run(doc.RootElement,p,args.Length>1?args[1]:"12");
    return;
}

var bytes=Enumerable.Range(0,10000).Select(i=>(byte)i).ToArray();
var image=VirtualUdfImage.Create([
    new("BDMV/index.bdmv",null,bytes.Length,bytes),
    new("BDMV/PLAYLIST/00040.mpls",null,6,"MPLS01"u8.ToArray()),
    new("BDMV/STREAM/00294.m2ts",null,bytes.Length,bytes)]);
if(args.Length==2 && args[0]=="--write-fixture")
{
    using var input=image.Open();
    using var output=File.Create(args[1]);
    input.CopyTo(output);
    Console.WriteLine($"Wrote small UDF reader fixture: {Path.GetFullPath(args[1])} ({image.Length} bytes).");
}
using(var stream=image.Open())
{
    CheckUdf250(stream);stream.Position=0;
    using var udf=new UdfReader(stream);
    if(!udf.FileExists(@"BDMV\PLAYLIST\00040.mpls"))throw new Exception("UDF navigation missing.");
    using var file=udf.OpenFile(@"BDMV\STREAM\00294.m2ts",FileMode.Open,FileAccess.Read);
    var actual=new byte[bytes.Length];file.ReadExactly(actual);
    if(!actual.SequenceEqual(bytes))throw new Exception("UDF payload differs.");
    Console.WriteLine($"PASS: UDF navigation and payload; image {image.Length}, metadata {image.MetadataBytes} bytes.");
}
using(var large=VirtualUdfImage.Create([new("BDMV/STREAM/large.m2ts","unused-unit-fixture",5L*1024*1024*1024)]).Open())
using(var udf=new UdfReader(large))
{
    if(udf.GetFileLength(@"\BDMV\STREAM\large.m2ts")!=5L*1024*1024*1024)throw new Exception("UDF 64-bit file length truncated.");
    Console.WriteLine("PASS: UDF file length and multiple allocation extents above 4 GiB.");
}
Console.WriteLine(CheckMediaOffset(VirtualUdfImage.Create([
    new("BDMV/index.bdmv",null,bytes.Length,bytes),new("BDMV/STREAM/00294.m2ts","unused-unit-fixture",bytes.Length)])));

static string CheckMediaOffset(VirtualUdfImage image)
{
    // Generic container probes read the image start; only UDF metadata may be there.
    var first=image.Regions.Where(r=>!r.Metadata).Select(r=>r.Offset).DefaultIfEmpty(image.Length).Min();
    if(first<VirtualUdfImage.MinimumMediaOffset)throw new Exception($"Original media starts at {first}, inside the generic probe window.");
    var head=new byte[VirtualUdfImage.MinimumMediaOffset];
    using(var stream=image.Open())stream.ReadExactly(head);
    for(var o=0;o+4+192*19<head.Length;o++)
    {
        var run=true;for(var k=0;k<20&&run;k++)run=head[o+4+192*k]==0x47;
        if(run)throw new Exception($"Image start contains an M2TS packet run at {o}.");
    }
    return $"PASS: original media starts at {first}; no M2TS packet run in the first {VirtualUdfImage.MinimumMediaOffset} bytes.";
}

static void CheckUdf250(Stream stream)
{
    var sectors=stream.Length/2048;
    var anchor=new byte[2048];stream.Position=256L*2048;stream.ReadExactly(anchor);
    var mainLength=BinaryPrimitives.ReadUInt32LittleEndian(anchor.AsSpan(16));
    var mainLocation=BinaryPrimitives.ReadUInt32LittleEndian(anchor.AsSpan(20));
    var reserveLength=BinaryPrimitives.ReadUInt32LittleEndian(anchor.AsSpan(24));
    var reserveLocation=BinaryPrimitives.ReadUInt32LittleEndian(anchor.AsSpan(28));
    if(BinaryPrimitives.ReadUInt16LittleEndian(anchor)!=2||mainLength!=16*2048||mainLocation!=32||
        reserveLength!=16*2048||reserveLocation!=sectors-288)
        throw new Exception("UDF anchor pointers do not match the primary and tail backup descriptor sequences.");

    var descriptors=new byte[2048];
    foreach(var (location,expected) in new[]{(32u,new ushort[]{1,5,6,7,8}),(reserveLocation,new ushort[]{1,5,6,7,8})})
        for(var i=0;i<expected.Length;i++)
        {
            stream.Position=((long)location+i)*2048;stream.ReadExactly(descriptors);
            if(BinaryPrimitives.ReadUInt16LittleEndian(descriptors)!=expected[i])
                throw new Exception("UDF primary or backup volume descriptor sequence is incomplete.");
            if(i==0 && (BinaryPrimitives.ReadUInt32LittleEndian(descriptors.AsSpan(64))!=1 ||
                BinaryPrimitives.ReadUInt32LittleEndian(descriptors.AsSpan(68))!=1))
                throw new Exception("UDF primary volume descriptor must declare supported character sets, not volume size.");
        }

    var partitionDescriptor=new byte[2048];stream.Position=33L*2048;stream.ReadExactly(partitionDescriptor);
    var partitionStart=BinaryPrimitives.ReadUInt32LittleEndian(partitionDescriptor.AsSpan(188));
    var partitionLength=BinaryPrimitives.ReadUInt32LittleEndian(partitionDescriptor.AsSpan(192));
    if(partitionStart!=288||partitionLength==0||partitionStart+partitionLength>reserveLocation)
        throw new Exception("UDF physical partition does not match the Blu-ray volume layout.");

    var lvd=new byte[2048];stream.Position=34L*2048;stream.ReadExactly(lvd);
    if(BinaryPrimitives.ReadUInt16LittleEndian(lvd)!=6||BinaryPrimitives.ReadUInt16LittleEndian(lvd.AsSpan(240))!=0x0250||
        BinaryPrimitives.ReadUInt32LittleEndian(lvd.AsSpan(432))!=16*2048||BinaryPrimitives.ReadUInt32LittleEndian(lvd.AsSpan(436))!=64||
        BinaryPrimitives.ReadUInt32LittleEndian(lvd.AsSpan(264))!=70||BinaryPrimitives.ReadUInt32LittleEndian(lvd.AsSpan(268))!=2||
        !Encoding.ASCII.GetString(lvd,451,23).Equals("*UDF Metadata Partition",StringComparison.Ordinal))
        throw new Exception("Virtual disc is missing its UDF 2.50 metadata partition map.");
    var metadataLocation=BinaryPrimitives.ReadUInt32LittleEndian(lvd.AsSpan(486));
    var mirrorLocation=BinaryPrimitives.ReadUInt32LittleEndian(lvd.AsSpan(490));
    var entry=new byte[2048];stream.Position=((long)partitionStart+metadataLocation)*2048;stream.ReadExactly(entry);
    var metadataLength=BinaryPrimitives.ReadUInt64LittleEndian(entry.AsSpan(56));
    if(BinaryPrimitives.ReadUInt16LittleEndian(entry)!=266||entry[27]!=250||metadataLength==0)
        throw new Exception("UDF metadata map does not point to a valid metadata file entry.");
    stream.Position=((long)partitionStart+mirrorLocation)*2048;stream.ReadExactly(entry);
    if(BinaryPrimitives.ReadUInt16LittleEndian(entry)!=266||entry[27]!=251)
        throw new Exception("UDF metadata mirror map does not point to a valid mirror entry.");
    var integrity=new byte[2048];stream.Position=64L*2048;stream.ReadExactly(integrity);
    if(BinaryPrimitives.ReadUInt16LittleEndian(integrity)!=9||BinaryPrimitives.ReadUInt32LittleEndian(integrity.AsSpan(28))!=1||
        BinaryPrimitives.ReadUInt32LittleEndian(integrity.AsSpan(72))!=2||
        metadataLength!=(ulong)BinaryPrimitives.ReadUInt32LittleEndian(integrity.AsSpan(92))*2048)
        throw new Exception("UDF volume integrity descriptor does not describe a closed 2-partition volume.");
}
