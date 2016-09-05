using System;
using System.Threading;
using SharpDX.DXGI;

namespace D3D12ShadowMap
{
    using SharpDX;
    using SharpDX.Windows;
    using SharpDX.Direct3D12;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using SharpDXSample;
    using System.Diagnostics;

    internal static class EnumUtilities
    {
        static public int GetCount<T>() where T : struct
        {
            return Enum.GetNames(typeof(T)).Length;
        }
    }

    internal class D3D12ShadowMap : IDisposable
    {
        private struct Vertex
        {
            public Vector3 Position;
            public Vector4 Color;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 16, Size = 512)]
        private struct ConstantBufferDataStruct
        {
            public Matrix Model;
            public Matrix View;
            public Matrix Projection;
            public Matrix LightView;
            public Matrix LightProjection;
        }

        enum ShadowType
        {
            Simple,
            PCF,
        }

        private const int FrameCount = 2;
        private const int MaterialCount = 2;
        private const int ShadowMapWidth = 512;
        private const int ShadowMapHeight = 512;

        private int Width;
        private int Height;
        private int FrameNumber = 0;
        private ShadowType CurrentShadowType;

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
        private ViewportF ViewportShadowMap;
        private Rectangle ScissorRect;
        private Rectangle ScissorRectShadowMap;
        private RootSignature RootSignature;
        private PipelineState PipelineStateSimple;
        private PipelineState PipelineStatePCF;
        private PipelineState PipelineStateShadowMap;
        private Resource VertexBuffer;
        private VertexBufferView VertexBufferView;
        private DescriptorHeap CbvSrvUavHeap;
        private Resource IndexBuffer;
        private IndexBufferView IndexBufferView;
        private ConstantBufferDataStruct[] ConstantBufferData = new ConstantBufferDataStruct[MaterialCount];
        private Resource ConstantBuffer;
        private IntPtr ConstantBufferPtr;
        private int CbvSrvUavDescriptorSize;
        private DescriptorHeap DepthStencilViewHeap;
        private int DsvDescriptorSize;
        private Resource DepthStencil;
        private Resource[] ShadowMaps = new Resource[FrameCount];
        private SimpleCamera Camera = new SimpleCamera();
        private RenderForm Form;

        public void Dispose()
        {
            WaitForGpu();

            foreach(var resource in ShadowMaps)
            {
                resource.Dispose();
            }
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
            CbvSrvUavHeap.Dispose();
            PipelineStateSimple.Dispose();
            PipelineStatePCF.Dispose();
            PipelineStateShadowMap.Dispose();
            CommandList.Dispose();
            DepthStencil.Dispose();
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
            Form = form;
            Form.KeyDown += OnKeyDown;

            Width = form.ClientSize.Width;
            Height = form.ClientSize.Height;

            Viewport = new ViewportF(0, 0, Width, Height, 0.0f, 1.0f);
            ScissorRect = new Rectangle(0, 0, Width, Height);

            ViewportShadowMap = new ViewportF(0, 0, ShadowMapWidth, ShadowMapHeight, 0.0f, 1.0f);
            ScissorRectShadowMap = new Rectangle(0, 0, ShadowMapWidth, ShadowMapHeight);

            Camera.Initialize(new Vector3(0.0f, 1.0f, 10.0f));
            Camera.RegisterHandler(form);

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

            {
                var rtvHeapDesc = new DescriptorHeapDescription()
                {
                    DescriptorCount = FrameCount,
                    Type = DescriptorHeapType.RenderTargetView,
                    Flags = DescriptorHeapFlags.None,
                };
                RenderTargetViewHeap = Device.CreateDescriptorHeap(rtvHeapDesc);
                RtvDescriptorSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);

                var dsvHeapDesc = new DescriptorHeapDescription()
                {
                    DescriptorCount = 1 + FrameCount,
                    Type = DescriptorHeapType.DepthStencilView,
                    Flags = DescriptorHeapFlags.None,
                };
                DepthStencilViewHeap = Device.CreateDescriptorHeap(dsvHeapDesc);
                DsvDescriptorSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.DepthStencilView);

