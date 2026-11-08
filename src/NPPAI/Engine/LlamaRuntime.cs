using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NPPAI.Engine;

/// <summary>Owns one lazily loaded, authenticated loopback server per bundled model.</summary>
public sealed class LlamaRuntime : IInferenceBackend
{
    sealed class Model {
        public string Id { get; set; }
        public string File { get; set; }
        public string Sha256 { get; set; }
    }
    sealed class Manifest { public Model[] Models { get; set; } }
    sealed class Server {
        public Model Model;
        public Task<ServerConnection> Starting;
        public Process Process;
        public string Status = "Not loaded";
    }
    readonly string packageDirectory;
    readonly Dictionary<string, Server> servers = new(StringComparer.Ordinal);
    readonly object sync = new();
    readonly CancellationTokenSource lifetime = new();
    readonly string packageError;
    ProcessJob job;
    bool disposed;

    public LlamaRuntime(string packageDirectory)
    {
        this.packageDirectory = Path.GetFullPath(packageDirectory);
        try
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(this.packageDirectory, "models", "models.json")),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (manifest?.Models == null || manifest.Models.Length == 0) throw new InvalidDataException("No bundled models found.");
            foreach (var model in manifest.Models)
            {
                if (string.IsNullOrWhiteSpace(model.Id) || string.IsNullOrWhiteSpace(model.File) || Path.GetFileName(model.File) != model.File ||
                    !model.File.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) || model.Sha256?.Length != 64)
                    throw new InvalidDataException("Invalid bundled model manifest.");
                servers.Add(model.Id, new Server { Model = model });
            }
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException || ex is ArgumentException)
        {
            servers.Clear(); packageError = "Incomplete NPPAI package: " + ex.Message;
        }
    }

    public IReadOnlyList<string> Models => servers.Keys.ToArray();
    public string Status { get { lock (sync) return packageError ?? string.Join("; ", servers.Select(s => s.Key + ": " + s.Value.Status)); } }
    public LlamaClient CreateClient(string model, int timeoutMs) => new(token => ConnectAsync(model, token), timeoutMs);

    async Task<ServerConnection> ConnectAsync(string model, CancellationToken token)
    {
        Task<ServerConnection> starting;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (packageError != null) throw new InvalidOperationException(packageError);
            if (!servers.TryGetValue(model, out var server)) throw new InvalidOperationException("Model is not included in this NPPAI package: " + model);
            if (server.Starting == null || server.Starting.IsFaulted || server.Starting.IsCanceled ||
                (server.Starting.IsCompleted && (server.Process == null || server.Process.HasExited)))
            {
                server.Process?.Dispose(); server.Process = null;
                var runtimeToken = lifetime.Token;
                server.Starting = Task.Run(() => StartAsync(server, runtimeToken));
            }
            starting = server.Starting;
        }
        // Cancelling a keystroke must not abort the shared model load.
        return await starting.WaitAsync(token).ConfigureAwait(false);
    }

    async Task<ServerConnection> StartAsync(Server server, CancellationToken token)
    {
        try
        {
            string path = Path.Combine(packageDirectory, "models", server.Model.File);
            using (var file = File.OpenRead(path)) {
                byte[] magic = new byte[4];
                if (file.Read(magic, 0, 4) != 4 || Encoding.ASCII.GetString(magic) != "GGUF")
                    throw new InvalidDataException("Bundled model is not a GGUF file: " + server.Model.File);
            }
            try { return await LaunchAsync(server, path, "vulkan", token).ConfigureAwait(false); }
            catch (Exception ex) when (!token.IsCancellationRequested && (ex is IOException || ex is HttpRequestException || ex is TimeoutException || ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException))
            {
                SetStatus(server, "GPU startup failed; loading on CPU");
                return await LaunchAsync(server, path, "cpu", token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { SetStatus(server, "Unavailable: " + ex.Message); throw; }
    }

    async Task<ServerConnection> LaunchAsync(Server server, string modelPath, string backend, CancellationToken token)
    {
        string executable = Path.Combine(packageDirectory, "runtime", backend, "llama-server.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Missing bundled llama.cpp runtime.", executable);
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        string key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var connection = new ServerConnection("http://127.0.0.1:" + port, key);
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(executable),
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string name in info.Environment.Keys.Where(n => n.StartsWith("LLAMA_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(name);
        info.Environment["OLLAMA_LLAMA_CPP_COMPAT"] = "1";
        foreach (string arg in new[] { "--model", modelPath, "--alias", server.Model.Id, "--host", "127.0.0.1", "--port", port.ToString(),
            "--api-key", key, "--ctx-size", "8192", "--parallel", "2", "--n-gpu-layers", backend == "cpu" ? "0" : "99",
            "--no-webui", "--offline", "--no-agent", "--verbosity", "1", "--jinja" }) info.ArgumentList.Add(arg);
        var diagnostics = new StringBuilder();
        var process = new Process { StartInfo = info };
        void Capture(object sender, DataReceivedEventArgs args) {
            if (args.Data == null) return;
            lock (diagnostics) { diagnostics.AppendLine(args.Data); if (diagnostics.Length > 4000) diagnostics.Remove(0, diagnostics.Length - 4000); }
        }
        process.OutputDataReceived += Capture; process.ErrorDataReceived += Capture;
        try
        {
            lock (sync)
            {
                token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(disposed, this);
                job ??= new ProcessJob();
                SetStatus(server, "Loading (" + backend + ")");
                if (!process.Start()) throw new InvalidOperationException("Unable to start bundled llama.cpp.");
                server.Process = process;
                job.Add(process);
            }
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            using var probe = new LlamaClient(connection.Endpoint, 1000);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                while (true)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (process.HasExited) {
                        string detail; lock (diagnostics) detail = diagnostics.ToString();
                        // Never include the per-process credential in a status or log message.
                        throw new InvalidOperationException("llama.cpp exited (" + process.ExitCode + "): " + detail.Replace(key, "<redacted>"));
                    }
                    try {
                        await probe.PingAsync(deadline.Token).ConfigureAwait(false);
                        // Authenticate against the model endpoint before trusting the selected port.
                        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) };
                        using var request = new HttpRequestMessage(HttpMethod.Get, connection.Endpoint + "/v1/models");
                        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
                        using var response = await http.SendAsync(request, deadline.Token).ConfigureAwait(false);
                        response.EnsureSuccessStatusCode();
                        SetStatus(server, "Ready (" + backend + ")");
                        return connection;
                    }
                    catch (HttpRequestException) { }
                    catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                    await Task.Delay(250, deadline.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("Bundled model did not load within two minutes."); }
        }
        catch
        {
            lock (sync) {
                if (server.Process == process) server.Process = null;
                Stop(process);
            }
            throw;
        }
    }

    void SetStatus(Server server, string value) { lock (sync) server.Status = value; }
    static void Stop(Process process) {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }
    public void Dispose()
    {
        lock (sync) {
            if (disposed) return;
            disposed = true; lifetime.Cancel();
            // Closing the job is the authoritative cleanup, including failed or interrupted starts.
            job?.Dispose();
            foreach (var server in servers.Values) { server.Process?.Dispose(); server.Process = null; }
            lifetime.Dispose();
        }
    }
}
