using System;
using System.Threading;
using SharpDX.DXGI;

namespace D3D12DynamicIndexing
{
    using SharpDX;
    using SharpDX.Windows;
    using SharpDX.Direct3D12;
    using System.IO;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using System.Diagnostics;

    internal class D3D12DynamicIndexing : IDisposable
    {
        private const int FrameCount = 2;
        private const int CityRowCount = 15;
        private const int CityColumnCount = 8;
        private const int CityMaterialCount = CityRowCount * CityColumnCount;
        private const int CityMaterialTextureWidth = 64;
        private const int CityMaterialTextureHeight = 64;
        private const int CityMaterialTextureChannelCount = 4;
        private const float CitySpacingInterval = 16.0f;

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
        private DescriptorHeap SamplerHeap;
        private int SamplerDescriptorSize;
        private Resource IndexBuffer;
        private IndexBufferView IndexBufferView;
        private Resource[] CityMaterialTextures = new Resource[CityMaterialCount];
        private Resource CityDiffuseTexture;
        private Resource DepthStencil;
        private List<FrameResource> FrameResources = new List<FrameResource>();
        private int IndicesCount;
        private FrameResource CurrentFrameResource;
        private SimpleCamera Camera = new SimpleCamera();
        private Stopwatch Stopwatch = Stopwatch.StartNew();

        public void Dispose()
        {
            WaitForGpu();

            foreach (var target in RenderTargets)
            {
                target.Dispose();
            }

            foreach (var resource in CityMaterialTextures)
            {
                resource.Dispose();
            }

            foreach (var resource in FrameResources)
            {
                resource.Dispose();
            }

            CommandAllocator.Dispose();
            CommandQueue.Dispose();
            RootSignature.Dispose();
            RenderTargetViewHeap.Dispose();
            DepthStencilViewHeap.Dispose();
            CbvSrvUavViewHeap.Dispose();
            SamplerHeap.Dispose();
            DepthStencil.Dispose();
            PipelineState.Dispose();
            CommandList.Dispose();
            VertexBuffer.Dispose();
            IndexBuffer.Dispose();
            CityDiffuseTexture.Dispose();
            Fence.Dispose();
            SwapChain.Dispose();
            Device.Dispose();
        }

        internal void Initialize(RenderForm form)
        {
            Camera.Initialize(new Vector3((CityColumnCount / 2.0f) * CitySpacingInterval - (CitySpacingInterval / 2.0f), 15, 50));
            Camera.MoveSpeed = CitySpacingInterval * 2.0f;

            SetupKeyHandler(form);
            LoadPipeline(form);
            LoadAssets();
        }

        private void SetupKeyHandler(RenderForm form)
        {
            form.KeyDown += Camera.OnKeyDown;
            form.KeyUp += Camera.OnKeyUp;
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
                // レンダーターゲットビューヒープ
                var rtvHeapDesc = new DescriptorHeapDescription()
                {
                    DescriptorCount = FrameCount,
                    Type = DescriptorHeapType.RenderTargetView,
                    Flags = DescriptorHeapFlags.None,
                };
                RenderTargetViewHeap = Device.CreateDescriptorHeap(rtvHeapDesc);
                RtvDescriptorSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);

                // フレームバッファのレンダーターゲットビューを作成
                var rtvDescHandle = RenderTargetViewHeap.CPUDescriptorHandleForHeapStart;
                for (var i = 0; i < FrameCount; i++)
                {
                    RenderTargets[i] = SwapChain.GetBackBuffer<Resource>(i);
                    Device.CreateRenderTargetView(RenderTargets[i], null, rtvDescHandle);
                    rtvDescHandle += RtvDescriptorSize;
                }

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
                    DescriptorCount = FrameCount * CityRowCount * CityColumnCount + CityMaterialCount + 1,
                    Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                    Flags = DescriptorHeapFlags.ShaderVisible,
                };
                CbvSrvUavViewHeap = Device.CreateDescriptorHeap(cbvSrvUavHeapDesc);
                CbvSrvUavDescriptorSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

