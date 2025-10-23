/*
 * HRTK: FillStereoCamera.cs
 *
 * Copyright (c) 2023 Brandon Matthews
 */

using UnityEngine;

namespace HRTK.MaskedRetargeting
{
    [ExecuteInEditMode]
    public class FillStereoCamera : MonoBehaviour
    {
        public Camera cam;
        public float distance = 0.01f;
        public float excessSize = 0f;
        //  public Camera.MonoOrStereoscopicEye eye;   
        void Update()
        {
            if (cam == null)
            {
                if (Application.isPlaying && Time.frameCount % 30 == 0)
                {
                    Debug.LogWarning($"[{name}] FillStereoCamera 未找到 cam 引用");
                }
                return;
            }

            if (Application.isPlaying && Time.frameCount % 60 == 0)
            {
                Debug.Log($"[{name}] FillStereoCamera cam={cam.name}, pos={transform.position}");
            }
            float pos = (cam.nearClipPlane + distance);

            Vector3 sep = Vector3.zero;
            float sepAmount = cam.stereoSeparation / 2.0f;
            if (cam.stereoTargetEye == StereoTargetEyeMask.Left) sep = cam.transform.right * (-1 * sepAmount);
            if (cam.stereoTargetEye == StereoTargetEyeMask.Right) sep = cam.transform.right * sepAmount;

            transform.position = cam.transform.position + (cam.transform.forward * pos) + sep;

            float h = Mathf.Tan(cam.fieldOfView * Mathf.Deg2Rad * 0.5f) * pos * 2f + excessSize;

            transform.localScale = new Vector3(h * cam.aspect, h, 1f);
        }
    }
}
   