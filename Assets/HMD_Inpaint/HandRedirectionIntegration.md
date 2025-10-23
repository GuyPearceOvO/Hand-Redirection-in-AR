项目说明文档：在AR中实现结合Inpainting与Hand Redirection的Haptic Retargeting系统
1. 背景

Haptic Retargeting（触觉重定向）是一种最初在虚拟现实（VR）中提出的伪触觉技术。它使用户只需操纵一个真实物体，就能在虚拟环境中感受到操纵多个虚拟物体的错觉。
该技术的关键是 Hand Redirection（手部重定向），通过在视觉上轻微偏移虚拟手的位置，让用户感觉自己在接触不同位置的物体。

这项技术最早由 Azmandian 等人在 2016 年的论文
《Haptic Retargeting: Dynamic Repurposing of Passive Haptics for Enhanced Virtual Reality Experiences》（CHI 2016, DOI: 10.1145/2858036.2858226）中提出。
在VR中，用户看不到自己的真实手，因此会自然地将虚拟手认知为自己的手，从而轻易接受这种重定向错觉。

然而，在 增强现实（AR）或混合现实（MR） 环境中，用户能看到自己的真实手，这种视觉线索会破坏错觉效果。因此，要在AR/MR中实现haptic retargeting，首先必须隐藏用户真实的手，再显示经过重定向的虚拟手。

2. 研究目的

本研究的目标是：

在 AR/MR 环境中实现 Haptic Retargeting。

具体目标如下：

在AR环境中隐藏用户的真实手；

在Unity中实现实时的Hand Redirection；

将Inpainting与Hand Redirection结合，实现混合现实中的伪触觉体验；

最终在Quest 3设备上实现全视野（Full-FOV）的伪触觉交互效果。

3. 方法概述
3.1 Inpainting系统（Python端）

在AR中隐藏真实手的关键是 图像修补（Inpainting）。整个流程如下：

第一步：手部掩码生成（Mask Generation）
使用经过重训的 RTMDet 模型 来识别手部。
由于原始RTMDet基于COCO数据集训练，而COCO中没有“手”类别，因此本研究使用 MHPv1 数据集重新训练，仅识别“手（hand）”和“人（person）”类别。

模型路径如下：

模型权重：
C:\Users\39241\HMD_Inpaint\HMD_Inpaint\Assets\StreamingAssets\InpaintServer\PC_Inpaint\weights

配置文件：
C:\Users\39241\HMD_Inpaint\HMD_Inpaint\Assets\StreamingAssets\InpaintServer\PC_Inpaint\config

第二步：图像修补（Inpainting）
将生成的手部掩码输入 OpenCV 的 Telea 算法（cv2.inpaint()）进行修补，从图像中去除手部像素并自动填补背景。

第三步：TCP服务器（包含与Unity的Bridge）
Python端的Inpainting服务器脚本位置如下：
C:\Users\39241\HMD_Inpaint\HMD_Inpaint\Assets\StreamingAssets\InpaintServer\RTMDet_Realtime\tcp_inpaint_server_rtmdet_only.py
该脚本同时实现了 Python ↔ Unity 的TCP Bridge，负责接收Unity发送的图像帧，执行RTMDet分割与OpenCV修补后，再将结果帧返回Unity。

启动虚拟环境：

conda activate rtm_inpaint


启动服务器命令：

python tcp_inpaint_server_rtmdet_only.py --host 127.0.0.1 --port 5566 --device cuda:0 \
--config ..\PC_Inpaint\config\rtmdet-ins_s_mhpv1_arm_only_v2_maskboost_800.py \
--weights ..\PC_Inpaint\weights\rtmdet-ins_s_mhpv1_arm_only_v2_maskboost_800\best_coco_segm_mAP_epoch_10.pth \
--target-label arm --target-label person --score-threshold 0.08 \
--inference-width 384 --inference-height 288 --inpaint-radius 4 \
--mask-close 3 --mask-dilate 2 --min-area 64 --keep-frames 2 \
--roi-margin 20 --jpeg-quality 75 --warmup

3.2 Unity端 Hand Redirection 系统

