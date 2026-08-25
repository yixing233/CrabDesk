using System.Net;
using System.Text;
using System.Text.Json;
using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class TavilySearchServiceTests
{
    [Fact]
    public async Task SearchesWithFixedHttpsEndpointAndBoundedPayload()
    {
        HttpRequestMessage? captured = null;
        string? requestBody = null;
        using var service = new TavilySearchService(new HttpClient(new StubHandler(async request =>
        {
            captured = request;
            requestBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""
                {"results":[{"title":"Official tool","content":"A desktop productivity utility."}]}
                """);
        })));

        var result = await service.SearchAsync(
            "tvly-key",
            [new AiClassificationInput("1", "Example Tool")]);

        Assert.Equal("https://api.tavily.com/search", captured!.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.Equal("tvly-key", captured.Headers.Authorization.Parameter);
        using var request = JsonDocument.Parse(requestBody!);
        Assert.Equal("Example Tool", request.RootElement.GetProperty("query").GetString());
        Assert.Equal("basic", request.RootElement.GetProperty("search_depth").GetString());
        Assert.Equal(3, request.RootElement.GetProperty("max_results").GetInt32());
        Assert.False(request.RootElement.GetProperty("include_raw_content").GetBoolean());
        Assert.False(request.RootElement.GetProperty("include_images").GetBoolean());
        Assert.False(request.RootElement.GetProperty("include_answer").GetBoolean());
        var evidenceItem = Assert.Single(result);
        Assert.Equal("1", evidenceItem.ItemKey);
        Assert.Contains("Official tool", evidenceItem.WebEvidence);
        Assert.Contains("productivity utility", evidenceItem.WebEvidence);
    }

    [Fact]
    public async Task LimitsSearchesToEightDistinctNames()
    {
        var requests = 0;
        using var service = new TavilySearchService(new HttpClient(new StubHandler(_ =>
        {
            requests++;
            return Task.FromResult(JsonResponse("""{"results":[{"title":"Official","content":"Utility"}]}"""));
        })));

        var items = Enumerable.Range(0, 12)
            .Select(index => new AiClassificationInput(index.ToString(), $"Tool {index}"))
            .ToArray();

        var result = await service.SearchAsync("tvly-key", items);

        Assert.Equal(TavilySearchService.MaxItemsPerRun, requests);
        Assert.Equal(TavilySearchService.MaxItemsPerRun, result.Count);
    }

    [Fact]
    public async Task DoesNotExposeTavilyErrorBody()
    {
        using var service = new TavilySearchService(new HttpClient(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("Bearer secret-value", Encoding.UTF8, "text/plain")
        }))));

        var error = await Assert.ThrowsAsync<AiWebSearchRequestException>(() =>
            service.SearchAsync("tvly-secret", [new AiClassificationInput("1", "Example Tool")]));

        Assert.Equal(AiWebSearchRequestException.SafeMessage, error.Message);
        Assert.DoesNotContain("secret", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request);
    }
}
