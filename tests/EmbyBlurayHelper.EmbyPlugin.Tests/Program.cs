using EmbyBlurayHelper.EmbyPlugin;
using EmbyBlurayHelper.EmbyPlugin.Media;
using EmbyBlurayHelper.EmbyPlugin.MediaFeatures;
using EmbyBlurayHelper.EmbyPlugin.Chapters;
using MediaBrowser.Model.Entities;

var count = 0;
var keyframe = (NativeImageFixture)EmbyBlurayHelper.EmbyPlugin.Thumbnails.NativeKeyframeExtractorFactory.Create(typeof(NativeImageFixture), null!);
Check(keyframe.Arguments() == "-native-args -skip_frame nokey", "keyframe adapter preserves native arguments and suppresses damaged non-keyframes");
Check(keyframe.GetType() == EmbyBlurayHelper.EmbyPlugin.Thumbnails.NativeKeyframeExtractorFactory.Create(typeof(NativeImageFixture), null!).GetType(), "native adapter type is cached");
Unsupported(() => EmbyBlurayHelper.EmbyPlugin.Thumbnails.NativeKeyframeExtractorFactory.Create(typeof(string), null!), "incompatible native extractors fail clearly");
var color = new MediaStream { Type = MediaStreamType.Video, ColorTransfer = "bt709" };
Check(!EmbyBlurayHelper.EmbyPlugin.Thumbnails.DiscThumbnailColorPolicy.NeedsToneMapping(color), "SDR is not tone mapped");
color.ColorTransfer = "smpte2084";
Check(EmbyBlurayHelper.EmbyPlugin.Thumbnails.DiscThumbnailColorPolicy.NeedsToneMapping(color), "PQ HDR10 and HDR10+ require tone mapping");
color.ExtendedVideoType = ExtendedVideoTypes.DolbyVision; color.ExtendedVideoSubType = ExtendedVideoSubTypes.DoviProfile76;
Check(EmbyBlurayHelper.EmbyPlugin.Thumbnails.DiscThumbnailColorPolicy.NeedsToneMapping(color), "DV profile 7 uses compatible HDR10 base layer");
color.ExtendedVideoSubType = ExtendedVideoSubTypes.None;
Unsupported(() => EmbyBlurayHelper.EmbyPlugin.Thumbnails.DiscThumbnailColorPolicy.NeedsToneMapping(color), "unconfirmed Dolby base layer is rejected");
color.ExtendedVideoType = ExtendedVideoTypes.Hdr10; color.ColorTransfer = null;
Unsupported(() => EmbyBlurayHelper.EmbyPlugin.Thumbnails.DiscThumbnailColorPolicy.NeedsToneMapping(color), "missing HDR transfer does not silently become SDR");
color.ExtendedVideoType = ExtendedVideoTypes.HyperLogGamma;
Unsupported(() => EmbyBlurayHelper.EmbyPlugin.Thumbnails.DiscThumbnailColorPolicy.NeedsToneMapping(color), "HLG marker with missing transfer cannot become SDR");
color.ColorTransfer = "arib-std-b67";
Check(EmbyBlurayHelper.EmbyPlugin.Thumbnails.DiscThumbnailColorPolicy.NeedsToneMapping(color), "HLG also requires verified tone mapping");
color.ExtendedVideoType = ExtendedVideoTypes.Hdr10Plus; color.ColorTransfer = "smpte2084";
Check(EmbyBlurayHelper.EmbyPlugin.Thumbnails.DiscThumbnailColorPolicy.NeedsToneMapping(color), "HDR10+ must be tone mapped for JPEG");
foreach (var filter in new[] { "supertonemap=tonemap=hable", "supertonemap", "tonemap=hable" })
{ EmbyBlurayHelper.EmbyPlugin.Thumbnails.DiscThumbnailColorPolicy.ValidateFilters(true, new[] { filter }); count++; }
foreach (var filter in new[] { "format=p010", "eq=contrast=0.9:gamma=0.95:saturation=1.8", "supertonemap=tonemap=none", "supertonemap=tonemap=0", "tonemap=none" })
    Unsupported(() => EmbyBlurayHelper.EmbyPlugin.Thumbnails.DiscThumbnailColorPolicy.ValidateFilters(true, new[] { filter }), "unsafe HDR preview filter is rejected: " + filter);
