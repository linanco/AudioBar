# AudioBar

AudioBar is a real-time desktop audio visualizer for Windows. It captures the system output mix via WASAPI loopback and renders a configurable spectrum bar over the taskbar area, with per-bar peak normalization, multi-band independent detection and a soft saturation curve.

![Effect Preview](screenshot.png)

## Features

- **Per-bar peak normalization** — each bar follows its own peak envelope (fast attack / slow release) to preserve dynamic response across frequency bands.
- **Multi-band independent detection** — low-end, mid-range (vocal), presence (2–6 kHz) and high-end bands are tracked separately so voice, melody and transients no longer compete for visual headroom.
- **High-end highlight** — bars flash white on presence / hi-hat / high-solo activity and smoothly fade back to the wallpaper-derived gradient.
- **Soft saturation curve** — continuous tones settle at moderate heights, only transient events (kicks, snares, plucks) push the bars to the top, eliminating the "all-flat-top" problem.
- **Robust capture lifecycle** — automatic device reattachment on hot-plug, watchdog recovery, and single-instance enforcement via a named mutex.

## Requirements

- Windows 10 / 11 x64
- .NET 9 Desktop Runtime (the self-contained release binary ships the runtime and needs no pre-installation)

## Build

```bash
dotnet build -c Release
```

Build output: `bin\Release\net9.0-windows\AudioBar.exe`

For a single-file self-contained binary (recommended for distribution):

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
```

## Run

```bash
dotnet run -c Release
```

Or launch the published `AudioBar.exe` directly.

## License

TBD
