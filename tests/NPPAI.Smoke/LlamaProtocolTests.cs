using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NPPAI.Engine;

internal static class LlamaProtocolTests
{
    internal static async Task Run(Action<bool, string> assert)
    {
        await Exchange("data: {\"choices\":[{\"delta\":{\"content\":\"hé\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"llo\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", async (client, request) => {
            string streamed = "";
            string text = await client.StreamChatAsync("fixture", new() { new() { role="user", content="hel" } }, 0, 32, s=>streamed+=s, default, PredictionResponse.Format("hel"));
            assert(text == "héllo" && streamed == text, "SSE chunks preserve UTF-8 and invoke the streaming callback");
            using var doc = JsonDocument.Parse(await request);
            assert(doc.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("schema").GetProperty("type").GetString() == "object", "llama.cpp receives the prediction JSON schema");
            assert(!doc.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean(), "thinking is disabled for interactive inference");
        });
        foreach (string response in new[] {
            "data: {bad json}\n\n", "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\ndata: [DONE]\n\n" })
        {
            await Exchange(response, async (client, _) => {
                try { await client.StreamChatAsync("fixture", new(), 0, 2, null, default, PredictionResponse.Format("")); throw new Exception("Expected incomplete/invalid stream rejection"); }
                catch (InvalidDataException) { assert(true, "malformed, interrupted or token-limited responses fail closed"); }
            });
        }
        using var remote = new LlamaClient("http://example.com");
        try { await remote.PingAsync(); throw new Exception("Expected loopback restriction"); }
        catch (ArgumentException) { assert(true, "inference client rejects non-loopback endpoints"); }
        using var missing = new LlamaRuntime(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        assert(missing.Models.Count == 0 && missing.Status.Contains("Incomplete"), "incomplete packages remain diagnosable without launching a server");
        using var clientMissing = missing.CreateClient("missing", 1000);
        try { await clientMissing.PingAsync(); throw new Exception("Expected incomplete package error"); }
        catch (InvalidOperationException) { assert(true, "missing model packages cannot silently use an external service"); }
    }

    static async Task Exchange(string response, Func<LlamaClient, Task<string>, Task> test)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        string endpoint = "http://127.0.0.1:" + port;
        using var listener = new HttpListener(); listener.Prefixes.Add(endpoint + "/"); listener.Start();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serve = Task.Run(async () => {
            var context = await listener.GetContextAsync();
            if (context.Request.Url!.AbsolutePath != "/v1/chat/completions" || context.Request.Headers["Authorization"] != "Bearer fixture-key")
                throw new Exception("Wrong inference route or missing authentication");
            using var reader = new StreamReader(context.Request.InputStream);
            received.SetResult(await reader.ReadToEndAsync());
            context.Response.ContentType = "text/event-stream";
            await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(response)); context.Response.Close();
        });
        using var client = new LlamaClient(_ => Task.FromResult(new ServerConnection(endpoint, "fixture-key")), 1000);
        await test(client, received.Task);
        await serve;
    }
}
