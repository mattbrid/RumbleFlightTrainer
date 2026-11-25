using Il2CppTMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace RumbleFlightTrainer;

internal sealed partial class FlightHudView
{
    private const float TextOutlineWidth = 0.50f;
    // The Gym leaderboard's NameCanvas uses this GoodDog Plain TMP font asset.
    private const string LeaderboardFontName = "RUMBLE Text Material";

    private TMP_FontAsset? hudFont;
    private Material? hudTextMaterial;

    private TextMeshPro CreateHudLabel(Transform parent, string name, float scale, Vector2 size, TextAlignmentOptions alignment)
    {
        var text = Child(parent, name).AddComponent<TextMeshPro>();
        text.transform.localScale = Vector3.one * scale;
        text.rectTransform.sizeDelta = size;
        text.rectTransform.pivot = new Vector2(0.5f, alignment == TextAlignmentOptions.Top ? 1f : 0.5f);
        hudFont ??= Resources.FindObjectsOfTypeAll<TMP_FontAsset>()
            .FirstOrDefault(font => font.name == LeaderboardFontName) ?? text.font;
        text.font = hudFont;
        text.fontSharedMaterial = hudTextMaterial ??= CreateHudTextMaterial(hudFont.material);
        text.fontSize = 8.8f;
        text.alignment = alignment;
        text.richText = false;
        text.extraPadding = true;
        text.text = string.Empty;
        text.color = Color.white;
        text.UpdateMeshPadding();

        var renderer = text.GetComponent<MeshRenderer>();
        renderer.sortingOrder = (int)DrawOrder.Text * 2;
        renderer.allowOcclusionWhenDynamic = false;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return text;
    }

    private Material CreateHudTextMaterial(Material fontMaterial)
    {
        // Use the game's mobile SDF pass for both the face and outline. Its ZTest is configurable.
        var material = new Material(fontMaterial);
        materials.Add(material);
        Shader sdf = Shader.Find("TextMeshPro/Mobile/Distance Field");
        if (sdf != null)
            material.shader = sdf;
        material.SetFloat("unity_GUIZTestMode", (float)CompareFunction.Always);
        material.renderQueue = (int)RenderQueue.Overlay;
        material.SetColor("_FaceColor", Color.white);
        material.SetColor("_OutlineColor", Color.black);
        // Equal dilation moves the outline outside the original glyph contour.
        material.SetFloat("_FaceDilate", TextOutlineWidth);
        material.SetFloat("_OutlineWidth", TextOutlineWidth);
        material.SetFloat("_OutlineSoftness", 0f);
        material.EnableKeyword("OUTLINE_ON");
        return material;
    }
}
