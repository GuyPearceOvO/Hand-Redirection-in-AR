using System;
using UnityEngine;
using UnityEngine.Scripting;
using UnityEngine.UI;

[Preserve]
public class PassthroughFrameReceiver : MonoBehaviour
{
    [Header("Output Targets")]
    [SerializeField] private Renderer m_targetRenderer;
    [SerializeField] private string m_textureProperty = "_MainTex";
    [SerializeField] private RawImage m_targetImage;
    [SerializeField] private bool m_autoCreateDebugImage = true;

    [Header("Diagnostics")]
    [SerializeField] private bool m_logDebug;

    public event Action<Texture> FrameApplied;

    private readonly object _lock = new object();
    private MaterialPropertyBlock _propertyBlock;
    private Texture2D _outputTexture;
    private byte[] _pendingFrame;
    private bool _hasNewFrame;

    public Texture2D CurrentTexture => _outputTexture;

    private void Awake()
    {
        Debug.Log($"PassthroughFrameReceiver: Awake() called, LogDebug={m_logDebug}");
        _propertyBlock ??= new MaterialPropertyBlock();

        if (m_autoCreateDebugImage)
        {
            EnsureOutputTargets();
        }

        if (m_logDebug)
        {
            Debug.Log($"PassthroughFrameReceiver: TargetRenderer={m_targetRenderer != null}, TargetImage={m_targetImage != null}");
        }
    }

    public void QueueFrame(byte[] data)
    {
        if (data == null || data.Length == 0)
        {
            if (m_logDebug)
            {
                Debug.LogWarning("PassthroughFrameReceiver: QueueFrame called with null/empty data");
            }
            return;
        }

        lock (_lock)
        {
            _pendingFrame = data;
            _hasNewFrame = true;
        }

        if (m_logDebug)
        {
            Debug.Log($"PassthroughFrameReceiver: Frame queued, size={data.Length} bytes");
        }
    }

    private void Update()
    {
        if (!_hasNewFrame)
        {
            return;
        }

        byte[] frame;
        lock (_lock)
        {
            if (!_hasNewFrame)
            {
                return;
            }

            frame = _pendingFrame;
            _pendingFrame = null;
            _hasNewFrame = false;
        }

        if (frame == null || frame.Length == 0)
        {
            return;
        }

        try
        {
            EnsureOutputTexture();
            if (!_outputTexture.LoadImage(frame, false))
            {
                if (m_logDebug)
                {
                    Debug.LogWarning("PassthroughFrameReceiver: failed to decode frame");
                }
                return;
            }

            ApplyTexture(_outputTexture);
        }
        catch (Exception ex)
        {
            if (m_logDebug)
            {
                Debug.LogWarning($"PassthroughFrameReceiver: exception while applying frame - {ex.Message}");
            }
        }
    }

    private void EnsureOutputTargets()
    {
        if (m_targetRenderer != null || m_targetImage != null)
        {
            return;
        }

        const float canvasDistance = 1.0f;
        var canvasGo = new GameObject("InpaintDebugCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;
        var rectTransform = canvas.GetComponent<RectTransform>();
        rectTransform.sizeDelta = new Vector2(0.6f, 0.35f);
        rectTransform.localScale = Vector3.one * 0.0015f;

        Transform anchor = Camera.main != null ? Camera.main.transform : transform;
        canvasGo.transform.SetPositionAndRotation(anchor.position + anchor.forward * canvasDistance, Quaternion.LookRotation(anchor.forward, Vector3.up));

        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);

        var imageGo = new GameObject("InpaintDebugRawImage", typeof(RectTransform), typeof(RawImage));
        imageGo.transform.SetParent(canvasGo.transform, false);
        var imageRect = imageGo.GetComponent<RectTransform>();
        imageRect.anchorMin = new Vector2(0.5f, 0.5f);
        imageRect.anchorMax = new Vector2(0.5f, 0.5f);
        imageRect.sizeDelta = new Vector2(1920, 1080);
        imageRect.localScale = Vector3.one;

        m_targetImage = imageGo.GetComponent<RawImage>();
    }

    private void EnsureOutputTexture()
    {
        if (_outputTexture != null)
        {
            return;
        }

        _outputTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
    }

    private void ApplyTexture(Texture texture)
    {
        if (m_targetRenderer != null)
        {
            _propertyBlock ??= new MaterialPropertyBlock();
            m_targetRenderer.GetPropertyBlock(_propertyBlock);
            _propertyBlock.SetTexture(m_textureProperty, texture);
            m_targetRenderer.SetPropertyBlock(_propertyBlock);
        }

        if (m_targetImage != null)
        {
            m_targetImage.texture = texture;
        }

        FrameApplied?.Invoke(texture);
    }

    private void OnDestroy()
    {
        if (_outputTexture != null)
        {
            Destroy(_outputTexture);
            _outputTexture = null;
        }
    }
}
