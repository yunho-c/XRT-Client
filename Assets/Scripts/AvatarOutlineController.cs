using UnityEngine;

public class AvatarOutlineController : MonoBehaviour
{
    [Header("Target")]
    public SkinnedMeshRenderer avatarRenderer;

    [Header("Body Appearance")]
    [Range(0f, 1f)]    public float hue        = 0.00f;
    [Range(0f, 1f)]    public float saturation  = 0.00f;
    [Range(0f, 1f)]    public float brightness  = 1.00f;
    [Range(0f, 1f)]    public float alpha       = 0.10f;

    void OnValidate() => Apply();
    void Start()      => Apply();

    void Apply()
    {
        if (avatarRenderer == null) return;
        Color c = Color.HSVToRGB(hue, saturation, brightness);
        c.a = alpha;

        for (int i = 0; i < avatarRenderer.sharedMaterials.Length; i++)
        {
            var mat = avatarRenderer.sharedMaterials[i];
            if (mat == null) continue;
            var mpb = new MaterialPropertyBlock();
            avatarRenderer.GetPropertyBlock(mpb, i);
            mpb.SetColor("_Color", c);
            avatarRenderer.SetPropertyBlock(mpb, i);
        }
    }

    void OnDestroy()
    {
        if (avatarRenderer == null) return;
        for (int i = 0; i < avatarRenderer.sharedMaterials.Length; i++)
            avatarRenderer.SetPropertyBlock(null, i);
    }
}
