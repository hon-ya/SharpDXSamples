using System;
using System.Threading;
using SharpDX.DXGI;

namespace D3D12ExecuteIndirect
{
    using SharpDX;
    using SharpDX.Windows;
    using SharpDX.Direct3D12;
    using System.Runtime.InteropServices;

    internal static class EnumUtilities
    {
        static public int GetCount<T>() where T : struct
        {
            return Enum.GetNames(typeof(T)).Length;
        }
    }

    internal class D3D12ExecuteIndirect : IDisposable
    {
        // 頂点データ
        private struct Vertex
        {
            public Vector3 Position;
        }

        // 定数バッファ。配列で扱うために、アライメントである 256 byte にサイズを固定しています。
        [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 256)]
        private struct ConstantBufferDataStruct
        {
            public Vector4 Velocity;
            public Vector4 Offset;
            public Vector4 Color;
            public Matrix Projection;
        }

        // コンピュートシェーダ用のルート定数
        struct CSRootConstantsDataStruct
        {
            public float XOffset;
            public float ZOffset;
            public float CullOffset;
            public float CommandCount;
        }

        struct IndirectCommand
        {
            public long Cbv;
            public DrawArgumentS DrawArguments;
        }

        // グラフィックスルートシグネチャの各ルートパラメタの種類
        enum GraphcisRootParameters
        {
            Cbv,
        }

        // コンピュートルートシグネチャの各ルートパラメタの種類
        enum ComputeRootParameters
        {
            SrvUavTable,
            RootConstants,
        }

        // CBV/SRV/UAV デスクリプタヒープ内における各デスクリプタのオフセット
        enum CbvSrvUavHeapOffsets
        {
            CbvSrvOffset,
            CommandsOffset,
            ProcessedCommandsOffset,
        }

        private const int FrameCount = 2;
        private const int TriangleCount = 1024;
        private const int TriangleResourceCount = TriangleCount * FrameCount;
        private const float TriangleHalfWidth = 0.05f;
        private const float TriangleDepth = 1.0f;
        private const float CullingCutoff = 0.5f;
        private const int ComputeThreadBlockSize = 128;

        private readonly int CommandSizePerFrame = TriangleCount * Utilities.SizeOf<IndirectCommand>();
        private readonly int CommandBufferCounterOffset = (TriangleCount * Utilities.SizeOf<IndirectCommand>() + 4095) & ~4095;

        private int Width;
        private int Height;
        private int FrameNumber = 0;

