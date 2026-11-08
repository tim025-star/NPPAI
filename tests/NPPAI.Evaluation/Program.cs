using System.Diagnostics;
using System.Text.Json;
using NPPAI;
using NPPAI.Engine;

string output=args.FirstOrDefault() ?? "artifacts/prediction-evaluation.json";
string[] cases={
    "FP. Entra logs look okay, IPs are clean.\nThe user appears to be us",
    "The user is based in the UK and likely uses a ",
    "AU is Palo, NZ is an RBNZ IP and UK is an ISP.\nThe locations may be explained by ",
    "MFA passed and the IPs are clean.\nThere is no evidence of ",
    "Sign-in locations include AU, NZ and UK. This may indicate ",
    "FP. Entra logs look ",
    "At this time, we are unable to ",
    "Need to confirm with the user whether ",
    "The user appears to be using software to log in from ",
    "Proxy use is suspected but has not been ",
};
using var runtime = new LlamaRuntime(Path.GetFullPath("artifacts/NPPAI"));
var rows=new List<object>();
if(args.Contains("--hello")) cases=new[]{"hello","hel","hello ","Hello"};
foreach(string model in args.Contains("--hello")?new[]{"qwen2.5:3b"}:new[]{"qwen2.5:3b","qwen3.5:9b"})
{
    using var client=runtime.CreateClient(model,90000);
    // Warm-up is deliberately excluded; cold-loading time is not typing latency.
    await client.StreamChatAsync(model,new(){new(){role="user",content="Reply OK"}},0,16,null,default);
    foreach(string profile in new[]{"baseline","soc"})
    foreach(var item in cases.Select((text,index)=>(text,index)))
    {
        var ctx=new CollectedContext{Text=item.text};
        var messages=profile=="soc"?PromptBuilder.Build(ctx,false,null,null,null,"soc",6):Baseline(item.text);
        var watch=Stopwatch.StartNew(); long? firstUsable=null; string streamed="";
        string raw=await client.StreamChatAsync(model,messages,0,profile=="soc"?88:80,delta=>{
            streamed+=delta;
            if(firstUsable==null && (profile=="soc"?PredictionResponse.Decode(streamed,item.text,6,"soc"):Completion.Clean(streamed,item.text,20)).Length>0) firstUsable=watch.ElapsedMilliseconds;
        },default,profile=="soc"?PredictionResponse.Format(item.text):null);
        string completion=profile=="soc"?PredictionResponse.Decode(raw,item.text,6,"soc"):Completion.Clean(raw,item.text,20);
        rows.Add(new{model,profile,caseId=item.index+1,context=item.text,raw,completion,firstUsableMs=firstUsable,totalMs=watch.ElapsedMilliseconds});
        Console.WriteLine($"{model} {profile} {item.index+1}: [{completion}] ({firstUsable} ms first / {watch.ElapsedMilliseconds} ms full)");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output,JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
    }
}
static List<ChatMessage> Baseline(string context)
{
    string word=Completion.WordPrefix(context);
    string system="You are an inline writing autocomplete. Finish the text naturally. Reply with text only, no quotes or explanations.\n";
    system+=word.Length>0?"The text ends inside a word. Your reply MUST start with that entire word, completing it if needed, then a short continuation. Do not repeat earlier text.\nExamples: input 'I will inves' -> reply 'investigate the issue.'; input 'Thanks for' -> reply 'for the update.'\nStart your reply with: "+word+"\n":"Return only the next few words. Do not repeat the input text.\n";
    return new(){new(){role="system",content=system},new(){role="user",content=context}};
}
