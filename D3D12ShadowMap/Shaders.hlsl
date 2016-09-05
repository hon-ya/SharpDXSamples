cbuffer ConstantBuffer : register(b0)
{
	float4x4 modelMatrix;
	float4x4 viewMatrix;
	float4x4 projectionMatrix;
	float4x4 lightViewMatrix;
	float4x4 lightProjectionMatrix;
	float4x4 biasMatrix;
};

struct VertexShaderInput
{
	float4 position : POSITION;
	float4 color : COLOR0;
};

struct PixelShaderInput
{
	float4 position : SV_POSITION;
	float4 color : COLOR0;
	float4 lightViewPosition : TEXCOORD0;
};

Texture2D g_Texture : register(t0);
SamplerComparisonState g_Sampler : register(s0);

// for creating shadow map
PixelShaderInput VSMainSM(VertexShaderInput input)
{
	PixelShaderInput output;

	float4 position = input.position;
	position = mul(position, modelMatrix);
	position = mul(position, lightViewMatrix);
	position = mul(position, lightProjectionMatrix);

	output.position = position;

	return output;
}

// for using shadow map
PixelShaderInput VSMain(VertexShaderInput input)
{
	PixelShaderInput output;

	float4 position = input.position;
	position = mul(position, modelMatrix);
	position = mul(position, viewMatrix);
	position = mul(position, projectionMatrix);

	float4 lightViewPosition = input.position;
	lightViewPosition = mul(lightViewPosition, modelMatrix);
	lightViewPosition = mul(lightViewPosition, lightViewMatrix);
	lightViewPosition = mul(lightViewPosition, lightProjectionMatrix);

	output.position = position;
	output.color = input.color;
	output.lightViewPosition = lightViewPosition;

	return output;
}

// for using shadow map
float4 PSMain(PixelShaderInput input) : SV_TARGET
{
	float depthBias = 0.000001f;

	float3 uv = input.lightViewPosition.xyz / input.lightViewPosition.w;
	uv.x = uv.x / 2.0f + 0.5f;
	uv.y = -uv.y / 2.0f + 0.5f;
	uv.z = uv.z - depthBias;

	float depth = g_Texture.SampleCmpLevelZero(g_Sampler, uv.xy, uv.z).r;

	return input.color * depth;
}
