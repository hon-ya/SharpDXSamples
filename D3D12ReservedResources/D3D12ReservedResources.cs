using System;
using System.Threading;
using SharpDX.DXGI;

namespace D3D12ReservedResources
{
    using SharpDX;
    using SharpDX.Windows;
    using SharpDX.Direct3D12;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Runtime.InteropServices;
    using System.Windows.Forms;

    internal class D3D12ReservedResources : IDisposable
    {
        private struct Vertex
        {
            public Vector3 Position;
            public Vector2 UV;
        };

        private struct MipInfo
        {
            public int HeapIndex;
            public bool PackedMip;
            public bool Mapped;
            public TiledResourceCoordinate StartCoordinate;
            public TileRegionSize RegionSize;
        }

        private const int FrameCount = 2;
        private const int TextureWidth = 256;
        private const int TextureHeight = 256;
        private const int TexturePixelSizeInBytes = 4;
        private const int DefaultResourcePlacementAlignment = 65536;

        private short MipLevels;
        private int ActiveMip;
        private bool ActiveMipChanged;

        private Form Form;
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
        private DescriptorHeap CbvSrvUavHeap;
        private int CbvSrvUavDescriptorSize;
        private MipInfo[] MipInfos;
        private Resource ReservedResource;
        private Resource TextureUploadHeap;
        private PackedMipInformation PackedMipInfo;
        private Heap[] TextureHeaps;

        public void Dispose()
        {
            WaitForGpu();

            foreach (var heap in TextureHeaps)
            {
                heap.Dispose();
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
            CbvSrvUavHeap.Dispose();
            PipelineState.Dispose();
            CommandList.Dispose();
            VertexBuffer.Dispose();
            ReservedResource.Dispose();
            TextureUploadHeap.Dispose();
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
            form.KeyDown += OnKeyDown; ;
        }

        private void OnKeyDown(object sender, System.Windows.Forms.KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case System.Windows.Forms.Keys.Up:
                    if(ActiveMip > 0)
                    {
                        ActiveMip--;
                        ActiveMipChanged = true;
                    }
                    break;
                case System.Windows.Forms.Keys.Down:
                    if(ActiveMip < MipLevels - 1)
                    {
                        ActiveMip++;
                        ActiveMipChanged = true;
                    }
                    break;
            }
        }

