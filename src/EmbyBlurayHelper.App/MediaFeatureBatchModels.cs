using System.Text.Json;

namespace EmbyBlurayHelper.App;

internal sealed class MediaFeaturePlan
{
    public string Id { get; set; } = "";
    public string ServerId { get; set; } = "";
    public string ConnectionFingerprint { get; set; } = "";
    public string ConnectionIdentity { get; set; } = "";
    public List<FeatureMovie> VerificationHistory { get; set; } = [];
    public string EngineSignature { get; set; } = "";
    public string Status { get; set; } = "idle";
    public string Operation { get; set; } = "scan";
    public bool ScanComplete { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string Message { get; set; } = "先检查媒体库，再选择需要补充规格的原盘。";
    public string? PersistenceWarning { get; set; }
    public int MoviesChecked { get; set; }
    public int Planned { get; set; }
    public int Processed { get; set; }
    public int Completed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public string? CurrentItemId { get; set; }
    public string? CurrentName { get; set; }
    public bool IncludeVerified { get; set; }
    public List<string> SelectedLibraryIds { get; set; } = [];
    public List<string> PlannedItemIds { get; set; } = [];
    public List<FeatureLibrary> Libraries { get; set; } = [];
    public List<FeatureMovie> Items { get; set; } = [];
}

internal sealed class FeatureLibrary
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool ScanComplete { get; set; }
    public int MoviesChecked { get; set; }
}

internal sealed class FeatureMovie
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> LibraryIds { get; set; } = [];
    public List<FeatureSource> Sources { get; set; } = [];
    public string Result { get; set; } = "";
    public string Message { get; set; } = "";
    public bool ProcessedInRun { get; set; }
    public DateTimeOffset? AttemptedAt { get; set; }
}

internal sealed class FeatureSource
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Signature { get; set; } = "";
    public DateTimeOffset? VerifiedAt { get; set; }
    public string Details { get; set; } = "";
    // Transient read-back validation input, never persisted or sent to the UI.
    [System.Text.Json.Serialization.JsonIgnore]
    public JsonElement Raw { get; set; }
}
