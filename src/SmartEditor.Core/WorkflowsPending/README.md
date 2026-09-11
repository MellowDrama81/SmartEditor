# Pending workflow variants (holding area)

Empty as of 2026-09-11 — every workflow variant that was authored during the per-model /
per-image-count Comfy Cloud coverage pass has been live-verified and promoted into
`../Workflows/`. See `docs/comfy-cloud-image-models.md` for the full status table and history
(including the one credit-exhaustion delay and one transient worker-crash retry along the way).

This directory stays as the holding area for any future variant: author the `*.meta.json` /
`*.json` pair here first, submit it live against `cloud.comfy.org` with representative source
images, confirm it returns an output image, then `git mv` the pair into `../Workflows/` and bump
the count assertion in `FileWorkflowCatalogTests`. Keeping it out of `../Workflows/` until then
matters because that folder is what the `.csproj` bundles and `FileWorkflowCatalog` loads at
runtime — an unverified graph in there would be user-facing immediately.
