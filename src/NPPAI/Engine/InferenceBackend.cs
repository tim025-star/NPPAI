using System;
using System.Collections.Generic;

namespace NPPAI.Engine;

public interface IInferenceBackend : IDisposable
{
    IReadOnlyList<string> Models { get; }
    string Status { get; }
    LlamaClient CreateClient(string model, int timeoutMs);
}
