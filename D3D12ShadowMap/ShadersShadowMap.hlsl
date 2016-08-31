cbuffer ConstantBuffer : register(b0)
{
	float4x4 modelMatrix;
	float4x4 viewMatrix;
	float4x4 projectionMatrix;
	float4x4 lightViewMatrix;
	float4x4 lightProjectionMatrix;
};

struct VertexShaderInput
{
	float4 position : POSITION;
};

struct PixelShaderInput
{
	float4 position : SV_POSITION;
};

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

