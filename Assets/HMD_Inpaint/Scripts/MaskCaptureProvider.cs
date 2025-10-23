using System;
using UnityEngine;

/// <summary>
/// Captures a mask render texture (e.g., from a dedicated mask camera) and exposes it as a CPU byte array.
/// The mask texture should encode opaque pixels (alpha = 1) wherever the real-world object exists.
/// </summary>
public class MaskCaptureProvider : MonoBehaviour
{
    [Header("Mask Source")]
    [SerializeField] private Camera m_maskCamera;
    [SerializeField] private MaskRigAdapter m_maskRigAdapter;
    [SerializeField] private RenderTexture m_explicitMaskTexture;
    [SerializeField] private int m_overrideWidth = 640;
    [SerializeField] private int m_overrideHeight = 480;
    [SerializeField, Range(0f, 1f)] private float m_alphaThreshold = 0.1f;
    [SerializeField] private bool m_logDebug;

    private RenderTexture _maskTexture;
    private Texture2D _cpuTexture;
    private byte[] _maskBuffer;

    public RenderTexture CurrentMaskTexture => _maskTexture;

    private void Awake()
    {
        EnsureRenderTarget();
    }

    private void OnEnable()
    {
        EnsureRenderTarget();
    }

    private void OnDisable()
    {
        ReleaseResources();
    }

    private void OnDestroy()
    {
        ReleaseResources();
    }

    /// <summary>
    /// Attempts to grab the latest mask pixels as a tightly packed 8-bit array (row-major, 0 or 255).
    /// </summary>
    public bool TryCaptureMask(out byte[] maskBytes, out int width, out int height)
    {
        maskBytes = null;
        width = 0;
        height = 0;

        if (!EnsureRenderTarget())
        {
            return false;
        }

        width = _maskTexture.width;
        height = _maskTexture.height;

        EnsureCpuTexture(width, height);
        EnsureMaskBuffer(width, height);

        var previous = RenderTexture.active;
        RenderTexture.active = _maskTexture;
        _cpuTexture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
        _cpuTexture.Apply(false, false);
        RenderTexture.active = previous;

        var pixels = _cpuTexture.GetPixels32();
        byte threshold = (byte)Mathf.Clamp(Mathf.RoundToInt(m_alphaThreshold * 255f), 0, 255);
        for (int i = 0; i < pixels.Length; i++)
        {
            _maskBuffer[i] = pixels[i].a >= threshold ? (byte)255 : (byte)0;
        }

        maskBytes = new byte[_maskBuffer.Length];
        Buffer.BlockCopy(_maskBuffer, 0, maskBytes, 0, _maskBuffer.Length);

        return true;
    }

    private bool EnsureRenderTarget()
    {
        if (m_explicitMaskTexture != null)
        {
            if (_maskTexture != m_explicitMaskTexture)
            {
                ReleaseResources();
                _maskTexture = m_explicitMaskTexture;
            }
            return true;
        }

        if (m_maskRigAdapter != null)
        {
            int targetWidth = m_overrideWidth > 0 ? m_overrideWidth : 0;
            int targetHeight = m_overrideHeight > 0 ? m_overrideHeight : 0;
            if (targetWidth > 0 && targetHeight > 0)
            {
                m_maskRigAdapter.SetTargetSize(targetWidth, targetHeight);
            }

            m_maskRigAdapter.EnsureConfigured();
            var rigTexture = m_maskRigAdapter.CurrentMaskTexture;
            if (rigTexture == null)
            {
                if (m_logDebug)
                {
                    Debug.LogWarning($"{nameof(MaskCaptureProvider)}: MaskRigAdapter 未提供有效 RenderTexture。");
                }
                return false;
            }

            if (_maskTexture != rigTexture)
            {
                ReleaseResources();
                _maskTexture = rigTexture;
            }

            return true;
        }

        if (m_maskCamera == null)
        {
            if (m_logDebug)
            {
                Debug.LogWarning($"{nameof(MaskCaptureProvider)}: Mask camera not assigned.");
            }
            return false;
        }

        int width = Mathf.Max(2, m_overrideWidth > 0 ? m_overrideWidth : m_maskCamera.pixelWidth);
        int height = Mathf.Max(2, m_overrideHeight > 0 ? m_overrideHeight : m_maskCamera.pixelHeight);

        if (_maskTexture != null && (_maskTexture.width != width || _maskTexture.height != height))
        {
            ReleaseRenderTexture();
        }

        if (_maskTexture == null)
        {
            _maskTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
            {
                name = "MaskCaptureProvider_RT",
                antiAliasing = 1,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point,
                autoGenerateMips = false,
                useMipMap = false
            };
            _maskTexture.Create();
            if (m_logDebug)
            {
                Debug.Log($"{nameof(MaskCaptureProvider)}: Created mask RenderTexture {width}x{height}");
            }
        }

        if (m_maskCamera.targetTexture != _maskTexture)
        {
            m_maskCamera.targetTexture = _maskTexture;
        }

        return true;
    }

    private void EnsureCpuTexture(int width, int height)
    {
        if (_cpuTexture != null && (_cpuTexture.width != width || _cpuTexture.height != height))
        {
            Destroy(_cpuTexture);
            _cpuTexture = null;
        }

        if (_cpuTexture == null)
        {
            _cpuTexture = new Texture2D(width, height, TextureFormat.RGBA32, false, false)
            {
                name = "MaskCaptureProvider_CPU"
            };
        }
    }

    private void EnsureMaskBuffer(int width, int height)
    {
        int size = width * height;
        if (_maskBuffer == null || _maskBuffer.Length != size)
        {
            _maskBuffer = new byte[size];
        }
    }

    private void ReleaseResources()
    {
        ReleaseRenderTexture();
        if (_cpuTexture != null)
        {
            Destroy(_cpuTexture);
            _cpuTexture = null;
        }
        _maskBuffer = null;
    }

    private void ReleaseRenderTexture()
    {
        if (_maskTexture != null && _maskTexture != m_explicitMaskTexture && (m_maskRigAdapter == null || _maskTexture != m_maskRigAdapter.CurrentMaskTexture))
        {
            if (m_maskCamera != null && m_maskCamera.targetTexture == _maskTexture)
            {
                m_maskCamera.targetTexture = null;
            }
            _maskTexture.Release();
            Destroy(_maskTexture);
        }
        _maskTexture = null;
    }
}
