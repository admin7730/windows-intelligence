# Third-party software and models

Windows Intelligence bundles the .NET Windows Desktop runtime, NAudio, sherpa-onnx and its ONNX Runtime native dependency. Refer to their distribution and licensing materials for terms. Keep the license files embedded with the speech models when redistributing the package.

- [.NET runtime license](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) and [.NET third-party notices](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT)
- [NAudio license](https://github.com/naudio/NAudio/blob/master/license.txt)
- [sherpa-onnx Apache 2.0 license](https://github.com/k2-fsa/sherpa-onnx/blob/master/LICENSE) and [ONNX Runtime MIT license](https://github.com/microsoft/onnxruntime/blob/main/LICENSE)
- [Bilingual Zipformer model card](https://huggingface.co/pfluo/k2fsa-zipformer-chinese-english-mixed), Apache 2.0. Model archives identify `icefall` as the training project.
- [SenseVoiceSmall model card and model license](https://huggingface.co/FunAudioLLM/SenseVoiceSmall), with the [sherpa-onnx INT8 conversion](https://k2-fsa.github.io/sherpa/onnx/sense-voice/pretrained.html). The model has its own FunASR model terms.
- [Qwen3-ASR-0.6B](https://github.com/QwenLM/Qwen3-ASR), Apache 2.0, with the [sherpa-onnx INT8 conversion](https://k2-fsa.github.io/sherpa/onnx/qwen3-asr/).
- [Silero VAD](https://github.com/snakers4/silero-vad), MIT, distributed through the sherpa-onnx model releases.
- [Kokoro-82M model card](https://huggingface.co/hexgrad/Kokoro-82M), Apache 2.0. The sherpa-onnx archive also supplies eSpeak NG language data and Jieba data. Preserve upstream terms when repackaging them. [eSpeak NG](https://github.com/espeak-ng/espeak-ng) · [cppjieba](https://github.com/yanyiwu/cppjieba)
- [Markdig 1.6.0](https://www.nuget.org/packages/Markdig/1.6.0), BSD 2-Clause, used to parse Markdown.
