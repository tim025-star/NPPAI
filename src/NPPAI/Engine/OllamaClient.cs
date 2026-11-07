using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NPPAI.Engine
{
    public class ChatMessage
    {
        public string role;
        public string content;
    }

    public class ChatRequest
    {
        public string model;
        public bool stream = true;
        public object options;
        public List<ChatMessage> messages;
    }

    /// <summary>
    /// Minimal Ollama HTTP client. NDJSON streaming for /api/chat,
    /// plus ping / model listing for diagnostics.
    /// </summary>
    public class OllamaClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _endpoint;

        public OllamaClient(string endpoint, int timeoutMs = 20000)
        {
            _endpoint = (endpoint ?? "http://localhost:11434").TrimEnd('/');
            _http = new HttpClient(new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(3)
            })
            {
                Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, timeoutMs))
            };
        }

        public string Endpoint => _endpoint;

        /// <summary>GET / — returns "Ollama is running" when healthy.</summary>
        public async Task<string> PingAsync(CancellationToken ct = default)
        {
            using var resp = await _http.GetAsync(_endpoint + "/", ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        /// <summary>GET /api/tags — list of locally available models.</summary>
        public async Task<List<string>> ListModelsAsync(CancellationToken ct = default)
        {
            using var resp = await _http.GetAsync(_endpoint + "/api/tags", ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var names = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out var arr))
            {
                foreach (var m in arr.EnumerateArray())
                    if (m.TryGetProperty("name", out var n))
                        names.Add(n.GetString());
            }
            return names;
        }

        /// <summary>
        /// POST /api/chat with stream:true. Invokes onChunk for each content delta.
        /// Returns the full concatenated message content.
        /// </summary>
        public async Task<string> StreamChatAsync(
            string model,
            List<ChatMessage> messages,
            double temperature,
            int maxTokens,
            Action<string> onChunk,
            CancellationToken ct)
        {
            var req = new ChatRequest
            {
                model = model,
                stream = true,
                options = new
                {
                    temperature = temperature,
                    num_predict = maxTokens
                },
                messages = messages
            };

            string json = JsonSerializer.Serialize(req, new JsonSerializerOptions { IncludeFields = true });
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint + "/api/chat") { Content = content };
            using var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var sb = new StringBuilder();
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            string line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            {
                line = line.Trim();
                if (line.Length == 0) continue;

                string delta = null;
                bool done = false;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("message", out var msg) &&
                        msg.TryGetProperty("content", out var c))
                        delta = c.GetString();
                    if (doc.RootElement.TryGetProperty("done", out var d))
                        done = d.GetBoolean();
                }
                catch (JsonException)
                {
                    continue; // partial / malformed line — skip
                }

                if (delta != null && delta.Length > 0)
                {
                    sb.Append(delta);
                    onChunk?.Invoke(delta);
                }
                if (done) break;
            }
            return sb.ToString();
        }

        public void Dispose() => _http.Dispose();
    }
}
