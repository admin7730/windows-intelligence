# Third-party software and models

Windows Intelligence bundles the .NET Windows Desktop runtime, NAudio, sherpa-onnx and its ONNX Runtime native dependency. Refer to their distribution and licensing materials for terms. Keep the license files embedded with the speech models when redistributing the package.

- [.NET runtime license](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) and [.NET third-party notices](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT)
- [NAudio license](https://github.com/naudio/NAudio/blob/master/license.txt)
- [sherpa-onnx 2.0.1](https://github.com/k2-fsa/sherpa-onnx/tree/v2.0.1), Apache 2.0, and [ONNX Runtime MIT license](https://github.com/microsoft/onnxruntime/blob/main/LICENSE).
- [Bilingual Zipformer model card](https://huggingface.co/pfluo/k2fsa-zipformer-chinese-english-mixed), Apache 2.0. Model archives identify `icefall` as the training project.
- SenseVoiceSmall by the FunAudioLLM team at Alibaba, distributed as the sherpa-onnx INT8 conversion. The FunASR model license is packaged in `Models/ASR/SenseVoice/LICENSE`. [Model card](https://huggingface.co/FunAudioLLM/SenseVoiceSmall) · [INT8 conversion](https://k2-fsa.github.io/sherpa/onnx/sense-voice/pretrained.html)
- [Qwen3-ASR-0.6B](https://github.com/QwenLM/Qwen3-ASR), Apache 2.0, with the [sherpa-onnx INT8 conversion](https://k2-fsa.github.io/sherpa/onnx/qwen3-asr/).
- [Silero VAD](https://github.com/snakers4/silero-vad), MIT, distributed through the sherpa-onnx model releases.
- [Kokoro-82M model card](https://huggingface.co/hexgrad/Kokoro-82M), Apache 2.0. Local synthesis uses the supplied lexicons and [cppjieba](https://github.com/yanyiwu/cppjieba) dictionaries.
- [Markdig 1.6.0](https://www.nuget.org/packages/Markdig/1.6.0), BSD 2-Clause, used to parse Markdown.
