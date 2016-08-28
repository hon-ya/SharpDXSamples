using System;
using System.Threading;
using SharpDX.DXGI;

namespace D3D12SmallResources
{
    using SharpDX;
    using SharpDX.Windows;
    using SharpDX.Direct3D12;
    using System.Collections.Generic;
    using System.Windows.Forms;
    using SharpDXSample;

    internal class D3D12SmallResources : IDisposable
    {
        private struct Vertex
        {
            public Vector3 Position;
            public Vector2 UV;
        };

        private const int FrameCount = 2;
        private const int GridWidth = 11;
        private const int GridHeight = 7;
        private const int TextureCount = GridWidth * GridHeight;
        private const int TextureWidth = 32;
        private const int TextureHeight = 32;
        private const int TexturePixelSizeInBytes = 4;
        private const int SmallReousrcePlacementAlignment = 4096;
        private const int DefaultResourcePlacementAlignment = 65536;

        private bool UsePlacedResources = true;

        private Form Form;
        private Adapter3 Adapter;
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
        private CommandQueue CopyQueue;
        private DescriptorHeap CbvSrvUavViewHeap;
        private int CbvSrvUavDescriptorSize;
        private CommandAllocator CopyCommandAllocator;
        private GraphicsCommandList CopyCommandList;
        private Heap TextureHeap;
        private Resource[] Textures = new Resource[TextureCount];
        private Random Random = new Random();

        public void Dispose()
        {
            WaitForGpu();

            foreach(var resource in Textures)
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
            CopyCommandAllocator.Dispose();
            CommandQueue.Dispose();
            CopyQueue.Dispose();
            RootSignature.Dispose();
            RenderTargetViewHeap.Dispose();
            CbvSrvUavViewHeap.Dispose();
            PipelineState.Dispose();
            CommandList.Dispose();
            CopyCommandList.Dispose();
            VertexBuffer.Dispose();
            TextureHeap.Dispose();
            Fence.Dispose();
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

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == System.Windows.Forms.Keys.Space)
            {
                // スペース押下で、PlacedResource の使用・不使用を切り替え
                UsePlacedResources = !UsePlacedResources;

                WaitForGpu();

                // 以前のテクスチャデータを破棄
                foreach (var resource in Textures)
                {
                    resource.Dispose();
                }
                TextureHeap.Dispose();

                // 新規にテクスチャを構築
                CreateTextures();
            }
        }

