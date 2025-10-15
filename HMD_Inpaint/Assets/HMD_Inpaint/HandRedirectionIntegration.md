# Hand Redirection + Inpaint 集成说明

本文档记录当前仓库结构、主要资源位置以及运行 Inpaint 服务的步骤，便于后续在 AR Hand Redirection 项目中协同维护。

## 目录结构概览

- `Assets/HMD_Inpaint/Scenes/Ultraleap_Inpaint.unity`  
  主要工作场景，包含 Ultraleap 输入、遮罩生成和 Quad 视频显示。
- `Assets/HMD_Inpaint/Scripts/`  
  所有自定义脚本，其中遮罩相关脚本位于 `Scripts/Masking/`。
- `Assets/HMD_Inpaint/Materials/` 与 `Assets/HMD_Inpaint/Shaders/`  
  包含 `MaskPassthrough` 材质 & Shader，用于生成遮罩纹理。
- `Assets/StreamingAssets/InpaintServer/`  
  Python 端实时 inpaint 服务，内含：
  - `RTMDet_Realtime/`：TCP 服务器脚本（`tcp_inpaint_server_skeleton.py` 等）。
  - `PC_Inpaint/`：RTMDet 配置、权重与 `rtmdet_inpainter.py`。

> 说明：原先的 `Assets/HMD_Inpaint_v1/` 已精简；第三方 `haptic-retargeting-toolkit` 和论文 PDF 不再纳入仓库。

## 运行步骤

1. **启动 Python Inpaint 服务（PC）**
   ```powershell
   cd C:\Users\39241\HMD_Inpaint\HMD_Inpaint\Assets\StreamingAssets\InpaintServer\RTMDet_Realtime
   conda activate rtm_inpaint    # 或你的 Python 环境
   python tcp_inpaint_server_skeleton.py --host 127.0.0.1 --port 5555
   ```
   - 首次运行会加载 RTMDet 权重；`mask=xxxxx` 表示已收到 Unity 传来的遮罩。
   - 需要自定义参数（设备、分辨率等）可参考脚本内 `parse_args`。

2. **Unity 端检查**
- 打开 `Ultraleap_Inpaint.unity`，确认以下引用：
   - `UltraleapMaskRig`：`Mask Rig Adapter` 指向 `MaskCamera`，`Camera` 与 `UltraleapFrameSender` 的 `Eye` 一致。
   - `MaskCaptureProvider`：`Mask Rig Adapter` 引用 `MaskCamera`，Override 分辨率与 Ultraleap IR 帧一致（默认 640×480）。
   - `UltraleapFrameSender`：`Geometry Mask Provider` 指向 `MaskCaptureProvider`，网络配置与服务器一致。
 - 关闭调试用的 `Capsule Hands`（若仅需展示 inpaint 后的画面），以免遮挡。

3. **运行与调试**
   - 进入 Play Mode，Unity 将连接 Python 服务器，把 IR 帧与遮罩发送到 `tcp_inpaint_server_skeleton.py`。
   - 如果想同时观察遮罩，可在场景里绑定 `MaskCaptureProvider.CurrentMaskTexture` 到调试 UI。

## 额外说明

- `Library/`、`Temp/` 等为 Unity 缓存，可在空间不足时删除后重开项目重建。
- Git 版本控制已忽略 `.vscode/`、第三方示例及论文文件；若需共享 VS Code 设置，可手动解除忽略并提交。
- 若要改成 HMD 直接展示 inpaint 结果，可将当前 Quad 输出替换为相应的 Passthrough 管线，并复用上述遮罩 + 服务器流程。
- `Assets/StreamingAssets/InpaintServer/PC_Inpaint/weights/` 目录较大，默认不纳入 Git。若需要预训练权重，请在服务器文档或 README 中补充下载链接后手动放置。***
