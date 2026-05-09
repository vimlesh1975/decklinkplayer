using System.Runtime.InteropServices;
using DeckLinkAPI;

namespace ffmpegplayer;

internal sealed class NativeDeckLinkPreviewOutput : IDisposable
{
    private const int BytesPerPixelUyvy = 2;

    private readonly IDeckLink _deckLink;
    private readonly IDeckLinkOutput_v14_2_1 _output;
    private readonly string _device;
    private readonly _BMDDisplayMode _displayMode;
    private readonly int _width;
    private readonly int _height;
    private readonly int _rowBytes;
    private readonly int _frameBytes;
    private bool _disposed;

    public NativeDeckLinkPreviewOutput(PlayRequest request)
    {
        var mode = DeckLinkSdkPlayer.ResolveDisplayMode(request);
        var acquiredOutput = DeckLinkSdkPlayer.AcquireDeckLinkOutput(request.Device, mode.DisplayMode, null);
        _deckLink = acquiredOutput.DeckLink;
        _output = acquiredOutput.Output;
        _device = request.Device;
        _displayMode = mode.DisplayMode;

        _output.GetDisplayMode(_displayMode, out var displayModeInfo);
        _width = displayModeInfo.GetWidth();
        _height = displayModeInfo.GetHeight();
        _rowBytes = checked(_width * BytesPerPixelUyvy);
        _frameBytes = checked(_rowBytes * _height);
        if (!acquiredOutput.VideoOutputAlreadyEnabled)
        {
            _output.EnableVideoOutput(_displayMode, _BMDVideoOutputFlags.bmdVideoOutputFlagDefault);
        }
    }

    public void DisplayFrame(byte[] uyvyFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (uyvyFrame.Length < _frameBytes)
        {
            throw new InvalidOperationException($"Preview frame is too small. Expected {_frameBytes} bytes, got {uyvyFrame.Length}.");
        }

        IDeckLinkMutableVideoFrame_v14_2_1? mutableFrame = null;
        try
        {
            _output.CreateVideoFrame(
                _width,
                _height,
                _rowBytes,
                _BMDPixelFormat.bmdFormat8BitYUV,
                _BMDFrameFlags.bmdFrameFlagDefault,
                out mutableFrame);

            mutableFrame.GetBytes(out var destination);
            Marshal.Copy(uyvyFrame, 0, destination, _frameBytes);
            _output.DisplayVideoFrameSync((IDeckLinkVideoFrame_v14_2_1)mutableFrame);
        }
        finally
        {
            DeckLinkSdkPlayer.ReleaseCom(mutableFrame);
        }
    }

    public void HoldForReplacement()
    {
        if (_disposed)
        {
            return;
        }

        DeckLinkSdkPlayer.HoldVideoOutput(_device, _displayMode, _deckLink, _output, null);
        _disposed = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _output.DisableVideoOutput();
        }
        catch
        {
            // Best effort cleanup while switching back to live playback.
        }

        DeckLinkSdkPlayer.ReleaseCom(_output);
        DeckLinkSdkPlayer.ReleaseCom(_deckLink);
        _disposed = true;
    }
}
