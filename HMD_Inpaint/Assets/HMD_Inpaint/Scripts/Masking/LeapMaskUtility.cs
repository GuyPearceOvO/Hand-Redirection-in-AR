using System;
using Leap;
using UnityEngine;
using Image = LeapInternal.Image;

/// <summary>
/// 提供将 Ultraleap 骨架投影到 IR 像素平面的遮罩生成工具。
/// </summary>
public static class LeapMaskUtility
{
    public static bool GenerateSkeletonMask(
        Controller controller,
        Frame frame,
        Device device,
        Image.CameraType camera,
        int width,
        int height,
        int strokeRadius,
        bool flipHorizontally,
        byte[] maskBuffer)
    {
        if (maskBuffer == null)
        {
            throw new ArgumentNullException(nameof(maskBuffer));
        }

        if (maskBuffer.Length != width * height)
        {
            throw new ArgumentException("maskBuffer size mismatch", nameof(maskBuffer));
        }

        Array.Clear(maskBuffer, 0, maskBuffer.Length);

        if (controller == null || frame == null || frame.Hands == null || frame.Hands.Count == 0)
        {
            return false;
        }

        bool wrote = false;
        foreach (var hand in frame.Hands)
        {
            if (hand == null)
            {
                continue;
            }

            wrote |= StampCircleFromPoint(ToVector3(hand.PalmPosition.x, hand.PalmPosition.y, hand.PalmPosition.z), controller, device, camera, width, height, strokeRadius * 2, flipHorizontally, maskBuffer);
            wrote |= StampCircleFromPoint(ToVector3(hand.WristPosition.x, hand.WristPosition.y, hand.WristPosition.z), controller, device, camera, width, height, strokeRadius, flipHorizontally, maskBuffer);

            var arm = hand.Arm;
            if (arm != null)
            {
                wrote |= StampSegment(ToVector3(arm.ElbowPosition.x, arm.ElbowPosition.y, arm.ElbowPosition.z), ToVector3(arm.WristPosition.x, arm.WristPosition.y, arm.WristPosition.z), controller, device, camera, width, height, strokeRadius * 2, flipHorizontally, maskBuffer);
                wrote |= StampCircleFromPoint(ToVector3(arm.ElbowPosition.x, arm.ElbowPosition.y, arm.ElbowPosition.z), controller, device, camera, width, height, strokeRadius * 2, flipHorizontally, maskBuffer);
            }

            foreach (var finger in hand.fingers)
            {
                if (finger == null)
                {
                    continue;
                }

                for (int boneIndex = 0; boneIndex < 4; boneIndex++)
                {
                    Bone bone = finger.bones != null && boneIndex < finger.bones.Length ? finger.bones[boneIndex] : null;
                    if (bone == null)
                    {
                        continue;
                    }
                    wrote |= StampSegment(ToVector3(bone.PrevJoint.x, bone.PrevJoint.y, bone.PrevJoint.z), ToVector3(bone.NextJoint.x, bone.NextJoint.y, bone.NextJoint.z), controller, device, camera, width, height, strokeRadius, flipHorizontally, maskBuffer);
                }
            }
        }

        return wrote;
    }

    private static bool StampCircleFromPoint(
        Vector3 devicePoint,
        Controller controller,
        Device device,
        Image.CameraType camera,
        int width,
        int height,
        int radius,
        bool flipHorizontally,
        byte[] maskBuffer)
    {
        if (!TryProjectToPixel(devicePoint, controller, device, camera, width, height, flipHorizontally, out int px, out int py))
        {
            return false;
        }

        StampCircle(px, py, radius, width, height, maskBuffer);
        return true;
    }

    private static bool StampSegment(
        Vector3 start,
        Vector3 end,
        Controller controller,
        Device device,
        Image.CameraType camera,
        int width,
        int height,
        int radius,
        bool flipHorizontally,
        byte[] maskBuffer)
    {
        if (!TryProjectToPixel(start, controller, device, camera, width, height, flipHorizontally, out int sx, out int sy) ||
            !TryProjectToPixel(end, controller, device, camera, width, height, flipHorizontally, out int ex, out int ey))
        {
            return false;
        }

        float dx = ex - sx;
        float dy = ey - sy;
        float length = Mathf.Sqrt(dx * dx + dy * dy);
        if (length < 1f)
        {
            StampCircle(sx, sy, radius, width, height, maskBuffer);
            return true;
        }

        int steps = Mathf.Clamp(Mathf.CeilToInt(length / Mathf.Max(1, radius / 2f)), 1, 256);
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            int px = Mathf.RoundToInt(Mathf.Lerp(sx, ex, t));
            int py = Mathf.RoundToInt(Mathf.Lerp(sy, ey, t));
            StampCircle(px, py, radius, width, height, maskBuffer);
        }
        return true;
    }

    private static bool TryProjectToPixel(
        Vector3 devicePoint,
        Controller controller,
        Device device,
        Image.CameraType camera,
        int width,
        int height,
        bool flipHorizontally,
        out int px,
        out int py)
    {
        px = 0;
        py = 0;

        if (controller == null)
        {
            return false;
        }

        float z = devicePoint.z;
        if (Mathf.Abs(z) < 1e-3f || z <= 0f)
        {
            return false;
        }

        float rayX = devicePoint.x / z;
        float rayY = devicePoint.y / z;
        Vector3 ray = new Vector3(rayX, rayY, 1f);
        Vector3 pixel;
        if (device != null && device.Handle != IntPtr.Zero)
        {
            pixel = controller.RectilinearToPixelEx(camera, ray, device);
        }
        else
        {
            pixel = controller.RectilinearToPixel(camera, ray);
        }

        if (float.IsNaN(pixel.x) || float.IsNaN(pixel.y))
        {
            return false;
        }

        int ix = Mathf.RoundToInt(pixel.x);
        int iy = Mathf.RoundToInt(pixel.y);
        if (ix < 0 || ix >= width || iy < 0 || iy >= height)
        {
            return false;
        }

        if (flipHorizontally)
        {
            ix = Mathf.Clamp(width - 1 - ix, 0, width - 1);
        }

        px = ix;
        py = height - 1 - iy;
        return true;
    }

    private static void StampCircle(int cx, int cy, int radius, int width, int height, byte[] maskBuffer)
    {
        if (maskBuffer == null || radius <= 0)
        {
            return;
        }

        int rSquared = radius * radius;
        int minX = Mathf.Clamp(cx - radius, 0, width - 1);
        int maxX = Mathf.Clamp(cx + radius, 0, width - 1);
        int minY = Mathf.Clamp(cy - radius, 0, height - 1);
        int maxY = Mathf.Clamp(cy + radius, 0, height - 1);

        for (int y = minY; y <= maxY; y++)
        {
            int dy = y - cy;
            int rowOffset = y * width;
            for (int x = minX; x <= maxX; x++)
            {
                int dx = x - cx;
                if (dx * dx + dy * dy <= rSquared)
                {
                    maskBuffer[rowOffset + x] = 255;
                }
            }
        }
    }

    private static Vector3 ToVector3(float x, float y, float z)
    {
        return new Vector3(x, y, z);
    }
}
