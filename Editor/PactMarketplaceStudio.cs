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
        private GameObject targetPrefab;
        private string status = "Ready";

        [MenuItem("Pact/Marketplace Studio")]
        public static void ShowWindow()
        {
            GetWindow<PactMarketplaceStudio>("Marketplace Studio");
        }

        private void OnGUI()
        {
            GUILayout.Label("PACT Marketplace Publisher", EditorStyles.boldLabel);

            targetPrefab = (GameObject)EditorGUILayout.ObjectField("3D Model", targetPrefab, typeof(GameObject), false);
            assetId = EditorGUILayout.TextField("Asset ID", assetId);
            userEmail = EditorGUILayout.TextField("Creator Email", userEmail);
            creatorName = EditorGUILayout.TextField("Creator Name", creatorName);

            GUILayout.Space(10);

            if (GUILayout.Button("BUILD & PUBLISH", GUILayout.Height(40)))
            {
                if (targetPrefab == null)
                {
                    EditorUtility.DisplayDialog("Error", "Assign a prefab.", "OK");
                    return;
                }

                if (!userEmail.Contains("@"))
                {
                    EditorUtility.DisplayDialog("Error", "Invalid email.", "OK");
                    return;
                }

                _ = BuildAndPublish();
            }

            GUILayout.Space(10);
            EditorGUILayout.HelpBox($"Status: {status}", MessageType.Info);
        }

        private async Task BuildAndPublish()
        {
            try
            {
                string cleanId = assetId.ToLower().Trim().Replace(" ", "_");

                status = "Capturing thumbnail...";
                Repaint();
                byte[] thumbBytes = CaptureThumbnail(targetPrefab);

                status = "Building AssetBundle...";
                Repaint();

                string buildPath = Path.Combine(Application.temporaryCachePath, "PactTemp");
                if (Directory.Exists(buildPath)) Directory.Delete(buildPath, true);
                Directory.CreateDirectory(buildPath);

                string bundleName = cleanId + ".unitybundle";
                AssetBundleBuild[] builds = new AssetBundleBuild[1];
                builds[0].assetBundleName = bundleName;
                builds[0].assetNames = new[] { AssetDatabase.GetAssetPath(targetPrefab) };

                BuildPipeline.BuildAssetBundles(
                    buildPath,
                    builds,
                    BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.DisableWriteTypeTree,
                    BuildTarget.iOS
                );

                string bundlePath = Path.Combine(buildPath, bundleName);
                if (!File.Exists(bundlePath)) throw new Exception("AssetBundle build failed.");
                byte[] bundleBytes = File.ReadAllBytes(bundlePath);

                status = "Syncing with Server...";
                Repaint();

                RequestPayload payload = new RequestPayload { assetId = cleanId, email = userEmail, creatorName = creatorName };
                string responseJson = await PostRequest(LAMBDA_URL, JsonUtility.ToJson(payload));
                Response response = JsonUtility.FromJson<Response>(responseJson);

                if (string.IsNullOrEmpty(response.bundleUrl)) throw new Exception("Invalid server response.");

                status = "Uploading bundle...";
                Repaint();
                await UploadFile(response.bundleUrl, bundleBytes, "application/octet-stream");

                status = "Uploading thumbnail...";
                Repaint();
                await UploadFile(response.thumbnailUrl, thumbBytes, "image/png");

                status = "Finalizing metadata...";
                Repaint();
                Metadata meta = new Metadata
                {
                    Token = response.token,
                    AssetID = cleanId,
                    Email = userEmail,
                    CreatorName = creatorName,
                    UnityVersion = Application.unityVersion,
                    Platform = "iOS",
                    UploadTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                };

                await UploadFile(response.jsonUrl, Encoding.UTF8.GetBytes(JsonUtility.ToJson(meta)), "application/json");

                status = "Success!";
                Repaint();
                EditorUtility.DisplayDialog("Success", "Upload completed. Check your email to verify.", "OK");
            }
            catch (Exception e)
            {
                status = "Error: " + e.Message;
                Debug.LogError(e);
                Repaint();
            }
        }

        private byte[] CaptureThumbnail(GameObject prefab)
        {
            const int SIZE = 512;
            RenderTexture rt = new RenderTexture(SIZE, SIZE, 24);
            GameObject root = new GameObject("ThumbRoot");
            GameObject instance = Instantiate(prefab, Vector3.zero, Quaternion.identity, root.transform);
            
            SetLayerRecursively(instance, 31);

            GameObject camObj = new GameObject("ThumbCam");
            Camera cam = camObj.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0, 0, 0, 0);
            cam.targetTexture = rt;
            cam.cullingMask = 1 << 31;

            Bounds bounds = new Bounds(instance.transform.position, Vector3.zero);
            foreach (Renderer r in instance.GetComponentsInChildren<Renderer>()) bounds.Encapsulate(r.bounds);

            float size = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
            cam.transform.position = bounds.center + new Vector3(0, size * 0.5f, -size * 2f);
            cam.transform.LookAt(bounds.center);

            cam.Render();

            RenderTexture.active = rt;
            Texture2D tex = new Texture2D(SIZE, SIZE, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, SIZE, SIZE), 0, 0);
            tex.Apply();

            // CLEANUP SEQUENCE
            cam.targetTexture = null;
            RenderTexture.active = null;
            rt.Release();

            byte[] bytes = tex.EncodeToPNG();

            DestroyImmediate(rt);
            DestroyImmediate(tex);
            DestroyImmediate(camObj);
            DestroyImmediate(root);

            return bytes;
        }

        private void SetLayerRecursively(GameObject obj, int layer)
        {
            obj.layer = layer;
            foreach (Transform child in obj.transform) SetLayerRecursively(child.gameObject, layer);
        }

        private async Task<string> PostRequest(string url, string json)
        {
            using (UnityWebRequest r = new UnityWebRequest(url, "POST"))
            {
                r.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                r.downloadHandler = new DownloadHandlerBuffer();
                r.SetRequestHeader("Content-Type", "application/json");

                var operation = r.SendWebRequest();
                while (!operation.isDone) await Task.Yield();

                if (r.result != UnityWebRequest.Result.Success) throw new Exception(r.error);
                return r.downloadHandler.text;
            }
        }

        private async Task UploadFile(string url, byte[] data, string contentType)
        {
            using (UnityWebRequest r = UnityWebRequest.Put(url, data))
            {
                r.SetRequestHeader("Content-Type", contentType);
                var operation = r.SendWebRequest();
                while (!operation.isDone) await Task.Yield();
                if (r.result != UnityWebRequest.Result.Success) throw new Exception(r.error);
            }
        }

        [Serializable] class RequestPayload { public string assetId; public string email; public string creatorName; }
        [Serializable] class Response { public string bundleUrl; public string thumbnailUrl; public string jsonUrl; public string token; }
        [Serializable] class Metadata { public string Token; public string AssetID; public string Email; public string CreatorName; public string UnityVersion; public string Platform; public long UploadTimestamp; }
    }
}
#endif