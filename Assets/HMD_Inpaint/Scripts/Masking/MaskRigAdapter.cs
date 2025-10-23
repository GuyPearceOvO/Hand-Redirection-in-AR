using UnityEngine;

/// <summary>
/// 参考 haptic-retargeting-toolkit，将指定相机复制为遮罩相机并输出 RenderTexture。
/// </summary>
public sealed class MaskRigAdapter : MonoBehaviour
{
    [Header("Cameras")]
    [SerializeField] private Camera m_baseCamera;
    [SerializeField] private SourceCamera m_maskSourceCamera;

    [Header("Mask Setup")]
    [SerializeField] private string m_maskLayerName = "Mask";
    [SerializeField] private int m_targetWidth = 640;
    [SerializeField] private int m_targetHeight = 480;
    [SerializeField] private bool m_matchBaseCameraProperties = true;

    [Header("Diagnostics")]
    [SerializeField] private bool m_logDebug;

    private RenderTexture _maskTexture;
    private int _maskLayer;

    public RenderTexture CurrentMaskTexture => _maskTexture;

    private void Reset()
    {
        m_baseCamera = Camera.main;
        m_maskSourceCamera = GetComponent<SourceCamera>();
    }

    private void OnEnable()
    {
        if (m_maskSourceCamera == null)
        {
            m_maskSourceCamera = GetComponent<SourceCamera>();
        }

        EnsureConfigured();
    }

    private void OnDisable()
    {
        ReleaseMaskTexture();
    }

    private void OnDestroy()
    {
        ReleaseMaskTexture();
    }

    public void EnsureConfigured()
    {
        if (!ResolveDependencies())
        {
            return;
        }

        ConfigureMaskCamera();
        EnsureRenderTexture();
        if (m_maskSourceCamera != null && _maskTexture != null)
        {
            var maskCamera = m_maskSourceCamera.Camera;
            if (maskCamera != null)
            {
                maskCamera.targetTexture = _maskTexture;
            }
        }
    }

    public void SetTargetSize(int width, int height)
    {
        m_targetWidth = Mathf.Max(2, width);
        m_targetHeight = Mathf.Max(2, height);
        EnsureConfigured();
    }

    private bool ResolveDependencies()
    {
        if (m_baseCamera == null)
        {
            m_baseCamera = Camera.main;
        }

        if (m_baseCamera == null)
        {
            if (m_logDebug)
            {
                Debug.LogWarning($"{nameof(MaskRigAdapter)}: 未找到 Base Camera。");
            }
            return false;
        }

        if (m_maskSourceCamera == null)
        {
            m_maskSourceCamera = GetComponent<SourceCamera>();
        }

        if (m_maskSourceCamera == null)
        {
            if (m_logDebug)
            {
                Debug.LogWarning($"{nameof(MaskRigAdapter)}: 未找到 SourceCamera 组件。");
            }
            return false;
        }

        _maskLayer = LayerMask.NameToLayer(m_maskLayerName);
        if (_maskLayer < 0)
        {
            if (m_logDebug)
            {
                Debug.LogWarning($"{nameof(MaskRigAdapter)}: 层 \"{m_maskLayerName}\" 未创建。");
            }
            return false;
        }

        return true;
    }

    private void ConfigureMaskCamera()
    {
        var maskCamera = m_maskSourceCamera.Camera;
        if (maskCamera == null)
        {
            return;
        }

        if (m_matchBaseCameraProperties)
        {
            maskCamera.CopyFrom(m_baseCamera);
        }

        maskCamera.clearFlags = CameraClearFlags.SolidColor;
        maskCamera.backgroundColor = Color.black;
        maskCamera.cullingMask = 1 << _maskLayer;
        maskCamera.stereoTargetEye = StereoTargetEyeMask.None;
        maskCamera.depth = m_baseCamera.depth - 3f;
        if (maskCamera.enabled)
        {
            maskCamera.enabled = false;
        }
    }

    private void EnsureRenderTexture()
    {
        int width = Mathf.Max(2, m_targetWidth);
        int height = Mathf.Max(2, m_targetHeight);

        if (_maskTexture != null && (_maskTexture.width != width || _maskTexture.height != height))
        {
            ReleaseMaskTexture();
        }

        if (_maskTexture == null)
        {
            _maskTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
            {
                name = "MaskRigAdapter_RT",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point,
                useMipMap = false,
                autoGenerateMips = false,
                antiAliasing = 1
            };
            _maskTexture.Create();

            if (m_logDebug)
            {
                Debug.Log($"{nameof(MaskRigAdapter)}: 创建遮罩 RenderTexture {width}x{height}");
            }
        }
    }

    private void ReleaseMaskTexture()
    {
        if (_maskTexture != null)
        {
            if (m_maskSourceCamera != null)
            {
                var maskCamera = m_maskSourceCamera.Camera;
                if (maskCamera != null && maskCamera.targetTexture == _maskTexture)
                {
                    maskCamera.targetTexture = null;
                }
            }

            _maskTexture.Release();
            Destroy(_maskTexture);
            _maskTexture = null;
        }
    }
}
