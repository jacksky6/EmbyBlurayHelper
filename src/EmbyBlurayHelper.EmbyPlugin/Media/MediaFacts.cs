namespace EmbyBlurayHelper.EmbyPlugin.Media;

// Technical display fields only; invalid probe values never erase saved information.
internal static class MediaFacts
{
    public static long? Runtime(long? saved, long? probed) => saved > 0 ? saved : probed > 0 ? probed : saved;
    public static long Size(long saved, long? probed) => saved > 0 ? saved : probed > 0 ? probed.Value : saved;
}
