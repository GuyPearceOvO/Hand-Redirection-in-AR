using UnityEngine;

/// <summary>
/// 将 PassthroughFrameReceiver 解码得到的纹理同步到左右眼 Quad。
/// </summary>
[RequireComponent(typeof(PassthroughFrameReceiver))]
public class StereoPassthroughDisplay : MonoBehaviour
{
    [SerializeField] private Renderer m_leftRenderer;
    [SerializeField] private Renderer m_rightRenderer;
    [SerializeField] private string m_textureProperty = "_MainTex";

    private PassthroughFrameReceiver _receiver;
    private MaterialPropertyBlock _leftBlock;
    private MaterialPropertyBlock _rightBlock;
    private Texture _lastTexture;

    private void Awake()
    {
        _receiver = GetComponent<PassthroughFrameReceiver>();
        _leftBlock = new MaterialPropertyBlock();
        _rightBlock = new MaterialPropertyBlock();
    }

    private void LateUpdate()
    {
        var texture = _receiver.CurrentTexture;
        if (texture == null)
        {
            return;
        }

        if (texture != _lastTexture)
        {
            ApplyTexture(texture);
            _lastTexture = texture;
        }
    }

    private void ApplyTexture(Texture texture)
    {
        if (m_leftRenderer != null)
        {
            m_leftRenderer.GetPropertyBlock(_leftBlock);
            _leftBlock.SetTexture(m_textureProperty, texture);
            m_leftRenderer.SetPropertyBlock(_leftBlock);
        }

        if (m_rightRenderer != null)
        {
            m_rightRenderer.GetPropertyBlock(_rightBlock);
            _rightBlock.SetTexture(m_textureProperty, texture);
            m_rightRenderer.SetPropertyBlock(_rightBlock);
        }
    }
}
