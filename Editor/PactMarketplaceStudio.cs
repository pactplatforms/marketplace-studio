#if UNITY_EDITOR

using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Pact.Marketplace
{
    public class PactMarketplaceStudio : EditorWindow
    {
        // ─────────────────────────────────────
        // CONFIG
        // ─────────────────────────────────────

        private const string LAMBDA_URL =
            "https://73cy1palri.execute-api.us-east-1.amazonaws.com/default/pact-generate_presigned_upload";

        // MOBILE-FIRST CONSTRAINTS
        private const int MAX_TRIANGLES      = 100000;
        private const int MAX_MATERIALS      = 10;
        private const int MAX_TEXTURE_SIZE   = 2048;
        private const float MAX_BUNDLE_SIZE_MB = 50f;

        // TEXTURE QUALITY
        private const TextureImporterFormat DEFAULT_ASTC_FORMAT =
            TextureImporterFormat.ASTC_6x6;

        private const TextureImporterFormat NORMAL_ASTC_FORMAT =
            TextureImporterFormat.ASTC_4x4;

        // ─────────────────────────────────────
        // UI
        // ─────────────────────────────────────

        private readonly string[] categories =
        {
            "Art",
            "Fashion",
            "Furniture",
            "Architecture",
            "Gaming",
            "Animation",
            "Characters",
            "Environment",
            "Vehicles",
            "Weapons",
            "Animals",
            "Vegetation"
        };

        private string assetId     = "my_asset";
        private string userEmail   = "";
        private string creatorName = "";

        private int categoryIndex = 0;

        private GameObject targetPrefab;

        private string status = "Ready";

        // ─────────────────────────────────────
        // MENU
        // ─────────────────────────────────────

        [MenuItem("Pact/Marketplace Studio")]
        public static void ShowWindow()
        {
            GetWindow<PactMarketplaceStudio>(
                "Marketplace Studio"
            );
        }

        // ─────────────────────────────────────
        // GUI
        // ─────────────────────────────────────

        private void OnGUI()
        {
            GUILayout.Space(8);

            GUILayout.Label(
                "PACT Marketplace Publisher",
                EditorStyles.boldLabel
            );

            GUILayout.Space(5);

            targetPrefab =
                (GameObject)EditorGUILayout.ObjectField(
                    "3D Model",
                    targetPrefab,
                    typeof(GameObject),
                    false
                );

            assetId =
                EditorGUILayout.TextField(
                    "Asset ID",
                    assetId
                );

            userEmail =
                EditorGUILayout.TextField(
                    "Creator Email",
                    userEmail
                );

            creatorName =
                EditorGUILayout.TextField(
                    "Creator Name",
                    creatorName
                );

            categoryIndex =
                EditorGUILayout.Popup(
                    "Category",
                    categoryIndex,
                    categories
                );

            GUILayout.Space(8);

            EditorGUILayout.HelpBox(
                $"Marketplace Limits\n\n" +
                $"Triangles: {MAX_TRIANGLES:N0}\n" +
                $"Materials: {MAX_MATERIALS}\n" +
                $"Texture Size: {MAX_TEXTURE_SIZE}\n" +
                $"Bundle Size: {MAX_BUNDLE_SIZE_MB}MB",
                MessageType.Info
            );

            GUILayout.Space(6);

            if (GUILayout.Button(
                "BUILD & PUBLISH",
                GUILayout.Height(42)))
            {
                if (targetPrefab != null &&
                    userEmail.Contains("@"))
                {
                    _ = BuildAndPublish();
                }
                else
                {
                    EditorUtility.DisplayDialog(
                        "Missing Info",
                        "Please select a prefab and valid email.",
                        "OK"
                    );
                }
            }

            GUILayout.Space(10);

            EditorGUILayout.HelpBox(
                $"Status: {status}",
                MessageType.None
            );
        }

        // ─────────────────────────────────────
        // MAIN PIPELINE
        // ─────────────────────────────────────

        private async Task BuildAndPublish()
        {
            Dictionary<string, CachedTextureState> cachedStates =
                new Dictionary<string, CachedTextureState>();

            try
            {
                string cleanId =
                    assetId
                    .ToLower()
                    .Trim()
                    .Replace(" ", "_");

                // VALIDATION
                status = "Validating asset...";
                Repaint();

                int triangleCount =
                    CountTriangles(targetPrefab);

                if (triangleCount > MAX_TRIANGLES)
                {
                    status =
                        $"Too many triangles ({triangleCount:N0}).";

                    Debug.LogError(status);

                    return;
                }

                int materialCount =
                    CountMaterials(targetPrefab);

                if (materialCount > MAX_MATERIALS)
                {
                    status =
                        $"Too many materials ({materialCount}).";

                    Debug.LogError(status);

                    return;
                }

                // TEMPORARY EXPORT OPTIMIZATION
                status = "Optimizing textures...";
                Repaint();

                cachedStates =
                    OptimizeTexturesForExport();

                // THUMBNAIL
                status = "Rendering thumbnail...";
                Repaint();

                byte[] thumbBytes =
                    CaptureThumbnail(targetPrefab);

                // BUILD
                status = "Building AssetBundle...";
                Repaint();

                string buildPath =
                    Path.Combine(
                        Application.temporaryCachePath,
                        "PactTemp"
                    );

                if (Directory.Exists(buildPath))
                {
                    Directory.Delete(buildPath, true);
                }

                Directory.CreateDirectory(buildPath);

                AssetBundleBuild[] builds =
                    new AssetBundleBuild[1];

                builds[0].assetBundleName =
                    "asset.unitybundle";

                builds[0].assetNames =
                    new[]
                    {
                        AssetDatabase.GetAssetPath(
                            targetPrefab
                        )
                    };

                BuildPipeline.BuildAssetBundles(
                buildPath,
                builds,

                BuildAssetBundleOptions
                    .DisableWriteTypeTree |

                BuildAssetBundleOptions
                    .ChunkBasedCompression |

                BuildAssetBundleOptions
                    .ForceRebuildAssetBundle,

                BuildTarget.iOS
            );
                string bundlePath =
                    Path.Combine(
                        buildPath,
                        "asset.unitybundle"
                    );

                if (!File.Exists(bundlePath))
                {
                    throw new Exception(
                        "Bundle build failed."
                    );
                }

                byte[] bundleBytes =
                    File.ReadAllBytes(bundlePath);

                float bundleSizeMB =
                    bundleBytes.Length /
                    1024f /
                    1024f;

                if (bundleSizeMB >
                    MAX_BUNDLE_SIZE_MB)
                {
                    throw new Exception(
                        $"Bundle too large ({bundleSizeMB:F2}MB)"
                    );
                }

                // REQUEST URLS
                status = "Requesting upload URLs...";
                Repaint();

                string jsonRes =
                    await PostRequest(
                        LAMBDA_URL,

                        JsonUtility.ToJson(
                            new RequestPayload
                            {
                                assetId = cleanId,
                                email = userEmail,
                                creatorName = creatorName
                            }
                        )
                    );

                var res =
                    JsonUtility.FromJson<Response>(
                        jsonRes
                    );

                if (res == null ||
                    string.IsNullOrEmpty(res.bundleUrl))
                {
                    throw new Exception(
                        "Lambda response invalid."
                    );
                }

                // UPLOAD BUNDLE
                status = "Uploading bundle...";
                Repaint();

                await UploadFile(
                    res.bundleUrl,
                    bundleBytes,
                    "application/octet-stream"
                );

                // UPLOAD THUMB
                if (!string.IsNullOrEmpty(
                    res.thumbnailUrl))
                {
                    status = "Uploading thumbnail...";
                    Repaint();

                    await UploadFile(
                        res.thumbnailUrl,
                        thumbBytes,
                        "image/png"
                    );
                }

                // METADATA
                status = "Uploading metadata...";
                Repaint();

                Metadata meta =
                    new Metadata
                    {
                        Token = res.token,
                        AssetID = cleanId,
                        Email = userEmail,
                        CreatorName = creatorName,
                        Category =
                            categories[categoryIndex],

                        TriangleCount =
                            triangleCount,

                        MaterialCount =
                            materialCount
                    };

                await UploadFile(
                    res.jsonUrl,

                    Encoding.UTF8.GetBytes(
                        JsonUtility.ToJson(meta)
                    ),

                    "application/json"
                );

                status =
                    $"SUCCESS — {cleanId} uploaded.";

                Repaint();

                Debug.Log(
                    $"[PACT] Published {cleanId} | " +
                    $"{triangleCount:N0} tris | " +
                    $"{materialCount} mats | " +
                    $"{bundleSizeMB:F2}MB"
                );

                EditorUtility.DisplayDialog(
                    "Upload Complete",

                    $"'{cleanId}' uploaded successfully.",

                    "OK"
                );
            }
            catch (Exception e)
            {
                status = "Error: " + e.Message;

                Debug.LogError(e);

                Repaint();
            }
            finally
            {
                RestoreTextureSettings(cachedStates);
            }
        }

        // ─────────────────────────────────────
        // TRIANGLES
        // ─────────────────────────────────────

        private int CountTriangles(GameObject prefab)
        {
            int total = 0;

            foreach (MeshFilter mf in
                prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh != null)
                {
                    total +=
                        mf.sharedMesh.triangles.Length / 3;
                }
            }

            foreach (SkinnedMeshRenderer smr in
                prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh != null)
                {
                    total +=
                        smr.sharedMesh.triangles.Length / 3;
                }
            }

            return total;
        }

        // ─────────────────────────────────────
        // MATERIALS
        // ─────────────────────────────────────

        private int CountMaterials(GameObject prefab)
        {
            int total = 0;

            Renderer[] renderers =
                prefab.GetComponentsInChildren<Renderer>(true);

            foreach (Renderer r in renderers)
            {
                total +=
                    r.sharedMaterials.Length;
            }

            return total;
        }

        // ─────────────────────────────────────
        // TEXTURE EXPORT OPTIMIZATION
        // ─────────────────────────────────────

        private Dictionary<string, CachedTextureState>
            OptimizeTexturesForExport()
        {
            Dictionary<string, CachedTextureState> cache =
                new Dictionary<string, CachedTextureState>();

            Renderer[] renderers =
                targetPrefab.GetComponentsInChildren<Renderer>(true);

            foreach (Renderer r in renderers)
            {
                foreach (Material mat in r.sharedMaterials)
                {
                    if (mat == null)
                        continue;

                    foreach (string prop in
                        mat.GetTexturePropertyNames())
                    {
                        Texture tex =
                            mat.GetTexture(prop);

                        if (tex == null)
                            continue;

                        string path =
                            AssetDatabase.GetAssetPath(tex);

                        if (cache.ContainsKey(path))
                            continue;

                        TextureImporter importer =
                            AssetImporter.GetAtPath(path)
                            as TextureImporter;

                        if (importer == null)
                            continue;

                        // CACHE ORIGINALS
                        cache[path] =
                            new CachedTextureState
                            {
                                maxTextureSize =
                                    importer.maxTextureSize,

                                textureCompression =
                                    importer.textureCompression,

                                mipmaps =
                                    importer.mipmapEnabled,

                                textureType =
                                    importer.textureType,

                                sRGB =
                                    importer.sRGBTexture,

                                iosSettings =
                                    importer.GetPlatformTextureSettings("iPhone"),

                                androidSettings =
                                    importer.GetPlatformTextureSettings("Android")
                            };

                        bool isNormalMap =
                            prop.ToLower().Contains("normal") ||
                            prop.ToLower().Contains("bump") ||
                            importer.textureType ==
                            TextureImporterType.NormalMap;

                        bool isDataMap =
                            prop.ToLower().Contains("metallic") ||
                            prop.ToLower().Contains("roughness") ||
                            prop.ToLower().Contains("mask");

                        TextureImporterFormat targetFormat =
                            isNormalMap
                            ? NORMAL_ASTC_FORMAT
                            : DEFAULT_ASTC_FORMAT;

                        importer.maxTextureSize =
                            Mathf.Min(
                                importer.maxTextureSize,
                                MAX_TEXTURE_SIZE
                            );

                        importer.mipmapEnabled = true;

                        // COLOR SPACE CORRECTIONS
                        if (isNormalMap)
                        {
                            importer.textureType =
                                TextureImporterType.NormalMap;

                            importer.sRGBTexture = false;
                        }
                        else if (isDataMap)
                        {
                            importer.sRGBTexture = false;
                        }
                        else
                        {
                            importer.sRGBTexture = true;
                        }

                        // IOS SETTINGS
                        TextureImporterPlatformSettings ios =
                            new TextureImporterPlatformSettings
                            {
                                name = "iPhone",
                                overridden = true,

                                maxTextureSize =
                                    MAX_TEXTURE_SIZE,

                                format =
                                    targetFormat,

                                compressionQuality =
                                    (int)TextureCompressionQuality.Normal
                            };

                        importer.SetPlatformTextureSettings(
                            ios
                        );

                        importer.SaveAndReimport();
                    }
                }
            }

            return cache;
        }

        // ─────────────────────────────────────
        // RESTORE ORIGINAL IMPORT SETTINGS
        // ─────────────────────────────────────

        private void RestoreTextureSettings(
            Dictionary<string, CachedTextureState> cache)
        {
            foreach (var kvp in cache)
            {
                string path = kvp.Key;

                CachedTextureState c =
                    kvp.Value;

                TextureImporter importer =
                    AssetImporter.GetAtPath(path)
                    as TextureImporter;

                if (importer == null)
                    continue;

                importer.maxTextureSize =
                    c.maxTextureSize;

                importer.textureCompression =
                    c.textureCompression;

                importer.mipmapEnabled =
                    c.mipmaps;

                importer.textureType =
                    c.textureType;

                importer.sRGBTexture =
                    c.sRGB;

                importer.SetPlatformTextureSettings(
                    c.iosSettings
                );

                importer.SetPlatformTextureSettings(
                    c.androidSettings
                );

                importer.SaveAndReimport();
            }
        }

        // ─────────────────────────────────────
        // THUMBNAIL
        // ─────────────────────────────────────

        private byte[] CaptureThumbnail(
            GameObject prefab)
        {
            const int SIZE = 512;

            GameObject root =
                new GameObject("ThumbRoot");

            GameObject p =
                Instantiate(
                    prefab,
                    Vector3.zero,
                    Quaternion.identity,
                    root.transform
                );

            SetLayerRecursively(p, 31);

            Renderer[] rs =
                p.GetComponentsInChildren<Renderer>();

            if (rs.Length == 0)
            {
                DestroyImmediate(root);
                return new byte[0];
            }

            Bounds b = rs[0].bounds;

            foreach (Renderer r in rs)
            {
                b.Encapsulate(r.bounds);
            }

            GameObject camObj =
                new GameObject("ThumbCam");

            Camera cam =
                camObj.AddComponent<Camera>();

            cam.backgroundColor =
                new Color(0, 0, 0, 0);

            cam.clearFlags =
                CameraClearFlags.SolidColor;

            cam.cullingMask =
                1 << 31;

            float rad =
                b.extents.magnitude;

            cam.transform.position =
                b.center +
                new Vector3(-1, 0.6f, -1)
                .normalized *
                (rad * 2.2f);

            cam.transform.LookAt(b.center);

            RenderTexture rt =
                new RenderTexture(
                    SIZE,
                    SIZE,
                    24
                );

            cam.targetTexture = rt;

            RenderTexture.active = rt;

            Texture2D tex =
                new Texture2D(
                    SIZE,
                    SIZE,
                    TextureFormat.RGBA32,
                    false
                );

            cam.Render();

            tex.ReadPixels(
                new Rect(0, 0, SIZE, SIZE),
                0,
                0
            );

            tex.Apply();

            byte[] bytes =
                tex.EncodeToPNG();

            cam.targetTexture = null;
            RenderTexture.active = null;

            DestroyImmediate(rt);
            DestroyImmediate(tex);
            DestroyImmediate(camObj);
            DestroyImmediate(root);

            return bytes;
        }

        // ─────────────────────────────────────
        // LAYERS
        // ─────────────────────────────────────

        private void SetLayerRecursively(
            GameObject o,
            int l)
        {
            o.layer = l;

            foreach (Transform t in o.transform)
            {
                SetLayerRecursively(
                    t.gameObject,
                    l
                );
            }
        }

        // ─────────────────────────────────────
        // HTTP
        // ─────────────────────────────────────

        private async Task<string> PostRequest(
            string url,
            string json)
        {
            using UnityWebRequest r =
                new UnityWebRequest(
                    url,
                    "POST"
                );

            r.uploadHandler =
                new UploadHandlerRaw(
                    Encoding.UTF8.GetBytes(json)
                );

            r.downloadHandler =
                new DownloadHandlerBuffer();

            r.SetRequestHeader(
                "Content-Type",
                "application/json"
            );

            await r.SendWebRequest();

            if (r.result !=
                UnityWebRequest.Result.Success)
            {
                throw new Exception(r.error);
            }

            return r.downloadHandler.text;
        }

        private async Task UploadFile(
            string url,
            byte[] data,
            string ct)
        {
            if (string.IsNullOrEmpty(url) ||
                data == null ||
                data.Length == 0)
            {
                return;
            }

            using UnityWebRequest r =
                UnityWebRequest.Put(
                    url,
                    data
                );

            r.SetRequestHeader(
                "Content-Type",
                ct
            );

            await r.SendWebRequest();

            if (r.result !=
                UnityWebRequest.Result.Success)
            {
                throw new Exception(r.error);
            }
        }

        // ─────────────────────────────────────
        // DATA
        // ─────────────────────────────────────

        [Serializable]
        class RequestPayload
        {
            public string assetId;
            public string email;
            public string creatorName;
        }

        [Serializable]
        class Response
        {
            public string bundleUrl;
            public string thumbnailUrl;
            public string jsonUrl;
            public string token;
        }

        [Serializable]
        class Metadata
        {
            public string Token;
            public string AssetID;
            public string Email;
            public string CreatorName;
            public string Category;

            public int TriangleCount;
            public int MaterialCount;
        }

        class CachedTextureState
        {
            public int maxTextureSize;

            public bool mipmaps;

            public bool sRGB;

            public TextureImporterType textureType;

            public TextureImporterCompression textureCompression;

            public TextureImporterPlatformSettings iosSettings;

            public TextureImporterPlatformSettings androidSettings;
        }
    }
}

#endif