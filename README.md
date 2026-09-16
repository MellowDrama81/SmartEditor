# SmartEditor

SmartEditor is a cross-platform desktop and Android image-generation editor built on Avalonia and ComfyUI-compatible backends. It can run against a self-hosted ComfyUI server or Comfy Cloud, optionally using an OpenAI-compatible LLM to choose workflows, refine prompts, and evaluate each generated image.

## Highlights

- Generate, edit, inpaint, upscale, remove backgrounds, and use structural-control workflows.
- Use zero to eight source images where a workflow supports them.
- Browse, tag, view, download, and reuse backend image assets.
- See planning, queue, generation, and review state during LLM-guided runs.
- View and download each result as soon as it is generated; LLM evaluation continues afterward.
- Continue an active Android generation in the background through a foreground-service notification.
- Recover submitted Comfy Cloud jobs after an Android process is killed or the app restarts.

## Getting started

1. Build and start the desktop app:

   ```powershell
   dotnet run --project src/SmartEditor.App.Desktop/SmartEditor.App.Desktop.csproj
   ```

2. Open **Settings** and select either:

   - **Self-hosted ComfyUI** — enter the reachable ComfyUI URL, such as `http://127.0.0.1:8188`.
   - **Comfy Cloud** — enter `https://cloud.comfy.org` and a Comfy Cloud API key.

3. Optionally configure an OpenAI-compatible LLM endpoint, API key, and model. Without an LLM, select a workflow manually and SmartEditor still generates images.

4. Add source images, write a prompt, select a workflow if needed, then choose **Generate**.

See [the user guide](docs/user-guide.md) for normal use and [the technical documentation](docs/technical-architecture.md) for architecture, recovery behavior, and development guidance.

## Development

Requirements:

- .NET 10 SDK
- For Android: the .NET Android workload and an Android SDK compatible with `net10.0-android36.0`
- A reachable ComfyUI backend for live generation

Useful checks:

```powershell
dotnet test tests/SmartEditor.Core.Tests/SmartEditor.Core.Tests.csproj --no-restore
dotnet build src/SmartEditor.App/SmartEditor.App.csproj --no-restore -p:DesignTimeBuild=true
```

## Security and data

Settings, including API keys, are stored locally as plain JSON under the platform application-data directory. Treat a shared device as untrusted. Source images and generated results are sent to the configured ComfyUI backend; use a backend and account appropriate for the content you process.

## Additional reference

- [Model capabilities](docs/model-capabilities.md)
- [Comfy Cloud image models and workflow coverage](docs/comfy-cloud-image-models.md)
