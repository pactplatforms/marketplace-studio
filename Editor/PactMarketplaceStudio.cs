using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System;

public class PactMarketplaceStudio : EditorWindow
{
    private const string LAMBDA_URL =
        "https://73cy1palri.execute-api.us-east-1.amazonaws.com/default/pact-generate_presigned_upload";

    private string assetId = "my_asset";
    private string userEmail = "";
    private string creatorName = "";

    private string[] categories =
    {
        "Art","Fashion","Furniture","Architecture","Gaming",
        "Animation","Characters","Environment","Vehicles",
        "Weapons","Animals","Vegetation"
    };

    private int categoryIndex = 0;
    private GameObject targetPrefab;
    private string status = "Ready";

    private const int TIMEOUT_SECONDS = 30;
    private const int MAX_RETRIES = 3;

    [MenuItem("Pact/Marketplace Studio")]
    public static void ShowWindow()
    {
        GetWindow<PactMarketplaceStudio>("Marketplace Studio");
    }

    private void OnGUI()
    {
        GUILayout.Label("Pact Publisher", EditorStyles.boldLabel);

        targetPrefab = (GameObject)EditorGUILayout.ObjectField(
            "3D Model:", targetPrefab, typeof(GameObject), false);

        assetId = EditorGUILayout.TextField("Asset ID:", assetId);
        userEmail = EditorGUILayout.TextField("Email:", userEmail);
        creatorName = EditorGUILayout.TextField("Creator Name:", creatorName);

        categoryIndex = EditorGUILayout.Popup("Category:", categoryIndex, categories);

        if (GUILayout.Button("BUILD & PUBLISH", GUILayout.Height(40)))
        {
            if (targetPrefab != null && userEmail.Contains("@"))
                _ = BuildAndPublish();
            else
                EditorUtility.DisplayDialog("Error", "Missing prefab or email", "OK");
        }

        GUILayout.Label("Status: " + status);
    }

    // -----------------------------
    // MAIN FLOW
    // -----------------------------
    private async Task BuildAndPublish()
    {
        try
        {
            string cleanId = assetId.ToLower().Trim().Replace(" ", "_");

            // -----------------------------
            // 1. Count triangles
            // -----------------------------
            status = "Analyzing mesh...";

            int triangles = 0;
            foreach (var mesh in targetPrefab.GetComponentsInChildren<MeshFilter>())
            {
                if (mesh.sharedMesh != null)
                    triangles += mesh.sharedMesh.triangles.Length / 3;
            }

            // -----------------------------
            // 2. Build AssetBundle
            // -----------------------------
            status = "Building AssetBundle...";

            string buildPath = "Library/PactAssetBundles/iOS";
            Directory.CreateDirectory(buildPath);

            string prefabPath = AssetDatabase.GetAssetPath(targetPrefab);
            AssetImporter importer = AssetImporter.GetAtPath(prefabPath);
            importer.SetAssetBundleNameAndVariant(cleanId, "");

            BuildPipeline.BuildAssetBundles(
                buildPath,
                BuildAssetBundleOptions.None,
                BuildTarget.iOS
            );

            string rawBundlePath = Path.Combine(buildPath, cleanId);
            string finalBundlePath = rawBundlePath + ".unitybundle";

            if (!File.Exists(rawBundlePath))
            {
                status = "Bundle build failed";
                return;
            }

            if (File.Exists(finalBundlePath))
                File.Delete(finalBundlePath);

            File.Move(rawBundlePath, finalBundlePath);
            importer.SetAssetBundleNameAndVariant("", "");

            byte[] bundleBytes = File.ReadAllBytes(finalBundlePath);

            // -----------------------------
            // 3. Lambda request (WITH RETRY)
            // -----------------------------
            status = "Requesting upload URLs...";

            string payload = JsonUtility.ToJson(new RequestPayload
            {
                assetId = cleanId,
                email = userEmail,
                creatorName = creatorName
            });

            string lambdaResponse = await PostWithRetry(LAMBDA_URL, payload);

            if (lambdaResponse == null)
            {
                status = "Lambda failed after retries";
                return;
            }

            Response res = JsonUtility.FromJson<Response>(lambdaResponse);

            if (res == null || string.IsNullOrEmpty(res.bundleUrl))
            {
                status = "Invalid Lambda response";
                return;
            }

            // -----------------------------
            // 4. Upload bundle (RETRY)
            // -----------------------------
            status = "Uploading bundle...";
            bool bundleOk = await UploadWithRetry(res.bundleUrl, bundleBytes, "application/octet-stream");

            if (!bundleOk)
            {
                status = "Bundle upload failed";
                return;
            }

            // -----------------------------
            // 5. Metadata
            // -----------------------------
            string json = JsonUtility.ToJson(new Metadata
            {
                Token = res.token,
                AssetID = cleanId,
                Email = userEmail,
                CreatorName = creatorName,
                Category = categories[categoryIndex],
                TriangleCount = triangles
            });

            // -----------------------------
            // 6. Upload metadata (RETRY)
            // -----------------------------
            status = "Uploading metadata...";

            bool metaOk = await UploadWithRetry(res.jsonUrl, Encoding.UTF8.GetBytes(json), "application/json");

            if (!metaOk)
            {
                status = "Metadata upload failed";
                return;
            }

            status = "SUCCESS";

            EditorUtility.DisplayDialog(
                "Pact",
                "Upload complete. Check email for verification.",
                "OK"
            );
        }
        catch (Exception ex)
        {
            status = "Fatal error: " + ex.Message;
            Debug.LogException(ex);
        }
    }

    // -----------------------------
    // HTTP HELPERS (RETRY + TIMEOUT)
    // -----------------------------

    private async Task<string> PostWithRetry(string url, string json)
    {
        for (int i = 0; i < MAX_RETRIES; i++)
        {
            using (var req = new UnityWebRequest(url, "POST"))
            {
                byte[] body = Encoding.UTF8.GetBytes(json);

                req.uploadHandler = new UploadHandlerRaw(body);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = TIMEOUT_SECONDS;

                var op = req.SendWebRequest();

                while (!op.isDone)
                    await Task.Delay(50);

                if (req.result == UnityWebRequest.Result.Success)
                    return req.downloadHandler.text;

                Debug.LogWarning($"Lambda retry {i + 1}: {req.error}");
                await Task.Delay(500 * (i + 1)); // backoff
            }
        }

        return null;
    }

    private async Task<bool> UploadWithRetry(string url, byte[] data, string contentType)
    {
        for (int i = 0; i < MAX_RETRIES; i++)
        {
            using (var req = UnityWebRequest.Put(url, data))
            {
                req.SetRequestHeader("Content-Type", contentType);
                req.timeout = TIMEOUT_SECONDS;

                var op = req.SendWebRequest();

                while (!op.isDone)
                    await Task.Delay(50);

                if (req.result == UnityWebRequest.Result.Success)
                    return true;

                Debug.LogWarning($"Upload retry {i + 1}: {req.error}");
                await Task.Delay(500 * (i + 1));
            }
        }

        return false;
    }

    // -----------------------------
    // DATA MODELS
    // -----------------------------
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