EmbyBlurayHelper.EmbyPlugin.Thumbnails.DiscThumbnailColorPolicy.ValidateFilters(false, Array.Empty<string>()); count++;
var thumbnailOptions = new MediaBrowser.Model.Configuration.LibraryOptions();
Check(!EmbyBlurayHelper.EmbyPlugin.Thumbnails.DiscThumbnailPolicy.Enabled(thumbnailOptions, false), "thumbnail extraction defaults to disabled");
thumbnailOptions.EnableChapterImageExtraction = true;
Check(EmbyBlurayHelper.EmbyPlugin.Thumbnails.DiscThumbnailPolicy.Enabled(thumbnailOptions, false) &&
    !EmbyBlurayHelper.EmbyPlugin.Thumbnails.DiscThumbnailPolicy.Enabled(thumbnailOptions, true), "task-only mode cannot extract during a scan");
thumbnailOptions.ExtractChapterImagesDuringLibraryScan = true;
Check(EmbyBlurayHelper.EmbyPlugin.Thumbnails.DiscThumbnailPolicy.Enabled(thumbnailOptions, true), "scan-and-task mode enables both entry points");
thumbnailOptions.EnableChapterImageExtraction = false;
Check(!EmbyBlurayHelper.EmbyPlugin.Thumbnails.DiscThumbnailPolicy.Enabled(thumbnailOptions, true), "master Emby switch overrides scan extraction");
thumbnailOptions.ThumbnailImagesIntervalSeconds = -1;
Check(EmbyBlurayHelper.EmbyPlugin.Thumbnails.DiscThumbnailPolicy.Interval(thumbnailOptions) == -1, "chapter-marker mode stays distinct from interval previews");
thumbnailOptions.ThumbnailImagesIntervalSeconds = 10;
Check(EmbyBlurayHelper.EmbyPlugin.Thumbnails.DiscThumbnailPolicy.Interval(thumbnailOptions) == 10, "Emby preview interval is preserved");
var saved = new[] { Track(1, "zho", "自定义字幕备注"), Track(2, "und") };
var merged = StreamMerge.Merge(new[] { Track(1, "eng"), Track(2, "jpn") }, saved);
Check(merged[0].Language == "zho" && merged[0].Title == "自定义字幕备注", "known language and user title survive reprobe");
Check(merged[1].Language == "jpn", "unknown language is filled from probe");
var external = Track(2, "zho"); external.IsExternal = true; external.Path = "/media/movie.zh.srt";
merged = StreamMerge.Merge(new[] { Track(1, "eng"), Track(2, "jpn") }, new[] { external });
Check(merged.Count == 3 && merged.Single(stream => stream.IsExternal).Path == external.Path &&
      merged.Select(stream => stream.Index).Distinct().Count() == 3, "external subtitle retained without index collision");
Check(StreamMerge.Merge(new[] { Track(1, "eng") }, null).Count == 1, "unprobed source with no saved streams is supported");
Throws(() => StreamMerge.Merge(new[] { Track(1, "eng") }, saved), "missing original internal track is rejected");
var changed = Track(1, "eng"); changed.Codec = "ass";
Throws(() => StreamMerge.Merge(new[] { changed, Track(2, "eng") }, saved), "changed codec cannot silently inherit a different track's language");
Throws(() => StreamMerge.Merge(new[] { Track(1, "eng"), Track(1, "jpn") }, null), "duplicate stream indexes are rejected");
Check(MediaFacts.Runtime(null, 82_846_513_670L) == 82_846_513_670L, "probe duration fills a missing runtime");
Check(MediaFacts.Runtime(123, 456) == 123, "existing valid runtime is preserved");
Check(MediaFacts.Runtime(0, 456) == 456 && MediaFacts.Runtime(-1, 456) == 456, "zero and negative runtimes are repaired");
Check(MediaFacts.Runtime(null, 0) is null && MediaFacts.Runtime(null, -1) is null, "invalid probe duration never becomes a saved runtime");
Check(MediaFacts.Size(0, 60_674_408_448) == 60_674_408_448 && MediaFacts.Size(123, 456) == 123,
    "large ISO size is supported without replacing known size");
