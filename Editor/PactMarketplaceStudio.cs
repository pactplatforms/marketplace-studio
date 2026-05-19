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
        private string assetId = "quad005", userEmail = "", creatorName = "";
        private GameObject targetPrefab;
        private string status = "Ready";

        [MenuItem("Pact/Marketplace Studio")]
        public static void ShowWindow() => GetWindow<PactMarketplaceStudio>("Marketplace Studio");

        private void OnGUI()
        {
            GUILayout.Label("PACT Marketplace Publisher", EditorStyles.boldLabel);
            targetPrefab = (GameObject)EditorGUILayout.ObjectField("3D Model", targetPrefab, typeof(GameObject), false);
            assetId = EditorGUILayout.TextField("Asset ID", assetId);
            userEmail = EditorGUILayout.TextField("Creator Email", userEmail);
            creatorName = EditorGUILayout.TextField("Creator Name", creatorName);

            if (GUILayout.Button("BUILD & PUBLISH", GUILayout.Height(40)))
            {
                if (targetPrefab != null && userEmail.Contains("@")) _ = BuildAndPublish();
            }
            EditorGUILayout.HelpBox($"Status: {status}", MessageType.Info);
        }

        private async Task BuildAndPublish()
        {
            try
            {
                string cleanId = assetId.ToLower().Trim().Replace(" ", "_");
                byte[] thumbBytes = CaptureThumbnail(targetPrefab);

                string buildPath = Path.Combine(Application.temporaryCachePath, "PactTemp");
                if (Directory.Exists(buildPath)) Directory.Delete(buildPath, true);
                Directory.CreateDirectory(buildPath);

                AssetBundleBuild[] builds = new AssetBundleBuild[1];
                builds[0].assetBundleName = "asset.unitybundle";
                builds[0].assetNames = new[] { AssetDatabase.GetAssetPath(targetPrefab) };

                BuildPipeline.BuildAssetBundles(buildPath, builds, 
                    BuildAssetBundleOptions.DisableWriteTypeTree | BuildAssetBundleOptions.ForceRebuildAssetBundle, 
                    BuildTarget.iOS);

                byte[] bundleBytes = File.ReadAllBytes(Path.Combine(buildPath, "asset.unitybundle"));

                var payload = new RequestPayload { assetId = cleanId, email = userEmail, creatorName = creatorName };
                string jsonRes = await PostRequest(LAMBDA_URL, JsonUtility.ToJson(payload));
                var res = JsonUtility.FromJson<Response>(jsonRes);

                await UploadFile(res.bundleUrl, bundleBytes, "application/octet-stream");
                await UploadFile(res.thumbnailUrl, thumbBytes, "image/png");

                var meta = new Metadata { Token = res.token, AssetID = cleanId, Email = userEmail };
                await UploadFile(res.jsonUrl, Encoding.UTF8.GetBytes(JsonUtility.ToJson(meta)), "application/json");

                status = "Success! Check Email.";
                Repaint();
            }
            catch (Exception e) { Debug.LogError(e); status = "Error"; }
        }

        private byte[] CaptureThumbnail(GameObject prefab)
        {
            RenderTexture rt = new RenderTexture(512, 512, 24);
            GameObject root = new GameObject("ThumbRoot");
            GameObject p = Instantiate(prefab, Vector3.zero, Quaternion.identity, root.transform);
            p.layer = 31;
            Camera cam = new GameObject("ThumbCam").AddComponent<Camera>();
            cam.targetTexture = rt; cam.cullingMask = 1 << 31; cam.Render();
            RenderTexture.active = rt;
            Texture2D tex = new Texture2D(512, 512, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, 512, 512), 0, 0); tex.Apply();
            cam.targetTexture = null; RenderTexture.active = null; rt.Release();
            byte[] b = tex.EncodeToPNG();
            DestroyImmediate(rt); DestroyImmediate(tex); DestroyImmediate(cam.gameObject); DestroyImmediate(root);
            return b;
        }

        private async Task<string> PostRequest(string url, string json)
        {
            using var r = new UnityWebRequest(url, "POST");
            r.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            r.downloadHandler = new DownloadHandlerBuffer();
            r.SetRequestHeader("Content-Type", "application/json");
            await r.SendWebRequest();
            return r.downloadHandler.text;
        }

        private async Task UploadFile(string url, byte[] data, string ct)
        {
            using var r = UnityWebRequest.Put(url, data);
            r.SetRequestHeader("Content-Type", ct);
            await r.SendWebRequest();
        }

        [Serializable] class RequestPayload { public string assetId, email, creatorName; }
        [Serializable] class Response { public string bundleUrl, thumbnailUrl, jsonUrl, token; }
        [Serializable] class Metadata { public string Token, AssetID, Email; }
    }
}
#endif