        private void LoadPipeline(RenderForm form)
        {
            Form = form;

            var width = form.ClientSize.Width;
            var height = form.ClientSize.Height;

            Viewport = new ViewportF(0, 0, width, height, 0.0f, 1.0f);
            ScissorRect = new Rectangle(0, 0, width, height);

            // テクスチャの高さと幅から Mip レベル数を計算
            MipLevels = 0;
            for(int w = TextureWidth, h = TextureHeight; w > 0 && h > 0; w >>= 1, h >>= 1)
            {
                MipLevels++;
            }
            ActiveMip = MipLevels - 1;
            MipInfos = new MipInfo[MipLevels];

#if DEBUG
            {
                DebugInterface.Get().EnableDebugLayer();
            }
#endif

            Device = new Device(null, SharpDX.Direct3D.FeatureLevel.Level_11_0);

            // タイルリソースをサポートしているかをチェックします。
            {
                var options = new FeatureDataD3D12Options();
                Device.CheckFeatureSupport(Feature.D3D12Options, ref options);
                Debug.Assert(options.TiledResourcesTier != TiledResourcesTier.TierNotSupported, "Device does not support tiled resources.");
            }

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
                    DescriptorCount = 1,
                    Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                    Flags = DescriptorHeapFlags.ShaderVisible,
                };
                CbvSrvUavHeap = Device.CreateDescriptorHeap(cbvSrvUavHeapDesc);
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
                var rootSignatureDesc = new RootSignatureDescription(RootSignatureFlags.AllowInputAssemblerInputLayout,
                    new[]
                    {
                        new RootParameter(ShaderVisibility.Pixel,
                            new RootConstants()
                            {
                                Value32BitCount = 1,
                                ShaderRegister = 0,
                            }),
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

            var uploadResources = new List<Resource>();

            {
                float aspectRatio = Viewport.Width / Viewport.Height;
                var quadVertices = new[]
                {
                    new Vertex { Position = new Vector3(-0.25f, -0.25f * aspectRatio, 0.0f), UV = new Vector2( 0.0f, 1.0f) },
                    new Vertex { Position = new Vector3(-0.25f, 0.25f * aspectRatio, 0.0f), UV = new Vector2(0.0f, 0.0f) },
                    new Vertex { Position = new Vector3(0.25f, -0.25f * aspectRatio, 0.0f), UV = new Vector2(1.0f, 1.0f) },
                    new Vertex { Position = new Vector3(0.25f, 0.25f * aspectRatio, 0.0f), UV = new Vector2(1.0f, 0.0f) },
                };
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

                var pVertexDataBegin = vertexBufferUpload.Map(0);
                {
                    Utilities.Write(pVertexDataBegin, quadVertices, 0, quadVertices.Length);
                }
                vertexBufferUpload.Unmap(0);

                CommandList.CopyBufferRegion(VertexBuffer, 0, vertexBufferUpload, 0, vertexBufferSize);
                CommandList.ResourceBarrierTransition(VertexBuffer, ResourceStates.CopyDestination, ResourceStates.VertexAndConstantBuffer);

                VertexBufferView = new VertexBufferView()
                {
                    BufferLocation = VertexBuffer.GPUVirtualAddress,
                    StrideInBytes = Utilities.SizeOf<Vertex>(),
                    SizeInBytes = vertexBufferSize,
                };
            }

            // ReservedTexture を作成します。
            {
                var reservedTextureDesc = new ResourceDescription()
                {
                    MipLevels = MipLevels,
                    Format = Format.R8G8B8A8_UNorm,
                    Width = TextureWidth,
                    Height = TextureHeight,
                    Flags = ResourceFlags.None,
                    DepthOrArraySize = 1,
                    SampleDescription =
                    {
                        Count = 1,
                        Quality = 0,
                    },
                    Dimension = ResourceDimension.Texture2D,
                    Layout = TextureLayout.UndefinedSwizzle64kb,
                };

                // ReservedResource の作成
                ReservedResource = Device.CreateReservedResource(
                    reservedTextureDesc,
                    ResourceStates.CopyDestination
                    );

                // ReservedResource を参照する SRV の作成
                var srvDesc = new ShaderResourceViewDescription()
                {
                    Shader4ComponentMapping = D3DXUtilities.DefaultComponentMapping(),
                    Format = reservedTextureDesc.Format,
                    Dimension = ShaderResourceViewDimension.Texture2D,
                    Texture2D = { MipLevels = MipLevels },
                };
                Device.CreateShaderResourceView(ReservedResource, srvDesc, CbvSrvUavHeap.CPUDescriptorHandleForHeapStart);

                var resourceSize = GetRequiredIntermediateSize(ReservedResource, 0, 1);

                // テクスチャデータのアップロード用ヒープの作成
                TextureUploadHeap = Device.CreateCommittedResource(
                    new HeapProperties(HeapType.Upload),
                    HeapFlags.None,
                    ResourceDescription.Buffer(resourceSize),
                    ResourceStates.GenericRead
                    );

                // タイル情報の取得
                var tileCount = 0;
                var tileShape = new TileShape();
                var subresourceCount = (int)MipLevels;
                var tilings = new SubResourceTiling[subresourceCount];

                var subresourceCountPtr = Utilities.AllocateMemory(Utilities.SizeOf<short>());
                {
                    Utilities.Write(subresourceCountPtr, ref subresourceCount);
                    Device.GetResourceTiling(ReservedResource, out tileCount, out PackedMipInfo, out tileShape, subresourceCountPtr, 0, tilings);
                }
                Utilities.FreeMemory(subresourceCountPtr);

                // Packed でない Mip には個別のヒープを、Packed な Mip はまとめて 1 つのヒープを割り当てます。
                var heapCount = PackedMipInfo.StandardMipCount + (PackedMipInfo.PackedMipCount > 0 ? 1 : 0);
                for(var i = 0; i < MipLevels; i++)
                {
                    if(i < PackedMipInfo.StandardMipCount)
                    {
                        // StandardMipCount 以下の Mip は Unpacked な Mip です。
                        MipInfos[i] = new MipInfo()
                        {
                            HeapIndex = i,
                            PackedMip = false,
                            Mapped = false,
                            StartCoordinate = new TiledResourceCoordinate()
                            {
                                Subresource = i,
                            },
                            RegionSize =
                            {
                                Width = tilings[i].WidthInTiles,
                                Height = tilings[i].HeightInTiles,
                                Depth = tilings[i].DepthInTiles,
                                TileCount = tilings[i].WidthInTiles * tilings[i].HeightInTiles * tilings[i].DepthInTiles,
                                UseBox = true,
                            },
                        };
                    }
                    else
                    {
                        // それ以外は Packed な Mip です。
                        // Packed な Mip はひとつのヒープに収めるため、すべて同じヒープインデックスです。
                        // また、同じヒープをマップするため、その他のパラメータも同じです。
                        MipInfos[i] = new MipInfo()
                        {
                            HeapIndex = heapCount - 1,
                            PackedMip = true,
                            Mapped = false,
                            StartCoordinate = new TiledResourceCoordinate()
                            {
                                Subresource = heapCount - 1,
                            },
                            RegionSize = new TileRegionSize()
                            {
                                TileCount = PackedMipInfo.TilesForPackedMipCount,
                                UseBox = false,
                            },
                        };
                    }
                }

                // ヒープを作成します。
                TextureHeaps = new Heap[heapCount];
                for(var i = 0; i < heapCount; i++)
                {
                    var heapDesc = new HeapDescription()
                    {
                        SizeInBytes = MipInfos[i].RegionSize.TileCount * DefaultResourcePlacementAlignment,
                        Properties = new HeapProperties(HeapType.Default),
                        Alignment = 0,
                        Flags = HeapFlags.DenyBuffers | HeapFlags.DenyRtDomainShaderTextureS,
                    };
                    TextureHeaps[i] = Device.CreateHeap(heapDesc);
                }

                // タイルマッピングを更新します。
                UpdateTileMapping();

                CommandList.ResourceBarrierTransition(ReservedResource, ResourceStates.CopyDestination, ResourceStates.PixelShaderResource);
            }

            CommandList.Close();
            CommandQueue.ExecuteCommandList(CommandList);

            {
                Fence = Device.CreateFence(FenceValues[FrameIndex], FenceFlags.None);
                FenceValues[FrameIndex]++;

                FenceEvent = new AutoResetEvent(false);

                WaitForGpu();
            }

            foreach (var resource in uploadResources)
            {
                resource.Dispose();
            }
        }

        private long GetRequiredIntermediateSize(Resource destiationResource, int firstSubresource, int numSubresources)
        {
            var desc = destiationResource.Description;

            long requiredSize;
            Device.GetCopyableFootprints(ref desc, firstSubresource, numSubresources, 0, null, null, null, out requiredSize);

            return requiredSize;
        }

        private void UpdateTileMapping()
        {
            var firstSubresource = MipInfos[ActiveMip].HeapIndex;
            var subresourceCount = MipInfos[ActiveMip].PackedMip ? PackedMipInfo.PackedMipCount : 1;

            if(!MipInfos[firstSubresource].Mapped)
            {
                // ActiveMip の変更が行われ、その結果、対象の Mip がある Heap を変更する必要があるとき、
                // ReservedResource がマップするヒープの切り替えを行います。

                // テクスチャデータの生成
                var texture = GenerateTextureData(firstSubresource, subresourceCount);

                // ReservedResource がマップするヒープの切り替え
                {
                    var updateRegions = 0;
                    var startCoordinates = new List<TiledResourceCoordinate>();
                    var regionSizes = new List<TileRegionSize>();
                    var rangeFlags = new List<TileRangeFlags>();
                    var heapRangeStartOffsets = new List<int>();
                    var rangeTileCounts = new List<int>();

                    for(var i = 0; i < TextureHeaps.Length; i++)
                    {
                        // マップされてなく、アクティブでもないヒープに対しては何もしない
                        if(!MipInfos[i].Mapped && i != firstSubresource)
                        {
                            continue;
                        }

                        startCoordinates.Add(MipInfos[i].StartCoordinate);
                        regionSizes.Add(MipInfos[i].RegionSize);

                        if(i == firstSubresource)
                        {
                            // アクティブな Mip はマップ状態に
                            rangeFlags.Add(TileRangeFlags.None);
                            MipInfos[i].Mapped = true;
                        }
                        else // MipInfos[i].Mapped == true
                        {
                            // マップしていたものはアンマップ状態に
                            rangeFlags.Add(TileRangeFlags.Null);
                            MipInfos[i].Mapped = false;
                        }

                        // このサンプルでは、タイルは常にヒープ先頭から始まります。
                        heapRangeStartOffsets.Add(0);
                        rangeTileCounts.Add(MipInfos[i].RegionSize.TileCount);

                        updateRegions++;
                    }

                    // マッピングを更新します。
                    // TileRangeFlags.Null 指定した Mip は外され、
                    // TileRangeFlags.None 指定した Mip はマップされます。
                    CommandQueue.UpdateTileMappings(
                        ReservedResource,
                        updateRegions,
                        startCoordinates.ToArray(),
                        regionSizes.ToArray(),
                        TextureHeaps[firstSubresource],
                        updateRegions,
                        rangeFlags.ToArray(),
                        heapRangeStartOffsets.ToArray(),
                        rangeTileCounts.ToArray(),
                        TileMappingFlags.None
                        );
                }

                // マップした領域へデータをアップロードします。
                {
                    // 一時リソース上にテクスチャデータを構築
                    var layouts = new PlacedSubResourceFootprint[subresourceCount];
                    {
                        // テクスチャのレイアウト情報を取得
                        var desc = ReservedResource.Description;
                        var numRows = new int[subresourceCount];
                        var rowSizesInBytes = new long[subresourceCount];
                        long totalBytes;
                        Device.GetCopyableFootprints(ref desc, firstSubresource, subresourceCount, 0, layouts, numRows, rowSizesInBytes, out totalBytes);

                        var textureUploadHeapPtr = TextureUploadHeap.Map(0);
                        {
                            // 各 Mip のデータを構築
                            var mipOffset = 0;
                            for (var i = 0; i < subresourceCount; i++)
                            {
                                var currentPtr = IntPtr.Add(textureUploadHeapPtr, (int)layouts[i].Offset);
                                for (var row = 0; row < numRows[i]; row++)
                                {
                                    Utilities.Write(currentPtr, texture, mipOffset + row * (int)rowSizesInBytes[i], (int)rowSizesInBytes[i]);

                                    currentPtr = IntPtr.Add(currentPtr, layouts[i].Footprint.RowPitch);
                                }

                                mipOffset += layouts[i].Footprint.Width * layouts[i].Footprint.Height * TexturePixelSizeInBytes;
                            }
                        }
                        TextureUploadHeap.Unmap(0);
                    }

                    for (var i = 0; i < subresourceCount; i++)
                    {
                        // 一時リソースから目的の領域へデータをコピー
                        CommandList.CopyTextureRegion(
                        new TextureCopyLocation(ReservedResource, firstSubresource + i),
                        0, 0, 0,
                        new TextureCopyLocation(TextureUploadHeap, layouts[i]),
                        null
                        );
                    }
                }
            }

            // テキストの変更
            Form.Text = $"Mip Level: {ActiveMip}";

            ActiveMipChanged = false;
        }

        private byte[] GenerateTextureData(int firstMip, int mipCount)
        {
            var dataSize = (TextureWidth >> firstMip) * (TextureHeight >> firstMip) * TexturePixelSizeInBytes;
            if(mipCount > 1)
            {
                dataSize *= 2;
            }
            var data = new byte[dataSize];

            var index = 0;
            for (var n = 0; n < mipCount; n++)
            {
                int currentMip = firstMip + n;
                int width = TextureWidth >> currentMip;
                int height = TextureHeight >> currentMip;
                int rowPitch = width * TexturePixelSizeInBytes;
                int cellPitch = Math.Max(rowPitch >> 3, TexturePixelSizeInBytes);
                int cellHeight = Math.Max(height >> 3, 1);
                int textureSize = rowPitch * height;

                for (int m = 0; m < textureSize; m += TexturePixelSizeInBytes)
                {
                    int x = m % rowPitch;
                    int y = m / rowPitch;
                    int i = x / cellPitch;
                    int j = y / cellHeight;

                    if (i % 2 == j % 2)
                    {
                        data[index++] = 0xff;    // R
                        data[index++] = 0x00;    // G
                        data[index++] = 0x00;    // B
                        data[index++] = 0xff;    // A
                    }
                    else
                    {
                        data[index++] = 0xff;    // R
                        data[index++] = 0xff;    // G
                        data[index++] = 0xff;    // B
                        data[index++] = 0xff;    // A
                    }
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

            SwapChain.Present(1, PresentFlags.None);

            MoveToNextFrame();
        }

        private void PopulateCommandList()
        {
            // フレームごとに、利用するコマンドアロケータを切り替えます。
            CommandAllocators[FrameIndex].Reset();
            CommandList.Reset(CommandAllocators[FrameIndex], PipelineState);

            if(ActiveMipChanged)
            {
                // Mip の変更が行われている時、マッピングの更新を行います。
                CommandList.ResourceBarrierTransition(ReservedResource, ResourceStates.PixelShaderResource, ResourceStates.CopyDestination);
                UpdateTileMapping();
                CommandList.ResourceBarrierTransition(ReservedResource, ResourceStates.CopyDestination, ResourceStates.PixelShaderResource);
            }

            CommandList.SetGraphicsRootSignature(RootSignature);

            CommandList.SetDescriptorHeaps(1, new[] { CbvSrvUavHeap });

            CommandList.SetGraphicsRoot32BitConstant(0, ActiveMip, 0);  // 参照する MipLevel を指定します。
            CommandList.SetGraphicsRootDescriptorTable(1, CbvSrvUavHeap.GPUDescriptorHandleForHeapStart);
            CommandList.SetViewport(Viewport);
            CommandList.SetScissorRectangles(ScissorRect);

            CommandList.ResourceBarrierTransition(RenderTargets[FrameIndex], ResourceStates.Present, ResourceStates.RenderTarget);

            var rtvDescHandle = RenderTargetViewHeap.CPUDescriptorHandleForHeapStart;
            rtvDescHandle += FrameIndex * RtvDescriptorSize;

            CommandList.SetRenderTargets(rtvDescHandle, null);

            CommandList.ClearRenderTargetView(rtvDescHandle, new Color4(0.0f, 0.2f, 0.4f, 1.0f), 0, null);

            CommandList.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleStrip;
            CommandList.SetVertexBuffer(0, VertexBufferView);
            CommandList.DrawInstanced(4, 1, 0, 0);

            CommandList.ResourceBarrierTransition(RenderTargets[FrameIndex], ResourceStates.RenderTarget, ResourceStates.Present);

            CommandList.Close();
        }

        /// <summary>
        /// 現在のフレームの処理の完了を待ちます。
        /// </summary>
        private void WaitForGpu()
        {
            // フェンスをシグナルし、即、待ちに入ります。
            CommandQueue.Signal(Fence, FenceValues[FrameIndex]);

            Fence.SetEventOnCompletion(FenceValues[FrameIndex], FenceEvent.SafeWaitHandle.DangerousGetHandle());
            FenceEvent.WaitOne();

            FenceValues[FrameIndex]++;
        }

        /// <summary>
        /// 次のフレームへ処理を移行します。
        /// </summary>
        private void MoveToNextFrame()
        {
            // フェンスをシグナルするコマンドを積み込みます。
            var currentFenceValue = FenceValues[FrameIndex];
            CommandQueue.Signal(Fence, currentFenceValue);

            // フレームインデックスを更新します。
            FrameIndex = SwapChain.CurrentBackBufferIndex;

            // 次のフレームで使うリソースの準備がまだ整っていない場合は、これを待ちます。
            if (Fence.CompletedValue < FenceValues[FrameIndex])
            {
                Fence.SetEventOnCompletion(FenceValues[FrameIndex], FenceEvent.SafeWaitHandle.DangerousGetHandle());
                FenceEvent.WaitOne();
            }

            // 次に使うフェンスの値を更新します。
            FenceValues[FrameIndex] = currentFenceValue + 1;
        }
    }
}