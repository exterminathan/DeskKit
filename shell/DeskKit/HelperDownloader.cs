// Helper download on request (contract 8.3): confirm, download over HTTPS, check SHA256, extract,
// detect again. Never keeps or runs a file that fails the check.
using System.IO.Compression;
using System.Security.Cryptography;

namespace DeskKit.Shell;

public static class HelperDownloader
{
    /// <summary>Text for the confirmation prompt (step 1).</summary>
    public static string ConfirmText(HelperInfo h) =>
        $"Download {h.Name}?\n\nUsed for: {h.Purpose}\nLicence: {h.Licence}\nPage: {h.Download.Page}\n\n" +
        $"DeskKit saves it to {Path.Combine(ShellPaths.HelpersDir, h.Id)}.";

    /// <summary>Steps 2 to 5. Returns the message to show the user.</summary>
    public static async Task<string> DownloadAsync(ModuleEntry e, HelperInfo h, CancellationToken ct = default)
    {
        var d = h.Download;
        if (!d.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return $"The download address of {h.Name} is not HTTPS. Download it yourself from {d.Page}";
        var tmp = Path.Combine(Path.GetTempPath(), $"deskkit-{h.Id}-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("DeskKit/0.1");
                using var resp = await http.GetAsync(d.Url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tmp);
                await src.CopyToAsync(dst, ct);
            }

            string hash;
            await using (var fs = File.OpenRead(tmp))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
            if (!string.Equals(hash, d.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                ShellLog.Write($"helper {h.Id}: checksum mismatch, got {hash}, expected {d.Sha256}");
                return $"The download of {h.Name} did not match its checksum. Download it yourself from {d.Page}";
            }

            var target = Path.Combine(ShellPaths.HelpersDir, h.Id);
            Directory.CreateDirectory(target);
            if (d.Archive == "zip") ZipFile.ExtractToDirectory(tmp, target, overwriteFiles: true);
            else
            {
                var name = Path.GetFileName(new Uri(d.Url).LocalPath);
                File.Copy(tmp, Path.Combine(target, name), overwrite: true);
            }
            ShellLog.Write($"helper {h.Id}: installed into {target}");
            var found = Runtimes.DetectHelper(e, h);
            return found != null
                ? $"{h.Name} is ready: {found}"
                : $"{h.Name} was downloaded to {target}, but {e.DisplayName} still cannot find it. Check {d.Page}";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException
                                       or UnauthorizedAccessException or TaskCanceledException)
        {
            ShellLog.Write($"helper {h.Id}: download failed: {ex.Message}");
            return $"The download of {h.Name} failed: {ex.Message}. Download it yourself from {d.Page}";
        }
        finally
        {
            try { File.Delete(tmp); } catch (Exception) { }
        }
    }
}
