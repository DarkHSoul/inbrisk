using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Inbrisk.Platform.Windows;

#region Header & Control Structs

/// <summary>
/// Frame metadata header stored with each video frame in the shared memory buffer.
/// Sequential layout ensures binary compatibility across processes and Windows sessions.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct FrameHeader
{
    public const uint DefaultMagic = 0x49424642; // "IBFB" (Inbrisk Frame Buffer)

    /// <summary>Magic identifier verifying header integrity (0x49424642).</summary>
    public uint Magic;

    /// <summary>Width in pixels.</summary>
    public int Width;

    /// <summary>Height in pixels.</summary>
    public int Height;

    /// <summary>Stride / row pitch in bytes.</summary>
    public int Stride;

    /// <summary>Monotonically increasing sequence number for this frame.</summary>
    public long FrameIndex;

    /// <summary>Timestamp in UTC ticks (DateTime.UtcNow.Ticks or Stopwatch timestamp).</summary>
    public long TimestampTicks;

    /// <summary>True if magic and dimensions indicate a valid populated frame.</summary>
    public readonly bool IsValid => Magic == DefaultMagic && Width > 0 && Height > 0 && Stride > 0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BufferControlBlock
{
    public const uint DefaultMagic = 0x49424642; // "IBFB"

    public uint Magic;
    public int Version;
    public int SlotCount;
    public volatile int ActiveSlotIndex;
    public long SlotCapacity;
    public long Slot0Offset;
    public long Slot1Offset;
    public long TotalFramesWritten;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SlotControlHeader
{
    public FrameHeader Header;
    public long WriteSequence;
    public long DataLength;
    public long Reserved1;
    public long Reserved2;
}

#endregion

/// <summary>
/// High-performance shared memory ring/double-buffer for low-overhead, zero-copy,
/// tear-free desktop frame transmission across isolated Windows sessions
/// (e.g. Session 2 Ghost worker producer to Session 1 primary user consumer).
/// </summary>
public sealed unsafe class GhostSharedFrameBuffer : IDisposable
{
    #region Constants

    public const string DefaultMapName = @"Global\InbriskGhostFrameBuffer";
    public const string FallbackMapName = @"Local\InbriskGhostFrameBuffer";
    public const string DefaultEventName = @"Global\InbriskGhostFrameEvent";
    public const string FallbackEventName = @"Local\InbriskGhostFrameEvent";

    /// <summary>Default max slot size: 3840 x 2160 x 4 = ~33.18 MB (4K 32bpp RGBA/BGRA).</summary>
    public const int DefaultMaxSlotSize = 3840 * 2160 * 4;

    private const int PageAlignment = 4096;
    private const uint SECURITY_DESCRIPTOR_REVISION = 1;
    private const uint PAGE_READWRITE = 0x04;
    private const uint PAGE_READONLY = 0x02;
    private const uint FILE_MAP_WRITE = 0x0002;
    private const uint FILE_MAP_READ = 0x0004;
    private const uint FILE_MAP_ALL_ACCESS = 0x000F001F;
    private const uint SYNCHRONIZE = 0x00100000;
    private const uint EVENT_MODIFY_STATE = 0x0002;
    private const uint EVENT_ALL_ACCESS = 0x1F0003;
    private const uint WAIT_OBJECT_0 = 0x00000000;
    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_ALREADY_EXISTS = 183;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    #endregion

    #region Win32 P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_DESCRIPTOR
    {
        public byte Revision;
        public byte Sbz1;
        public ushort Control;
        public IntPtr Owner;
        public IntPtr Group;
        public IntPtr Sacl;
        public IntPtr Dacl;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeSecurityDescriptor(out SECURITY_DESCRIPTOR pSecurityDescriptor, uint dwRevision);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSecurityDescriptorDacl(ref SECURITY_DESCRIPTOR pSecurityDescriptor, [MarshalAs(UnmanagedType.Bool)] bool bDaclPresent, IntPtr pDacl, [MarshalAs(UnmanagedType.Bool)] bool bDaclDefaulted);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileMappingW(IntPtr hFile, ref SECURITY_ATTRIBUTES lpFileMappingAttributes, uint flProtect, uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileMappingW(IntPtr hFile, IntPtr lpFileMappingAttributes, uint flProtect, uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenFileMappingW(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEventW(ref SECURITY_ATTRIBUTES lpEventAttributes, [MarshalAs(UnmanagedType.Bool)] bool bManualReset, [MarshalAs(UnmanagedType.Bool)] bool bInitialState, string lpName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenEventW(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ResetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    #endregion

    #region State

    private readonly bool _isProducer;
    private readonly string _mapName;
    private readonly string? _eventName;
    private readonly long _slotCapacity;
    private readonly long _slot0Offset;
    private readonly long _slot1Offset;

    private IntPtr _mapHandle = IntPtr.Zero;
    private IntPtr _mappedViewBase = IntPtr.Zero;
    private IntPtr _eventHandle = IntPtr.Zero;
    private BufferControlBlock* _controlBlock;

    private long _localFrameIndex;
    private long _lastReadFrameIndex = -1;
    private bool _isDisposed;

    #endregion

    #region Properties

    /// <summary>Active Windows kernel memory map name used by this instance.</summary>
    public string MapName => _mapName;

    /// <summary>Active Windows kernel event name used for frame synchronization (if available).</summary>
    public string? EventName => _eventName;

    /// <summary>True if this instance is writing frames, false if reading.</summary>
    public bool IsProducer => _isProducer;

    /// <summary>Maximum payload capacity in bytes for each frame slot.</summary>
    public long SlotCapacity => _slotCapacity;

    /// <summary>Frame index of the most recently read frame by this consumer.</summary>
    public long LastReadFrameIndex => _lastReadFrameIndex;

    /// <summary>Total frames committed to this shared memory buffer by producers.</summary>
    public long TotalFramesWritten => _controlBlock != null ? _controlBlock->TotalFramesWritten : 0;

    /// <summary>Currently active ready slot index (0 or 1, or -1 if no frame written yet).</summary>
    public int ActiveSlotIndex => _controlBlock != null ? _controlBlock->ActiveSlotIndex : -1;

    /// <summary>True if this buffer has been disposed.</summary>
    public bool IsDisposed => _isDisposed;

    #endregion

    #region Constructors & Factory Methods

    /// <summary>
    /// Opens or creates the shared frame buffer with cross-session NULL DACL permissions.
    /// </summary>
    /// <param name="isProducer">True to initialize as writer/producer, false as reader/consumer.</param>
    /// <param name="mapName">Name of the memory mapped file (default: Global\InbriskGhostFrameBuffer).</param>
    /// <param name="maxSlotSize">Maximum size per buffer slot in bytes (default: 3840x2160x4).</param>
    public GhostSharedFrameBuffer(
        bool isProducer = true,
        string mapName = DefaultMapName,
        int maxSlotSize = DefaultMaxSlotSize)
    {
        _isProducer = isProducer;

        if (maxSlotSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSlotSize), "Slot size must be positive.");
        }

        // Layout calculation: Control block at offset 0, Slot 0 at 4KB, Slot 1 aligned after Slot 0.
        int slotHeaderSize = sizeof(SlotControlHeader);
        long alignedSlotSpan = AlignUp(slotHeaderSize + maxSlotSize, PageAlignment);
        _slot0Offset = PageAlignment;
        _slot1Offset = _slot0Offset + alignedSlotSpan;
        long totalMappingSize = _slot1Offset + alignedSlotSpan;

        if (isProducer)
        {
            _mapHandle = CreateSharedMemoryWithNullDacl(
                totalMappingSize,
                mapName,
                allowFallback: true,
                out _mapName,
                out bool alreadyExisted);

            _mappedViewBase = MapViewOfFile(
                _mapHandle,
                FILE_MAP_ALL_ACCESS,
                0,
                0,
                UIntPtr.Zero);

            if (_mappedViewBase == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, $"MapViewOfFile failed with error {err}.");
            }

            _controlBlock = (BufferControlBlock*)_mappedViewBase;

            if (!alreadyExisted || _controlBlock->Magic != BufferControlBlock.DefaultMagic)
            {
                _controlBlock->Magic = BufferControlBlock.DefaultMagic;
                _controlBlock->Version = 1;
                _controlBlock->SlotCount = 2;
                _controlBlock->ActiveSlotIndex = -1;
                _controlBlock->SlotCapacity = maxSlotSize;
                _controlBlock->Slot0Offset = _slot0Offset;
                _controlBlock->Slot1Offset = _slot1Offset;
                _controlBlock->TotalFramesWritten = 0;
            }

            _slotCapacity = _controlBlock->SlotCapacity;
            _slot0Offset = _controlBlock->Slot0Offset;
            _slot1Offset = _controlBlock->Slot1Offset;

            // Optional synchronization event
            _eventName = _mapName.Contains("Local") ? FallbackEventName : DefaultEventName;
            _eventHandle = CreateEventWithNullDacl(_eventName);
        }
        else
        {
            _mapHandle = OpenSharedMemoryWithFallback(
                mapName,
                readOnly: true,
                out _mapName);

            _mappedViewBase = MapViewOfFile(
                _mapHandle,
                FILE_MAP_READ,
                0,
                0,
                UIntPtr.Zero);

            if (_mappedViewBase == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, $"MapViewOfFile failed with error {err}.");
            }

            _controlBlock = (BufferControlBlock*)_mappedViewBase;

            if (_controlBlock->Magic != BufferControlBlock.DefaultMagic)
            {
                throw new InvalidOperationException("Shared frame buffer control block has invalid magic signature.");
            }

            _slotCapacity = _controlBlock->SlotCapacity;
            _slot0Offset = _controlBlock->Slot0Offset;
            _slot1Offset = _controlBlock->Slot1Offset;

            // Optional synchronization event
            _eventName = _mapName.Contains("Local") ? FallbackEventName : DefaultEventName;
            _eventHandle = OpenEventW(SYNCHRONIZE, false, _eventName);
        }
    }

    /// <summary>Creates a producer instance initialized for publishing frames.</summary>
    public static GhostSharedFrameBuffer CreateProducer(
        string mapName = DefaultMapName,
        int maxSlotSize = DefaultMaxSlotSize)
        => new(isProducer: true, mapName, maxSlotSize);

    /// <summary>Opens an existing shared memory buffer for consumer reading.</summary>
    public static GhostSharedFrameBuffer OpenConsumer(
        string mapName = DefaultMapName)
        => new(isProducer: false, mapName);

    /// <summary>Tries to open an existing shared memory buffer for consumer reading.</summary>
    public static bool TryOpenConsumer(
        out GhostSharedFrameBuffer? consumer,
        string mapName = DefaultMapName)
    {
        try
        {
            consumer = new GhostSharedFrameBuffer(isProducer: false, mapName);
            return true;
        }
        catch
        {
            consumer = null;
            return false;
        }
    }

    #endregion

    #region Producer API

    /// <summary>
    /// Writes a complete video frame into the inactive shared memory slot and atomically commits it.
    /// Guarantees tear-free reading for consumers via seqlock write sequences.
    /// </summary>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="stride">Row stride in bytes.</param>
    /// <param name="pixelData">Raw pixel bytes (e.g. BGRA/RGBA).</param>
    public void WriteFrame(int width, int height, int stride, ReadOnlySpan<byte> pixelData)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (!_isProducer)
        {
            throw new InvalidOperationException("Cannot write to frame buffer in consumer mode.");
        }

        if (width <= 0 || height <= 0 || stride <= 0)
        {
            throw new ArgumentException($"Invalid frame dimensions: {width}x{height}, stride {stride}");
        }

        if (pixelData.Length > _slotCapacity)
        {
            throw new ArgumentException(
                $"Pixel data size ({pixelData.Length} bytes) exceeds buffer slot capacity ({_slotCapacity} bytes).",
                nameof(pixelData));
        }

        // Determine inactive slot (double buffering)
        int currentActive = Volatile.Read(ref _controlBlock->ActiveSlotIndex);
        int targetSlot = (currentActive == 0) ? 1 : 0;

        SlotControlHeader* slotHeader = GetSlotControlHeader(targetSlot);
        long seq = Volatile.Read(ref slotHeader->WriteSequence);

        // Sequence increment to odd indicates write in progress
        Volatile.Write(ref slotHeader->WriteSequence, seq + 1);
        Thread.MemoryBarrier();

        // Zero-copy direct memory copy to target slot
        byte* destPtr = GetSlotDataPtr(targetSlot);
        fixed (byte* srcPtr = pixelData)
        {
            Buffer.MemoryCopy(srcPtr, destPtr, _slotCapacity, pixelData.Length);
        }

        // Populate slot header
        slotHeader->Header.Magic = FrameHeader.DefaultMagic;
        slotHeader->Header.Width = width;
        slotHeader->Header.Height = height;
        slotHeader->Header.Stride = stride;
        slotHeader->Header.FrameIndex = Interlocked.Increment(ref _localFrameIndex);
        slotHeader->Header.TimestampTicks = DateTime.UtcNow.Ticks;
        slotHeader->DataLength = pixelData.Length;

        // Sequence increment to even indicates write complete
        Thread.MemoryBarrier();
        Volatile.Write(ref slotHeader->WriteSequence, seq + 2);
        Thread.MemoryBarrier();

        // Atomically switch active slot
        Volatile.Write(ref _controlBlock->ActiveSlotIndex, targetSlot);
        Interlocked.Increment(ref _controlBlock->TotalFramesWritten);

        // Signal consumer event if initialized
        SignalFrameReady();
    }

    #endregion

    #region Consumer API

    /// <summary>
    /// Attempts to read the most recent valid frame into the provided destination buffer.
    /// Returns false if no frame is available, if a write collision occurred (tear prevention),
    /// or if the destination buffer is insufficient.
    /// </summary>
    public bool TryReadLatestFrame(Span<byte> buffer, out FrameHeader header)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        // Retry loop to handle concurrent slot completion smoothly
        for (int attempt = 0; attempt < 2; attempt++)
        {
            int activeSlot = Volatile.Read(ref _controlBlock->ActiveSlotIndex);
            if (activeSlot < 0 || activeSlot > 1)
            {
                header = default;
                return false;
            }

            SlotControlHeader* slotHeader = GetSlotControlHeader(activeSlot);
            long seq1 = Volatile.Read(ref slotHeader->WriteSequence);

            if ((seq1 & 1) != 0)
            {
                // Slot is currently being written to by producer
                continue;
            }

            FrameHeader h = slotHeader->Header;
            if (h.Magic != FrameHeader.DefaultMagic)
            {
                header = default;
                return false;
            }

            int dataLength = (int)slotHeader->DataLength;
            if (dataLength <= 0 || buffer.Length < dataLength)
            {
                header = default;
                return false;
            }

            byte* srcPtr = GetSlotDataPtr(activeSlot);
            fixed (byte* destPtr = buffer)
            {
                Buffer.MemoryCopy(srcPtr, destPtr, buffer.Length, dataLength);
            }

            Thread.MemoryBarrier();
            long seq2 = Volatile.Read(ref slotHeader->WriteSequence);

            if (seq1 == seq2)
            {
                // Consistency check passed: no write collision occurred during copy
                header = h;
                _lastReadFrameIndex = h.FrameIndex;
                return true;
            }
        }

        header = default;
        return false;
    }

    /// <summary>
    /// Direct zero-copy accessor for the active frame in shared memory.
    /// Provides direct read-only pointer access to the mapped slot without copying memory.
    /// Double buffering protects this slot until the next alternating frame write begins.
    /// </summary>
    public bool TryGetLatestFramePointer(out FrameHeader header, out ReadOnlySpan<byte> pixelSpan)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        int activeSlot = Volatile.Read(ref _controlBlock->ActiveSlotIndex);
        if (activeSlot < 0 || activeSlot > 1)
        {
            header = default;
            pixelSpan = default;
            return false;
        }

        SlotControlHeader* slotHeader = GetSlotControlHeader(activeSlot);
        long seq1 = Volatile.Read(ref slotHeader->WriteSequence);

        if ((seq1 & 1) != 0)
        {
            header = default;
            pixelSpan = default;
            return false;
        }

        FrameHeader h = slotHeader->Header;
        if (h.Magic != FrameHeader.DefaultMagic)
        {
            header = default;
            pixelSpan = default;
            return false;
        }

        int dataLength = (int)slotHeader->DataLength;
        if (dataLength <= 0)
        {
            header = default;
            pixelSpan = default;
            return false;
        }

        header = h;
        pixelSpan = new ReadOnlySpan<byte>(GetSlotDataPtr(activeSlot), dataLength);
        _lastReadFrameIndex = h.FrameIndex;
        return true;
    }

    /// <summary>
    /// Waits for the producer to signal a new frame via the named kernel event.
    /// Returns true if signaled, false if timed out or synchronization event is not active.
    /// </summary>
    public bool WaitForNewFrame(int timeoutMilliseconds = 1000)
    {
        if (_eventHandle == IntPtr.Zero || _eventHandle == INVALID_HANDLE_VALUE)
        {
            return false;
        }

        uint res = WaitForSingleObject(_eventHandle, (uint)Math.Max(0, timeoutMilliseconds));
        return res == WAIT_OBJECT_0;
    }

    #endregion

    #region Internal Helpers

    private SlotControlHeader* GetSlotControlHeader(int slotIndex)
    {
        long offset = slotIndex == 0 ? _slot0Offset : _slot1Offset;
        return (SlotControlHeader*)((byte*)_mappedViewBase + offset);
    }

    private byte* GetSlotDataPtr(int slotIndex)
    {
        long offset = (slotIndex == 0 ? _slot0Offset : _slot1Offset) + sizeof(SlotControlHeader);
        return (byte*)_mappedViewBase + offset;
    }

    private void SignalFrameReady()
    {
        if (_eventHandle != IntPtr.Zero && _eventHandle != INVALID_HANDLE_VALUE)
        {
            SetEvent(_eventHandle);
        }
    }

    private static long AlignUp(long value, long alignment)
    {
        return (value + alignment - 1) & ~(alignment - 1);
    }

    #endregion

    #region Security & Memory Mapping Helpers

    /// <summary>
    /// Creates a shared memory section with a NULL DACL security descriptor.
    /// NULL DACL grants read/write permissions to processes across all Windows sessions (Session 0, 1, 2+).
    /// </summary>
    private static IntPtr CreateSharedMemoryWithNullDacl(
        long size,
        string requestedName,
        bool allowFallback,
        out string actualName,
        out bool alreadyExisted)
    {
        actualName = requestedName;
        alreadyExisted = false;

        var sd = new SECURITY_DESCRIPTOR();
        if (!InitializeSecurityDescriptor(out sd, SECURITY_DESCRIPTOR_REVISION))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeSecurityDescriptor failed.");
        }

        // bDaclPresent = true, pDacl = NULL specifies a NULL DACL (unconditional access to everyone across sessions)
        if (!SetSecurityDescriptorDacl(ref sd, true, IntPtr.Zero, false))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetSecurityDescriptorDacl failed.");
        }

        IntPtr pSd = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_DESCRIPTOR>());
        try
        {
            Marshal.StructureToPtr(sd, pSd, false);

            var sa = new SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = pSd,
                bInheritHandle = false
            };

            uint high = (uint)(size >> 32);
            uint low = (uint)(size & 0xFFFFFFFF);

            IntPtr handle = CreateFileMappingW(
                INVALID_HANDLE_VALUE,
                ref sa,
                PAGE_READWRITE,
                high,
                low,
                requestedName);

            if (handle == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();

                // If Global namespace access denied (e.g. non-elevated user), fallback to Local namespace
                if (allowFallback && err == ERROR_ACCESS_DENIED && requestedName.StartsWith(@"Global\", StringComparison.OrdinalIgnoreCase))
                {
                    actualName = requestedName.Replace(@"Global\", @"Local\");
                    handle = CreateFileMappingW(
                        INVALID_HANDLE_VALUE,
                        ref sa,
                        PAGE_READWRITE,
                        high,
                        low,
                        actualName);

                    if (handle == IntPtr.Zero)
                    {
                        err = Marshal.GetLastWin32Error();
                        throw new Win32Exception(err, $"CreateFileMapping failed for '{actualName}' with error {err}.");
                    }
                }
                else
                {
                    throw new Win32Exception(err, $"CreateFileMapping failed for '{requestedName}' with error {err}.");
                }
            }

            alreadyExisted = (Marshal.GetLastWin32Error() == ERROR_ALREADY_EXISTS);
            return handle;
        }
        finally
        {
            Marshal.FreeHGlobal(pSd);
        }
    }

    private static IntPtr OpenSharedMemoryWithFallback(
        string requestedName,
        bool readOnly,
        out string actualName)
    {
        actualName = requestedName;
        uint access = readOnly ? FILE_MAP_READ : FILE_MAP_ALL_ACCESS;

        IntPtr handle = OpenFileMappingW(access, false, requestedName);
        if (handle == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();

            // Check fallback to Local if Global not found or inaccessible
            if (requestedName.StartsWith(@"Global\", StringComparison.OrdinalIgnoreCase) &&
                (err == ERROR_FILE_NOT_FOUND || err == ERROR_ACCESS_DENIED))
            {
                actualName = requestedName.Replace(@"Global\", @"Local\");
                handle = OpenFileMappingW(access, false, actualName);
                if (handle != IntPtr.Zero)
                {
                    return handle;
                }
            }

            throw new Win32Exception(err, $"OpenFileMapping failed for '{requestedName}' with error {err}.");
        }

        return handle;
    }

    private static IntPtr CreateEventWithNullDacl(string eventName)
    {
        try
        {
            var sd = new SECURITY_DESCRIPTOR();
            if (!InitializeSecurityDescriptor(out sd, SECURITY_DESCRIPTOR_REVISION) ||
                !SetSecurityDescriptorDacl(ref sd, true, IntPtr.Zero, false))
            {
                return IntPtr.Zero;
            }

            IntPtr pSd = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_DESCRIPTOR>());
            try
            {
                Marshal.StructureToPtr(sd, pSd, false);
                var sa = new SECURITY_ATTRIBUTES
                {
                    nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                    lpSecurityDescriptor = pSd,
                    bInheritHandle = false
                };

                return CreateEventW(ref sa, false, false, eventName);
            }
            finally
            {
                Marshal.FreeHGlobal(pSd);
            }
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        if (_mappedViewBase != IntPtr.Zero)
        {
            UnmapViewOfFile(_mappedViewBase);
            _mappedViewBase = IntPtr.Zero;
        }

        if (_mapHandle != IntPtr.Zero && _mapHandle != INVALID_HANDLE_VALUE)
        {
            CloseHandle(_mapHandle);
            _mapHandle = IntPtr.Zero;
        }

        if (_eventHandle != IntPtr.Zero && _eventHandle != INVALID_HANDLE_VALUE)
        {
            CloseHandle(_eventHandle);
            _eventHandle = IntPtr.Zero;
        }

        GC.SuppressFinalize(this);
    }

    ~GhostSharedFrameBuffer()
    {
        Dispose();
    }

    #endregion
}
