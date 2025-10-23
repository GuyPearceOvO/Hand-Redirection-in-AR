using System;
using UnityEngine;

/// <summary>
/// Wraps Unity Camera.OnRenderImage so other components可以订阅渲染回调。
/// 这是从 haptic-retargeting-toolkit 精简出来的版本。
/// </summary>
[RequireComponent(typeof(Camera))]
public sealed class SourceCamera : MonoBehaviour
{
    private Camera _camera;

    /// <summary>供外部访问的底层摄像机。</summary>
    public Camera Camera => _camera;

    /// <summary>在 OnRenderImage 阶段回调，参数为源纹理和目标纹理。</summary>
    public Action<RenderTexture, RenderTexture> OnRenderImageEvent;

    private void Awake()
    {
        _camera = GetComponent<Camera>();
    }

    private void OnRenderImage(RenderTexture src, RenderTexture dest)
    {
        Graphics.Blit(src, dest);
        OnRenderImageEvent?.Invoke(src, dest);
    }
}
