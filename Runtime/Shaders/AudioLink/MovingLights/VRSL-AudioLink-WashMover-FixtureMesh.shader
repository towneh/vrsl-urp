Shader "VRSL-URP/AudioLink/Wash Mover/Fixture"
{
	Properties
	{
		//[Header (INSTANCED PROPERITES)]
		 [HideInInspector][Toggle] _PanInvert ("Invert Mover Pan", Int) = 0
		 [HideInInspector][Toggle] _TiltInvert ("Invert Mover Tilt", Int) = 0
		_RenderTextureMultiplier("Render Texture Multiplier", Range(1,10)) = 1

		 //[HideInInspector]_FinalStrobeFreq ("Final Strobe Frequency", Float) = 0
		 //[HideInInspector]_NewTimer("New Timer From Udon For Strobe", Float) = 0

		 //[Toggle] _EnableStrobe ("Enable Strobe", Int) = 0
		 [HideInInspector]_FixtureBaseRotationY("Mover Pan Offset (Blue + Green)", Range(-540,540)) = 0
		 [HideInInspector]_FixtureRotationX("Mover Tilt Offset (Blue)", Range(-180,180)) = 0
		 [HideInInspector]_ProjectionSelection ("GOBO Selection", Range(0,6)) = 0
		 [HideInInspector]_StrobeFreq("Strobe Frequency", Range(0,25)) = 1
		 [HideInInspector][Toggle] _EnableSpin("Enable Auto Spinning", Float) = 0

		[Header(Audio Section)]
         [Toggle]_EnableAudioLink("Enable Audio Link", Float) = 0
		 [Toggle] _EnableColorChord ("Enable Color Chord Tinting", Int) = 0
         _Band("Band", Float) = 0
         _BandMultiplier("Band Multiplier", Range(1, 15)) = 1
         _Delay("Delay", Float) = 0
         _NumBands("Num Bands", Float) = 4
         _AudioSpectrum("AudioSpectrum", 2D) = "black" {}
		 [Toggle]_UseTraditionalSampling("Use Traditional Texture Sampling", Int) = 0

		//[Header (BASIC CONTROLS)]
		_FinalIntensity("Final Intensity", Range(0,1)) = 1
		_GlobalIntensity("Global Intensity", Range(0,1)) = 1
		_GlobalIntensityBlend("Global Intensity Blend", Range(0,1)) = 1
		_UniversalIntensity ("Universal Intensity", Range (0,1)) = 1
		[HDR]_Emission("Light Color Tint", Color) = (1,1,1,1)
		_Saturation("Final Saturation", Range(0,1)) = 1
		_SaturationLength("Final Saturation Length", Range(0,0.2)) = 0.1
		_LensMaxBrightness("Lens Max Brightness", Range(0.0, 20)) = 5
		_ConeWidth("Cone Width", Range(0,5.5)) = 0
		_ConeLength("Cone Length", Range(1,10)) = 1
		_ConeSync ("Cone Scale Sync", Range(0,1)) = 0.2
		_FixutreIntensityMultiplier ("Intensity Multipler (For Bloom Scaling)", Range(1,10)) = 1

		//Color Texture Sampling Properties
		 [Toggle] _EnableColorTextureSample ("Enable Color Texture Sampling", Int) = 0
		 _SamplingTexture ("Texture To Sample From for Color", 2D) = "white" {}
		 _TextureColorSampleX ("X coordinate to sample the texture from", Range(0,1)) = 0.5
		 _TextureColorSampleY ("Y coordinate to sample the texture from", Range(0,1)) = 0.5

		// _BlockLengthX("OSC Block Base Distance X", Float) = 0.019231
		// _BlockLengthY("OSC Block Base Distance Y", Float) = 0

		// [Enum(UnityEngine.Rendering.BlendMode)] _BlendSrc ("Source Blend mode", Float) = 2
		// [Enum(UnityEngine.Rendering.BlendMode)] _BlendDst ("Destination Blend mode", Float) = 1
		// [Enum(UnityEngine.Rendering.BlendOp)] _BlendOp ("Blend Operation", Float) = 0
		//[Space(16)]

		//[Header(MOVER CONTROLS)]


		_FixtureRotationOrigin("Fixture Pivot Origin", Float) = (0, 0.014709, -1.02868, 0)
		// [Toggle] _UseRawGrid("Use Raw Grid For Light Intensity And Color", Int) = 0
		// [NoScaleOffset] _OSCGridRenderTextureRAW("OSC Grid Render Texture (RAW Unsmoothed)", 2D) = "white" {}
		// [NoScaleOffset] _OSCGridRenderTexture("OSC Grid Render Texture (To Control Lights)", 2D) = "white" {}
		// [NoScaleOffset] _OSCGridStrobeTimer ("OSC Grid Render Texture (For Strobe Timings", 2D) = "white" {}
		_MaxMinPanAngle("Max/Min Pan Angle (-x, x)", Float) = 180
		_MaxMinTiltAngle("Max/Min Tilt Angle (-y, y)", Float) = 180
		_FixtureMaxIntensity ("Maximum Cone Intensity",Range (0,0.5)) = 0.5
	
		//_Fade ("Fade mod", Range(0, 6)) = 1.5


		[Toggle] _EnableThemeColorSampling ("Enable Theme Color Sampling", Int) = 0
		 _ThemeColorTarget ("Choose Theme Color", Int) = 0

	//	[Space(16)]



	//	[Toggle] _UseWorldNorm("Use World Normal vs View Normal", Float) = 0
		//[KeywordEnum(None, UseDNTexture)] _DNEnabler ("Enable Depth Normal Texture", Float) = 0
	//	_ModX ("Projection UV X Stretch", Range(-2, 2)) = 1
	//	_ModY ("Projection UV Y Stretch", Range(-2, 2)) = 1
		


		//[Space(48)]
		//[Header(MAIN)]
		// [Enum(UVs, 0, Triplanar World, 1, Triplanar Object, 2)]_TextureSampleMode("Texture Mode", Int) = 0
		// _TriplanarFalloff("Triplanar Blend", Range(0.5,1)) = 1
		_MainTex("Main Texture", 2D) = "white" {}
	_Color("Color", Color) = (1,1,1,1)

		//[Space(16)]
//	[Header(NORMALS)]
	_BumpMap("Normal Map", 2D) = "bump" {}
	_BumpScale("Normal Scale", Range(-1,1)) = 1

		//[Space(16)]
	//[Header(METALLIC)]
	_MetallicGlossMap("Metallic Map", 2D) = "white" {}
	_Metallic("Metallic", Range(0,1)) = 0
	_Glossiness("Smoothness", Range(0,1)) = 0
	_OcclusionMap("Occlusion Map", 2D) = "white" {}
	_OcclusionStrength("Occlusion Strength", Range(0,1)) = 0
	_DecorativeEmissiveMap("Decorative Emissive Map", 2D) = "black" {}
	_DecorativeEmissiveMapStrength("Decorative Emissive Map Strength", Range(0,1)) = 0


	}

	SubShader
	{
		Tags { "Queue" = "AlphaTest+1" "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

		Pass
		{
			Name "UniversalForward"
			Tags { "LightMode" = "UniversalForward" }

			HLSLPROGRAM
			#pragma target 4.5
			#pragma vertex VRSLMoverVertex
			#pragma fragment VRSLMoverForwardFragment
			#pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
			#pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
			#pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
			#pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
			#pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
			#pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
			#pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
			#pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
			#pragma multi_compile_fragment _ _LIGHT_COOKIES
			#pragma multi_compile _ _LIGHT_LAYERS
			#pragma multi_compile_fragment _ _CLUSTER_LIGHT_LOOP
			#pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
			#pragma multi_compile _ SHADOWS_SHADOWMASK
			#pragma multi_compile _ PROBE_VOLUMES_L1 PROBE_VOLUMES_L2
			#pragma multi_compile_fog
			#pragma multi_compile_instancing
			#pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON

			#define VRSL_AUDIOLINK
			#define WASH
			#define VRSL_MOVER_FORWARD
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
			#include "Packages/town.mr.vrsl-urp/Runtime/Shaders/MovingLights/VRSL-MoverBody-URP.hlsl"
			ENDHLSL
		}

		Pass
		{
			Name "ShadowCaster"
			Tags { "LightMode" = "ShadowCaster" }
			ZWrite On
			ZTest LEqual
			ColorMask 0

			HLSLPROGRAM
			#pragma target 4.5
			#pragma vertex VRSLMoverShadowVertex
			#pragma fragment VRSLMoverShadowFragment
			#pragma multi_compile_instancing
			#pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

			#define VRSL_AUDIOLINK
			#define WASH
			#define VRSL_MOVER_SHADOW
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
			#include "Packages/town.mr.vrsl-urp/Runtime/Shaders/MovingLights/VRSL-MoverBody-URP.hlsl"
			ENDHLSL
		}

		Pass
		{
			Name "DepthOnly"
			Tags { "LightMode" = "DepthOnly" }
			ZWrite On
			ColorMask R

			HLSLPROGRAM
			#pragma target 4.5
			#pragma vertex VRSLMoverVertex
			#pragma fragment VRSLMoverDepthOnlyFragment
			#pragma multi_compile_instancing
			#pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON

			#define VRSL_AUDIOLINK
			#define WASH
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			#include "Packages/town.mr.vrsl-urp/Runtime/Shaders/MovingLights/VRSL-MoverBody-URP.hlsl"
			ENDHLSL
		}

		Pass
		{
			Name "DepthNormals"
			Tags { "LightMode" = "DepthNormals" }
			ZWrite On

			HLSLPROGRAM
			#pragma target 4.5
			#pragma vertex VRSLMoverVertex
			#pragma fragment VRSLMoverDepthNormalsFragment
			#pragma multi_compile_instancing
			#pragma multi_compile _ STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON
			#pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

			#define VRSL_AUDIOLINK
			#define WASH
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			#include "Packages/town.mr.vrsl-urp/Runtime/Shaders/MovingLights/VRSL-MoverBody-URP.hlsl"
			ENDHLSL
		}
	}

	CustomEditor "VRSLInspector"
}
