# Local TTS

Fully offline, on-device neural text-to-speech for RimWorld, powered by the **Kokoro-82M**
model running through **ONNX Runtime**. No API keys, no cloud calls. **Standalone** — no other
mod required; RimSynapse Core is an optional soft integration (GPU stats reporting).

- **GPU** acceleration via **DirectML** (any DirectX 12 GPU — NVIDIA/AMD/Intel), with automatic
  **CPU** fallback.
- Synthesis runs **asynchronously** on a background worker thread. As of v1.0.0 (in progress,
  issue #12) audio is **staged as a file** and the requesting mod is notified when it's ready —
  the current build still plays through RimSynapse Core's `AudioPlaybackManager`.
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
   dotnet build Source/LocalTts.csproj -c Release
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

- Windows 64-bit
- Optional: RimSynapse Core v0.9.0+ (loads first when present; enables GPU stats reporting).
  The current build still hard-references Core — the standalone decoupling is issue #12.

## License

Local TTS is free software licensed under the **GNU General Public License v3.0** — see
[`LICENSE`](LICENSE). This is a consequence of bundling **espeak-ng** (GPL-3.0) for
grapheme-to-phoneme conversion; GPL-3.0 is not a restriction on price (the mod is free)
but a guarantee that the source stays open and everyone keeps the freedom to use, study,
modify, and share it.

Third-party components and their licenses are documented in
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md), with full texts under
[`licenses/`](licenses/):

| Component | License |
|---|---|
| Kokoro-82M model + voices | Apache-2.0 |
| ONNX Runtime, .NET libraries | MIT |
| espeak-ng | GPL-3.0 |

Large binaries (model, voices, espeak-ng) are not committed to this repository; they are
fetched by `download-assets.ps1` and packaged into release/Workshop builds.
