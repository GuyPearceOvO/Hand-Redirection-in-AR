import os
import cv2
import numpy as np
from mmdet.apis import DetInferencer
from pycocotools import mask as mask_utils

# ==== 配置路径 ====
config_path = 'config/rtmdet-ins_s.py'
weights_path = 'weights/rtmdet-ins_s.pth'

# ==== 初始化模型 ====
inferencer = DetInferencer(
    model=config_path,
    weights=weights_path,
    device='cuda:0'
)

# ==== 打开摄像头 ====
cap = cv2.VideoCapture(0)  # 使用默认摄像头
if not cap.isOpened():
    raise FileNotFoundError("❌ 无法打开摄像头")

# 设置目标分辨率
target_width = 640
target_height = 480
cap.set(cv2.CAP_PROP_FRAME_WIDTH, target_width)
cap.set(cv2.CAP_PROP_FRAME_HEIGHT, target_height)

# 获取类别名
class_names = inferencer.model.dataset_meta['classes']
HAND_LABELS = ['person']  # 暂用 person 模拟 hand

print("🚀 开始实时检测...")
print("按 'q' 键退出程序")

while True:
    ret, frame = cap.read()
    if not ret:
        break

    # Resize 到较小尺寸，加快推理速度
    frame = cv2.resize(frame, (target_width, target_height))
    rgb_frame = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)

    result = inferencer(
        inputs=rgb_frame,
        show=False,
        no_save_pred=True,
        no_save_vis=True,
        out_dir=None
    )

    preds = result['predictions'][0]
    masks = preds['masks']
    labels = preds['labels']

    # 合并所有感兴趣类别的 mask（如：person）
    combined_mask = np.zeros((target_height, target_width), dtype=bool)

    for i in range(len(masks)):
        label_id = labels[i]
        label_name = class_names[label_id]

        if label_name not in HAND_LABELS:
            continue

        m = masks[i]
        if isinstance(m, dict) and 'size' in m and 'counts' in m:
            mask = mask_utils.decode(m).astype(bool)
            combined_mask |= mask
        else:
            continue

    # === 应用 OpenCV Inpainting ===
    if combined_mask.any():
        # 将 boolean mask 转换为 uint8，0或255
        inpaint_mask = (combined_mask.astype(np.uint8)) * 255

        # OpenCV inpainting，选择 TELEA 算法
        frame = cv2.inpaint(frame, inpaint_mask, inpaintRadius=3, flags=cv2.INPAINT_TELEA)

    # 显示原始和处理后的结果
    cv2.imshow('Original', rgb_frame)  # 显示原始画面
    cv2.imshow('Hand Inpainting', frame)  # 显示修复后的画面
    
    # 将两个窗口并排放置
    cv2.moveWindow('Original', 100, 100)
    cv2.moveWindow('Hand Inpainting', 100 + target_width + 30, 100)
    
    # 检查按键，按q退出
    if cv2.waitKey(1) & 0xFF == ord('q'):
        break

# 释放资源
cap.release()
cv2.destroyAllWindows()
print("✅ 程序已退出")
