# Third-party notices

The original Waikan source code and documentation are dedicated to the public
domain under CC0 1.0 (see `LICENSE`). Third-party components and model weights
remain under their own licenses and are **not** relicensed under CC0.

This application redistributes the following open-source components. The
licenses are included in the `licenses` directory.

## sherpa-onnx

- Project: https://github.com/k2-fsa/sherpa-onnx
- C# NuGet package: `org.k2fsa.sherpa.onnx` 1.13.8
- Native runtime: `onnxruntime` and `sherpa-onnx-c-api`
- License: Apache License 2.0
- Copyright: the sherpa-onnx contributors

## Keyword spotting model

- Model: `sherpa-onnx-kws-zipformer-zh-en-3M-2025-12-20`
- Source: https://github.com/k2-fsa/sherpa-onnx/releases/tag/kws-models
- License: Apache License 2.0
- Files redistributed: int8 encoder, decoder, int8 joiner, tokens; chunk-16 configuration only.
- The model detects the configured keyword `外刊`; no other model assets (test audio, alternate chunk sizes) are redistributed.
- Additional notice: `models/kws/NOTICE.md`

## NAudio

- Project: https://github.com/naudio/NAudio
- Version: 2.2.1
- License: MIT
- Copyright: 2008-2026 Mark Heath

## Microsoft .NET / WPF

- Runtime: .NET 10, Windows Desktop Runtime
- License: MIT
- The self-contained distribution includes the official .NET runtime files.
- License text: `licenses/dotnet-LICENSE.txt`
- Microsoft notices: `licenses/dotnet-ThirdPartyNotices.txt`

## Microsoft.Data.Sqlite

- Project: https://github.com/dotnet/efcore
- Version: 10.0.13
- License: MIT
- The package includes the SQLitePCLRaw bundles.

## SQLite / SQLitePCLRaw

- SQLite: https://www.sqlite.org/
- SQLite is in the public domain.
- SQLitePCLRaw is licensed under Apache License 2.0.
- SQLitePCLRaw project: https://github.com/ericsink/SQLitePCLRaw

## Windows APIs

The application calls Windows APIs through P/Invoke for DWM title bars,
topmost overlay windows, and process/window integration. These are operating
system APIs, not redistributed code.

## Full license texts

- Apache License 2.0: `licenses/Apache-2.0.txt`
- NAudio MIT: `licenses/NAudio-MIT.txt`
- .NET MIT + ThirdPartyNotices: `licenses/dotnet-LICENSE.txt`, `licenses/dotnet-ThirdPartyNotices.txt`

If you redistribute this application, keep this file and the `licenses`
directory together with the binary.
