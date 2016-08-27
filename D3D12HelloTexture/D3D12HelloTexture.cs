using System;
using System.Threading;
using SharpDX.DXGI;

namespace D3D12HelloTexture
{
    using SharpDX;
    using SharpDX.Windows;
    using SharpDX.Direct3D12;
    using System.Runtime.InteropServices;

    internal class D3D12HelloTexture : IDisposable
    {
        private struct Vertex
        {
            public Vector3 Position;
            public Vector2 TexCoord;
        };

        private const int FrameCount = 2;

        private const int TextureWidth = 256;
        private const int TextureHeight = 256;
        private const int TexturePixelSize = 4;

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
        private DescriptorHeap ShaderResourceViewHeap;
        private Resource Texture;

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
            ShaderResourceViewHeap.Dispose();
            PipelineState.Dispose();
            CommandList.Dispose();
            VertexBuffer.Dispose();
            Texture.Dispose();
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

            var rtvDescHandle = RenderTargetViewHeap.CPUDescriptorHandleForHeapStart;
            for (var i = 0; i < FrameCount; i++)
            {
                RenderTargets[i] = SwapChain.GetBackBuffer<Resource>(i);
                Device.CreateRenderTargetView(RenderTargets[i], null, rtvDescHandle);
                rtvDescHandle += RtvDescriptorSize;
            }

            // シェーダリソースビューのためのデスクリプターヒープを作成
            var srvHeapDesc = new DescriptorHeapDescription()
            {
                DescriptorCount = 1,
                Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                Flags = DescriptorHeapFlags.ShaderVisible,
            };
            ShaderResourceViewHeap = Device.CreateDescriptorHeap(srvHeapDesc);


            CommandAllocator = Device.CreateCommandAllocator(CommandListType.Direct);
        }

        private void LoadAssets()
        {
            // テクスチャを扱うルートシグネチャを作成します。
            var rootSignatureDesc = new RootSignatureDescription(RootSignatureFlags.AllowInputAssemblerInputLayout,
                // Root Parameters
                new[]
                {
                    // ルートパラメータ 0:
                    // テクスチャレジスタ 0 のシェーダリソース（ピクセルシェーダからのみ参照）
                    new RootParameter(ShaderVisibility.Pixel,
                        new DescriptorRange()
                        {
                            RangeType = DescriptorRangeType.ShaderResourceView,
                            BaseShaderRegister = 0,
                            OffsetInDescriptorsFromTableStart = int.MinValue,
                            DescriptorCount = 1,
                        })
                },
                // Samplers
                new[]
                {
                    // サンプラーデスク 0:
                    // サンプラレジスタ 0 の静的サンプラ（ピクセルシェーダからのみ参照）
                    new StaticSamplerDescription(ShaderVisibility.Pixel, 0, 0)
                    {
                        Filter = Filter.MinimumMinMagMipPoint,
                        AddressUVW = TextureAddressMode.Border,
                    }
                });
            RootSignature = Device.CreateRootSignature(rootSignatureDesc.Serialize());

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

            // コマンドリストを作成。この後使うので、すぐには閉じない。
            CommandList = Device.CreateCommandList(CommandListType.Direct, CommandAllocator, PipelineState);

            // 頂点データを定義します。
            float aspectRatio = Viewport.Width / Viewport.Height;
            var triangleVertices = new[]
            {
                new Vertex { Position = new Vector3(0.0f, 0.25f * aspectRatio, 0.0f), TexCoord = new Vector2(0.5f, 0.0f) },
                new Vertex { Position = new Vector3(0.25f, -0.25f * aspectRatio, 0.0f), TexCoord = new Vector2(1.0f, 1.0f) },
                new Vertex { Position = new Vector3(-0.25f, -0.25f * aspectRatio, 0.0f), TexCoord = new Vector2(0.0f, 1.0f) },
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

            // テクスチャリソースを作成します。
            var textureDesc = ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, TextureWidth, TextureHeight);
            Texture = Device.CreateCommittedResource(
                new HeapProperties(HeapType.Default),
                HeapFlags.None,
                textureDesc,
                ResourceStates.CopyDestination
                );

            var uploadBufferSize = GetRequiredIntermediateSize(Texture, 0, 1);

            // テクスチャのバイナリデータ
            var textureData = GenerateTextureData();

            // アップロード用のテクスチャリソースを作成し、テクスチャリソースへコピーします。
#if true
            var textureUploadHeap = Device.CreateCommittedResource(
                new HeapProperties(CpuPageProperty.WriteBack, MemoryPool.L0),
                HeapFlags.None,
                textureDesc,
                ResourceStates.GenericRead
                );

            var handle = GCHandle.Alloc(textureData, GCHandleType.Pinned);
            var ptr = Marshal.UnsafeAddrOfPinnedArrayElement(textureData, 0);
            textureUploadHeap.WriteToSubresource(0, null, ptr, TexturePixelSize * TextureWidth, textureData.Length);
            handle.Free();

            CommandList.CopyTextureRegion(new TextureCopyLocation(Texture, 0), 0, 0, 0, new TextureCopyLocation(textureUploadHeap, 0), null);
#else
            var textureUploadHeap = Device.CreateCommittedResource(
                new HeapProperties(HeapType.Upload),
                HeapFlags.None,
                ResourceDescription.Buffer(uploadBufferSize),
                ResourceStates.GenericRead
                );

            var pTextureDataBegin = textureUploadHeap.Map(0);
            {
                Utilities.Write(pTextureDataBegin, textureData, 0, textureData.Length);
            }
            textureUploadHeap.Unmap(0);

            CommandList.CopyTextureRegion(
                new TextureCopyLocation(Texture, 0), 
                0, 0, 0, 
                new TextureCopyLocation(
                    textureUploadHeap, 
                    new PlacedSubResourceFootprint()
                    {
                        Footprint =
                        {
                            Format = textureDesc.Format,
                            Width = (int)textureDesc.Width,
                            Height = textureDesc.Height,
                            Depth = 1,
                            RowPitch = TextureWidth * TexturePixelSize,
                        },
                        Offset = 0
                    }),
                null
                );
#endif
            // コピー先テクスチャをシェーダリソースとして利用するためのリソースバリアを設定します。
            CommandList.ResourceBarrier(new ResourceTransitionBarrier(Texture, ResourceStates.CopyDestination, ResourceStates.PixelShaderResource));

            // シェーダリソースビューの作成
            var srvDesc = new ShaderResourceViewDescription
            {
                Shader4ComponentMapping = D3DXUtilities.DefaultComponentMapping(),
                Format = textureDesc.Format,
                Dimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = { MipLevels = 1 },
            };
            Device.CreateShaderResourceView(Texture, srvDesc, ShaderResourceViewHeap.CPUDescriptorHandleForHeapStart);

            // コマンドを閉じ、実行します。
            CommandList.Close();
            CommandQueue.ExecuteCommandList(CommandList);

            Fence = Device.CreateFence(0, FenceFlags.None);
            FenceValue = 1;

            FenceEvent = new AutoResetEvent(false);

            // コマンド完了を待ちます。
            WaitForPreviousFrame();

            // アップロード用のリソースを開放します。
            textureUploadHeap.Dispose();
        }

