using System.Runtime.InteropServices;
using DeckLinkAPI;

namespace ffmpegplayer;

internal sealed class NativeDeckLinkPreviewOutput : IDisposable
{
    private const int BytesPerPixelUyvy = 2;

    private readonly IDeckLink _deckLink;
    private readonly IDeckLinkOutput_v14_2_1? _outputV14;
    private readonly IDeckLinkOutput_v11_4? _outputV11;
    private readonly IDeckLinkOutput_v10_11? _outputV10;
    private readonly IDeckLinkOutput? _displayModeOutput;
    private readonly string _device;
    private readonly _BMDDisplayMode _displayMode;
    private int _width;
    private int _height;
    private int _rowBytes;
    private int _frameBytes;
    private bool _disposed;

    public NativeDeckLinkPreviewOutput(PlayRequest request)
    {
        var mode = DeckLinkSdkPlayer.ResolveDisplayMode(request);
        _device = request.Device;
        _displayMode = mode.DisplayMode;

        if (DeckLinkSdkPlayer.TryTakeHeldDeckLinkOutput(request.Device, _displayMode, out var heldDeckLink, out var heldOutput))
        {
            _deckLink = heldDeckLink ?? throw new InvalidOperationException("Held DeckLink output is missing its device.");
            _outputV14 = heldOutput ?? throw new InvalidOperationException("Held DeckLink output is missing its output interface.");
            _outputV14.GetDisplayMode(_displayMode, out var heldDisplayModeInfo);
            SetFrameGeometry(heldDisplayModeInfo);
            return;
        }

        _deckLink = DeckLinkSdkPlayer.FindDeckLink(request.Device);
        if (TryCastDeckLinkOutput(_deckLink, out IDeckLinkOutput_v11_4? outputV11) && outputV11 is not null)
        {
            _outputV11 = outputV11;
            outputV11.GetDisplayMode(_displayMode, out var displayModeInfo);
            SetFrameGeometry(displayModeInfo);
            outputV11.EnableVideoOutput(_displayMode, _BMDVideoOutputFlags.bmdVideoOutputFlagDefault);
            return;
        }

        if (TryCastDeckLinkOutput(_deckLink, out IDeckLinkOutput_v10_11? outputV10) && outputV10 is not null)
        {
            _outputV10 = outputV10;
            _displayModeOutput = (IDeckLinkOutput)_deckLink;
            _displayModeOutput.GetDisplayMode(_displayMode, out var displayModeInfo);
            SetFrameGeometry(displayModeInfo);
            outputV10.EnableVideoOutput(_displayMode, _BMDVideoOutputFlags.bmdVideoOutputFlagDefault);
            return;
        }

        throw new InvalidOperationException("DeckLink output does not expose a supported single-frame output interface.");
    }

    private static bool TryCastDeckLinkOutput<TOutput>(IDeckLink deckLink, out TOutput? output)
        where TOutput : class
    {
        try
        {
            output = (TOutput)deckLink;
            return true;
        }
        catch (InvalidCastException)
        {
        }
        catch (COMException)
        {
        }

        output = null;
        return false;
    }

    private void SetFrameGeometry(IDeckLinkDisplayMode displayModeInfo)
    {
        _width = displayModeInfo.GetWidth();
        _height = displayModeInfo.GetHeight();
        _rowBytes = checked(_width * BytesPerPixelUyvy);
        _frameBytes = checked(_rowBytes * _height);
    }

    public void DisplayFrame(byte[] uyvyFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (uyvyFrame.Length < _frameBytes)
        {
            throw new InvalidOperationException($"Preview frame is too small. Expected {_frameBytes} bytes, got {uyvyFrame.Length}.");
        }

        if (_outputV14 is not null)
        {
            DisplayFrameV14(uyvyFrame);
            return;
        }

        if (_outputV11 is not null)
        {
            DisplayFrameV11(uyvyFrame);
            return;
        }

        if (_outputV10 is not null)
        {
            DisplayFrameV10(uyvyFrame);
            return;
        }

        throw new InvalidOperationException("DeckLink scrub output is not initialized.");
    }

    private void DisplayFrameV14(byte[] uyvyFrame)
    {
        IDeckLinkMutableVideoFrame_v14_2_1? mutableFrame = null;
        try
        {
            _outputV14!.CreateVideoFrame(
                _width,
                _height,
                _rowBytes,
                _BMDPixelFormat.bmdFormat8BitYUV,
                _BMDFrameFlags.bmdFrameFlagDefault,
                out mutableFrame);

            mutableFrame.GetBytes(out var destination);
            Marshal.Copy(uyvyFrame, 0, destination, _frameBytes);
            _outputV14.DisplayVideoFrameSync((IDeckLinkVideoFrame_v14_2_1)mutableFrame);
        }
        finally
        {
            DeckLinkSdkPlayer.ReleaseCom(mutableFrame);
        }
    }

    private void DisplayFrameV11(byte[] uyvyFrame)
    {
        IDeckLinkMutableVideoFrame_v14_2_1? mutableFrame = null;
        try
        {
            _outputV11!.CreateVideoFrame(
                _width,
                _height,
                _rowBytes,
                _BMDPixelFormat.bmdFormat8BitYUV,
                _BMDFrameFlags.bmdFrameFlagDefault,
                out mutableFrame);

            mutableFrame.GetBytes(out var destination);
            Marshal.Copy(uyvyFrame, 0, destination, _frameBytes);
            _outputV11.DisplayVideoFrameSync((IDeckLinkVideoFrame_v14_2_1)mutableFrame);
        }
        finally
        {
            DeckLinkSdkPlayer.ReleaseCom(mutableFrame);
        }
    }

    private void DisplayFrameV10(byte[] uyvyFrame)
    {
        IDeckLinkMutableVideoFrame_v14_2_1? mutableFrame = null;
        try
        {
            _outputV10!.CreateVideoFrame(
                _width,
                _height,
                _rowBytes,
                _BMDPixelFormat.bmdFormat8BitYUV,
                _BMDFrameFlags.bmdFrameFlagDefault,
                out mutableFrame);

            mutableFrame.GetBytes(out var destination);
            Marshal.Copy(uyvyFrame, 0, destination, _frameBytes);
            _outputV10.DisplayVideoFrameSync((IDeckLinkVideoFrame_v14_2_1)mutableFrame);
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

        if (_outputV14 is null)
        {
            Dispose();
            return;
        }

        DeckLinkSdkPlayer.HoldVideoOutput(_device, _displayMode, _deckLink, _outputV14, null);
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
            if (_outputV14 is not null)
            {
                _outputV14.DisableVideoOutput();
            }
            else if (_outputV11 is not null)
            {
                _outputV11.DisableVideoOutput();
            }
            else
            {
                _outputV10?.DisableVideoOutput();
            }
        }
        catch
        {
            // Best effort cleanup while switching back to live playback.
        }

        DeckLinkSdkPlayer.ReleaseCom(_outputV14);
        DeckLinkSdkPlayer.ReleaseCom(_outputV11);
        DeckLinkSdkPlayer.ReleaseCom(_outputV10);
        DeckLinkSdkPlayer.ReleaseCom(_displayModeOutput);
        DeckLinkSdkPlayer.ReleaseCom(_deckLink);
        _disposed = true;
    }
}
