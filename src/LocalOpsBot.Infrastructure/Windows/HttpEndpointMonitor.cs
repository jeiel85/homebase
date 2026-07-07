using System.Diagnostics;
using System.Net.Http;
using LocalOpsBot.Core.Monitoring;

namespace LocalOpsBot.Infrastructure.Windows;

public sealed class HttpEndpointMonitor : IHttpEndpointMonitor
{
    private readonly IHttpClientFactory _httpClientFactory;

    public HttpEndpointMonitor(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<HttpEndpointResult> CheckAsync(HttpEndpointConfig config, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var client = _httpClientFactory.CreateClient("DevMonitor");
            client.Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);

            var method = new HttpMethod(config.Method ?? "GET");
            var request = new HttpRequestMessage(method, config.Url);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            sw.Stop();

            var expectedCodes = config.ExpectedStatusCodes ?? [200];
            var success = expectedCodes.Length == 0 || expectedCodes.Contains((int)response.StatusCode);

            return new HttpEndpointResult(
                config.Name, config.Url, success,
                (int)response.StatusCode, sw.ElapsedMilliseconds,
                success ? null : $"Unexpected status code {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new HttpEndpointResult(
                config.Name, config.Url, false,
                null, sw.ElapsedMilliseconds, ex.Message);
        }
    }
}
