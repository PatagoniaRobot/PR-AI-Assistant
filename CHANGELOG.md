# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-10-21

First public release, as free software under the Apache License 2.0.

### Added

- **Local chat with GGUF models** through llama.cpp (`b8671`), with streaming answers,
  thinking mode and automatic trimming of the history to fit the context window.
- **Model catalog.** Searches HuggingFace, reads each file's header to tell real models
  from projectors, draft heads, adapters and split parts, shows the declared license, and
  downloads, verifies and registers the chosen file. Downloaded models can be deleted from
  the application.
- **Recommendation for the PC.** Detects the graphics card of any brand, the RAM and the
  physical cores, computes the exact memory a model needs from its header, marks the
  recommended quantization, and the *Automatic* execution profile (the default) sets
  context size, GPU layers and threads for each model. The conversation cache is
  compressed when that gains context or layers.
- **Two engines, picked by the graphics card**: CUDA for NVIDIA and Vulkan for AMD and
  Intel. If one is missing, the other is used.
- **Investigation Mode**: examiner identification, legal disclaimer (RFC 3227),
  append-only forensic log with UTC timestamps and SHA-256 hashes, evidence files loaded
  as context, and session export to TXT, HTML or JSON with an integrity hash.
- **PR Academy**: an isolated mode for technical and legal questions, with no access to
  evidence.
- **Usage profiles** that enable or disable web search, model downloads, image generation,
  file attachments and the mandatory log. Investigation Mode always overrides them.
- **Image generation** with Z-Image Turbo through stable-diffusion.cpp: three variants to
  download with one button, a video memory indicator, cancellation, reproducible images
  (the seed and the exact command line are saved next to each PNG), and an automatic
  retry when the engine runs out of memory.
- **Web search** through a SearXNG instance configured by the user, reading up to six
  pages in the background and citing them.
- **Configurable folders** for models and generated images, with verified moves between
  drives.
- **Bilingual interface**, Spanish and English, following the Windows language; built-in
  help in both languages.
- **Forensic edition** (`publish.bat forense`), compiled without web search, model
  downloads or image generation.
- Two release packages, each with its `.sha256`: one for any PC (Vulkan engine, 85 MB)
  and one for NVIDIA cards (CUDA and Vulkan engines, 604 MB).
