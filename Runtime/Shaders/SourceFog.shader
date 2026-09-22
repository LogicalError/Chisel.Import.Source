// Source's fog over the frame: linear between two distances and capped at a maximum density, which is the part URP's
// own fog cannot express (viewrender.cpp, CViewRender::EnableWorldFog). Blended on the gamma-encoded colour, as Source
// blends it, then taken back to linear for the HDR target.
//
// The sky is never fogged. Source's world fog is for the world; a 3D skybox carries its own, which its view is drawn
// with (CSkyboxView::Enable3dSkyboxFog), and the 2D sky is not fogged at all. Two things say "sky" here:
//
//   - alpha 0, which Chisel/Source/Sky Face writes on every sky face in every view. It has to be a mark rather than a
//     depth test, because a sky face is an ordinary opaque surface: it writes the depth of a wall that may be metres
//     away, and fogging the vista by the distance of the wall it is painted on would lay the level's haze over it.
//   - depth at the far plane, which is the 2D skybox, drawn where nothing else was.
//
// The mark does not leave this pass: every path below returns alpha 1, so nothing downstream ever sees the 0. Without
// that, the mark would reach URP's post chain, where an opaque surface with no alpha is not something to rely on.
Shader "Hidden/Chisel/Source/Fog"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off Cull Off ZTest Always

        Pass
        {
            Name "SourceFog"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            // Blit.hlsl lives in core's Runtime/Utilities, not in URP's ShaderLibrary. Getting that wrong does not
            // fail quietly: the shader does not compile, Unity draws it with the error shader, and a full-screen pass
            // then paints the frame one flat colour - which is what this did the first time it ran.
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            // The pass's own depth, declared and bound by SourceFogFeature. Not _CameraDepthTexture: that one is only
            // bound when something in the frame happens to have set it, and a fog that reads a constant depth covers
            // the frame in fog colour instead of doing nothing, which is the worse way to fail.
            TEXTURE2D_X(_SourceFogDepth);

            float4 _SourceFogColor;             // primary, gamma encoded
            float4 _SourceFogColorSecondary;    // secondary, gamma encoded
            float4 _SourceFogParams;            // start, end, maxdensity, blend
            float4 _SourceFogDirection;         // dirPrimary in world space

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                half4 scene = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);

                // A sky face said so. The test is "is there NO geometry in this pixel", and it is exact rather than a
                // compromise: a sky face writes alpha exactly 0, and any fragment the alpha test lets through writes
                // an alpha above 0 - a covered sample cannot contribute 0. So a pixel resolves to 0 only when every
                // one of its samples is sky. The epsilon is there for the target's 8-bit quantisation and nothing
                // else, which is why it is small and not 0.5.
                //
                // It has to be small. Alpha to coverage makes a cutout fragment export its COVERAGE (URP's AlphaClip
                // -> SharpenAlphaStrict), so a fence edge resolves to a fraction, and at 0.5 every antialiased edge
                // pixel against sky would have escaped the fog - a sharp unfogged fringe around exactly the surfaces
                // the coverage mask was added to smooth. At 0.05 a pixel with any real geometry in it is fogged, and
                // one that is essentially all sky is not.
                if (scene.a < 0.05)
                    return half4(scene.rgb, 1);

                float rawDepth = LOAD_TEXTURE2D_X(_SourceFogDepth, uint2(input.positionCS.xy)).r;
                // Nothing in front of the far plane means the 2D skybox, and that is not fogged either
                #if UNITY_REVERSED_Z
                    if (rawDepth <= 1e-5)
                        return half4(scene.rgb, 1);
                #else
                    if (rawDepth >= 1.0 - 1e-5)
                        return half4(scene.rgb, 1);
                #endif

                // Distance along the view, as Source measures it
                float distance = LinearEyeDepth(rawDepth, _ZBufferParams);

                float start = _SourceFogParams.x;
                float end   = _SourceFogParams.y;
                float density = saturate((distance - start) / max(end - start, 1e-5)) * _SourceFogParams.z;
                if (density <= 0)
                    return half4(scene.rgb, 1);

                float3 fog = _SourceFogColor.rgb;
                if (_SourceFogParams.w > 0.5)
                {
                    // 0.5 * dot(forward, dirPrimary) + 0.5, on the gamma values, as GetFogColor does it
                    float3 forward = -normalize(UNITY_MATRIX_V[2].xyz);
                    float factor = 0.5 * dot(forward, _SourceFogDirection.xyz) + 0.5;
                    fog = lerp(_SourceFogColorSecondary.rgb, _SourceFogColor.rgb, factor);
                }

                // Source mixes in gamma space; the target is linear
                float3 sceneGamma = LinearToSRGB(max(scene.rgb, 0));
                float3 mixed = lerp(sceneGamma, fog, density);
                return half4(SRGBToLinear(mixed), 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
