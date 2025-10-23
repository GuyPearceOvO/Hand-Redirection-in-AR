using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Leap;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Scripting;
using Image = LeapInternal.Image;

/// <summary>
/// Captures Ultraleap IR frames, sends them (optionally with a skeleton-derived mask) to a local
/// inpainting server, and forwards the processed image back to PassthroughFrameReceiver.
/// </summary>
[Preserve]
public class UltraleapFrameSender : MonoBehaviour
{
    [Header("Ultraleap Source")]
    [SerializeField] private LeapImageRetriever m_imageRetriever;
    [SerializeField] private LeapServiceProvider m_serviceProvider;
    [SerializeField] private EyeSelection m_eye = EyeSelection.Left;
    [SerializeField, Range(0.01f, 0.5f)] private float m_sendIntervalSeconds = 1f / 30f;
    [SerializeField, Range(1, 32)] private int m_maskStrokeRadius = 8;
    [SerializeField] private MaskCaptureProvider m_geometryMaskProvider;
    [SerializeField] private bool m_includeSkeletonMask = true;
    [SerializeField] private bool m_flipMaskHorizontally = true;

    [Header("Networking")]
    [SerializeField] private string m_host = "127.0.0.1";
    [SerializeField] private int m_port = 5566;
    [SerializeField] private bool m_waitForResponse = true;

    [Header("Encoding")]
    [SerializeField, Range(1, 100)] private int m_jpegQuality = 80;

    [Header("Diagnostics")]
    [SerializeField] private bool m_logDebug;

    private enum EyeSelection
    {
        Left,
        Right
    }

    private const int RequestHeaderSize = 8;   // [imageLength(int)][maskLength(int)]
    private const int ResponseHeaderSize = 4;  // [imageLength(int)]

    private TcpClient _client;
    private NetworkStream _stream;
    private Coroutine _sendCoroutine;
    private PassthroughFrameReceiver _receiver;

    private Texture2D _scratchTexture;
    private Color32[] _rgbBuffer;
    private byte[] _rawBuffer;
    private byte[] _maskBuffer;

    private void Awake()
    {
        if (m_imageRetriever == null)
        {
#if UNITY_2023_1_OR_NEWER
            m_imageRetriever = FindFirstObjectByType<LeapImageRetriever>();
#else
            m_imageRetriever = FindObjectOfType<LeapImageRetriever>();
#endif
        }

        if (m_serviceProvider == null)
        {
#if UNITY_2023_1_OR_NEWER
            m_serviceProvider = FindFirstObjectByType<LeapServiceProvider>();
#else
            m_serviceProvider = FindObjectOfType<LeapServiceProvider>();
#endif
        }

        _receiver = GetComponent<PassthroughFrameReceiver>();

        if (m_logDebug)
        {
            Debug.Log($"UltraleapFrameSender: Retriever={(m_imageRetriever != null)}, Provider={(m_serviceProvider != null)}, Receiver={( _receiver != null)}");
        }
    }

    private void OnEnable()
    {
        if (_sendCoroutine == null)
        {
            _sendCoroutine = StartCoroutine(SendLoop());
        }
    }

    private void OnDisable()
    {
        if (_sendCoroutine != null)
        {
            StopCoroutine(_sendCoroutine);
            _sendCoroutine = null;
        }

        CloseConnection();
        DisposeScratchResources();
    }

    private IEnumerator SendLoop()
    {
        var wait = new WaitForSeconds(m_sendIntervalSeconds);

        while (enabled)
        {
            if (!IsStreamReady())
            {
                yield return ConnectRoutine();
                if (!IsStreamReady())
                {
                    yield return wait;
                    continue;
                }
            }

            if (!TryCaptureFrame(out var frameBytes, out var maskBytes))
            {
                yield return null;
                continue;
            }

            var sendTask = WriteFrameAsync(frameBytes, maskBytes);
            while (!sendTask.IsCompleted)
            {
                yield return null;
            }

            if (sendTask.IsFaulted)
            {
                if (m_logDebug)
                {
                    Debug.LogWarning($"UltraleapFrameSender: send failed - {sendTask.Exception?.GetBaseException().Message}");
                }
                CloseConnection();
                yield return wait;
                continue;
            }

            if (m_waitForResponse && _receiver != null && IsStreamReady())
            {
                var readTask = ReadFrameAsync();
                while (!readTask.IsCompleted)
                {
                    yield return null;
                }

                if (readTask.IsFaulted)
                {
                    if (m_logDebug)
                    {
                        Debug.LogWarning($"UltraleapFrameSender: receive failed - {readTask.Exception?.GetBaseException().Message}");
                    }
                    CloseConnection();
                }
                else
                {
                    var result = readTask.Result;
                    if (result != null && result.Length > 0)
                    {
                        _receiver.QueueFrame(result);
                    }
                }
            }

            yield return wait;
        }
    }

