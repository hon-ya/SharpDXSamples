using System;
using System.Threading;
using SharpDX.DXGI;

namespace D3D12HelloFrameBuffering
{
    using SharpDX;
    using SharpDX.Windows;
    using SharpDX.Direct3D12;
    using System.Collections.Generic;

    internal class HelloFrameBuffering : IDisposable
    {
        struct Vertex
        {
            public Vector3 Position;
            public Vector4 Color;
        };

        public const int FrameCount = 2;

        public Device Device { get; set; }
        public CommandQueue CommandQueue { get; private set; }
        public SwapChain3 SwapChain { get; set; }
        public int FrameIndex { get; private set; }
        public DescriptorHeap RenderTargetViewHeap { get; set; }
        public int RtvDescriptorSize { get; set; }
        public Resource[] RenderTargets { get; set; } = new Resource[FrameCount];
        public CommandAllocator[] CommandAllocators { get; private set; } = new CommandAllocator[FrameCount];
        public GraphicsCommandList CommandList { get; private set; }
        public int[] FenceValues { get; private set; } = new int[FrameCount];
        public Fence Fence { get; private set; }
        public AutoResetEvent FenceEvent { get; private set; }
        public ViewportF Viewport { get; private set; }
        public Rectangle ScissorRect { get; private set; }
        public RootSignature RootSignature { get; private set; }
        public PipelineState PipelineState { get; private set; }
        public Resource VertexBuffer { get; private set; }
        public VertexBufferView VertexBufferView { get; private set; }

        public void Dispose()
        {
            this.WaitForGpu();

            foreach (var target in this.RenderTargets)
            {
                target.Dispose();
            }
            foreach(var allocator in this.CommandAllocators)
            {
                allocator.Dispose();
            }
            this.CommandQueue.Dispose();
            this.RootSignature.Dispose();
            this.RenderTargetViewHeap.Dispose();
            this.PipelineState.Dispose();
            this.CommandList.Dispose();
            this.VertexBuffer.Dispose();
            this.Fence.Dispose();
            this.SwapChain.Dispose();
            this.Device.Dispose();
        }

        internal void Initialize(RenderForm form)
        {
            this.LoadPipeline(form);
            this.LoadAssets();
        }

        private void LoadPipeline(RenderForm form)
        {
            var width = form.ClientSize.Width;
            var height = form.ClientSize.Height;

            this.Viewport = new ViewportF(0, 0, width, height, 0.0f, 1.0f);
            this.ScissorRect = new Rectangle(0, 0, width, height);

#if DEBUG
            {
                DebugInterface.Get().EnableDebugLayer();
            }
#endif

            this.Device = new Device(null, SharpDX.Direct3D.FeatureLevel.Level_11_0);

            using (var factory = new Factory4())
            {
                var commandQueueDesc = new CommandQueueDescription(CommandListType.Direct);
                this.CommandQueue = this.Device.CreateCommandQueue(commandQueueDesc);

                var swapChainDesc = new SwapChainDescription()
                {
                    BufferCount = FrameCount,
                    ModeDescription = new ModeDescription(width, height, new Rational(60, 1), Format.B8G8R8A8_UNorm),
                    Usage = Usage.RenderTargetOutput,
                    SwapEffect = SwapEffect.FlipDiscard,
                    OutputHandle = form.Handle,
                    Flags = SwapChainFlags.None,
                    SampleDescription = new SampleDescription(1, 0),
                    IsWindowed = true,
                };
                using (var tempSwapChain = new SwapChain(factory, this.CommandQueue, swapChainDesc))
                {
                    this.SwapChain = tempSwapChain.QueryInterface<SwapChain3>();
                    this.FrameIndex = this.SwapChain.CurrentBackBufferIndex;
                }

                factory.MakeWindowAssociation(form.Handle, WindowAssociationFlags.IgnoreAltEnter);
            }

            var rtvHeapDesc = new DescriptorHeapDescription()
            {
                DescriptorCount = FrameCount,
                Type = DescriptorHeapType.RenderTargetView,
                Flags = DescriptorHeapFlags.None,
            };
            this.RenderTargetViewHeap = this.Device.CreateDescriptorHeap(rtvHeapDesc);
            this.RtvDescriptorSize = this.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);

            // 各フレームごとに必要となるリソースを作成します。
            var rtvDescHandle = this.RenderTargetViewHeap.CPUDescriptorHandleForHeapStart;
            for (var i = 0; i < FrameCount; i++)
            {
                // レンダーターゲットビューを作成します。
                this.RenderTargets[i] = this.SwapChain.GetBackBuffer<Resource>(i);
                this.Device.CreateRenderTargetView(this.RenderTargets[i], null, rtvDescHandle);
                rtvDescHandle += this.RtvDescriptorSize;

                // コマンドバッファアロケータを作成します。
                this.CommandAllocators[i] = this.Device.CreateCommandAllocator(CommandListType.Direct);
            }
        }

