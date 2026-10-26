using System.Linq;
using HarmonyLib;
using Nielsen;
using UnityEngine;

namespace FabledomTailor
{
    // The Sheep farm's trough shows its grain (Jess, 2026-10-05): heaped at 20 or more, a little from 1 to 19, bare at 0.
    // The trough is part of the farm's one mesh (the long low box inside the pen on the right), so two grain mounds
    // are laid in it and switched by the store.
    [HarmonyPatch]
    internal static class Trough
    {
        private static Material grainMaterial;

        [HarmonyPatch(typeof(Constructable), "Awake"), HarmonyPostfix]
        private static void Dress(Constructable __instance)
        {
            var wo = __instance.GetComponent<WorldObject>();
            if (wo == null || Tailor.FarmData == null || wo.data != Tailor.FarmData) return;
            if (!Plugin.TroughGrain.Value) return;
            try
            {
                var mf = __instance.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(m => m.sharedMesh != null && m.sharedMesh.name == "sheepFarm_type1_var001");
                if (mf == null) { Plugin.Log.LogWarning("Tailor: no farm mesh to lay the trough grain in."); return; }
                if (mf.transform.Find("Fab trough full") != null) return;
                // the trough in the mesh's own frame: x 15.9..18.4, z 7.8..16.3, rim at y 1.8
                var full = Mound(mf.transform, "Fab trough full", new Vector3(Plugin.TroughX.Value, 0.4f, Plugin.TroughZ.Value), new Vector3(Plugin.TroughWidth.Value, 1.5f, Plugin.TroughLength.Value));
                var little = Mound(mf.transform, "Fab trough little", new Vector3(Plugin.TroughX.Value, 0.4f, Plugin.TroughZ.Value), new Vector3(Plugin.TroughWidth.Value * 0.8f, 0.6f, Plugin.TroughLength.Value * 0.9f));
                var place = __instance.GetComponent<ProductionPlace>();
                void Refresh()
                {
                    int grain = place != null && place.storage != null ? place.storage.GetResourceStored("grain") : 0;
                    bool built = __instance.isConstructionComplete;
                    full.SetActive(built && grain >= Plugin.TroughFullAt.Value);
                    little.SetActive(built && grain > 0 && grain < Plugin.TroughFullAt.Value);
                }
                Refresh();
                if (place != null && place.storage != null) place.storage.OnStorageAltered += Refresh;
                __instance.OnConstructionComplete += Refresh;
                Plugin.Instance.StartCoroutine(RefreshLater(Refresh));
            }
            catch (System.Exception ex) { Plugin.Log.LogWarning("Tailor: trough: " + ex.Message); }
        }

        // The storage may not have loaded its contents when Awake ran.
        private static System.Collections.IEnumerator RefreshLater(System.Action refresh)
        {
            for (int i = 0; i < 30; i++) yield return null;
            try { refresh(); } catch { }
        }

        // A low box of grain colour, bottom at the given height, centred on x/z. Our own mesh rather than Unity's
        // primitive: readable, with normals, two uv sets, colours and tangents, so the game's shaders find every stream.
        private static Mesh boxMesh;
        private static GameObject Mound(Transform parent, string name, Vector3 at, Vector3 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(at.x, at.y + size.y / 2f, at.z);
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = size;
            go.layer = parent.gameObject.layer;
            go.AddComponent<MeshFilter>().sharedMesh = BoxMesh();
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = GrainMaterial();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            go.SetActive(false);
            return go;
        }

        private static Mesh BoxMesh()
        {
            if (boxMesh != null) return boxMesh;
            var m = new Mesh { name = "Fab grain heap" };
            var v = new System.Collections.Generic.List<Vector3>(); var n = new System.Collections.Generic.List<Vector3>();
            var uv = new System.Collections.Generic.List<Vector2>(); var t = new System.Collections.Generic.List<int>();
            void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
            {
                int i = v.Count;
                v.Add(a); v.Add(b); v.Add(c); v.Add(d);
                for (int k = 0; k < 4; k++) n.Add(normal);
                uv.Add(new Vector2(0, 0)); uv.Add(new Vector2(0, 1)); uv.Add(new Vector2(1, 1)); uv.Add(new Vector2(1, 0));
                t.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
            }
            float h = 0.5f;
            Face(new Vector3(-h, h, -h), new Vector3(-h, h, h), new Vector3(h, h, h), new Vector3(h, h, -h), Vector3.up);
            Face(new Vector3(-h, -h, h), new Vector3(-h, -h, -h), new Vector3(h, -h, -h), new Vector3(h, -h, h), Vector3.down);
            Face(new Vector3(-h, -h, -h), new Vector3(-h, h, -h), new Vector3(h, h, -h), new Vector3(h, -h, -h), Vector3.back);
            Face(new Vector3(h, -h, h), new Vector3(h, h, h), new Vector3(-h, h, h), new Vector3(-h, -h, h), Vector3.forward);
            Face(new Vector3(-h, -h, h), new Vector3(-h, h, h), new Vector3(-h, h, -h), new Vector3(-h, -h, -h), Vector3.left);
            Face(new Vector3(h, -h, -h), new Vector3(h, h, -h), new Vector3(h, h, h), new Vector3(h, -h, h), Vector3.right);
            m.SetVertices(v); m.SetNormals(n); m.SetUVs(0, uv); m.SetUVs(1, uv);
            m.SetColors(System.Linq.Enumerable.Repeat(Color.white, v.Count).ToList());
            m.SetTriangles(t, 0);
            m.RecalculateTangents(); m.RecalculateBounds();
            boxMesh = m;
            return m;
        }

        // The wheat crop's own material, so the heap matches the game's grain; a plain straw colour if it is not found.
        private static Material GrainMaterial()
        {
            if (grainMaterial != null) return grainMaterial;
            try
            {
                var wheat = Resources.FindObjectsOfTypeAll<MeshFilter>().FirstOrDefault(m => m.sharedMesh != null && m.sharedMesh.name.StartsWith("wheat_var") && m.GetComponent<MeshRenderer>() != null && m.GetComponent<MeshRenderer>().sharedMaterial != null);
                if (wheat != null) { grainMaterial = wheat.GetComponent<MeshRenderer>().sharedMaterial; Plugin.Log.LogInfo($"Tailor: trough grain uses material '{grainMaterial.name}' from {wheat.sharedMesh.name}."); return grainMaterial; }
            }
            catch { }
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            grainMaterial = new Material(shader) { color = new Color(0.86f, 0.70f, 0.30f) };
            Plugin.Log.LogInfo("Tailor: trough grain uses a plain straw colour (no wheat material found).");
            return grainMaterial;
        }
    }
}
