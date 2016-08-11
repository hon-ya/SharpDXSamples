using System;
using System.Threading;
using SharpDX.DXGI;

namespace D3D12HelloMultipleRenderTargets
{
    using SharpDX;
    using SharpDX.Windows;
    using SharpDX.Direct3D12;
    using System.Runtime.InteropServices;

    internal class HelloMultipleRenderTargets : IDisposable
    {
        private struct Vertex
        {
            public Vector3 Position;
            public Vector2 TexCoord;
        };

        private struct VertexMRT
        {
            public Vector3 Position;
            public Vector4 Color0;
            public Vector4 Color1;
            public Vector4 Color2;
            public Vector4 Color3;
        };

        private const int FrameCount = 2;
        private const int MRTCount = 4;

        private int Width;
        private int Height;

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
        private ViewportF[] Viewports = new ViewportF[MRTCount];
        private Rectangle ScissorRect;
        private Color4 ClearColor;
        private Color4[] ClearColors = new Color4[MRTCount];
        private RootSignature RootSignature;
        private RootSignature RootSignatureMRT;
        private PipelineState PipelineState;
        private PipelineState PipelineStateMRT;
        private DescriptorHeap ShaderResourceViewHeap;
        private int SrvDescriptorSize;
        private Resource[] TextureMRTs = new Resource[MRTCount];
        private Resource VertexBufferRectangle;
        private VertexBufferView VertexBufferViewRectangle;
        private Resource VertexBufferTriangle;
        private VertexBufferView VertexBufferViewTriangle;

        public void Dispose()
        {
            WaitForPreviousFrame();

            foreach (var target in RenderTargets)
            {
                target.Dispose();
            }

            foreach (var texture in TextureMRTs)
            {
                texture.Dispose();
            }

            CommandAllocator.Dispose();
            CommandQueue.Dispose();
            RootSignature.Dispose();
            RootSignatureMRT.Dispose();
            RenderTargetViewHeap.Dispose();
            ShaderResourceViewHeap.Dispose();
            PipelineState.Dispose();
            PipelineStateMRT.Dispose();
            CommandList.Dispose();
            VertexBufferRectangle.Dispose();
            VertexBufferTriangle.Dispose();
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
            ClearColor = new Color4(0.0f, 0.2f, 0.4f, 1.0f);

            // MRT の結果を 4 分割描画するためのビューポート
            Viewports[0] = new ViewportF(0, 0, Width / 2.0f, Height / 2.0f, 0.0f, 1.0f);
            Viewports[1] = new ViewportF(Width / 2.0f, 0, Width / 2.0f, Height / 2.0f, 0.0f, 1.0f);
            Viewports[2] = new ViewportF(0, Height / 2.0f, Width / 2.0f, Height / 2.0f, 0.0f, 1.0f);
            Viewports[3] = new ViewportF(Width / 2.0f, Height / 2.0f, Width / 2.0f, Height / 2.0f, 0.0f, 1.0f);

            // 各 MRT のターゲットのクリアカラー
            ClearColors[0] = new Color4(0.2f, 0.0f, 0.0f, 1.0f);
            ClearColors[1] = new Color4(0.0f, 0.2f, 0.0f, 1.0f);
            ClearColors[2] = new Color4(0.0f, 0.0f, 0.2f, 1.0f);
            ClearColors[3] = new Color4(0.1f, 0.1f, 0.0f, 1.0f);

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

            // フレームバッファ + MRT の RTV
            var rtvHeapDesc = new DescriptorHeapDescription()
            {
                DescriptorCount = FrameCount + MRTCount,    
                Type = DescriptorHeapType.RenderTargetView,
                Flags = DescriptorHeapFlags.None,
            };
            RenderTargetViewHeap = Device.CreateDescriptorHeap(rtvHeapDesc);
            RtvDescriptorSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);

            var rtvDescHandle = RenderTargetViewHeap.CPUDescriptorHandleForHeapStart;
            for (var i = 0; i < FrameCount; i++)
            {
                RenderTargets[i] = SwapChain.GetBackBuffer<Resource>(i);
                Device.CreateRenderTargetView(RenderTargets[i], null, rtvDescHandle);
                rtvDescHandle += RtvDescriptorSize;
            }

            // MRT のターゲットをシェーダリソースとして扱うための SRV
            var srvHeapDesc = new DescriptorHeapDescription()
            {
                DescriptorCount = MRTCount, 
                Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                Flags = DescriptorHeapFlags.ShaderVisible,
            };
            ShaderResourceViewHeap = Device.CreateDescriptorHeap(srvHeapDesc);
            SrvDescriptorSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