        private void LoadPipeline(RenderForm form)
        {
            Form = form;

            var width = form.ClientSize.Width;
            var height = form.ClientSize.Height;

            Viewport = new ViewportF(0, 0, width, height, 0.0f, 1.0f);
            ScissorRect = new Rectangle(0, 0, width, height);

#if DEBUG
            {
                DebugInterface.Get().EnableDebugLayer();
            }
#endif

            using (var factory = new Factory4())
            {
                Adapter = factory.GetAdapter(0).QueryInterface<Adapter3>();

                Device = new Device(Adapter, SharpDX.Direct3D.FeatureLevel.Level_11_0);

                var commandQueueDesc = new CommandQueueDescription(CommandListType.Direct);
                CommandQueue = Device.CreateCommandQueue(commandQueueDesc);

                var copyQueueDesc = new CommandQueueDescription(CommandListType.Copy);
                CopyQueue = Device.CreateCommandQueue(copyQueueDesc);

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

            {
                var rtvHeapDesc = new DescriptorHeapDescription()
                {
                    DescriptorCount = FrameCount,
                    Type = DescriptorHeapType.RenderTargetView,
                    Flags = DescriptorHeapFlags.None,
                };
                RenderTargetViewHeap = Device.CreateDescriptorHeap(rtvHeapDesc);
                RtvDescriptorSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);

                var cbvSrvUavHeapDesc = new DescriptorHeapDescription()
                {
                    DescriptorCount = TextureCount,
                    Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                    Flags = DescriptorHeapFlags.ShaderVisible,
                };
                CbvSrvUavViewHeap = Device.CreateDescriptorHeap(cbvSrvUavHeapDesc);
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

            CopyCommandAllocator = Device.CreateCommandAllocator(CommandListType.Copy);
        }

        private void LoadAssets()
        {
            {
                var rootSignatureDesc = new RootSignatureDescription(RootSignatureFlags.AllowInputAssemblerInputLayout,
                    new[]
                    {
                        new RootParameter(ShaderVisibility.Pixel,
                            new []
                            {
                                new DescriptorRange()
                                {
                                    RangeType = DescriptorRangeType.ShaderResourceView,
                                    BaseShaderRegister = 0,
                                    OffsetInDescriptorsFromTableStart = 0,
                                    DescriptorCount = 1,
                                },
                            }),
                    },
                    new[]
                    {
                        new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
                        {
                            Filter = Filter.MinMagMipLinear,
                            AddressUVW = TextureAddressMode.Border,
                        },
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
            }

            CommandList = Device.CreateCommandList(CommandListType.Direct, CommandAllocators[FrameIndex], PipelineState);

            CopyCommandList = Device.CreateCommandList(CommandListType.Copy, CopyCommandAllocator, null);
            CopyCommandList.Close();

            var uploadResources = new List<Resource>();

            // 頂点バッファ、ビューを作成
            {
                // 各テクスチャを表示するための矩形データをテクスチャ数だけ作成
                var quadVertices = new Vertex[TextureCount * 4];
                {
                    float aspectRatio = Viewport.Width / Viewport.Height;

                    int index = 0;
                    float offsetX = 0.15f;
                    float marginX = offsetX / 10.0f;
                    float startX = (GridWidth / 2.0f) * -(offsetX + marginX) + marginX / 2.0f;
                    float offsetY = offsetX * aspectRatio;
                    float marginY = offsetY / 10.0f;
                    float y = (GridHeight / 2.0f) * (offsetY + marginY) - marginY / 2.0f;
                    for (int row = 0; row < GridHeight; row++)
                    {
                        float x = startX;
                        for (int column = 0; column < GridWidth; column++)
                        {
                            quadVertices[index++] = new Vertex { Position = new Vector3(x, y - offsetY, 0.0f), UV = new Vector2(0.0f, 0.0f) };
                            quadVertices[index++] = new Vertex { Position = new Vector3(x, y, 0.0f), UV = new Vector2(0.0f, 1.0f) };
                            quadVertices[index++] = new Vertex { Position = new Vector3(x + offsetX, y - offsetY, 0.0f), UV = new Vector2(1.0f, 0.0f) };
                            quadVertices[index++] = new Vertex { Position = new Vector3(x + offsetX, y, 0.0f), UV = new Vector2(1.0f, 1.0f) };
                            x += offsetX + marginX;
                        }
                        y -= offsetY + marginY;
                    }
                }
                var vertexBufferSize = Utilities.SizeOf(quadVertices);

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
                    Data = Utilities.ToByteArray(quadVertices),
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

            // テクスチャを構築
            CreateTextures();
        }

        /// <summary>
        /// テクスチャの作成
        /// </summary>
        private void CreateTextures()
        {
            CopyCommandAllocator.Reset();
            CopyCommandList.Reset(CopyCommandAllocator, null);

            var textureDesc = ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, TextureWidth, TextureHeight, 1, 1);

            if(UsePlacedResources)
            {
                // PlacedResource を使ったテクスチャを作成します。

                textureDesc.Alignment = SmallReousrcePlacementAlignment;
                var info = Device.GetResourceAllocationInfo(0, textureDesc);

                if(info.Alignment != SmallReousrcePlacementAlignment)
                {
                    textureDesc.Alignment = 0;
                    info = Device.GetResourceAllocationInfo(0, textureDesc);
                }

                // すべてのテクスチャを収める Heap を作成します。
                var heapDesc = new HeapDescription()
                {
                    SizeInBytes = TextureCount * info.SizeInBytes,
                    Properties = new HeapProperties(HeapType.Default),
                    Alignment = 0,
                    Flags = HeapFlags.DenyBuffers | HeapFlags.DenyRtDomainShaderTextureS,
                };
                TextureHeap = Device.CreateHeap(heapDesc);

                var barriers = new ResourceBarrier[TextureCount];

                for(var i = 0; i < TextureCount; i++)
                {
                    // Heap 上の PlacedResource として各テクスチャを作成します。
                    Textures[i] = Device.CreatePlacedResource(
                        TextureHeap,
                        i * info.SizeInBytes,
                        textureDesc,
                        ResourceStates.Common
                        );

                    // PlacedResource を利用するには、AliasingBarrier を設定する必要があります。
                    // https://msdn.microsoft.com/ja-jp/library/windows/desktop/dn899180(v=vs.85).aspx
                    //
                    // SharpDX では、resourceBefore を null にするとエラーを返すので、無効にしておきます。
                    //barriers[i] = new ResourceAliasingBarrier(null, Textures[i]);
                }

                //CopyCommandList.ResourceBarrier(barriers);
            }
            else
            {
                // 個別の CommittedResource としてテクスチャを作成します。

                for (var i = 0; i < TextureCount; i++)
                {
                    Textures[i] = Device.CreateCommittedResource(
                        new HeapProperties(HeapType.Default),
                        HeapFlags.None,
                        textureDesc,
                        ResourceStates.Common
                        );
                }
            }

            var uploadResources = new List<Resource>();

            // テクスチャデータを書き込み、シェーダリソースビューを作成します。
            var srvHandle = CbvSrvUavViewHeap.CPUDescriptorHandleForHeapStart;
            for(var i = 0; i < TextureCount; i++)
            {
                var uploadBufferSize = D3D12Utilities.GetRequiredIntermediateSize(Device, Textures[i], 0, 1) + DefaultResourcePlacementAlignment;

                var textureUpload = Device.CreateCommittedResource(
                    new HeapProperties(HeapType.Upload),
                    HeapFlags.None,
                    ResourceDescription.Buffer(uploadBufferSize),
                    ResourceStates.GenericRead
                    );
                uploadResources.Add(textureUpload);

                var textureData = new D3D12Utilities.SubresourceData()
                {
                    Data = GenerateTexture(),
                    Offset = 0,
                    RowPitch = TextureWidth * TexturePixelSizeInBytes,
                    SlicePitch = TextureWidth * TextureHeight * TexturePixelSizeInBytes,
                };

                // 目的のテクスチャへテクスチャデータをコピー
                D3D12Utilities.UpdateSubresources(Device, CopyCommandList, Textures[i], textureUpload, 0, 0, 1, new[] { textureData });

                // シェーダリソースビューの作成
                var srvDesc = new ShaderResourceViewDescription
                {
                    Shader4ComponentMapping = D3D12Utilities.DefaultComponentMapping(),
                    Format = textureDesc.Format,
                    Dimension = ShaderResourceViewDimension.Texture2D,
                    Texture2D = { MipLevels = 1 },
                };
                Device.CreateShaderResourceView(Textures[i], srvDesc, srvHandle);
                srvHandle += CbvSrvUavDescriptorSize;
            }

            CopyCommandList.Close();
            CopyQueue.ExecuteCommandList(CopyCommandList);

            // 同期
            CopyQueue.Signal(Fence, FenceValues[FrameIndex]);
            Fence.SetEventOnCompletion(FenceValues[FrameIndex], FenceEvent.SafeWaitHandle.DangerousGetHandle());
            FenceEvent.WaitOne();
            FenceValues[FrameIndex]++;

            foreach(var resource in uploadResources)
            {
                resource.Dispose();
            }
        }

        private byte[] GenerateTexture()
        {
            var rowPitch = TextureWidth * TexturePixelSizeInBytes;
            var cellPitch = rowPitch >> 3;
            var cellHeight = TextureWidth >> 3;
            var textureSize = rowPitch * TextureHeight;

            var data = new byte[textureSize];
            var rgb = new byte[3];
            Random.NextBytes(rgb);

            for (var n = 0; n < textureSize; n += TexturePixelSizeInBytes)
            {
                var  x = n % rowPitch;
                var  y = n / rowPitch;
                var  i = x / cellPitch;
                var  j = y / cellHeight;

                if (i % 2 == j % 2)
                {
                    data[n] = 0x00;
                    data[n + 1] = 0x00;
                    data[n + 2] = 0x00;
                    data[n + 3] = 0xff;
                }
                else
                {
                    data[n] = rgb[0];
                    data[n + 1] = rgb[1];
                    data[n + 2] = rgb[2];
                    data[n + 3] = 0xff;
                }
            }

            return data;
        }

        internal void Update()
        {
        }

        internal void Render()
        {
            PopulateCommandList();

            CommandQueue.ExecuteCommandList(CommandList);

            // GPU メモリの使用状況を取得します。
            QueryVideoMemoryInformation memoryInfo;
            Adapter.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local, out memoryInfo);

            // GPU メモリの使用状況をウィンドウタイトルに表示します。
            Form.Text = $"[ResourceType: {(UsePlacedResources ? "Placed" : "Commited")}] - Memory Used: {FormatMemorySize(memoryInfo.CurrentUsage)}";

            SwapChain.Present(1, PresentFlags.None);

            MoveToNextFrame();
        }

        private string FormatMemorySize(long usage)
        {
            var mb = 1 << 20;
            var kb = 1 << 10;

            if(usage > mb)
            {
                return $"{usage / mb:f1} MB";
            }
            else if(usage > kb)
            {
                return $"{usage / kb:f1} KB";
            }
            else
            {
                return $"{usage} B";
            }
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

            var rtvDescHandle = RenderTargetViewHeap.CPUDescriptorHandleForHeapStart;
            rtvDescHandle += FrameIndex * RtvDescriptorSize;

            CommandList.SetRenderTargets(rtvDescHandle, null);

            CommandList.ClearRenderTargetView(rtvDescHandle, new Color4(0.0f, 0.2f, 0.4f, 1.0f), 0, null);

            // 描画処理
            {
                CommandList.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleStrip;
                CommandList.SetVertexBuffer(0, VertexBufferView);

                var srvHandle = CbvSrvUavViewHeap.GPUDescriptorHandleForHeapStart;
                for(var i = 0; i < TextureCount; i++)
                {
                    CommandList.SetGraphicsRootDescriptorTable(0, srvHandle);
                    CommandList.DrawInstanced(4, 1, i * 4, 0);

                    srvHandle += CbvSrvUavDescriptorSize;
                }
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