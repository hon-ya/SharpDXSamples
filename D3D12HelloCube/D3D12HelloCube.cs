using System;
using System.Threading;
using SharpDX.DXGI;

namespace D3D12HelloCube
{
    using SharpDX;
    using SharpDX.Windows;
    using SharpDX.Direct3D12;

    internal class D3D12HelloCube : IDisposable
    {
        private struct Vertex
        {
            public Vector3 Position;
            public Vector4 Color;
        };

        private struct ConstantBufferDataStruct
        {
            public Matrix Model;
            public Matrix View;
            public Matrix Projection;
        }

        private const int FrameCount = 2;

        private int FrameNumber = 0;

        private Device Device;
        private CommandQueue CommandQueue;
        private SwapChain3 SwapChain;
        private int FrameIndex;
        private DescriptorHeap RenderTargetViewHeap;
        private int RtvDescriptorSize;
        private Resource[] RenderTargets = new Resource[FrameCount];
        private CommandAllocator CommandAllocator;
        private GraphicsCommandList CommandList;
        private int FenceValue;
        private Fence Fence;
        private AutoResetEvent FenceEvent;
        private ViewportF Viewport;
        private Rectangle ScissorRect;
        private RootSignature RootSignature;
        private PipelineState PipelineState;
        private Resource VertexBuffer;
        private VertexBufferView VertexBufferView;
        private Resource IndexBuffer;
        private IndexBufferView IndexBufferView;
        private Resource ConstantBuffer;
        private DescriptorHeap ConstantBufferViewHeap;
        private ConstantBufferDataStruct ConstantBufferData;
        private IntPtr ConstantBufferPtr;

        public void Dispose()
        {
            WaitForPreviousFrame();

            foreach (var target in RenderTargets)
            {
                target.Dispose();
            }

            CommandAllocator.Dispose();
            CommandQueue.Dispose();
            RootSignature.Dispose();
            RenderTargetViewHeap.Dispose();
            ConstantBufferViewHeap.Dispose();
            PipelineState.Dispose();
            CommandList.Dispose();
            VertexBuffer.Dispose();
            IndexBuffer.Dispose();
            ConstantBuffer.Dispose();
            Fence.Dispose();
            SwapChain.Dispose();
            Device.Dispose();
        }

        internal void Initialize(RenderForm form)
        {
            LoadPipeline(form);
            LoadAssets();
        }

        private void LoadPipeline(RenderForm form)
        {
            var width = form.ClientSize.Width;
            var height = form.ClientSize.Height;

            Viewport = new ViewportF(0, 0, width, height, 0.0f, 1.0f);
            ScissorRect = new Rectangle(0, 0, width, height);

#if DEBUG
            {
                DebugInterface.Get().EnableDebugLayer();
            }
#endif

            Device = new Device(null, SharpDX.Direct3D.FeatureLevel.Level_11_0);

            using (var factory = new Factory4())
            {
                var commandQueueDesc = new CommandQueueDescription(CommandListType.Direct);
                CommandQueue = Device.CreateCommandQueue(commandQueueDesc);

                var swapChainDesc = new SwapChainDescription()
                {
                    BufferCount = FrameCount,
                    ModeDescription = new ModeDescription(width, height, new Rational(60, 1), Format.R8G8B8A8_UNorm),
                    Usage = Usage.RenderTargetOutput,
                    SwapEffect = SwapEffect.FlipDiscard,
                    OutputHandle = form.Handle,
                    Flags = SwapChainFlags.None,
                    SampleDescription = new SampleDescription(1, 0),
                    IsWindowed = true,
                };
                using (var tempSwapChain = new SwapChain(factory, CommandQueue, swapChainDesc))
                {
                    SwapChain = tempSwapChain.QueryInterface<SwapChain3>();
                    FrameIndex = SwapChain.CurrentBackBufferIndex;
                }

                factory.MakeWindowAssociation(form.Handle, WindowAssociationFlags.IgnoreAltEnter);
            }

            var rtvHeapDesc = new DescriptorHeapDescription()
            {
                DescriptorCount = FrameCount,
                Type = DescriptorHeapType.RenderTargetView,
                Flags = DescriptorHeapFlags.None,
            };
            RenderTargetViewHeap = Device.CreateDescriptorHeap(rtvHeapDesc);
            RtvDescriptorSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);

            // コンスタントバッファビューのデスクリプタヒープを作成します。
            var cbvHeapDesc = new DescriptorHeapDescription()
            {
                DescriptorCount = 1,
                Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                Flags = DescriptorHeapFlags.ShaderVisible,
            };
            ConstantBufferViewHeap = Device.CreateDescriptorHeap(cbvHeapDesc);

