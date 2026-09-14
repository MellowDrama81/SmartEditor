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

    /// <summary>Number of upcoming "api/jobs/" poll requests to fail with a network-transport
    /// exception (as a real dropped connection would surface through <see cref="HttpClient"/>)
    /// before responding normally — simulates a transient blip mid-poll.</summary>
    public int FailNextPollAttempts { get; set; }
    public List<string?> AssetListCursorsSeen { get; } = [];
    public List<string?> AssetListLimitsSeen { get; } = [];

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
            if (FailNextPollAttempts > 0)
            {
                FailNextPollAttempts--;
                throw new HttpRequestException("Simulated transient network failure.");
            }

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

        if (path.EndsWith("api/object_info", StringComparison.Ordinal))
        {
            return JsonResponse("""
                {"LoadImage":{"input":{"required":{"image":["COMBO",{"options":["cloud-a.png","cloud-b.png"]}]}}}}
                """);
        }

        if (path.EndsWith("api/assets", StringComparison.Ordinal))
        {
            var cursor = uri.Query.Contains("cursor=", StringComparison.Ordinal)
                ? Uri.UnescapeDataString(uri.Query[(uri.Query.IndexOf("cursor=", StringComparison.Ordinal) + "cursor=".Length)..].Split('&')[0])
                : null;
            AssetListCursorsSeen.Add(cursor);

            var limit = uri.Query.Contains("limit=", StringComparison.Ordinal)
                ? uri.Query[(uri.Query.IndexOf("limit=", StringComparison.Ordinal) + "limit=".Length)..].Split('&')[0]
                : null;
            AssetListLimitsSeen.Add(limit);

            if (cursor is null)
            {
                // Page 1 of 2, to exercise pagination (has_more/next_cursor) end to end. Includes a
                // non-"input"/"output"-tagged asset (a model file, as real Cloud accounts return
                // alongside input/output images) to verify ComfyCloudClient filters those out.
                return JsonResponse("""
                    {"assets":[
                        {"id":"asset-1","name":"a.png","display_name":"A.png","loader_path":"hash-a.png","tags":["input"]},
                        {"id":"model-1","name":"big-model.safetensors","display_name":"big-model.safetensors","loader_path":"big-model.safetensors","tags":["models","diffusion_models"]},
                        {"id":"asset-2","name":"b.png","display_name":"B.png","loader_path":"hash-b.png","tags":["input"]}
                    ],"has_more":true,"next_cursor":"page2","total":4}
                    """);
            }

            return JsonResponse("""
                {"assets":[
                    {"id":"asset-3","name":"c.png","display_name":"C.png","loader_path":"hash-c.png","tags":["output"]}
                ],"has_more":false,"total":4}
                """);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