        private long GetRequiredIntermediateSize(Resource destiationResource, int firstSubresource, int numSubresources)
        {
            var desc = destiationResource.Description;

            long requiredSize;
            Device.GetCopyableFootprints(ref desc, firstSubresource, numSubresources, 0, null, null, null, out requiredSize);

            return requiredSize;
        }

        private byte[] GenerateTextureData()
        {
            const int rowPitch = TextureWidth * TexturePixelSize;
            const int cellPitch = rowPitch >> 3;
            const int cellHeight = TextureWidth >> 3;
            const int textureSize = rowPitch * TextureHeight;

            var data = new byte[textureSize];

            for (int n = 0; n < textureSize; n += TexturePixelSize)
            {
                int x = n % rowPitch;
                int y = n / rowPitch;
                int i = x / cellPitch;
                int j = y / cellHeight;

                if (i % 2 == j % 2)
                {
                    data[n] = 0x00;        // R
                    data[n + 1] = 0x00;    // G
                    data[n + 2] = 0x00;    // B
                    data[n + 3] = 0xff;    // A
                }
                else
                {
                    data[n] = 0xff;        // R
                    data[n + 1] = 0xff;    // G
                    data[n + 2] = 0xff;    // B
                    data[n + 3] = 0xff;    // A
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

            WaitForPreviousFrame();
        }

        private void PopulateCommandList()
        {
            CommandAllocator.Reset();

            CommandList.Reset(CommandAllocator, PipelineState);

            CommandList.SetGraphicsRootSignature(RootSignature);

            // 使用するデスクリプタヒープを設定します。
            CommandList.SetDescriptorHeaps(1, new[] { ShaderResourceViewHeap });

            // ルートパラメータ 0 に対応するシェーダリソースビューを設定します。
            CommandList.SetGraphicsRootDescriptorTable(0, ShaderResourceViewHeap.GPUDescriptorHandleForHeapStart);
            CommandList.SetViewport(Viewport);
            CommandList.SetScissorRectangles(ScissorRect);

            CommandList.ResourceBarrierTransition(RenderTargets[FrameIndex], ResourceStates.Present, ResourceStates.RenderTarget);

            var rtvDescHandle = RenderTargetViewHeap.CPUDescriptorHandleForHeapStart;
            rtvDescHandle += FrameIndex * RtvDescriptorSize;

            CommandList.SetRenderTargets(rtvDescHandle, null);

            CommandList.ClearRenderTargetView(rtvDescHandle, new Color4(0.0f, 0.2f, 0.4f, 1.0f), 0, null);

            CommandList.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleList;
            CommandList.SetVertexBuffer(0, VertexBufferView);
            CommandList.DrawInstanced(3, 1, 0, 0);

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
        }
    }
}