using UnityEngine;
using UnityEditor;
using System.IO;
using System.Threading.Tasks;
using System.Net.Http;
using System;
using System.Text;

namespace Pact.Marketplace
{
    public class PactMarketplaceStudio : EditorWindow
    {
        private string assetId = "my_asset";
        private string email = "";
        private string creatorName = "emmanuel";
        private string status = "Ready";

        private const string HANDSHAKE_URL = "https://ki26mr9lih.execute-api.us-east-1.amazonaws.com/generate";

        [MenuItem("Pact/Marketplace Studio")]
        public static void ShowWindow() => GetWindow<PactMarketplaceStudio>("Pact Studio");

        private void OnGUI()
        {
            GUILayout.Label("Pact AR Studio", EditorStyles.boldLabel);
            assetId = EditorGUILayout.TextField("Asset ID", assetId);
            email = EditorGUILayout.TextField("Creator Email", email);
            creatorName = EditorGUILayout.TextField("Creator Name", creatorName);

            if (GUILayout.Button("Build and Publish") && Selection.activeGameObject != null)
            {
                _ = BuildAndPublish();
            }
            
            GUILayout.Space(10);
            GUILayout.Label($"Status: {status}");
        }

        private async Task BuildAndPublish()
        {
            status = "Processing...";
            GameObject target = Selection.activeGameObject;

            // 1. Calculate Triangles
            int triangles = 0;
            foreach (var filter in target.GetComponentsInChildren<MeshFilter>())
            {
                triangles += filter.sharedMesh.triangles.Length / 3;
            }

            // 2. Capture Thumbnail (Memory Safe Fix)
            byte[] thumbBytes = CaptureThumbnail(target);

            // 3. Build AssetBundle
            string bundleDir = "Assets/AssetBundles";
            if (!Directory.Exists(bundleDir)) Directory.CreateDirectory(bundleDir);
            
            AssetBundleBuild[] buildMap = new AssetBundleBuild[1];
            buildMap[0].assetBundleName = $"{assetId}.unitybundle";
            buildMap[0].assetNames = new[] { AssetDatabase.GetAssetPath(target) };

            BuildPipeline.BuildAssetBundles(bundleDir, buildMap, BuildAssetBundleOptions.None, BuildTarget.iOS);
            byte[] bundleBytes = File.ReadAllBytes(Path.Combine(bundleDir, $"{assetId}.unitybundle"));

            // 4. Execute Handshake and Uploads
            await ExecuteUpload(bundleBytes, thumbBytes, triangles);
        }

        private byte[] CaptureThumbnail(GameObject target)
        {
            int res = 512;
            RenderTexture rt = new RenderTexture(res, res, 24);
            Texture2D screenShot = new Texture2D(res, res, TextureFormat.RGB24, false);
            
            GameObject camGo = new GameObject("ThumbCam");
            Camera cam = camGo.AddComponent<Camera>();
            cam.targetTexture = rt;
            camGo.transform.position = target.transform.position + new Vector3(0, 1, -3);
            camGo.transform.LookAt(target.transform);
            
            cam.Render();

            RenderTexture.active = rt;
            screenShot.ReadPixels(new Rect(0, 0, res, res), 0, 0);
            screenShot.Apply();

            // --- CRITICAL CLEANUP TO PREVENT CRASH ---
            RenderTexture.active = null;
            cam.targetTexture = null;
            
            byte[] bytes = screenShot.EncodeToPNG();
            
            DestroyImmediate(camGo);
            DestroyImmediate(rt);
            DestroyImmediate(screenShot);

            return bytes;
        }

        private async Task ExecuteUpload(byte[] bundle, byte[] thumb, int tris)
        {
            try 
            {
                using (HttpClient client = new HttpClient())
                {
                    var payload = new { assetId, email, creatorName, triangleCount = tris };
                    var jsonPayload = JsonUtility.ToJson(payload);

                    // A. Handshake
                    var response = await client.PostAsync(HANDSHAKE_URL, new StringContent(jsonPayload, Encoding.UTF8, "application/json"));
                    var resBody = await response.Content.ReadAsStringAsync();
                    var handshake = JsonUtility.FromJson<HandshakeResponse>(resBody);

                    // B. Multi-part Upload
                    await client.PutAsync(handshake.bundleUrl, new ByteArrayContent(bundle));
                    await client.PutAsync(handshake.thumbnailUrl, new ByteArrayContent(thumb));
                    await client.PutAsync(handshake.jsonUrl, new StringContent(jsonPayload));

                    status = "Success! Check your email.";
                }
            }
            catch (Exception e)
            {
                status = "Error: " + e.Message;
                Debug.LogError(e);
            }
        }

        [Serializable] 
        public class HandshakeResponse { 
            public string bundleUrl; 
            public string jsonUrl; 
            public string thumbnailUrl; 
        }
    }
}