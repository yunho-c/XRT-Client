using UnityEngine;

/// <summary>
/// Makes the StylizedCharacter model 90 % transparent (alpha = 0.10) while keeping
/// a thin visible outline so the operator can still read the avatar's pose.
///
/// Outline technique: a second mesh pass that scales vertices along normals and
/// renders with front-face culling, producing a solid-colour silhouette rim.
///
/// Setup:
///   1. Attach to the root of the StylizedCharacter GameObject.
///   2. Set outlineColor and outlineThickness in the Inspector.
///   3. Call Apply() manually or enable applyOnStart.
/// </summary>
public class AvatarTransparency : MonoBehaviour
{
    [Header("Transparency")]
    [Tooltip("Body alpha (0 = invisible, 1 = fully opaque). 0.10 = 90 % transparent.")]
    [Range(0f, 1f)]
    public float bodyAlpha = 0.10f;

    [Header("Outline")]
    public Color outlineColor     = Color.white;
    [Range(0f, 0.05f)]
    public float outlineThickness = 0.008f;

    [Header("Setup")]
    public bool applyOnStart = true;

    private Material[] _bodyMats;
    private Material   _outlineMat;
    private GameObject _outlineGO;

    void Start()
    {
        if (applyOnStart)
            Apply();
    }

    /// <summary>Apply transparency and outline to all Renderer children.</summary>
    public void Apply()
    {
        Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            Debug.LogWarning("[AvatarTransparency] No Renderer found under " + gameObject.name);
            return;
        }

        _bodyMats = new Material[renderers.Length];
        for (int i = 0; i < renderers.Length; i++)
        {
            // Clone material so we don't alter shared assets
            Material original = renderers[i].sharedMaterial;
            if (original == null) continue;

            Material mat = new Material(original);
            mat.name = original.name + "_Transparent";
            SetTransparent(mat, bodyAlpha);
            renderers[i].material = mat;
            _bodyMats[i] = mat;
        }

        BuildOutline(renderers);
    }

    // ── Outline ───────────────────────────────────────────────────────────────
    void BuildOutline(Renderer[] sourceRenderers)
    {
        _outlineMat = new Material(GetOutlineShader());
        _outlineMat.name = "OutlineMat";
        ConfigureOutlineMaterial(_outlineMat, outlineColor, outlineThickness);

        // One outline GO per skinned/mesh renderer
        _outlineGO = new GameObject("AvatarOutline");
        _outlineGO.transform.SetParent(transform, false);
        _outlineGO.transform.localPosition = Vector3.zero;
        _outlineGO.transform.localRotation = Quaternion.identity;
        _outlineGO.transform.localScale    = Vector3.one;

        foreach (var rend in sourceRenderers)
        {
            if (rend is SkinnedMeshRenderer smr)
            {
                var outlineSMR = _outlineGO.AddComponent<SkinnedMeshRenderer>();
                outlineSMR.sharedMesh       = smr.sharedMesh;
                outlineSMR.bones            = smr.bones;
                outlineSMR.rootBone         = smr.rootBone;
                outlineSMR.sharedMaterials  = new Material[] { _outlineMat };
                outlineSMR.shadowCastingMode= UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            else if (rend is MeshRenderer mr)
            {
                var mf     = rend.GetComponent<MeshFilter>();
                if (mf == null) continue;

                var outlineChild = new GameObject("OutlineChild");
                outlineChild.transform.SetParent(_outlineGO.transform, false);
                outlineChild.transform.localPosition = rend.transform.localPosition;
                outlineChild.transform.localRotation = rend.transform.localRotation;
                outlineChild.transform.localScale    = rend.transform.localScale;

                outlineChild.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                var outlineMR = outlineChild.AddComponent<MeshRenderer>();
                outlineMR.sharedMaterials  = new Material[] { _outlineMat };
                outlineMR.shadowCastingMode= UnityEngine.Rendering.ShadowCastingMode.Off;
            }
        }
    }

    // ── Shader helpers ────────────────────────────────────────────────────────
    static Shader GetOutlineShader()
    {
        // Look for a dedicated outline shader first
        Shader sh = Shader.Find("Custom/Outline");
        if (sh != null) return sh;

        // URP unlit fallback — we use front-face culling + vertex expansion
        sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh != null) return sh;

        return Shader.Find("Unlit/Color");
    }

    static void ConfigureOutlineMaterial(Material mat, Color color, float thickness)
    {
        // Expand vertices by setting a small scale — the material handles culling.
        // For the built-in Standard/Unlit, we rely on setting Cull Front in SetPass.
        // A proper outline shader (Custom/Outline) exposes _OutlineThickness.
        if (mat.HasProperty("_OutlineColor"))     mat.SetColor("_OutlineColor",     color);
        if (mat.HasProperty("_OutlineThickness"))  mat.SetFloat("_OutlineThickness", thickness);
        if (mat.HasProperty("_Color"))             mat.SetColor("_Color",            color);
        if (mat.HasProperty("_BaseColor"))         mat.SetColor("_BaseColor",        color);

        // Make cull Front so only back faces (extruded) are visible = rim silhouette
        mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Front);
        mat.SetFloat("_Surface", 0f); // opaque

        // Scale the outline GO slightly beyond 1 to push vertices outward
        // (works when the outline mesh is a copy positioned at the same origin).
    }

    static void SetTransparent(Material mat, float alpha)
    {
        if (mat.shader.name.Contains("Universal"))
        {
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend",   0f);
            mat.SetFloat("_ZWrite",  0f);
            mat.SetFloat("_AlphaClip", 0f);
            Color c = mat.GetColor("_BaseColor");
            c.a = alpha;
            mat.SetColor("_BaseColor", c);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = 3000;
        }
        else
        {
            // Standard shader
            mat.SetFloat("_Mode", 3f);
            mat.SetInt("_SrcBlend",  (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend",  (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite",    0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            Color c = mat.color;
            c.a = alpha;
            mat.color      = c;
            mat.renderQueue= 3000;
        }
    }

    void OnDestroy()
    {
        if (_outlineMat != null) Destroy(_outlineMat);
        if (_outlineGO  != null) Destroy(_outlineGO);
        if (_bodyMats   != null)
            foreach (var m in _bodyMats)
                if (m != null) Destroy(m);
    }
}
