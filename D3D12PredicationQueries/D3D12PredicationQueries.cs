using System;
using System.Threading;
using SharpDX.DXGI;

namespace D3D12PredicationQueries
{
    using SharpDX;
    using SharpDX.Windows;
    using SharpDX.Direct3D12;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;

    internal class D3D12PredicationQueries : IDisposable
    {
        private struct Vertex
        {
            public Vector3 Position;
            public Vector4 Color;
        };

        [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 256)]
        private struct ConstantBufferDataStruct
        {
            public Vector4 Offset;
        }

        private const int FrameCount = 2;
        private const int CbvCountPerFrame = 2;

        private int Width;
        private int Height;

        private Device Device;
        private CommandQueue CommandQueue;
        private SwapChain3 SwapChain;
        private int FrameIndex;
        private DescriptorHeap RenderTargetViewHeap;
        private int RtvDescriptorSize;
        private Resource[] RenderTargets = new Resource[FrameCount];
        private CommandAllocator[] CommandAllocators = new CommandAllocator[FrameCount];
        private GraphicsCommandList CommandList;
        private int[] FenceValues = new int[FrameCount];
        private Fence Fence;
        private AutoResetEvent FenceEvent;
        private ViewportF Viewport;
        private Rectangle ScissorRect;
        private RootSignature RootSignature;
        private PipelineState PipelineState;
        private Resource VertexBuffer;
        private VertexBufferView VertexBufferView;
        private DescriptorHeap DepthStencilViewHeap;
        private int DsvDescriptorSize;
        private DescriptorHeap CbvSrvUavViewHeap;
        private int CbvSrvUavDescriptorSize;
        private QueryHeap QueryHeap;
        private PipelineState QueryPipelineState;
        private Resource DepthStencil;
        private Resource ConstantBuffer;
        private ConstantBufferDataStruct[] ConstantBufferData = new ConstantBufferDataStruct[CbvCountPerFrame];
        private IntPtr ConstantBufferPtr;
        private Resource QueryResult;

        public void Dispose()
        {
            WaitForGpu();

            foreach (var target in RenderTargets)
            {
                target.Dispose();
            }
            foreach(var allocator in CommandAllocators)
            {
                allocator.Dispose();
            }
            CommandQueue.Dispose();
            RootSignature.Dispose();
            RenderTargetViewHeap.Dispose();
            DepthStencilViewHeap.Dispose();
            CbvSrvUavViewHeap.Dispose();
            QueryHeap.Dispose();
            PipelineState.Dispose();
            QueryPipelineState.Dispose();
            CommandList.Dispose();
            VertexBuffer.Dispose();
            DepthStencil.Dispose();
            ConstantBuffer.Dispose();
            QueryResult.Dispose();
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
            Width = form.ClientSize.Width;
            Height = form.ClientSize.Height;

            Viewport = new ViewportF(0, 0, Width, Height, 0.0f, 1.0f);
            ScissorRect = new Rectangle(0, 0, Width, Height);

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
                    ModeDescription = new ModeDescription(Width, Height, new Rational(60, 1), Format.R8G8B8A8_UNorm),
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

            // 各種デスクリプタヒープの作成
            {
                var rtvHeapDesc = new DescriptorHeapDescription()
                {
                    DescriptorCount = FrameCount,
                    Type = DescriptorHeapType.RenderTargetView,
                    Flags = DescriptorHeapFlags.None,
                };
                RenderTargetViewHeap = Device.CreateDescriptorHeap(rtvHeapDesc);
                RtvDescriptorSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);

                // デプス・ステンシルビューヒープ
                var dsvHeapDesc = new DescriptorHeapDescription()
                {
                    DescriptorCount = 1,
                    Type = DescriptorHeapType.DepthStencilView,
                    Flags = DescriptorHeapFlags.None,
                };
                DepthStencilViewHeap = Device.CreateDescriptorHeap(dsvHeapDesc);
                DsvDescriptorSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.DepthStencilView);

