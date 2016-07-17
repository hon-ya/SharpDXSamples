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

	output.position = input.position;
	output.color = input.color;

	return output;
}

float4 PSMain(PixelShaderInput input) : SV_TARGET
{
	return input.color;
}
