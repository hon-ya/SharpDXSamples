using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace D3D12DynamicIndexing
{
    using SharpDX;
    using SharpDX.Direct3D12;
    using System.Runtime.InteropServices;

    class FrameResource : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 256)]
        public struct ConstantBufferDataStruct
        {
            public Matrix Mvp;
        }

        private CommandAllocator BundleAllocator;
        private readonly int CityRowCount;
        private readonly int CityColumnCount;
        private readonly int CityMaterialCount;
        private readonly float CitySpacingInterval;
        private IntPtr ConstantBufferUploadPtr;
        private Matrix[] ModelMatrices;

        public CommandAllocator CommandAllocator { get; private set; }
        public Resource ConstantBufferUpload { get; private set; }
        public GraphicsCommandList Bundle { get; private set; }

        public FrameResource(Device device, int cityRowCount, int cityColumnCount, int cityMaterialCount, float citySpacingInterval)
        {
            CityRowCount = cityRowCount;
            CityColumnCount = cityColumnCount;
            CityMaterialCount = cityMaterialCount;
            CitySpacingInterval = citySpacingInterval;

            ModelMatrices = new Matrix[CityRowCount * CityColumnCount];

            CommandAllocator = device.CreateCommandAllocator(CommandListType.Direct);
            BundleAllocator = device.CreateCommandAllocator(CommandListType.Bundle);

            ConstantBufferUpload = device.CreateCommittedResource(
                new HeapProperties(HeapType.Upload),
                HeapFlags.None,
                ResourceDescription.Buffer(Utilities.SizeOf<ConstantBufferDataStruct>() * CityRowCount * CityColumnCount),
                ResourceStates.GenericRead
                );

            ConstantBufferUploadPtr = ConstantBufferUpload.Map(0);

            SetCityPositions(citySpacingInterval, -citySpacingInterval);
        }

        public void Dispose()
        {
            CommandAllocator.Dispose();
            BundleAllocator.Dispose();
            Bundle.Dispose();
            ConstantBufferUpload.Dispose();
        }

        /// <summary>
        /// 各オブジェクトのモデル位置行列を設定する。
        /// </summary>
        /// <param name="intervalX"></param>
        /// <param name="intervalZ"></param>
        private void SetCityPositions(float intervalX, float intervalZ)
        {
            for(var i = 0; i < CityRowCount; i++)
            {
                var cityOffsetZ = i * intervalZ;

                for(var j = 0; j < CityColumnCount; j++)
                {
                    var cityOffsetX = j * intervalX;

                    ModelMatrices[i * CityColumnCount + j] = Matrix.Translation(
                        cityOffsetX,
                        0.02f * (i * CityColumnCount + j),
                        cityOffsetZ
                        );
                }
            }
        }

        /// <summary>
        /// バンドルを初期化します。
        /// </summary>
        /// <param name="device"></param>
        /// <param name="pipelineState"></param>
        /// <param name="frameResourceIndex"></param>
        /// <param name="indicesCount"></param>
        /// <param name="indexBufferView"></param>
        /// <param name="vertexBufferView"></param>
        /// <param name="cbvSrvUavViewHeap"></param>
        /// <param name="cbvSrvUavDescriptorSize"></param>
        /// <param name="samplerHeap"></param>
        /// <param name="rootSignature"></param>
        internal void InitBundle(Device device, PipelineState pipelineState, int frameResourceIndex, int indicesCount, IndexBufferView indexBufferView, VertexBufferView vertexBufferView, DescriptorHeap cbvSrvUavViewHeap, int cbvSrvUavDescriptorSize, DescriptorHeap samplerHeap, RootSignature rootSignature)
        {
            Bundle = device.CreateCommandList(CommandListType.Bundle, BundleAllocator, pipelineState);

            PopulateCommandList(Bundle, pipelineState, frameResourceIndex, indicesCount, indexBufferView,
                vertexBufferView, cbvSrvUavViewHeap, cbvSrvUavDescriptorSize, samplerHeap, rootSignature);

            Bundle.Close();
        }

        /// <summary>
        /// 各オブジェクトを描画するためのコマンドを積み込みます。
        /// </summary>
        /// <param name="commandList"></param>
        /// <param name="pipelineState"></param>
        /// <param name="frameResourceIndex"></param>
        /// <param name="indicesCount"></param>
        /// <param name="indexBufferView"></param>
        /// <param name="vertexBufferView"></param>
        /// <param name="cbvSrvUavViewHeap"></param>
        /// <param name="cbvSrvUavDescriptorSize"></param>
        /// <param name="samplerHeap"></param>
        /// <param name="rootSignature"></param>
        private void PopulateCommandList(GraphicsCommandList commandList, PipelineState pipelineState, int frameResourceIndex, int indicesCount, IndexBufferView indexBufferView, VertexBufferView vertexBufferView, DescriptorHeap cbvSrvUavViewHeap, int cbvSrvUavDescriptorSize, DescriptorHeap samplerHeap, RootSignature rootSignature)
        {
            commandList.SetGraphicsRootSignature(rootSignature);

            commandList.SetDescriptorHeaps(2, new []{ cbvSrvUavViewHeap, samplerHeap });
            commandList.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleList;
            commandList.SetIndexBuffer(indexBufferView);
            commandList.SetVertexBuffer(0, vertexBufferView);
            commandList.SetGraphicsRootDescriptorTable(0, cbvSrvUavViewHeap.GPUDescriptorHandleForHeapStart);
            commandList.SetGraphicsRootDescriptorTable(1, samplerHeap.GPUDescriptorHandleForHeapStart);

            var frameResourceDescriptorOffset = (1 + CityMaterialCount) + (frameResourceIndex * CityRowCount * CityColumnCount);

            var cbvSrvHandle = cbvSrvUavViewHeap.GPUDescriptorHandleForHeapStart;
            cbvSrvHandle += frameResourceDescriptorOffset * cbvSrvUavDescriptorSize;

            for(var i = 0; i < CityRowCount; i++)
            {
                for(var j = 0; j < CityColumnCount; j++)
                {
                    commandList.PipelineState = pipelineState;

                    // オブジェクト毎の CBV を設定
                    commandList.SetGraphicsRootDescriptorTable(2, cbvSrvHandle);
                    cbvSrvHandle += cbvSrvUavDescriptorSize;

                    // テクスチャ配列を参照するために使う動的インデックスの値を設定
                    commandList.SetGraphicsRoot32BitConstant(3, (i * CityColumnCount) + j, 0);

                    commandList.DrawIndexedInstanced(indicesCount, 1, 0, 0, 0);
                }
            }
        }

        /// <summary>
        /// 定数バッファの内容を更新します。
        /// 指定のビュー、プロジェクション行列を用いて、各オブジェクトの MVP 行列を作成し、更新します。
        /// </summary>
        /// <param name="view"></param>
        /// <param name="projection"></param>
        internal void UpdateConstantBuffers(Matrix view, Matrix projection)
        {
            var currentPtr = ConstantBufferUploadPtr;

            for (var i = 0; i < CityRowCount; i++)
            {
                for (var j = 0; j < CityColumnCount; j++)
                {
                    var model = ModelMatrices[i * CityColumnCount + j];
                    var mvp = Matrix.Transpose(model * view * projection);
                    var constantBufferData = new ConstantBufferDataStruct()
                    {
                        Mvp = mvp,
                    };

                    currentPtr = Utilities.WriteAndPosition(currentPtr, ref constantBufferData);
                }
            }
        }
    }
}
