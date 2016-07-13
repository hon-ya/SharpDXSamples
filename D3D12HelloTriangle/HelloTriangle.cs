using System;
using System.Threading;
using SharpDX.DXGI;

namespace D3D12HelloTriangle
{
    using SharpDX;
    using SharpDX.Windows;
    using SharpDX.Direct3D12;

    internal class HelloTriangle : IDisposable
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
        public CommandAllocator CommandAllocator { get; private set; }
        public GraphicsCommandList CommandList { get; private set; }
        public int FenceValue { get; private set; }
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
            this.WaitForPreviousFrame();

            foreach (var target in this.RenderTargets)
            {
                target.Dispose();
            }

            this.CommandAllocator.Dispose();
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

            var rtvDescHandle = this.RenderTargetViewHeap.CPUDescriptorHandleForHeapStart;
            for (var i = 0; i < FrameCount; i++)
            {
                this.RenderTargets[i] = this.SwapChain.GetBackBuffer<Resource>(i);
                this.Device.CreateRenderTargetView(this.RenderTargets[i], null, rtvDescHandle);
                rtvDescHandle += this.RtvDescriptorSize;
            }

            this.CommandAllocator = this.Device.CreateCommandAllocator(CommandListType.Direct);
        }

        private void LoadAssets()
        {
            var rootSignatureDesc = new RootSignatureDescription(RootSignatureFlags.AllowInputAssemblerInputLayout);
            this.RootSignature = this.Device.CreateRootSignature(rootSignatureDesc.Serialize());

#if DEBUG
            var vertexShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Shaders.hlsl", "VSMain", "vs_5_0", SharpDX.D3DCompiler.ShaderFlags.Debug));
            var pixelShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Shaders.hlsl", "PSMain", "ps_5_0", SharpDX.D3DCompiler.ShaderFlags.Debug));
#else
            var vertexShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Shaders.hlsl", "VSMain", "vs_5_0"));
            var pixelShader = new ShaderBytecode(SharpDX.D3DCompiler.ShaderBytecode.CompileFromFile("Shaders.hlsl", "PSMain", "vs_5_0"));
#endif

            var inputElementDescs = new []
            {
                new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElement("COLOR", 0, Format.R32G32B32A32_Float, 12, 0),
            };

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

            this.CommandList = this.Device.CreateCommandList(CommandListType.Direct, this.CommandAllocator, this.PipelineState);
            this.CommandList.Close();

            var triangleVertices = new[]
            {
                new Vertex { Position = new Vector3(0.0f, 0.5f, 0.0f), Color = new Vector4(1.0f, 0.0f, 0.0f, 0.0f) },
                new Vertex { Position = new Vector3(0.5f, -0.5f, 0.0f), Color = new Vector4(0.0f, 1.0f, 0.0f, 0.0f) },
                new Vertex { Position = new Vector3(-0.5f, -0.5f, 0.0f), Color = new Vector4(0.0f, 0.0f, 1.0f, 0.0f) },
            };
            var vertexBufferSize = Utilities.SizeOf(triangleVertices);

            this.VertexBuffer = this.Device.CreateCommittedResource(
                new HeapProperties(HeapType.Upload), 
                HeapFlags.None, 
                ResourceDescription.Buffer(vertexBufferSize), 
                ResourceStates.GenericRead
                );

            var pVertexDataBegin = this.VertexBuffer.Map(0);
            {
                Utilities.Write(pVertexDataBegin, triangleVertices, 0, triangleVertices.Length);
            }
            this.VertexBuffer.Unmap(0);

            this.VertexBufferView = new VertexBufferView()
            {
                BufferLocation = this.VertexBuffer.GPUVirtualAddress,
                StrideInBytes = Utilities.SizeOf<Vertex>(),
                SizeInBytes = vertexBufferSize,
            };

            this.Fence = this.Device.CreateFence(0, FenceFlags.None);
            this.FenceValue = 1;

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
            this.CommandAllocator.Reset();

            this.CommandList.Reset(this.CommandAllocator, this.PipelineState);

            this.CommandList.ResourceBarrierTransition(this.RenderTargets[this.FrameIndex], ResourceStates.Present, ResourceStates.RenderTarget);

            this.CommandList.SetGraphicsRootSignature(this.RootSignature);
            this.CommandList.SetViewport(this.Viewport);
            this.CommandList.SetScissorRectangles(this.ScissorRect);

            var rtvDescHandle = this.RenderTargetViewHeap.CPUDescriptorHandleForHeapStart;
            rtvDescHandle += this.FrameIndex * this.RtvDescriptorSize;
            this.CommandList.SetRenderTargets(rtvDescHandle, null);

            this.CommandList.ClearRenderTargetView(rtvDescHandle, new Color4(0.0f, 0.2f, 0.4f, 1.0f), 0, null);

            this.CommandList.PrimitiveTopology = SharpDX.Direct3D.PrimitiveTopology.TriangleList;
            this.CommandList.SetVertexBuffer(0, this.VertexBufferView);
            this.CommandList.DrawInstanced(3, 1, 0, 0);

            this.CommandList.ResourceBarrierTransition(this.RenderTargets[this.FrameIndex], ResourceStates.RenderTarget, ResourceStates.Present);

            this.CommandList.Close();
        }

        private void WaitForPreviousFrame()
        {
            var fence = this.FenceValue;

            this.CommandQueue.Signal(this.Fence, fence);
            this.FenceValue++;

            if (this.Fence.CompletedValue < fence)
            {
                this.Fence.SetEventOnCompletion(fence, this.FenceEvent.SafeWaitHandle.DangerousGetHandle());
                this.FenceEvent.WaitOne();
            }

            this.FrameIndex = this.SwapChain.CurrentBackBufferIndex;
        }
    }
}