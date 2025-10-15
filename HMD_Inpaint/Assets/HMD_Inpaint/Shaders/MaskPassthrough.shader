Shader "MaskedRetargeting/MaskPassthrough"
{
    Properties
    {
        _MainTex("Texture", 2D) = "white" {}
        _Inflation("Inflation", Float) = 0
        _InvertedAlpha("Inverted Alpha", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" }
        LOD 100

        Pass
        {
            ZWrite Off
            ZTest LEqual
            BlendOp RevSub, Min
            Blend Zero One, One One

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _Inflation;
            float _InvertedAlpha;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex + v.normal * _Inflation);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv);
                float alpha = lerp(1 - col.r, col.r, _InvertedAlpha);
                return fixed4(0, 0, 0, alpha);
            }
            ENDCG
        }
    }
}