    private bool TryCaptureFrame(out byte[] frameBytes, out byte[] maskBytes)
    {
        frameBytes = null;
        maskBytes = null;

        if (m_imageRetriever == null)
        {
            return false;
        }

        var eyeData = m_imageRetriever.TextureData;
        var textureData = eyeData?.TextureData;
        var combinedTexture = textureData?.CombinedTexture;
        if (combinedTexture == null)
        {
            return false;
        }

        int combinedWidth = combinedTexture.width;
        int combinedHeight = combinedTexture.height;
        if (combinedWidth <= 16 || combinedHeight <= 16 || (combinedHeight % 2) != 0)
        {
            return false;
        }

        int singleHeight = combinedHeight / 2;
        int pixelCount = combinedWidth * singleHeight;

        NativeArray<byte> raw = combinedTexture.GetRawTextureData<byte>();
        if (!raw.IsCreated || raw.Length < pixelCount * 2)
        {
            return false;
        }

        EnsureScratchResources(combinedWidth, singleHeight, pixelCount);
        raw.CopyTo(_rawBuffer);

        int offset = m_eye == EyeSelection.Right ? pixelCount : 0;
        for (int y = 0; y < singleHeight; y++)
        {
            int srcRow = singleHeight - 1 - y;
            int srcOffset = offset + srcRow * combinedWidth;
            int dstOffset = y * combinedWidth;
            for (int x = 0; x < combinedWidth; x++)
            {
                byte value = _rawBuffer[srcOffset + x];
                _rgbBuffer[dstOffset + x] = new Color32(value, value, value, 255);
            }
        }

        _scratchTexture.SetPixels32(_rgbBuffer);
        _scratchTexture.Apply(false, false);

        frameBytes = ImageConversion.EncodeToJPG(_scratchTexture, m_jpegQuality);
        if (frameBytes == null || frameBytes.Length == 0)
        {
            return false;
        }

        byte[] skeletonMask = null;
        if (m_includeSkeletonMask)
        {
            var controller = m_serviceProvider?.GetLeapController();
            var frame = controller?.Frame();
            var device = m_serviceProvider?.CurrentDevice;
            var camera = m_eye == EyeSelection.Right ? Image.CameraType.RIGHT : Image.CameraType.LEFT;
            if (controller != null && frame != null &&
                TryGenerateSkeletonMask(controller, frame, device, camera, combinedWidth, singleHeight, out var generatedMask))
            {
                skeletonMask = generatedMask;
            }
        }

        byte[] geometryMask = null;
        if (m_geometryMaskProvider != null && m_geometryMaskProvider.TryCaptureMask(out var capturedMask, out int maskWidth, out int maskHeight))
        {
            if (capturedMask != null && capturedMask.Length == maskWidth * maskHeight)
            {
                if (maskWidth != combinedWidth || maskHeight != singleHeight)
                {
                    geometryMask = ResizeMask(capturedMask, maskWidth, maskHeight, combinedWidth, singleHeight);
                }
                else
                {
                    geometryMask = capturedMask;
                }
            }
        }

        maskBytes = MergeMasks(skeletonMask, geometryMask);

        return true;
    }

    private static byte[] MergeMasks(byte[] skeletonMask, byte[] geometryMask)
    {
        if ((skeletonMask == null || skeletonMask.Length == 0) && (geometryMask == null || geometryMask.Length == 0))
        {
            return null;
        }

        if (skeletonMask == null || skeletonMask.Length == 0)
        {
            var copy = new byte[geometryMask.Length];
            Buffer.BlockCopy(geometryMask, 0, copy, 0, geometryMask.Length);
            return copy;
        }

        if (geometryMask == null || geometryMask.Length == 0)
        {
            var copy = new byte[skeletonMask.Length];
            Buffer.BlockCopy(skeletonMask, 0, copy, 0, skeletonMask.Length);
            return copy;
        }

        if (skeletonMask.Length != geometryMask.Length)
        {
            // Fallback to whichever mask is larger; they should match if both come from the same texture size.
            var size = Math.Min(skeletonMask.Length, geometryMask.Length);
            var merged = new byte[size];
            for (int i = 0; i < size; i++)
            {
                merged[i] = (skeletonMask[i] != 0 || geometryMask[i] != 0) ? (byte)255 : (byte)0;
            }
            return merged;
        }

        var result = new byte[skeletonMask.Length];
        for (int i = 0; i < skeletonMask.Length; i++)
        {
            result[i] = (skeletonMask[i] != 0 || geometryMask[i] != 0) ? (byte)255 : (byte)0;
        }

        return result;
    }

