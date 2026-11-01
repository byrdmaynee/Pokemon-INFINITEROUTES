using Godot;
using InfiniteRoutes.Extractor.Extraction;

namespace InfiniteRoutes.Client.World;

/// <summary>
/// Overworld character sprites (the player and NPCs). They face the camera like the DS's billboards, but are
/// depth-tested as if standing upright on their feet. A camera-facing quad leans back with the camera's pitch, so
/// ground rising behind a character (stairs, escalators, raised floors) used to cut through it; an upright stand-in
/// only meets what is really in front of the character, such as a counter or a wall.
/// </summary>
public static class CharacterSprite
{
    const string Code = """
        shader_type spatial;
        render_mode unshaded, cull_disabled;

        uniform sampler2D sheet : source_color, filter_nearest;
        // A little extra pull toward the camera, for depth only, so feet don't flicker against the ground.
        uniform float depth_pull = 0.3;

        void vertex() {
            vec3 origin = MODEL_MATRIX[3].xyz;
            vec3 right = INV_VIEW_MATRIX[0].xyz;
            vec3 shown = origin + right * VERTEX.x + INV_VIEW_MATRIX[1].xyz * VERTEX.y;
            vec3 standing = origin + right * VERTEX.x + vec3(0.0, VERTEX.y, 0.0);
            vec4 clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(shown, 1.0);
            vec3 eye = (VIEW_MATRIX * vec4(standing, 1.0)).xyz;
            eye += normalize(-eye) * depth_pull;
            vec4 depth = PROJECTION_MATRIX * vec4(eye, 1.0);
            POSITION = vec4(clip.xy, depth.z / depth.w * clip.w, clip.w);
        }

        void fragment() {
            vec4 color = texture(sheet, UV);
            if (color.a < 0.5)
                discard;
            ALBEDO = color.rgb;
        }
        """;

    static Shader? _shader;

    /// <summary>A sprite for a <see cref="CharacterSheet"/> whose frames are <paramref name="frameSize"/> pixels square.</summary>
    public static Sprite3D Create(Texture2D? sheet, int frameSize = CharacterSheet.FrameSize)
    {
        _shader ??= new Shader { Code = Code };
        var material = new ShaderMaterial { Shader = _shader };
        if (sheet is not null)
            material.SetShaderParameter("sheet", sheet);
        return new Sprite3D
        {
            Texture = sheet,
            Hframes = CharacterSheet.Columns,
            Vframes = CharacterSheet.Rows,
            // 32 px frames on a 16 px tile grid: 1/16 world units per pixel, feet on the ground.
            PixelSize = 1f / 16,
            Offset = new Vector2(0, frameSize / 2f - 2),
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
            MaterialOverride = material,
        };
    }
}
