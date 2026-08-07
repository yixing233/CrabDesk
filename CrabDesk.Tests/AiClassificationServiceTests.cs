using System.Net;
using System.Text;
using System.Text.Json;
using CrabDesk.Core;

namespace CrabDesk.Tests;

public sealed class AiClassificationServiceTests
{
    [Fact]
    public async Task LoadsCommonOpenAiModelList()
    {
        HttpRequestMessage? captured = null;
        using var client = new HttpClient(new StubHandler(request =>
        {
            captured = request;
            return JsonResponse("""{"data":[{"id":"model-b"},{"id":"model-a"}]}""");
        }));
        using var service = new AiClassificationService(client);

        var models = await service.GetModelsAsync(new AiClassificationSettings
        {
            BaseUrl = "https://models.example/v1",
            ApiKey = "secret"
        });

        Assert.Equal(["model-a", "model-b"], models);
        Assert.Equal("/v1/models", captured!.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.Equal("secret", captured.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task ClassifiesNamesUsingOnlyProvidedLabels()
    {
        string? requestBody = null;
        using var client = new HttpClient(new StubHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""
            {
              "choices": [{
                "message": {
                  "content": "```json\n{\"items\":[{\"id\":\"0\",\"label\":\"开发工具\"},{\"id\":\"1\",\"label\":\"游戏\"}]}\n```"
                }
              }]
            }
            """);
        }));
        using var service = new AiClassificationService(client);
        var settings = new AiClassificationSettings
        {
            BaseUrl = "https://models.example/v1",
            Model = "model-a",
            CustomPrompt = "按用途分类"
        };

        var result = await service.ClassifyAsync(
            settings,
            [
                new AiClassificationInput("path:code", "Visual Studio Code"),
                new AiClassificationInput("path:game", "Steam")
            ],
            ["开发工具", "游戏"]);

        Assert.Equal(2, result.Count);
        Assert.Equal("path:code", result[0].ItemKey);
        Assert.Equal("开发工具", result[0].Label);
        using var requestJson = JsonDocument.Parse(requestBody!);
        var messages = requestJson.RootElement.GetProperty("messages");
        Assert.Contains("按用途分类", messages[0].GetProperty("content").GetString());
        Assert.Contains("Visual Studio Code", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task TestsSelectedModelConnectivityWithMinimalChatRequest()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        using var client = new HttpClient(new StubHandler(async request =>
        {
            captured = request;
            capturedBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse("{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}");
        }));
        using var service = new AiClassificationService(client);

        await service.TestModelConnectivityAsync(new AiClassificationSettings
        {
            BaseUrl = "https://models.example/v1",
            Model = "model-a"
        });

        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("/v1/chat/completions", captured.RequestUri!.AbsolutePath);
        Assert.Contains("model-a", capturedBody);
        Assert.Contains("max_tokens", capturedBody);
    }

    [Fact]
    public async Task IgnoresUnknownLabelsAndDuplicateIds()
    {
        using var client = new HttpClient(new StubHandler(_ => JsonResponse("""
        {
          "choices": [{"message": {"content": "{\"items\":[{\"id\":\"0\",\"label\":\"未知\"},{\"id\":\"0\",\"label\":\"工作\"}]}"}}]
        }
        """)));
        using var service = new AiClassificationService(client);

        var result = await service.ClassifyAsync(
            new AiClassificationSettings
            {
                BaseUrl = "https://models.example/v1",
                Model = "model-a"
            },
            [new AiClassificationInput("path:item", "Item")],
            ["工作"]);

        Assert.Single(result);
        Assert.Equal("工作", result[0].Label);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            : this(request => Task.FromResult(handler(request)))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }
}
