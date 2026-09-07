# Third-Party Notices

Local TTS is licensed under the **GNU General Public License v3.0** (see `LICENSE`).
It bundles and builds upon the third-party components below; each is the property of
its respective authors and is used under the license noted. Full license texts are in
the `licenses/` directory, which must travel with any redistribution.

Complete and corresponding source for Local TTS is publicly available at
https://github.com/archdukejim/Local-Text-to-Speech (this satisfies the GPL-3.0 source
requirement for the binary builds distributed on the Steam Workshop and GitHub releases).

---

## espeak-ng — grapheme-to-phoneme

- **Files:** `Resources/native/win-x64/espeak-ng.dll`, `espeak-ng-data/`
- **Source:** https://github.com/espeak-ng/espeak-ng
- **License:** GNU General Public License v3.0 — `licenses/GPL-3.0.txt`
- **Note:** Local TTS calls espeak-ng via P/Invoke and bundles the library in its
  released builds so the mod works out of the box. espeak-ng is GPL-3.0; this is the
  reason Local TTS as a whole is distributed under GPL-3.0. The library is not committed
  to the source repository — it is fetched by `download-assets.ps1` for development and
  packaged into release/Workshop builds.

## Kokoro-82M — speech model + voices

- **Files:** `Resources/models/kokoro-v1.0.onnx`, `Resources/models/voices/*.bin`
- **Source:** https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX
  (ONNX build of https://huggingface.co/hexgrad/Kokoro-82M)
- **License:** Apache License 2.0 — `licenses/Apache-2.0.txt`
- **Attribution (from the Kokoro model card):**
  - Kokoro-82M by hexgrad; architecture based on **StyleTTS 2** (@yl4579).
  - Training data included Apache-compatible and CC-BY audio; the CC-BY sources
    require attribution: **Koniwa** (CC BY 3.0) and **SIWIS** (CC BY 4.0).
  - The model weights are shipped as published; Local TTS does not modify them.

## ONNX Runtime — inference engine

- **Files:** `Resources/native/win-x64/onnxruntime.dll` (DirectML build),
  `Assemblies/Microsoft.ML.OnnxRuntime.dll`
- **Source:** https://github.com/microsoft/onnxruntime
- **License:** MIT — `licenses/MIT.txt` (Copyright (c) Microsoft Corporation)

## .NET runtime libraries

- **Files:** `Assemblies/System.Buffers.dll`, `System.Memory.dll`,
  `System.Numerics.Vectors.dll`, and (bundled for standalone operation, issue #12)
  `System.Runtime.CompilerServices.Unsafe.dll`, `System.Threading.Tasks.Extensions.dll`
- **Source:** https://github.com/dotnet/runtime
- **License:** MIT — `licenses/MIT.txt` (Copyright (c) .NET Foundation and Contributors)

---

_This file records attribution and license obligations; it is not legal advice._
