using UnityEngine;

/// <summary>
/// 将绑定的 Quad 调整到摄像机近裁面处，并按视场大小进行缩放。
/// 支持左右眼分离位置，便于在 HMD 中贴合整个视域。
/// </summary>
[ExecuteAlways]
public class FillStereoQuad : MonoBehaviour
{
    [SerializeField] private Camera m_camera;
    [SerializeField] private bool m_isLeftEye = true;
    [SerializeField, Min(0.0f)] private float m_distanceFromNearPlane = 0.01f;
    [SerializeField] private float m_excessScale = 0.0f;

    private void LateUpdate()
    {
        if (m_camera == null)
        {
            return;
        }

        AlignToCamera();
    }

    private void AlignToCamera()
    {
        var camTransform = m_camera.transform;
        float nearOffset = m_camera.nearClipPlane + m_distanceFromNearPlane;
        float halfSeparation = m_camera.stereoSeparation * 0.5f;
        Vector3 eyeOffset = camTransform.right * (m_isLeftEye ? -halfSeparation : halfSeparation);

        transform.SetPositionAndRotation(
            camTransform.position + camTransform.forward * nearOffset + eyeOffset,
            camTransform.rotation);

        float height = Mathf.Tan(m_camera.fieldOfView * Mathf.Deg2Rad * 0.5f) * nearOffset * 2f + m_excessScale;
        float width = height * m_camera.aspect;
        transform.localScale = new Vector3(width, height, 1f);
    }

    public void SetCamera(Camera camera)
    {
        m_camera = camera;
    }

    public void SetEye(bool isLeftEye)
    {
        m_isLeftEye = isLeftEye;
    }
}
