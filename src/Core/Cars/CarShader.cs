using UnityEngine;

namespace OverTheHillPlus.Cars;

/// <summary>
/// How the game's car shader picks its texture. With _IsSwitchable on it draws _Livery_Texture, which the car's
/// PropertyBlockSetter fills. With it off it draws _Main_Texture through _Tiling_Offset and never looks at the livery slot.
/// So a copy of a car body's material with the switch off comes out white (a body has no _Main_Texture), and a copy of an
/// add-on's material comes out in the colours of the game's add-on atlas at that material's tiling.
/// </summary>
public static class CarShader
{
    private const string Livery = "_Livery_Texture", Main = "_Main_Texture", Tiling = "_Tiling_Offset", Switchable = "_IsSwitchable";

    /// <summary>Makes a copied car material draw this texture on the model's own UVs, whatever livery the car wears.</summary>
    public static void KeepTexture(Material m, Texture albedo)
    {
        if (m.HasProperty(Livery)) m.SetTexture(Livery, albedo);
        if (m.HasProperty(Main)) m.SetTexture(Main, albedo);
        if (m.HasProperty(Tiling)) m.SetVector(Tiling, new Vector4(1f, 1f, 0f, 0f));
        if (m.HasProperty(Switchable)) m.SetFloat(Switchable, 0f);
    }
}
