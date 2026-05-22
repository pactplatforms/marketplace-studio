#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System;

namespace Pact.Marketplace
{
    public class PactMarketplaceStudio : EditorWindow
    {
        private const string LAMBDA_URL =
            "https://73cy1palri.execute-api.us-east-1.amazonaws.com/default/pact-generate_presigned_upload";

        // MOBILE-FIRST MARKETPLACE LIMITS
        private const int   MAX_TRIANGLES      = 100000;
        private const int   MAX_TEXTURE_SIZE   = 2048;
        private const int   MAX_MATERIALS      = 10;
        private const float MAX_BUNDLE_SIZE_MB = 50f;

        private string[] categories =
        {
            "Art", "Fashion", "Furniture", "Architecture", "Gaming",
            "Animation", "Characters", "Environment", "Vehicles",
            "Weapons", "Animals", "Vegetation"
        };

        private string     assetId       = "my_asset";
        private string     userEmail     = "";
        private string     creatorName   = "";
        private int        categoryIndex = 0;

        private GameObject targetPrefab;

        private string status = "Ready";

        [MenuItem("Pact/Marketplace Studio")]
        public static void ShowWindow()
        {
            GetWindow<PactMarketplaceStudio>(
                "Marketplace Studio"
            );
        }

        private void OnGUI()
        {
            GUILayout.Label(
                "PACT Marketplace Publisher",
                EditorStyles.boldLabel
            );

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

            EditorGUILayout.HelpBox(
                $"Limits:\n" +
                $"- Max Triangles: {MAX_TRIANGLES:N0}\n" +
                $"- Max Materials: {MAX_MATERIALS}\n" +
                $"- Max Texture Size: {MAX_TEXTURE_SIZE}\n" +
                $"- Max Bundle Size: {MAX_BUNDLE_SIZE_MB}MB",
                MessageType.Info
            );

            if (GUILayout.Button(
                "BUILD & PUBLISH",
                GUILayout.Height(40)))
            {
                if (targetPrefab != null &&
                    userEmail.Contains("@"))
                {
                    _ = BuildAndPublish();
                }
                else
                {
                    EditorUtility.DisplayDialog(
                        "Error",
                        "Check prefab and email",
                        "OK"
                    );
                }
            }

            EditorGUILayout.HelpBox(
                $"Status: {status}",
                MessageType.Info
            );
        }

        private async Task BuildAndPublish()
        {
            try
            {
                string cleanId =
                    assetId
                    .ToLower()
                    .Trim()
                    .Replace(" ", "_");

                // VALIDATION
                status = "Validating...";
                Repaint();

                int triangleCount =
                    CountTriangles(targetPrefab);

                if (triangleCount > MAX_TRIANGLES)
                {
                    status =
                        $"Too many triangles ({triangleCount:N0}). " +
                        $"Max is {MAX_TRIANGLES:N0}.";

                    Debug.LogError(status);

                    return;
                }

                int materialCount =
                    CountMaterials(targetPrefab);

                if (materialCount > MAX_MATERIALS)
                {
                    status =
                        $"Too many materials ({materialCount}). " +
                        $"Max is {MAX_MATERIALS}.";

                    Debug.LogError(status);

                    return;
                }

                // TEXTURE OPTIMIZATION
                status = "Optimizing textures...";
                Repaint();

                OptimizeTextures();

                // THUMBNAIL
                status = "Rendering thumbnail...";
                Repaint();

                byte[] thumbBytes =
                    CaptureThumbnail(targetPrefab);

                // BUILD BUNDLE
                status = "Building bundle...";
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
                    status =
                        "Bundle build failed. " +
                        "Check Console.";

                    return;
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
                    status =
                        $"Bundle too large ({bundleSizeMB:F2}MB). " +
                        $"Max is {MAX_BUNDLE_SIZE_MB}MB.";

                    Debug.LogError(status);

                    return;
                }

                // REQUEST PRESIGNED URLS
                status = "Requesting upload access...";
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

                Debug.Log(
                    "[PACT] Lambda response: " +
                    jsonRes
                );

                var res =
                    JsonUtility.FromJson<Response>(
                        jsonRes
                    );

                if (res == null ||
                    string.IsNullOrEmpty(res.bundleUrl))
                {
                    status =
                        "Lambda did not return upload URLs.";

                    Debug.LogError(
                        "[PACT] Lambda response: " +
                        jsonRes
                    );

                    return;
                }

                // UPLOAD BUNDLE
                status = "Uploading bundle...";
                Repaint();

                await UploadFile(
                    res.bundleUrl,
                    bundleBytes,
                    "application/octet-stream"
                );

                // UPLOAD THUMBNAIL
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

                // UPLOAD METADATA
                status = "Uploading metadata...";
                Repaint();

                await UploadFile(
                    res.jsonUrl,

                    Encoding.UTF8.GetBytes(
                        JsonUtility.ToJson(
                            new Metadata
                            {
                                Token = res.token,
                                AssetID = cleanId,
                                Email = userEmail,
                                CreatorName = creatorName,
                                Category =
                                    categories[categoryIndex],

                                TriangleCount =
                                    triangleCount
                            }
                        )
                    ),

                    "application/json"
                );

                status =
                    $"SUCCESS — check {userEmail} " +
                    $"for verification.";

                Repaint();

                Debug.Log(
                    $"[PACT] Published {cleanId} | " +
                    $"{triangleCount:N0} tris | " +
                    $"{materialCount} mats | " +
                    $"{bundleSizeMB:F2}MB"
                );

                EditorUtility.DisplayDialog(
                    "PACT Marketplace Studio",

                    $"Asset '{cleanId}' uploaded.\n" +
                    $"Check {userEmail} for the verification email.",

                    "OK"
                );
            }
            catch (Exception e)
            {
                status = "Error: " + e.Message;

                Debug.LogError(e);

                Repaint();
            }
        }

