项目：**HMD_Inpaint**（AR 手部重定向）  
目标：利用 Ultraleap 采集的双目 IR 画面，经 Python 端 RTMDet + OpenCV Telea 修补后，回写到 Quest HMD 内部，使用 HRTK 的 “Mask Setup + Overlay Camera” 流程实现全视域 inpainting，淘汰旧的 “单纹理双 Quad” 方案。

## 当前场景与组件
- 工作场景：`Assets/HMD_Inpaint/Scenes/FullVision/Full Vision Test v1.unity`
  - `OVRCameraRig`：`CenterEyeAnchor` 为唯一的 `MainCamera`
  - `LeftEyeAnchor/Mask Setup` 与 `RightEyeAnchor/Mask Setup`（HRTK 预制体）
  - `Overlay Camera`（启用、Tag = `Untagged`，负责最终显示）
  - `Mask Camera`（每只眼一个，启用、`Untagged`）
  - `UltraleapBridge_Left / _Right`（`PassthroughFrameReceiver` + `UltraleapFrameSender` + `MaskRigFrameApplier`）
  - `XR Leap Provider Manager` 预制体（提供 `LeapXRServiceProvider` 与 `LeapImageRetriever`）
- 参考 Sample：`haptic-retargeting-toolkit/Samples~/Masked Retargeting/Masked Retargeting Test.unity`

## 关键samples
C:\Users\39241\HMD_Inpaint\haptic-retargeting-toolkit

## Python Inpainting Server
```
Assets/StreamingAssets/InpaintServer/RTMDet_Realtime/tcp_inpaint_server_rtmdet_only.py
Assets/StreamingAssets/InpaintServer/PC_Inpaint/rtmdet_inpainter_stable.py
```
示例启动命令：
```
cd C:\Users\39241\HMD_Inpaint\HMD_Inpaint\Assets\StreamingAssets\InpaintServer\RTMDet_Realtime

conda activate rtm_inpaint

python tcp_inpaint_server_rtmdet_only.py --host 127.0.0.1 --port 5566 --device cuda:0 --config ..\PC_Inpaint\config\rtmdet-ins_s_mhpv1_arm_only_v2_maskboost_800.py --weights ..\PC_Inpaint\weights\rtmdet-ins_s_mhpv1_arm_only_v2_maskboost_800\best_coco_segm_mAP_epoch_10.pth --target-label arm --target-label person --score-threshold 0.08 --inference-width 384 --inference-height 288 --inpaint-radius 4 --mask-close 3 --mask-dilate 2 --min-area 64 --keep-frames 2 --roi-margin 20 --jpeg-quality 75 --warmup --debug-dir .\debug_out --debug-every 10
```

## 当前问题
1. Link 连接与 Python 服务都正常，Unity 日志持续出现 `PassthroughFrameReceiver: Frame queued …`。
2. 切换到 HRTK 的 Mask Setup / Overlay Camera 管线后，Quest 内只看到纯蓝背景（Overlay Camera 的背景色），没有 inpainting 画面。
3. `Mask Quad` 使用 `MaskOutputLeft`（`Unlit/Texture`）并挂有 `FillStereoCamera`，`Cam` 指向 `LeftEyeAnchor (Camera)`；`Mask Camera` 的 `Target Texture` 显示 `TempBuffer #### (831x395)`，但预览仍为纯黑。
4. `MaskRigFrameApplier` 开启 `Log Debug` 未报错，怀疑 `Graphics.Blit` 没把纹理写入或材质 `_MainTex` 未绑定。
