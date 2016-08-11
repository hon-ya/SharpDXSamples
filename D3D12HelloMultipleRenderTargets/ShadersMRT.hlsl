struct VertexShaderInput
{
	float4 position : POSITION;
	float4 color0 : COLOR0;
	float4 color1 : COLOR1;
	float4 color2 : COLOR2;
	float4 color3 : COLOR3;
};

struct PixelShaderInput
{
	float4 position : SV_POSITION;
	float4 color0 : COLOR0;
	float4 color1 : COLOR1;
	float4 color2 : COLOR2;
	float4 color3 : COLOR3;
};

struct PixelSahderOutput
{
	float4 color0 : COLOR0;
	float4 color1 : COLOR1;
	float4 color2 : COLOR2;
	float4 color3 : COLOR3;
};

PixelShaderInput VSMain(VertexShaderInput input)
{
	PixelShaderInput output;

	output.position = input.position;
	output.color0 = input.color0;
	output.color1 = input.color1;
	output.color2 = input.color2;
	output.color3 = input.color3;

	return output;
}

PixelSahderOutput PSMain(PixelShaderInput input) : SV_TARGET
{
	PixelSahderOutput output;

	output.color0 = input.color0;
	output.color1 = input.color1;
	output.color2 = input.color2;
	output.color3 = input.color3;

	return output;
}
