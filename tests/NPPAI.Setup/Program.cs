using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NPPAI.Setup;

if (args.Length == 2 && args[0] == "--network-probe")
{
    var metadata = JsonSerializer.Deserialize<Package>(File.ReadAllText(args[1]), SetupCore.Json)!;
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    client.DefaultRequestHeaders.UserAgent.ParseAdd("NPPAI-Setup/1.0");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream");
    var id = SetupCore.Parts(metadata)[0].Id;
    using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/tim025-star/NPPAI/releases/assets/{id}");
    request.Headers.Range = new RangeHeaderValue(0, 1023);
    using var response = await client.SendAsync(request);
    response.EnsureSuccessStatusCode();
    var bytes = await response.Content.ReadAsByteArrayAsync();
    if (response.StatusCode != HttpStatusCode.PartialContent || bytes.Length != 1024 || bytes[0] != 'P' || bytes[1] != 'K') throw new Exception("Invalid release ZIP download response");
    using var fullResponse = await client.GetAsync(request.RequestUri, HttpCompletionOption.ResponseHeadersRead);
    fullResponse.EnsureSuccessStatusCode();
    using var fullStream = await fullResponse.Content.ReadAsStreamAsync();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var header = new byte[1024];
    await fullStream.ReadExactlyAsync(header, deadline.Token);
    if (!header.SequenceEqual(bytes)) throw new Exception("Full download header mismatch");
    Console.WriteLine("PASS live GitHub release download through installer HTTP client");
    return;
}

if (args.Length == 3 && args[0] == "--full-package")
{
    var metadata = JsonSerializer.Deserialize<Package>(File.ReadAllText(args[1]), SetupCore.Json)!;
    var directory = Path.GetFullPath(args[2]);
    using var client = new HttpClient();
    await SetupCore.Install(metadata, Path.Combine(directory, "Notepad++"), Path.Combine(directory, "cache"), client, new Progress<SetupProgress>(x => Console.WriteLine(x.Message)), default);
    Console.WriteLine("PASS full-size installer package verification and installation");
    return;
}

