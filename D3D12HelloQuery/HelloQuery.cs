using System;
using System.Threading;
using SharpDX.DXGI;

namespace D3D12HelloQuery
{
    using SharpDX;
    using SharpDX.Windows;
    using SharpDX.Direct3D12;

    internal class HelloQuery : IDisposable
    {
        private struct Vertex
        {
            public Vector3 Position;
            public Vector4 Color;
        };

        private const int FrameCount = 2;

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
            PipelineState.Dispose();
            CommandList.Dispose();
            VertexBuffer.Dispose();
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

            CommandAllocator = Device.CreateCommandAllocator(CommandListType.Direct);
        }

        private void LoadAssets()
        {
            // 空のルートシグネチャを作成します。
            var rootSignatureDesc = new RootSignatureDescription(RootSignatureFlags.AllowInputAssemblerInputLayout);
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
            float aspectRatio = Viewport.Width / Viewport.Height;
            var triangleVertices = new[]
            {
                new Vertex { Position = new Vector3(0.0f, 0.25f * aspectRatio, 0.0f), Color = new Vector4(1.0f, 0.0f, 0.0f, 0.0f) },
                new Vertex { Position = new Vector3(0.25f, -0.25f * aspectRatio, 0.0f), Color = new Vector4(0.0f, 1.0f, 0.0f, 0.0f) },
                new Vertex { Position = new Vector3(-0.25f, -0.25f * aspectRatio, 0.0f), Color = new Vector4(0.0f, 0.0f, 1.0f, 0.0f) },
            };
            var vertexBufferSize = Utilities.SizeOf(triangleVertices);

            // 頂点バッファを作成します。
            //
            // 注意：頂点バッファの様はスタティックなデータを配置するためにアップロードヒープを使うのは適しません。
            // 正しくは、アップロードヒープにおいた頂点データを HeapType.Default のバッファにコピーするなどしてください。
            // ここでは、簡略化のためにアップロードヒープをそのまま使います。
            VertexBuffer = Device.CreateCommittedResource(
                new HeapProperties(HeapType.Upload), 
                HeapFlags.None, 
                ResourceDescription.Buffer(vertexBufferSize), 
                ResourceStates.GenericRead
                );

            // 頂点データを頂点バッファに書き込みます。
            var pVertexDataBegin = VertexBuffer.Map(0);
            {
                Utilities.Write(pVertexDataBegin, triangleVertices, 0, triangleVertices.Length);
            }
            VertexBuffer.Unmap(0);

            // 頂点バッファビューを作成します。
            VertexBufferView = new VertexBufferView()
            {
                BufferLocation = VertexBuffer.GPUVirtualAddress,
                StrideInBytes = Utilities.SizeOf<Vertex>(),
                SizeInBytes = vertexBufferSize,
            };

            Fence = Device.CreateFence(0, FenceFlags.None);
            FenceValue = 1;

            FenceEvent = new AutoResetEvent(false);
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

            // 必要な各種ステートを設定します。
            CommandList.SetGraphicsRootSignature(RootSignature);
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