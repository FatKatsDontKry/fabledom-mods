using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FabledomTailor
{
    // Optional images: PNG files in an "icons" folder beside the plugin DLL.
    //   nogarments.png, hungrysheep.png  - the overhead markers for the two statuses
    //   tailor_albedo/_normal/_roughness/_emission.png - the Tailor's own look
    //   tailor_fabric.png                - the cloth on the clothing lines
    // Any file missing just leaves the game's own picture in place.
    internal static class Icons
    {
        private static readonly Dictionary<string, Sprite> cache = new Dictionary<string, Sprite>();
        private static string Folder => Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? "", "icons");

        internal static Sprite Load(string name)
        {
            if (cache.TryGetValue(name, out var cached)) return cached;
            Sprite sprite = null;
            try
            {
                string path = Path.Combine(Folder, name + ".png");
                if (File.Exists(path))
                {
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "Fab icon " + name, filterMode = FilterMode.Bilinear };
                    if (tex.LoadImage(File.ReadAllBytes(path)))
                    {
                        sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                        sprite.name = "Fab icon " + name;
                        Plugin.Log.LogInfo($"Tailor: icon loaded: {name}.png ({tex.width}x{tex.height}).");
                    }
                    else Plugin.Log.LogWarning($"Tailor: icon {name}.png could not be read as an image.");
                }
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"Tailor: icon {name}: {ex.Message}"); }
            cache[name] = sprite;
            return sprite;
        }

        // A texture from the icons folder (no sprite), or null if the file is missing. Normal, roughness and other data
        // maps are loaded linear; colour textures as sRGB.
        private static readonly Dictionary<string, Texture2D> texCache = new Dictionary<string, Texture2D>();
        internal static Texture2D LoadTexture(string name, bool linear)
        {
            if (texCache.TryGetValue(name, out var cached)) return cached;
            Texture2D tex = null;
            try
            {
                string path = Path.Combine(Folder, name + ".png");
                if (File.Exists(path))
                {
                    tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = "Fab " + name, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, anisoLevel = 4 };
                    if (!tex.LoadImage(File.ReadAllBytes(path))) { tex = null; Plugin.Log.LogWarning($"Tailor: {name}.png could not be read as an image."); }
                    else Plugin.Log.LogInfo($"Tailor: texture loaded: {name}.png ({tex.width}x{tex.height}).");
                }
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning($"Tailor: texture {name}: {ex.Message}"); tex = null; }
            texCache[name] = tex;
            return tex;
        }
    }
}
