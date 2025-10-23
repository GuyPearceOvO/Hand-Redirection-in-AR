using System;
using Leap;
using UnityEngine;
using Image = LeapInternal.Image;

/// <summary>
/// 基于 Ultraleap 骨架信息生成与 IR 帧对齐的遮罩纹理，并写入 MaskRigAdapter 的 RenderTexture。
/// </summary>
public sealed class UltraleapMaskRig : MonoBehaviour
{
    [Header("Ultraleap")]
    [SerializeField] private LeapServiceProvider m_serviceProvider;
    [SerializeField] private Image.CameraType m_camera = Image.CameraType.LEFT;

    [Header("Mask Output")]
    [SerializeField] private MaskRigAdapter m_maskRigAdapter;
    [SerializeField, Range(1, 32)] private int m_strokeRadius = 12;
    [SerializeField] private bool m_flipHorizontally = true;

    [Header("Diagnostics")]
    [SerializeField] private bool m_logDebug;

    private byte[] _maskBuffer;
    private Texture2D _cpuTexture;
    private Color32[] _pixelBuffer;

    private void Awake()
    {
        if (m_serviceProvider == null)
        {
            m_serviceProvider = FindObjectOfType<LeapServiceProvider>();
        }

        if (m_maskRigAdapter == null)
        {
            m_maskRigAdapter = GetComponentInChildren<MaskRigAdapter>();
        }
    }

    private void OnDisable()
    {
        ReleaseCpuResources();
    }

    private void LateUpdate()
    {
        var maskTexture = m_maskRigAdapter?.CurrentMaskTexture;
        if (maskTexture == null)
        {
            return;
        }

        int width = maskTexture.width;
        int height = maskTexture.height;
        if (width < 2 || height < 2)
        {
            return;
        }

        EnsureBuffers(width, height);

        var controller = m_serviceProvider?.GetLeapController();
        var frame = controller?.Frame();
        var device = m_serviceProvider?.CurrentDevice;

        bool wrote = LeapMaskUtility.GenerateSkeletonMask(
            controller,
            frame,
            device,
            m_camera,
            width,
            height,
            m_strokeRadius,
            m_flipHorizontally,
            _maskBuffer);

        if (!wrote)
        {
            Array.Clear(_maskBuffer, 0, _maskBuffer.Length);
        }

        WriteBufferToTexture(width, height, maskTexture);
    }

    private void EnsureBuffers(int width, int height)
    {
        int size = width * height;
        if (_maskBuffer == null || _maskBuffer.Length != size)
        {
            _maskBuffer = new byte[size];
        }

        if (_pixelBuffer == null || _pixelBuffer.Length != size)
        {
            _pixelBuffer = new Color32[size];
        }

        if (_cpuTexture != null && (_cpuTexture.width != width || _cpuTexture.height != height))
        {
            Destroy(_cpuTexture);
            _cpuTexture = null;
        }

        if (_cpuTexture == null)
        {
            _cpuTexture = new Texture2D(width, height, TextureFormat.RGBA32, false, true)
            {
                name = "UltraleapMaskRig_CPU",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
        }
    }

    private void WriteBufferToTexture(int width, int height, RenderTexture target)
    {
        for (int i = 0; i < _maskBuffer.Length; i++)
        {
            byte value = _maskBuffer[i];
            _pixelBuffer[i] = new Color32(0, 0, 0, value);
        }

        _cpuTexture.SetPixels32(_pixelBuffer);
        _cpuTexture.Apply(false, false);

        var previous = RenderTexture.active;
        try
        {
            RenderTexture.active = target;
            GL.Clear(true, true, Color.clear);
            Graphics.Blit(_cpuTexture, target);
        }
        finally
        {
            RenderTexture.active = previous;
        }
    }

    private void ReleaseCpuResources()
    {
        if (_cpuTexture != null)
        {
            Destroy(_cpuTexture);
            _cpuTexture = null;
        }

        _maskBuffer = null;
        _pixelBuffer = null;
    }
}
