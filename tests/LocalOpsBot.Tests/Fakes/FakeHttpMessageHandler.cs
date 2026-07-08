using System.Net;
using System.Text;

namespace LocalOpsBot.Tests.Fakes;

public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();

    public List<HttpRequestMessage> Requests { get; } = new();

    public void QueueResponse(HttpStatusCode status, string content)
    {
        _responses.Enqueue(new HttpResponseMessage(status)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        });
    }

    // Enqueue an arbitrary content body (e.g. binary zip bytes or a plain-text checksum).
    public void QueueContent(HttpStatusCode status, HttpContent content)
    {
        _responses.Enqueue(new HttpResponseMessage(status) { Content = content });
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        var response = _responses.TryDequeue(out var next)
            ? next
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        return Task.FromResult(response);
    }
}