var root = Path.Combine(Path.GetTempPath(), "nppai-setup-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var progress = new Progress<SetupProgress>();
int passed = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
async Task Reject(Func<Task> action, string label) { try { await action(); } catch (Exception error) when (error is IOException or InvalidDataException) { Check(true, label); return; } throw new Exception("Expected rejection: " + label); }
try
{
    var npp = Path.Combine(root, "Notepad++"); Directory.CreateDirectory(npp);
    var pe = new byte[256]; pe[0] = 0x4d; pe[1] = 0x5a; pe[0x3c] = 0x80; pe[0x80] = 0x50; pe[0x81] = 0x45; pe[0x84] = 0x64; pe[0x85] = 0x86;
    File.WriteAllBytes(Path.Combine(npp, "notepad++.exe"), pe);
    SetupCore.ValidateNotepad(npp); Check(true, "x64 Notepad++ detection");
    pe[0x84] = 0x4c; File.WriteAllBytes(Path.Combine(npp, "notepad++.exe"), pe);
    await Reject(() => { SetupCore.ValidateNotepad(npp); return Task.CompletedTask; }, "reject wrong architecture");
    pe[0x84] = 0x64; File.WriteAllBytes(Path.Combine(npp, "notepad++.exe"), pe);
    foreach (var path in new[] { "elsewhere/file", "NPPAI/../file", "NPPAI/C:/file", "NPPAI/runtime\\..\\file", "NPPAI/file:stream", "NPPAI/file." })
        await Reject(() => { SetupCore.SafeEntry(path); return Task.CompletedTask; }, "reject unsafe ZIP path " + path);

    var data = Encoding.UTF8.GetBytes("resumable verified download");
    var asset = new Asset("fixture", data.Length, SetupCore.Digest(data));
    var pathToDownload = Path.Combine(root, "download");
    File.WriteAllBytes(pathToDownload + ".partial", data[..8]);
    var handler = new FixtureHandler(data); using var http = new HttpClient(handler);
    await SetupCore.Download(http, new Uri("https://example.test/fixture"), pathToDownload, asset, progress, default);
    Check(handler.LastOffset == 8 && File.ReadAllBytes(pathToDownload).SequenceEqual(data), "resume interrupted download and validate SHA-256");
    int requests = handler.Requests;
    await SetupCore.Download(http, new Uri("https://example.test/fixture"), pathToDownload, asset, progress, default);
    Check(requests == handler.Requests, "reuse verified download without network");
    await Reject(() => SetupCore.Download(http, new Uri("https://example.test/fixture"), Path.Combine(root, "bad"), asset with { Sha256 = new string('0', 64) }, progress, default), "reject corrupt download");

    var content = new Dictionary<string, byte[]>();
    foreach (var name in new[] { "NPPAI.dll", "NPPAI.Managed.dll", "coreclr.dll", "hostfxr.dll", "models/models.json", "runtime/cpu/llama-server.exe", "runtime/vulkan/llama-server.exe" }) content[name] = Encoding.UTF8.GetBytes(name);
    var records = content.Select(x => new PackageFile(x.Key, x.Value.Length, SetupCore.Digest(x.Value))).ToArray();
    byte[] Archive(bool badFile)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            foreach (var file in content) { using var stream = zip.CreateEntry("NPPAI/" + file.Key).Open(); stream.Write(badFile ? new byte[file.Value.Length] : file.Value); }
            using var manifest = zip.CreateEntry("NPPAI/PACKAGE-FILES.json").Open(); manifest.Write(JsonSerializer.SerializeToUtf8Bytes(records));
        }
        return memory.ToArray();
    }
    async Task<Package> Fixture(bool badFile, string cache)
    {
        Directory.CreateDirectory(cache);
        var bytes = Archive(badFile);
        var name = "NPPAI-v0.1.0-win-x64.zip.001";
        File.WriteAllBytes(Path.Combine(cache, name), bytes);
        return await Task.FromResult(new Package("v0.1.0", new string('1',40), SetupCore.Digest(bytes), bytes.Length, new[] { new Asset(name, bytes.Length, SetupCore.Digest(bytes)) }));
    }
    string Cache() => Path.Combine(root, Guid.NewGuid().ToString("N"));
    var initialCache = Cache(); var package = await Fixture(false, initialCache);
    await SetupCore.Install(package, npp, initialCache, http, progress, default);
    var target = Path.Combine(npp, "plugins", "NPPAI");
    Check(File.ReadAllBytes(Path.Combine(target, "NPPAI.dll")).SequenceEqual(content["NPPAI.dll"]), "install complete verified package into correct plugin folder");
    File.WriteAllText(Path.Combine(target, "old-only.txt"), "old");
    var badCache = Cache(); var badPackage = await Fixture(true, badCache);
    await Reject(() => SetupCore.Install(badPackage, npp, badCache, http, progress, default), "reject extracted file hash mismatch");
    Check(File.Exists(Path.Combine(target, "old-only.txt")), "failed update preserves previous plugin");
    var cancelCache = Cache(); package = await Fixture(false, cancelCache);
    using var cts = new CancellationTokenSource(); cts.Cancel();
    try { await SetupCore.Install(package, npp, cancelCache, http, progress, cts.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
    Check(File.Exists(Path.Combine(target, "old-only.txt")), "cancellation preserves previous plugin");
    var upgradeCache = Cache(); package = await Fixture(false, upgradeCache);
    await SetupCore.Install(package, npp, upgradeCache, http, progress, default);
    Check(!File.Exists(Path.Combine(target, "old-only.txt")) && Directory.GetDirectories(Path.Combine(npp, "plugins")).Length == 1, "upgrade replaces stale files and removes staging and backup");
    Check(!Directory.EnumerateFiles(upgradeCache).Any(), "successful installation removes downloaded parts");

    // A completed cached part followed by a streamed part reproduces the visible reset.
    // Keep observing through verification/extraction to catch resets between phases too.
    var splitBytes = Archive(false);
    int middle = splitBytes.Length / 2;
    var splitCache = Cache(); Directory.CreateDirectory(splitCache);
    var first = new Asset("NPPAI-v0.1.0-win-x64.zip.001", middle, SetupCore.Digest(splitBytes[..middle]));
    var second = new Asset("NPPAI-v0.1.0-win-x64.zip.002", splitBytes.Length - middle, SetupCore.Digest(splitBytes[middle..]));
    File.WriteAllBytes(Path.Combine(splitCache, first.Name), splitBytes[..middle]);
    var splitPackage = new Package("v0.1.0", new string('1', 40), SetupCore.Digest(splitBytes), splitBytes.Length, new[] { first, second });
    using var splitHttp = new HttpClient(new FixtureHandler(splitBytes[middle..]));
    var observed = new List<SetupProgress>();
    await SetupCore.Install(splitPackage, npp, splitCache, splitHttp, new CaptureProgress(observed), default);
    var percentages = observed.Where(x => x.Total > 0).Select(x => x.Done * 1000 / x.Total).ToArray();
    Check(percentages.Length > 3 && percentages.Zip(percentages.Skip(1), (a,b) => b >= a).All(x => x), "overall progress never restarts between parts or installation phases: " + string.Join(",", percentages.Take(8)));
    Check(observed.Any(x => x.Message.Contains("part 2 of 2")), "progress identifies the current part and total part count");

    var blockedNpp = Path.Combine(root, "blocked-destination"); Directory.CreateDirectory(blockedNpp);
    File.Copy(Path.Combine(npp, "notepad++.exe"), Path.Combine(blockedNpp, "notepad++.exe"));
    File.WriteAllText(Path.Combine(blockedNpp, "plugins"), "A file prevents writing the plugin directory");
    var blockedCache = Cache();
    var archiveData = Archive(false);
    var blockedPackage = new Package("v0.1.0", new string('1',40), SetupCore.Digest(archiveData), archiveData.Length, new[] { new Asset(first.Name, archiveData.Length, SetupCore.Digest(archiveData)) });
    var blockedHandler = new FixtureHandler(archiveData); using var blockedHttp = new HttpClient(blockedHandler);
    await Reject(() => SetupCore.Install(blockedPackage, blockedNpp, blockedCache, blockedHttp, progress, default), "reject unwritable installation destination");
    Check(blockedHandler.Requests == 0 && !Directory.Exists(blockedCache), "check destination permissions before creating a download cache or downloading");
    Console.WriteLine($"Passed {passed} installer checks.");
}
finally { SetupCore.DeleteOwned(root); }

sealed class FixtureHandler(byte[] data) : HttpMessageHandler
{
    public long LastOffset;
    public int Requests;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Requests++; LastOffset = request.Headers.Range?.Ranges.First().From ?? 0;
        var response = new HttpResponseMessage(LastOffset > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new StreamContent(new ChunkedStream(data[(int)LastOffset..])) };
        if (LastOffset > 0) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(LastOffset, data.Length - 1, data.Length);
        return Task.FromResult(response);
    }
}

sealed class ChunkedStream(byte[] data) : MemoryStream(data)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => base.ReadAsync(buffer[..Math.Min(32, buffer.Length)], token);
}

sealed class CaptureProgress(List<SetupProgress> observed) : IProgress<SetupProgress>
{
    public void Report(SetupProgress value) => observed.Add(value);
}