        private Device Device;
        private CommandQueue CommandQueue;
        private CommandQueue ComputeCommandQueue;
        private SwapChain3 SwapChain;
        private int FrameIndex;
        private DescriptorHeap RenderTargetViewHeap;
        private int RtvDescriptorSize;
        private Resource[] RenderTargets = new Resource[FrameCount];
        private CommandAllocator[] CommandAllocators = new CommandAllocator[FrameCount];
        private CommandAllocator[] ComputeCommandAllocators = new CommandAllocator[FrameCount];
        private GraphicsCommandList CommandList;
        private int[] FenceValues = new int[FrameCount];
        private Fence Fence;
        private AutoResetEvent FenceEvent;
        private ViewportF Viewport;
        private Rectangle ScissorRect;
        private RootSignature RootSignature;
        private RootSignature ComputeRootSignature;
        private PipelineState PipelineState;
        private Resource VertexBuffer;
        private VertexBufferView VertexBufferView;
        private Rectangle CullingScissorRect;
        private DescriptorHeap DepthStencilViewHeap;
        private int DsvDescriptorSize;
        private DescriptorHeap CbvSrvUavHeap;
        private int CbvSrvUavDescriptorSize;
        private PipelineState ComputePipelineState;
        private GraphicsCommandList ComputeCommandList;
        private Resource DepthStencil;
        private Resource ConstantBuffer;
        private ConstantBufferDataStruct[] ConstantBufferData = new ConstantBufferDataStruct[TriangleCount];
        private Random Random = new Random();
        private IntPtr ConstantBufferPtr;
        private CommandSignature CommandSignature;
        private Resource CommandBuffer;
        private Resource CommandBufferUpload;
        private Resource[] ProcessedCommandBuffers = new Resource[FrameCount];
        private Resource ProcessedCommandBufferCounterReset;
        private bool EnableCulling = true;
        private CSRootConstantsDataStruct CSRootConstantsData;
        private IntPtr CSRootConstantsDataPtr;
        private Fence ComputeFence;

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
            foreach (var allocator in ComputeCommandAllocators)
            {
                allocator.Dispose();
            }
            foreach (var buffer in ProcessedCommandBuffers)
            {
                buffer.Dispose();
            }
            CommandQueue.Dispose();
            ComputeCommandQueue.Dispose();
            RootSignature.Dispose();
            ComputeRootSignature.Dispose();
            CommandSignature.Dispose();
            RenderTargetViewHeap.Dispose();
            DepthStencilViewHeap.Dispose();
            CbvSrvUavHeap.Dispose();
            PipelineState.Dispose();
            ComputePipelineState.Dispose();
            CommandList.Dispose();
            ComputeCommandList.Dispose();
            VertexBuffer.Dispose();
            DepthStencil.Dispose();
            ConstantBuffer.Dispose();
            CommandBuffer.Dispose();
            CommandBufferUpload.Dispose();
            ProcessedCommandBufferCounterReset.Dispose();
            Fence.Dispose();
            ComputeFence.Dispose();
            SwapChain.Dispose();
            Device.Dispose();
        }

        internal void Initialize(RenderForm form)
        {
            SetupKeyHandler(form);
            LoadPipeline(form);
            LoadAssets();
        }

        private void SetupKeyHandler(RenderForm form)
        {
            form.KeyDown += OnKeyDown;
        }

        private void OnKeyDown(object sender, System.Windows.Forms.KeyEventArgs e)
        {
            if(e.KeyCode == System.Windows.Forms.Keys.Space)
            {
                EnableCulling = !EnableCulling;
            }
        }

        private void LoadPipeline(RenderForm form)
        {
            CSRootConstantsData = new CSRootConstantsDataStruct
            {
                XOffset = TriangleHalfWidth,
                ZOffset = TriangleDepth,
                CullOffset = CullingCutoff,
                CommandCount = TriangleCount,
            };

            CSRootConstantsDataPtr = Marshal.AllocHGlobal(Marshal.SizeOf(CSRootConstantsData));
            Marshal.StructureToPtr(CSRootConstantsData, CSRootConstantsDataPtr, false);

            Width = form.ClientSize.Width;
            Height = form.ClientSize.Height;

            Viewport = new ViewportF(0, 0, Width, Height, 0.0f, 1.0f);
            ScissorRect = new Rectangle(0, 0, Width, Height);

            float center = Width / 2.0f;
            CullingScissorRect = new Rectangle()
            {
                Top = 0,
                Left = (int)(center - (center * CullingCutoff)),
                Right = (int)(center + (center * CullingCutoff)),
                Bottom = Height,
            };

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

                // コンピュート用のキューを作成
                var computeQueueDesc = new CommandQueueDescription(CommandListType.Compute);
                ComputeCommandQueue = Device.CreateCommandQueue(computeQueueDesc);

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

            var rtvHeapDesc = new DescriptorHeapDescription()
            {
                DescriptorCount = FrameCount,
                Type = DescriptorHeapType.RenderTargetView,
                Flags = DescriptorHeapFlags.None,
            };
            RenderTargetViewHeap = Device.CreateDescriptorHeap(rtvHeapDesc);
            RtvDescriptorSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);

            // デプス・ステンシルビュー用のデスクリプタヒープを作成
            var dsvHeapDesc = new DescriptorHeapDescription()
            {
                DescriptorCount = 1,
                Type = DescriptorHeapType.DepthStencilView,
                Flags = DescriptorHeapFlags.None,
            };
            DepthStencilViewHeap = Device.CreateDescriptorHeap(dsvHeapDesc);
            DsvDescriptorSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.DepthStencilView);

            // CBV/SRV/UAV 用のデスクリプタヒープを作成
            var cbvSrvUavHeapDesc = new DescriptorHeapDescription()
            {
                DescriptorCount = EnumUtilities.GetCount<CbvSrvUavHeapOffsets>() * FrameCount,
                Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                Flags = DescriptorHeapFlags.ShaderVisible,
            };
            CbvSrvUavHeap = Device.CreateDescriptorHeap(cbvSrvUavHeapDesc);
            CbvSrvUavDescriptorSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

            var rtvDescHandle = RenderTargetViewHeap.CPUDescriptorHandleForHeapStart;
            for (var i = 0; i < FrameCount; i++)
            {
                RenderTargets[i] = SwapChain.GetBackBuffer<Resource>(i);
                Device.CreateRenderTargetView(RenderTargets[i], null, rtvDescHandle);
                rtvDescHandle += RtvDescriptorSize;

                CommandAllocators[i] = Device.CreateCommandAllocator(CommandListType.Direct);
                ComputeCommandAllocators[i] = Device.CreateCommandAllocator(CommandListType.Compute);
            }
        }

        private void LoadAssets()
        {
            // ルートシグネチャの作成
            {
                // グラフィックス用のルートシグネチャを作成
                var rootSignatureDesc = new RootSignatureDescription(RootSignatureFlags.AllowInputAssemblerInputLayout,
                    new[]
                    {
                        // ルートパラメータ 0:
                        // コンスタントバッファレジスタ 0 のコンスタントバッファ（頂点シェーダからのみ参照）
                        new RootParameter(ShaderVisibility.Vertex,
                            new RootDescriptor()
                            {
                                RegisterSpace = 0,
                                ShaderRegister = 0,
                            },
                            RootParameterType.ConstantBufferView
                            )
                    });
                RootSignature = Device.CreateRootSignature(rootSignatureDesc.Serialize());

                // コンピュート用のルートシグネチャを作成
                var computeRootSignatureDesc = new RootSignatureDescription(RootSignatureFlags.None,
                    new[]
                    {
                        new RootParameter(ShaderVisibility.All,
                            new DescriptorRange()
                            {
                                RangeType = DescriptorRangeType.ShaderResourceView,
                                BaseShaderRegister = 0,
                                OffsetInDescriptorsFromTableStart = 0,
                                DescriptorCount = 2,
                            },
                            new DescriptorRange()
                            {
                                RangeType = DescriptorRangeType.UnorderedAccessView,
                                BaseShaderRegister = 0,
                                OffsetInDescriptorsFromTableStart = 2,
                                DescriptorCount = 1,
                            }),
                        new RootParameter(ShaderVisibility.All,
                            new RootConstants()
                            {
                                Value32BitCount = 4,
                                ShaderRegister = 0,
                            }),
                    });
                ComputeRootSignature = Device.CreateRootSignature(computeRootSignatureDesc.Serialize());
            }

            // パイプラインステートオブジェクトの作成
            {
#if DEBUG
                var vertexShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Shaders.hlsl", "VSMain", "vs_5_0", SharpDX.D3DCompiler.ShaderFlags.Debug));
                var pixelShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Shaders.hlsl", "PSMain", "ps_5_0", SharpDX.D3DCompiler.ShaderFlags.Debug));
                var computeShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Compute.hlsl", "CSMain", "cs_5_0", SharpDX.D3DCompiler.ShaderFlags.Debug));
#else
                var vertexShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Shaders.hlsl", "VSMain", "vs_5_0"));
                var pixelShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Shaders.hlsl", "PSMain", "ps_5_0"));
                var computeShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Compute.hlsl", "CSMain", "cs_5_0"));
#endif

                var inputElementDescs = new[]
                {
                    new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                };

                // グラフィックス用のパイプラインステートオブジェクトの作成
                var psoDesc = new GraphicsPipelineStateDescription()
                {
                    InputLayout = new InputLayoutDescription(inputElementDescs),
                    RootSignature = RootSignature,
                    VertexShader = vertexShader,
                    PixelShader = pixelShader,
                    RasterizerState = RasterizerStateDescription.Default(),
                    BlendState = BlendStateDescription.Default(),
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
                PipelineState = Device.CreateGraphicsPipelineState(psoDesc);

                // コンピュート用のパイプラインステートオブジェクトの作成
                var computePsoDesc = new ComputePipelineStateDescription()
                {
                    RootSignature = ComputeRootSignature,
                    ComputeShader = computeShader,
                };
                ComputePipelineState = Device.CreateComputePipelineState(computePsoDesc);
            }

            // グラフィックス、コンピュートそれぞれのコマンドリストを作成
            CommandList = Device.CreateCommandList(CommandListType.Direct, CommandAllocators[FrameIndex], PipelineState);
            ComputeCommandList = Device.CreateCommandList(CommandListType.Compute, ComputeCommandAllocators[FrameIndex], ComputePipelineState);

            // コンピュートコマンドリストは、すぐに閉じます。
            ComputeCommandList.Close();

            // 頂点バッファ、ビューの作成
            {
                var triangleVertices = new[]
                {
                    new Vertex { Position = new Vector3(0.0f, TriangleHalfWidth, TriangleDepth) },
                    new Vertex { Position = new Vector3(TriangleHalfWidth, -TriangleHalfWidth, TriangleDepth) },
                    new Vertex { Position = new Vector3(-TriangleHalfWidth, -TriangleHalfWidth, TriangleDepth) }
                };
                var vertexBufferSize = Utilities.SizeOf(triangleVertices);

                VertexBuffer = Device.CreateCommittedResource(
                    new HeapProperties(HeapType.Upload),
                    HeapFlags.None,
                    ResourceDescription.Buffer(vertexBufferSize),
                    ResourceStates.GenericRead
                    );

                var pVertexDataBegin = VertexBuffer.Map(0);
                {
                    Utilities.Write(pVertexDataBegin, triangleVertices, 0, triangleVertices.Length);
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
                var constantBufferDataSize = TriangleResourceCount * Utilities.SizeOf<ConstantBufferDataStruct>();

                // コンスタントバッファを作成します。
                ConstantBuffer = Device.CreateCommittedResource(
                    new HeapProperties(HeapType.Upload),
                    HeapFlags.None,
                    ResourceDescription.Buffer(constantBufferDataSize),
                    ResourceStates.GenericRead
                    );

                // コンスタントバッファビューを作成します。
                var cbvDesc = new ConstantBufferViewDescription()
                {
                    SizeInBytes = Utilities.SizeOf<ConstantBufferDataStruct>(),
                };

                // コンスタントバッファのデータを初期化します。
                float aspectRatio = Viewport.Width / Viewport.Height;
                for (var i = 0; i < TriangleCount; i++)
                {
                    ConstantBufferData[i] = new ConstantBufferDataStruct
                    {
                        Velocity = new Vector4(Random.NextFloat(0.01f, 0.02f), 0.0f, 0.0f, 0.0f),
                        Offset = new Vector4(Random.NextFloat(-5.0f, -1.5f), Random.NextFloat(-1.0f, 1.0f), Random.NextFloat(0.0f, 2.0f), 0.0f),
                        Color = new Vector4(Random.NextFloat(0.5f, 1.0f), Random.NextFloat(0.5f, 1.0f), Random.NextFloat(0.5f, 1.0f), 1.0f),
                        Projection = Matrix.Transpose(Matrix.PerspectiveFovLH(MathUtil.PiOverFour, aspectRatio, 0.01f, 20.0f)),
                    };
                }

                // コンスタントバッファを初期化します。
                // コンスタントバッファは、アプリ終了までマップしたままにします。
                ConstantBufferPtr = ConstantBuffer.Map(0);
                var currentConstantBufferPtr = ConstantBufferPtr;
                for (var i = 0; i < TriangleCount; i++)
                {
                    currentConstantBufferPtr = Utilities.WriteAndPosition(currentConstantBufferPtr, ref ConstantBufferData[i]);
                }

                // シェーダリソースビューを作成します。
                var srvDesc = new ShaderResourceViewDescription()
                {
                    Format = Format.Unknown,
                    Dimension = ShaderResourceViewDimension.Buffer,
                    Shader4ComponentMapping = D3DXUtilities.DefaultComponentMapping(),
                    Buffer =
                    {
                        ElementCount = TriangleCount,
                        StructureByteStride = Utilities.SizeOf<ConstantBufferDataStruct>(),
                        Flags = BufferShaderResourceViewFlags.None,
                    },
                };

                var srvHandle = CbvSrvUavHeap.CPUDescriptorHandleForHeapStart;
                srvHandle += (int)CbvSrvUavHeapOffsets.CbvSrvOffset * CbvSrvUavDescriptorSize;

                for(var i = 0; i < FrameCount; i++)
                {
                    srvDesc.Buffer.FirstElement = i * TriangleCount;
                    Device.CreateShaderResourceView(ConstantBuffer, srvDesc, srvHandle);
                    srvHandle += EnumUtilities.GetCount<CbvSrvUavHeapOffsets>() * CbvSrvUavDescriptorSize;
                }
            }

            // インダイレクトドローに使用するコマンドシグネチャの作成
            {
                var argumentDescs = new[]
                {
                    new IndirectArgumentDescription()
                    {
                        Type = IndirectArgumentType.ConstantBufferView,
                        ConstantBufferView =
                        {
                            RootParameterIndex = (int)GraphcisRootParameters.Cbv,
                        }
                    },
                    new IndirectArgumentDescription()
                    {
                        Type = IndirectArgumentType.Draw,
                    },
                };

                var commandSignatureDesc = new CommandSignatureDescription()
                {
                    IndirectArguments = argumentDescs,
                    ByteStride = Utilities.SizeOf<IndirectCommand>(),
                };

                CommandSignature = Device.CreateCommandSignature(commandSignatureDesc, RootSignature);
            }

            // コンピュートシェーダで利用するコマンドバッファと UAV を作成します。
            {
                var commandBufferSize = CommandSizePerFrame * FrameCount;

                // コンピュートシェーダで処理するバッファを作成します。
                CommandBuffer = Device.CreateCommittedResource(
                    new HeapProperties(HeapType.Default),
                    HeapFlags.None,
                    ResourceDescription.Buffer(commandBufferSize),
                    ResourceStates.CopyDestination
                    );

                // バッファの内容を更新するためのアップロードバッファを作成します。
                CommandBufferUpload = Device.CreateCommittedResource(
                    new HeapProperties(HeapType.Upload),
                    HeapFlags.None,
                    ResourceDescription.Buffer(commandBufferSize),
                    ResourceStates.GenericRead
                    );

                // バッファに収めるデータを用意します。
                // 内容は、三角形を生成するインダイレクトコマンドです。
                var commands = new IndirectCommand[TriangleResourceCount];
                var gpuAddress = ConstantBuffer.GPUVirtualAddress;
                var commandIndex = 0;
                for(var frame = 0; frame < FrameCount; frame++)
                {
                    for(var i = 0; i < TriangleCount; i++)
                    {
                        commands[commandIndex] = new IndirectCommand
                        {
                            Cbv = gpuAddress,
                            DrawArguments =
                            {
                                VertexCountPerInstance = 3,
                                InstanceCount = 1,
                                StartVertexLocation = 0,
                                StartInstanceLocation = 0,
                            },
                        };

                        commandIndex++;
                        gpuAddress += Utilities.SizeOf<ConstantBufferDataStruct>();
                    }
                }

                // アップロード用のバッファを初期化します。
                var currentCommandBufferUploadPtr = CommandBufferUpload.Map(0);
                for (var i = 0; i < TriangleResourceCount; i++)
                {
                    currentCommandBufferUploadPtr = Utilities.WriteAndPosition(currentCommandBufferUploadPtr, ref commands[i]);
                }
                CommandBufferUpload.Unmap(0);

                // アップロードバッファの内容をバッファにコピーします。
                CommandList.CopyBufferRegion(CommandBuffer, 0, CommandBufferUpload, 0, commandBufferSize);

                // バッファの状態を遷移させます。
                CommandList.ResourceBarrier(new ResourceTransitionBarrier(CommandBuffer, ResourceStates.GenericRead, ResourceStates.NonPixelShaderResource));

                // コマンドを収めたバッファを参照するシェーダリソースビューを作成します。
                var srvDesc = new ShaderResourceViewDescription()
                {
                    Format = Format.Unknown,
                    Dimension = ShaderResourceViewDimension.Buffer,
                    Shader4ComponentMapping = D3DXUtilities.DefaultComponentMapping(),
                    Buffer =
                    {
                        ElementCount = TriangleCount,
                        StructureByteStride = Utilities.SizeOf<IndirectCommand>(),
                        Flags = BufferShaderResourceViewFlags.None,
                    },
                };

                var commandsHandle = CbvSrvUavHeap.CPUDescriptorHandleForHeapStart;
                commandsHandle += (int)CbvSrvUavHeapOffsets.CommandsOffset * CbvSrvUavDescriptorSize;
                for(var i = 0; i < FrameCount; i++)
                {
                    srvDesc.Buffer.FirstElement = i * TriangleCount;
                    Device.CreateShaderResourceView(CommandBuffer, srvDesc, commandsHandle);
                    commandsHandle += EnumUtilities.GetCount<CbvSrvUavHeapOffsets>() * CbvSrvUavDescriptorSize;
                }

                // コンピュートシェーダは、バッファに収めたコマンドを処理し、その結果を UAV （が示すバッファ）に収めます。
                // このための UAV （とバッファ）を作成します。
                var processedCommandsHandle = CbvSrvUavHeap.CPUDescriptorHandleForHeapStart;
                processedCommandsHandle += (int)CbvSrvUavHeapOffsets.ProcessedCommandsOffset * CbvSrvUavDescriptorSize;
                for(var frame = 0; frame < FrameCount; frame++)
                {
                    // 結果を収めるバッファを作成します。
                    ProcessedCommandBuffers[frame] = Device.CreateCommittedResource(
                        new HeapProperties(HeapType.Default),
                        HeapFlags.None,
                        ResourceDescription.Buffer(CommandBufferCounterOffset + Utilities.SizeOf<UInt32>(), ResourceFlags.AllowUnorderedAccess),
                        ResourceStates.CopyDestination
                        );

                    // 結果を収めるバッファを参照する UAV を作成します。
                    var uavDesc = new UnorderedAccessViewDescription()
                    {
                        Format = Format.Unknown,
                        Dimension = UnorderedAccessViewDimension.Buffer,
                        Buffer =
                        {
                            FirstElement = 0,
                            ElementCount = TriangleCount,
                            StructureByteStride = Utilities.SizeOf<IndirectCommand>(),
                            CounterOffsetInBytes = CommandBufferCounterOffset,
                            Flags = BufferUnorderedAccessViewFlags.None,
                        },
                    };

                    Device.CreateUnorderedAccessView(
                        ProcessedCommandBuffers[frame], 
                        ProcessedCommandBuffers[frame],
                        uavDesc,
                        processedCommandsHandle
                        );

                    processedCommandsHandle += EnumUtilities.GetCount<CbvSrvUavHeapOffsets>() * CbvSrvUavDescriptorSize;
                }

                // カウンタを 0 クリアするための 0 バッファを作成します。
                ProcessedCommandBufferCounterReset = Device.CreateCommittedResource(
                    new HeapProperties(HeapType.Upload),
                    HeapFlags.None,
                    ResourceDescription.Buffer(Utilities.SizeOf<UInt32>()),
                    ResourceStates.GenericRead
                    );

                var processedCommandBufferCounterResetPtr = ProcessedCommandBufferCounterReset.Map(0);
                {
                    UInt32 zero = 0; 
                    Utilities.Write(processedCommandBufferCounterResetPtr, ref zero);
                }
                ProcessedCommandBufferCounterReset.Unmap(0);
            }

            // コマンドを実行します。
            CommandList.Close();
            CommandQueue.ExecuteCommandList(CommandList);

            // 同期オブジェクトの初期化とコマンドの実行完了を待ちます。
            {
                Fence = Device.CreateFence(FenceValues[FrameIndex], FenceFlags.None);
                ComputeFence = Device.CreateFence(FenceValues[FrameIndex], FenceFlags.None);
                FenceValues[FrameIndex]++;

                FenceEvent = new AutoResetEvent(false);

                // コマンドの実行完了を待ちます。
                WaitForGpu();
            }
        }

        internal void Update()
        {
            // 三角形の位置情報を更新します。
            const float offsetBounds = 2.5f;

            for (var i = 0; i < TriangleCount; i++)
            {
                ConstantBufferData[i].Offset.X += ConstantBufferData[i].Velocity.X;
                if(ConstantBufferData[i].Offset.X > offsetBounds)
                {
                    ConstantBufferData[i].Velocity.X = Random.NextFloat(0.01f, 0.02f);
                    ConstantBufferData[i].Offset.X = -offsetBounds;
                }
            }

            var currentPtr = Utilities.IntPtrAdd(ConstantBufferPtr, TriangleCount * FrameIndex * Utilities.SizeOf<ConstantBufferDataStruct>());
            for (var i = 0; i < TriangleCount; i++)
            {
                currentPtr = Utilities.WriteAndPosition(currentPtr, ref ConstantBufferData[i]);
            }
        }

        internal void Render()
        {
            PopulateCommandList();

            if(EnableCulling)
            {
                ComputeCommandQueue.ExecuteCommandList(ComputeCommandList);

                ComputeCommandQueue.Signal(ComputeFence, FenceValues[FrameIndex]);

                CommandQueue.Wait(ComputeFence, FenceValues[FrameIndex]);
            }

            CommandQueue.ExecuteCommandList(CommandList);

            SwapChain.Present(1, PresentFlags.None);

            MoveToNextFrame();
        }

        private void PopulateCommandList()
        {
            // コンピュート、グラフィックス用のコマンドアロケータをリセット
            ComputeCommandAllocators[FrameIndex].Reset();
            CommandAllocators[FrameIndex].Reset();

            // コマンドリストをリセット
            ComputeCommandList.Reset(ComputeCommandAllocators[FrameIndex], ComputePipelineState);
            CommandList.Reset(CommandAllocators[FrameIndex], PipelineState);

            if(EnableCulling)
            {
                ComputeCommandList.SetComputeRootSignature(ComputeRootSignature);

                ComputeCommandList.SetDescriptorHeaps(1, new[] { CbvSrvUavHeap });

                var CbvSrvUavHandle = CbvSrvUavHeap.GPUDescriptorHandleForHeapStart;
                CbvSrvUavHandle += ((int)CbvSrvUavHeapOffsets.CbvSrvOffset + FrameIndex * EnumUtilities.GetCount<CbvSrvUavHeapOffsets>()) * CbvSrvUavDescriptorSize;

                ComputeCommandList.SetComputeRootDescriptorTable((int)ComputeRootParameters.SrvUavTable, CbvSrvUavHandle);

                ComputeCommandList.SetComputeRoot32BitConstants((int)ComputeRootParameters.RootConstants, 4, CSRootConstantsDataPtr, 0);

                ComputeCommandList.CopyBufferRegion(ProcessedCommandBuffers[FrameIndex], CommandBufferCounterOffset, ProcessedCommandBufferCounterReset, 0, Utilities.SizeOf<UInt32>());

                ComputeCommandList.ResourceBarrier(new ResourceTransitionBarrier(ProcessedCommandBuffers[FrameIndex], ResourceStates.CopyDestination, ResourceStates.UnorderedAccess));

                ComputeCommandList.Dispatch((int)Math.Ceiling(TriangleCount / (float)ComputeThreadBlockSize), 1, 1);
            }

            ComputeCommandList.Close();

            {
                CommandList.SetGraphicsRootSignature(RootSignature);

                CommandList.SetDescriptorHeaps(1, new[] { CbvSrvUavHeap });

                CommandList.SetViewport(Viewport);
                CommandList.SetScissorRectangles(EnableCulling ? CullingScissorRect : ScissorRect);

                CommandList.ResourceBarrier(
                    new ResourceTransitionBarrier(
                        EnableCulling ? ProcessedCommandBuffers[FrameIndex] : CommandBuffer,
                        EnableCulling ? ResourceStates.UnorderedAccess : ResourceStates.NonPixelShaderResource,
                        ResourceStates.IndirectArgument
                        ),
                    new ResourceTransitionBarrier(
                        RenderTargets[FrameIndex],
                        ResourceStates.Present,
                        ResourceStates.RenderTarget
                        )
                    );

                var rtvHandle = RenderTargetViewHeap.CPUDescriptorHandleForHeapStart;
                rtvHandle += FrameIndex * RtvDescriptorSize;
                var dsvHandle = DepthStencilViewHeap.CPUDescriptorHandleForHeapStart;
                CommandList.SetRenderTargets(1, rtvHandle, dsvHandle);

                CommandList.ClearRenderTargetView(rtvHandle, new Color4(0.0f, 0.2f, 0.4f, 1.0f), 0, null);
                CommandList.ClearDepthStencilView(dsvHandle, ClearFlags.FlagsDepth, 1.0f, 0);

                CommandList.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleStrip;
                CommandList.SetVertexBuffer(0, VertexBufferView);

                if(EnableCulling)
                {
                    CommandList.ExecuteIndirect(
                        CommandSignature, 
                        TriangleCount, 
                        ProcessedCommandBuffers[FrameIndex], 
                        0,
                        ProcessedCommandBuffers[FrameIndex],
                        CommandBufferCounterOffset
                        );
                }
                else
                {
                    CommandList.ExecuteIndirect(
                        CommandSignature,
                        TriangleCount,
                        CommandBuffer,
                        CommandSizePerFrame * FrameIndex,
                        null,
                        0
                        );
                }

                CommandList.ResourceBarrier(
                    new ResourceTransitionBarrier(
                        EnableCulling ? ProcessedCommandBuffers[FrameIndex] : CommandBuffer,
                        ResourceStates.IndirectArgument,
                        EnableCulling ? ResourceStates.CopyDestination : ResourceStates.NonPixelShaderResource
                        ),
                    new ResourceTransitionBarrier(
                        RenderTargets[FrameIndex],
                        ResourceStates.RenderTarget,
                        ResourceStates.Present
                        )
                    );
            }

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

            FrameNumber++;
        }
    }
}