# Image models available on Comfy Cloud

For what each of these models is actually *good and bad at* (as opposed to how many images it
accepts), see [`docs/model-capabilities.md`](model-capabilities.md) — that document, and the
`src/SmartEditor.Core/Guidance/model-guidance.json` it's sourced from, are what the planning LLM
reads to choose between workflows backed by different models.

The image-generation workflows bundled with the sibling `SlopFactory` project
(`Mellow.SlopFactory.Core/Domain/ComfyBuiltInWorkflows.cs`, confirmed usable against `cloud.comfy.org`
for at least the pose/edge/depth/normal extractors) each wire up a specific underlying model. This
document looks past that wiring to the **models themselves** — how many input/reference images each
one actually supports per its own official documentation — since a workflow template often uses far
fewer images than the model is actually capable of.

Every row below is sourced from the model creator's own site, model card, or API docs (Hugging Face,
official blog/announcement, or the hosting provider's own API reference for the two commercial hosted
models). Where I had to rely on a secondary or community source because no official one exists, that's
called out explicitly — treat those rows as less certain.

## Image generation / editing models

| Model | Creator | Documented input images | ComfyUI workflow wires up | Note |
|---|---|---|---|---|
| **FLUX.2 [dev]** | Black Forest Labs | **Min 0, max 10** (up to 8 via API, up to 10 in BFL's own playground) | 0–1 | The bundled workflow badly underuses this model — it's documented to handle up to 8-10 simultaneous references, not 1. |
| **FLUX.2 [klein]** | Black Forest Labs | Min 0; **no official numeric max** — BFL's announcement describes "multi reference generation" qualitatively but never states a cap, for either the 4B or 9B size | 1 (single edit) / 2 (dual edit, masked reference) | Don't assume it matches FLUX.2 dev's documented 8-10; that number is dev-specific and BFL hasn't published Klein's own ceiling. |
| **Krea 2 / Krea 2 Turbo** | Krea AI | Min 0 (pure text-to-image); Krea's own docs describe "Style References" supporting **multiple simultaneous** reference images (uncapped number stated), plus a "Moodboards" mode analyzing 4+ images | 1 | The ComfyUI workflow uses the community `krea2_turbo_style_reference` LoRA (by ostris, not Krea AI), whose own card says it was trained for **1-2** references — narrower than Krea's own multi-image feature. |
| **Qwen-Image-Edit-2511** | Alibaba / Qwen | Min 1; **no explicit cap restated in the 2511 card itself** — its code example uses 2 images. Its direct predecessor, Qwen-Image-Edit-2509, explicitly documents **"optimal performance... with 1 to 3 input images"** with a 3-image showcase; 2511 is described as a consistency refinement of the same multi-image capability, not a documented reduction | 2 | Best-sourced answer: **up to 3** is the documented sweet spot (inherited from 2509), so the workflow's 2-image wiring also leaves capability on the table. |
| **Qwen Image + InstantX Union ControlNet** | Qwen (base model) + InstantX (ControlNet) | **1** control/guide image is the documented default (`control_image=` is singular in InstantX's own card); the card mentions combining multiple conditions is possible only via an external diffusers PR, not as a supported first-class path | 1 | Matches the model's documented default exactly — no underutilization here. |
| **Z-Image Turbo** | Tongyi-MAI (Alibaba Tongyi Lab) | **0** — pure text-to-image per its Hugging Face card; image-conditioning only exists in sibling models (Z-Image-Edit, Z-Image-Omni-Base), not Turbo | 0 | Matches. |
| **NetaYume Lumina 3.5** | Neta.art Lab (built on Alpha-VLLM's Lumina-Image-2.0); the "3.5" tag is a community re-upload, not confirmed as an official Neta.art release | **0** — pure text-to-image, no img2img/reference conditioning documented | 0 | Matches. Not imported into SmartEditor. |
| **NewBie-Image Exp 0.1** | NewBie-AI | **0** — pure text-to-image (Gemma3-4B-it + Jina CLIP v2 text encoding, FLUX.1-dev VAE), no image-conditioning path documented | 0 | Matches. Not imported into SmartEditor. |
| **ByteDance Seedream 4.5** *(hosted — ComfyUI API Node)* | ByteDance | **Min 1, max 10** per fal.ai's API docs for the Seedream 4.5 edit endpoint ("reference up to 10 images per edit; last 10 used if more provided") — this is a reseller's documentation of ByteDance's API, not ByteDance's own doc site, which I couldn't find an English version of | 1 | Substantially underuses the documented 10-image capacity. |
| **Nano Banana Pro (Gemini 3 Pro Image)** *(hosted — ComfyUI API Node)* | Google | **Min 0, max 14** per Google's own Gemini API docs — with sub-caps of ≤6 images of objects and ≤5 images of people/characters (subsets of the 14, not additive); Google notes the exact number "varies by surface" (app vs. AI Studio vs. Vertex vs. direct API) | 2 | Substantially underuses the documented (surface-dependent) capacity of up to 14. |

## Preprocessing / control-guide extraction utilities

These extract a structural guide from one image for use as the guide input to a ControlNet
generation workflow above — they don't generate a new image from a prompt, so "input images" isn't
a capability ceiling in the same sense (they're single-image tools by design, not artificially
limited).

| Model | Purpose | Input images |
|---|---|---|
| DWPose | Extract an OpenPose-style body/hand/face skeleton | 1 |
| Canny | Extract a high-contrast edge map | 1 |
| Lineart | Extract clean contour lineart | 1 |
| Depth Anything V2 | Extract a relative depth map | 1 |
| BAE | Extract a surface-normal map | 1 |
| 4x-UltraSharp (`upscale-with-model`) | Upscale an image 4x with sharpened detail | 1 |
| Rembg (`remove-background`) | Cut a subject out onto a transparent background | 1 |

The last two aren't ControlNet-guide extractors, but the same "deterministic single-image utility,
not a generative choice" category — added and live-verified 2026-09-11.

## Per-model workflow coverage and live test status

Goal: one bundled workflow per model per supported source-image count. The app's `EditRequest`
hard-caps a single request at **8** source images, so the enumeration below stops at 8 even where the
model documents more. Qwen-Image-Edit-2511 and Krea 2 additionally stop at 3 because the
`TextEncodeQwenImageEditPlus` node they rely on only exposes `image1`–`image3`.

**Test method.** Each workflow was submitted live to `cloud.comfy.org` (real `X-API-Key`, real
credit spend) via a harness that mirrors `SmartEditor.Core`'s `ComfyCloudClient` (upload → placeholder
substitution → `POST /api/prompt` → poll `GET /api/jobs/{id}` → fetch output). Synthetic fixture
images were used — a pass means the graph submits cleanly, executes on a Cloud worker, and returns an
output image; it is **not** a quality judgement. Runs on **2026-09-10 / 2026-09-11**.

Legend: ✅ created + live-verified · 🟡 created, not yet verified (Comfy Cloud account credit balance
was exhausted mid-batch on 2026-09-11 — these live in `src/SmartEditor.Core/WorkflowsPending/`, not
the bundled `Workflows/` folder) · — not applicable.

| Model | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | Workflow id pattern |
|---|:--:|:--:|:--:|:--:|:--:|:--:|:--:|:--:|:--:|---|
| Z-Image Turbo | ✅ | — | — | — | — | — | — | — | — | `z-image-turbo` |
| FLUX.2 dev | ✅¹ | ✅¹ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | `flux2` (1), `flux2-{n}img` (0, 2–8), plus masked inpainting `flux2-inpaint` (added and live-verified 2026-09-11) |
| FLUX.2 klein 9B | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | `flux2-klein-edit-single`/`-double`, `flux2-klein-{n}img` (0, 3–8) |
| Krea 2 Turbo (style ref) | — | ✅ | ✅ | ✅ | — | — | — | — | — | `krea2-style-reference`, `krea2-style-reference-{n}img` (2–3) |
| Krea 2 (hosted API Node) | — | ✅ | ✅ | ✅ | — | — | — | — | — | `api-krea2-style-reference-{n}img` (1–3) — Krea's own hosted model, distinct from the local LoRA above; underlying node documents chaining up to 10 style references; added and live-verified 2026-09-11 |
| Qwen-Image-Edit-2511 | — | ✅ | ✅ | ✅ | — | — | — | — | — | `qwen-image-edit-2511` (2), `qwen-image-edit-2511-{n}img` (1, 3) |
| Qwen Image + InstantX Union CN | — | ✅ | — | — | — | — | — | — | — | `qwen-instantx-union-controlnet`, plus one-shot photo→guide→generate variants `qwen-dwpose-union-controlnet` (pose), `qwen-canny-union-controlnet`, `qwen-depth-union-controlnet`, `qwen-lineart-union-controlnet`, `qwen-normal-union-controlnet` — the last 4 added and live-verified 2026-09-11 |
| Qwen Image + PAI Fun-Controlnet-Union | — | ✅ | — | — | — | — | — | — | — | `qwen-fun-union-controlnet` — broader control-type coverage (adds MLSD/scribble/built-in inpainting) than InstantX Union; added and live-verified 2026-09-11, see `docs/model-capabilities.md` |
| Qwen Image + DiffSynth model-patch CN | — | ✅ | — | — | — | — | — | — | — | `qwen-diffsynth-canny-controlnet`, `qwen-diffsynth-depth-controlnet`, `qwen-diffsynth-inpaint` — a third, independently-trained mechanism (model-patch, not classic ControlNet); added and live-verified 2026-09-11 |
| ByteDance Seedream 4.5 | — | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | `api-bytedance-seedream4`, `api-bytedance-seedream4-{n}img` |
| Nano Banana Pro | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | `api-nano-banana-pro` (2), `api-nano-banana-pro-{n}img` |

¹ `flux2.json` handles 1 image only — its graph has no text-only path — so `minImages` was
corrected to `1` and the 0-image case is served by the separate `flux2-0img` graph instead.

Masked / control-guide workflows (all pre-existing, all live-verified ✅ on 2026-09-10):
`flux2-klein-inpaint-reference`, `qwen-image-edit-2511-inpainting`, `qwen-image-instantx-inpainting`,
`qwen-dwpose-union-controlnet`, and the DWPose / Canny / Lineart / Depth / BAE-normal extractors.

**Every planned cell in the 0–8 matrix is now live-verified.** Verified workflows added to the
bundle (34 total): `api-bytedance-seedream4-{2..8}img` (2026-09-10),
`api-nano-banana-pro-{0,1,3,4,5,6}img` (2026-09-10), `flux2-0img`, `flux2-{2..8}img` (2026-09-11,
after a Comfy Cloud credit top-up), `flux2-klein-{0,3,4,5,6,7,8}img` (2026-09-11 — the 0-image run
hit one transient `ServiceError: RIP to the server your workflow was running on` worker crash and
passed on retry), `krea2-style-reference-{2,3}img`, `qwen-image-edit-2511-{1,3}img`, and
`api-nano-banana-pro-{7,8}img` (2026-09-11). `src/SmartEditor.Core/WorkflowsPending/` is now empty
of graphs (kept, with its README, as the holding area for any future variant).

Multi-image wiring used: partner-node models (Seedream, Nano Banana Pro) batch their `LoadImage`
nodes through `BatchImagesNode`; FLUX.2 dev/klein chain one `VAEEncode` → `ReferenceLatent` per
reference image; Qwen/Krea pass `image1`–`image3` into `TextEncodeQwenImageEditPlus`.

## Key takeaway

For four of these models — **FLUX.2 dev, Qwen-Image-Edit-2511, ByteDance Seedream, and Nano Banana
Pro** — the *originally* bundled ComfyUI workflow accepted far fewer images than the underlying model
supports. That was a workflow-design choice, not a model limitation, and it has now been addressed by
adding per-image-count variants (see the table above) rather than swapping models: the workflows'
`LoadImage` nodes and placeholder-token wiring were extended. Every model's full documented range up
to the app's 8-image cap — Seedream 1–8, Nano Banana Pro 0–8, FLUX.2 dev 0–8, FLUX.2 klein 0–8,
Qwen-Image-Edit 1–3, and Krea 2 1–3 — is now live-verified against `cloud.comfy.org`.

## Sources

- FLUX.2 dev: [bfl.ai/blog/flux-2](https://bfl.ai/blog/flux-2), [docs.bfl.ai/flux_2/flux2_image_editing](https://docs.bfl.ai/flux_2/flux2_image_editing), [huggingface.co/black-forest-labs/FLUX.2-dev](https://huggingface.co/black-forest-labs/FLUX.2-dev)
- FLUX.2 klein: [bfl.ai/blog/flux2-klein-towards-interactive-visual-intelligence](https://bfl.ai/blog/flux2-klein-towards-interactive-visual-intelligence), [huggingface.co/black-forest-labs/FLUX.2-klein-4B](https://huggingface.co/black-forest-labs/FLUX.2-klein-4B)
- Krea 2: [krea.ai/docs/user-guide/features/krea](https://www.krea.ai/docs/user-guide/features/krea); style-reference LoRA: [huggingface.co/ostris/krea2_turbo_style_reference](https://huggingface.co/ostris/krea2_turbo_style_reference)
- Qwen-Image-Edit-2511: [huggingface.co/Qwen/Qwen-Image-Edit-2511](https://huggingface.co/Qwen/Qwen-Image-Edit-2511); Qwen-Image-Edit-2509 (predecessor, states the "1 to 3" figure): [huggingface.co/Qwen/Qwen-Image-Edit-2509](https://huggingface.co/Qwen/Qwen-Image-Edit-2509); base model: [github.com/QwenLM/Qwen-Image](https://github.com/QwenLM/Qwen-Image)
- InstantX Qwen-Image ControlNet Union: [huggingface.co/InstantX/Qwen-Image-ControlNet-Union](https://huggingface.co/InstantX/Qwen-Image-ControlNet-Union)
- Z-Image Turbo: [huggingface.co/Tongyi-MAI/Z-Image-Turbo](https://huggingface.co/Tongyi-MAI/Z-Image-Turbo)
- NetaYume Lumina: [huggingface.co/neta-art/Neta-Lumina](https://huggingface.co/neta-art/Neta-Lumina) (official org card); "3.5" community variant: [huggingface.co/duongve/NetaYume-Lumina-Image-2.0-Diffusers-v35-pretrained](https://huggingface.co/duongve/NetaYume-Lumina-Image-2.0-Diffusers-v35-pretrained)
- NewBie-Image Exp 0.1: [huggingface.co/NewBie-AI/NewBie-image-Exp0.1](https://huggingface.co/NewBie-AI/NewBie-image-Exp0.1)
- ByteDance Seedream 4.5: [fal.ai/models/fal-ai/bytedance/seedream/v4.5/edit](https://fal.ai/models/fal-ai/bytedance/seedream/v4.5/edit) (reseller documentation, not ByteDance's own site)
- Nano Banana Pro / Gemini 3 Pro Image: [ai.google.dev/gemini-api/docs/gemini-3](https://ai.google.dev/gemini-api/docs/gemini-3), [ai.google.dev/gemini-api/docs/image-generation](https://ai.google.dev/gemini-api/docs/image-generation), [blog.google prompting tips](https://blog.google/products-and-platforms/products/gemini/prompting-tips-nano-banana-pro/)
