using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using EmbyBlurayHelper.EmbyPlugin.Updates;

namespace EmbyBlurayHelper.App;

internal sealed class BundledPluginUpdate
{
    internal byte[] Binary { get; }
    internal PluginUpdateManifest Manifest { get; }
    internal BundledPluginUpdate()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("EmbyBlurayHelper.App.PluginUpdate.dll")
            ?? throw new InvalidOperationException("The bundled Emby plugin update is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer); Binary = buffer.ToArray();
        using var pe = new PEReader(new MemoryStream(Binary));
        var metadata = pe.GetMetadataReader(); var assembly = metadata.GetAssemblyDefinition();
        if (metadata.GetString(assembly.Name) != PluginUpdateManifest.AssemblyIdentity) throw new InvalidOperationException("Unexpected bundled plugin identity.");
        Manifest = new PluginUpdateManifest { Version = assembly.Version.ToString(3), Size = Binary.LongLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(Binary)).ToLowerInvariant() };
    }
}