            var rtvDescHandle = RenderTargetViewHeap.CPUDescriptorHandleForHeapStart;
            for (var i = 0; i < FrameCount; i++)
            {
                RenderTargets[i] = SwapChain.GetBackBuffer<Resource>(i);
                Device.CreateRenderTargetView(RenderTargets[i], null, rtvDescHandle);
                rtvDescHandle += RtvDescriptorSize;
            }

            CommandAllocator = Device.CreateCommandAllocator(CommandListType.Direct);
        }

        private void LoadAssets()
        {
            // コンスタントバッファを扱うルートシグネチャを生成します。
            var rootSignatureDesc = new RootSignatureDescription(RootSignatureFlags.AllowInputAssemblerInputLayout,
                // Root Parameters
                new[]
                {
                    // ルートパラメータ 0:
                    // コンスタントバッファレジスタ 0 のコンスタントバッファ（頂点シェーダからのみ参照）
                    new RootParameter(ShaderVisibility.Vertex,
                        new DescriptorRange()
                        {
                            RangeType = DescriptorRangeType.ConstantBufferView,
                            BaseShaderRegister = 0,
                            OffsetInDescriptorsFromTableStart = int.MinValue,
                            DescriptorCount = 1,
                        })
                });
            RootSignature = Device.CreateRootSignature(rootSignatureDesc.Serialize());

            // シェーダをロードします。デバッグビルドのときは、デバッグフラグを立てます。
#if DEBUG
            var vertexShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Shaders.hlsl", "VSMain", "vs_5_0", SharpDX.D3DCompiler.ShaderFlags.Debug));
            var pixelShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Shaders.hlsl", "PSMain", "ps_5_0", SharpDX.D3DCompiler.ShaderFlags.Debug));
#else
            var vertexShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Shaders.hlsl", "VSMain", "vs_5_0"));
            var pixelShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Shaders.hlsl", "PSMain", "ps_5_0"));
#endif

            // 頂点レイアウトを定義します。
            var inputElementDescs = new []
            {
                new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElement("COLOR", 0, Format.R32G32B32A32_Float, 12, 0),
            };

            // グラフィックスパイプラインステートオブジェクトを作成します。
            var psoDesc = new GraphicsPipelineStateDescription()
            {
                InputLayout = new InputLayoutDescription(inputElementDescs),
                RootSignature = RootSignature,
                VertexShader = vertexShader,
                PixelShader = pixelShader,
                RasterizerState = RasterizerStateDescription.Default(),
                BlendState = BlendStateDescription.Default(),
                DepthStencilFormat = Format.D32_Float,
                DepthStencilState = new DepthStencilStateDescription()
                {
                    IsDepthEnabled = false,
                    IsStencilEnabled = false,
                },
                SampleMask = int.MaxValue,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RenderTargetCount = 1,
                Flags = PipelineStateFlags.None,
                SampleDescription = new SampleDescription(1, 0),
                StreamOutput = new StreamOutputDescription(),
            };
            psoDesc.RenderTargetFormats[0] = Format.R8G8B8A8_UNorm;

            PipelineState = Device.CreateGraphicsPipelineState(psoDesc);

            CommandList = Device.CreateCommandList(CommandListType.Direct, CommandAllocator, PipelineState);
            CommandList.Close();

            // 頂点データを定義します。
            var cubeVertices = new[]
            {
                // front
                new Vertex { Position = new Vector3(-1.0f, -1.0f,  1.0f), Color = new Vector4(1.0f, 0.0f, 0.0f, 0.0f) },
                new Vertex { Position = new Vector3(-1.0f,  1.0f,  1.0f), Color = new Vector4(1.0f, 0.0f, 0.0f, 0.0f) },
                new Vertex { Position = new Vector3( 1.0f,  1.0f,  1.0f), Color = new Vector4(1.0f, 0.0f, 0.0f, 0.0f) },
                new Vertex { Position = new Vector3( 1.0f, -1.0f,  1.0f), Color = new Vector4(1.0f, 0.0f, 0.0f, 0.0f) },

                // back
                new Vertex { Position = new Vector3(-1.0f, -1.0f, -1.0f), Color = new Vector4(0.0f, 1.0f, 0.0f, 0.0f) },
                new Vertex { Position = new Vector3( 1.0f, -1.0f, -1.0f), Color = new Vector4(0.0f, 1.0f, 0.0f, 0.0f) },
                new Vertex { Position = new Vector3( 1.0f,  1.0f, -1.0f), Color = new Vector4(0.0f, 1.0f, 0.0f, 0.0f) },
                new Vertex { Position = new Vector3(-1.0f,  1.0f, -1.0f), Color = new Vector4(0.0f, 1.0f, 0.0f, 0.0f) },

                // top
                new Vertex { Position = new Vector3(-1.0f,  1.0f, -1.0f), Color = new Vector4(0.0f, 0.0f, 1.0f, 0.0f) },
                new Vertex { Position = new Vector3( 1.0f,  1.0f, -1.0f), Color = new Vector4(0.0f, 0.0f, 1.0f, 0.0f) },
                new Vertex { Position = new Vector3( 1.0f,  1.0f,  1.0f), Color = new Vector4(0.0f, 0.0f, 1.0f, 0.0f) },
                new Vertex { Position = new Vector3(-1.0f,  1.0f,  1.0f), Color = new Vector4(0.0f, 0.0f, 1.0f, 0.0f) },

                // bottom
                new Vertex { Position = new Vector3(-1.0f, -1.0f, -1.0f), Color = new Vector4(0.0f, 0.0f, 0.0f, 1.0f) },
                new Vertex { Position = new Vector3(-1.0f, -1.0f,  1.0f), Color = new Vector4(0.0f, 0.0f, 0.0f, 1.0f) },
                new Vertex { Position = new Vector3( 1.0f, -1.0f,  1.0f), Color = new Vector4(0.0f, 0.0f, 0.0f, 1.0f) },
                new Vertex { Position = new Vector3( 1.0f, -1.0f, -1.0f), Color = new Vector4(0.0f, 0.0f, 0.0f, 1.0f) },

                // right
                new Vertex { Position = new Vector3( 1.0f, -1.0f, -1.0f), Color = new Vector4(1.0f, 1.0f, 0.0f, 0.0f) },
                new Vertex { Position = new Vector3( 1.0f, -1.0f,  1.0f), Color = new Vector4(1.0f, 1.0f, 0.0f, 0.0f) },
                new Vertex { Position = new Vector3( 1.0f,  1.0f,  1.0f), Color = new Vector4(1.0f, 1.0f, 0.0f, 0.0f) },
                new Vertex { Position = new Vector3( 1.0f,  1.0f, -1.0f), Color = new Vector4(1.0f, 1.0f, 0.0f, 0.0f) },

                // left
                new Vertex { Position = new Vector3(-1.0f, -1.0f, -1.0f), Color = new Vector4(1.0f, 0.0f, 1.0f, 0.0f) },
                new Vertex { Position = new Vector3(-1.0f,  1.0f, -1.0f), Color = new Vector4(1.0f, 0.0f, 1.0f, 0.0f) },
                new Vertex { Position = new Vector3(-1.0f,  1.0f,  1.0f), Color = new Vector4(1.0f, 0.0f, 1.0f, 0.0f) },
                new Vertex { Position = new Vector3(-1.0f, -1.0f,  1.0f), Color = new Vector4(1.0f, 0.0f, 1.0f, 0.0f) },
            };
            var vertexBufferSize = Utilities.SizeOf(cubeVertices);

            // インデックスデータを定義します。
            var cubeIndices = new UInt16[]
            {
                0, 1, 2, 0, 2, 3,		// front
			    4, 5, 6, 4, 6, 7,		// back
			    8, 9, 10, 8, 10, 11,	// top
			    12, 13, 14, 12, 14, 15,	// bottom
			    16, 17, 18, 16, 18, 19,	// right
			    20, 21, 22, 20, 22, 23  // left
            };
            var indexBufferSize = Utilities.SizeOf(cubeIndices);

            // 頂点バッファを作成します。
            VertexBuffer = Device.CreateCommittedResource(
                new HeapProperties(HeapType.Upload), 
                HeapFlags.None, 
                ResourceDescription.Buffer(vertexBufferSize), 
                ResourceStates.GenericRead
                );

            // 頂点データを頂点バッファに書き込みます。
            var pVertexDataBegin = VertexBuffer.Map(0);
            {
                Utilities.Write(pVertexDataBegin, cubeVertices, 0, cubeVertices.Length);
            }
            VertexBuffer.Unmap(0);

            // 頂点バッファビューを作成します。
            VertexBufferView = new VertexBufferView()
            {
                BufferLocation = VertexBuffer.GPUVirtualAddress,
                StrideInBytes = Utilities.SizeOf<Vertex>(),
                SizeInBytes = vertexBufferSize,
            };

            // インデックスバッファを作成します。
            IndexBuffer = Device.CreateCommittedResource(
                new HeapProperties(HeapType.Upload),
                HeapFlags.None,
                ResourceDescription.Buffer(indexBufferSize),
                ResourceStates.GenericRead
                );

            // インデックスデータをインデックスバッファに書き込みます。
            var pIndexDataBegin = IndexBuffer.Map(0);
            {
                Utilities.Write(pIndexDataBegin, cubeIndices, 0, cubeIndices.Length);
            }
            IndexBuffer.Unmap(0);

            // インデックスバッファビューを作成します。
            IndexBufferView = new IndexBufferView()
            {
                BufferLocation = IndexBuffer.GPUVirtualAddress,
                Format = Format.R16_UInt,
                SizeInBytes = indexBufferSize,
            };

            // コンスタントバッファを作成します。
            ConstantBuffer = Device.CreateCommittedResource(
                new HeapProperties(HeapType.Upload),
                HeapFlags.None,
                ResourceDescription.Buffer(1024 * 64),
                ResourceStates.GenericRead
                );

            // コンスタントバッファビューを作成します。
            var cbvDesc = new ConstantBufferViewDescription()
            {
                BufferLocation = ConstantBuffer.GPUVirtualAddress,
                SizeInBytes = (Utilities.SizeOf<ConstantBufferDataStruct>() + 255) & ~255,
            };
            Device.CreateConstantBufferView(cbvDesc, ConstantBufferViewHeap.CPUDescriptorHandleForHeapStart);

            // コンスタントバッファを初期化します。
            // コンスタントバッファは、アプリ終了までマップしたままにします。
            ConstantBufferPtr = ConstantBuffer.Map(0);
            Utilities.Write(ConstantBufferPtr, ref ConstantBufferData);

            Fence = Device.CreateFence(0, FenceFlags.None);
            FenceValue = 1;

            FenceEvent = new AutoResetEvent(false);
        }

