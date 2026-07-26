using Starward.Core;
using Starward.Core.HoYoPlay;
using Starward.Core.Hypergryph;
using System;
using System.Globalization;
using System.Linq;

namespace Starward.Features.Hypergryph;

internal static class HypergryphGameMetadata
{
    public static GameInfo CreateEndfieldGameInfo()
    {
        GameId gameId = GameId.FromGameBiz(GameBiz.endfield_cn)!;
        return new GameInfo
        {
            Id = gameId.Id,
            GameBiz = gameId.GameBiz,
            DisplayStatus = GameInfoDisplayStatus.LAUNCHER_GAME_DISPLAY_STATUS_AVAILABLE,
            GameServerConfigs = [],
            Display = new GameInfoDisplay
            {
                Language = CultureInfo.CurrentUICulture.Name,
                Name = gameId.GameBiz.ToGameName(),
                Title = gameId.GameBiz.ToGameName(),
                Subtitle = "",
                Icon = new GameImage { Url = HypergryphGameConstants.EndfieldIcon },
                Logo = new GameImage { Url = HypergryphGameConstants.EndfieldIcon },
                Thumbnail = new GameImage { Url = HypergryphGameConstants.EndfieldBackground },
                Background = new GameImage { Url = HypergryphGameConstants.EndfieldBackground },
            },
        };
    }

    public static GameConfig CreateEndfieldGameConfig()
    {
        return new GameConfig
        {
            GameId = GameId.FromGameBiz(GameBiz.endfield_cn)!,
            ExeFileName = HypergryphGameConstants.EndfieldExeName,
            InstallationDir = HypergryphGameConstants.EndfieldInstallationDirectory,
            DefaultDownloadMode = DownloadMode.DOWNLOAD_MODE_FILE,
            RelatedProcesses = ["Endfield.exe", "PlatformProcess.exe"],
            RedundantFileCleanupPaths = [],
        };
    }

    public static GameBackgroundInfo CreateEndfieldBackgroundInfo()
    {
        return new GameBackgroundInfo
        {
            GameId = GameId.FromGameBiz(GameBiz.endfield_cn)!,
            Backgrounds = [],
        };
    }

    public static GameContent CreateEndfieldGameContent(HypergryphLauncherContent launcherContent)
    {
        return new GameContent
        {
            GameId = GameId.FromGameBiz(GameBiz.endfield_cn)!,
            Language = CultureInfo.CurrentUICulture.Name,
            Banners = launcherContent.Banners
                .Where(x => !string.IsNullOrWhiteSpace(x.Url))
                .Select(x => new GameBanner
                {
                    Id = x.Id,
                    Image = new GameImage
                    {
                        Url = x.Url,
                        Link = x.JumpUrl,
                        LoginStateInLink = x.NeedToken,
                    },
                }).ToList(),
            Posts = launcherContent.AnnouncementTabs
                .SelectMany(tab => tab.Announcements
                    .Where(x => !string.IsNullOrWhiteSpace(x.Content))
                    .Select(x => new GamePost
                    {
                        Id = x.Id,
                        Type = tab.TabName,
                        Title = x.Content,
                        Link = x.JumpUrl,
                        Date = FormatAnnouncementDate(x.StartTimestamp),
                    }))
                .ToList(),
            SocialMediaList = [],
        };
    }

    private static string FormatAnnouncementDate(string timestamp)
    {
        if (long.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out long milliseconds))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToLocalTime().ToString("MM/dd", CultureInfo.InvariantCulture);
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }
        return "";
    }

    public static GamePackage CreateEndfieldGamePackage(HypergryphLatestGame latest)
    {
        HypergryphPackagePart firstPart = latest.Package.Packs.FirstOrDefault() ?? new();
        return new GamePackage
        {
            GameId = GameId.FromGameBiz(GameBiz.endfield_cn)!,
            Main = new GamePackageVersion
            {
                Major = new GamePackageResource
                {
                    Version = latest.Version,
                    GamePackages =
                    [
                        new GamePackageFile
                        {
                            Url = firstPart.Url,
                            MD5 = firstPart.MD5,
                            Size = latest.Package.Packs.Sum(x => x.PackageSize),
                            DecompressedSize = latest.Package.TotalSize + latest.Package.Packs.Sum(x => x.PackageSize),
                        },
                    ],
                    AudioPackages = [],
                    ResListUrl = "",
                },
                Patches = [],
            },
            PreDownload = new GamePackageVersion { Patches = [] },
        };
    }
}
