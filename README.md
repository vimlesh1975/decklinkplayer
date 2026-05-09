# DecklinkPlayer

DecklinkPlayer is a Windows desktop playout tool for Blackmagic DeckLink cards. It is built for practical studio use: browse media, preview locally, monitor audio, seek quickly, and send clean SDI video/audio through the Blackmagic DeckLink SDK.

The app uses your bundled FFmpeg binaries only for decoding and probing. DeckLink output is handled by the Blackmagic SDK.

## Highlights

- Desktop WinForms GUI for fast manual playout.
- Blackmagic DeckLink SDK output with embedded audio.
- Preview-only mode when no DeckLink output is needed.
- In-app video preview alongside SDI output.
- Vertical left/right audio meters in dBFS.
- PC audio monitor option using bundled `ffplay.exe`.
- Media tree and search for a library folder, defaulting to `C:\casparcg\_media`.
- Video and still-image browsing.
- Pause, resume, seekbar, frame step, 5-frame, 10-frame, and 1-second seek controls.
- Starts a newly selected file while another file is already playing.
- Persistent settings for DeckLink device, mode, media folder, preview-only, and PC audio.
- Playback logs written beside the app in `logs\ffmpeg_playout_*.log`.
- x64-only build for DeckLink and FFmpeg compatibility.

## Current Defaults

- App heading: `DecklinkPlayer`
- Default media root: `C:\casparcg\_media`
- Default media file: `go1080p25.mp4`
- Default DeckLink device: `DeckLink SDI 4K`
- Default DeckLink mode: `Hi50` / 1080i50
- Pixel format: `uyvy422`
- Audio: 48 kHz, 32-bit PCM, stereo by default
- Output folder: `bin\x64\Debug\DecklinkPlayer`

## Requirements

- Windows x64.
- .NET Desktop Runtime 10, unless you publish a self-contained build.
- Blackmagic Desktop Video drivers for DeckLink output.
- `DeckLinkAPI.Interop.dll` in the app output folder.
- Your own bundled 64-bit FFmpeg binaries in the app output folder:
  - `ffmpeg.exe`
  - `ffprobe.exe`
  - `ffplay.exe` only if you want PC audio monitor

The app intentionally does not rely on random system FFmpeg from `PATH`. Keep the FFmpeg binaries beside the running `decklinkplayer_*.exe`.

## Build

Build the x64 desktop app:

```powershell
dotnet build .\ffmpegplayer.csproj -p:Platform=x64
```

The build target automatically:

- Closes old running `decklinkplayer*`, `ffmpegplayer*`, and `ffmpeg` processes.
- Removes old timestamped player exe files from the output folder.
- Builds into `bin\x64\Debug\DecklinkPlayer`.
- Creates a timestamped exe like `decklinkplayer_090526_163758.exe`.
- Deletes the plain `decklinkplayer.exe`.
- Starts the new timestamped exe automatically.

## Running

Use the latest timestamped exe in:

```powershell
bin\x64\Debug\DecklinkPlayer
```

Example:

```powershell
.\bin\x64\Debug\DecklinkPlayer\decklinkplayer_090526_163758.exe
```

Make sure `ffmpeg.exe` and `ffprobe.exe` are in the same folder.

## Using The App

1. Choose or browse a media library folder.
2. Search or select a file from the media tree.
3. Use Preview only if you only want local app preview.
4. Uncheck Preview only for DeckLink SDI playout.
5. Use Pause, Stop, seekbar, and frame/second seek buttons while playing.
6. Use Show Settings and Show Log only when you need those panels.

Preview only can be checked and the app will still search for DeckLink devices on startup, so you can switch to SDI output later without restarting.

## DeckLink Output

DeckLink playout uses the Blackmagic SDK directly. FFmpeg decodes video to UYVY frames and audio to PCM; the SDK writes frames and audio to the selected output device.

This route is used because it gives better reliability than FFmpeg's DeckLink muxer for this app's seek, pause, preview, and switching workflow.

## Preview And Audio

The preview window shows the decoded video inside the app. Audio meters show left and right channel peaks in dBFS with green, yellow, and red zones.

If `PC audio` is checked, DecklinkPlayer launches bundled `ffplay.exe` as a monitor. This is separate from embedded DeckLink SDI audio.

## Settings

Settings are saved here:

```text
%APPDATA%\DeckLinkPlayer\settings.txt
```

Saved values include:

- DeckLink device
- DeckLink mode
- Media root folder
- Video size and frame rate
- Pixel format
- Audio channel count
- SDI link and Level A options
- Preview only
- PC audio

## Logs

Playback logs are written to:

```text
bin\x64\Debug\DecklinkPlayer\logs
```

Log files are named:

```text
ffmpeg_playout_DDMMYY_HHMMSS.log
```

These logs include decoder commands, FFmpeg messages, SDK playback status, and useful sync/debug messages.

## Troubleshooting

If DeckLink is not installed or not detected, the app should not show a fatal error. It will stay usable in Preview only mode.

If playback cannot start, check that `ffmpeg.exe` and `ffprobe.exe` are beside the timestamped exe.

If PC audio does not start, check that `ffplay.exe` is also beside the timestamped exe.

If DeckLink output is black, confirm the selected device and mode match your card, monitor, router, and SDI format.

If audio/video sync looks wrong after seeking, test the same file from the beginning and check the latest `logs\ffmpeg_playout_*.log`.

If the app asks for .NET runtime, install the x64 .NET Desktop Runtime 10.

## Developer Notes

- Main GUI: `MainForm.cs`
- DeckLink SDK playout engine: `DeckLinkSdkPlayer.cs`
- FFmpeg command helpers and CLI parsing: `FfmpegDeckLink.cs`, `Cli.cs`
- Audio meter control: `AudioMeterBar.cs`
- Media root browser: `MediaRootDialog.cs`
- DeckLink COM interop: `Interop\DeckLinkAPI.Interop.dll`

The project is named `ffmpegplayer.csproj` for history, but the built assembly and exe are `decklinkplayer`.
