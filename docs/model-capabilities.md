# Model capabilities: what each one is good and bad at

`docs/comfy-cloud-image-models.md` covers *how many* source images each model accepts and which
bundled workflows exist for each count. This document covers a different question: given a task,
which model is actually the right *choice* — what each one is genuinely good at, and where it
reliably struggles.

This is the human-readable version. The machine-readable source of truth the planning LLM actually
reads at runtime is `src/SmartEditor.Core/Guidance/model-guidance.json` — `ImageEditPlanner`
(`src/SmartEditor.Core/Services/ImageEditPlanner.cs`) loads it via `IModelGuidanceCatalog` /
`FileModelGuidanceCatalog` and folds a "best for / struggles with" note for each model into its
planning system prompt, alongside the existing per-workflow catalog description. A test
(`FileModelGuidanceCatalogTests`) asserts every id in the bundled workflow catalog is covered by
exactly one guidance entry, so this can't silently drift out of sync as workflows are added.

Research method: current web sources (reviews, vendor blog posts, model cards, benchmark writeups)
as of **September 2026** — not general training knowledge, since several of these models postdate
any static cutoff. Sources are listed per model below and duplicated in the JSON entries.

## Z-Image Turbo (`z-image-turbo`)

**Best for:** fast, cheap text-to-image drafts with no reference photo; photorealistic portraits,
headshots, and editorial character art with natural skin and studio lighting; legible bilingual
(English/Chinese) in-image text at moderate complexity.

**Struggles with:** wide artistic style diversity (leans photoreal even for stylized asks); crowded
scenes with multiple background people (face distortion is a known failure mode); very long/complex
text layouts (the non-Turbo Z-Image variant is stronger there).

