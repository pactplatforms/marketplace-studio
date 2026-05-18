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
        private const string LAMBDA_URL = "https://73cy1palri.execute-api.us-east-1.amazonaws.com/default/pact-generate_presigned_upload";

        private string assetId = "my_asset";
        private string userEmail = "";
        private string creatorName = "";
        private string[] categories = { "Art", "Fashion", "Furniture", "Architecture", "Gaming", "Animation", "Characters", "Environment", "Vehicles", "Weapons", "Animals", "Vegetation" };
        private int categoryIndex = 0;
        private GameObject targetPrefab;
        private string status = "Ready";

        [MenuItem("Pact/Marketplace Studio")]
        public static void ShowWindow() => GetWindow<PactMarketplaceStudio>("Marketplace Studio");

        private void OnGUI()
        {
            GUILayout.Label("Pact Publisher", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            targetPrefab = (GameObject)EditorGUILayout.ObjectField("3D Model:", targetPrefab, typeof(GameObject), false);
            assetId = EditorGUILayout.TextField("Asset ID:", assetId);
            userEmail = EditorGUILayout.TextField("Creator Email:", userEmail);
            creatorName = EditorGUILayout.TextField("Creator Name:", creatorName);
            categoryIndex = EditorGUILayout.Popup("Category:", categoryIndex, categories);

            EditorGUILayout.Space(10);

            if (GUILayout.Button("BUILD & PUBLISH", GUILayout.Height(40)))
            {
                if (targetPrefab != null && userEmail.Contains("@")) _ = BuildAndPublish();
                else EditorUtility.DisplayDialog("Error", "Check prefab and email", "OK");
            }

            EditorGUILayout.HelpBox("Status: " + status, MessageType.Info);
        }

        private async Task BuildAndPublish()
        {
            try
            {
                // 1. THUMBNAIL RENDERING (Ingestion Logic)
                status = "Rendering Thumbnail...";
                byte[] thumbBytes = CaptureThumbnail(targetPrefab);

                // 2. BUNDLE GENERATION (Ingestion Logic)
                status = "Building iOS Bundle...";
                string buildPath = Path.Combine(Application.temporaryCachePath, "PactTemp");
                if (Directory.Exists(buildPath)) Directory.Delete(buildPath, true);
                Directory.CreateDirectory(buildPath);

                string bundleName = assetId.ToLower().Replace(" ", "_") + ".unity3d";
                AssetBundleBuild[] build = new AssetBundleBuild[1];
                build[0].assetBundleName = bundleName;
                build[0].assetNames = new string[] { AssetDatabase.GetAssetPath(targetPrefab) };

                BuildPipeline.BuildAssetBundles(buildPath, build, BuildAssetBundleOptions.None, BuildTarget.iOS);
                byte[] bundleBytes = File.ReadAllBytes(Path.Combine(buildPath, bundleName));

                // 3. UPLOAD & VERIFICATION
                status = "Requesting Upload Access...";
                var payload = new RequestPayload { assetId = assetId, email = userEmail, creatorName = creatorName };
                string responseJson = await PostRequest(LAMBDA_URL, JsonUtility.ToJson(payload));
                var res = JsonUtility.FromJson<Response>(responseJson);

                status = "Uploading Files...";
                await UploadFile(res.bundleUrl, bundleBytes, "application/octet-stream");
                await UploadFile(res.thumbnailUrl, thumbBytes, "image/png");
                
                var meta = new Metadata { Token = res.token, AssetID = assetId, Email = userEmail, CreatorName = creatorName, Category = categories[categoryIndex] };
                await UploadFile(res.jsonUrl, Encoding.UTF8.GetBytes(JsonUtility.ToJson(meta)), "application/json");

                status = "Success!";
                EditorUtility.DisplayDialog("Pact", "Asset Published Successfully!", "OK");
            }
            catch (Exception e) { status = "Error: " + e.Message; Debug.LogError(e); }
        }

        // --- ISOLATED THUMBNAIL LOGIC ---
        private byte[] CaptureThumbnail(GameObject prefab)
        {
            const int SIZE = 512;
            GameObject root = new GameObject("P_Root");
            GameObject p = Instantiate(prefab, Vector3.zero, Quaternion.identity);
            p.transform.SetParent(root.transform);
            
            // Set layer to 31 for isolation
            SetLayerRecursively(p, 31);

            Renderer[] rs = p.GetComponentsInChildren<Renderer>();
            Bounds b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);

            GameObject cObj = new GameObject("P_Cam");
            Camera c = cObj.AddComponent<Camera>();
            c.backgroundColor = new Color(0, 0, 0, 0);
            c.clearFlags = CameraClearFlags.SolidColor;
            c.cullingMask = 1 << 31;
            
            float rad = b.extents.magnitude;
            c.transform.position = b.center + new Vector3(-1, 0.6f, -1).normalized * (rad * 2.2f);
            c.transform.LookAt(b.center);

            RenderTexture rt = new RenderTexture(SIZE, SIZE, 24);
            c.targetTexture = rt;
            RenderTexture.active = rt;
            Texture2D tex = new Texture2D(SIZE, SIZE, TextureFormat.RGBA32, false);
            c.Render();
            tex.ReadPixels(new Rect(0, 0, SIZE, SIZE), 0, 0);
            tex.Apply();

            byte[] bytes = tex.EncodeToPNG();
            DestroyImmediate(rt); DestroyImmediate(tex); DestroyImmediate(cObj); DestroyImmediate(root);
            return bytes;
        }

        private void SetLayerRecursively(GameObject o, int l) { o.layer = l; foreach (Transform t in o.transform) SetLayerRecursively(t.gameObject, l); }

        private async Task<string> PostRequest(string url, string json) {
            using var r = new UnityWebRequest(url, "POST");
            r.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            r.downloadHandler = new DownloadHandlerBuffer();
            r.SetRequestHeader("Content-Type", "application/json");
            await r.SendWebRequest();
            return r.downloadHandler.text;
        }

        private async Task UploadFile(string url, byte[] data, string ct) {
            using var r = UnityWebRequest.Put(url, data);
            r.SetRequestHeader("Content-Type", ct);
            await r.SendWebRequest();
        }

        [Serializable] class RequestPayload { public string assetId, email, creatorName; }
        [Serializable] class Response { public string bundleUrl, jsonUrl, thumbnailUrl, token; }
        [Serializable] class Metadata { public string Token, AssetID, Email, CreatorName, Category; }
    }
}
#endif