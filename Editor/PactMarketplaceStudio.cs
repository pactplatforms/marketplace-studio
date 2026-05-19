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
                if (targetPrefab == null || !userEmail.Contains("@"))
                {
                    EditorUtility.DisplayDialog("Error", "Check prefab and email.", "OK");
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

                BuildPipeline.BuildAssetBundles(buildPath, builds, BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.DisableWriteTypeTree, BuildTarget.iOS);
                
                string bundlePath = Path.Combine(buildPath, bundleName);
                if (!File.Exists(bundlePath)) throw new Exception("Build failed.");
                byte[] bundleBytes = File.ReadAllBytes(bundlePath);

                status = "Syncing with Server...";
                Repaint();
                RequestPayload payload = new RequestPayload { assetId = cleanId, email = userEmail, creatorName = creatorName };
                string responseJson = await PostRequest(LAMBDA_URL, JsonUtility.ToJson(payload));
                Response res = JsonUtility.FromJson<Response>(responseJson);

                status = "Uploading...";
                Repaint();
                await UploadFile(res.bundleUrl, bundleBytes, "application/octet-stream");
                await UploadFile(res.thumbnailUrl, thumbBytes, "image/png");

                Metadata meta = new Metadata { Token = res.token, AssetID = cleanId, Email = userEmail, CreatorName = creatorName };
                await UploadFile(res.jsonUrl, Encoding.UTF8.GetBytes(JsonUtility.ToJson(meta)), "application/json");

                status = "Success!";
                Repaint();
                EditorUtility.DisplayDialog("Success", "Check email to verify.", "OK");
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
            RenderTexture rt = new RenderTexture(512, 512, 24);
            GameObject root = new GameObject("ThumbRoot");
            GameObject instance = Instantiate(prefab, Vector3.zero, Quaternion.identity, root.transform);
            SetLayerRecursively(instance, 31);

            Camera cam = new GameObject("ThumbCam").AddComponent<Camera>();
            cam.backgroundColor = new Color(0, 0, 0, 0);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.targetTexture = rt;
            cam.cullingMask = 1 << 31;

            Bounds b = new Bounds(instance.transform.position, Vector3.zero);
            foreach (Renderer r in instance.GetComponentsInChildren<Renderer>()) b.Encapsulate(r.bounds);
            float s = Mathf.Max(b.size.x, b.size.y, b.size.z);
            cam.transform.position = b.center + new Vector3(0, s * 0.5f, -s * 2f);
            cam.transform.LookAt(b.center);

            cam.Render();
            RenderTexture.active = rt;
            Texture2D tex = new Texture2D(512, 512, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, 512, 512), 0, 0);
            tex.Apply();

            // CLEANUP
            cam.targetTexture = null;
            RenderTexture.active = null;
            rt.Release();

            byte[] bytes = tex.EncodeToPNG();
            DestroyImmediate(rt); DestroyImmediate(tex); DestroyImmediate(cam.gameObject); DestroyImmediate(root);
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
                var op = r.SendWebRequest();
                while (!op.isDone) await Task.Yield();
                return r.downloadHandler.text;
            }
        }

        private async Task UploadFile(string url, byte[] data, string contentType)
        {
            using (UnityWebRequest r = UnityWebRequest.Put(url, data))
            {
                r.SetRequestHeader("Content-Type", contentType);
                var op = r.SendWebRequest();
                while (!op.isDone) await Task.Yield();
            }
        }

        [Serializable] class RequestPayload { public string assetId, email, creatorName; }
        [Serializable] class Response { public string bundleUrl, thumbnailUrl, jsonUrl, token; }
        [Serializable] class Metadata { public string Token, AssetID, Email, CreatorName; }
    }
}
#endif