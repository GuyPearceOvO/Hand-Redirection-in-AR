# RTMDet-Ins-s ARM-only v2 (mask-boost, 800-scale finetune)
# Increase input scale to 800 for potentially finer masks.

_base_ = 'C:/Users/39241/miniconda3/envs/rtm_inpaint/Lib/site-packages/mmdet/.mim/configs/rtmdet/rtmdet-ins_s_8xb32-300e_coco.py'

data_root = 'C:/Users/39241/RTMDet_Test/datasets/LV-MHP-v1_COCO_ARM_ONLY/'
class_names = ('arm',)
num_classes = 1
metainfo = dict(classes=class_names, palette=[(0, 255, 0)])

model = dict(
    bbox_head=dict(
        num_classes=num_classes,
        loss_cls=dict(type='QualityFocalLoss', use_sigmoid=True, beta=2.0, loss_weight=1.0),
        loss_mask=dict(type='DiceLoss', loss_weight=6.0, eps=5e-6, reduction='mean')
    ),
    test_cfg=dict(
        score_thr=0.001,
        nms=dict(type='nms', iou_threshold=0.5),
        max_per_img=50,
        mask_thr_binary=0.4
    )
)

max_epochs = 10
train_cfg = dict(type='EpochBasedTrainLoop', max_epochs=max_epochs, val_interval=1000)

optim_wrapper = dict(
    type='OptimWrapper',
    optimizer=dict(type='AdamW', lr=0.00001, weight_decay=0.05),
    paramwise_cfg=dict(bias_decay_mult=0, norm_decay_mult=0, bypass_duplicate=True),
    clip_grad=dict(max_norm=35, norm_type=2)
)

param_scheduler = [
    dict(type='LinearLR', start_factor=0.001, by_epoch=False, begin=0, end=200),
    dict(type='MultiStepLR', begin=0, end=max_epochs, by_epoch=True, milestones=[7], gamma=0.1)
]

# 800-scale pipelines
train_pipeline = [
    dict(type='LoadImageFromFile'),
    dict(type='LoadAnnotations', with_bbox=True, with_mask=True),
    dict(type='Resize', scale=(800, 800), keep_ratio=True),
    dict(type='Pad', size=(800, 800)),
    dict(type='RandomFlip', prob=0.5),
    dict(type='PackDetInputs')
]

val_pipeline = [
    dict(type='LoadImageFromFile'),
    dict(type='LoadAnnotations', with_bbox=True, with_mask=True),
    dict(type='Resize', scale=(800, 800), keep_ratio=True),
    dict(type='Pad', size_divisor=32),
    dict(type='PackDetInputs', meta_keys=('img_id', 'img_path', 'ori_shape', 'img_shape', 'scale_factor'))
]

# Slightly reduce batch size to fit memory at 800 scale
train_dataloader = dict(
    batch_size=6, num_workers=4,
    dataset=dict(
        type='CocoDataset', data_root=data_root,
        ann_file='annotations/instances_train2017.json',
        data_prefix=dict(img='images/'),
        pipeline=train_pipeline, metainfo=metainfo
    )
)

val_dataloader = dict(
    batch_size=1, num_workers=2,
    dataset=dict(
        type='CocoDataset', data_root=data_root,
        ann_file='annotations/instances_val2017.json',
        data_prefix=dict(img='images/'),
        test_mode=True, pipeline=val_pipeline, metainfo=metainfo
    )
)

test_dataloader = val_dataloader

val_evaluator = dict(
    type='CocoMetric',
    ann_file=data_root + 'annotations/instances_val2017.json',
    metric=['bbox', 'segm'],
    classwise=True
)
test_evaluator = val_evaluator

default_hooks = dict(
    logger=dict(type='LoggerHook', interval=50),
    checkpoint=dict(type='CheckpointHook', interval=1, save_best='coco/segm_mAP', rule='greater', max_keep_ckpts=2)
)

work_dir = './work_dirs/rtmdet-ins_s_mhpv1_arm_only_v2_maskboost_800'
load_from = 'work_dirs/rtmdet-ins_s_mhpv1_arm_only_v2_finetune/best_coco_segm_mAP_epoch_5.pth'
