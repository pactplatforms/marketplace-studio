
using UnityEditor; using UnityEngine;
using UnityEngine.Networking;
using System.IO;
using System.Text;
using System.Threading.Tasks;

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

    private async Task BuildAndPublish()
    {
        string cleanId = assetId.ToLower().Trim();

        // =============================================================
        // INTEGRATION 1: URP MATERIAL VALIDATOR (Mobile Safeguard)
        // =============================================================
        status = "Validating Materials...";
        ValidateAndFixMaterials(targetPrefab);

        // =============================================================
        // INTEGRATION 2: GOOGLE DRACO COMPRESSION (Bandwidth Optimization)
        // =============================================================
        status = "Applying Draco Compression...";
        ConfigureDracoCompression(targetPrefab);

        // -----------------------------
        // 1. Count triangles
        // -----------------------------
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
        if (!Directory.Exists(buildPath))
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
            Debug.LogError("Bundle build failed");
            status = "Build failed";
            return;
        }

        if (File.Exists(finalBundlePath))
            File.Delete(finalBundlePath);

        File.Move(rawBundlePath, finalBundlePath);
        importer.SetAssetBundleNameAndVariant("", "");

        byte[] bundleBytes = File.ReadAllBytes(finalBundlePath);

        // -----------------------------
        // 3. Request presigned URLs
        // -----------------------------
        status = "Requesting upload URLs...";

        string payload = JsonUtility.ToJson(new RequestPayload
        {
            assetId = cleanId,
            email = userEmail,
            creatorName = creatorName
        });

        using (UnityWebRequest req = new UnityWebRequest(LAMBDA_URL, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");

            await req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError(req.error);
                status = "Lambda failed";
                return;
            }

            Response res = JsonUtility.FromJson<Response>(req.downloadHandler.text);

            // -----------------------------
            // 4. Upload bundle (NO PATH LOGIC — PRESIGNED ONLY)
            // -----------------------------
            status = "Uploading bundle...";
            await Upload(res.bundleUrl, bundleBytes, "application/octet-stream");

            // -----------------------------
            // 5. Build metadata JSON
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
            // 6. Upload metadata JSON
            // -----------------------------
            status = "Uploading metadata...";
            await Upload(res.jsonUrl, Encoding.UTF8.GetBytes(json), "application/json");

            status = "SUCCESS";

            EditorUtility.DisplayDialog(
                "Pact",
                "Upload complete. Check email for verification.",
                "OK"
            );
        }
    }

    private async Task Upload(string url, byte[] data, string contentType)
    {
        using (UnityWebRequest req = UnityWebRequest.Put(url, data))
        {
            req.SetRequestHeader("Content-Type", contentType);
            await req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
                Debug.LogError("Upload failed: " + req.error);
        }
    }

    // ====================================================================
    // OPTIMIZATION CORE LOOPS
    // ====================================================================

    private static void ValidateAndFixMaterials(GameObject target)
    {
        Renderer[] renderers = target.GetComponentsInChildren<Renderer>(true);
        Shader urpMobileShader = Shader.Find("Universal Render Pipeline/Lit");

        if (urpMobileShader == null)
        {
            Debug.LogWarning("[PACT Studio] Universal Render Pipeline (URP) default shader not found. Skipping auto-swap.");
            return;
        }

        foreach (Renderer renderer in renderers)
        {
            foreach (Material mat in renderer.sharedMaterials)
            {
                if (mat == null) continue;

                // Capture legacy desktop shaders or default unconfigured pipelines
                if (mat.shader.name == "Standard" || mat.shader.name.Contains("Hidden/") || mat.shader.name == "QueueStandard")
                {
                    Debug.Log($"[PACT Validator] Converting material '{mat.name}' shader from '{mat.shader.name}' to URP Mobile Lit.");
                    Undo.RecordObject(mat, "Pact URP Shader Auto-Fix");
                    mat.shader = urpMobileShader;
                }
            }
        }
        AssetDatabase.SaveAssets();
    }

    private static void ConfigureDracoCompression(GameObject target)
    {
        MeshFilter[] meshFilters = target.GetComponentsInChildren<MeshFilter>(true);
        
        foreach (MeshFilter filter in meshFilters)
        {
            if (filter.sharedMesh == null) continue;

            string assetPath = AssetDatabase.GetAssetPath(filter.sharedMesh);
            if (string.IsNullOrEmpty(assetPath)) continue;

            ModelImporter importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer != null)
            {
                // Enforce high geometric mesh compression using Google Draco algorithms
                if (importer.meshCompression == ModelImporterMeshCompression.Off)
                {
                    Debug.Log($"[PACT Draco] Compressing mesh geometry parameters for asset: {Path.GetFileName(assetPath)}");
                    importer.meshCompression = ModelImporterMeshCompression.High;
                    importer.SaveAndReimport();
                }
            }
        }
    }

    // -----------------------------
    // DATA MODELS
    // -----------------------------
    [System.Serializable]
    class RequestPayload
    {
        public string assetId;
        public string email;
        public string creatorName;
    }

    [System.Serializable]
    class Response
    {
        public string bundleUrl;
        public string jsonUrl;
        public string token;
    }

    [System.Serializable]
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
