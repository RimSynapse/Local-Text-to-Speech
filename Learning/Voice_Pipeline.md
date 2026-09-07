# Voice Pipeline

How a line of text becomes audible speech, entirely inside RimWorld's process. Everything below
runs on **one background worker thread** — the simulation never waits on synthesis.

```
text
 └─> espeak-ng (G2P)          grapheme → IPA phonemes, bundled native library
      └─> tokenizer           phoneme characters → Kokoro token ids (≤ 510 per call)
           └─> style vector   the chosen voice's embedding row, optionally blended
                └─> Kokoro-82M (ONNX Runtime)   token ids + style + speed → 24kHz float audio
                     └─> PCM encode             float32 → 16-bit PCM
                          └─> stage to file + notify   the caller is told the audio is ready, and where
                               └─> playback             built-in player, or the caller's own
```

## Stages

1. **Phonemization (espeak-ng).** A bundled native espeak-ng converts arbitrary English text to
   IPA phonemes. Each bundled voice carries its espeak language (`en-us` for American voices,
   `en-gb` for British ones), so pronunciation matches the accent.

2. **Tokenization.** Phoneme characters map to Kokoro token ids through a fixed vocabulary
   (bundled as JSON with an identical embedded fallback, self-checked at load). Unknown characters
   are dropped; input is capped at 510 tokens per synthesis call.

3. **Voice style.** Every voice ships as a `.bin` style bank — one 256-float embedding row per
   possible input length. The row matching the token count is selected. Two voices can be blended
   by linear interpolation of their style vectors, which is how the settings' *Blend* control
   creates custom timbres.

4. **Inference (Kokoro-82M via ONNX Runtime).** The token ids (zero-padded), style vector, and
   speed scalar feed the ONNX session. Execution uses **DirectML** on any DirectX 12 GPU, or
   multi-threaded CPU otherwise; the choice is automatic and visible in the engine status dump.
   Output is mono float32 audio at **24 kHz**.

5. **Staging and notification.** Samples are encoded to 16-bit PCM and staged as an audio file
   in the mod's cache, keyed by the request (text + voice + parameters) — a repeated line is a
   cache hit and never synthesizes twice. The requesting code is then notified on the main
   thread that the file is ready and where it is. Playback is the consumer's choice: Local TTS's
   built-in player (used by the settings tester), or the caller's own audio path.

## Resource footprint

- The model (~86 MB quantized, ~310 MB full precision) loads lazily on first use and stays
  resident for the session.
- When RimSynapse Core is installed and synthesis runs on GPU, the mod registers its estimated
  VRAM footprint with Core's GPU stats consumers channel, so tools like RimSynapse NVIDIA-Tool
  attribute that memory correctly instead of lumping it into "system". Without Core this simply
  no-ops.

## For modders — the broker API

Local TTS is a speech *service*: you ask for a line, it hands you a file.

1. Request a line — you get a ticket back immediately; synthesis runs on Local TTS's worker
   thread, never yours and never the main thread.
2. When the staged audio file is complete and playable, your callback fires on the main thread
   with the ticket and the file path (or a failure reason — silence is never the contract).
   Prefer polling? The ticket can be queried instead.
3. Play it however you like — or hand it back to Local TTS's built-in player.

Because readiness is an explicit notification, "wait for the voice before the event fires" is
trivial to build on top. The concrete API surface ships with v1.0.0 and is documented in the
repository README, together with build-from-source instructions (the model and native libraries
are fetched by `download-assets.ps1`, not stored in git).
