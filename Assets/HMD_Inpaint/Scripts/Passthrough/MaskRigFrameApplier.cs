using System;
using HRTK.MaskedRetargeting;
using UnityEngine;

/// <summary>
/// Bridges PassthroughFrameReceiver output into the MaskRig render texture that feeds the HRTK quads.
/// Attach this to the same GameObject that owns PassthroughFrameReceiver / UltraleapFrameSender.
/// </summary>
[RequireComponent(typeof(PassthroughFrameReceiver))]
public class MaskRigFrameApplier : MonoBehaviour
{
    [Header("Masked Retargeting")]
    [SerializeField] private MaskRig m_targetMaskRig;
    [SerializeField] private bool m_blitInLateUpdate;

    [Header("Diagnostics")]
    [SerializeField] private bool m_logDebug;

    private PassthroughFrameReceiver _receiver;
    private Texture _pendingTexture;
    private bool _hasPendingFrame;
    private int _outputLayer = -1;

    private void Awake()
    {
        _receiver = GetComponent<PassthroughFrameReceiver>();
    }

    private void OnEnable()
    {
        if (_receiver != null)
        {
            _receiver.FrameApplied += HandleFrameApplied;
        }

        CacheOutputLayer();
    }

    private void OnDisable()
    {
        if (_receiver != null)
        {
            _receiver.FrameApplied -= HandleFrameApplied;
        }

        _pendingTexture = null;
        _hasPendingFrame = false;
        _layerEnsuredForCurrentRig = false;
    }

    private void LateUpdate()
    {
        if (!m_blitInLateUpdate || !_hasPendingFrame || _pendingTexture == null)
        {
            return;
        }

        BlitToMaskRig(_pendingTexture);
        _hasPendingFrame = false;
    }

    private void HandleFrameApplied(Texture texture)
    {
        if (texture == null)
        {
            return;
        }

        if (m_blitInLateUpdate)
        {
            _pendingTexture = texture;
            _hasPendingFrame = true;
        }
        else
        {
            BlitToMaskRig(texture);
        }
    }

    private void CacheOutputLayer()
    {
        if (m_targetMaskRig == null)
        {
            _outputLayer = -1;
            return;
        }

        string layerName = m_targetMaskRig.Eye == Camera.MonoOrStereoscopicEye.Left
            ? "LeftOutput"
            : "RightOutput";
        _outputLayer = LayerMask.NameToLayer(layerName);
    }

    private void BlitToMaskRig(Texture texture)
    {
        if (m_targetMaskRig == null)
        {
            if (m_logDebug)
            {
                Debug.LogWarning($"{nameof(MaskRigFrameApplier)} on {name} has no MaskRig assigned.");
            }
            return;
        }

        RenderTexture targetRT = m_targetMaskRig.OutputTexture;

        if (targetRT == null)
        {
            var maskCamera = m_targetMaskRig.MaskCamera;
            if (maskCamera != null)
            {
                targetRT = maskCamera.targetTexture;
            }
        }

        if (!_layerEnsuredForCurrentRig && _outputLayer == -1)
        {
            CacheOutputLayer();
        }

        if (targetRT == null)
        {
            if (m_logDebug)
            {
                Debug.LogWarning($"{nameof(MaskRigFrameApplier)} could not locate a target RenderTexture on {m_targetMaskRig.name}.");
            }
            return;
        }

        EnsureLayerAndMaterialBindings(targetRT);
        Graphics.Blit(texture, targetRT);
    }

    private bool _layerEnsuredForCurrentRig;

    private void EnsureLayerAndMaterialBindings(RenderTexture targetRT)
    {
        if (m_targetMaskRig == null)
        {
            return;
        }

        if (_outputLayer == -1)
        {
            CacheOutputLayer();
        }

        var eyeCamera = m_targetMaskRig.EyeCamera;
        if (eyeCamera != null && _outputLayer >= 0)
        {
            eyeCamera.cullingMask |= (1 << _outputLayer);
        }

        var outputRenderer = m_targetMaskRig.OutputQuad;
        if (outputRenderer != null)
        {
            if (_outputLayer >= 0 && outputRenderer.gameObject.layer != _outputLayer)
            {
                outputRenderer.gameObject.layer = _outputLayer;
            }

            var mat = outputRenderer.sharedMaterial;
            if (mat != null && mat.GetTexture("_MainTex") != targetRT)
            {
                mat.SetTexture("_MainTex", targetRT);
            }
        }

        _layerEnsuredForCurrentRig = true;
    }
}
