param([switch]$Fixtures)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location -LiteralPath $root
try {
    New-Item -ItemType Directory -Force .tooling/models,.tooling/asr-fixtures,.tooling/sensevoice-fixtures,.tooling/qwen-asr-fixtures,artifacts/models/ASR/SenseVoice,artifacts/models/ASR/Qwen3,artifacts/models/TTS | Out-Null
    function Read-ModelArchive([string]$File,[string]$Url,[string]$Sha256) {
        if (!(Test-Path $File) -or (Get-FileHash -Algorithm SHA256 $File).Hash -ne $Sha256) {
            & curl.exe --fail --location --silent --show-error --retry 2 $Url --output "$File.partial"
            if ($LASTEXITCODE -or (Get-FileHash -Algorithm SHA256 "$File.partial").Hash -ne $Sha256) { throw "Failed to verify $File." }
            Move-Item -LiteralPath "$File.partial" -Destination $File -Force
        }
    }
    $zipformer = 'sherpa-onnx-streaming-zipformer-bilingual-zh-en-2023-02-20'
    $sensevoice = 'sherpa-onnx-sense-voice-zh-en-ja-ko-yue-int8-2024-07-17'
    $qwen = 'sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25'
    $base = 'https://github.com/k2-fsa/sherpa-onnx/releases/download'
    Read-ModelArchive '.tooling/models/asr.tar.bz2' "$base/asr-models/$zipformer.tar.bz2" '27FFBD9EE24AD186D99ACC2F6354D7992B27BCAB490812510665FA8F9389C5F8'
    Read-ModelArchive '.tooling/models/sensevoice.tar.bz2' "$base/asr-models/$sensevoice.tar.bz2" '7D1EFA2138A65B0B488DF37F8B89E3D91A60676E416F515B952358D83DFD347E'
    Read-ModelArchive ".tooling/models/$qwen.tar.bz2" "$base/asr-models/$qwen.tar.bz2" '393F8A14E2F5FB96746AAAB342997A40641001FBD5BF9592A080A8329178EE96'
    Read-ModelArchive '.tooling/models/silero_vad.onnx' "$base/asr-models/silero_vad.onnx" '9E2449E1087496D8D4CABA907F23E0BD3F78D91FA552479BB9C23AC09CBB1FD6'
    Read-ModelArchive '.tooling/models/qwen3-asr-license.txt' 'https://raw.githubusercontent.com/QwenLM/Qwen3-ASR/main/LICENSE' '2E63E9A38B6E8FC0C7BC37CE174CACA1862870856C6DAF5697CFB785E925520B'
    Read-ModelArchive '.tooling/models/silero-vad-license.txt' 'https://raw.githubusercontent.com/snakers4/silero-vad/master/LICENSE' 'A44A6081C73AD75F0255BB2BB5CAB74EF1829565A895A24E53A4F11290AB7655'
    Read-ModelArchive '.tooling/models/sensevoice-license.txt' 'https://raw.githubusercontent.com/modelscope/FunASR/main/MODEL_LICENSE' '7DBA975A2069691DB4992B0592D70828B330D2F8A30A71450F4E152A554E84F8'
    $ttsArchive = if (Test-Path '.tooling/tts/kokoro.tar.bz2') { '.tooling/tts/kokoro.tar.bz2' } else { '.tooling/models/kokoro.tar.bz2' }
    Read-ModelArchive $ttsArchive "$base/tts-models/kokoro-multi-lang-v1_0.tar.bz2" '60703707BC05CCD48D60E1EB723F38B07D6E474795A0B6A588D7220400E0F7A4'
    $members = 'README.md','tokens.txt','encoder-epoch-99-avg-1.int8.onnx','decoder-epoch-99-avg-1.int8.onnx','joiner-epoch-99-avg-1.int8.onnx' | ForEach-Object { "$zipformer/$_" }
    & tar.exe -xf .tooling/models/asr.tar.bz2 -C artifacts/models/ASR --strip-components 1 @members
    if ($LASTEXITCODE) { throw 'Could not extract Zipformer model.' }
    $members = 'README.md','tokens.txt','model.int8.onnx' | ForEach-Object { "$sensevoice/$_" }
    & tar.exe -xf .tooling/models/sensevoice.tar.bz2 -C artifacts/models/ASR/SenseVoice --strip-components 1 @members
    if ($LASTEXITCODE) { throw 'Could not extract SenseVoice model.' }
    Copy-Item -LiteralPath .tooling/models/sensevoice-license.txt -Destination artifacts/models/ASR/SenseVoice/LICENSE
    $members = 'README.md','conv_frontend.onnx','encoder.int8.onnx','decoder.int8.onnx','tokenizer' | ForEach-Object { "$qwen/$_" }
    & tar.exe -xf ".tooling/models/$qwen.tar.bz2" -C artifacts/models/ASR/Qwen3 --strip-components 1 @members
    if ($LASTEXITCODE) { throw 'Could not extract Qwen3-ASR.' }
    Copy-Item -LiteralPath .tooling/models/qwen3-asr-license.txt -Destination artifacts/models/ASR/Qwen3/LICENSE -Force
    Copy-Item -LiteralPath .tooling/models/silero-vad-license.txt -Destination artifacts/models/ASR/silero-vad-LICENSE -Force
    Copy-Item -LiteralPath .tooling/models/silero_vad.onnx -Destination artifacts/models/ASR/silero_vad.onnx -Force
    & tar.exe -xf $ttsArchive -C artifacts/models/TTS --strip-components 1 --exclude '*/espeak-ng-data' --exclude '*/espeak-ng-data/*'
    if ($LASTEXITCODE) { throw 'Could not extract Kokoro model.' }
    Copy-Item -LiteralPath artifacts/models/TTS/LICENSE -Destination artifacts/models/ASR/LICENSE
    if ($Fixtures) {
        & tar.exe -xf .tooling/models/asr.tar.bz2 -C .tooling/asr-fixtures --strip-components 2 "$zipformer/test_wavs"
        if ($LASTEXITCODE) { throw 'Could not extract Zipformer audio fixtures.' }
        & tar.exe -xf .tooling/models/sensevoice.tar.bz2 -C .tooling/sensevoice-fixtures --strip-components 2 "$sensevoice/test_wavs"
        if ($LASTEXITCODE) { throw 'Could not extract SenseVoice audio fixtures.' }
        & tar.exe -xf ".tooling/models/$qwen.tar.bz2" -C .tooling/qwen-asr-fixtures --strip-components 2 "$qwen/test_wavs/f1_noise.wav"
        if ($LASTEXITCODE) { throw 'Could not extract Qwen3-ASR audio fixtures.' }
    }
} finally { Pop-Location }