                // サンプラービューヒープ
                var samplerHeapDesc = new DescriptorHeapDescription()
                {
                    DescriptorCount = 1,
                    Type = DescriptorHeapType.Sampler,
                    Flags = DescriptorHeapFlags.ShaderVisible,
                };
                SamplerHeap = Device.CreateDescriptorHeap(samplerHeapDesc);
                SamplerDescriptorSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.Sampler);
            }

            CommandAllocator = Device.CreateCommandAllocator(CommandListType.Direct);
        }

        private void LoadAssets()
        {
            // ルートシグネチャの作成
            {
                var rootSignatureDesc = new RootSignatureDescription(RootSignatureFlags.AllowInputAssemblerInputLayout,
                    new []
                    {
                        new RootParameter(ShaderVisibility.Pixel,
                            new []
                            {
                                new DescriptorRange()
                                {
                                    RangeType = DescriptorRangeType.ShaderResourceView,
                                    BaseShaderRegister = 0,
                                    OffsetInDescriptorsFromTableStart = 0,
                                    DescriptorCount = 1 + CityMaterialCount,
                                },
                            }),
                        new RootParameter(ShaderVisibility.Pixel,
                            new []
                            {
                                new DescriptorRange()
                                {
                                    RangeType = DescriptorRangeType.Sampler,
                                    BaseShaderRegister = 0,
                                    OffsetInDescriptorsFromTableStart = 0,
                                    DescriptorCount = 1,
                                },
                            }),
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
                        new RootParameter(ShaderVisibility.Pixel,
                            new RootConstants()
                            {
                                Value32BitCount = 1,
                                ShaderRegister = 0,
                            }),
                    });
                RootSignature = Device.CreateRootSignature(rootSignatureDesc.Serialize());
            }

            // パイプラインステートオブジェクトの作成
            {
                var vertexShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.FromFile("Shaders.vs.cso"));
                var pixelShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.FromFile("Shaders.ps.cso"));

                var rasterizerState = RasterizerStateDescription.Default();
                rasterizerState.CullMode = CullMode.None;

                var psoDesc = new GraphicsPipelineStateDescription()
                {
                    InputLayout = new InputLayoutDescription(SampleAssets.StandardVertexDescription),
                    RootSignature = RootSignature,
                    VertexShader = vertexShader,
                    PixelShader = pixelShader,
                    RasterizerState = rasterizerState,
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
            }

            CommandList = Device.CreateCommandList(CommandListType.Direct, CommandAllocator, PipelineState);

            // 使用したリソースを記録するリスト
            var resources = new List<Resource>();

            // メッシュデータ
            var meshData = File.ReadAllBytes(SampleAssets.DataFileName);

            // 頂点バッファの作成
            {

                // 頂点バッファ
                VertexBuffer = Device.CreateCommittedResource(
                    new HeapProperties(HeapType.Default),
                    HeapFlags.None,
                    ResourceDescription.Buffer(SampleAssets.VertexDataSize),
                    ResourceStates.CopyDestination
                    );

                // 頂点バッファへデータをアップロードするための一時リソース
                var vertexBufferUpload = Device.CreateCommittedResource(
                    new HeapProperties(HeapType.Upload),
                    HeapFlags.None,
                    ResourceDescription.Buffer(SampleAssets.VertexDataSize),
                    ResourceStates.GenericRead
                    );
                resources.Add(vertexBufferUpload);

                // 頂点データを一時リソースへ書き込み
                var pDataBegin = vertexBufferUpload.Map(0);
                {
                    Utilities.Write(pDataBegin, meshData, SampleAssets.VertexDataOffset, SampleAssets.VertexDataSize);
                }
                vertexBufferUpload.Unmap(0);

                // 一時リソースから頂点バッファへコピーするコマンドを積み込み
                CommandList.CopyBufferRegion(VertexBuffer, 0, vertexBufferUpload, 0, SampleAssets.VertexDataSize);

                // 頂点シェーダから参照される状態へ、頂点バッファの状態を遷移
                CommandList.ResourceBarrier(new ResourceTransitionBarrier(VertexBuffer, ResourceStates.CopyDestination, ResourceStates.VertexAndConstantBuffer));

                VertexBufferView = new VertexBufferView()
                {
                    BufferLocation = VertexBuffer.GPUVirtualAddress,
                    StrideInBytes = SampleAssets.StandardVertexStride,
                    SizeInBytes = SampleAssets.VertexDataSize,
                };
            }

            // インデックスデータ
            {
                // インデックスバッファ
                IndexBuffer = Device.CreateCommittedResource(
                    new HeapProperties(HeapType.Default),
                    HeapFlags.None,
                    ResourceDescription.Buffer(SampleAssets.IndexDataSize),
                    ResourceStates.CopyDestination
                    );

                // インデックスバッファへデータをアップロードするための一時リソース
                var indexBufferUpload = Device.CreateCommittedResource(
                    new HeapProperties(HeapType.Upload),
                    HeapFlags.None,
                    ResourceDescription.Buffer(SampleAssets.IndexDataSize),
                    ResourceStates.GenericRead
                    );
                resources.Add(indexBufferUpload);

                // インデックスデータを一時リソースへ書き込み
                var pDataBegin = indexBufferUpload.Map(0);
                {
                    Utilities.Write(pDataBegin, meshData, SampleAssets.IndexDataOffset, SampleAssets.IndexDataSize);
                }
                indexBufferUpload.Unmap(0);

                // 一時リソースからインデックスバッファへコピーするコマンドを積み込み
                CommandList.CopyBufferRegion(IndexBuffer, 0, indexBufferUpload, 0, SampleAssets.IndexDataSize);

                // インデックスシェーダから参照される状態へ、インデックスバッファの状態を遷移
                CommandList.ResourceBarrier(new ResourceTransitionBarrier(IndexBuffer, ResourceStates.CopyDestination, ResourceStates.IndexBuffer));

                IndexBufferView = new IndexBufferView()
                {
                    BufferLocation = IndexBuffer.GPUVirtualAddress,
                    Format = SampleAssets.StandardIndexFormat,
                    SizeInBytes = SampleAssets.IndexDataSize,
                };

                IndicesCount = SampleAssets.IndexDataSize / 4;
            }

            // テクスチャとサンプラの作成
            {
                // シティマテリアル用のテクスチャ配列の作成
                {
                    var textureDesc = new ResourceDescription()
                    {
                        MipLevels = 1,
                        Format = Format.R8G8B8A8_UNorm,
                        Width = CityMaterialTextureWidth,
                        Height = CityMaterialTextureHeight,
                        Flags = ResourceFlags.None,
                        DepthOrArraySize = 1,
                        SampleDescription =
                        {
                            Count = 1,
                            Quality = 0,
                        },
                        Dimension = ResourceDimension.Texture2D,
                    };

                    var materialGradStep = 1.0f / CityMaterialCount;

                    var cityTextureDataList = new List<byte[]>();

                    // テクスチャリソースとテクスチャデータの作成
                    for (var i = 0; i < CityMaterialCount; i++)
                    {
                        CityMaterialTextures[i] = Device.CreateCommittedResource(
                            new HeapProperties(HeapType.Default),
                            HeapFlags.None,
                            textureDesc,
                            ResourceStates.CopyDestination
                            );

                        var t = i * materialGradStep;

                        var cityTextureData = new byte[CityMaterialTextureWidth * CityMaterialTextureHeight * CityMaterialTextureChannelCount];

                        for (var x = 0; x < CityMaterialTextureWidth; x++)
                        {
                            for (var y = 0; y < CityMaterialTextureHeight; y++)
                            {
                                var pixelIndex = (y * CityMaterialTextureChannelCount * CityMaterialTextureWidth) + (x * CityMaterialTextureChannelCount);

						        var tPrime = t + ((1.0f * y / CityMaterialTextureHeight) * materialGradStep);

                                var hsl = new ColorMine.ColorSpaces.Hsl()
                                {
                                    H = tPrime * 360,
                                    S = 0.5f * 100,
                                    L = 0.5f * 100,
                                };
                                var rgb = hsl.ToRgb();

                                cityTextureData[pixelIndex + 0] = (byte)rgb.R;
                                cityTextureData[pixelIndex + 1] = (byte)rgb.G;
                                cityTextureData[pixelIndex + 2] = (byte)rgb.B;
                                cityTextureData[pixelIndex + 3] = 255;
                            }
                        }

                        cityTextureDataList.Add(cityTextureData);
                    }

                    // テクスチャデータをテクスチャバッファにアップロードします。
                    {
                        var subresourceCount = textureDesc.DepthOrArraySize * textureDesc.MipLevels;
                        var uploadBufferStep = GetRequiredIntermediateSize(CityMaterialTextures[0], 0, subresourceCount);
                        var uploadBufferSize = uploadBufferStep * CityMaterialCount;

                        var materialsUploadHeap = Device.CreateCommittedResource(
                            new HeapProperties(HeapType.Upload),
                            HeapFlags.None,
                            ResourceDescription.Buffer(uploadBufferSize),
                            ResourceStates.GenericRead
                            );
                        resources.Add(materialsUploadHeap);

                        var currentUploadPtr = materialsUploadHeap.Map(0);
                        {
                            for (var i = 0; i < CityMaterialCount; i++)
                            {
                                // テクスチャデータをアップロード用リソースに書き込み
                                Utilities.Write(currentUploadPtr, cityTextureDataList[i], 0, cityTextureDataList[i].Length);
                                currentUploadPtr = IntPtr.Add(currentUploadPtr, (int)uploadBufferStep);

                                // アップロード用リソースからテクスチャバッファへコピー
                                CommandList.CopyTextureRegion(
                                    new TextureCopyLocation(CityMaterialTextures[i], 0),
                                    0, 0, 0,
                                    new TextureCopyLocation(
                                        materialsUploadHeap,
                                        new PlacedSubResourceFootprint()
                                        {
                                            Footprint =
                                            {
                                            Format = textureDesc.Format,
                                            Width = (int)textureDesc.Width,
                                            Height = textureDesc.Height,
                                            Depth = 1,
                                            RowPitch = CityMaterialTextureWidth * CityMaterialTextureChannelCount,
                                            },
                                            Offset = i * uploadBufferStep,
                                        }),
                                    null
                                    );

                                CommandList.ResourceBarrier(new ResourceTransitionBarrier(CityMaterialTextures[i], ResourceStates.CopyDestination, ResourceStates.PixelShaderResource));
                            }
                        }
                        materialsUploadHeap.Unmap(0);
                    }
                }

                // ディフューズテクスチャの作成
                {
                    var textureDesc = new ResourceDescription()
                    {
                        MipLevels = SampleAssets.Textures[0].MipLevels,
                        Format = SampleAssets.Textures[0].Format,
                        Width = SampleAssets.Textures[0].Width,
                        Height = SampleAssets.Textures[0].Height,
                        Flags = ResourceFlags.None,
                        DepthOrArraySize = 1,
                        SampleDescription =
                        {
                            Count = 1,
                            Quality = 0,
                        },
                        Dimension = ResourceDimension.Texture2D,
                    };

                    CityDiffuseTexture = Device.CreateCommittedResource(
                        new HeapProperties(HeapType.Default),
                        HeapFlags.None,
                        textureDesc,
                        ResourceStates.CopyDestination
                        );

                    var subresourceCount = textureDesc.DepthOrArraySize * textureDesc.MipLevels;
                    var uploadBufferSize = GetRequiredIntermediateSize(CityDiffuseTexture, 0, subresourceCount);

                    var textureUploadHeap = Device.CreateCommittedResource(
                        new HeapProperties(CpuPageProperty.WriteBack, MemoryPool.L0),
                        HeapFlags.None,
                        textureDesc,
                        ResourceStates.GenericRead
                        );
                    resources.Add(textureUploadHeap);

                    var handle = GCHandle.Alloc(meshData, GCHandleType.Pinned);
                    var ptr = Marshal.UnsafeAddrOfPinnedArrayElement(meshData, 0);
                    ptr = IntPtr.Add(ptr, SampleAssets.Textures[0].Data[0].Offset);
                    {
                        textureUploadHeap.WriteToSubresource(0, null, ptr, SampleAssets.Textures[0].Data[0].Pitch, SampleAssets.Textures[0].Data[0].Size);
                    }
                    handle.Free();

                    CommandList.CopyTextureRegion(new TextureCopyLocation(CityDiffuseTexture, 0), 0, 0, 0, new TextureCopyLocation(textureUploadHeap, 0), null);

                    CommandList.ResourceBarrier(new ResourceTransitionBarrier(CityDiffuseTexture, ResourceStates.CopyDestination, ResourceStates.PixelShaderResource));
                }

                // サンプラの作成
                {
                    var samplerDesc = new SamplerStateDescription()
                    {
                        Filter = Filter.MinMagMipLinear,
                        AddressU = TextureAddressMode.Wrap,
                        AddressV = TextureAddressMode.Wrap,
                        AddressW = TextureAddressMode.Wrap,
                        MinimumLod = 0,
                        MaximumLod = float.MaxValue,
                        MipLodBias = 0.0f,
                        MaximumAnisotropy = 1,
                        ComparisonFunction = Comparison.Always,
                    };

                    Device.CreateSampler(samplerDesc, SamplerHeap.CPUDescriptorHandleForHeapStart);
                }

                // SRV を作成
                {
                    var srvHandle = CbvSrvUavViewHeap.CPUDescriptorHandleForHeapStart;

                    // ディフューズテクスチャの SRV 作成
                    var srvDesc = new ShaderResourceViewDescription()
                    {
                        Shader4ComponentMapping = D3DXUtilities.DefaultComponentMapping(),
                        Format = SampleAssets.Textures[0].Format,
                        Dimension = ShaderResourceViewDimension.Texture2D,
                        Texture2D = { MipLevels = 1 },
                    };
                    Device.CreateShaderResourceView(CityDiffuseTexture, srvDesc, srvHandle);
                    srvHandle += CbvSrvUavDescriptorSize;

                    // マテリアルテクスチャの SRV 作成
                    for(var i = 0; i < CityMaterialCount; i++)
                    {
                        var materialSrvDesc = new ShaderResourceViewDescription()
                        {
                            Shader4ComponentMapping = D3DXUtilities.DefaultComponentMapping(),
                            Format = Format.R8G8B8A8_UNorm,
                            Dimension = ShaderResourceViewDimension.Texture2D,
                            Texture2D = { MipLevels = 1 },
                        };
                        Device.CreateShaderResourceView(CityMaterialTextures[i], materialSrvDesc, srvHandle);
                        srvHandle += CbvSrvUavDescriptorSize;
                    }
                }
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

            // コマンドを実行します。
            CommandList.Close();
            CommandQueue.ExecuteCommandList(CommandList);

            // 全フレームで固定のリソースを作成
            CreateFrameResource();

            // 同期オブジェクトの初期化とコマンドの実行完了を待ちます。
            {
                Fence = Device.CreateFence(FenceValues[FrameIndex], FenceFlags.None);
                FenceValues[FrameIndex]++;

                FenceEvent = new AutoResetEvent(false);

                // コマンドの実行完了を待ちます。
                WaitForGpu();
            }

            // リソースを解放
            foreach (var resource in resources)
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

        /// <summary>
        /// フレーム毎に必要なリソースを作成
        /// </summary>
        private void CreateFrameResource()
        {
            CommandList.Reset(CommandAllocator, PipelineState);

            var cbvSrvHandle = CbvSrvUavViewHeap.CPUDescriptorHandleForHeapStart;
            cbvSrvHandle += (1 + CityMaterialCount) * CbvSrvUavDescriptorSize;

            for(var i = 0; i < FrameCount; i++)
            {
                var frameResource = new FrameResource(Device, CityRowCount, CityColumnCount, CityMaterialCount, CitySpacingInterval);

                var cbOffset = 0;
                for(var j = 0; j < CityRowCount; j++)
                {
                    for(var k = 0; k < CityColumnCount; k++)
                    {
                        var cbvDesc = new ConstantBufferViewDescription()
                        {
                            BufferLocation = frameResource.ConstantBufferUpload.GPUVirtualAddress + cbOffset,
                            SizeInBytes = Utilities.SizeOf<FrameResource.ConstantBufferDataStruct>(),
                        };
                        Device.CreateConstantBufferView(cbvDesc, cbvSrvHandle);

                        cbvSrvHandle += CbvSrvUavDescriptorSize;
                        cbOffset += cbvDesc.SizeInBytes;
                    }
                }

                frameResource.InitBundle(Device, PipelineState, i, IndicesCount, IndexBufferView,
                    VertexBufferView, CbvSrvUavViewHeap, CbvSrvUavDescriptorSize, SamplerHeap, RootSignature);

                FrameResources.Add(frameResource);
            }

            CommandList.Close();
            CommandQueue.ExecuteCommandList(CommandList);
        }

        internal void Update()
        {
            var elapsedTime = Stopwatch.Elapsed;
            Stopwatch.Restart();

            CurrentFrameResource = FrameResources[FrameIndex];

            Camera.Update(elapsedTime);

            // 定数バッファを更新
            CurrentFrameResource.UpdateConstantBuffers(Camera.GetViewMatrix(), Camera.GetProjectionMatrix(0.8f, 1.0f * Width / Height));
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
            CurrentFrameResource.CommandAllocator.Reset();

            CommandList.Reset(CurrentFrameResource.CommandAllocator, PipelineState);

            CommandList.SetGraphicsRootSignature(RootSignature);

            CommandList.SetDescriptorHeaps(2, new[] { CbvSrvUavViewHeap, SamplerHeap });

            CommandList.SetViewport(Viewport);
            CommandList.SetScissorRectangles(ScissorRect);

            CommandList.ResourceBarrierTransition(RenderTargets[FrameIndex], ResourceStates.Present, ResourceStates.RenderTarget);

            var rtvDescHandle = RenderTargetViewHeap.CPUDescriptorHandleForHeapStart;
            rtvDescHandle += FrameIndex * RtvDescriptorSize;
            var dsvDescHandle = DepthStencilViewHeap.CPUDescriptorHandleForHeapStart;

            CommandList.SetRenderTargets(rtvDescHandle, dsvDescHandle);

            CommandList.ClearRenderTargetView(rtvDescHandle, new Color4(0.0f, 0.2f, 0.4f, 1.0f), 0, null);
            CommandList.ClearDepthStencilView(dsvDescHandle, ClearFlags.FlagsDepth, 1.0f, 0);

            // バンドルに積まれたコマンドを描画
            CommandList.ExecuteBundle(CurrentFrameResource.Bundle);

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