Check(MediaFacts.Size(0, null) == 0 && MediaFacts.Size(0, -1) == 0, "invalid probe size does not fabricate file information");
var video = new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "hevc", Width = 3840, Height = 2160, ExtendedVideoType = ExtendedVideoTypes.Hdr10 };
var audio = new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "truehd", Channels = 8, Language = "zho", Title = "用户备注", BitRate = 12345 };
var features = FeatureStreamMerge.Merge(new[] { video, audio, external }, new[] {
    new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "hevc", Width = 1920, ExtendedVideoType = ExtendedVideoTypes.DolbyVision, ExtendedVideoSubType = ExtendedVideoSubTypes.DoviProfile76, Title = "Dolby Vision Profile 7 FEL / HDR10" },
    new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "truehd", Channels = 8, Language = "eng", Title = "Dolby TrueHD Atmos", Profile = "Dolby TrueHD + Dolby Atmos", BitRate = 999 } });
Check(features[0].ExtendedVideoType == ExtendedVideoTypes.DolbyVision && features[0].ExtendedVideoSubType == ExtendedVideoSubTypes.DoviProfile76, "confirmed DV uses Emby's native subtype fields");
Check(features[0].Width == 3840 && features[0].Height == 2160, "supplementary probe cannot replace saved dimensions");
Check(features[1].Profile.Contains("Atmos") && features[1].BitRate == 12345, "only confirmed Atmos profile supplements saved audio fields");
Check(features[1].Title == "用户备注" && features[1].Language == "zho", "feature enrichment preserves user remarks and language");
Check(features.Count == 3 && ReferenceEquals(features[2], external), "feature enrichment keeps external subtitles and indices");
var ordinary = new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "ac3", Channels = 6 };
FeatureStreamMerge.Merge(new[] { ordinary }, new[] { new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "truehd", Channels = 8, Profile = "Dolby TrueHD + Dolby Atmos" } });
Check(ordinary.Profile is null, "different audio encoding at same index cannot inherit Atmos");
Check(!BluRaySourceResolver.IsBluRaySource("bluray", "/media/BDMV/movie.mkv") &&
    !BluRaySourceResolver.IsBluRaySource("bluray", "/media/BDMV/STREAM/00001.m2ts"), "plugin routes reject ordinary files even with an incorrect bluray container");
Check(BluRaySourceResolver.IsBluRaySource("bluray", "/media/movie") &&
    BluRaySourceResolver.IsBluRaySource(null, "/media/movie/BDMV/index.bdmv") &&
    BluRaySourceResolver.IsBluRaySource(null, "/media/movie.ISO"), "plugin retains folder, index and ISO support");
