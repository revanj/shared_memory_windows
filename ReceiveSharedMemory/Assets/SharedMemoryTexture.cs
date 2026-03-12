using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

public class SharedMemoryTexture : MonoBehaviour
{
    [Header("Shared Memory Settings")]
    public string sharedMemoryName = "MySharedImage";
    public int imageWidth  = 1920;
    public int imageHeight = 1080;
    public TextureFormat textureFormat = TextureFormat.RGBA32;

    [Header("Target")]
    public Renderer targetRenderer;

    private Texture2D _texture;
    private MemoryMappedFile _mmf;
    private MemoryMappedViewAccessor _accessor;

    private const int HEADER_SIZE = 16;
    private int  _lastFrameIndex = -1;
    private long _dataSize;
    private int  _bytesPerPixel;

    // Pinned pointer directly into the MMF — no intermediate buffer
    private unsafe byte* _mmfPtr;
    private bool _ptrAcquired = false;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct ImageHeader
    {
        public int Width;
        public int Height;
        public int Channels;
        public int FrameIndex;
    }

    void Start()
    {
        _bytesPerPixel = textureFormat == TextureFormat.RGB24 ? 3 : 4;
        _dataSize = (long)imageWidth * imageHeight * _bytesPerPixel;
        InitTexture(imageWidth, imageHeight);
        OpenSharedMemory();
    }

    unsafe void Update()
    {
        if (!_ptrAcquired || _accessor == null)
        {
            TryReconnect();
            return;
        }

        // ── 1. Read header directly from pinned pointer (no allocation) ──────
        ImageHeader header = *(ImageHeader*)_mmfPtr;

        if (header.FrameIndex == _lastFrameIndex) return; // nothing new
        _lastFrameIndex = header.FrameIndex;

        // ── 2. Resize if needed ──────────────────────────────────────────────
        if (header.Width != _texture.width || header.Height != _texture.height)
        {
            _bytesPerPixel = header.Channels == 3 ? 3 : 4;
            textureFormat  = header.Channels == 3 ? TextureFormat.RGB24 : TextureFormat.RGBA32;
            _dataSize      = (long)header.Width * header.Height * _bytesPerPixel;
            InitTexture(header.Width, header.Height);
        }

        // ── 3. Zero-copy: write MMF pointer → texture native memory ──────────
        NativeArray<byte> texData = _texture.GetRawTextureData<byte>();

        UnsafeUtility.MemCpy(
            texData.GetUnsafePtr(),          // dst: texture's own memory
            _mmfPtr + HEADER_SIZE,           // src: shared mem (no intermediate buffer!)
            _dataSize
        );

        // ── 4. Upload to GPU ─────────────────────────────────────────────────
        _texture.Apply(false, false);        // false,false = no mipmaps, keep readable
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private void InitTexture(int w, int h)
    {
        if (_texture != null) Destroy(_texture);
        // makeNoLongerReadable:false keeps CPU-side buffer alive for GetRawTextureData
        _texture = new Texture2D(w, h, textureFormat, false, false);
        _texture.filterMode = FilterMode.Bilinear;

        if (targetRenderer != null)
            targetRenderer.material.mainTexture = _texture;
        else if (TryGetComponent<Renderer>(out var r))
            r.material.mainTexture = _texture;
    }

    private unsafe void OpenSharedMemory()
    {
        try
        {
            long totalSize = HEADER_SIZE + _dataSize;
            _mmf      = MemoryMappedFile.OpenExisting(sharedMemoryName);
            _accessor = _mmf.CreateViewAccessor(0, totalSize, MemoryMappedFileAccess.Read);

            // Acquire a stable raw pointer — no per-frame marshaling ever needed
            byte* ptr = null;
            _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            _mmfPtr     = ptr;
            _ptrAcquired = true;

            Debug.Log($"[SharedMemoryTexture] Opened '{sharedMemoryName}', ptr={((IntPtr)ptr).ToString("X")}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SharedMemoryTexture] Could not open: {e.Message}");
        }
    }

    private float _reconnectTimer;
    private void TryReconnect()
    {
        _reconnectTimer += Time.deltaTime;
        if (_reconnectTimer >= 2f) { _reconnectTimer = 0f; OpenSharedMemory(); }
    }

    private unsafe void CloseSharedMemory()
    {
        if (_ptrAcquired)
        {
            _accessor?.SafeMemoryMappedViewHandle.ReleasePointer();
            _ptrAcquired = false;
            _mmfPtr = null;
        }
        _accessor?.Dispose(); _accessor = null;
        _mmf?.Dispose();      _mmf = null;
    }

    void OnDestroy()
    {
        CloseSharedMemory();
        if (_texture != null) Destroy(_texture);
    }
}