            CommandAllocator = Device.CreateCommandAllocator(CommandListType.Direct);
        }

        private void LoadAssets()
        {
            {
                // MRT 結果表示用ルートシグネチャの作成
                var rootSignatureDesc = new RootSignatureDescription(RootSignatureFlags.AllowInputAssemblerInputLayout,
                    new[]
                    {
                        new RootParameter(ShaderVisibility.Pixel,
                            new DescriptorRange()
                            {
                                RangeType = DescriptorRangeType.ShaderResourceView,
                                BaseShaderRegister = 0,
                                OffsetInDescriptorsFromTableStart = int.MinValue,
                                DescriptorCount = 1,
                            })
                        },
                    new[]
                    {
                        new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
                        {
                            Filter = Filter.MinimumMinMagMipPoint,
                            AddressUVW = TextureAddressMode.Border,
                        }
                    });
                RootSignature = Device.CreateRootSignature(rootSignatureDesc.Serialize());

                // MRT 用ルートシグネチャの作成
                var rootSignatureDescMRT = new RootSignatureDescription(RootSignatureFlags.AllowInputAssemblerInputLayout);
                RootSignatureMRT = Device.CreateRootSignature(rootSignatureDesc.Serialize());
            }

            {
#if DEBUG
                var vertexShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Shaders.hlsl", "VSMain", "vs_5_0", SharpDX.D3DCompiler.ShaderFlags.Debug));
                var pixelShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Shaders.hlsl", "PSMain", "ps_5_0", SharpDX.D3DCompiler.ShaderFlags.Debug));

                var vertexShaderMRT = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("ShadersMRT.hlsl", "VSMain", "vs_5_0", SharpDX.D3DCompiler.ShaderFlags.Debug));
                var pixelShaderMRT = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("ShadersMRT.hlsl", "PSMain", "ps_5_0", SharpDX.D3DCompiler.ShaderFlags.Debug));
#else
                var vertexShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Shaders.hlsl", "VSMain", "vs_5_0"));
                var pixelShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Shaders.hlsl", "PSMain", "ps_5_0"));

                var vertexShaderMRT = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("ShadersMRT.hlsl", "VSMain", "vs_5_0"));
                var pixelShaderMRT = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("ShadersMRT.hlsl", "PSMain", "ps_5_0"));
