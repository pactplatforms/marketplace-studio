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

        private const int MAX_TRIANGLES = 100000;

        private string      assetId       = "my_asset";
        private string      userEmail     = "";
        private string      creatorName   = "";
        private string[]    categories    =
        {
            "Art", "Fashion", "Furniture", "Architecture", "Gaming",
            "Animation", "Characters", "Environment", "Vehicles",
            "Weapons", "Animals", "Vegetation"
        };
        private int         categoryIndex = 0;
        private GameObject  targetPrefab;
        private string      status        = "Ready";

        [MenuItem("Pact/Marketplace Studio")]
        public static void ShowWindow() =>
            GetWindow<PactMarketplaceStudio>("Marketplace Studio");

        private void OnGUI()
        {
            GUILayout.Label("Pact Publisher", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            targetPrefab  = (GameObject)EditorGUILayout.ObjectField(
                "3D Model:", targetPrefab, typeof(GameObject), false);
            assetId       = EditorGUILayout.TextField("Asset ID:",      assetId);
            userEmail     = EditorGUILayout.TextField("Creator Email:", userEmail);
            creatorName   = EditorGUILayout.TextField("Creator Name:",  creatorName);
            categoryIndex = EditorGUILayout.Popup("Category:", categoryIndex, categories);

            EditorGUILayout.Space(10);

            if (GUILayout.Button("BUILD & PUBLISH", GUILayout.Height(40)))
            {
                if (targetPrefab != null && userEmail.Contains("@"))
                    _ = BuildAndPublish();
                else
                    EditorUtility.DisplayDialog("Error", "Check prefab and email", "OK");
            }

            EditorGUILayout.HelpBox("Status: " + status, MessageType.Info);
        }

        private async Task BuildAndPublish()
        {
            try
            {
                string cleanId = assetId.ToLower().Trim().Replace(" ", "_");

                // ── 1. Count triangles ────────────────────────────
                status = "Analysing mesh...";
                Repaint();

                int triangles = 0;
                foreach (var mf in targetPrefab.GetComponentsInChildren<MeshFilter>())
                    if (mf.sharedMesh != null)
                        triangles += mf.sharedMesh.triangles.Length / 3;

                if (triangles > MAX_TRIANGLES)
                {
                    status = $"Too many triangles ({triangles:N0}). Max is {MAX_TRIANGLES:N0}.";
                    EditorUtility.DisplayDialog("Error", status, "OK");
                    return;
                }

                // ── 2. Render thumbnail ───────────────────────────
                status = "Rendering thumbnail...";
                Repaint();
                byte[] thumbBytes = CaptureThumbnail(targetPrefab);

                // ── 3. Build AssetBundle ──────────────────────────
                status = "Building iOS bundle...";
                Repaint();

                string buildPath = Path.Combine(
                    Application.temporaryCachePath, "PactTemp");

                if (Directory.Exists(buildPath))
                    Directory.Delete(buildPath, true);
                Directory.CreateDirectory(buildPath);

                // Extension must be .unitybundle — Bouncer filters on this suffix
                string bundleName = cleanId + ".unitybundle";

                AssetBundleBuild[] buildMap = new AssetBundleBuild[1];
                buildMap[0].assetBundleName = bundleName;
                buildMap[0].assetNames      = new string[]
                {
                    AssetDatabase.GetAssetPath(targetPrefab)
                };

                BuildPipeline.BuildAssetBundles(
                    buildPath, buildMap,
                    BuildAssetBundleOptions.None,
                    BuildTarget.iOS
                );

                string bundlePath = Path.Combine(buildPath, bundleName);
                if (!File.Exists(bundlePath))
                {
                    status = "Bundle build failed. Check the Console for errors.";
                    return;
                }

                byte[] bundleBytes = File.ReadAllBytes(bundlePath);

                // ── 4. Request presigned URLs ─────────────────────
                status = "Requesting upload access...";
                Repaint();

                var payload = new RequestPayload
                {
                    assetId     = cleanId,
                    email       = userEmail,
                    creatorName = creatorName
                };

                string responseJson = await PostRequest(
                    LAMBDA_URL, JsonUtility.ToJson(payload));

                Debug.Log("[PACT] Lambda response: " + responseJson);

                var res = JsonUtility.FromJson<Response>(responseJson);

                if (res == null || string.IsNullOrEmpty(res.bundleUrl))
                {
                    status = "Lambda did not return upload URLs. Check Console.";
                    Debug.LogError("[PACT] Lambda response was: " + responseJson);
                    return;
                }

                // ── 5. Upload bundle ──────────────────────────────
                status = "Uploading bundle...";
                Repaint();
                await UploadFile(res.bundleUrl, bundleBytes, "application/octet-stream");

                // ── 6. Upload thumbnail ───────────────────────────
                if (!string.IsNullOrEmpty(res.thumbnailUrl))
                {
                    status = "Uploading thumbnail...";
                    Repaint();
                    await UploadFile(res.thumbnailUrl, thumbBytes, "image/png");
                }

                // ── 7. Upload metadata JSON ───────────────────────
                status = "Uploading metadata...";
                Repaint();

                var meta = new Metadata
                {
                    Token         = res.token,
                    AssetID       = cleanId,
                    Email         = userEmail,
                    CreatorName   = creatorName,
                    Category      = categories[categoryIndex],
                    TriangleCount = triangles
                };

                await UploadFile(
                    res.jsonUrl,
                    Encoding.UTF8.GetBytes(JsonUtility.ToJson(meta)),
                    "application/json"
                );

                // ── 8. Done ───────────────────────────────────────
                status = "Success! Check email for verification link.";
                Repaint();

                EditorUtility.DisplayDialog(
                    "PACT Marketplace Studio",
                    $"Asset '{cleanId}' uploaded successfully.\n\n" +
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

        // ── Thumbnail capture ─────────────────────────────────────

        private byte[] CaptureThumbnail(GameObject prefab)
        {
            const int SIZE = 512;

            GameObject root = new GameObject("P_Root");
            GameObject p    = Instantiate(prefab, Vector3.zero, Quaternion.identity);
            p.transform.SetParent(root.transform);
            SetLayerRecursively(p, 31);

            Renderer[] rs = p.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0)
            {
                DestroyImmediate(root);
                return new byte[0];
            }

            Bounds b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);

            GameObject cObj = new GameObject("P_Cam");
            Camera     c    = cObj.AddComponent<Camera>();
            c.backgroundColor = new Color(0, 0, 0, 0);
            c.clearFlags      = CameraClearFlags.SolidColor;
            c.cullingMask     = 1 << 31;

            float rad = b.extents.magnitude;
            c.transform.position = b.center +
                new Vector3(-1, 0.6f, -1).normalized * (rad * 2.2f);
            c.transform.LookAt(b.center);

            RenderTexture rt  = new RenderTexture(SIZE, SIZE, 24);
            c.targetTexture   = rt;
            RenderTexture.active = rt;

            Texture2D tex = new Texture2D(SIZE, SIZE, TextureFormat.RGBA32, false);
            c.Render();
            tex.ReadPixels(new Rect(0, 0, SIZE, SIZE), 0, 0);
            tex.Apply();

            byte[] bytes = tex.EncodeToPNG();

            DestroyImmediate(rt);
            DestroyImmediate(tex);
            DestroyImmediate(cObj);
            DestroyImmediate(root);

            return bytes;
        }

        private void SetLayerRecursively(GameObject o, int l)
        {
            o.layer = l;
            foreach (Transform t in o.transform)
                SetLayerRecursively(t.gameObject, l);
        }

        // ── HTTP helpers ──────────────────────────────────────────

        private async Task<string> PostRequest(string url, string json)
        {
            using var r = new UnityWebRequest(url, "POST");
            r.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            r.downloadHandler = new DownloadHandlerBuffer();
            r.SetRequestHeader("Content-Type", "application/json");
            await r.SendWebRequest();

            if (r.result != UnityWebRequest.Result.Success)
                Debug.LogError("[PACT] POST error: " + r.error);

            return r.downloadHandler.text;
        }

        private async Task UploadFile(string url, byte[] data, string ct)
        {
            if (string.IsNullOrEmpty(url) || data == null || data.Length == 0)
            {
                Debug.LogWarning("[PACT] Skipping upload — empty URL or data.");
                return;
            }

            using var r = UnityWebRequest.Put(url, data);
            r.SetRequestHeader("Content-Type", ct);
            await r.SendWebRequest();

            if (r.result != UnityWebRequest.Result.Success)
                Debug.LogError("[PACT] Upload error: " + r.error);
        }

        // ── Data models ───────────────────────────────────────────

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
            public string jsonUrl;
            public string thumbnailUrl;
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
            public int    TriangleCount;
        }
    }
}
#endif