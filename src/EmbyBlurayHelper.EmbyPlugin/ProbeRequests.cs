using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace EmbyBlurayHelper.EmbyPlugin;

[Route("/BlurayHelper/Status", "GET")]
[Authenticated(Roles = "Admin")]
public sealed class BlurayHelperStatusRequest { }

[Route("/BlurayHelper/Probe", "POST")]
[Authenticated(Roles = "Admin")]
public sealed class ProbeBluRayMediaRequest
{
    public string ItemId { get; set; } = string.Empty;
    public string MediaSourceId { get; set; } = string.Empty;
    public bool RepairPlaylist { get; set; }
}

[Route("/BlurayHelper/MediaFeatures", "GET,POST")]
[Authenticated(Roles = "Admin")]
public sealed class MediaFeatureConfigurationRequest
{
    public bool Enabled { get; set; }
    public bool? ShowOriginalTrackTitles { get; set; }
}

[Route("/BlurayHelper/MediaFeatures/Probe", "POST")]
[Authenticated(Roles = "Admin")]
public sealed class ProbeMediaFeaturesRequest
{
    public string ItemId { get; set; } = string.Empty;
    public string MediaSourceId { get; set; } = string.Empty;
}

[Route("/BlurayHelper/Chapters", "GET,POST")]
[Authenticated(Roles = "Admin")]
public sealed class ChapterConfigurationRequest
{
    public bool Enabled { get; set; }
    public string ReaderPath { get; set; } = string.Empty;
}

[Route("/BlurayHelper/Chapters/Inspect", "POST")]
[Authenticated(Roles = "Admin")]
public sealed class InspectChaptersRequest
{
    public string ItemId { get; set; } = string.Empty;
    public string MediaSourceId { get; set; } = string.Empty;
}

[Route("/BlurayHelper/Chapters/Fill", "POST")]
[Authenticated(Roles = "Admin")]
public sealed class FillChaptersRequest
{
    public string ItemId { get; set; } = string.Empty;
    public string MediaSourceId { get; set; } = string.Empty;
    public string ExpectedSignature { get; set; } = string.Empty;
}
