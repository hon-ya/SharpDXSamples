#define threadBlockSize 128

struct ComputeInput
{
	int number;
};

struct ComputeOutput
{
	int result;
};

struct CSInput
{
	uint3 groupId : SV_GroupID;
	uint groupIndex : SV_GroupIndex;
};

StructuredBuffer<ComputeInput> computeInputs : register(t0);
RWStructuredBuffer<ComputeOutput> computeOutputs : register(u0);

[numthreads(threadBlockSize, 1, 1)]
void CSMain(CSInput input)
{
	uint index = (input.groupId.x * threadBlockSize) + input.groupIndex;

	int number = computeInputs[index].number;

	computeOutputs[index].result = number * number;
}
