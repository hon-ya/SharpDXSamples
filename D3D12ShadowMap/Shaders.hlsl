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
SamplerState g_Sampler : register(s0);

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

float4 PSMain(PixelShaderInput input) : SV_TARGET
{
	float depthBias = 0.000001f;

	float3 uv = input.lightViewPosition.xyz / input.lightViewPosition.w;
	uv.x = uv.x / 2.0f + 0.5f;
	uv.y = -uv.y / 2.0f + 0.5f;
	uv.z = uv.z - depthBias;

	float depth = g_Texture.Sample(g_Sampler, uv.xy).r;

	if (uv.z > depth)
	{
		// in shadow
		return input.color * 0.1f;
	}
	else
	{
		// out shadow
		return input.color;
	}
}
