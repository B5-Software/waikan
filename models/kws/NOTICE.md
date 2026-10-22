# Keyword spotting model notice

Model: `sherpa-onnx-kws-zipformer-zh-en-3M-2025-12-20`

- Project: https://github.com/k2-fsa/sherpa-onnx
- Model release: https://github.com/k2-fsa/sherpa-onnx/releases/tag/kws-models
- License: Apache License 2.0
- Runtime used: `sherpa-onnx` C# API 1.13.8, CPU, int8 encoder/joiner, chunk-16.
- Local inference only. The model detects the configured keyword `外刊`
  (pinyin tokens `w ài k ān`) and does not produce a full transcript.

The model package also contains other chunk sizes and test recordings which are
not redistributed with this application.
