cbuffer ConstantBuffer : register(b0)
{
	float4x4 modelMatrix;
	float4x4 viewMatrix;
	float4x4 projectionMatrix;
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
};

PixelShaderInput VSMain(VertexShaderInput input)
{
	PixelShaderInput output;

	float4 position = input.position;
	position = mul(position, modelMatrix);
	position = mul(position, viewMatrix);
	position = mul(position, projectionMatrix);

	output.position = position;
	output.color = input.color;

	return output;
}

float4 PSMain(PixelShaderInput input) : SV_TARGET
{
	return input.color;
}
