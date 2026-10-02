namespace Rayvia.Services;

public static class XrayRelease
{
    public const string Version = "v26.3.27";
    public const string AssetName = "Xray-windows-64.zip";
    public const string DigestAssetName = AssetName + ".dgst";
    public const string ReleaseApi = "https://api.github.com/repos/XTLS/Xray-core/releases/tags/" + Version;
}
