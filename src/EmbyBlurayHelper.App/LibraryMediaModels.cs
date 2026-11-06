namespace EmbyBlurayHelper.App;

internal sealed class LibraryPlan
{
    public string Id { get; set; } = "";
    public string ServerId { get; set; } = "";
    public string ConnectionFingerprint { get; set; } = "";
    public string ConnectionIdentity { get; set; } = "";
    public List<LibraryMovie> VerificationHistory { get; set; } = [];
    public string Status { get; set; } = "idle";
    public string Operation { get; set; } = "scan";
    public bool ScanComplete { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? CheckedAt { get; set; }
    public int MoviesChecked { get; set; }
    public int MoviesTotal { get; set; }
    public int Planned { get; set; }
    public int Processed { get; set; }
    public int Completed { get; set; }
    public int Partial { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public string? CurrentItemId { get; set; }
    public string? CurrentName { get; set; }
    public string Message { get; set; } = "尚未检查电影库。";
    public string? PersistenceWarning { get; set; }
    public string Scope { get; set; } = "";
    public bool IncludeVerified { get; set; }
    public List<string> SelectedLibraryIds { get; set; } = [];
    public List<string> PlannedItemIds { get; set; } = [];
    public List<LibraryGroup> Libraries { get; set; } = [];
    public List<LibraryMovie> Items { get; set; } = [];
}

internal sealed class LibraryMovie
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Format { get; set; } = "";
    public string Classification { get; set; } = "unknown";
    public int MissingAudio { get; set; }
    public int MissingSubtitles { get; set; }
    public int MissingRuntime { get; set; }
    public string Result { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTimeOffset? AttemptedAt { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public bool ProcessedInRun { get; set; }
    public int IsoReaderRevision { get; set; }
    public bool NeedsPlaylistRepair { get; set; }
    public List<string> LibraryIds { get; set; } = [];
    public List<LibrarySource> Sources { get; set; } = [];
}

internal sealed class LibraryGroup
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int MoviesChecked { get; set; }
    public int MoviesTotal { get; set; }
    public bool ScanComplete { get; set; }
}

internal sealed class LibrarySource
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsIso { get; set; }
    public long? RunTimeTicks { get; set; }
    public List<LibraryTrack> Tracks { get; set; } = [];
}

internal sealed class LibraryTrack
{
    public int Index { get; set; }
    public string Type { get; set; } = "";
    public string Language { get; set; } = "";
}
