using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using PassthroughCameraSamples;
using UnityEngine;
using UnityEngine.Scripting;

[Preserve]
public class PassthroughFrameSender : MonoBehaviour
{
    [Header("Passthrough Source")]
    [SerializeField] private WebCamTextureManager m_webCamTextureManager;
    [SerializeField, Range(0.05f, 1f)] private float m_sendIntervalSeconds = 0.1f;

    [Header("Networking")]
    [SerializeField] private string m_host = "127.0.0.1";
    [SerializeField] private int m_port = 5566;
    [SerializeField] private bool m_waitForResponse = true;

    [Header("Encoding")]
    [SerializeField, Range(1, 100)] private int m_jpegQuality = 80;

    [Header("Diagnostics")]
    [SerializeField] private bool m_logDebug;

    private TcpClient _client;
    private NetworkStream _stream;
    private Texture2D _scratchTexture;
    private Color32[] _pixelBuffer;
    private Coroutine _sendCoroutine;
    private PassthroughFrameReceiver _receiver;

    private const int HeaderSize = 4;
    private const int RequestHeaderSize = 8;

    private void Awake()
    {
        Debug.Log($"PassthroughFrameSender: Awake() called, LogDebug={m_logDebug}");

        if (m_webCamTextureManager == null)
        {
#if UNITY_2023_1_OR_NEWER
            m_webCamTextureManager = FindFirstObjectByType<WebCamTextureManager>();
#else
            m_webCamTextureManager = FindObjectOfType<WebCamTextureManager>();
#endif
        }

        _receiver = GetComponent<PassthroughFrameReceiver>();

        if (m_logDebug)
        {
            Debug.Log($"PassthroughFrameSender: WebCamTextureManager={m_webCamTextureManager != null}, Receiver={_receiver != null}");
            Debug.Log($"PassthroughFrameSender: Target={m_host}:{m_port}, Interval={m_sendIntervalSeconds}s");
        }
    }

    private void OnEnable()
    {
        Debug.Log("PassthroughFrameSender: OnEnable() called");
        if (_sendCoroutine == null)
        {
            _sendCoroutine = StartCoroutine(SendLoop());
            if (m_logDebug)
            {
                Debug.Log("PassthroughFrameSender: SendLoop started");
            }
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
        DisposeScratchTexture();
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

            if (!TryCaptureFrame(out var frameData))
            {
                yield return null;
                continue;
            }

            var sendTask = WriteFrameAsync(frameData);
            while (!sendTask.IsCompleted)
            {
                yield return null;
            }

            if (sendTask.IsFaulted)
            {
                if (m_logDebug)
                {
                    Debug.LogWarning($"PassthroughFrameSender send failed: {sendTask.Exception?.GetBaseException().Message}");
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
                        Debug.LogWarning($"PassthroughFrameSender receive failed: {readTask.Exception?.GetBaseException().Message}");
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

    private bool TryCaptureFrame(out byte[] frameBytes)
    {
        frameBytes = null;
        if (m_webCamTextureManager == null)
        {
            if (m_logDebug)
            {
                Debug.LogWarning("PassthroughFrameSender: WebCamTextureManager missing");
            }
            return false;
        }

        var webCamTexture = m_webCamTextureManager.WebCamTexture;
        if (webCamTexture == null)
        {
            if (m_logDebug)
            {
                Debug.LogWarning("PassthroughFrameSender: WebCamTexture is null");
            }
            return false;
        }

        if (webCamTexture.width <= 16 || webCamTexture.height <= 16)
        {
            if (m_logDebug)
            {
                Debug.LogWarning($"PassthroughFrameSender: WebCamTexture resolution too small: {webCamTexture.width}x{webCamTexture.height}");
            }
            return false;
        }

        if (_scratchTexture == null || _scratchTexture.width != webCamTexture.width || _scratchTexture.height != webCamTexture.height)
        {
            DisposeScratchTexture();
            _scratchTexture = new Texture2D(webCamTexture.width, webCamTexture.height, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp
            };
        }

        _pixelBuffer ??= new Color32[webCamTexture.width * webCamTexture.height];
        webCamTexture.GetPixels32(_pixelBuffer);
        _scratchTexture.SetPixels32(_pixelBuffer);
        _scratchTexture.Apply(false);

        frameBytes = ImageConversion.EncodeToJPG(_scratchTexture, m_jpegQuality);
        return frameBytes != null && frameBytes.Length > 0;
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
                Debug.LogWarning($"PassthroughFrameSender: failed to connect to {m_host}:{m_port} ({reason})");
            }
            CloseConnection();
            yield break;
        }

        _stream = _client.GetStream();
        if (m_logDebug)
        {
            Debug.Log($"PassthroughFrameSender: connected to {m_host}:{m_port}");
        }
    }

    private async Task WriteFrameAsync(byte[] frame)
    {
        if (!IsStreamReady())
        {
            throw new IOException("Stream not ready");
        }

        var header = new byte[RequestHeaderSize];
        Buffer.BlockCopy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(frame.Length)), 0, header, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(0)), 0, header, 4, 4);

        await _stream.WriteAsync(header, 0, header.Length).ConfigureAwait(false);
        await _stream.WriteAsync(frame, 0, frame.Length).ConfigureAwait(false);
        await _stream.FlushAsync().ConfigureAwait(false);
    }

    private async Task<byte[]> ReadFrameAsync()
    {
        var header = await ReadExactAsync(HeaderSize).ConfigureAwait(false);
        if (header == null || header.Length != HeaderSize)
        {
            return null;
        }

        var length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(header, 0));
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
        var offset = 0;
        while (offset < length)
        {
            var read = await _stream.ReadAsync(buffer, offset, length - offset).ConfigureAwait(false);
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

    private void DisposeScratchTexture()
    {
        if (_scratchTexture != null)
        {
            Destroy(_scratchTexture);
            _scratchTexture = null;
        }
    }
}
