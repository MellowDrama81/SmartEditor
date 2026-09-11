using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace SmartEditor.Core.Tests.TestSupport;

/// <summary>A fake Comfy Cloud HTTP server used to verify <c>ComfyCloudClient</c>'s request
/// shapes (the <c>/api/</c> prefix, flat job status, and the <c>/api/view</c> redirect to a
/// third-party host) without a real Cloud account.</summary>
internal sealed class FakeCloudServerHandler : HttpMessageHandler
{
    public int UploadCount { get; private set; }
    public string? LastPromptBody { get; private set; }
    public List<string?> ApiKeysSeen { get; } = [];
    public string JobStatus { get; set; } = "completed";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ApiKeysSeen.Add(request.Headers.TryGetValues("X-API-Key", out var values) ? values.FirstOrDefault() : null);

        var uri = request.RequestUri!;

        if (uri.Host == "storage.googleapis.com")
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([9, 9, 9, 9]) };
        }

        var path = uri.AbsolutePath;

        if (path.EndsWith("api/upload/image", StringComparison.Ordinal))
        {
            UploadCount++;
            return JsonResponse($$"""{"name":"cloud-uploaded-{{UploadCount}}.png"}""");
        }

        if (path.EndsWith("api/prompt", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
        {
            LastPromptBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse("""{"node_errors":{},"prompt_id":"cloud-job-1"}""");
        }

        if (path.Contains("api/jobs/", StringComparison.Ordinal))
        {
            if (JobStatus is "success" or "completed")
            {
                var body = new JsonObject
                {
                    ["status"] = JobStatus,
                    ["outputs"] = new JsonObject
                    {
                        ["9"] = new JsonObject
                        {
                            ["images"] = new JsonArray
                            {
                                new JsonObject { ["filename"] = "result.png", ["subfolder"] = "", ["type"] = "output" },
                            },
                        },
                    },
                };
                return JsonResponse(body.ToJsonString());
            }

            return JsonResponse(new JsonObject { ["status"] = JobStatus }.ToJsonString());
        }

        if (path.EndsWith("api/view", StringComparison.Ordinal))
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://storage.googleapis.com/fake-bucket/result.png?sig=abc");
            return response;
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
