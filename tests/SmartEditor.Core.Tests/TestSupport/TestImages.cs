namespace SmartEditor.Core.Tests.TestSupport;

internal static class TestImages
{
    /// <summary>A minimal valid 1x1 PNG, used wherever a test needs decodable image bytes.</summary>
    public static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
}
