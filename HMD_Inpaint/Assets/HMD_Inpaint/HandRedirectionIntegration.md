项目名称：HMD_Inpaint（AR hand redirection） 目标：在 AR 中隐藏真实手部，并叠加重定向后的虚拟手，实现类似 Haptic Retargeting 的交互体验。 

当前方案： 

输入：Ultraleap 手势设备提供的 IR 帧与手部骨架。 

Unity 侧遮罩生成： 

Assets/HMD_Inpaint/Scripts/Masking/UltraleapMaskRig.cs 使用 LeapMaskUtility 将骨架投影到 IR 像素坐标，生成 640×480 二值遮罩。 

MaskRigAdapter 管理遮罩 RenderTexture，MaskCaptureProvider 读取并传输遮罩。 

Unity → Python： 

UltraleapFrameSender.cs 把 IR 帧（JPEG）与遮罩发送到 RTMDet_Realtime/tcp_inpaint_server_skeleton.py。 

Python 端： 

rtmdet_inpainter.py（参考 haptic-retargeting-toolkit）结合 RTMDet + OpenCV Telea inpaint 对手部区域修补。 

修补后的帧回传给 Unity，由 PassthroughFrameReceiver.cs 显示在 Quad 上。 

关键资源/场景： 

Unity 场景：Assets/HMD_Inpaint/Scenes/Ultraleap_Inpaint.unity 

参考论文：
C:\Users\39241\HMD_Inpaint\论文\HapticRetargeting_CHI2016.pdf
C:\Users\39241\HMD_Inpaint\论文\MaskWarp_Visuo-Haptic_Illusions_in_Mixed_Reality_using_Real-time_Video_Inpainting.pdf

参考项目：C:\Users\39241\HMD_Inpaint\haptic-retargeting-toolkit

python_server: C:\Users\39241\HMD_Inpaint\HMD_Inpaint\Assets\StreamingAssets\InpaintServer

关键脚本：C:\Users\39241\HMD_Inpaint\HMD_Inpaint\Assets\HMD_Inpaint\Scripts

启动方法：
Cd /C:/Users/39241/HMD_Inpaint/HMD_Inpaint/Assets/StreamingAssets/InpaintServer/RTMDet_Realtime  

conda activate rtm_inpaint  

python tcp_inpaint_server_skeleton.py --host 127.0.0.1 --port 5555 

后续 TODO： 
优化 FPS（Unity 与 Python 两端的推送/推理性能）。 

提升 inpaint 质量（遮罩厚度、Gaussian blur、RTMDet 参数等）。 

在 HMD 全视场中直接展示修补结果，而不是投影到 Quad，并叠加手部重定向模型。 

请继续在这个上下文基础上协助我。 

目前用的好像是cpu，用gpu。
 