using Microsoft.Extensions.Logging;
using Snap.HPatch;
using Starward.Core.Hypergryph;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Starward.RPC.GameInstall;

internal sealed class HypergryphGameInstallService
{
    private const int DownloadConcurrency = 4;

    private readonly ILogger<HypergryphGameInstallService> _logger;
    private readonly HypergryphLauncherClient _launcherClient;
    private readonly GameInstallHelper _gameInstallHelper;

    public HypergryphGameInstallService(
        ILogger<HypergryphGameInstallService> logger,
        HypergryphLauncherClient launcherClient,
        GameInstallHelper gameInstallHelper)
    {
        _logger = logger;
        _launcherClient = launcherClient;
        _gameInstallHelper = gameInstallHelper;
    }

    public async Task ExecuteAsync(GameInstallContext context, CancellationToken cancellationToken)
    {
        if (context.Operation is not GameInstallOperation.Predownload)
        {
            foreach (string file in Directory.EnumerateFiles(context.InstallPath, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.SetAttributes(file, FileAttributes.Normal);
            }
        }
        HypergryphInstallMetadata? metadata = await HypergryphInstallMetadata.ReadAsync(context.InstallPath, cancellationToken);
        string localVersion = metadata?.Version ?? "";
        HypergryphLatestGame latest = await _launcherClient.GetLatestEndfieldAsync(localVersion, cancellationToken);

        if (string.IsNullOrWhiteSpace(localVersion) && File.Exists(Path.Combine(context.InstallPath, HypergryphGameConstants.EndfieldExeName)))
        {
            localVersion = await IsLatestOfficialInstallAsync(context, latest, cancellationToken) ? latest.Version : "0.0.0";
            if (localVersion == latest.Version)
            {
                metadata = new HypergryphInstallMetadata
                {
                    Version = latest.Version,
                    GameFilesMD5 = latest.Package.GameFilesMD5,
                };
                await metadata.WriteAsync(context.InstallPath, cancellationToken);
            }
        }

        context.LocalGameVersion = localVersion;
        context.LatestGameVersion = latest.Version;
        context.PredownloadVersion = latest.PrePatch?.TargetVersion;

        switch (context.Operation)
        {
            case GameInstallOperation.Predownload:
                await ExecutePredownloadAsync(context, latest, metadata, cancellationToken);
                break;
            case GameInstallOperation.Update when latest.Patch is not null && latest.Patch.DownloadParts.Count > 0:
                await ExecuteUpdateWithFallbackAsync(context, latest, cancellationToken);
                break;
            case GameInstallOperation.Install:
            case GameInstallOperation.Update:
            case GameInstallOperation.Repair:
                await ExecuteFullPackageAsync(context, latest, cancellationToken);
                break;
            default:
                throw new NotSupportedException($"Unsupported Hypergryph install operation: {context.Operation}.");
        }
    }

    private async Task ExecuteUpdateWithFallbackAsync(GameInstallContext context, HypergryphLatestGame latest, CancellationToken cancellationToken)
    {
        try
        {
            if (latest.Patch!.IsV2)
            {
                await ExecuteV2PatchAsync(context, latest, latest.Patch, cancellationToken);
            }
            else
            {
                await ExecuteLegacyPatchAsync(context, latest, latest.Patch, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Endfield patch failed. Falling back to the full package.");
            string downloadDirectory = HypergryphInstallMetadata.GetDownloadsDirectory(context.InstallPath);
            DeleteDownloadedFiles(latest.Patch!.DownloadParts.Select(x => Path.Combine(downloadDirectory, x.GetFileName())));
            ResetProgress(context);
            await ExecuteFullPackageAsync(context, latest, cancellationToken);
        }
    }

    private async Task ExecutePredownloadAsync(
        GameInstallContext context,
        HypergryphLatestGame latest,
        HypergryphInstallMetadata? metadata,
        CancellationToken cancellationToken)
    {
        HypergryphGamePatch patch = latest.PrePatch
            ?? throw new InvalidOperationException("There is no Endfield pre-download package currently available.");
        IReadOnlyList<HypergryphPackagePart> parts = patch.DownloadParts;
        if (parts.Count == 0)
        {
            throw new InvalidOperationException("The Endfield pre-download package is empty.");
        }

        context.DownloadMode = GameInstallDownloadMode.CompressedPackage;
        await DownloadPartsAsync(context, parts, cancellationToken);
        metadata ??= new HypergryphInstallMetadata { Version = context.LocalGameVersion ?? "" };
        metadata.PredownloadFingerprint = patch.GetFingerprint();
        await metadata.WriteAsync(context.InstallPath, cancellationToken);
    }

    private async Task ExecuteFullPackageAsync(GameInstallContext context, HypergryphLatestGame latest, CancellationToken cancellationToken)
    {
        IReadOnlyList<HypergryphPackagePart> parts = latest.Package.Packs;
        if (parts.Count == 0)
        {
            throw new InvalidDataException("The Endfield full package is empty.");
        }

        context.DownloadMode = GameInstallDownloadMode.CompressedPackage;
        IReadOnlyList<string> files = await DownloadPartsAsync(context, parts, cancellationToken);
        GameInstallFile package = CreateCompressedPackage(context.InstallPath, parts, latest.Package.TotalSize);

        context.State = GameInstallState.Decompressing;
        context.Progress_WriteTotalBytes = latest.Package.TotalSize;
        context.Progress_WriteFinishBytes = 0;
        context.Progress_Percent = 0;
        await _gameInstallHelper.ExtractCompressedPackageAsync(context, package, 1, cancellationToken);

        await InstallLauncherManifestsAsync(context.InstallPath, latest, null, cancellationToken);
        await VerifyInstalledGameAsync(context, latest, cancellationToken);
        context.Progress_WriteFinishBytes = context.Progress_WriteTotalBytes;
        await WriteInstalledMetadataAsync(context.InstallPath, latest, cancellationToken);
        DeleteDownloadedFiles(files);
    }

    private async Task ExecuteLegacyPatchAsync(
        GameInstallContext context,
        HypergryphLatestGame latest,
        HypergryphGamePatch patch,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<HypergryphPackagePart> parts = patch.DownloadParts;
        context.DownloadMode = GameInstallDownloadMode.CompressedPackage | GameInstallDownloadMode.Patch;
        IReadOnlyList<string> files = await DownloadPartsAsync(context, parts, cancellationToken);
        GameInstallFile package = CreateCompressedPackage(context.InstallPath, parts, patch.TotalSize);

        context.State = GameInstallState.Decompressing;
        context.Progress_WriteTotalBytes = patch.TotalSize;
        context.Progress_WriteFinishBytes = 0;
        context.Progress_Percent = 0;
        await _gameInstallHelper.ExtractCompressedPackageAsync(context, package, 1, cancellationToken);
        await DeleteListedFilesAsync(context.InstallPath, cancellationToken);
        await InstallLauncherManifestsAsync(context.InstallPath, latest, null, cancellationToken);
        await VerifyInstalledGameAsync(context, latest, cancellationToken);
        context.Progress_WriteFinishBytes = context.Progress_WriteTotalBytes;
        await WriteInstalledMetadataAsync(context.InstallPath, latest, cancellationToken);
        DeleteDownloadedFiles(files);
    }

    private async Task ExecuteV2PatchAsync(
        GameInstallContext context,
        HypergryphLatestGame latest,
        HypergryphGamePatch patch,
        CancellationToken cancellationToken)
    {
        context.DownloadMode = GameInstallDownloadMode.CompressedPackage | GameInstallDownloadMode.Patch;
        IReadOnlyList<string> files = await DownloadPartsAsync(context, patch.Patches, cancellationToken);
        HypergryphPatchManifest manifest = await _launcherClient.GetPatchManifestAsync(patch, cancellationToken);
        GameInstallFile package = CreateCompressedPackage(context.InstallPath, patch.Patches, patch.TotalSize);
        string stagingPath = Path.Combine(HypergryphInstallMetadata.GetMetadataDirectory(context.InstallPath), "patch-staging");

        RecreateDirectory(stagingPath);
        try
        {
            context.State = GameInstallState.Decompressing;
            context.Progress_WriteTotalBytes = patch.TotalSize;
            context.Progress_WriteFinishBytes = 0;
            context.Progress_Percent = 0;
            await _gameInstallHelper.ExtractCompressedPackageAsync(
                context,
                package,
                0.45,
                cancellationToken,
                patch.CDKey,
                stagingPath,
                applyPackageDiff: false);

            context.State = GameInstallState.Merging;
            await ApplyV2ManifestAsync(context, stagingPath, manifest, cancellationToken);
            CopyRootOverlay(stagingPath, context.InstallPath);
            await DeleteListedFilesAsync(stagingPath, context.InstallPath, cancellationToken);
            await InstallLauncherManifestsAsync(context.InstallPath, latest, patch, cancellationToken);
            await VerifyInstalledGameAsync(context, latest, cancellationToken);
            context.Progress_Percent = 1;
            context.Progress_WriteFinishBytes = context.Progress_WriteTotalBytes;
            await WriteInstalledMetadataAsync(context.InstallPath, latest, cancellationToken);
            DeleteDownloadedFiles(files);
        }
        finally
        {
            if (Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, true);
            }
        }
    }

    private async Task ApplyV2ManifestAsync(
        GameInstallContext context,
        string stagingPath,
        HypergryphPatchManifest manifest,
        CancellationToken cancellationToken)
    {
        string vfsRoot = GetSafePath(context.InstallPath, manifest.VfsBasePath);
        List<string> obsoleteFiles = [];
        int processed = 0;

        foreach (HypergryphPatchFile item in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativeTarget = Path.Combine(item.NamePath, item.Name);
            string target = GetSafePath(vfsRoot, relativeTarget);

            if (!await MatchesAsync(context, target, item.Size, item.MD5, cancellationToken))
            {
                if (!string.IsNullOrWhiteSpace(item.LocalPath))
                {
                    string source = GetSafePath(stagingPath, item.LocalPath);
                    await ReplaceFileAsync(source, target, item.Size, item.MD5, context, cancellationToken);
                }
                else
                {
                    HypergryphPatchDiff? diff = await FindApplicablePatchAsync(context, vfsRoot, item.Patches, cancellationToken);
                    if (diff is null)
                    {
                        throw new InvalidDataException($"No applicable Endfield patch was found for {relativeTarget}.");
                    }
                    string source = GetSafePath(vfsRoot, Path.Combine(diff.BaseFilePath, diff.BaseFile));
                    string diffPath = GetSafePath(stagingPath, Path.Combine("vfs_files", "vfs_patch", diff.PatchPath, diff.Patch));
                    await PatchFileAsync(context, source, diffPath, target, item.Size, item.MD5, cancellationToken);
                    if (!string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                    {
                        obsoleteFiles.Add(source);
                    }
                }
            }

            processed++;
            context.Progress_Percent = 0.45 + 0.5 * processed / Math.Max(1, manifest.Files.Count);
        }

        foreach (string path in obsoleteFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private async Task<HypergryphPatchDiff?> FindApplicablePatchAsync(
        GameInstallContext context,
        string vfsRoot,
        IEnumerable<HypergryphPatchDiff> patches,
        CancellationToken cancellationToken)
    {
        foreach (HypergryphPatchDiff patch in patches)
        {
            string source = GetSafePath(vfsRoot, Path.Combine(patch.BaseFilePath, patch.BaseFile));
            if (await MatchesAsync(context, source, patch.BaseSize, patch.BaseMD5, cancellationToken))
            {
                return patch;
            }
        }
        return null;
    }

    private async Task PatchFileAsync(
        GameInstallContext context,
        string source,
        string diff,
        string target,
        long size,
        string md5,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(diff))
        {
            throw new FileNotFoundException("The Endfield HDiff file is missing.", diff);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temporaryPath = target + ".starward_tmp";
        try
        {
            await using FileStream sourceStream = File.OpenRead(source);
            await using FileStream diffStream = File.OpenRead(diff);
            await using (FileStream targetStream = File.Create(temporaryPath))
            {
                if (!HPatch.PatchZstandard(sourceStream, diffStream, targetStream))
                {
                    throw new InvalidDataException($"Failed to apply Endfield HDiff patch to {target}.");
                }
            }
            if (!await MatchesAsync(context, temporaryPath, size, md5, cancellationToken))
            {
                throw new InvalidDataException($"The patched Endfield file does not match: {target}.");
            }
            File.Move(temporaryPath, target, true);
            Interlocked.Add(ref context.storageWriteBytes, size);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private async Task ReplaceFileAsync(
        string source,
        string target,
        long size,
        string md5,
        GameInstallContext context,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("The Endfield replacement file is missing.", source);
        }
        if (!await MatchesAsync(context, source, size, md5, cancellationToken))
        {
            throw new InvalidDataException($"The Endfield replacement file does not match: {source}.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, true);
        Interlocked.Add(ref context.storageWriteBytes, size);
    }

    private async Task<bool> MatchesAsync(GameInstallContext context, string path, long size, string md5, CancellationToken cancellationToken)
    {
        return await _gameInstallHelper.CheckFileMD5Async(context, path, size, md5, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> DownloadPartsAsync(
        GameInstallContext context,
        IReadOnlyList<HypergryphPackagePart> parts,
        CancellationToken cancellationToken)
    {
        string directory = HypergryphInstallMetadata.GetDownloadsDirectory(context.InstallPath);
        Directory.CreateDirectory(directory);
        context.State = GameInstallState.Downloading;
        context.Progress_DownloadTotalBytes = parts.Sum(x => x.PackageSize);
        context.Progress_DownloadFinishBytes = 0;
        string[] files = parts.Select(x => Path.Combine(directory, x.GetFileName())).ToArray();

        await Parallel.ForEachAsync(
            parts,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = DownloadConcurrency },
            async (part, token) =>
            {
                string path = Path.Combine(directory, part.GetFileName());
                Exception? lastException = null;
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    try
                    {
                        await _gameInstallHelper.DownloadToFileAsync(context, path, part.Url, part.PackageSize, part.MD5, token);
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lastException = ex;
                        if (attempt < 3)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(attempt + 1), token);
                        }
                    }
                }
                throw lastException ?? new IOException($"Failed to download {part.Url}.");
            });
        return files;
    }

    private static GameInstallFile CreateCompressedPackage(
        string installPath,
        IReadOnlyList<HypergryphPackagePart> parts,
        long decompressedSize)
    {
        string directory = HypergryphInstallMetadata.GetDownloadsDirectory(installPath);
        return new GameInstallFile
        {
            DownloadMode = GameInstallDownloadMode.CompressedPackage,
            Size = parts.Sum(x => x.PackageSize),
            CompressedPackages = parts.Select(x => new GameInstallCompressedPackage
            {
                FullPath = Path.Combine(directory, x.GetFileName()),
                Url = x.Url,
                MD5 = x.MD5,
                Size = x.PackageSize,
                DecompressedSize = decompressedSize,
            }).ToList(),
        };
    }

    private async Task<bool> IsLatestOfficialInstallAsync(
        GameInstallContext context,
        HypergryphLatestGame latest,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(context.InstallPath, "game_files");
        if (!File.Exists(path) || string.IsNullOrWhiteSpace(latest.Package.GameFilesMD5))
        {
            return false;
        }
        await using FileStream stream = File.OpenRead(path);
        string md5 = Convert.ToHexStringLower(await MD5.HashDataAsync(stream, cancellationToken));
        return string.Equals(md5, latest.Package.GameFilesMD5, StringComparison.OrdinalIgnoreCase);
    }

    private async Task InstallLauncherManifestsAsync(
        string installPath,
        HypergryphLatestGame latest,
        HypergryphGamePatch? v2Patch,
        CancellationToken cancellationToken)
    {
        byte[] gameFiles = await _launcherClient.GetGameFilesAsync(latest.Package, cancellationToken);
        await ReplaceManifestAsync(Path.Combine(installPath, "game_files"), gameFiles, cancellationToken);

        if (v2Patch is not null && !string.IsNullOrWhiteSpace(v2Patch.V2VerifyFilesUrl))
        {
            byte[] verifyFiles = await _launcherClient.GetV2VerifyFilesAsync(v2Patch, cancellationToken);
            await ReplaceManifestAsync(Path.Combine(installPath, "verify_files"), verifyFiles, cancellationToken);
        }
    }

    private static async Task ReplaceManifestAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        string temporaryPath = path + ".starward_tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private async Task VerifyInstalledGameAsync(
        GameInstallContext context,
        HypergryphLatestGame latest,
        CancellationToken cancellationToken)
    {
        context.State = GameInstallState.Verifying;
        string exe = Path.Combine(context.InstallPath, HypergryphGameConstants.EndfieldExeName);
        if (!File.Exists(exe))
        {
            throw new FileNotFoundException("Endfield.exe was not found after installation.", exe);
        }
        if (!await IsLatestOfficialInstallAsync(context, latest, cancellationToken))
        {
            throw new InvalidDataException("The installed Endfield game_files manifest does not match the latest package.");
        }
    }

    private static async Task WriteInstalledMetadataAsync(
        string installPath,
        HypergryphLatestGame latest,
        CancellationToken cancellationToken)
    {
        await new HypergryphInstallMetadata
        {
            Version = latest.Version,
            GameFilesMD5 = latest.Package.GameFilesMD5,
            PredownloadFingerprint = "",
        }.WriteAsync(installPath, cancellationToken);
    }

    private static void CopyRootOverlay(string stagingPath, string installPath)
    {
        string vfsFiles = Path.Combine(stagingPath, "vfs_files");
        foreach (string file in Directory.EnumerateFiles(stagingPath, "*", SearchOption.AllDirectories))
        {
            if (IsWithin(file, vfsFiles))
            {
                continue;
            }
            string relative = Path.GetRelativePath(stagingPath, file);
            if (relative is "patch.json" or "delete_files.txt" or "deletefiles.txt")
            {
                continue;
            }
            string target = GetSafePath(installPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private static async Task DeleteListedFilesAsync(string installPath, CancellationToken cancellationToken)
    {
        await DeleteListedFilesAsync(installPath, installPath, cancellationToken);
    }

    private static async Task DeleteListedFilesAsync(string listRoot, string installPath, CancellationToken cancellationToken)
    {
        foreach (string name in new[] { "delete_files.txt", "deletefiles.txt" })
        {
            string path = Path.Combine(listRoot, name);
            if (!File.Exists(path))
            {
                continue;
            }
            string text = await File.ReadAllTextAsync(path, cancellationToken);
            IEnumerable<string> files;
            try
            {
                files = JsonSerializer.Deserialize<List<string>>(text) ?? [];
            }
            catch (JsonException)
            {
                files = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            foreach (string file in files)
            {
                File.Delete(GetSafePath(installPath, file));
            }
            if (string.Equals(listRoot, installPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
            }
        }
    }

    private static string GetSafePath(string root, string relativePath)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The Hypergryph package contains an invalid path: {relativePath}.");
        }
        return fullPath;
    }

    private static bool IsWithin(string path, string directory)
    {
        string fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static void RecreateDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
        Directory.CreateDirectory(path);
    }

    private static void DeleteDownloadedFiles(IEnumerable<string> files)
    {
        foreach (string file in files)
        {
            File.Delete(file);
            File.Delete(file + "_tmp");
        }
    }

    private static void ResetProgress(GameInstallContext context)
    {
        context.Progress_DownloadTotalBytes = 0;
        context.Progress_DownloadFinishBytes = 0;
        context.Progress_ReadTotalBytes = 0;
        context.Progress_ReadFinishBytes = 0;
        context.Progress_WriteTotalBytes = 0;
        context.Progress_WriteFinishBytes = 0;
        context.Progress_Percent = 0;
    }
}
