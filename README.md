# RimSynapse — Local Text-to-Speech

Fully offline, on-device neural text-to-speech for the RimSynapse suite, powered by the
**Kokoro-82M** model running through **ONNX Runtime**. No API keys, no cloud calls.

- **GPU** acceleration via **DirectML** (any DirectX 12 GPU — NVIDIA/AMD/Intel), with automatic
  **CPU** fallback.
- Synthesis runs **asynchronously** on a background worker thread; audio is played through
  RimSynapse Core's shared `AudioPlaybackManager`.
- English grapheme→phoneme via bundled **espeak-ng**.

## Layout

```
About/                       mod metadata (About.xml, Manifest.xml)
Assemblies/                  built dll + ONNX Runtime managed + System.* shims  (staged by build)
Resources/native/win-x64/    onnxruntime.dll, espeak-ng.dll, espeak-ng-data/    (gitignored)
Resources/models/            kokoro-v1.0.onnx, voices/*.bin                     (gitignored)
Source/                      C# (net48) — see Source/Engine
```

Large binaries (model + native libs) are kept out of git and fetched per machine.

## Building

1. Copy your RimWorld path into `Source/GamePath.props` (gitignored; a default is provided).
2. Fetch the bundled assets:
   ```powershell
   pwsh -File download-assets.ps1            # full fp32 model (~310 MB)
   pwsh -File download-assets.ps1 -Quantized # smaller q8f16 model (~86 MB)
   ```
   The script pulls the Kokoro model + voices from Hugging Face. For **espeak-ng**, either let the
   script warn and install it yourself, or extract the official MSI without installing:
   ```powershell
   msiexec /a espeak-ng.msi /qn TARGETDIR=<dir>
   # then copy libespeak-ng.dll -> Resources/native/win-x64/espeak-ng.dll
   #      and  espeak-ng-data/  -> Resources/native/win-x64/espeak-ng-data/
   ```
3. Build (this also stages the ONNX Runtime managed + native DirectML binaries into the mod):
   ```bash
   dotnet build Source/RimSynapseLocalTts.csproj -c Release
   ```

## Using it

```csharp
RimSynapse.LocalTts.LocalTtsMod.Instance.Engine.Speak("Hello, colonist.");
```

Settings (Mod Options → *RimSynapse Local TTS*): acceleration mode (Auto/GPU/CPU), voice id, speed,
and a "Speak a test line" button.

## Validation (RimSynapse debug gate)

Dev mode → Debug Actions → **RimSynapse**:

- **LocalTTS: Synthesize + dump stats (Log)** — headless proof; runs the whole pipeline and logs
  sample count / duration / peak amplitude / backend without needing an audio device.
- **LocalTTS: Speak test line** — full path incl. playback (needs an active game).
- **LocalTTS: Dump engine status (Log)** — model/GPU/vocab/backend/settings snapshot.

All three are headlessly triggerable via the dev-tools `run_debug_action` bridge.

## Requirements

- RimSynapse Core (loads first)
- Windows 64-bit
