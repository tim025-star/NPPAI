using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace NPPAI.Setup;

internal sealed record Asset(string Name, long Bytes, string Sha256, long Id = 0);
internal sealed record Package(string Tag, string Commit, string ArchiveHash, long ArchiveBytes, Asset[] Assets);
internal sealed record PackageFile(string Path, long Bytes, string Sha256);
internal sealed record SetupProgress(string Message, long Done = 0, long Total = 0);

internal static class SetupCore
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    internal static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static bool HashEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    internal static bool IsHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    internal static Asset[] Parts(Package package)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(package.Tag, @"^v\d+\.\d+\.\d+$") || !IsHash(package.ArchiveHash))
            throw new InvalidDataException("Invalid embedded release manifest.");
        var parts = package.Assets.Where(x => x.Name.StartsWith($"NPPAI-{package.Tag}-win-x64.zip.", StringComparison.Ordinal)).OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
        if (parts.Length == 0 || parts.Sum(x => x.Bytes) != package.ArchiveBytes) throw new InvalidDataException("Incomplete release manifest.");
        for (int i = 0; i < parts.Length; i++)
            if (parts[i].Name != $"NPPAI-{package.Tag}-win-x64.zip.{i + 1:D3}" || parts[i].Bytes <= 0 || !IsHash(parts[i].Sha256))
                throw new InvalidDataException("Invalid archive part.");
        return parts;
    }

    internal static void NoLinks(string path)
    {
        for (var current = new DirectoryInfo(System.IO.Path.GetFullPath(path)); current != null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Installation and cache folders cannot contain symbolic links or junctions.");
    }

    internal static void ValidateNotepad(string directory)
    {
        NoLinks(directory);
        var path = System.IO.Path.Combine(directory, "notepad++.exe");
        using var file = new BinaryReader(File.OpenRead(path));
        if (file.BaseStream.Length < 64 || file.ReadUInt16() != 0x5a4d) throw new IOException("Select a folder containing x64 Notepad++.");
        file.BaseStream.Position = 0x3c;
        var header = file.ReadInt32();
        if (header < 64 || header > file.BaseStream.Length - 6) throw new IOException("Invalid Notepad++ executable.");
        file.BaseStream.Position = header;
        if (file.ReadUInt32() != 0x4550 || file.ReadUInt16() != 0x8664) throw new IOException("NPPAI requires x64 Notepad++. The selected executable has a different architecture.");
    }

    internal static IEnumerable<string> FindNotepad()
    {
        var candidates = new List<string>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\notepad++.exe");
            if (key?.GetValue("") is string exe && System.IO.Path.GetDirectoryName(exe.Trim('"')) is string directory) candidates.Add(directory);
            using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Notepad++");
            if (uninstall?.GetValue("InstallLocation") is string location && location.Length > 0) candidates.Add(location);
        }
        candidates.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Notepad++"));
        candidates.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Notepad++"));
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            bool valid;
            try { ValidateNotepad(candidate); valid = true; } catch { valid = false; }
            if (valid) yield return candidate;
        }
    }

    internal static void CheckClosed(string directory)
    {
        var exe = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, "notepad++.exe"));
        foreach (var process in Process.GetProcessesByName("notepad++"))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Save your work and close this Notepad++ installation, then try again.");
                }
                catch (System.ComponentModel.Win32Exception) { throw new IOException("Close Notepad++ before installing; its running installation could not be checked."); }
                catch (InvalidOperationException) { /* Process exited during inspection. */ }
            }
        }
    }

    internal static async Task<bool> VerifyFile(string path, Asset asset, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != asset.Bytes) return false;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("A download cache file is a symbolic link.");
        using var stream = File.OpenRead(path);
        return HashEquals(Convert.ToHexString(await SHA256.HashDataAsync(stream, token)), asset.Sha256);
    }

    internal static async Task Download(HttpClient client, Uri uri, string path, Asset asset, IProgress<SetupProgress> progress, CancellationToken token)
    {
        if (await VerifyFile(path, asset, token)) { progress.Report(new($"Using verified download: {asset.Name}", asset.Bytes, asset.Bytes)); return; }
        if (File.Exists(path)) File.Delete(path);
        var partial = path + ".partial";
        if (File.Exists(partial) && (File.GetAttributes(partial) & FileAttributes.ReparsePoint) != 0) throw new IOException("A partial download is a symbolic link.");
        long offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (offset >= asset.Bytes) { File.Delete(partial); offset = 0; }
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        progress.Report(new($"Connecting to GitHub for {asset.Name}"));
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(token);
        connection.CancelAfter(TimeSpan.FromSeconds(90));
        HttpResponseMessage received;
        try { received = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connection.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("GitHub did not respond within 90 seconds. Check the connection and retry."); }
        using var response = received;
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (range?.From != offset || range.Length != asset.Bytes || range.To != asset.Bytes - 1) throw new IOException("The download server returned an unexpected byte range.");
        }
        else if (response.StatusCode == HttpStatusCode.OK) offset = 0;
        else throw new IOException("Unexpected download response.");
        using (var input = await response.Content.ReadAsStreamAsync(token))
        using (var output = new FileStream(partial, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true))
        {
            var buffer = new byte[1024 * 1024];
            long done = offset;
            int read;
            while ((read = await input.ReadAsync(buffer, token)) != 0)
            {
                done += read;
                if (done > asset.Bytes) throw new IOException("Download exceeds the pinned size.");
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                progress.Report(new($"Downloading {asset.Name} ({done / 1048576:N0} / {asset.Bytes / 1048576:N0} MB)", done, asset.Bytes));
            }
        }
        progress.Report(new($"Verifying {asset.Name}"));
        if (!await VerifyFile(partial, asset, token)) { File.Delete(partial); throw new IOException("Download checksum mismatch. Retry to download a clean copy."); }
        File.Move(partial, path);
    }

    internal static string SafeEntry(string path)
    {
        if (!path.StartsWith("NPPAI/", StringComparison.Ordinal)) throw new InvalidDataException("Archive entry is outside NPPAI.");
        var relative = path[6..];
        var pieces = relative.Split('/');
        if (pieces.Any(x => x.Length == 0 || x is "." or ".." || x.EndsWith('.') || x.EndsWith(' ') || x.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("Unsafe archive entry.");
        return relative;
    }

    internal static async Task Extract(Package package, string[] paths, string stage, IProgress<SetupProgress> progress, CancellationToken token)
    {
        using var reader = new ReleasePartReader(paths);
        progress.Report(new("Verifying the complete archive"));
        if (!HashEquals(Convert.ToHexString(await SHA256.HashDataAsync(reader, token)), package.ArchiveHash)) throw new IOException("Complete archive checksum mismatch.");
        reader.Position = 0;
        using var zip = new ZipArchive(reader, ZipArchiveMode.Read);
        var manifest = zip.GetEntry("NPPAI/PACKAGE-FILES.json") ?? throw new InvalidDataException("Missing package file manifest.");
        if (manifest.Length > 4 * 1024 * 1024) throw new InvalidDataException("Oversized file manifest.");
        using var manifestStream = manifest.Open();
        var records = await JsonSerializer.DeserializeAsync<PackageFile[]>(manifestStream, Json, token) ?? throw new InvalidDataException("Empty file manifest.");
        var expected = records.ToDictionary(x => SafeEntry("NPPAI/" + x.Path), StringComparer.OrdinalIgnoreCase);
        if (expected.Count > 10000 || expected.Values.Any(x => x.Bytes < 0 || !IsHash(x.Sha256)) || expected.Values.Sum(x => x.Bytes) > 20L * 1024 * 1024 * 1024 || zip.Entries.Count != records.Length + 1)
            throw new InvalidDataException("Invalid package file manifest.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            var relative = SafeEntry(entry.FullName);
            if (!seen.Add(relative)) throw new InvalidDataException("Duplicate archive path.");
            bool isManifest = relative == "PACKAGE-FILES.json";
            if (!isManifest && (!expected.TryGetValue(relative, out var record) || record.Bytes != entry.Length)) throw new InvalidDataException("Unexpected archive file.");
            var target = System.IO.Path.Combine(stage, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            progress.Report(new($"Installing {relative}", seen.Count, zip.Entries.Count));
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024];
            int read;
            long written = 0;
            while ((read = await input.ReadAsync(buffer, token)) != 0)
            {
                written += read;
                if (written > entry.Length) throw new InvalidDataException("Archive size mismatch.");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), token);
            }
            if (written != entry.Length || (!isManifest && !HashEquals(Convert.ToHexString(hash.GetHashAndReset()), expected[relative].Sha256))) throw new IOException("Extracted file checksum mismatch: " + relative);
        }
        foreach (var required in new[] { "NPPAI.dll", "NPPAI.Managed.dll", "coreclr.dll", "hostfxr.dll", "models/models.json", "runtime/cpu/llama-server.exe", "runtime/vulkan/llama-server.exe" })
            if (!seen.Contains(required)) throw new InvalidDataException("Incomplete plugin package: " + required);
    }

    // Remove only this operation's owned directory, refusing to traverse any reparse points.
    internal static void DeleteOwned(string path)
    {
        NoLinks(path);
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Refusing to remove a linked file.");
            File.Delete(file);
        }
        foreach (var directory in Directory.EnumerateDirectories(path)) DeleteOwned(directory);
        Directory.Delete(path);
    }

    internal static async Task<string?> Install(Package package, string notepadDirectory, string cache, HttpClient client, IProgress<SetupProgress> progress, CancellationToken token)
    {
        var parts = Parts(package);
        ValidateNotepad(notepadDirectory);
        CheckClosed(notepadDirectory);
        NoLinks(cache);
        Directory.CreateDirectory(cache);
        long cached = parts.Sum(x => Math.Min(x.Bytes, File.Exists(System.IO.Path.Combine(cache, x.Name)) ? new FileInfo(System.IO.Path.Combine(cache, x.Name)).Length : File.Exists(System.IO.Path.Combine(cache, x.Name) + ".partial") ? new FileInfo(System.IO.Path.Combine(cache, x.Name) + ".partial").Length : 0));
        var cacheDrive = new DriveInfo(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(cache))!);
        var targetDrive = new DriveInfo(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(notepadDirectory))!);
        long downloadSpace = package.ArchiveBytes - cached;
        long installSpace = package.ArchiveBytes + 1024L * 1024 * 1024;
        if (cacheDrive.AvailableFreeSpace < downloadSpace || targetDrive.AvailableFreeSpace < installSpace || (cacheDrive.Name.Equals(targetDrive.Name, StringComparison.OrdinalIgnoreCase) && targetDrive.AvailableFreeSpace < downloadSpace + installSpace))
            throw new IOException("Insufficient free space. Allow about 20 GB for downloads and installation, then retry.");
        var paths = new List<string>();
        foreach (var asset in parts)
        {
            var path = System.IO.Path.Combine(cache, asset.Name);
            var url = asset.Id > 0 ? $"https://api.github.com/repos/tim025-star/NPPAI/releases/assets/{asset.Id}" : $"https://github.com/tim025-star/NPPAI/releases/download/{package.Tag}/{asset.Name}";
            await Download(client, new Uri(url), path, asset, progress, token);
            paths.Add(path);
        }
        var plugins = System.IO.Path.Combine(System.IO.Path.GetFullPath(notepadDirectory), "plugins");
        NoLinks(plugins);
        Directory.CreateDirectory(plugins);
        var lockPath = System.IO.Path.Combine(plugins, ".NPPAI-setup.lock");
        if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("The installation lock is a symbolic link.");
        using var installationLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        var target = System.IO.Path.Combine(plugins, "NPPAI");
        NoLinks(target);
        var stage = System.IO.Path.Combine(plugins, ".NPPAI-install-" + Guid.NewGuid().ToString("N"));
        var backup = System.IO.Path.Combine(plugins, ".NPPAI-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            await Extract(package, paths.ToArray(), stage, progress, token);
            token.ThrowIfCancellationRequested();
            CheckClosed(notepadDirectory);
            NoLinks(target);
            bool hadPrevious = Directory.Exists(target);
            if (hadPrevious) Directory.Move(target, backup);
            try { Directory.Move(stage, target); }
            catch { if (hadPrevious) Directory.Move(backup, target); throw; }
            // The replacement is committed. Cleanup failure must not report a failed installation.
            string? warning = null;
            try { if (hadPrevious) DeleteOwned(backup); }
            catch { warning = "The previous plugin backup was retained at " + backup; }
            foreach (var path in paths) { try { File.Delete(path); } catch { /* Harmless verified cache. */ } }
            return warning;
        }
        finally { if (Directory.Exists(stage)) DeleteOwned(stage); }
    }
}
