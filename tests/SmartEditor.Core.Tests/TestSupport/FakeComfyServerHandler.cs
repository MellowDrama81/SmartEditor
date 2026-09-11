using System.Net;
using System.Text;

namespace SmartEditor.Core.Tests.TestSupport;

/// <summary>A fake ComfyUI HTTP server used to verify <c>ComfyUiClient</c>'s request shapes
/// without a real ComfyUI instance.</summary>
internal sealed class FakeComfyServerHandler : HttpMessageHandler
{
    public int UploadCount { get; private set; }
    public string? LastPromptBody { get; private set; }

    /// <summary>The raw JSON array rendered as the "image" input's combo options in the fake
    /// object_info response, e.g. <c>["a.png","b.png"]</c> (older plain-list shape). Override to
    /// <c>"\"COMBO\",{\"options\":[\"a.png\"]}"</c> to exercise the newer COMBO shape instead.</summary>
    public string ObjectInfoImageListJson { get; set; } = """["input-a.png","input-b.png"]""";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;

        if (path.EndsWith("upload/image", StringComparison.Ordinal))
        {
            UploadCount++;
            return JsonResponse($$"""{"name":"uploaded-{{UploadCount}}.png","subfolder":"","type":"input"}""");
        }

        if (path.EndsWith("prompt", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
        {
            LastPromptBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse("""{"prompt_id":"test-prompt-1"}""");
        }

        if (path.Contains("history/", StringComparison.Ordinal))
        {
            return JsonResponse("""
                {"test-prompt-1":{"status":{"status_str":"success","completed":true},"outputs":{"9":{"images":[{"filename":"result.png","subfolder":"","type":"output"}]}}}}
                """);
        }

        if (path.EndsWith("view", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) };
        }

        if (path.EndsWith("object_info", StringComparison.Ordinal))
        {
            return JsonResponse(
                """{"LoadImage":{"input":{"required":{"image":[""" +
                ObjectInfoImageListJson +
                """,{"image_upload":true}]}}}}""");
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
