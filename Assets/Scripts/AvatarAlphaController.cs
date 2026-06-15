using UnityEngine;

public class AvatarAlphaController : MonoBehaviour
{
    public SkinnedMeshRenderer avatarRenderer;

    [Range(0f, 1f)]
    public float alpha = 0.1f;

    void OnValidate()
    {
        if (avatarRenderer == null) return;
        var mpb = new MaterialPropertyBlock();
        avatarRenderer.GetPropertyBlock(mpb);
        Color c = avatarRenderer.sharedMaterial != null
            ? avatarRenderer.sharedMaterial.GetColor("_Color")
            : Color.white;
        c.a = alpha;
        mpb.SetColor("_Color", c);
        avatarRenderer.SetPropertyBlock(mpb);
    }

    void Start() => OnValidate();

    void OnDestroy()
    {
        if (avatarRenderer != null)
            avatarRenderer.SetPropertyBlock(null);
    }
}
