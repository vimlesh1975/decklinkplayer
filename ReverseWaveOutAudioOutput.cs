using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace ffmpegplayer;

internal sealed class ReverseWaveOutAudioOutput : IDisposable
{
    private const int AudioSampleRate = 48000;
    private const int SourceBytesPerSample = 4;
    private const int OutputBytesPerSample = 2;
    private const int WaveMapper = -1;
    private const int WaveFormatPcm = 1;
    private const int WhdrDone = 0x00000001;
    private const int MaxPendingBuffers = 12;

    private readonly object _gate = new();
    private readonly List<WaveBuffer> _pendingBuffers = [];
    private readonly int _sourceChannels;
    private readonly int _outputChannels;
    private IntPtr _waveOut;
    private bool _disposed;

    public ReverseWaveOutAudioOutput(int sourceChannels)
    {
        if (sourceChannels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceChannels), "Audio channels must be positive.");
        }

        _sourceChannels = sourceChannels;
        _outputChannels = Math.Min(sourceChannels, 2);
        var format = new WaveFormatEx
        {
            FormatTag = WaveFormatPcm,
            Channels = (ushort)_outputChannels,
            SamplesPerSec = AudioSampleRate,
            BitsPerSample = 16,
            BlockAlign = (ushort)(_outputChannels * OutputBytesPerSample),
        };
        format.AvgBytesPerSec = format.SamplesPerSec * format.BlockAlign;

        var result = waveOutOpen(out _waveOut, unchecked((uint)WaveMapper), ref format, IntPtr.Zero, IntPtr.Zero, 0);
        if (result != 0)
        {
            throw new InvalidOperationException($"Windows audio output open failed: {result}");
        }
    }

    public void Enqueue(byte[] pcm32, int byteCount)
    {
        if (_disposed || byteCount <= 0)
        {
            return;
        }

        byteCount = Math.Min(byteCount, pcm32.Length);
        var sampleFrames = byteCount / (_sourceChannels * SourceBytesPerSample);
        if (sampleFrames <= 0)
        {
            return;
        }

        var pcm16 = ConvertToPcm16(pcm32, sampleFrames);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            CleanupCompletedBuffersUnderLock();
            if (_pendingBuffers.Count >= MaxPendingBuffers)
            {
                // Keep reverse video responsive; drop audio rather than building latency.
                return;
            }

            var buffer = new WaveBuffer(pcm16);
            PrepareAndWriteBuffer(buffer);
            _pendingBuffers.Add(buffer);
        }
    }

    private byte[] ConvertToPcm16(byte[] pcm32, int sampleFrames)
    {
        var pcm16 = new byte[checked(sampleFrames * _outputChannels * OutputBytesPerSample)];
        for (var frame = 0; frame < sampleFrames; frame++)
        {
            for (var channel = 0; channel < _outputChannels; channel++)
            {
                var sourceOffset = checked((frame * _sourceChannels + channel) * SourceBytesPerSample);
                var sample32 = BinaryPrimitives.ReadInt32LittleEndian(pcm32.AsSpan(sourceOffset, SourceBytesPerSample));
                var sample16 = (short)Math.Clamp(sample32 / 65536, short.MinValue, short.MaxValue);
                var destinationOffset = checked((frame * _outputChannels + channel) * OutputBytesPerSample);
                BinaryPrimitives.WriteInt16LittleEndian(pcm16.AsSpan(destinationOffset, OutputBytesPerSample), sample16);
            }
        }

        return pcm16;
    }

    private void PrepareAndWriteBuffer(WaveBuffer buffer)
    {
        var header = new WaveHeader
        {
            Data = buffer.Data,
            BufferLength = (uint)buffer.Length,
        };
        Marshal.StructureToPtr(header, buffer.Header, false);

        var headerSize = Marshal.SizeOf<WaveHeader>();
        var result = waveOutPrepareHeader(_waveOut, buffer.Header, (uint)headerSize);
        if (result != 0)
        {
            buffer.Dispose();
            throw new InvalidOperationException($"Windows audio prepare failed: {result}");
        }

        buffer.Prepared = true;
        result = waveOutWrite(_waveOut, buffer.Header, (uint)headerSize);
        if (result != 0)
        {
            UnprepareBuffer(buffer);
            buffer.Dispose();
            throw new InvalidOperationException($"Windows audio write failed: {result}");
        }
    }

    private void CleanupCompletedBuffersUnderLock()
    {
        for (var index = _pendingBuffers.Count - 1; index >= 0; index--)
        {
            var buffer = _pendingBuffers[index];
            var header = Marshal.PtrToStructure<WaveHeader>(buffer.Header);
            if ((header.Flags & WhdrDone) == 0)
            {
                continue;
            }

            UnprepareBuffer(buffer);
            buffer.Dispose();
            _pendingBuffers.RemoveAt(index);
        }
    }

    private void UnprepareBuffer(WaveBuffer buffer)
    {
        if (!buffer.Prepared || _waveOut == IntPtr.Zero)
        {
            return;
        }

        waveOutUnprepareHeader(_waveOut, buffer.Header, (uint)Marshal.SizeOf<WaveHeader>());
        buffer.Prepared = false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_waveOut != IntPtr.Zero)
            {
                waveOutReset(_waveOut);
                foreach (var buffer in _pendingBuffers)
                {
                    UnprepareBuffer(buffer);
                    buffer.Dispose();
                }

                _pendingBuffers.Clear();
                waveOutClose(_waveOut);
                _waveOut = IntPtr.Zero;
            }
        }
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out IntPtr waveOut, uint deviceId, ref WaveFormatEx format, IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr waveOut, IntPtr waveHeader, uint waveHeaderSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr waveOut, IntPtr waveHeader, uint waveHeaderSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr waveOut, IntPtr waveHeader, uint waveHeaderSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr waveOut);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr waveOut);

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public int SamplesPerSec;
        public int AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public IntPtr User;
        public uint Flags;
        public uint Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    private sealed class WaveBuffer : IDisposable
    {
        public WaveBuffer(byte[] data)
        {
            Length = data.Length;
            Data = Marshal.AllocHGlobal(data.Length);
            Header = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
            Marshal.Copy(data, 0, Data, data.Length);
        }

        public IntPtr Data { get; }

        public IntPtr Header { get; }

        public int Length { get; }

        public bool Prepared { get; set; }

        public void Dispose()
        {
            Marshal.FreeHGlobal(Header);
            Marshal.FreeHGlobal(Data);
        }
    }
}
