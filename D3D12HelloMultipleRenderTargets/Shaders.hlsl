struct VertexShaderInput
{
	float4 position : POSITION;
	float2 uv : TEXCOORD0;
};

struct PixelShaderInput
{
	float4 position : SV_POSITION;
	float2 uv : TEXCOORD0;
};

Texture2D g_Texture : register(t0);
SamplerState g_Sampler : register(s0);

PixelShaderInput VSMain(VertexShaderInput input)
{
	PixelShaderInput output;

	output.position = input.position;
	output.uv = input.uv;

	return output;
}

float4 PSMain(PixelShaderInput input) : SV_TARGET
{
	return g_Texture.Sample(g_Sampler, input.uv);
}