#endif

                // MRT 結果表示用パイプラインステートオブジェクトの作成
                var inputElementDescs = new[]
                {
                    new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                    new InputElement("TEXCOORD", 0, Format.R32G32_Float, 12, 0),
                };

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

                // MRT 用パイプラインステートオブジェクトの作成
                var inputElementDescsMRT = new[]
                {
                    new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                    new InputElement("COLOR", 0, Format.R32G32B32A32_Float, 12, 0),
                    new InputElement("COLOR", 1, Format.R32G32B32A32_Float, 12 + 16 * 1, 0),
                    new InputElement("COLOR", 2, Format.R32G32B32A32_Float, 12 + 16 * 2, 0),
                    new InputElement("COLOR", 3, Format.R32G32B32A32_Float, 12 + 16 * 3, 0),
                };

                var psoDescMRT = new GraphicsPipelineStateDescription()
                {
                    InputLayout = new InputLayoutDescription(inputElementDescsMRT),
                    RootSignature = RootSignatureMRT,
                    VertexShader = vertexShaderMRT,
                    PixelShader = pixelShaderMRT,
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
                    RenderTargetCount = MRTCount,
                    Flags = PipelineStateFlags.None,
                    SampleDescription = new SampleDescription(1, 0),
                    StreamOutput = new StreamOutputDescription(),
                };
                for(var i = 0; i < MRTCount; i++)
                {
                    psoDescMRT.RenderTargetFormats[i] = Format.R8G8B8A8_UNorm;
                }
                PipelineStateMRT = Device.CreateGraphicsPipelineState(psoDescMRT);
            }

            // 異なるパイプラインステートを扱うため、初期パイプラインステートは指定しない。
            CommandList = Device.CreateCommandList(CommandListType.Direct, CommandAllocator, null);
            CommandList.Close();

            // MRT の結果をテクスチャとして表示するための矩形頂点データ
            {
                var rectangleVertices = new[]
                {
                    new Vertex { Position = new Vector3(-1.0f, -1.0f, 0.0f), TexCoord = new Vector2(0.0f, 1.0f) },
                    new Vertex { Position = new Vector3(-1.0f,  1.0f, 0.0f), TexCoord = new Vector2(0.0f, 0.0f) },
                    new Vertex { Position = new Vector3( 1.0f, -1.0f, 0.0f), TexCoord = new Vector2(1.0f, 1.0f) },
                    new Vertex { Position = new Vector3( 1.0f,  1.0f, 0.0f), TexCoord = new Vector2(1.0f, 0.0f) },
                };
                var vertexBufferSize = Utilities.SizeOf(rectangleVertices);

                VertexBufferRectangle = Device.CreateCommittedResource(
                    new HeapProperties(HeapType.Upload),
                    HeapFlags.None,
                    ResourceDescription.Buffer(vertexBufferSize),
                    ResourceStates.GenericRead
                    );

                var pVertexDataBegin = VertexBufferRectangle.Map(0);
                {
                    Utilities.Write(pVertexDataBegin, rectangleVertices, 0, rectangleVertices.Length);
                }
                VertexBufferRectangle.Unmap(0);

                VertexBufferViewRectangle = new VertexBufferView()
                {
                    BufferLocation = VertexBufferRectangle.GPUVirtualAddress,
                    StrideInBytes = Utilities.SizeOf<Vertex>(),
                    SizeInBytes = vertexBufferSize,
                };
            }

            // MRT 描画で描画するトライアングル頂点データ
            {
                float aspectRatio = Viewport.Width / Viewport.Height;
                var triangleVertices = new[]
                {
                    new VertexMRT
                    {
                        Position = new Vector3(0.0f, 0.25f * aspectRatio, 0.0f),
                        Color0 = new Vector4(1.0f, 0.0f, 0.0f, 0.0f),
                        Color1 = new Vector4(0.0f, 1.0f, 0.0f, 0.0f),
                        Color2 = new Vector4(0.0f, 0.0f, 1.0f, 0.0f),
                        Color3 = new Vector4(1.0f, 1.0f, 0.0f, 0.0f),
                    },
                    new VertexMRT
                    {
                        Position = new Vector3(0.25f, -0.25f * aspectRatio, 0.0f),
                        Color0 = new Vector4(0.0f, 1.0f, 0.0f, 0.0f),
                        Color1 = new Vector4(0.0f, 0.0f, 1.0f, 0.0f),
                        Color2 = new Vector4(1.0f, 0.0f, 0.0f, 0.0f),
                        Color3 = new Vector4(0.0f, 1.0f, 1.0f, 0.0f),
                    },
                    new VertexMRT
                    {
                        Position = new Vector3(-0.25f, -0.25f * aspectRatio, 0.0f),
                        Color0 = new Vector4(0.0f, 0.0f, 1.0f, 0.0f),
                        Color1 = new Vector4(1.0f, 0.0f, 0.0f, 0.0f),
                        Color2 = new Vector4(0.0f, 1.0f, 0.0f, 0.0f),
                        Color3 = new Vector4(1.0f, 0.0f, 1.0f, 0.0f),
                    },
                };
                var vertexBufferSize = Utilities.SizeOf(triangleVertices);

                VertexBufferTriangle = Device.CreateCommittedResource(
                    new HeapProperties(HeapType.Upload),
                    HeapFlags.None,
                    ResourceDescription.Buffer(vertexBufferSize),
                    ResourceStates.GenericRead
                    );

                var pVertexDataBegin = VertexBufferTriangle.Map(0);
                {
                    Utilities.Write(pVertexDataBegin, triangleVertices, 0, triangleVertices.Length);
                }
                VertexBufferTriangle.Unmap(0);

                VertexBufferViewTriangle = new VertexBufferView()
                {
                    BufferLocation = VertexBufferTriangle.GPUVirtualAddress,
                    StrideInBytes = Utilities.SizeOf<VertexMRT>(),
                    SizeInBytes = vertexBufferSize,
                };
            }

            // MRT 描画で利用するテクスチャの作成
            {
                var textureDesc = ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, Width, Height, 1, 0, 1, 0, ResourceFlags.AllowRenderTarget);

                var srvDesc = new ShaderResourceViewDescription
                {
                    Shader4ComponentMapping = D3DXUtilities.DefaultComponentMapping(),
                    Format = textureDesc.Format,
                    Dimension = ShaderResourceViewDimension.Texture2D,
                    Texture2D = { MipLevels = 1 },
                };

                var rtvHandle = RenderTargetViewHeap.CPUDescriptorHandleForHeapStart;
                rtvHandle += FrameCount * RtvDescriptorSize;

                var srvHandle = ShaderResourceViewHeap.CPUDescriptorHandleForHeapStart;

                for (var i = 0; i < MRTCount; i++)
                {
                    TextureMRTs[i] = Device.CreateCommittedResource(
                        new HeapProperties(HeapType.Default),
                        HeapFlags.None,
                        textureDesc,
                        ResourceStates.PixelShaderResource,
                        new ClearValue
                        {
                            Format = textureDesc.Format,
                            Color = new SharpDX.Mathematics.Interop.RawVector4()
                            {
                                X = ClearColors[i].Red,
                                Y = ClearColors[i].Green,
                                Z = ClearColors[i].Blue,
                                W = ClearColors[i].Alpha,
                            },
                        }
                        );

                    // テクスチャをカラーターゲットとして扱うビューの作成
                    Device.CreateRenderTargetView(TextureMRTs[i], null, rtvHandle);
                    rtvHandle += RtvDescriptorSize;

                    // テクスチャをシェーダリソースとして扱うビューの作成
                    Device.CreateShaderResourceView(TextureMRTs[i], srvDesc, srvHandle);
                    srvHandle += SrvDescriptorSize;
                }
            }

            {
                Fence = Device.CreateFence(0, FenceFlags.None);
                FenceValue = 1;

                FenceEvent = new AutoResetEvent(false);
            }
        }

        internal void Update()
        {
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

            CommandList.Reset(CommandAllocator, null);

            // MRT 描画
            {
                CommandList.PipelineState = PipelineStateMRT;

                CommandList.SetGraphicsRootSignature(RootSignatureMRT);
                CommandList.SetViewport(Viewport);
                CommandList.SetScissorRectangles(ScissorRect);

                for(var i = 0; i < MRTCount; i++)
                {
                    CommandList.ResourceBarrierTransition(TextureMRTs[i], ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
                }

                var rtvDescHandle = RenderTargetViewHeap.CPUDescriptorHandleForHeapStart;
                rtvDescHandle += FrameCount * RtvDescriptorSize;

                CommandList.SetRenderTargets(MRTCount, rtvDescHandle, null);

                for (var i = 0; i < MRTCount; i++)
                {
                    CommandList.ClearRenderTargetView(rtvDescHandle, ClearColors[i], 0, null);
                    rtvDescHandle += RtvDescriptorSize;
                }

                CommandList.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleList;
                CommandList.SetVertexBuffer(0, VertexBufferViewTriangle);
                CommandList.DrawInstanced(3, 1, 0, 0);

                for (var i = 0; i < MRTCount; i++)
                {
                    CommandList.ResourceBarrierTransition(TextureMRTs[i], ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);
                }
            }

            // MRT で描画したテクスチャを画面 4 分割して表示
            {
                CommandList.PipelineState = PipelineState;

                CommandList.SetGraphicsRootSignature(RootSignature);

                CommandList.SetDescriptorHeaps(1, new[] { ShaderResourceViewHeap });

                CommandList.SetScissorRectangles(ScissorRect);

                CommandList.ResourceBarrierTransition(RenderTargets[FrameIndex], ResourceStates.Present, ResourceStates.RenderTarget);

                var rtvDescHandle = RenderTargetViewHeap.CPUDescriptorHandleForHeapStart;
                rtvDescHandle += FrameIndex * RtvDescriptorSize;

                CommandList.SetRenderTargets(rtvDescHandle, null);

                CommandList.ClearRenderTargetView(rtvDescHandle, ClearColor, 0, null);

                CommandList.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleStrip;
                CommandList.SetVertexBuffer(0, VertexBufferViewRectangle);

                var srvHandle = ShaderResourceViewHeap.GPUDescriptorHandleForHeapStart;

                for (var i = 0; i < MRTCount; i++)
                {
                    CommandList.SetGraphicsRootDescriptorTable(0, srvHandle);
                    CommandList.SetViewport(Viewports[i]);

                    CommandList.DrawInstanced(4, 1, 0, 0);

                    srvHandle += SrvDescriptorSize;
                }

                CommandList.ResourceBarrierTransition(RenderTargets[FrameIndex], ResourceStates.RenderTarget, ResourceStates.Present);
            }

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
        }
    }
}