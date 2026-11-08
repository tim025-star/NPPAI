using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NPPAI.Engine;

public class ChatMessage
{
    public string role;
    public string content;
}

public record ServerConnection(string Endpoint, string ApiKey = null);

/// <summary>Streaming llama-server client; the runtime resolves and loads the requested model.</summary>
public sealed class LlamaClient : IDisposable
{
    readonly HttpClient http;
    readonly Func<CancellationToken, Task<ServerConnection>> connect;

    public LlamaClient(Func<CancellationToken, Task<ServerConnection>> connect, int timeoutMs = 20000)
    {
        this.connect = connect;
        http = new HttpClient(new SocketsHttpHandler {
            AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(2)
        }) { Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, timeoutMs)) };
    }

    public LlamaClient(string endpoint, int timeoutMs = 20000)
        : this(_ => Task.FromResult(new ServerConnection(endpoint)), timeoutMs) { }

    static string Validate(ServerConnection server)
    {
        if (!Uri.TryCreate(server.Endpoint, UriKind.Absolute, out var uri) || !uri.IsLoopback ||
            uri.Scheme != "http" || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Inference must use a local HTTP endpoint.");
        return server.Endpoint.TrimEnd('/');
    }

    public async Task<string> PingAsync(CancellationToken token = default)
    {
        var server = await connect(token).ConfigureAwait(false);
        using var response = await http.GetAsync(Validate(server) + "/health", token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
    }

    public async Task<string> StreamChatAsync(string model, List<ChatMessage> messages, double temperature,
        int maxTokens, Action<string> onChunk, CancellationToken token, object format = null)
    {
        // Model loading has its own bounded deadline. The inference timeout starts once it is ready.
        var server = await connect(token).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(http.Timeout);
        token = deadline.Token;
        var payload = new {
            model, messages, temperature, max_tokens = maxTokens, stream = true,
            chat_template_kwargs = new { enable_thinking = false },
            response_format = format == null ? null : new { type = "json_schema", json_schema = new { name = "completion", strict = true, schema = format } }
        };
        string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { IncludeFields = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        using var request = new HttpRequestMessage(HttpMethod.Post, Validate(server) + "/v1/chat/completions") {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (server.ApiKey != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.ApiKey);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var text = new StringBuilder();
        bool finished = false;
        string line;
        while ((line = await reader.ReadLineAsync(token).ConfigureAwait(false)) != null)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            string data = line.Substring(5).Trim();
            if (data == "[DONE]") { finished = true; break; }
            try
            {
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("error", out _)) throw new InvalidDataException("llama.cpp returned an inference error.");
                if (!doc.RootElement.TryGetProperty("choices", out var choices)) continue;
                foreach (var choice in choices.EnumerateArray())
                {
                    if (format != null && choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String && reason.GetString() == "length")
                        throw new InvalidDataException("llama.cpp reached the response token limit.");
                    if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    {
                        string chunk = content.GetString();
                        text.Append(chunk); onChunk?.Invoke(chunk);
                    }
                }
            }
            catch (JsonException ex) { throw new InvalidDataException("llama.cpp returned malformed streaming JSON.", ex); }
        }
        if (!finished) throw new InvalidDataException("llama.cpp closed an incomplete response stream.");
        return text.ToString();
    }

    public void Dispose() => http.Dispose();
}