    private static byte[] ResizeMask(byte[] source, int srcWidth, int srcHeight, int dstWidth, int dstHeight)
    {
        var resized = new byte[dstWidth * dstHeight];
        for (int y = 0; y < dstHeight; y++)
        {
            float srcY = (y + 0.5f) / dstHeight * srcHeight - 0.5f;
            int sy = Mathf.Clamp(Mathf.RoundToInt(srcY), 0, srcHeight - 1);
            for (int x = 0; x < dstWidth; x++)
            {
                float srcX = (x + 0.5f) / dstWidth * srcWidth - 0.5f;
                int sx = Mathf.Clamp(Mathf.RoundToInt(srcX), 0, srcWidth - 1);
                resized[y * dstWidth + x] = source[sy * srcWidth + sx] >= 128 ? (byte)255 : (byte)0;
            }
        }

        return resized;
    }

    private bool TryGenerateSkeletonMask(Controller controller, Frame frame, Device device, Image.CameraType camera, int width, int height, out byte[] maskBytes)
    {
        maskBytes = null;
        int size = width * height;
        EnsureMaskBuffer(size);

        bool wrote = LeapMaskUtility.GenerateSkeletonMask(
            controller,
            frame,
            device,
            camera,
            width,
            height,
            m_maskStrokeRadius,
            m_flipMaskHorizontally,
            _maskBuffer);

        if (!wrote)
        {
            return false;
        }

        maskBytes = new byte[size];
        Buffer.BlockCopy(_maskBuffer, 0, maskBytes, 0, size);
        return true;
    }

    private void EnsureScratchResources(int width, int height, int pixelCount)
    {
        if (_scratchTexture == null || _scratchTexture.width != width || _scratchTexture.height != height)
        {
            DisposeScratchResources();
            _scratchTexture = new Texture2D(width, height, TextureFormat.RGB24, false, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            _rgbBuffer = new Color32[pixelCount];
        }

        if (_rawBuffer == null || _rawBuffer.Length != pixelCount * 2)
        {
            _rawBuffer = new byte[pixelCount * 2];
        }
    }

    private void EnsureMaskBuffer(int size)
    {
        if (_maskBuffer == null || _maskBuffer.Length != size)
        {
            _maskBuffer = new byte[size];
        }
    }

    private IEnumerator ConnectRoutine()
    {
        CloseConnection();

        _client = new TcpClient();
        var connectTask = _client.ConnectAsync(m_host, m_port);
        while (!connectTask.IsCompleted)
        {
            yield return null;
        }

        if (connectTask.IsFaulted || !_client.Connected)
        {
            if (m_logDebug)
            {
                var reason = connectTask.Exception?.GetBaseException().Message ?? "unknown";
                Debug.LogWarning($"UltraleapFrameSender: failed to connect {m_host}:{m_port} ({reason})");
            }
            CloseConnection();
            yield break;
        }

        _stream = _client.GetStream();
        if (m_logDebug)
        {
            Debug.Log($"UltraleapFrameSender: connected to {m_host}:{m_port}");
        }
    }

    private async Task WriteFrameAsync(byte[] frame, byte[] mask)
    {
        if (!IsStreamReady())
        {
            throw new IOException("Stream not ready");
        }

        int maskLength = mask?.Length ?? 0;
        var header = new byte[RequestHeaderSize];
        Buffer.BlockCopy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(frame.Length)), 0, header, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(maskLength)), 0, header, 4, 4);

        await _stream.WriteAsync(header, 0, header.Length).ConfigureAwait(false);
        await _stream.WriteAsync(frame, 0, frame.Length).ConfigureAwait(false);
        if (maskLength > 0)
        {
            await _stream.WriteAsync(mask, 0, maskLength).ConfigureAwait(false);
        }
        await _stream.FlushAsync().ConfigureAwait(false);
    }

    private async Task<byte[]> ReadFrameAsync()
    {
        var header = await ReadExactAsync(ResponseHeaderSize).ConfigureAwait(false);
        if (header == null || header.Length != ResponseHeaderSize)
        {
            return null;
        }

        int length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(header, 0));
        if (length <= 0)
        {
            return null;
        }

        return await ReadExactAsync(length).ConfigureAwait(false);
    }

    private async Task<byte[]> ReadExactAsync(int length)
    {
        if (!IsStreamReady())
        {
            throw new IOException("Stream not ready");
        }

        var buffer = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int read = await _stream.ReadAsync(buffer, offset, length - offset).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Remote endpoint closed the connection");
            }
            offset += read;
        }
        return buffer;
    }

    private bool IsStreamReady()
    {
        return _client != null && _client.Connected && _stream != null;
    }

    private void CloseConnection()
    {
        if (_stream != null)
        {
            _stream.Dispose();
            _stream = null;
        }

        if (_client != null)
        {
            _client.Close();
            _client = null;
        }
    }

    private void DisposeScratchResources()
    {
        if (_scratchTexture != null)
        {
            Destroy(_scratchTexture);
            _scratchTexture = null;
        }
        _rgbBuffer = null;
        _rawBuffer = null;
        _maskBuffer = null;
    }

}
