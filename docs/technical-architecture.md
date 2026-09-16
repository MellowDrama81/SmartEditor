# Technical architecture

## Projects

| Project | Responsibility |
|---|---|
| `src/SmartEditor.Core` | Backend abstractions, Comfy clients, workflow binding, LLM planning/judging, models, and workflow metadata. |
| `src/SmartEditor.App` | Shared Avalonia UI, view models, local stores/caches, dependency injection, and recovery coordination. |
| `src/SmartEditor.App.Desktop` | Desktop executable host. |
| `src/SmartEditor.App.Android` | Android executable host, manifest, and foreground service. |
| `tests/SmartEditor.Core.Tests` | Core unit tests, including Comfy Cloud HTTP behavior. |

## Generation flow

`EditorViewModel` owns one user run and its cancellation token. It starts an `IGenerationKeepAlive` operation, then chooses one of two paths:

1. **LLM-guided**: `ImageEditOrchestrator` runs a bounded plan → generate → judge loop. `ImageEditPlanner` selects a compatible workflow and prompt; `ImageEditJudge` evaluates the returned bytes. The orchestrator reports a result before judging, so the UI can display and upload it without waiting for the judge.
2. **Manual/no-LLM**: the selected workflow runs for the configured iteration count, with a new random seed for every run.

Both paths invoke `IComfyUiClient.RunWorkflowAsync`. The common client base uploads inputs, substitutes workflow tokens, submits the graph, waits for completion, and downloads the first output image.

## Backend adapters

`ComfyUiClientBase` contains backend-independent workflow substitution and execution. Concrete clients implement transport operations.

- **Self-hosted ComfyUI** uses its normal endpoints and can report best-effort percentage progress through its WebSocket feed.
- **Comfy Cloud** uses API-key HTTP requests. It exposes coarse `waiting_to_dispatch`/`pending`/`in_progress` job states and has no percentage-progress feed. Redirected result downloads are followed manually so the Cloud API key is not forwarded to the signed third-party storage URL.

`ComfyWorkflowException.IsTerminal` distinguishes an outcome known to be final (for example, a cancelled/missing job) from a retryable local, network, authentication, or server condition.

## Progress and immediate results

`EditRunProgress` drives the editor status through Planning, Queued, Generating, and Judging stages. `ComfyJobUpdate` carries durable Cloud job IDs independently of UI progress.

The orchestrator emits a provisional `EditIteration` containing the image bytes immediately after Comfy returns. The editor adds it to history and starts the asset-library upload without blocking the LLM judge. The final judge result replaces that same iteration display.

## Cloud recovery

`GenerationRecoveryStore` persists a deduplicated list of submitted Comfy Cloud job IDs in application data. Writes use `AtomicFile`, which writes a uniquely named temporary file and replaces the destination only after the temporary write succeeds.

Critical lifecycle updates use `SynchronousProgress<ComfyJobUpdate>` so the job ID is written inline after submission rather than being delayed on the UI queue. Journal errors are deliberately non-fatal: a submitted remote job must continue even if device storage is unavailable, and the user is warned that automatic recovery is unavailable.

On startup, `GenerationRecoveryService` reads pending IDs and calls `RecoverWorkflowAsync` for each. It removes an ID only after the result has been successfully inserted into Assets, or after the backend reports a terminal job outcome. Retryable failures retain the ID and report a status message. A journal-removal error is isolated so other jobs still process.

Recovery is supported only when `IComfyUiClient.SupportsJobRecovery` is true, currently Comfy Cloud. Both LLM-guided and manual runs pass lifecycle updates for supported backends.

## Android lifecycle

`AndroidGenerationKeepAlive` tracks active operations by GUID. It starts `GenerationForegroundService` when the first operation begins and stops it only after the last one ends, preventing one editor tab from stopping another tab's background protection. The service uses Android's `dataSync` foreground-service type and updates a persistent notification with the latest operation status.

Foreground execution improves survivability; it is not a guarantee against all Android process termination. Cloud recovery is the durable fallback.

## Local persistence and caches

- `AppSettingsStore` stores backend, LLM, and cache settings. It updates `Current` only after a successful atomic write.
- `AssetTagsStore` maintains local filename-to-tag mappings and raises a save-failed event for the asset UI.
- `AssetThumbnailCache` bounds thumbnail and full-resolution local caches according to Settings.
- Asset view models dispose Avalonia bitmaps when refreshes replace items, avoiding retention of stale thumbnails and full-size images.

All local persistence is best-effort and application-data failures are surfaced to the user rather than crashing normal execution paths.

## Adding a workflow

Workflow definitions and graph JSON live in `src/SmartEditor.Core/Workflows`. A workflow declares supported image counts, mask requirements, and placeholder tokens. The common client replaces prompt, seed, uploaded-image, and optional mask-image placeholders before submitting the graph.

When adding a bundled workflow, also add a matching entry to `src/SmartEditor.Core/Guidance/model-guidance.json`. The catalog tests enforce coverage for bundled workflow IDs. See [model capabilities](model-capabilities.md) for routing guidance and [Comfy Cloud workflow coverage](comfy-cloud-image-models.md) for supported model/input combinations.

## Verification

Run the core test suite and build the shared app project:

```powershell
dotnet test tests/SmartEditor.Core.Tests/SmartEditor.Core.Tests.csproj --no-restore
dotnet build src/SmartEditor.App/SmartEditor.App.csproj --no-restore -p:DesignTimeBuild=true
```

The Android target additionally requires the installed Android workload, matching SDK platforms, and a device/emulator for lifecycle validation.
