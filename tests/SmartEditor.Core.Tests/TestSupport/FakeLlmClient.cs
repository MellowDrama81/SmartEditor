using SmartEditor.Core.Abstractions;

namespace SmartEditor.Core.Tests.TestSupport;

internal sealed class FakeLlmClient : ILlmClient
{
    private readonly Queue<string> _responses;
    public List<LlmRequest> Requests { get; } = [];

    public FakeLlmClient(params string[] responses)
    {
        _responses = new Queue<string>(responses);
    }

    public Task<string> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("FakeLlmClient ran out of scripted responses.");
        }

        return Task.FromResult(_responses.Dequeue());
    }
}