在 Unity 中使用 HART (Hand Redirection Toolkit) 实现手部重定向：
https://github.com/AndreZenner/hand-redirection-toolkit/wiki

该系统通过修改真实手与虚拟手之间的映射关系，让用户在触摸同一个现实物体时，产生与多个虚拟物体交互的错觉。

Unity 端通过 TCP Bridge 接收来自 Python 的 inpainted 图像帧，并将其渲染到场景中。由于真实手已被修补删除，虚拟手可以自然地显示在正确位置，从而维持触觉错觉。

3.3 系统结构

整个系统运行在一台 PC 上，Quest 3 和 Ultraleap Stereo IR 摄像头同时连接到 PC。

流程如下：

Ultraleap 捕获双目红外画面；

Python 端执行 RTMDet + OpenCV Telea 图像修补；

通过 TCP Bridge 传输修补后的视频帧；

Unity 接收帧并在场景中渲染，同时执行 Hand Redirection；

Quest 3 通过 Oculus Link 作为显示设备（不在头显本地运行）。

该方案避免在 Quest 3 上运行高负载任务，从而避免GPU/CPU瓶颈。

4. 全视野（Full-FOV）渲染实现

初期方案仅将修补后的视频贴在一个 Quad 上显示，效果类似“在HMD中看电影”，沉浸感很差。
为实现全视野显示，改进如下：

将两块 Quad 分别贴在 LeftEyeAnchor 与 RightEyeAnchor 上；

使用 FillStereoCamera.cs 脚本确保双目画面对齐；

实现全视野（Full-FOV）渲染，沉浸感显著提升。

实现场景路径：
Assets/HMD_Inpaint/Scenes/Full-FOV Inpaint/Full Fov Inpaint.unity

5. 性能问题

当前存在以下瓶颈：

左右眼使用两条独立 Inpainting 流程，帧率较低（约10–15 FPS）；

Python 与 Unity 之间的数据传输存在延迟；

Inpainting 仅使用 CPU，没有GPU加速。

6. 未来方向

性能优化

尝试使用GPU加速（CUDA版Telea或深度学习修补模型）；

优化TCP数据传输，或考虑使用共享内存或Unity原生插件；

合并左右眼管线，减少重复计算。

Passthrough Camera API再利用

之前放弃的原因是Passthrough API无法直接修改原始帧；

但使用双Quad + Stereo Fill后，不再需要直接操作底层帧；

因此可重新尝试使用Passthrough Camera API，实现
“Full-FOV Inpainting + Hand Redirection”
而无需外接Ultraleap摄像头。

该功能原型的TCP服务器位置如下：
C:\Users\39241\HMD_Inpaint\HMD_Inpaint\Assets\StreamingAssets\InpaintServer\PC_Inpaint\Quest-PC_Server.py

原型方案如下：
Quest 3 通过 Passthrough Camera API 捕获帧 →
PassthroughFrameSender.cs 将 JPEG 帧发送到 PC →
Python 端 tcp_inpaint_server_rtmdet_only.py 调用 rtmdet_inpainter.py 执行修补 →
修补后帧返回 Unity → 显示。
场景路径：
Assets/HMD_Inpaint/Scenes/Quest_Inpaint/HMD_Inpaint_HR.unity

同步优化

改进 Inpainting 帧与 Hand Redirection 的同步；

进行用户体验实验，评估视觉一致性与沉浸感。

7. 当前进展

RTMDet 手部分割模型：已完成训练与验证。

OpenCV Telea Inpainting：已实现。

Python–Unity TCP桥：已稳定运行。

Hand Redirection（HART）：已集成。

Full-FOV Inpainting：已实现。

Passthrough Camera API：计划重新启用。

FPS优化：进行中。

8. 总结

本研究成功地在AR环境中实现了**手部隐藏（Inpainting）与手部重定向（Hand Redirection）**的初步结合。
通过实时去除真实手并渲染虚拟手，用户在AR中也能体验到类似VR的伪触觉错觉。

下一步计划：

提高实时性能；

利用Passthrough Camera API实现无外接摄像头的全视野Inpainting；

将系统完善为国际会议（如CHI/GCCE）可展示的Demo版本。