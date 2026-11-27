#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Seamstress.EditorTools
{
    /// <summary>
    /// Configures imported Seamstress garment FBX files and points their material slots at the
    /// generated .mat assets. The same helper adapts those materials to Built-in, URP or HDRP.
    /// It lives under Assets/Seamstress so it only affects assets exported by Seamstress.
    /// </summary>
    public sealed class SeamstressGarmentImport : AssetPostprocessor
    {
        private const string Root = "Assets/Seamstress";
        private const string RootPrefix = Root + "/";
        private const string ClothShader = "Seamstress/Cloth";
        private const string ClothTransparentShader = "Seamstress/Cloth Transparent";
        private const string UrpLitShader = "Universal Render Pipeline/Lit";
        private const string HdrpLitShader = "HDRP/Lit";
        private const string ManifestName = "seamstress-garment.json";

        private static readonly HashSet<string> PendingModels = new HashSet<string>();
        private static readonly HashSet<string> PendingMaterials = new HashSet<string>();
        private static readonly HashSet<string> RepairingModels = new HashSet<string>();
        private static bool _scheduled;

        [InitializeOnLoadMethod]
        private static void OnEditorLoad()
        {
            EditorApplication.delayCall += ScanExistingAssets;
        }

        [MenuItem("Tools/Seamstress/Reimport Garment Assets")]
        public static void ReimportGarmentAssets()
        {
            ScanExistingAssets();
            Flush();
        }

        private void OnPreprocessModel()
        {
            if (!IsUnderRoot(assetPath))
                return;
            var importer = assetImporter as ModelImporter;
            if (importer == null)
                return;
            Configure(importer);
            ScheduleModel(assetPath);
        }

        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
            string[] movedAssets, string[] movedFromAssetPaths)
        {
            bool any = false;
            foreach (string path in importedAssets.Concat(movedAssets))
            {
                if (!IsUnderRoot(path))
                    continue;
                if (path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                {
                    ScheduleModel(path);
                    any = true;
                }
                else if (path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                {
                    ScheduleMaterial(path);
                    any = true;
                }
            }
            if (any)
                EnsureScheduled();
        }

        private static void ScanExistingAssets()
        {
            if (!AssetDatabase.IsValidFolder(Root))
                return;
            foreach (string guid in AssetDatabase.FindAssets("", new[] { Root }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                    ScheduleModel(path);
                else if (path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                    ScheduleMaterial(path);
            }
            EnsureScheduled();
        }

        private static bool IsUnderRoot(string path)
        {
            return path != null && path.StartsWith(RootPrefix, StringComparison.Ordinal);
        }

        private static void ScheduleModel(string path)
        {
            PendingModels.Add(path);
            EnsureScheduled();
        }

        private static void ScheduleMaterial(string path)
        {
            PendingMaterials.Add(path);
            EnsureScheduled();
        }

        private static void EnsureScheduled()
        {
            if (_scheduled)
                return;
            _scheduled = true;
            EditorApplication.delayCall += Flush;
        }

        private static void Flush()
        {
            _scheduled = false;
            string[] materials = PendingMaterials.ToArray();
            string[] models = PendingModels.ToArray();
            PendingMaterials.Clear();
            PendingModels.Clear();

            foreach (string path in materials)
                AdaptMaterial(path, null);
            AssetDatabase.SaveAssets();
            foreach (string path in models)
                RepairModel(path);
            AssetDatabase.SaveAssets();
        }

        private static void RepairModel(string path)
        {
            if (!RepairingModels.Add(path))
                return;
            try
            {
                var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                if (importer == null)
                    return;
                bool changed = Configure(importer);
                Dictionary<AssetImporter.SourceAssetIdentifier, UnityEngine.Object> map = importer.GetExternalObjectMap();
                string folder = (Path.GetDirectoryName(path) ?? "").Replace('\\', '/');
                string materialFolder = folder + "/Materials";
                Dictionary<string, MaterialEntry> entries = LoadManifestEntries(folder);
                if (AssetDatabase.IsValidFolder(materialFolder))
                {
                    foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { materialFolder }))
                    {
                        string materialPath = AssetDatabase.GUIDToAssetPath(guid);
                        var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                        if (material == null)
                            continue;
                        MaterialEntry entry;
                        if (entries.TryGetValue(material.name, out entry))
                            AdaptMaterial(materialPath, entry);
                        var identifier = new AssetImporter.SourceAssetIdentifier(typeof(Material), material.name);
                        UnityEngine.Object current;
                        if (!map.TryGetValue(identifier, out current) || current != material)
                        {
                            importer.AddRemap(identifier, material);
                            changed = true;
                        }
                    }
                }
                if (changed)
                    importer.SaveAndReimport();
            }
            finally
            {
                RepairingModels.Remove(path);
            }
        }

        private static bool Configure(ModelImporter importer)
        {
            bool changed = false;
            if (!importer.useFileUnits) { importer.useFileUnits = true; changed = true; }
            if (!importer.useFileScale) { importer.useFileScale = true; changed = true; }
            if (Mathf.Abs(importer.globalScale - 1f) > 0.0001f) { importer.globalScale = 1f; changed = true; }
            if (importer.importNormals != ModelImporterNormals.Import) { importer.importNormals = ModelImporterNormals.Import; changed = true; }
            if (importer.importTangents != ModelImporterTangents.CalculateMikk) { importer.importTangents = ModelImporterTangents.CalculateMikk; changed = true; }
            if (importer.materialImportMode != ModelImporterMaterialImportMode.ImportStandard) { importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard; changed = true; }
            // Unity 6 marks this obsolete, but the setting still selects external materials there.
            var materialLocation = typeof(ModelImporter).GetProperty("materialLocation");
            if (materialLocation != null)
            {
                object external = Enum.Parse(materialLocation.PropertyType, "External");
                if (!Equals(materialLocation.GetValue(importer, null), external))
                {
                    materialLocation.SetValue(importer, external, null);
                    changed = true;
                }
            }
            if (importer.materialSearch != ModelImporterMaterialSearch.RecursiveUp) { importer.materialSearch = ModelImporterMaterialSearch.RecursiveUp; changed = true; }
            if (importer.materialName != ModelImporterMaterialName.BasedOnMaterialName) { importer.materialName = ModelImporterMaterialName.BasedOnMaterialName; changed = true; }
            if (importer.meshCompression != ModelImporterMeshCompression.Off) { importer.meshCompression = ModelImporterMeshCompression.Off; changed = true; }
            if (importer.isReadable) { importer.isReadable = false; changed = true; }
            return changed;
        }

        private static Dictionary<string, MaterialEntry> LoadManifestEntries(string bundleFolder)
        {
            var result = new Dictionary<string, MaterialEntry>(StringComparer.Ordinal);
            string path = bundleFolder + "/" + ManifestName;
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (asset == null)
                return result;
            GarmentManifest manifest;
            try
            {
                manifest = JsonUtility.FromJson<GarmentManifest>(asset.text);
            }
            catch
            {
                return result;
            }
            if (manifest == null || manifest.materials == null)
                return result;
            foreach (MaterialEntry entry in manifest.materials)
            {
                if (entry != null && !string.IsNullOrEmpty(entry.name))
                    result[entry.name] = entry;
            }
            return result;
        }

        private static void AdaptMaterial(string path, MaterialEntry entry)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
                return;

            string materialFolder = (Path.GetDirectoryName(path) ?? "").Replace('\\', '/');
            string bundleFolder = (Path.GetDirectoryName(materialFolder) ?? "").Replace('\\', '/');

            Color color = material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor")
                        : material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white;
            Texture texture = material.HasProperty("_BaseMap") ? material.GetTexture("_BaseMap")
                            : material.HasProperty("_MainTex") ? material.GetTexture("_MainTex") : null;
            Vector2 scale = Vector2.one;
            if (material.HasProperty("_BaseMap"))
                scale = material.GetTextureScale("_BaseMap");
            else if (material.HasProperty("_MainTex"))
                scale = material.GetTextureScale("_MainTex");
            float metallic = material.HasProperty("_Metallic") ? material.GetFloat("_Metallic") : 0f;
            float smoothness = material.HasProperty("_Smoothness") ? material.GetFloat("_Smoothness")
                             : material.HasProperty("_Glossiness") ? material.GetFloat("_Glossiness") : 0.5f;

            if (entry != null)
            {
                color = new Color(entry.r, entry.g, entry.b, entry.opacity);
                texture = null;
                if (!string.IsNullOrEmpty(entry.texture))
                    texture = AssetDatabase.LoadAssetAtPath<Texture2D>((bundleFolder + "/" + entry.texture).Replace('\\', '/'));
                float repeat = entry.repeat <= 0.0001f ? 1f : entry.repeat;
                scale = new Vector2(repeat, repeat);
                metallic = entry.metallic;
                smoothness = 1f - entry.roughness;
            }

            bool transparent = color.a < 0.999f;

            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            if (pipeline == null)
            {
                Shader shader = Shader.Find(transparent ? ClothTransparentShader : ClothShader);
                if (shader == null)
                    return;
                material.shader = shader;
                SetColor(material, "_Color", color);
                SetTexture(material, "_MainTex", texture, scale);
                SetFloat(material, "_Metallic", metallic);
                SetFloat(material, "_Glossiness", smoothness);
                material.renderQueue = -1;
            }
            else if (pipeline.GetType().Name.IndexOf("HDRenderPipeline", StringComparison.Ordinal) >= 0)
            {
                Shader shader = Shader.Find(HdrpLitShader);
                if (shader == null)
                    return;
                material.shader = shader;
                SetColor(material, "_BaseColor", color);
                SetTexture(material, "_BaseColorMap", texture, scale);
                SetFloat(material, "_Metallic", metallic);
                SetFloat(material, "_Smoothness", smoothness);
                SetFloat(material, "_DoubleSidedEnable", 1f);
                material.SetOverrideTag("RenderType", transparent ? "Transparent" : "Opaque");
                if (transparent)
                {
                    SetFloat(material, "_SurfaceType", 1f);
                    SetFloat(material, "_BlendMode", 0f);
                    material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    material.EnableKeyword("_BLENDMODE_ALPHA");
                    material.renderQueue = (int)RenderQueue.Transparent;
                }
                else
                {
                    SetFloat(material, "_SurfaceType", 0f);
                    material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    material.DisableKeyword("_BLENDMODE_ALPHA");
                    material.renderQueue = -1;
                }
            }
            else
            {
                Shader shader = Shader.Find(UrpLitShader);
                if (shader == null)
                    return;
                material.shader = shader;
                SetColor(material, "_BaseColor", color);
                SetTexture(material, "_BaseMap", texture, scale);
                SetFloat(material, "_Metallic", metallic);
                SetFloat(material, "_Smoothness", smoothness);
                SetFloat(material, "_Cull", 0f);
                if (transparent)
                {
                    SetFloat(material, "_Surface", 1f);
                    SetFloat(material, "_Blend", 0f);
                    SetFloat(material, "_SrcBlend", (float)BlendMode.SrcAlpha);
                    SetFloat(material, "_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                    SetFloat(material, "_ZWrite", 0f);
                    material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    material.SetOverrideTag("RenderType", "Transparent");
                    material.renderQueue = (int)RenderQueue.Transparent;
                }
                else
                {
                    SetFloat(material, "_Surface", 0f);
                    SetFloat(material, "_ZWrite", 1f);
                    material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    material.SetOverrideTag("RenderType", "Opaque");
                    material.renderQueue = -1;
                }
            }

            EditorUtility.SetDirty(material);
        }

        private static void SetColor(Material material, string property, Color value)
        {
            if (material.HasProperty(property))
                material.SetColor(property, value);
        }

        private static void SetFloat(Material material, string property, float value)
        {
            if (material.HasProperty(property))
                material.SetFloat(property, value);
        }

        private static void SetTexture(Material material, string property, Texture texture, Vector2 scale)
        {
            if (!material.HasProperty(property))
                return;
            material.SetTexture(property, texture);
            material.SetTextureScale(property, scale);
        }

        [Serializable]
        private sealed class GarmentManifest
        {
            public MaterialEntry[] materials;
        }

        [Serializable]
        private sealed class MaterialEntry
        {
            public string name;
            public string texture;
            public float repeat = 1f;
            public float opacity = 1f;
            public float metallic;
            public float roughness = 0.8f;
            public float r = 0.8f;
            public float g = 0.8f;
            public float b = 0.8f;
        }
    }
}
#endif
