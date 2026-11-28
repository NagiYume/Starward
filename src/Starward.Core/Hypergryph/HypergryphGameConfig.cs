using System.Security.Cryptography;
using System.Text;
using Starward.Core.HoYoPlay;

namespace Starward.Core.Hypergryph;

public static class HypergryphGameConfig
{
    /// <summary>Reads the package version written by the official Arknights launcher.</summary>
    /// <exception cref="InvalidDataException">The config identifies a different game or its entry does not match.</exception>
    public static async Task<Version?> GetArknightsInstallVersionAsync(
        GameId gameId, string installPath, CancellationToken cancellationToken = default, bool allowIncompleteExecutable = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (gameId.GameBiz.Value != GameBiz.arknights_cn)
        {
            return null;
        }

        Dictionary<string, string> config;
        try
        {
            using FileStream stream = File.OpenRead(Path.Combine(installPath, "config.ini"));
            long size = stream.Length;
            if (size is 0 or > 64 * 1024 || size % 16 != 0)
            {
                return null;
            }
            byte[] encrypted = new byte[(int)size];
            await stream.ReadExactlyAsync(encrypted, cancellationToken);
            config = ReadGameSection(HypergryphLauncherClient.DecryptLauncherFile(encrypted));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return null;
        }

        if (!config.TryGetValue("appcode", out string? appCode))
        {
            return null;
        }
        HypergryphGameProfile profile = HypergryphGameConstants.GetGameProfile(gameId);
        if (appCode != profile.GameAppCode || config.GetValueOrDefault("channel") != profile.Channel
            || config.GetValueOrDefault("sub_channel") != profile.SubChannel)
        {
            throw new InvalidDataException("The Arknights installation belongs to a different game or channel.");
        }
        if (!string.Equals(config.GetValueOrDefault("entry"), profile.ExeName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The Arknights installation has an unexpected game executable.");
        }
        if (!Version.TryParse(config.GetValueOrDefault("version"), out Version? version))
        {
            return null;
        }
        string? expectedHash = config.GetValueOrDefault("entry_md5");
        if (expectedHash is not { Length: 32 } || !expectedHash.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("The Arknights executable checksum is invalid.");
        }

        try
        {
            using FileStream executable = File.OpenRead(Path.Combine(installPath, profile.ExeName));
            byte[] hash = await MD5.HashDataAsync(executable, cancellationToken);
            if (!string.Equals(Convert.ToHexString(hash), expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                if (allowIncompleteExecutable)
                {
                    return null;
                }
                throw new InvalidDataException("The Arknights executable does not match its installed config.");
            }
        }
        catch (FileNotFoundException ex)
        {
            if (allowIncompleteExecutable)
            {
                return null;
            }
            throw new InvalidDataException("The Arknights executable specified by the installed config is missing.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return version;
    }

    private static Dictionary<string, string> ReadGameSection(byte[] plaintext)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StreamReader(new MemoryStream(plaintext), Encoding.UTF8);
        bool inGameSection = false;
        while (reader.ReadLine() is string line)
        {
            line = line.Trim();
            if (line.StartsWith('['))
            {
                inGameSection = string.Equals(line, "[Game]", StringComparison.OrdinalIgnoreCase);
            }
            else if (inGameSection && !line.StartsWith(';') && !line.StartsWith('#'))
            {
                int delimiter = line.IndexOf('=');
                if (delimiter > 0)
                {
                    values[line[..delimiter].Trim()] = line[(delimiter + 1)..].Trim();
                }
            }
        }
        return values;
    }
}