        internal void Update()
        {
            var model = Matrix.RotationAxis(
                    new Vector3(0.0f, 1.0f, 0.0f),
                    MathUtil.DegreesToRadians(FrameNumber % 360)
                    );

            var view = Matrix.LookAtRH(
                    new Vector3(3.0f, 2.0f, 3.0f),
                    new Vector3(0.0f, 0.0f, 0.0f),
                    new Vector3(0.0f, 1.0f, 0.0f)
                    );

            var projection = Matrix.PerspectiveFovRH(
                    MathUtil.DegreesToRadians(60.0f),
                    Viewport.Width / Viewport.Height,
                    0.0001f,
                    100.0f
                    );

            ConstantBufferData.Model = Matrix.Transpose(model);
            ConstantBufferData.View = Matrix.Transpose(view);
            ConstantBufferData.Projection = Matrix.Transpose(projection);

            Utilities.Write(ConstantBufferPtr, ref ConstantBufferData);
        }

        internal void Render()
        {
            PopulateCommandList();

            CommandQueue.ExecuteCommandList(CommandList);

            SwapChain.Present(1, PresentFlags.None);

            WaitForPreviousFrame();
        }

        private void PopulateCommandList()
        {
            CommandAllocator.Reset();

            CommandList.Reset(CommandAllocator, PipelineState);

            // 必要な各種ステートを設定します。
            CommandList.SetGraphicsRootSignature(RootSignature);

            // 使用するデスクリプタヒープを設定します。
            CommandList.SetDescriptorHeaps(1, new[] { ConstantBufferViewHeap });
            // デスクリプタテーブルのルートパラメータ 0 番に対応するデスクリプタとして、コンスタントバッファビューを渡します。
            CommandList.SetGraphicsRootDescriptorTable(0, ConstantBufferViewHeap.GPUDescriptorHandleForHeapStart);

            CommandList.SetViewport(Viewport);
            CommandList.SetScissorRectangles(ScissorRect);

            CommandList.ResourceBarrierTransition(RenderTargets[FrameIndex], ResourceStates.Present, ResourceStates.RenderTarget);

            var rtvDescHandle = RenderTargetViewHeap.CPUDescriptorHandleForHeapStart;
            rtvDescHandle += FrameIndex * RtvDescriptorSize;

            // レンダーターゲットを設定します。
            CommandList.SetRenderTargets(rtvDescHandle, null);

            // コマンドを積み込みます。
            CommandList.ClearRenderTargetView(rtvDescHandle, new Color4(0.0f, 0.2f, 0.4f, 1.0f), 0, null);

            CommandList.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleList;
            CommandList.SetVertexBuffer(0, VertexBufferView);
            CommandList.SetIndexBuffer(IndexBufferView);
            CommandList.DrawIndexedInstanced(36, 1, 0, 0, 0);

            CommandList.ResourceBarrierTransition(RenderTargets[FrameIndex], ResourceStates.RenderTarget, ResourceStates.Present);

            CommandList.Close();
        }

        private void WaitForPreviousFrame()
        {
            var fence = FenceValue;

            CommandQueue.Signal(Fence, fence);
            FenceValue++;

            if (Fence.CompletedValue < fence)
            {
                Fence.SetEventOnCompletion(fence, FenceEvent.SafeWaitHandle.DangerousGetHandle());
                FenceEvent.WaitOne();
            }

            FrameIndex = SwapChain.CurrentBackBufferIndex;

            FrameNumber++;
        }
    }
}