        // ─────────────────────────────────────
        // VALIDATION
        // ─────────────────────────────────────

        private int CountTriangles(GameObject prefab)
        {
            int total = 0;

            // STATIC MESHES
            foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh != null)
                {
                    total +=
                        mf.sharedMesh.triangles.Length / 3;
                }
            }

            // SKINNED MESHES
            foreach (var smr in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh != null)
                {
                    total +=
                        smr.sharedMesh.triangles.Length / 3;
                }
            }

            return total;
        }

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
        // TEXTURE OPTIMIZATION
        // ─────────────────────────────────────

        private void OptimizeTextures()
        {
            if (targetPrefab == null)
                return;

            Renderer[] renderers =
                targetPrefab.GetComponentsInChildren<Renderer>(true);

            foreach (Renderer r in renderers)
            {
                foreach (Material mat in r.sharedMaterials)
                {
                    if (mat == null)
                        continue;

                    string[] textureProps =
                        mat.GetTexturePropertyNames();

                    foreach (string prop in textureProps)
                    {
                        Texture tex =
                            mat.GetTexture(prop);

                        if (tex == null)
                            continue;

                        string path =
                            AssetDatabase.GetAssetPath(tex);

                        TextureImporter importer =
                            AssetImporter.GetAtPath(path)
                            as TextureImporter;

                        if (importer == null)
                            continue;

                        bool changed = false;

                        if (importer.maxTextureSize >
                            MAX_TEXTURE_SIZE)
                        {
                            importer.maxTextureSize =
                                MAX_TEXTURE_SIZE;

                            changed = true;
                        }

                        if (importer.textureCompression !=
                            TextureImporterCompression.Compressed)
                        {
                            importer.textureCompression =
                                TextureImporterCompression.Compressed;

                            changed = true;
                        }

                        if (!importer.mipmapEnabled)
                        {
                            importer.mipmapEnabled = true;
                            changed = true;
                        }

                        if (changed)
                        {
                            importer.SaveAndReimport();
                        }
                    }
                }
            }
        }

        // ─────────────────────────────────────
        // THUMBNAIL
        // ─────────────────────────────────────

        private byte[] CaptureThumbnail(GameObject prefab)
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
        // NETWORKING
        // ─────────────────────────────────────

        private async Task<string> PostRequest(
            string url,
            string json)
        {
            using var r =
                new UnityWebRequest(url, "POST");

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
                Debug.LogWarning(
                    "[PACT] Skipping upload — empty URL or data."
                );

                return;
            }

            using var r =
                UnityWebRequest.Put(url, data);

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
        // DATA MODELS
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
        }
    }
}
#endif