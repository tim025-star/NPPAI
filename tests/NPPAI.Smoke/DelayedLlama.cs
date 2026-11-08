using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

// Controlled provider response for the destructive-edit race tests.
sealed class DelayedLlama : IDisposable
{
    readonly HttpListener listener = new();
    readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string Endpoint { get; }
    public DelayedLlama(string original, string replacement)
    {
        var reservation = new TcpListener(IPAddress.Loopback,0); reservation.Start();
        int port=((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        Endpoint=$"http://127.0.0.1:{port}";
        listener.Prefixes.Add(Endpoint+"/"); listener.Start();
        _=Task.Run(async ()=>
        {
            try
            {
                var context=await listener.GetContextAsync();
                Received.TrySetResult(); await release.Task;
                string content=JsonSerializer.Serialize(new {edits=new[]{new {original,replacement}}});
                byte[] response=Encoding.UTF8.GetBytes("data: "+JsonSerializer.Serialize(new {choices=new[]{new {delta=new {content},finish_reason="stop"}}})+"\n\ndata: [DONE]\n\n");
                context.Response.ContentType="text/event-stream";
                await context.Response.OutputStream.WriteAsync(response); context.Response.Close();
            }
            catch(Exception) when(!listener.IsListening) { }
        });
    }
    public void Release() => release.TrySetResult();
    public void Dispose() {release.TrySetResult();listener.Close();}
}
