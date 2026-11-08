using System;
using System.Collections.Generic;
using System.Linq;

namespace NPPAI.Engine;

public sealed class CorrectionJob
{
    public nint Document, Editor;
    public int Start;
    public string Text, Context;
    public bool Valid = true, Checked;
}

// UI-thread-owned anchors. Disjoint edits move anchors; edits inside invalidate the snapshot.
public sealed class CorrectionQueue
{
    readonly List<CorrectionJob> jobs = new();
    public IEnumerable<CorrectionJob> Jobs => jobs;
    public CorrectionJob Add(nint document, nint editor, int start, string text, string context)
    {
        var existing = jobs.FirstOrDefault(j => j.Valid && j.Document == document && j.Start == start && j.Text == text);
        if (existing != null) return existing;
        var job = new CorrectionJob { Document = document, Editor = editor, Start = start, Text = text, Context = context };
        jobs.Add(job);
        while (jobs.Count > 16) { jobs[0].Valid = false; jobs.RemoveAt(0); }
        return job;
    }
    public void Observe(nint document, int position, int removed, int added, Func<string,int> byteCount)
    {
        foreach (var job in jobs.Where(j => j.Valid && j.Document == document))
        {
            int end = job.Start + byteCount(job.Text);
            if (position + removed < job.Start) job.Start += added-removed;
            else if (position < end && position+removed >= job.Start || position == job.Start || position == end && job.Text.Length>0 && char.IsLetterOrDigit(job.Text[^1])) job.Valid = false;
        }
    }
    public void Clear() { foreach(var job in jobs) job.Valid=false; jobs.Clear(); }
}