        private void LoadAssets()
        {
            // 空のルートシグネチャを作成します。
            var rootSignatureDesc = new RootSignatureDescription(RootSignatureFlags.AllowInputAssemblerInputLayout);
            this.RootSignature = this.Device.CreateRootSignature(rootSignatureDesc.Serialize());

            // シェーダをロードします。デバッグビルドのときは、デバッグフラグを立てます。
#if DEBUG
            var vertexShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Shaders.hlsl", "VSMain", "vs_5_0", SharpDX.D3DCompiler.ShaderFlags.Debug));
            var pixelShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Shaders.hlsl", "PSMain", "ps_5_0", SharpDX.D3DCompiler.ShaderFlags.Debug));
#else
            var vertexShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Shaders.hlsl", "VSMain", "vs_5_0"));
            var pixelShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Shaders.hlsl", "PSMain", "vs_5_0"));
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
                RootSignature = this.RootSignature,
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

            this.PipelineState = this.Device.CreateGraphicsPipelineState(psoDesc);

            this.CommandList = this.Device.CreateCommandList(CommandListType.Direct, this.CommandAllocators[this.FrameIndex], this.PipelineState);
            this.CommandList.Close();


            // 頂点データを定義します。
            float aspectRatio = this.Viewport.Width / this.Viewport.Height;
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
            this.VertexBuffer = this.Device.CreateCommittedResource(
                new HeapProperties(HeapType.Upload), 
                HeapFlags.None, 
                ResourceDescription.Buffer(vertexBufferSize), 
                ResourceStates.GenericRead
                );

            // 頂点データを頂点バッファに書き込みます。
            var pVertexDataBegin = this.VertexBuffer.Map(0);
            {
                Utilities.Write(pVertexDataBegin, triangleVertices, 0, triangleVertices.Length);
            }
            this.VertexBuffer.Unmap(0);

            // 頂点バッファビューを作成します。
            this.VertexBufferView = new VertexBufferView()
            {
                BufferLocation = this.VertexBuffer.GPUVirtualAddress,
                StrideInBytes = Utilities.SizeOf<Vertex>(),
                SizeInBytes = vertexBufferSize,
            };

            this.Fence = this.Device.CreateFence(this.FenceValues[this.FrameIndex], FenceFlags.None);
            this.FenceValues[this.FrameIndex]++;

            this.FenceEvent = new AutoResetEvent(false);
        }

        internal void Update()
        {
        }

        internal void Render()
        {
            this.PopulateCommandList();

            this.CommandQueue.ExecuteCommandList(this.CommandList);

            this.SwapChain.Present(1, PresentFlags.None);

            this.WaitForPreviousFrame();
        }

        private void PopulateCommandList()
        {
            this.CommandAllocators[this.FrameIndex].Reset();

            this.CommandList.Reset(this.CommandAllocators[this.FrameIndex], this.PipelineState);

            // 必要な各種ステートを設定します。
            this.CommandList.SetGraphicsRootSignature(this.RootSignature);
            this.CommandList.SetViewport(this.Viewport);
            this.CommandList.SetScissorRectangles(this.ScissorRect);

            this.CommandList.ResourceBarrierTransition(this.RenderTargets[this.FrameIndex], ResourceStates.Present, ResourceStates.RenderTarget);

            var rtvDescHandle = this.RenderTargetViewHeap.CPUDescriptorHandleForHeapStart;
            rtvDescHandle += this.FrameIndex * this.RtvDescriptorSize;

            // レンダーターゲットを設定します。
            this.CommandList.SetRenderTargets(rtvDescHandle, null);

            // コマンドを積み込みます。
            this.CommandList.ClearRenderTargetView(rtvDescHandle, new Color4(0.0f, 0.2f, 0.4f, 1.0f), 0, null);

            this.CommandList.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleList;
            this.CommandList.SetVertexBuffer(0, this.VertexBufferView);
            this.CommandList.DrawInstanced(3, 1, 0, 0);

            this.CommandList.ResourceBarrierTransition(this.RenderTargets[this.FrameIndex], ResourceStates.RenderTarget, ResourceStates.Present);

            this.CommandList.Close();
        }

        private void WaitForGpu()
        {
            this.CommandQueue.Signal(this.Fence, this.FenceValues[this.FrameIndex]);

            this.Fence.SetEventOnCompletion(this.FenceValues[this.FrameIndex], this.FenceEvent.SafeWaitHandle.DangerousGetHandle());

            this.FenceValues[this.FrameIndex]++;
        }

        private void WaitForPreviousFrame()
        {
            // フェンスをシグナルするコマンドを積み込み
            var currentFenceValue = this.FenceValues[this.FrameIndex];
            this.CommandQueue.Signal(this.Fence, currentFenceValue);

            // フレームインデックスの更新
            this.FrameIndex = this.SwapChain.CurrentBackBufferIndex;

            // 次のフレームで使うリソースの準備がまだ整っていない場合は、これを待つ
            if (this.Fence.CompletedValue < this.FenceValues[this.FrameIndex])
            {
                this.Fence.SetEventOnCompletion(this.FenceValues[this.FrameIndex], this.FenceEvent.SafeWaitHandle.DangerousGetHandle());
                this.FenceEvent.WaitOne();
            }

            this.FenceValues[this.FrameIndex] = currentFenceValue + 1;
        }
    }
}