# SmartEditor user guide

## Configure a backend

Open **Settings** before generating.

### Self-hosted ComfyUI

Choose self-hosted mode and enter the URL of your ComfyUI server. The desktop default is `http://127.0.0.1:8188`. On Android, use a network address reachable from the phone rather than `127.0.0.1` unless ComfyUI runs on the phone itself.

### Comfy Cloud

Choose Comfy Cloud mode, use `https://cloud.comfy.org`, and provide a Comfy Cloud API key. SmartEditor uploads source images, submits workflows, polls the Cloud job, and downloads the produced result.

## Optional LLM setup

An LLM is optional. Configure an OpenAI-compatible base URL, API key, and model to let SmartEditor:

- choose a compatible workflow;
- refine the prompt for that workflow; and
- judge whether each image met the request, retrying within the configured iteration limit.

Without an LLM, select a workflow yourself. SmartEditor runs the selected workflow once per configured iteration, using a new seed each time.

## Generate an image

1. Create or select an editor tab.
2. Add source images if the intended workflow needs them. A request can contain up to eight images.
3. Optionally create a mask for an inpainting-compatible workflow.
4. Enter a prompt.
5. Choose a workflow, or let the configured LLM choose one.
6. Start generation.

The status text reports the current phase:

- **Planning**: the LLM is choosing/refining a workflow.
- **Queued**: Comfy Cloud accepted the job but has not started it.
- **Generating**: work has begun. A self-hosted server may also provide percentage progress.
- **Reviewing**: the LLM is evaluating an already-available result.

Each completed image appears in iteration history immediately, before LLM review ends. Use **View** or **Download** on any result, including an image that the LLM later marks unsatisfactory.

## Use the asset library

Open **Assets** from the application header to:

- browse input and output images available on the selected backend;
- add local image files;
- open a full-size preview;
- download an asset;
- add comma-separated tags for local filtering; and
- reuse an asset as a source image in an editor tab.

Thumbnails and full-size previews are cached locally within the limits selected in Settings. Setting either cache limit to `0` disables that cache.

## Android background behavior and recovery

While a generation or recovery is active on Android, SmartEditor uses a foreground-service notification. This gives the work priority while the app is backgrounded, but Android can still stop an app in exceptional conditions.

For Comfy Cloud, SmartEditor records the submitted job ID before waiting for completion. If the process is killed, the next app launch checks recorded jobs and adds completed results to Assets. A failed local journal write is shown as a warning; keep the app open until that generation completes because it cannot then be recovered automatically.

Self-hosted ComfyUI does not provide the durable Cloud job endpoint used for this recovery flow. If its app process is killed, submit the generation again if necessary.

## Troubleshooting

- **Cannot connect:** verify the backend URL, network connectivity, API key, and that the selected backend is correct.
- **A workflow is unavailable:** add compatible source-image counts and mask state, or select another workflow. Workflows can also be disabled in the workflow manager.
- **Result is visible but not in Assets:** the image can still be viewed/downloaded from iteration history. Open Assets and inspect its status message for a failed backend upload.
- **Recovery repeats after launch:** fix the backend credentials or network issue. A completed result stays in the recovery journal until it has been successfully added to Assets.
- **Settings or tags do not save:** check device storage and application-data permissions, then save again. API keys are not encrypted.