Sources: [getimg.ai review](https://getimg.ai/blog/z-image-turbo-review-how-this-small-ai-model-delivers-big-results), [BudgetPixel review](https://budgetpixel.com/models/z-image-turbo)

## FLUX.2 dev (`flux2`, `flux2-0img`, `flux2-2img`…`flux2-8img`, `flux2-inpaint`)

**Best for:** photorealistic product, fashion, and editorial shots needing physically plausible
lighting/reflections/materials; long, detail-dense prompts with many bound attributes (exact
colors, counts, positions) needing literal, faithful following; reference-guided generation used
as a style/composition guide rather than a strict pixel edit; masked-region inpainting
(`flux2-inpaint`, added and live-verified 2026-09-11) where every unmasked pixel must be preserved
exactly — the result is composited back over the original so only the selected region changes.

**Struggles with:** deliberately abstract, painterly, or highly stylized requests (strong bias back
toward photorealism); multi-step spatial/logical reasoning ("put the object left of the red one
behind the blue one") — mid-pack on visual-logic benchmarks; portraits can show "Flux shine"
(overly smooth, hyper-idealized skin) when a candid, imperfect look is wanted.

Sources: [FLUX.2 Pro review](https://medium.com/@leucopsis/flux-2-pro-review-and-comparison-with-midjourney-v7-and-with-nano-banana-pro-337224a5551f), [ThePlanetTools review](https://theplanettools.ai/tools/flux-2), [BFL blog](https://bfl.ai/blog/flux-2)

## FLUX.2 Klein 9B (`flux2-klein-edit-single`, `-double`, `-inpaint-reference`, `flux2-klein-0img`, `-3img`…`-8img`)

**Best for:** fast, low-latency edits (1–2s) close to Dev quality without Dev's cost/latency;
identity-preserving edits and face-swap-style reference transfer, where it outperforms comparable
small/fast models; lighter-weight multi-reference edits (logo placement, object transfer) on more
constrained turnaround budgets.

**Struggles with:** nuanced artistic/stylistic tasks (noticeably behind Krea 2 and Qwen-Image for
style work); occasionally odd/brittle prompt-following versus the full Dev model; no official
documented maximum reference-image count, so behavior at the high end (6–8 images) is less
predictable than for models with a stated ceiling.

Sources: [Diffusion Doodles: shrinking FLUX.2 dev](https://medium.com/diffusion-doodles/flux-2-klein-shrinking-flux-2-dev-2258b1078e75), [MyAIForce: Krea 2 vs FLUX.2 Klein](https://myaiforce.com/krea-2-vs-flux-2-klein/), [Fotomanyagi FLUX.2 guide](https://fotomanyagi.com/en/blog/flux-2-models-guide)

## Krea 2 Turbo style reference (`krea2-style-reference`, `-2img`, `-3img`)

**Best for:** aesthetically cohesive, mood-driven imagery — dramatic lighting, atmosphere, and
editorial/magazine-style composition read as intentional; style-reference-driven generation across
illustration, anime, watercolor, and concept-art styles; creative-direction and marketing/fashion
work where visual mood matters more than literal accuracy.

**Struggles with:** legible in-image text — the weakest of all covered models here, avoid for any
task needing readable typography; complex, multi-part instructional prompts with many discrete
constraints; technical/product photography needing exact specifications. Note also that the bundled
workflow's LoRA (`ostris/krea2_turbo_style_reference`) was trained for **1–2** references, so the
3-image variant exceeds its documented training range even though it runs.

Sources: [Krea2.net review](https://krea2.net/krea-2-review), [MindStudio: what is Krea 2](https://www.mindstudio.ai/blog/what-is-krea-2-k2-aesthetic-ai-image-model), [Krea 2 technical report](https://www.krea.ai/blog/krea-2-technical-report)

## Krea 2 hosted API — style reference (`api-krea2-style-reference-1img`, `-2img`, `-3img`)

**Best for:** the same aesthetic/mood-driven style-reference generation as the local Krea 2 Turbo
LoRA above, but via Krea's own hosted model (added and live-verified 2026-09-11) — no LoRA
training-range limitation, per-reference strength including negative weights, and the underlying
node documents chaining up to 10 references (well past the local LoRA's 1–2 trained range); worth
it when full-quality "Krea 2 Large" output justifies the added hosted latency/cost over the local
Turbo distillation.

**Struggles with:** it's a paid, hosted partner node requiring separately authorized/credited Krea 2
access on the Comfy account; true "Moodboards" (Krea's 250-image style-analysis feature) is **not**
reachable this way — it requires a moodboard first created in Krea's own web app and referenced by
UUID, which no upload flow in this app can produce, so only the direct style-reference chain is
buildable; same text-rendering weakness as the local workflow.

Sources: [Krea 2 API launch](https://www.krea.ai/blog/krea-2-api-launch), [Krea Moodboards docs](https://docs.krea.ai/developers/krea-2/moodboards)

## Qwen-Image-Edit 2511 (`qwen-image-edit-2511`, `-1img`, `-3img`, `-character-pose`, `-character-prepared-pose`, `-inpainting`)

**Best for:** multi-image and multi-person edits that must hold composition/identity together
across a back-and-forth session, not just a single pass; character-identity-preserving edits from a
reference portrait (clothing/pose changes, imaginative edits) without breaking the rest of the
scene; in-image text editing/replacement in Chinese and English while preserving original font,
size, and styling (packaging, banners, UI mockups); structure-aware edits needing precise
preservation of unchanged regions.

**Struggles with:** needs a lower CFG than typical diffusion defaults (~4–5, not 7–8 — higher
values introduce artifacts); the improvement over single-subject edits versus predecessor 2509 is
incremental, so it's a weaker differentiator for a plain one-subject edit; hard-capped at 3
reference images by the `TextEncodeQwenImageEditPlus` node the workflow uses — never route a
4+-image request here.

Sources: [WaveSpeedAI: production-ready editing](https://medium.com/@social_18794/qwen-image-edit-2511-the-first-real-upgrade-that-makes-ai-editing-feel-production-ready-5d229d44a25f), [Alibaba Cloud: improve consistency](https://www.alibabacloud.com/blog/qwen-image-edit-2511-improve-consistency_602762), [Apatero guide](https://www.apatero.com/blog/qwen-edit-2511-complete-guide-image-editing-2025)

## Qwen Image + InstantX Union ControlNet (`qwen-instantx-union-controlnet`, `qwen-image-instantx-inpainting`, `qwen-dwpose-union-controlnet`, `qwen-canny-union-controlnet`, `qwen-depth-union-controlnet`, `qwen-lineart-union-controlnet`, `qwen-normal-union-controlnet`)

**Best for:** generating a brand-new image under strong structural control from one prepared guide
(depth map, Canny edge map, pose skeleton, or soft-edge map) rather than editing an existing photo;
depth-guided control specifically, which keeps pose/facial structure most consistent among its four
supported modes; recreating a photo's pose in an otherwise new generated scene when the source photo
should not otherwise influence style or identity. One-shot photo→guide→generate shortcuts exist for
all five extractable guide types — pose (`qwen-dwpose-union-controlnet`), canny, depth, lineart, and
surface-normal — so a plain source photo never needs a manual two-step (extract, then feed into the
generic guide workflow) unless the guide is prepared separately. All five one-shot variants were
live-verified on 2026-09-11; the lineart and normal-map ones are an off-label but confirmed-working
use of the model's soft-edge/depth control modes (InstantX's official card only documents
canny/soft-edge/depth/pose, not lineart or normal-map specifically).

**Struggles with:** anything requiring the source image's own visual identity/style rather than
just its structure — this is a guide-image tool, not a reference/edit tool; preserving small
in-image text or fine typography unless explicitly called out in the prompt; combining more than
one control type at once (unsupported outside an experimental diffusers path per the model card);
narrower control-type coverage (4 types) than `qwen-fun-union-controlnet` below.

Sources: [InstantX model card](https://huggingface.co/InstantX/Qwen-Image-ControlNet-Union/blob/main/README.md), [Diffusion Doodles: Qwen Image ControlNets](https://medium.com/diffusion-doodles/qwen-image-controlnets-53f8703aba42), [Comfy blog: day-1 support](https://blog.comfy.org/p/day-1-support-of-qwen-image-instantx)

## Qwen Image + PAI Fun-Controlnet-Union (`qwen-fun-union-controlnet`)

**Best for:** the same guide-image-driven generation as InstantX Union above, but with broader
control-type coverage in one model — canny, HED/soft-edge, depth, pose, MLSD (straight-line /
architectural), and scribble, plus its own inpainting mode; architectural, product, or
interior-design guides using MLSD control, which InstantX Union doesn't support at all; a safer
default when the guide's control type is unknown or mixed.

**Struggles with:** newer and less battle-tested than InstantX Union (versioned filename suggests a
late-2025/early-2026 release) — treat outputs with a bit more scrutiny until more real-world use
accumulates; the same fundamental limit as any guide-image tool (no source-image identity/style
carryover, only structure); live-verified to load and execute on this Comfy Cloud account
(2026-09-11) but not yet benchmarked head-to-head against InstantX Union for per-type output
quality.

Sources: [alibaba-pai/Qwen-Image-2512-Fun-Controlnet-Union](https://huggingface.co/alibaba-pai/Qwen-Image-2512-Fun-Controlnet-Union), [SceneWorks mirror](https://huggingface.co/SceneWorks/qwen-image-2512-fun-controlnet-union)

## Qwen Image + DiffSynth-Studio Blockwise ControlNet, model-patch (`qwen-diffsynth-canny-controlnet`, `qwen-diffsynth-depth-controlnet`, `qwen-diffsynth-inpaint`)

**Best for:** a third option for canny/depth/inpaint guide-driven Qwen generation using a genuinely
different mechanism than the two Union ControlNets — the guide is baked into the diffusion model
itself as a patch (`QwenImageDiffsynthControlnet` + `ModelPatchLoader`), not applied as external
conditioning; worth trying as an independently-trained alternative when a Union ControlNet's result
on a guide doesn't match the desired structural fidelity; the inpaint variant composites its result
back over the original, so unmasked pixels are preserved exactly, same guarantee as the app's other
masked workflows. Added and live-verified 2026-09-11.

**Struggles with:** only three single-purpose patches exist (canny, depth, inpaint) — no pose, MLSD,
or scribble, unlike either Union ControlNet; per its own documentation this is "not a true
ControlNet, but a model patch which supports three control modes," so behavior can differ subtly
from the Union ControlNets on the same guide; the inpaint variant's mask polarity needed careful,
non-obvious wiring to get right during testing (see note below) — narrower real-world track record
than the Union ControlNets.

*Testing note:* the inpaint variant's first draft inverted the source image's alpha-derived mask
before use, mirroring the pattern the app's other masked workflows (FLUX.2 klein/dev) need — but for
this graph that produced a fully-repainted image with no masking at all. A debug pass (rendering the
mask as a visible black/white image mid-graph) showed the raw, un-inverted `LoadImage` mask was
already correctly polarized here; removing the extra inversion fixed it. Two different bundled
graphs needing opposite mask handling for the same visual intent is exactly why this is spelled out
rather than assumed — don't copy the FLUX.2 masking pattern onto a new Qwen DiffSynth workflow
without re-verifying which polarity it actually needs.

Sources: [DiffSynth-Studio Blockwise Canny model card](https://huggingface.co/DiffSynth-Studio/Qwen-Image-Blockwise-ControlNet-Canny), [Comfy blog: Qwen Image ControlNet & LoRA support](https://blog.comfy.org/p/comfyui-now-supports-qwen-image-controlnet)

## ByteDance Seedream 4.5/5.0, hosted API (`api-bytedance-seedream4`, `-2img`…`-8img`)

**Best for:** dense, high-fidelity in-image text and typography — including handwriting and
artistic/stylized text, a clear strength versus most competitors; multi-source compositing from
several reference images at once (product swaps, element transfer between images) while preserving
depth, perspective, and lighting consistency; general subject recognition and reference fidelity
when combining many distinct source photos into one coherent scene.

**Struggles with:** reasoning-intensive edits needing inference of real-world context or
example-based instruction-following — that's Seedream 5.0's differentiator, not 4.5's; it's a paid,
hosted partner node requiring separately authorized/credited Seedream access on the Comfy account,
so it's unsuitable when the user has no such access configured; exact deployment limits (resolution
ceiling, aspect ratios, latency) aren't fully documented by the vendor — treat extreme
aspect-ratio/resolution requests cautiously.

Sources: [Morphic model page](https://morphic.com/resources/models/seedream-4-5), [VP Land coverage](https://www.vp-land.com/stories/bytedance-s-seedream-4-5-levels-up-text-and-quality), [WaveSpeed guide](https://wavespeed.ai/blog/posts/seedream-4-5-complete-guide-2026/)

## Nano Banana Pro / Gemini 3 Pro Image, hosted API (`api-nano-banana-pro`, `-0img`, `-1img`, `-3img`…`-8img`)

**Best for:** the best-in-class choice whenever output needs correctly spelled, legible in-image
text — short taglines through long multilingual passages, with font/texture/calligraphy control;
consistent multi-image blending and accurate identity preservation across multiple subjects at once
(documented up to five people); practical, information-dense outputs — infographics, diagrams,
multilingual localized content, high-resolution (up to 4K) print-ready output.

**Struggles with:** deliberately surreal, abstract, or highly stylized prompts — a strong
RLHF-driven bias toward realism tends to "correct" surreal intent back toward the photorealistic
median; it's a paid, hosted partner node requiring separately authorized/credited Gemini image
access on the Comfy account; the exact image-count ceiling and per-surface sub-caps (objects vs.
people) vary by hosting surface, so very high reference counts should be treated as best-effort.

Sources: [minimaxir review](https://minimaxir.com/2025/12/nano-banana-pro/), [Google blog announcement](https://blog.google/innovation-and-ai/products/nano-banana-pro/), [TechBullion deep dive](https://techbullion.com/inside-nano-banana-pro-how-gemini-3-pro-image-closes-the-gaps-that-held-ai-imagery-back/)

## Control-guide extractors — DWPose / Canny / Lineart / Depth Anything V2 / BAE (`dwpose-extract-pose-guide`, `canny-extract-control-guide`, `lineart-extract-control-guide`, `depth-extract-control-guide`, `normal-extract-control-guide`)

**Best for:** producing a single-purpose structural guide image (pose skeleton, edge map, lineart,
depth map, or normal map) from one source photo, to feed into a ControlNet generation workflow
afterward; isolating structure/pose from a source photo when its style, color, or identity should
*not* carry over into the next generation step; deterministic, fast, single-image preprocessing.

**Struggles with:** these do not generate a new image from a prompt at all — never select one when
the user is asking for a finished edited/generated picture; no multi-image input, each is a
single-image tool by design; extraction quality depends entirely on source-photo clarity (occlusion,
low contrast, or clutter degrade the guide).

Source: `docs/comfy-cloud-image-models.md` (this repo's own model-capability survey).

## 4x-UltraSharp upscaler (`upscale-with-model`)

**Best for:** increasing an existing image's resolution 4x with sharpened detail — "make this
bigger", "upscale", "higher resolution" requests with no other content change wanted; a finishing
step after any generation workflow when the user wants more detail or a larger output, not a
different picture. Deterministic and prompt-free, added and live-verified 2026-09-11.

**Struggles with:** cannot add, remove, or alter content — route anything that also wants a content
change to a generation workflow instead; single-image only; sharpens detail that's present, doesn't
invent detail lost to heavy compression or an already-low-quality source.

## Rembg background removal (`remove-background`)

**Best for:** cutting a subject out onto a transparent background — "remove the background",
"isolate the product/person", or prep work before compositing into another image (e.g. as a clean
reference for one of the multi-image generation workflows). Uses the general-purpose
`isnet-general-use` model; deterministic and prompt-free, added and live-verified 2026-09-11.

**Struggles with:** fine, wispy detail (hair strands, fur, glass, smoke) commonly gets soft/fuzzy
edges or halos with any general-purpose background remover; doesn't generate or paint in a new
background — pair with a generation workflow for a full "replace the background" request;
single-image only.

## Appendix: the wider ControlNet ecosystem on this Comfy Cloud account (not bundled)

Beyond Qwen-Image, the account's `/api/object_info` inventory advertises ControlNet weights for
several other base models SmartEditor doesn't otherwise use. These are **not** first-class
SmartEditor models (they'd need whole new base-model pipelines — checkpoint/CLIP loading,
samplers — not just a new ControlNet file), so this is reference material for a future expansion
decision, not a bundled workflow. Every row below was checked with a **live test submission**, not
just inventory presence, because inventory presence alone is unreliable — see the SDXL row.

| Base model | Control types listed | Live-tested (2026-09-11) | Result |
|---|---|---|---|
| **SDXL** | Union, Canny ×2, Depth, OpenPose ×2, Scribble, Tile | ✅ tested (Canny) | ❌ **Fake.** Rejected: `"controlnet-canny-sdxl-1.0.safetensors" is not a valid value` / `Value not in list`, despite being listed in the loader node's own dropdown. Matches an independent finding from the sibling SlopFactory project in August 2026 — don't trust SDXL ControlNet entries in this account's inventory without re-testing. |
| **SD1.5 / SD1.4** | T2I-Adapters (canny/depth/color/sketch/style/keypose/openpose/seg), CoAdapters, ControlNet v1.1 (depth/openpose/scribble) | ✅ tested (depth, openpose) | ✅ Real — both loaded and executed correctly, openpose control visibly tracked the guide's pose. Scribble untested but same file family/loader, presumed real. |
| **SD3.5 (large)** | Blur, Canny, Depth | ✅ tested (canny) | ✅ Real — traced the guide's geometry precisely into a photoreal SD3.5 render. |
| **FLUX.1** (not FLUX.2) | Union-style (`Flux1Dev_CNup-FP8`), plus BFL's own dedicated Canny/Depth-tuned dev checkpoints (`flux1-canny-dev`, `flux1-depth-dev`) | ✅ tested (Union, canny type) | ✅ File loads and executes without error. Note: the probe graph used a simplified KSampler(cfg=1) pattern rather than FLUX.1's idiomatic `BasicGuider`/`SamplerCustomAdvanced` chain, so this confirms the ControlNet file is genuine, not that the sampling was production-quality. |
| **Wan 2.1** (video) | Canny, Depth, HED, Uni3C | not tested | Video model — out of scope for this image-editing app regardless of validity |

If any of SD1.5, SD3.5, or FLUX.1 becomes worth adding as a real SmartEditor model+workflow, budget
for building its base pipeline (checkpoint/dual-or-triple-CLIP loading, VAE, an appropriate
sampler chain) in addition to wiring in the ControlNet — none of that infrastructure exists in this
codebase today outside the FLUX.2/Krea 2/Qwen-Image/Z-Image families already covered above.