var intro = new ChapterInfo { Name = "intro", MarkerType = MarkerType.IntroStart, StartPositionTicks = 20_000_000 };
var credits = new ChapterInfo { Name = "credits", MarkerType = MarkerType.CreditsStart, StartPositionTicks = 850_000_000 };
long[] native = [0, 109_651_200, 177_051_867, 560_685_111];
var chapterResult = NativeChapterMerge.Merge([intro, credits], native, 900_000_000, false, 5);
Check(NativeChapterMerge.Matches(chapterResult, native), "empty list receives original timestamps");
Check(chapterResult.Contains(intro) && chapterResult.Contains(credits), "intro and credits markers are retained by identity");
var generated = Enumerable.Range(0, 4).Select(i => new ChapterInfo { Name = "Chapter " + (i + 1), MarkerType = MarkerType.Chapter, StartPositionTicks = i * TimeSpan.TicksPerMinute * 5 }).ToArray();
Check(NativeChapterMerge.Generated(generated, 5), "default five-minute chapters are identified");
Check(NativeChapterMerge.Generated(generated.Select((c,i) => new ChapterInfo { Name = "章节 " + (i + 1), StartPositionTicks = c.StartPositionTicks, MarkerType = MarkerType.Chapter }), 5), "NAS localized generated chapter names are recognized");
Check(!NativeChapterMerge.Generated(generated, 10), "another interval cannot be treated as generated");
chapterResult = NativeChapterMerge.Merge(generated.Concat([intro]).ToArray(), native, 900_000_000, false, 5);
Check(NativeChapterMerge.Matches(chapterResult, native) && chapterResult.Contains(intro), "generated list replaced and markers retained");
generated[1].Name = "自定义片段";
Throws(() => NativeChapterMerge.Merge(generated, native, 900_000_000, false, 5), "renamed generated chapter is protected");
var custom = new[] { new ChapterInfo { Name = "我的章节", StartPositionTicks = 10, MarkerType = MarkerType.Chapter } };
Throws(() => NativeChapterMerge.Merge(custom, native, 900_000_000, false, 5), "custom timestamp protected");
Check(NativeChapterMerge.Matches(NativeChapterMerge.Merge(custom, native, 900_000_000, true, 5), native), "unchanged managed list can update");
var same = native.Select((t, i) => new ChapterInfo { StartPositionTicks = t, MarkerType = MarkerType.Chapter, Name = "名字" + i, ImagePath = "image" + i }).ToArray();
chapterResult = NativeChapterMerge.Merge(same, native, 900_000_000, false, 5);
Check(chapterResult.Zip(same).All(p => ReferenceEquals(p.First, p.Second)), "matching native list retains names and image references");
Check(!NativeChapterMerge.CanManage(same, false, 5), "a matching custom list cannot silently become helper-owned");
Check(NativeChapterMerge.CanManage([], false, 5) && NativeChapterMerge.CanManage(same, true, 5), "only empty/generated or acknowledged managed lists permit replacement");
var signature = NativeChapterMerge.Signature(same); same[0].Name += " edited";
Check(signature != NativeChapterMerge.Signature(same), "manual edit invalidates management receipt");
Check(!NativeChapterMerge.Generated([new ChapterInfo { Name = "Chapter 1", MarkerType = MarkerType.Chapter }], 5), "single zero chapter cannot prove generated provenance");
foreach (long[] invalid in new long[][] { [], [0, 0], [100, 20], [-1, 20], [0, 900_000_000] })
{
    try { NativeChapterMerge.Merge([], invalid, 900_000_000, false, 5); throw new Exception("Invalid timestamps were accepted"); }
    catch (InvalidDataException) { count++; }
}
Console.WriteLine($"Passed {count} plugin media/chapters preservation checks.");

static MediaStream Track(int index, string language, string? title = null) => new()
{ Index = index, Type = MediaStreamType.Subtitle, Codec = "PGSSUB", Language = language, Title = title };
void Check(bool condition, string name) { if (!condition) throw new Exception(name); count++; }
void Throws(Action action, string name)
{ try { action(); } catch (InvalidOperationException) { count++; return; } throw new Exception(name); }
void Unsupported(Action action, string name)
{ try { action(); } catch (NotSupportedException) { count++; return; } throw new Exception(name); }

public class NativeImageFixture
{
    public NativeImageFixture(MediaBrowser.Controller.IServerApplicationHost host) { _ = host; }
    public string Arguments() => GetExtraInputArguments("input", new MediaStream());
    protected virtual string GetExtraInputArguments(string path, MediaStream stream) => "-native-args";
}
