using System;
using System.Threading;
using SharpDX.DXGI;

namespace D3D12HelloWindow
{
    using SharpDX;
    using SharpDX.Windows;
    using SharpDX.Direct3D12;

    internal class HelloWindow : IDisposable
    {
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

        public void Dispose()
        {
            this.WaitForPreviousFrame();

            foreach(var target in this.RenderTargets)
            {
                target.Dispose();
            }

            this.CommandAllocator.Dispose();
            this.CommandQueue.Dispose();
            this.RenderTargetViewHeap.Dispose();
            this.CommandList.Dispose();
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
            for(var i = 0; i < FrameCount; i++)
            {
                this.RenderTargets[i] = this.SwapChain.GetBackBuffer<Resource>(i);
                this.Device.CreateRenderTargetView(this.RenderTargets[i], null, rtvDescHandle);
                rtvDescHandle += this.RtvDescriptorSize;
            }

            this.CommandAllocator = this.Device.CreateCommandAllocator(CommandListType.Direct);
        }

        private void LoadAssets()
        {
            this.CommandList = this.Device.CreateCommandList(CommandListType.Direct, this.CommandAllocator, null);
            this.CommandList.Close();

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

            this.CommandList.Reset(this.CommandAllocator, null);

            this.CommandList.ResourceBarrierTransition(this.RenderTargets[this.FrameIndex], ResourceStates.Present, ResourceStates.RenderTarget);

            var rtvDescHandle = this.RenderTargetViewHeap.CPUDescriptorHandleForHeapStart;
            rtvDescHandle += this.FrameIndex * this.RtvDescriptorSize;

            this.CommandList.ClearRenderTargetView(rtvDescHandle, new Color4(0.0f, 0.2f, 0.4f, 1.0f), 0, null);

            this.CommandList.ResourceBarrierTransition(this.RenderTargets[this.FrameIndex], ResourceStates.RenderTarget, ResourceStates.Present);

            this.CommandList.Close();
        }

        private void WaitForPreviousFrame()
        {
            var fence = this.FenceValue;

            this.CommandQueue.Signal(this.Fence, fence);
            this.FenceValue++;

            if(this.Fence.CompletedValue < fence)
            {
                this.Fence.SetEventOnCompletion(fence, this.FenceEvent.SafeWaitHandle.DangerousGetHandle());
                this.FenceEvent.WaitOne();
            }

            this.FrameIndex = this.SwapChain.CurrentBackBufferIndex;
        }
    }
}