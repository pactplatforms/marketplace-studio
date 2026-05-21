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

        // SNAP-STYLE MOBILE LIMITS
        private const int MAX_TRIANGLES = 100000;
        private const int MAX_TEXTURE_SIZE = 2048;
        private const float MAX_BUNDLE_SIZE_MB = 50f;

        private string assetId = "quad005";
        private string userEmail = "";
        private string creatorName = "";

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

            EditorGUILayout.HelpBox(
                $"Limits:\n" +
                $"- Max Triangles: {MAX_TRIANGLES:N0}\n" +
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
                status = "Validating...";
                Repaint();

                int triangleCount =
                    CountTriangles(targetPrefab);

                if (triangleCount > MAX_TRIANGLES)
                {
                    status =
                        $"Too Many Triangles ({triangleCount:N0})";

                    Debug.LogError(status);

                    return;
                }

                status = "Optimizing Textures...";
                Repaint();

                OptimizeTextures();

                string cleanId =
                    assetId
                    .ToLower()
                    .Trim()
                    .Replace(" ", "_");

                byte[] thumbBytes =
                    CaptureThumbnail(targetPrefab);

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

                status = "Building Bundle...";
                Repaint();

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
                        $"Bundle Too Large ({bundleSizeMB:F2}MB)";

                    Debug.LogError(status);

                    return;
                }

                status = "Requesting Upload...";
                Repaint();

                var payload =
                    new RequestPayload
                    {
                        assetId = cleanId,
                        email = userEmail,
                        creatorName = creatorName
                    };

                string jsonRes =
                    await PostRequest(
                        LAMBDA_URL,
                        JsonUtility.ToJson(payload)
                    );

                var res =
                    JsonUtility.FromJson<Response>(
                        jsonRes
                    );

                status = "Uploading Bundle...";
                Repaint();

                await UploadFile(
                    res.bundleUrl,
                    bundleBytes,
                    "application/octet-stream"
                );

                await UploadFile(
                    res.thumbnailUrl,
                    thumbBytes,
                    "image/png"
                );

                var meta =
                    new Metadata
                    {
                        Token = res.token,
                        AssetID = cleanId,
                        Email = userEmail,
                        TriangleCount = triangleCount
                    };

                await UploadFile(
                    res.jsonUrl,
                    Encoding.UTF8.GetBytes(
                        JsonUtility.ToJson(meta)
                    ),
                    "application/json"
                );

                status = "SUCCESS";
                Repaint();

                Debug.Log(
                    $"Uploaded {cleanId} | " +
                    $"{triangleCount:N0} tris | " +
                    $"{bundleSizeMB:F2}MB"
                );
            }
            catch (Exception e)
            {
                Debug.LogError(e);
                status = "ERROR";
            }
        }

        private int CountTriangles(GameObject prefab)
        {
            int total = 0;

            MeshFilter[] meshes =
                prefab.GetComponentsInChildren<MeshFilter>(true);

            foreach (var mf in meshes)
            {
                if (mf.sharedMesh != null)
                {
                    total +=
                        mf.sharedMesh.triangles.Length / 3;
                }
            }

            return total;
        }

        private void OptimizeTextures()
        {
            string[] guids =
                AssetDatabase.FindAssets("t:Texture2D");

            foreach (string guid in guids)
            {
                string path =
                    AssetDatabase.GUIDToAssetPath(guid);

                TextureImporter importer =
                    AssetImporter.GetAtPath(path)
                    as TextureImporter;

                if (importer == null)
                    continue;

                importer.maxTextureSize =
                    MAX_TEXTURE_SIZE;

                importer.textureCompression =
                    TextureImporterCompression.Compressed;

                importer.mipmapEnabled = true;

                importer.SaveAndReimport();
            }
        }

        private byte[] CaptureThumbnail(GameObject prefab)
        {
            RenderTexture rt =
                new RenderTexture(512, 512, 24);

            GameObject root =
                new GameObject("ThumbRoot");

            GameObject p =
                Instantiate(
                    prefab,
                    Vector3.zero,
                    Quaternion.identity,
                    root.transform
                );

            p.layer = 31;

            Camera cam =
                new GameObject("ThumbCam")
                .AddComponent<Camera>();

            cam.targetTexture = rt;
            cam.cullingMask = 1 << 31;

            cam.transform.position =
                new Vector3(0, 0, -3);

            cam.Render();

            RenderTexture.active = rt;

            Texture2D tex =
                new Texture2D(
                    512,
                    512,
                    TextureFormat.RGBA32,
                    false
                );

            tex.ReadPixels(
                new Rect(0, 0, 512, 512),
                0,
                0
            );

            tex.Apply();

            cam.targetTexture = null;

            RenderTexture.active = null;

            rt.Release();

            byte[] b =
                tex.EncodeToPNG();

            DestroyImmediate(rt);
            DestroyImmediate(tex);
            DestroyImmediate(cam.gameObject);
            DestroyImmediate(root);

            return b;
        }

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

            public int TriangleCount;
        }
    }
}
#endif