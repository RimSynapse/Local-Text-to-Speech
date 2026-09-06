# Local TTS Documentation

Welcome to the documentation for **Local TTS** — fully offline, on-device neural speech for
RimWorld, powered by the Kokoro-82M model running through ONNX Runtime. Standalone: no other
mod required, and any mod can request speech through the async broker API.

## Table of Contents

- [Getting Started](Getting_Started) — requirements, installation, and the mod settings explained
- [Voice Pipeline](Voice_Pipeline) — how text becomes speech: espeak-ng, Kokoro, style vectors, staging, and playback

## RimSynapse

Local TTS works standalone. When [RimSynapse Core](https://github.com/RimSynapse/Core/wiki) is
installed it additionally reports its GPU memory footprint to Core's GPU stats channel and can
serve as the suite's embedded voice backend. (Core's *Voicebox Setup Guide* covers their separate
out-of-process TTS server — Local TTS needs no server: everything runs inside RimWorld's process.)

## Support

- Issues: https://github.com/archdukejim/Local-Text-to-Speech/issues
- Discord: https://discord.gg/eVBebZ2N9s