                var srvHeapDesc = new DescriptorHeapDescription()
                {
                    DescriptorCount = (1 + MaterialCount) * FrameCount,
                    Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                    Flags = DescriptorHeapFlags.ShaderVisible,
                };
                CbvSrvUavHeap = Device.CreateDescriptorHeap(srvHeapDesc);
                CbvSrvUavDescriptorSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
            }

            {
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
            {
                // ルートシグネチャは共用する。
                var rootSignatureDesc = new RootSignatureDescription(RootSignatureFlags.AllowInputAssemblerInputLayout,
                    new[]
                    {
                        new RootParameter(ShaderVisibility.Vertex,
                            new DescriptorRange()
                            {
                                RangeType = DescriptorRangeType.ConstantBufferView,
                                BaseShaderRegister = 0,
                                OffsetInDescriptorsFromTableStart = 0,
                                DescriptorCount = 1,
                            }),
                        new RootParameter(ShaderVisibility.Pixel,
                            new DescriptorRange()
                            {
                                RangeType = DescriptorRangeType.ShaderResourceView,
                                BaseShaderRegister = 0,
                                OffsetInDescriptorsFromTableStart = 0,
                                DescriptorCount = 1,
                            }),
                    },
                    new[]
                    {
                        new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
                        {
                            Filter = Filter.ComparisonMinMagMipLinear,
                            AddressUVW = TextureAddressMode.Border,
                            BorderColor = StaticBorderColor.OpaqueWhite,
                            ComparisonFunc = Comparison.LessEqual,
                        },
                    });
                RootSignature = Device.CreateRootSignature(rootSignatureDesc.Serialize());
            }

            {
#if DEBUG
                var vertexShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Shaders.hlsl", "VSMain", "vs_5_0", SharpDX.D3DCompiler.ShaderFlags.Debug | SharpDX.D3DCompiler.ShaderFlags.SkipOptimization));
                var pixelShaderSimple = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Shaders.hlsl", "PSMainSimple", "ps_5_0", SharpDX.D3DCompiler.ShaderFlags.Debug | SharpDX.D3DCompiler.ShaderFlags.SkipOptimization));
                var pixelShaderPCF = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Shaders.hlsl", "PSMainPCF", "ps_5_0", SharpDX.D3DCompiler.ShaderFlags.Debug | SharpDX.D3DCompiler.ShaderFlags.SkipOptimization));
#else
                var vertexShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Shaders.hlsl", "VSMain", "vs_5_0"));
                var pixelShaderSimple = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Shaders.hlsl", "PSMainSimple", "ps_5_0"));
                var pixelShaderPCF = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Shaders.hlsl", "PSMainPCF", "ps_5_0"));
#endif

                var inputElementDescs = new[]
                {
                    new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                    new InputElement("COLOR", 0, Format.R32G32B32A32_Float, 12, 0),
                };

                var psoDesc = new GraphicsPipelineStateDescription()
                {
                    InputLayout = new InputLayoutDescription(inputElementDescs),
                    RootSignature = RootSignature,
                    VertexShader = vertexShader,
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

                psoDesc.PixelShader = pixelShaderSimple;
                PipelineStateSimple = Device.CreateGraphicsPipelineState(psoDesc);

                psoDesc.PixelShader = pixelShaderPCF;
                PipelineStatePCF = Device.CreateGraphicsPipelineState(psoDesc);

                // シャドウマップ生成用パイプラインステートオブジェクトの作成
#if DEBUG
                var vertexShaderShadowMap = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Shaders.hlsl", "VSMainSM", "vs_5_0", SharpDX.D3DCompiler.ShaderFlags.Debug | SharpDX.D3DCompiler.ShaderFlags.SkipOptimization));
#else
                var vertexShaderShadowMap = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("ShadersShadowMap.hlsl", "VSMainSM", "vs_5_0"));
#endif

                var rasterizerStateShadowMap = RasterizerStateDescription.Default();
                rasterizerStateShadowMap.CullMode = CullMode.Front;

                // レンダーターゲットに対して描画しないため、ピクセルシェーダは使わない
                var psoDescShadowMap = new GraphicsPipelineStateDescription()
                {
                    InputLayout = new InputLayoutDescription(inputElementDescs),
                    RootSignature = RootSignature,
                    VertexShader = vertexShaderShadowMap,
                    PixelShader = null, 
                    RasterizerState = rasterizerStateShadowMap,
                    BlendState = BlendStateDescription.Default(),
                    DepthStencilFormat = Format.D32_Float,
                    DepthStencilState = DepthStencilStateDescription.Default(),
                    SampleMask = int.MaxValue,
                    PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                    RenderTargetCount = 0,
                    Flags = PipelineStateFlags.None,
                    SampleDescription = new SampleDescription(1, 0),
                    StreamOutput = new StreamOutputDescription(),
                };

                PipelineStateShadowMap = Device.CreateGraphicsPipelineState(psoDescShadowMap);
            }

            CommandList = Device.CreateCommandList(CommandListType.Direct, CommandAllocators[FrameIndex], null);

            var uploadResources = new List<Resource>();

            {
                var vertices = new[]
                {
                    //
                    // Cube
                    //
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

                    //
                    // Ground
                    //
                    new Vertex { Position = new Vector3(-1.0f,  -1.0f, -1.0f), Color = new Vector4(1.0f, 1.0f, 1.0f, 0.0f) },
                    new Vertex { Position = new Vector3( 1.0f,  -1.0f, -1.0f), Color = new Vector4(1.0f, 1.0f, 1.0f, 0.0f) },
                    new Vertex { Position = new Vector3( 1.0f,  -1.0f,  1.0f), Color = new Vector4(1.0f, 1.0f, 1.0f, 0.0f) },
                    new Vertex { Position = new Vector3(-1.0f,  -1.0f,  1.0f), Color = new Vector4(1.0f, 1.0f, 1.0f, 0.0f) },
                };

                var vertexBufferSize = Utilities.SizeOf(vertices);

                VertexBuffer = Device.CreateCommittedResource(
                    new HeapProperties(HeapType.Default),
                    HeapFlags.None,
                    ResourceDescription.Buffer(vertexBufferSize),
                    ResourceStates.CopyDestination
                    );

                var vertexBufferUpload = Device.CreateCommittedResource(
                    new HeapProperties(HeapType.Upload),
                    HeapFlags.None,
                    ResourceDescription.Buffer(vertexBufferSize),
                    ResourceStates.GenericRead
                    );
                uploadResources.Add(vertexBufferUpload);

                var vertexData = new D3D12Utilities.SubresourceData()
                {
                    Data = Utilities.ToByteArray(vertices),
                    Offset = 0,
                    RowPitch = vertexBufferSize,
                    SlicePitch = vertexBufferSize,
                };

                D3D12Utilities.UpdateSubresources(Device, CommandList, VertexBuffer, vertexBufferUpload, 0, 0, 1, new[] { vertexData });

                CommandList.ResourceBarrierTransition(VertexBuffer, ResourceStates.CopyDestination, ResourceStates.VertexAndConstantBuffer);

                VertexBufferView = new VertexBufferView()
                {
                    BufferLocation = VertexBuffer.GPUVirtualAddress,
                    StrideInBytes = Utilities.SizeOf<Vertex>(),
                    SizeInBytes = vertexBufferSize,
                };
            }

            {
                var indices = new UInt16[]
                {
                    //
                    // Cube
                    //
                    0, 1, 2, 0, 2, 3,		// front
			        4, 5, 6, 4, 6, 7,		// back
			        8, 9, 10, 8, 10, 11,	// top
			        12, 13, 14, 12, 14, 15,	// bottom
			        16, 17, 18, 16, 18, 19,	// right
			        20, 21, 22, 20, 22, 23,  // left

                    //
                    // Ground
                    //
                    0, 1, 2, 0, 2, 3,
                };

                var indexBufferSize = Utilities.SizeOf(indices) + Utilities.SizeOf(indices);

                IndexBuffer = Device.CreateCommittedResource(
                    new HeapProperties(HeapType.Default),
                    HeapFlags.None,
                    ResourceDescription.Buffer(indexBufferSize),
                    ResourceStates.CopyDestination
                    );

                var indexBufferUpload = Device.CreateCommittedResource(
                    new HeapProperties(HeapType.Upload),
                    HeapFlags.None,
                    ResourceDescription.Buffer(indexBufferSize),
                    ResourceStates.GenericRead
                    );
                uploadResources.Add(indexBufferUpload);

                var indexData = new D3D12Utilities.SubresourceData()
                {
                    Data = Utilities.ToByteArray(indices),
                    Offset = 0,
                    RowPitch = indexBufferSize,
                    SlicePitch = indexBufferSize,
                };

                D3D12Utilities.UpdateSubresources(Device, CommandList, IndexBuffer, indexBufferUpload, 0, 0, 1, new[] { indexData });

                CommandList.ResourceBarrier(new ResourceTransitionBarrier(IndexBuffer, ResourceStates.CopyDestination, ResourceStates.IndexBuffer));

                IndexBufferView = new IndexBufferView()
                {
                    BufferLocation = IndexBuffer.GPUVirtualAddress,
                    Format = Format.R16_UInt,
                    SizeInBytes = indexBufferSize,
                };
            }

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

            {
                ConstantBuffer = Device.CreateCommittedResource(
                    new HeapProperties(HeapType.Upload),
                    HeapFlags.None,
                    ResourceDescription.Buffer(Utilities.SizeOf<ConstantBufferDataStruct>() * MaterialCount * FrameCount),
                    ResourceStates.GenericRead
                    );

                ConstantBufferPtr = ConstantBuffer.Map(0);

                var currentPtr = ConstantBufferPtr;
                for (var i = 0; i < FrameCount; i++)
                {
                    for (var j = 0; j < MaterialCount; j++)
                    {
                        Utilities.Write(currentPtr, ref ConstantBufferData[j]);

                        currentPtr = IntPtr.Add(currentPtr, Utilities.SizeOf<ConstantBufferDataStruct>());
                    }
                }

                var cbvDesc = new ConstantBufferViewDescription()
                {
                    BufferLocation = ConstantBuffer.GPUVirtualAddress,
                    SizeInBytes = Utilities.SizeOf<ConstantBufferDataStruct>(),
                };

                var srvHandle = CbvSrvUavHeap.CPUDescriptorHandleForHeapStart;
                for (var i = 0; i < FrameCount; i++)
                {
                    srvHandle += CbvSrvUavDescriptorSize;

                    for (var j = 0; j < MaterialCount; j++)
                    {
                        Device.CreateConstantBufferView(cbvDesc, srvHandle);

                        cbvDesc.BufferLocation += cbvDesc.SizeInBytes;
                        srvHandle += CbvSrvUavDescriptorSize;
                    }
                }
            }

            // シャドウマップ用のデプステクスチャの作成
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

                var srvDesc = new ShaderResourceViewDescription()
                {
                    Shader4ComponentMapping = D3D12Utilities.DefaultComponentMapping(),
                    Format = Format.R32_Float,
                    Dimension = ShaderResourceViewDimension.Texture2D,
                    Texture2D = { MipLevels = 1 },
                };

                for (var i = 0; i < FrameCount; i++)
                {
                    var dsvHandle = DepthStencilViewHeap.CPUDescriptorHandleForHeapStart;
                    dsvHandle += DsvDescriptorSize + DsvDescriptorSize * i;

                    var srvHandle = CbvSrvUavHeap.CPUDescriptorHandleForHeapStart;
                    srvHandle += (1 + MaterialCount) * CbvSrvUavDescriptorSize * i;

                    // D32_Float と R32_Float の両方として使えるよう、R32_Typeless でテクスチャを作成
                    ShadowMaps[i] = Device.CreateCommittedResource(
                        new HeapProperties(HeapType.Default),
                        HeapFlags.None,
                        ResourceDescription.Texture2D(Format.R32_Typeless, ShadowMapWidth, ShadowMapHeight, 1, 1, 1, 0, ResourceFlags.AllowDepthStencil),
                        ResourceStates.PixelShaderResource,
                        depthOptimizedClearValue
                        );

                    Device.CreateDepthStencilView(ShadowMaps[i], depthStencilDesc, dsvHandle);
                    Device.CreateShaderResourceView(ShadowMaps[i], srvDesc, srvHandle);
                }
            }

            CommandList.Close();
            CommandQueue.ExecuteCommandList(CommandList);

            {
                Fence = Device.CreateFence(FenceValues[FrameIndex], FenceFlags.None);
                FenceValues[FrameIndex]++;

                FenceEvent = new AutoResetEvent(false);

                WaitForGpu();
            }

            foreach(var resource in uploadResources)
            {
                resource.Dispose();
            }
        }

        private void OnKeyDown(object sender, System.Windows.Forms.KeyEventArgs e)
        {
            if (e.KeyCode == System.Windows.Forms.Keys.C)
            {
                CurrentShadowType = (ShadowType)(((int)CurrentShadowType + 1) % EnumUtilities.GetCount<ShadowType>());
            }
        }

        internal void Update()
        {
            Camera.Update();

            var cubeModel =
                Matrix.RotationAxis(
                    new Vector3(0.0f, 1.0f, 0.0f),
                    MathUtil.DegreesToRadians(FrameNumber % 360)
                    ) *
                Matrix.Translation(0.0f, 1.5f, 0.0f);

            var groundModel = Matrix.Scaling(10.0f, 1.0f, 10.0f);

            var models = new[] { cubeModel, groundModel };

            var view = Camera.GetViewMatrix();

            var projection = Camera.GetProjectionMatrix(
                    MathUtil.DegreesToRadians(60.0f),
                    Viewport.Width / Viewport.Height,
                    0.0001f,
                    100.0f
                    );

            var lightView = Matrix.LookAtRH(
                    new Vector3(-4.0f, 8.0f, 4.0f),
                    new Vector3(0.0f, 0.0f, 0.0f),
                    new Vector3(0.0f, 1.0f, 0.0f)
                    );

            var lightProjection = Matrix.PerspectiveFovRH(
                    MathUtil.DegreesToRadians(30.0f),
                    ViewportShadowMap.Width / ViewportShadowMap.Height,
                    0.0001f,
                    100.0f
                    );

            var currentPtr = IntPtr.Add(ConstantBufferPtr, Utilities.SizeOf<ConstantBufferDataStruct>() * MaterialCount * FrameIndex);
            for (var i = 0; i < MaterialCount; i++)
            {
                ConstantBufferData[i].Model = Matrix.Transpose(models[i]);
                ConstantBufferData[i].View = Matrix.Transpose(view);
                ConstantBufferData[i].Projection = Matrix.Transpose(projection);
                ConstantBufferData[i].LightView = Matrix.Transpose(lightView);
                ConstantBufferData[i].LightProjection = Matrix.Transpose(lightProjection);

                Utilities.Write(currentPtr, ref ConstantBufferData[i]);

                currentPtr = IntPtr.Add(currentPtr, Utilities.SizeOf<ConstantBufferDataStruct>());
            }

            Form.Text = $"Shadow Type: {CurrentShadowType}";
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
            CommandList.Reset(CommandAllocators[FrameIndex], null);

            CommandList.SetGraphicsRootSignature(RootSignature);

            CommandList.SetDescriptorHeaps(1, new[] { CbvSrvUavHeap });

            CommandList.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleList;

            // シャドウマップを生成する
            {
                CommandList.PipelineState = PipelineStateShadowMap;

                CommandList.SetViewport(ViewportShadowMap);
                CommandList.SetScissorRectangles(ScissorRectShadowMap);

                CommandList.ResourceBarrierTransition(ShadowMaps[FrameIndex], ResourceStates.PixelShaderResource, ResourceStates.DepthWrite);

                var dsvHandle = DepthStencilViewHeap.CPUDescriptorHandleForHeapStart;
                dsvHandle += DsvDescriptorSize + DsvDescriptorSize * FrameIndex;

                var srvHandle = CbvSrvUavHeap.GPUDescriptorHandleForHeapStart;
                srvHandle += (1 + MaterialCount) * CbvSrvUavDescriptorSize * FrameIndex;

                // レンダーターゲットには描画しないので、レンダーターゲットをバインドしない。
                CommandList.SetRenderTargets((CpuDescriptorHandle?)null, dsvHandle);
                CommandList.ClearDepthStencilView(dsvHandle, ClearFlags.FlagsDepth, 1.0f, 0);

                CommandList.SetVertexBuffer(0, VertexBufferView);
                CommandList.SetIndexBuffer(IndexBufferView);
                CommandList.SetGraphicsRootDescriptorTable(0, srvHandle + CbvSrvUavDescriptorSize);
                CommandList.DrawIndexedInstanced(36, 1, 0, 0, 0);
                CommandList.SetGraphicsRootDescriptorTable(0, srvHandle + CbvSrvUavDescriptorSize * 2);
                CommandList.DrawIndexedInstanced(6, 1, 36, 24, 0);

                CommandList.ResourceBarrierTransition(ShadowMaps[FrameIndex], ResourceStates.DepthWrite, ResourceStates.PixelShaderResource);
            }

            // シャドウマップを利用した描画を行う
            {
                switch(CurrentShadowType)
                {
                    case ShadowType.Simple:
                        {
                            CommandList.PipelineState = PipelineStateSimple;
                        }
                        break;
                    case ShadowType.PCF:
                        {
                            CommandList.PipelineState = PipelineStatePCF;
                        }
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                CommandList.SetViewport(Viewport);
                CommandList.SetScissorRectangles(ScissorRect);

                CommandList.ResourceBarrierTransition(RenderTargets[FrameIndex], ResourceStates.Present, ResourceStates.RenderTarget);

                var rtvHandle = RenderTargetViewHeap.CPUDescriptorHandleForHeapStart;
                rtvHandle += FrameIndex * RtvDescriptorSize;

                var dsvHandle = DepthStencilViewHeap.CPUDescriptorHandleForHeapStart;

                var srvHandle = CbvSrvUavHeap.GPUDescriptorHandleForHeapStart;
                srvHandle += (1 + MaterialCount) * CbvSrvUavDescriptorSize * FrameIndex;

                CommandList.SetRenderTargets(rtvHandle, dsvHandle);

                CommandList.ClearRenderTargetView(rtvHandle, new Color4(0.0f, 0.2f, 0.4f, 1.0f), 0, null);
                CommandList.ClearDepthStencilView(dsvHandle, ClearFlags.FlagsDepth, 1.0f, 0);

                CommandList.SetGraphicsRootDescriptorTable(1, srvHandle);

                CommandList.SetVertexBuffer(0, VertexBufferView);
                CommandList.SetIndexBuffer(IndexBufferView);
                CommandList.SetGraphicsRootDescriptorTable(0, srvHandle + CbvSrvUavDescriptorSize);
                CommandList.DrawIndexedInstanced(36, 1, 0, 0, 0);
                CommandList.SetGraphicsRootDescriptorTable(0, srvHandle + CbvSrvUavDescriptorSize * 2);
                CommandList.DrawIndexedInstanced(6, 1, 36, 24, 0);

                CommandList.ResourceBarrierTransition(RenderTargets[FrameIndex], ResourceStates.RenderTarget, ResourceStates.Present);
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