                // CBV, SRV, UAV ヒープ
                var cbvSrvUavHeapDesc = new DescriptorHeapDescription()
                {
                    DescriptorCount = CbvCountPerFrame * FrameCount,
                    Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                    Flags = DescriptorHeapFlags.ShaderVisible,
                };
                CbvSrvUavViewHeap = Device.CreateDescriptorHeap(cbvSrvUavHeapDesc);
                CbvSrvUavDescriptorSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
            }

            // クエリヒープの作成
            {
                // オクルージョンクエリ用ヒープ
                var queryHeapDesc = new QueryHeapDescription()
                {
                    Count = 1,
                    Type = QueryHeapType.Occlusion,
                };
                QueryHeap = Device.CreateQueryHeap(queryHeapDesc);
            }

            // フレーム毎のリソースの作成
            {
                // フレームバッファのレンダーターゲットビューを作成
                var rtvDescHandle = RenderTargetViewHeap.CPUDescriptorHandleForHeapStart;
                for (var i = 0; i < FrameCount; i++)
                {
                    RenderTargets[i] = SwapChain.GetBackBuffer<Resource>(i);
                    Device.CreateRenderTargetView(RenderTargets[i], null, rtvDescHandle);
                    rtvDescHandle += RtvDescriptorSize;

                    CommandAllocators[i] = Device.CreateCommandAllocator(CommandListType.Direct);
                }
            }
        }

        private void LoadAssets()
        {
            // ルートシグネチャの作成
            {
                var rootSignatureDesc = new RootSignatureDescription(RootSignatureFlags.AllowInputAssemblerInputLayout,
                    new[]
                    {
                        new RootParameter(ShaderVisibility.Vertex,
                            new []
                            {
                                new DescriptorRange()
                                {
                                    RangeType = DescriptorRangeType.ConstantBufferView,
                                    BaseShaderRegister = 0,
                                    OffsetInDescriptorsFromTableStart = 0,
                                    DescriptorCount = 1,
                                },
                            }),
                    });
                RootSignature = Device.CreateRootSignature(rootSignatureDesc.Serialize());
            }

            {
#if DEBUG
                var vertexShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Shaders.hlsl", "VSMain", "vs_5_0", SharpDX.D3DCompiler.ShaderFlags.Debug));
                var pixelShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Shaders.hlsl", "PSMain", "ps_5_0", SharpDX.D3DCompiler.ShaderFlags.Debug));
#else
                var vertexShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Shaders.hlsl", "VSMain", "vs_5_0"));
                var pixelShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Shaders.hlsl", "PSMain", "ps_5_0"));
#endif

                var inputElementDescs = new[]
                {
                    new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                    new InputElement("COLOR", 0, Format.R32G32B32A32_Float, 12, 0),
                };

                var blendDesc = BlendStateDescription.Default();
                blendDesc.RenderTarget[0] = new RenderTargetBlendDescription()
                {
                    IsBlendEnabled = true,
                    LogicOpEnable = false,
                    SourceBlend = BlendOption.SourceAlpha,
                    DestinationBlend = BlendOption.InverseSourceAlpha,
                    BlendOperation = BlendOperation.Add,
                    SourceAlphaBlend = BlendOption.One,
                    DestinationAlphaBlend = BlendOption.Zero,
                    AlphaBlendOperation = BlendOperation.Add,
                    LogicOp = LogicOperation.Noop,
                    RenderTargetWriteMask = ColorWriteMaskFlags.All,
                };

                var psoDesc = new GraphicsPipelineStateDescription()
                {
                    InputLayout = new InputLayoutDescription(inputElementDescs),
                    RootSignature = RootSignature,
                    VertexShader = vertexShader,
                    PixelShader = pixelShader,
                    RasterizerState = RasterizerStateDescription.Default(),
                    BlendState = blendDesc,
                    DepthStencilFormat = Format.D32_Float,
                    DepthStencilState = DepthStencilStateDescription.Default(),
                    SampleMask = int.MaxValue,
                    PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                    RenderTargetCount = 1,
                    Flags = PipelineStateFlags.None,
                    SampleDescription = new SampleDescription(1, 0),
                    StreamOutput = new StreamOutputDescription(),
                };
                psoDesc.RenderTargetFormats[0] = Format.R8G8B8A8_UNorm;

                // 描画に利用するパイプラインステートを作成
                PipelineState = Device.CreateGraphicsPipelineState(psoDesc);

                // オクルージョンクエリ用のパイプラインステートを作成
                // レンダーターゲット、デプス・ステンシルの双方への書き込みを無効にします。
                psoDesc.BlendState.RenderTarget[0].RenderTargetWriteMask = 0;
                psoDesc.DepthStencilState.DepthWriteMask = DepthWriteMask.Zero;
                QueryPipelineState = Device.CreateGraphicsPipelineState(psoDesc);
            }

            CommandList = Device.CreateCommandList(CommandListType.Direct, CommandAllocators[FrameIndex], PipelineState);
            CommandList.Close();


            // 頂点バッファ、ビューの作成
            {
                float aspectRatio = Viewport.Width / Viewport.Height;
                var quadVertices = new[]
                {
                    // 遠方の四角形
                    new Vertex { Position = new Vector3(-0.25f, -0.25f * aspectRatio, 0.5f), Color = new Vector4(1.0f, 1.0f, 1.0f, 1.0f) },
                    new Vertex { Position = new Vector3(-0.25f, 0.25f * aspectRatio, 0.5f), Color = new Vector4(1.0f, 1.0f, 1.0f, 1.0f) },
                    new Vertex { Position = new Vector3(0.25f, -0.25f * aspectRatio, 0.5f), Color = new Vector4(1.0f, 1.0f, 1.0f, 1.0f) },
                    new Vertex { Position = new Vector3(0.25f, 0.25f * aspectRatio, 0.5f), Color = new Vector4(1.0f, 1.0f, 1.0f, 1.0f) },

                    // 近傍の四角形
                    new Vertex { Position = new Vector3(-0.5f, -0.35f * aspectRatio, 0.0f), Color = new Vector4(1.0f, 0.0f, 0.0f, 0.65f) },
                    new Vertex { Position = new Vector3(-0.5f, 0.35f * aspectRatio, 0.0f), Color = new Vector4(0.0f, 1.0f, 0.0f, 0.65f) },
                    new Vertex { Position = new Vector3(0.5f, -0.35f * aspectRatio, 0.0f), Color = new Vector4(0.0f, 0.0f, 1.0f, 0.65f) },
                    new Vertex { Position = new Vector3(0.5f, 0.35f * aspectRatio, 0.0f), Color = new Vector4(1.0f, 1.0f, 0.0f, 0.65f) },

                    // オクルージョンクエリ用。遠方の四角形と同じ位置に描画する四角形。
                    // ただし、Z ファイティングを回避するために僅かに Z 値にオフセットをとっている。
                    new Vertex { Position = new Vector3(-0.25f, -0.25f * aspectRatio, 0.4999f), Color = new Vector4(0.0f, 0.0f, 0.0f, 1.0f) },
                    new Vertex { Position = new Vector3(-0.25f, 0.25f * aspectRatio, 0.4999f), Color = new Vector4(0.0f, 0.0f, 0.0f, 1.0f) },
                    new Vertex { Position = new Vector3(0.25f, -0.25f * aspectRatio, 0.4999f), Color = new Vector4(0.0f, 0.0f, 0.0f, 1.0f) },
                    new Vertex { Position = new Vector3(0.25f, 0.25f * aspectRatio, 0.4999f), Color = new Vector4(0.0f, 0.0f, 0.0f, 1.0f) },
                };
                var vertexBufferSize = Utilities.SizeOf(quadVertices);

                VertexBuffer = Device.CreateCommittedResource(
                    new HeapProperties(HeapType.Upload),
                    HeapFlags.None,
                    ResourceDescription.Buffer(vertexBufferSize),
                    ResourceStates.GenericRead
                    );

                var pVertexDataBegin = VertexBuffer.Map(0);
                {
                    Utilities.Write(pVertexDataBegin, quadVertices, 0, quadVertices.Length);
                }
                VertexBuffer.Unmap(0);

                VertexBufferView = new VertexBufferView()
                {
                    BufferLocation = VertexBuffer.GPUVirtualAddress,
                    StrideInBytes = Utilities.SizeOf<Vertex>(),
                    SizeInBytes = vertexBufferSize,
                };
            }

            // デプス・ステンシルビューの作成
            {
                var depthStencilDesc = new DepthStencilViewDescription()
                {
                    Format = Format.D32_Float,
                    Dimension = DepthStencilViewDimension.Texture2D,
                    Flags = DepthStencilViewFlags.None,
                };

                var depthOptimizedClearValue = new ClearValue()
                {
                    Format = Format.D32_Float,
                    DepthStencil = new DepthStencilValue()
                    {
                        Depth = 1.0f,
                        Stencil = 0,
                    }
                };

                DepthStencil = Device.CreateCommittedResource(
                    new HeapProperties(HeapType.Default),
                    HeapFlags.None,
                    ResourceDescription.Texture2D(Format.D32_Float, Width, Height, 1, 0, 1, 0, ResourceFlags.AllowDepthStencil),
                    ResourceStates.DepthWrite,
                    depthOptimizedClearValue
                    );

                Device.CreateDepthStencilView(DepthStencil, depthStencilDesc, DepthStencilViewHeap.CPUDescriptorHandleForHeapStart);
            }

            // 定数バッファの作成
            {
                // コンスタントバッファを作成します。
                ConstantBuffer = Device.CreateCommittedResource(
                    new HeapProperties(HeapType.Upload),
                    HeapFlags.None,
                    ResourceDescription.Buffer(FrameCount * CbvCountPerFrame * Utilities.SizeOf<ConstantBufferDataStruct>()),
                    ResourceStates.GenericRead
                    );

                // コンスタントバッファを初期化します。
                // コンスタントバッファは、アプリ終了までマップしたままにします。
                ConstantBufferPtr = ConstantBuffer.Map(0);
                var currentPtr = ConstantBufferPtr;
                for(var i = 0; i < FrameCount; i++)
                {
                    Utilities.Write(currentPtr, ConstantBufferData, 0, CbvCountPerFrame);
                    currentPtr = IntPtr.Add(ConstantBufferPtr, CbvCountPerFrame * Utilities.SizeOf<ConstantBufferDataStruct>());
                }

                // コンスタントバッファビューを作成します。
                var cbvDesc = new ConstantBufferViewDescription()
                {
                    BufferLocation = ConstantBuffer.GPUVirtualAddress,
                    SizeInBytes = Utilities.SizeOf<ConstantBufferDataStruct>(),
                };

                var cbvHandle = CbvSrvUavViewHeap.CPUDescriptorHandleForHeapStart;

                for (var i = 0; i < FrameCount * CbvCountPerFrame; i++)
                {
                    Device.CreateConstantBufferView(cbvDesc, cbvHandle);
                    cbvDesc.BufferLocation += cbvDesc.SizeInBytes;
                    cbvHandle += CbvSrvUavDescriptorSize;
                }
            }

            // クエリ結果を格納するためのバッファの作成
            {
                QueryResult = Device.CreateCommittedResource(
                    new HeapProperties(HeapType.Default),
                    HeapFlags.None,
                    ResourceDescription.Buffer(8),
                    ResourceStates.Predication
                    );
            }

            // 同期オブジェクトの初期化
            {
                Fence = Device.CreateFence(FenceValues[FrameIndex], FenceFlags.None);
                FenceValues[FrameIndex]++;

                FenceEvent = new AutoResetEvent(false);
            }
        }

        internal void Update()
        {
            var translationSpeed = 0.01f;
            var offsetBounds = 1.5f;

            // 近傍の四角形を水平移動させます。
            ConstantBufferData[1].Offset.X += translationSpeed;
            if(ConstantBufferData[1].Offset.X > offsetBounds)
            {
                ConstantBufferData[1].Offset.X = -offsetBounds;
            }

            var ptr = IntPtr.Add(ConstantBufferPtr, (FrameIndex * CbvCountPerFrame + 1) * Utilities.SizeOf<ConstantBufferDataStruct>());
            Utilities.Write(ptr, ref ConstantBufferData[1]);
        }

        internal void Render()
        {
            PopulateCommandList();

            CommandQueue.ExecuteCommandList(CommandList);

            SwapChain.Present(1, PresentFlags.None);

            MoveToNextFrame();
        }

        private void PopulateCommandList()
        {
            CommandAllocators[FrameIndex].Reset();
            CommandList.Reset(CommandAllocators[FrameIndex], PipelineState);

            CommandList.SetGraphicsRootSignature(RootSignature);

            CommandList.SetDescriptorHeaps(1, new[] { CbvSrvUavViewHeap });

            CommandList.SetViewport(Viewport);
            CommandList.SetScissorRectangles(ScissorRect);

            CommandList.ResourceBarrierTransition(RenderTargets[FrameIndex], ResourceStates.Present, ResourceStates.RenderTarget);

            var rtvHandle = RenderTargetViewHeap.CPUDescriptorHandleForHeapStart;
            rtvHandle += FrameIndex * RtvDescriptorSize;
            var dsvHandle = DepthStencilViewHeap.CPUDescriptorHandleForHeapStart;
            CommandList.SetRenderTargets(1, rtvHandle, dsvHandle);

            CommandList.ClearRenderTargetView(rtvHandle, new Color4(0.0f, 0.2f, 0.4f, 1.0f), 0, null);
            CommandList.ClearDepthStencilView(dsvHandle, ClearFlags.FlagsDepth, 1.0f, 0);

            // 描画を行います。
            {
                CommandList.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleStrip;
                CommandList.SetVertexBuffer(0, VertexBufferView);

                var cbvHandle = CbvSrvUavViewHeap.GPUDescriptorHandleForHeapStart;
                cbvHandle += FrameIndex * CbvCountPerFrame * CbvSrvUavDescriptorSize;

                var cbvFarQuad = cbvHandle;
                var cbvNearQuad = cbvHandle + CbvSrvUavDescriptorSize;

                // 遠方の四角形の描画
                CommandList.SetGraphicsRootDescriptorTable(0, cbvFarQuad);
                // Predication を仕込みます。
                //
                // 条件式が成立する（QueryResult が EqualZero である）とき、後続のコマンドは実行されません。
                //
                // QueryResult には、前フレームにおけるオクルージョンクエリの結果が収まっています。
                // つまり、オクルージョンクエリ用四角形が近傍四角形によって完全に覆い隠されている（QueryResult が 0 である）場合には、
                // 遠方の四角形の描画は実行されません。
                CommandList.SetPredication(QueryResult, 0, PredicationOperation.EqualZero);
                CommandList.DrawInstanced(4, 1, 0, 0);

                // 近傍の四角形の描画
                CommandList.SetGraphicsRootDescriptorTable(0, cbvNearQuad);
                // Predication を無効化します。
                CommandList.SetPredication(null, 0, PredicationOperation.EqualZero);
                CommandList.DrawInstanced(4, 1, 4, 0);

                // オクルージョンクエリ用四角形の描画
                // クエリ用パイプラインステート（レンダーターゲット、デプス・ステンシルターゲットへの書き込みを行わない）に変更します。
                CommandList.PipelineState = QueryPipelineState;
                CommandList.SetGraphicsRootDescriptorTable(0, cbvFarQuad);
                // クエリを開始します。
                //
                // BinaryOcclusion は、クエリ区間内に実行された描画において、デプス・ステンシルテストを
                // パスするピクセルが 1 つも存在しない場合には 0 を、そうでない場合には 1 を返します。
                //
                // 今回は、オクルージョンクエリ用四角形が近傍四角形によって完全に覆い隠されている場合には 0 を、
                // そうでない場合には 1 を返します。
                CommandList.BeginQuery(QueryHeap, QueryType.BinaryOcclusion, 0);
                CommandList.DrawInstanced(4, 1, 8, 0);
                // クエリを終了します。
                CommandList.EndQuery(QueryHeap, QueryType.BinaryOcclusion, 0);

                // オクルージョンクエリの結果を得ます。
                // QueryHeap に収まっている結果を QueryResult へコピーします。
                // コピーしたあとは、QueryResult の状態を Predication 用に戻します。
                CommandList.ResourceBarrierTransition(QueryResult, ResourceStates.Predication, ResourceStates.CopyDestination);
                CommandList.ResolveQueryData(QueryHeap, QueryType.BinaryOcclusion, 0, 1, QueryResult, 0);
                CommandList.ResourceBarrierTransition(QueryResult, ResourceStates.CopyDestination, ResourceStates.Predication);
            }

            CommandList.ResourceBarrierTransition(RenderTargets[FrameIndex], ResourceStates.RenderTarget, ResourceStates.Present);

            CommandList.Close();
        }

        private void WaitForGpu()
        {
            CommandQueue.Signal(Fence, FenceValues[FrameIndex]);

            Fence.SetEventOnCompletion(FenceValues[FrameIndex], FenceEvent.SafeWaitHandle.DangerousGetHandle());
            FenceEvent.WaitOne();

            FenceValues[FrameIndex]++;
        }

        private void MoveToNextFrame()
        {
            var currentFenceValue = FenceValues[FrameIndex];
            CommandQueue.Signal(Fence, currentFenceValue);

            FrameIndex = SwapChain.CurrentBackBufferIndex;

            if (Fence.CompletedValue < FenceValues[FrameIndex])
            {
                Fence.SetEventOnCompletion(FenceValues[FrameIndex], FenceEvent.SafeWaitHandle.DangerousGetHandle());
                FenceEvent.WaitOne();
            }

            FenceValues[FrameIndex] = currentFenceValue + 1;
        }
    }
}