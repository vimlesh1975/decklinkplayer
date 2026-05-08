# DeckLink Player

Small Windows desktop app for DeckLink playout.

It decodes media with your bundled `ffmpeg.exe`, then outputs video frames and embedded audio directly through the Blackmagic DeckLink SDK.

## What it does

- Opens a desktop GUI for media playout
- Shows and searches video and image files in `C:\casparcg\_media`
- Lists DeckLink devices visible to FFmpeg
- Lists supported modes for a DeckLink device
- Plays a media file out to DeckLink through the Blackmagic SDK
- Plays an internal moving test signal for DeckLink timing checks
- Keeps a live playback log in the app

## Requirements

- Windows with Blackmagic Desktop Video drivers installed
- Your bundled `ffmpeg.exe` in the app/output folder
- FFmpeg build compiled with DeckLink support for device/mode listing
- `DeckLinkAPI.Interop.dll` beside the app for SDK playout

You already appear to have a compatible FFmpeg build in this environment.

## Build

```powershell
dotnet build
```

Each build force-closes any running `ffmpegplayer*` process, deletes older exe copies from the output folder, creates one fresh timestamped exe, and removes the plain `ffmpegplayer.exe`.

## Desktop GUI

Run:

```powershell
dotnet run
```

Or open the latest timestamped app in the build folder:

```powershell
bin\Debug\net10.0-windows\ffmpegplayer_*.exe
```

The app searches only for your bundled `ffmpeg.exe` beside the app or inside common project `bin` folders. It does not fall back to PATH.
The media tree defaults to `C:\casparcg\_media`; use the Search box to find clips or still images by filename, select a file to load it, then use `Play Selected` or double-click a file to start playout. If another file is already playing, `Play Selected` or double-click stops the current playout and starts the new one.
The media field defaults to `go1080p25.mp4` when that file is found beside the app or in a common project output folder.
The DeckLink device defaults to `DeckLink SDI 4K`.
The DeckLink mode defaults to `Hi50`, which is 1920x1080 interlaced 50 fields / 25 frames per second.
The SDK output maps the DeckLink format codes reported by `DeckLink SDI 4K`, including SD, 720p, 1080p/i, 2K DCI, UHD 4K, and DCI 4K modes.
Loop is checked by default in the GUI.
Preroll defaults to `0.5` and duplex defaults to `unset`.
For `DeckLink SDI 4K`, the app always omits `-duplex_mode` because that card reports the option as unsupported.
The SDI link defaults to `single`, and 3G-SDI Level A defaults to `true`; both are exposed in the GUI so you can quickly try `unset` or `false` if the downstream monitor or router needs a different SDI flavor.
Use `Moving Test` to send an internal animated test signal without reading a media file.
SDK playout outputs 48 kHz 32-bit PCM embedded SDI audio for normal file playback.
Still images are shown in the media tree and play as silent held frames until stopped or switched.

## CLI Commands

After building, use the generated DLL for command-line checks:

```powershell
$player = ".\bin\Debug\net10.0-windows\ffmpegplayer.dll"
```

List devices:

```powershell
dotnet $player devices
```

List modes for one device:

```powershell
dotnet $player formats --device "DeckLink SDI 4K"
```

Play a file using a DeckLink format code:

```powershell
dotnet $player play --input "C:\media\clip.mp4" --device "DeckLink SDI 4K" --format-code Hp25
```

Play a file by explicitly setting output size and frame rate:

```powershell
dotnet $player play --input "C:\media\clip.mp4" --device "DeckLink SDI 4K" --video-size 1920x1080 --frame-rate 25
```

Loop forever:

```powershell
dotnet $player play --input "C:\media\clip.mp4" --device "DeckLink SDI 4K" --format-code Hp25 --loop
```

Preview the generated SDK decoder commands without starting playback:

```powershell
dotnet $player play --input "C:\media\clip.mp4" --device "DeckLink SDI 4K" --format-code Hp25 --dry-run
```

## Notes

- The player defaults to `uyvy422`, which is the safest DeckLink output pixel format for this first version.
- Interlaced DeckLink modes are tagged with their listed field order before playout, and the output stream also sets `-field_order`.
- File playout uses `-re` so FFmpeg reads media at real-time speed instead of burst-filling the DeckLink buffer.
- Output size and rate are set with DeckLink-friendly `-s` and `-r` options instead of duplicate scale/fps filters.
- Live decoder/playout logging is shown in the GUI and written to `logs\ffmpeg_playout_*.log` beside the app.
- If you do not pass `--format-code` or explicit `--video-size` and `--frame-rate`, the input file must already match a DeckLink-supported mode.
- Use `Ctrl+C` to stop playout.

## Good next steps

- Add tighter single-process SDK A/V synchronization
- Add scheduled playlist playout
- Add local web control panel
