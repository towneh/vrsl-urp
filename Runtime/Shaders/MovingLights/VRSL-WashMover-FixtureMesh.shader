Shader "VRSL-URP/Wash Mover/Fixture"
{
	Properties
	{
		//[Header (INSTANCED PROPERITES)]
		 [HideInInspector]_DMXChannel ("Starting DMX Channel", Int) = 0
		 [HideInInspector][Toggle] _NineUniverseMode ("Extended Universe Mode", Int) = 0
		 [HideInInspector][Toggle] _PanInvert ("Invert Mover Pan", Int) = 0
		 [HideInInspector][Toggle] _TiltInvert ("Invert Mover Tilt", Int) = 0
		 [Toggle] _EnableStrobe ("Enable Strobe", Int) = 0
		 [Toggle] _EnableDMX ("Enable Stream DMX/DMX Control", Int) = 0
         [Toggle] _EnableFineChannels ("Enable Fine Channels (For Pan/Tilt)", Int) = 0
		 [HideInInspector]_FixtureBaseRotationY("Mover Pan Offset (Blue + Green)", Range(-540,540)) = 0
		 [HideInInspector]_FixtureRotationX("Mover Tilt Offset (Blue)", Range(-180,180)) = 0
		 [HideInInspector]_ProjectionSelection ("GOBO Selection", Range(0,6)) = 0
		 [HideInInspector]_StrobeFreq("Strobe Frequency", Range(0,25)) = 1
		 [HideInInspector][Toggle] _EnableSpin("Enable Auto Spinning", Float) = 0
		_FinalIntensity("Final Intensity", Range(0,1)) = 1
		_GlobalIntensity("Global Intensity", Range(0,1)) = 1
		_GlobalIntensityBlend("Global Intensity Blend", Range(0,1)) = 1
		_UniversalIntensity ("Universal Intensity", Range (0,1)) = 1
		[HDR]_Emission("Light Color Tint", Color) = (1,1,1,1)
		_Saturation("Final Saturation", Range(0,1)) = 1
		_LensMaxBrightness("Lens Max Brightness", Range(0.00, 200)) = 5
		_ConeWidth("Cone Width", Range(0,5.5)) = 0
		_ConeLength("Cone Length", Range(1,10)) = 1
		_FixutreIntensityMultiplier ("Intensity Multipler (For Bloom Scaling)", Range(1,100)) = 1
		_FixtureRotationOrigin("Fixture Pivot Origin", Float) = (0, 0.014709, -1.02868, 0)
		_MaxMinPanAngle("Max/Min Pan Angle (-x, x)", Float) = 180
		_MaxMinTiltAngle("Max/Min Tilt Angle (-y, y)", Float) = 180
		_FixtureMaxIntensity ("Maximum Cone Intensity",Range (0,0.5)) = 0.5
		_MainTex("Main Texture", 2D) = "white" {}
		_Color("Color", Color) = (1,1,1,1)

		[Space(16)]
		[Header(NORMALS)]
		_BumpMap("Normal Map", 2D) = "bump" {}
		_BumpScale("Normal Scale", Range(-1,1)) = 1
		[Space(16)]
		[Header(METALLIC)]
		_MetallicGlossMap("Metallic Map", 2D) = "white" {}
		_Metallic("Metallic", Range(0,1)) = 0
		_Glossiness("Smoothness", Range(0,1)) = 0
		_OcclusionMap("Occlusion Map", 2D) = "white" {}
		_OcclusionStrength("Occlusion Strength", Range(0,1)) = 0
		_DecorativeEmissiveMap("Decorative Emissive Map", 2D) = "black" {}
		_DecorativeEmissiveMapStrength("Decorative Emissive Map Strength", Range(0,1)) = 0

		[Space(16)]
		[Header(LTCGI)]
		[Toggle(_VRSL_LTCGI)] _VRSL_LTCGI ("Lit by LTCGI Screens (needs the LTCGI package)", Float) = 0

	}

	SubShader
	{
		Tags { "Queue" = "AlphaTest+1" "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "LTCGI" = "_VRSL_LTCGI" }

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
			#pragma shader_feature_local_fragment _ _VRSL_LTCGI

			#define VRSL_DMX
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

			#define VRSL_DMX
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

			#define VRSL_DMX
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

			#define VRSL_DMX
			#define WASH
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			#include "Packages/town.mr.vrsl-urp/Runtime/Shaders/MovingLights/VRSL-MoverBody-URP.hlsl"
			ENDHLSL
		}
	}

}
