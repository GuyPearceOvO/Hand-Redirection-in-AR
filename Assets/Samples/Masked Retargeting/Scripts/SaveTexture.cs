/*
 * Utility helpers for dumping RenderTextures to disk.
 * The original HRTK sample references `SaveTexture` but the class was not part
 * of the published package, so we recreate the minimal functionality here.
 */
using System.IO;
using UnityEngine;

public static class SaveTexture
{
    /// <summary>
    /// Saves the provided RenderTexture to a PNG file on disk. Creates parent
    /// directories on demand and overwrites existing files.
    /// </summary>
    public static void SaveRenderTextureToFile(RenderTexture source, string filePath)
    {
        if (source == null)
        {
            Debug.LogWarning("SaveTexture: source RenderTexture is null.");
            return;
        }

        if (string.IsNullOrEmpty(filePath))
        {
            Debug.LogWarning("SaveTexture: file path is empty.");
            return;
        }

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var previous = RenderTexture.active;
        Texture2D temp = null;
        try
        {
            RenderTexture.active = source;
            temp = new Texture2D(source.width, source.height, TextureFormat.ARGB32, false, false);
            temp.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            temp.Apply(false, false);

            var bytes = temp.EncodeToPNG();
            File.WriteAllBytes(filePath.EndsWith(".png") ? filePath : $"{filePath}.png", bytes);
        }
        finally
        {
            if (temp != null)
            {
                Object.Destroy(temp);
            }
            RenderTexture.active = previous;
        }
    }
}
