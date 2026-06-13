# DecklinkPlayer

DecklinkPlayer is a Windows desktop playout tool for Blackmagic DeckLink cards. It is built for quick studio use: browse a media library, preview inside the app, seek, pause, shuttle, play playlists, and output SDI through the Blackmagic DeckLink SDK.

FFmpeg is used only for decoding, probing, preview helpers, and PC preview audio. DeckLink SDI output is handled by the Blackmagic SDK.

## Current Stable Output

DeckLink playout is fixed to one clean broadcast output mode:

- Device: selected from the DeckLink card dropdown
- Format: `1080i50`
- DeckLink mode code: `Hi50`
- Size: `1920x1080`
- Frame rate: `25 fps`
- Field order: upper field first
- Pixel format: `uyvy422`
- Audio: 48 kHz, 32-bit PCM, stereo embedded in SDI

There is no manual sync control and no DeckLink mode selector. This avoids accidental format drift.

## Important Sync Note

For DeckLink output, judge sync using SDI video with embedded SDI audio.

`PC audio` is intentionally disabled during DeckLink playout because Windows/ffplay monitoring latency can make sync look wrong. `PC audio` is available only in Preview-only mode.

## Features

- Desktop WinForms GUI.
- Blackmagic DeckLink SDK playout with embedded audio.
- Fixed `1080i50` SDI output.
- DeckLink card selector with refresh.
- Preview-only mode when no DeckLink output is needed.
- In-app video preview beside SDI output.
- Vertical left/right audio meters in dBFS.
- Media tree and search, defaulting to `C:\casparcg\_media`.
- Video and still-image browsing.
- Pause, resume, stop, seekbar, frame step, 5-frame, 10-frame, and 1-second seek buttons.
- Forward and reverse shuttle speeds.
- Playlist playback.
- MediaInfo window.
- Playback logs in a `logs` subfolder.
- x64-only build.

## Requirements

- Windows x64.
- .NET Desktop Runtime 10, unless you publish self-contained.
- Blackmagic Desktop Video drivers.
- `DeckLinkAPI.Interop.dll` in the output folder.
- Your bundled 64-bit FFmpeg binaries beside the running exe:
  - `ffmpeg.exe`
  - `ffprobe.exe`
  - `ffplay.exe` only for Preview-only PC audio

The app uses the bundled FFmpeg binaries beside the executable, not random FFmpeg from `PATH`.

## Build

Build x64:

```powershell
dotnet build .\ffmpegplayer.csproj -p:Platform=x64
```

The build target automatically:

- Closes old running `decklinkplayer*`, `ffmpegplayer*`, and `ffmpeg` processes.
- Removes old timestamped player exe files from the output folder.
- Builds into `bin\x64\Debug\DecklinkPlayer`.
- Creates a timestamped exe like `decklinkplayer_130626_100808.exe`.
- Deletes the plain `decklinkplayer.exe`.
- Starts the new timestamped exe automatically.

## Run

Use the latest timestamped exe in:

```text
bin\x64\Debug\DecklinkPlayer
```

Example:

```powershell
.\bin\x64\Debug\DecklinkPlayer\decklinkplayer_130626_100808.exe
```

## Basic Use

1. Choose a media library folder, or use the default `C:\casparcg\_media`.
2. Select a file from the media tree or search results.
3. Select the DeckLink card.
4. Keep `Preview only` unchecked for SDI output.
5. Use SDI embedded audio to judge lip sync.
6. Use `Preview only` if you want local preview without DeckLink output.

## Settings

Settings are saved here:

```text
%APPDATA%\DeckLinkPlayer\settings.txt
```

Saved values include:

- DeckLink device
- Media root folder
- Preview-only preference
- PC audio preference

The SDI output mode is not saved because it is fixed to `1080i50`.

## Logs

Playback logs are written to:

```text
bin\x64\Debug\DecklinkPlayer\logs
```

Log file names look like:

```text
ffmpeg_playout_DDMMYY_HHMMSS.log
```

Logs include decoder commands, FFmpeg messages, SDK playback status, and troubleshooting notes.

## Troubleshooting

- If DeckLink is not installed or not detected, the app remains usable in Preview-only mode.
- If playback cannot start, check that `ffmpeg.exe` and `ffprobe.exe` are beside the timestamped exe.
- If Preview-only PC audio does not start, check that `ffplay.exe` is beside the timestamped exe.
- If SDI sync looks wrong, make sure you are not listening to PC audio while watching DeckLink video.
- If the app asks for .NET runtime, install the x64 .NET Desktop Runtime 10.

## Developer Notes

- Main GUI: `MainForm.cs`
- DeckLink SDK playout engine: `DeckLinkSdkPlayer.cs`
- FFmpeg command helpers and CLI parsing: `FfmpegDeckLink.cs`, `Cli.cs`
- Audio meter control: `AudioMeterBar.cs`
- MediaInfo integration: `MediaInfoForm.cs`, `MediaInfoProvider.cs`
- Media root browser: `MediaRootDialog.cs`
- Reverse playback/cache: `ReverseFrameCache.cs`, `ReverseAudioChunkQueue.cs`
- DeckLink COM interop: `Interop\DeckLinkAPI.Interop.dll`

The project file is still named `ffmpegplayer.csproj` for history, but the built app is `decklinkplayer`.
