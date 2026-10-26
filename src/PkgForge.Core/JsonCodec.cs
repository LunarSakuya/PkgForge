using System.Text.Json;

namespace PkgForge;

public static class JsonCodec
{
    public static IndexManifest ParseIndexManifest(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize(utf8Json, PkgForgeJsonContext.Default.IndexManifest);
            return manifest ?? throw new PkgException(ErrorCodes.InvalidIndex, ExitCode.Verification,
                "The index manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new PkgException(ErrorCodes.InvalidIndex, ExitCode.Verification,
                $"The index manifest is not valid JSON: {exception.Message}", exception);
        }
    }

    public static string SerializeIndexManifest(IndexManifest manifest) =>
        JsonSerializer.Serialize(manifest, PkgForgeJsonContext.Default.IndexManifest);

    public static string SerializeIndexState(IndexState state) =>
        JsonSerializer.Serialize(state, PkgForgeJsonContext.Default.IndexState);

    public static IndexState? DeserializeIndexState(string json) =>
        JsonSerializer.Deserialize(json, PkgForgeJsonContext.Default.IndexState);

    public static string SerializeVerifiedBundle(VerifiedBundle bundle) =>
        JsonSerializer.Serialize(bundle, PkgForgeJsonContext.Default.VerifiedBundle);

    public static VerifiedBundle? DeserializeVerifiedBundle(string json) =>
        JsonSerializer.Deserialize(json, PkgForgeJsonContext.Default.VerifiedBundle);

    public static string SerializeInstallManifest(InstallManifest manifest) =>
        JsonSerializer.Serialize(manifest, PkgForgeJsonContext.Default.InstallManifest);

    public static InstallManifest? DeserializeInstallManifest(string json) =>
        JsonSerializer.Deserialize(json, PkgForgeJsonContext.Default.InstallManifest);

    public static string SerializeInstallMarker(InstallMarker marker) =>
        JsonSerializer.Serialize(marker, PkgForgeJsonContext.Default.InstallMarker);

    public static string SerializeLauncherVersion(LauncherVersionJson version) =>
        JsonSerializer.Serialize(version, PkgForgeJsonContext.Default.LauncherVersionJson);

    public static InstallMarker? DeserializeInstallMarker(string json) =>
        JsonSerializer.Deserialize(json, PkgForgeJsonContext.Default.InstallMarker);
}
