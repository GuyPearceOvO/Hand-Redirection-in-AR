# Hand Redirection in AR with Inpainting-based Hand Removal
### Research Project: Implementing Haptic Retargeting in Augmented Reality

## 1. Overview

This project aims to **implement haptic retargeting in AR/MR** by combining two techniques:

- **Hand Redirection** – visually shifting the user’s virtual hand to simulate contact with multiple virtual objects while touching a single real one.
- **Inpainting-based Hand Removal** – hiding the user’s real hand in the AR view to maintain the illusion of ownership and continuity.

This research extends the concept of *Haptic Retargeting* (Azmandian et al., CHI 2016, DOI: [10.1145/2858036.2858226](https://dl.acm.org/doi/10.1145/2858036.2858226)) from **VR** to **AR**, where users can see their real hands, making the redirection illusion more challenging to achieve.

## 2. Research Goal

In VR, the user’s hand is fully virtual, so hand redirection works naturally.  
In AR, however, the user can see their real hand through the headset, breaking the illusion.  
The main challenge is therefore to **visually remove the real hand in real time**, and display the **redirected virtual hand** instead.

The goal of this project is to:
1. Hide the user’s real hand in AR through inpainting.
2. Implement real-time hand redirection in Unity.
3. Combine both to achieve haptic retargeting in AR/MR.
4. Realize a full-field-of-view (Full-FOV) AR experience on Meta Quest 3.

## 3. System Overview

### 3.1 Python-side: Inpainting System

Inpainting is the key to hiding the real hand.  
The pipeline includes hand detection and background reconstruction.

**Step 1: Hand Mask Generation**  
A custom **RTMDet** model (retrained on the **MHPv1** dataset) is used to detect hands.  
The original COCO-trained RTMDet does not include a "hand" class, so retraining was required.  
Paths for the model files are as follows:

- Weights:  
  `C:\Users\39241\HMD_Inpaint\HMD_Inpaint\Assets\StreamingAssets\InpaintServer\PC_Inpaint\weights`
- Configs:  
  `C:\Users\39241\HMD_Inpaint\HMD_Inpaint\Assets\StreamingAssets\InpaintServer\PC_Inpaint\config`

**Step 2: Image Inpainting**  
Detected hand regions are removed using **OpenCV’s Telea algorithm** (`cv2.inpaint()`), filling the area with the surrounding background.

**Step 3: TCP Server with Unity Bridge**  
The inpainting server also handles real-time communication with Unity via TCP.  
Server script location:
```
C:\Users\39241\HMD_Inpaint\HMD_Inpaint\Assets\StreamingAssets\InpaintServer\RTMDet_Realtime\tcp_inpaint_server_rtmdet_only.py
```

To launch the environment and start the server:

```bash
conda activate rtm_inpaint

python tcp_inpaint_server_rtmdet_only.py --host 127.0.0.1 --port 5566 --device cuda:0 ^
--config ..\PC_Inpaint\config\rtmdet-ins_s_mhpv1_arm_only_v2_maskboost_800.py ^
--weights ..\PC_Inpaint\weights\rtmdet-ins_s_mhpv1_arm_only_v2_maskboost_800\best_coco_segm_mAP_epoch_10.pth ^
--target-label arm --target-label person --score-threshold 0.08 ^
--inference-width 384 --inference-height 288 --inpaint-radius 4 ^
--mask-close 3 --mask-dilate 2 --min-area 64 --keep-frames 2 ^
--roi-margin 20 --jpeg-quality 75 --warmup
```

### 3.2 Unity-side: Hand Redirection (HART Toolkit)

Unity uses the **Hand Redirection Toolkit (HART)**:  
[https://github.com/AndreZenner/hand-redirection-toolkit/wiki](https://github.com/AndreZenner/hand-redirection-toolkit/wiki)

HART modifies the mapping between the real and virtual hands to create an illusion that the user is touching multiple objects while physically touching one.

Unity receives inpainted frames from Python via TCP bridge and displays them in the AR environment.  
The user only sees the redirected virtual hand — the real hand has been removed.

### 3.3 System Architecture

The entire system runs on a PC connected to:
- **Quest 3** via Oculus Link (used only for display, not computation)
- **Ultraleap Stereo IR camera** (for real hand image capture)

**Workflow:**
1. Ultraleap captures stereo IR frames.  
2. Python executes RTMDet segmentation and Telea inpainting.  
3. Processed frames are sent to Unity through TCP.  
4. Unity renders the inpainted frames and applies Hand Redirection (HART).  
5. Quest 3 displays the AR scene with redirected hands.

## 4. Full-FOV Rendering

Initially, the inpainted video was rendered on a single Quad plane, similar to watching a distant movie screen in VR — low immersion.

To achieve a **full field of view (Full-FOV)**:

- Two Quads are attached to **LeftEyeAnchor** and **RightEyeAnchor**.
- A custom script **FillStereoCamera.cs** ensures both eye views are synchronized.
- This creates an immersive full-view passthrough effect.

Scene path:  
`Assets/HMD_Inpaint/Scenes/Full-FOV Inpaint/Full Fov Inpaint.unity`

## 5. Current Performance Limitations

- Dual inpainting pipelines for left and right eyes reduce FPS (10–15 FPS).  
- Network latency between Python and Unity.  
- Inpainting currently runs on CPU; GPU acceleration is not yet implemented.

## 6. Future Work

1. **Performance Optimization**
   - Implement GPU-accelerated Telea or deep learning inpainting models.  
   - Optimize TCP data transmission or use shared memory / native plugins.  
   - Merge dual-eye pipelines for efficiency.

2. **Passthrough Camera API Integration**
   - Previously abandoned because the API could not modify raw passthrough frames.  
   - However, since dual-Quad rendering no longer requires raw frame modification,  
     it is now possible to reintroduce Meta’s **Passthrough Camera API**  
     for full-FOV inpainting without external cameras.

   TCP server script for this prototype:  
   `C:\Users\39241\HMD_Inpaint\HMD_Inpaint\Assets\StreamingAssets\InpaintServer\PC_Inpaint\Quest-PC_Server.py`

   Prototype workflow:
   - Quest 3 captures frames via Passthrough Camera API  
   - `PassthroughFrameSender.cs` sends JPEG frames to PC  
   - Python server `tcp_inpaint_server_rtmdet_only.py` performs inpainting  
   - Processed frames are returned to Unity for display

   Scene path:  
   `Assets/HMD_Inpaint/Scenes/Quest_Inpaint/HMD_Inpaint_HR.unity`

3. **Synchronization Improvement**
   - Improve synchronization between redirected hand and inpainted frames.  
   - Conduct user studies on perceptual coherence and immersion.

## 7. Current Progress

- RTMDet hand segmentation model: ✅ retrained and functional.  
- OpenCV Telea inpainting: ✅ implemented.  
- Python–Unity TCP bridge: ✅ stable.  
- Hand Redirection (HART): ✅ integrated.  
- Full-FOV rendering: ✅ implemented.  
- Passthrough Camera API: 🔄 reintroduction planned.  
- FPS optimization: 🚧 in progress.

Next steps:
- Improve system performance for real-time use.  
- Reintroduce Passthrough Camera API for native AR integration.  


