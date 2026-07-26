namespace Starward.Core.Hypergryph;

public static class HypergryphGameConstants
{
    public const string LauncherAppCode = "abYeZZ16BPluCFyT";

    public const string EndfieldAppCode = "6LL0KJuqHBVz33WK";

    public const string EndfieldTargetApp = "EndField";

    public const string Channel = "1";

    public const string SubChannel = "1";

    public const string EndfieldExeName = "Endfield.exe";

    public const string EndfieldInstallationDirectory = "Endfield Game";

    public const string EndfieldBackground = "ms-appx:///Assets/Image/background_endfield.jpg";

    public const string EndfieldIcon = "ms-appx:///Assets/Image/icon_endfield.png";

    public static bool IsEndfield(GameBiz gameBiz) => gameBiz.Value is GameBiz.endfield_cn;
}
